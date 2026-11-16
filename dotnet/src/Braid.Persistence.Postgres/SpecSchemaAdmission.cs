using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Braid.Application;

namespace Braid.Persistence.Postgres;

internal static class SpecSchemaAdmission
{
    internal static string MigrationSql
    {
        get
        {
            var assembly = typeof(SpecSchemaAdmission).Assembly;
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".0044_spec_bundles.sql", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            return reader.ReadToEnd();
        }
    }
    private static readonly string Sql = MigrationSql;
    private static readonly string Checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql)));
    private static readonly IReadOnlyDictionary<string, string> Bodies = Regex.Matches(Sql + "\n" + DocumentEvidenceSchemaAdmission.MigrationSql,
        @"CREATE (?:OR REPLACE )?FUNCTION braid\.(spec_\w+)\(.*?AS \$body\$(.*?)\$body\$;", RegexOptions.Singleline | RegexOptions.CultureInvariant)
        .GroupBy(m => m.Groups[1].Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Groups[2].Value, StringComparer.Ordinal);
    private static readonly string[] Tables = ["spec_stores", "spec_bundles", "spec_sources", "spec_objects", "spec_temporaries", "spec_temporary_seals"];

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SpecDatabaseProfile profile, CancellationToken token)
    {
        var allowed = profile switch
        {
            SpecDatabaseProfile.Reader => new[] { "spec_schema_version", "spec_store_admit", "spec_bundle_authorize", "spec_object_gate", "spec_inventory", "spec_selection_discover_v1", "creation_proposals_discover_v1" },
            SpecDatabaseProfile.Maintenance => ["spec_schema_version", "spec_store_admit", "spec_object_gate", "spec_object_complete", "spec_retirements", "spec_temp_read", "spec_temp_complete"],
            _ => Bodies.Keys.Where(n => n != "spec_authorize").ToArray()
        };
        await using (var guard = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=$1 AND r.rolcanlogin AND NOT r.rolinherit
                AND NOT r.rolsuper AND NOT r.rolbypassrls AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND current_setting('transaction_isolation')='read committed' AND current_setting('row_security')='on'
                AND current_setting('session_replication_role')='origin' AND current_setting('server_encoding')='UTF8'
                AND current_setting('client_encoding')='UTF8' AND current_setting('standard_conforming_strings')='on'
                AND NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid OR roleid=r.oid)
                AND NOT has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
                AND has_schema_privilege(r.oid,'braid','USAGE') AND NOT has_schema_privilege('public','braid','USAGE,CREATE')
                AND NOT has_schema_privilege(r.oid,'braid','USAGE WITH GRANT OPTION')
                AND (SELECT nspowner='braid_migrator'::regrole FROM pg_namespace WHERE nspname='braid')
                AND NOT EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
                    AND (n.nspowner=r.oid OR has_schema_privilege(r.oid,n.oid,'CREATE')))
                AND NOT EXISTS(SELECT 1 FROM pg_class c WHERE c.relnamespace='braid'::regnamespace AND c.relkind IN ('r','p','v','m','f')
                    AND (has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')
                        OR has_table_privilege('public',c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR has_any_column_privilege('public',c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_class c WHERE c.relnamespace='braid'::regnamespace AND c.relkind='S'
                    AND (has_sequence_privilege(r.oid,c.oid,'USAGE,SELECT,UPDATE') OR has_sequence_privilege('public',c.oid,'USAGE,SELECT,UPDATE')))
                AND NOT EXISTS(SELECT 1 FROM pg_proc f WHERE f.pronamespace='braid'::regnamespace AND
                    (has_function_privilege('public',f.oid,'EXECUTE') OR has_function_privilege(r.oid,f.oid,'EXECUTE WITH GRANT OPTION')
                        OR (has_function_privilege(r.oid,f.oid,'EXECUTE') AND NOT f.proname=ANY($2))))
                AND (SELECT count(*) FROM pg_class c WHERE c.relnamespace='braid'::regnamespace AND c.relname=ANY($3)
                    AND c.relkind='r' AND c.relpersistence='p' AND NOT c.relispartition AND c.relowner='braid_migrator'::regrole
                    AND (c.relname='spec_stores' OR c.relrowsecurity))=6
                AND NOT EXISTS(SELECT 1 FROM pg_class c WHERE c.relnamespace='braid'::regnamespace AND c.relname=ANY($3)
                    AND (EXISTS(SELECT 1 FROM pg_trigger t WHERE t.tgrelid=c.oid AND NOT t.tgisinternal)
                        OR EXISTS(SELECT 1 FROM pg_rewrite w WHERE w.ev_class=c.oid)
                        OR EXISTS(SELECT 1 FROM pg_inherits i WHERE i.inhparent=c.oid OR i.inhrelid=c.oid)))
            FROM pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            guard.Parameters.AddWithValue(profile switch { SpecDatabaseProfile.Reader => "braid_spec_reader", SpecDatabaseProfile.Writer => "braid_spec_writer", _ => "braid_spec_maintenance" });
            guard.Parameters.AddWithValue(allowed.Append("document_evidence_schema_version").ToArray());
            guard.Parameters.AddWithValue(Tables);
            if (await guard.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw new SpecStorageException(SpecStorageError.Unavailable);
        }
        await ValidateTables(connection, transaction, token).ConfigureAwait(false);
        await DocumentEvidenceSchemaAdmission.ValidateAsync(connection, transaction, token).ConfigureAwait(false);
        await using (var functions = new NpgsqlCommand("""
            SELECT proname,prosrc,prosecdef,proowner='braid_migrator'::regrole,proconfig,provolatile::text,proisstrict,proparallel::text
            FROM pg_proc WHERE pronamespace='braid'::regnamespace AND proname=ANY($1)
            """, connection, transaction))
        {
            functions.Parameters.AddWithValue(Bodies.Keys.ToArray());
            await using var rows = await functions.ExecuteReaderAsync(token).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await rows.ReadAsync(token).ConfigureAwait(false))
            {
                var name = rows.GetString(0);
                if (!seen.Add(name) || rows.GetString(1) != Bodies[name] || !rows.GetBoolean(2) || !rows.GetBoolean(3)
                    || rows.IsDBNull(4) || !rows.GetFieldValue<string[]>(4).SequenceEqual(["search_path=pg_catalog, braid, pg_temp"])
                    || rows.GetString(5) != (name == "spec_schema_version" ? "s" : "v") || rows.GetBoolean(6) || rows.GetString(7) != "u")
                    throw new SpecStorageException(SpecStorageError.Unavailable);
            }
            if (seen.Count != Bodies.Count) throw new SpecStorageException(SpecStorageError.Unavailable);
        }
        await using var history = new NpgsqlCommand("SELECT braid.spec_schema_version()", connection, transaction);
        if (await history.ExecuteScalarAsync(token).ConfigureAwait(false) is not string checksum || checksum != Checksum)
            throw new SpecStorageException(SpecStorageError.Unavailable);
    }

    private static readonly IReadOnlyDictionary<string, string> Columns = new Dictionary<string, string>
    {
        ["spec_stores"] = "id:uuid!,kind:text!,location_digest:text!,active:boolean!",
        ["spec_bundles"] = "organization_id:uuid!,project_id:uuid!,id:uuid!,creator_id:uuid!,creation_key:uuid!,version:bigint!,tombstoned:boolean!,tombstone_key:uuid,tombstone_version:bigint",
        ["spec_sources"] = "organization_id:uuid!,project_id:uuid!,bundle_id:uuid!,id:uuid!,version:bigint!,format:integer!,digest:text!,byte_count:integer!,tombstoned:boolean!,tombstone_key:uuid,tombstone_version:bigint",
        ["spec_objects"] = "organization_id:uuid!,project_id:uuid!,bundle_id:uuid!,id:uuid!,source_id:uuid!,source_version:bigint!,kind:integer!,digest:text!,byte_count:integer!,store_id:uuid!,owner_id:uuid!,attempt_id:uuid!,state:text!,retirement_id:uuid,extractor_version:integer,profile_digest:text,extraction_outcome:integer,source_digest:text,reservation_version:bigint!,source_format:integer!",
        ["spec_temporaries"] = "organization_id:uuid!,project_id:uuid!,bundle_id:uuid!,object_id:uuid!,id:uuid!,attempt_id:uuid!,phase:integer!,evidence:bytea!,seal_digest:text!,previous_digest:text,retirement_id:uuid,retired:boolean!",
        ["spec_temporary_seals"] = "organization_id:uuid!,project_id:uuid!,bundle_id:uuid!,object_id:uuid!,temporary_id:uuid!,phase:integer!,evidence:bytea!,seal_digest:text!,previous_digest:text"
    };
    private static readonly IReadOnlyDictionary<string, string[]> UniqueKeys = new Dictionary<string, string[]>
    {
        ["spec_stores"] = ["id", "location_digest"],
        ["spec_bundles"] = ["organization_id,project_id,id", "organization_id,project_id,creator_id,creation_key"],
        ["spec_sources"] = ["organization_id,project_id,bundle_id,id,version", "organization_id,project_id,bundle_id,id,version,format"],
        ["spec_objects"] = ["organization_id,project_id,bundle_id,id", "store_id,attempt_id", "store_id,retirement_id", "organization_id,project_id,bundle_id,source_id,source_version|(kind = 1)"],
        ["spec_temporaries"] = ["organization_id,project_id,bundle_id,object_id,id"],
        ["spec_temporary_seals"] = ["organization_id,project_id,bundle_id,object_id,temporary_id,phase"]
    };
    private static async Task ValidateTables(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT c.relname,string_agg(a.attname||':'||format_type(a.atttypid,a.atttypmod)||CASE WHEN a.attnotnull THEN '!' ELSE '' END,',' ORDER BY a.attnum)
            FROM pg_class c JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            WHERE c.relnamespace='braid'::regnamespace AND c.relname=ANY($1) GROUP BY c.relname
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(Tables);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var count = 0;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (Columns[reader.GetString(0)] != reader.GetString(1)) throw new SpecStorageException(SpecStorageError.Unavailable);
                count++;
            }
            if (count != Tables.Length) throw new SpecStorageException(SpecStorageError.Unavailable);
        }
        await using var indexes = new NpgsqlCommand("""
            SELECT c.relname,string_agg(a.attname,',' ORDER BY k.ordinality),pg_get_expr(i.indpred,i.indrelid),i.indisvalid AND i.indisready AND i.indimmediate
            FROM pg_class c JOIN pg_index i ON i.indrelid=c.oid AND i.indisunique
            CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(number,ordinality)
            LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=k.number
            WHERE c.relnamespace='braid'::regnamespace AND c.relname=ANY($1)
            GROUP BY c.relname,i.indexrelid,pg_get_expr(i.indpred,i.indrelid),i.indisvalid,i.indisready,i.indimmediate
            """, connection, transaction);
        indexes.Parameters.AddWithValue(Tables);
        await using var rows = await indexes.ExecuteReaderAsync(token).ConfigureAwait(false);
        var actual = Tables.ToDictionary(n => n, _ => new List<string>());
        while (await rows.ReadAsync(token).ConfigureAwait(false))
        {
            if (!rows.GetBoolean(3) || rows.IsDBNull(1)) throw new SpecStorageException(SpecStorageError.Unavailable);
            actual[rows.GetString(0)].Add(rows.GetString(1) + (rows.IsDBNull(2) ? "" : "|" + rows.GetString(2)));
        }
        if (Tables.Any(n => !actual[n].Order(StringComparer.Ordinal).SequenceEqual(UniqueKeys[n].Order(StringComparer.Ordinal))))
            throw new SpecStorageException(SpecStorageError.Unavailable);
    }
}
