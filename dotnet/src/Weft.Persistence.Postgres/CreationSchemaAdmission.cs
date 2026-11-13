using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class CreationSchemaAdmission
{
    internal static string MigrationChecksum => MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 45).Checksum;
    internal static string IngestionGuardBody => System.Text.RegularExpressions.Regex.Match(
        MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 45).Sql,
        @"CREATE FUNCTION weft\.creation_ingestion_guard\(\).*?AS \$body\$(.*?)\$body\$;", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
    // Catalog shape captured from the actual complete migration, independent of its journal claim.
    internal const string ShapeDigest = "77c8a1b6c7e14bad1f7efe2ee636b2248e0ba6c37d1f4fc8d50af7010dc35371";
    internal static readonly string[] Tables = ["creation_proposals", "creation_sources", "creation_approvals"];
    internal static readonly string[] Functions = System.Text.RegularExpressions.Regex.Matches(
        MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 45).Sql + DocumentEvidenceSchemaAdmission.MigrationSql, @"CREATE (?:OR REPLACE )?FUNCTION weft\.((?:creation_|document_evidence_)\w+)\(")
        .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
    internal static readonly string[] Roles = ["weft_creation_reader", "weft_creation_writer", "weft_creation_approver"];

    /// <summary>Whether any object 0045 or 0046 creates under its own name exists. None means an older schema;
    /// any one is a (possibly partial) installation that must match the complete admitted shape.</summary>
    internal static async Task<bool> InstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_class c WHERE c.relnamespace='weft'::regnamespace AND c.relname=ANY($1::text[]))
                OR EXISTS(SELECT 1 FROM pg_catalog.pg_proc p WHERE p.pronamespace='weft'::regnamespace AND p.proname=ANY($2::text[]))
                OR EXISTS(SELECT 1 FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
                    WHERE c.relnamespace='weft'::regnamespace AND t.tgname='revision_ingestions_creation_guard')
            """, connection, transaction);
        command.Parameters.AddWithValue(Tables);
        command.Parameters.AddWithValue(Functions);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
    }

    internal static async Task<string> ShapeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            WITH entries AS (
                SELECT 'table/'||c.relname AS key, jsonb_build_array(c.relkind,c.relpersistence,c.relrowsecurity,c.relforcerowsecurity,c.relispartition,
                    pg_get_userbyid(c.relowner),c.relacl::text)::text AS value
                FROM pg_class c WHERE c.relnamespace='weft'::regnamespace AND c.relname=ANY($1)
                UNION ALL
                SELECT 'column/'||c.relname||'/'||a.attnum, jsonb_build_array(a.attname,format_type(a.atttypid,a.atttypmod),a.attnotnull,a.attidentity,a.attgenerated,
                    pg_get_expr(d.adbin,d.adrelid),a.attacl::text,co.collname)::text
                FROM pg_class c JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
                LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum LEFT JOIN pg_collation co ON co.oid=a.attcollation
                WHERE c.relnamespace='weft'::regnamespace AND c.relname=ANY($1)
                UNION ALL
                SELECT 'constraint/'||c.relname||'/'||k.conname,jsonb_build_array(pg_get_constraintdef(k.oid),k.convalidated,k.condeferrable,k.condeferred)::text
                FROM pg_class c JOIN pg_constraint k ON k.conrelid=c.oid WHERE c.relnamespace='weft'::regnamespace AND c.relname=ANY($1)
                UNION ALL
                SELECT 'index/'||c.relname||'/'||i.indexrelid::regclass::text,jsonb_build_array(pg_get_indexdef(i.indexrelid),i.indisvalid,i.indisready,i.indimmediate)::text
                FROM pg_class c JOIN pg_index i ON i.indrelid=c.oid WHERE c.relnamespace='weft'::regnamespace AND c.relname=ANY($1)
                UNION ALL
                SELECT 'policy/'||c.relname||'/'||p.polname,jsonb_build_array(p.polcmd,p.polpermissive,
                    ARRAY(SELECT CASE WHEN role=0 THEN 'PUBLIC' ELSE pg_get_userbyid(role) END FROM unnest(p.polroles) role ORDER BY role),
                    pg_get_expr(p.polqual,p.polrelid),pg_get_expr(p.polwithcheck,p.polrelid))::text
                FROM pg_class c JOIN pg_policy p ON p.polrelid=c.oid WHERE c.relnamespace='weft'::regnamespace AND c.relname=ANY($1)
                UNION ALL
                SELECT 'trigger/'||c.relname||'/'||t.tgname,jsonb_build_array(pg_get_triggerdef(t.oid),t.tgenabled)::text
                FROM pg_class c JOIN pg_trigger t ON t.tgrelid=c.oid AND NOT t.tgisinternal WHERE c.relnamespace='weft'::regnamespace
                    AND (c.relname=ANY($1) OR t.tgname='revision_ingestions_creation_guard')
                UNION ALL
                SELECT 'function/'||p.oid::regprocedure::text,jsonb_build_array(pg_get_function_result(p.oid),p.prosrc,p.prosecdef,pg_get_userbyid(p.proowner),p.proconfig,p.provolatile,p.proisstrict,p.proparallel,p.proacl::text,
                    p.proargnames,p.prokind,p.proleakproof,p.proargmodes,pg_get_expr(p.proargdefaults,0),p.prosupport::regproc::text)::text
                FROM pg_proc p WHERE p.pronamespace='weft'::regnamespace AND (p.proname LIKE 'creation\_%' ESCAPE '\' OR p.proname LIKE 'spec\_%' ESCAPE '\' OR p.proname='document_evidence_schema_version'
                    OR EXISTS(SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid WHERE t.tgfoid=p.oid AND c.relnamespace='weft'::regnamespace AND c.relname=ANY($1)))
                UNION ALL
                SELECT 'inherit/'||c.relname,jsonb_build_array(i.inhparent::regclass::text,i.inhseqno)::text FROM pg_class c JOIN pg_inherits i ON i.inhrelid=c.oid
                WHERE c.relnamespace='weft'::regnamespace AND c.relname=ANY($1)
            ) SELECT string_agg(key||'='||value,E'\n' ORDER BY key COLLATE "C") FROM entries
            """, connection, transaction);
        command.Parameters.AddWithValue(Tables.Concat(new[] { "principals", "organization_memberships", "project_memberships", "project_payload_read_grants", "projects", "spec_stores", "spec_bundles", "spec_sources", "spec_objects" }).ToArray());
        var text = (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? "";
        // Internal RI trigger names contain catalog OIDs, so validate their enforcement
        // separately instead of including unstable names in the catalog fingerprint.
        await using var foreignKeys = new NpgsqlCommand("""
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid
                WHERE c.relnamespace='weft'::regnamespace AND c.relname=ANY($1) AND k.contype='f'
                    AND ((SELECT count(*) FROM pg_trigger t WHERE t.tgconstraint=k.oid
                        AND t.tgisinternal AND t.tgenabled='O'
                        AND t.tgrelid IN (k.conrelid,k.confrelid)) <> 4
                    OR EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgconstraint=k.oid
                        AND (NOT t.tgisinternal OR t.tgenabled<>'O'))))
            """, connection, transaction);
        foreignKeys.Parameters.AddWithValue(command.Parameters[0].Value!);
        if (await foreignKeys.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
            throw new InvalidOperationException("Creation dependency foreign-key enforcement failed.");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CreationDatabaseProfile profile, CancellationToken ct)
    {
        var allowed = profile switch
        {
            CreationDatabaseProfile.Reader => new[] { "creation_read", "creation_schema_version", "document_evidence_schema_version" },
            CreationDatabaseProfile.Writer => ["creation_read", "creation_schema_version", "creation_select", "creation_retain", "document_evidence_schema_version"],
            _ => ["creation_read", "creation_schema_version", "creation_approve", "creation_fence", "document_evidence_schema_version"]
        };
        await using var guard = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=$1 AND r.rolcanlogin AND NOT r.rolinherit AND NOT r.rolsuper
                AND NOT r.rolbypassrls AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND current_setting('transaction_isolation')='read committed' AND current_setting('row_security')='on'
                AND current_setting('session_replication_role')='origin' AND current_setting('server_encoding')='UTF8'
                AND current_setting('client_encoding')='UTF8' AND current_setting('standard_conforming_strings')='on'
                AND NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid OR roleid=r.oid)
                AND NOT has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
                AND has_schema_privilege(r.oid,'weft','USAGE') AND NOT has_schema_privilege(r.oid,'weft','USAGE WITH GRANT OPTION')
                AND NOT has_schema_privilege('public','weft','USAGE,CREATE')
                AND NOT EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
                    AND (n.nspowner=r.oid OR has_schema_privilege(r.oid,n.oid,'CREATE')))
                AND NOT EXISTS(SELECT 1 FROM pg_class c WHERE c.relnamespace='weft'::regnamespace AND c.relkind IN ('r','p','v','m','f')
                    AND (has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')
                        OR has_table_privilege('public',c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR has_any_column_privilege('public',c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_class c WHERE c.relnamespace='weft'::regnamespace AND c.relkind='S'
                    AND (has_sequence_privilege(r.oid,c.oid,'USAGE,SELECT,UPDATE') OR has_sequence_privilege('public',c.oid,'USAGE,SELECT,UPDATE')))
                AND NOT EXISTS(SELECT 1 FROM pg_proc f WHERE f.pronamespace='weft'::regnamespace AND
                    (has_function_privilege('public',f.oid,'EXECUTE') OR has_function_privilege(r.oid,f.oid,'EXECUTE WITH GRANT OPTION')
                        OR (has_function_privilege(r.oid,f.oid,'EXECUTE') AND NOT f.proname=ANY($2))))
                AND NOT EXISTS(SELECT 1 FROM pg_class c WHERE c.relnamespace='weft'::regnamespace AND c.relname=ANY($3)
                    AND (EXISTS(SELECT 1 FROM pg_rewrite w WHERE w.ev_class=c.oid) OR EXISTS(SELECT 1 FROM pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)))
            FROM pg_roles r WHERE r.rolname=current_user
            """, connection, transaction);
        guard.Parameters.AddWithValue(Roles[(int)profile]); guard.Parameters.AddWithValue(allowed); guard.Parameters.AddWithValue(Tables);
        if (await guard.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true || await ShapeAsync(connection, transaction, ct).ConfigureAwait(false) != ShapeDigest)
            throw new InvalidOperationException("Creation schema or role admission failed.");
        await using var history = new NpgsqlCommand("SELECT weft.creation_schema_version()", connection, transaction);
        if (await history.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string checksum || checksum != MigrationChecksum)
            throw new InvalidOperationException("Creation migration history mismatch.");
        await DocumentEvidenceSchemaAdmission.ValidateAsync(connection, transaction, ct).ConfigureAwait(false);
    }
}
