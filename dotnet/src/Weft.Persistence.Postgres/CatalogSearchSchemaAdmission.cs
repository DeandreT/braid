using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class CatalogSearchSchemaAdmission
{
    internal const string MigrationChecksum = "d2b3a94b7c4f8831f629787cd2fd1ccb88416d5021893da37ad5a2935875916d";
    internal static readonly string[] ReadObjects = ["catalog_search_report_index", "catalog_search_reports"];
    internal static readonly string[] IndexNames = ["projects_search_trgm", "mappings_search_trgm", "mapping_revisions_search_trgm", "revision_ingestions_filename_search_trgm", "catalog_search_report_index_trgm"];
    private static readonly string Migration = LoadMigration();
    private static readonly string IndexBody = Migration.Split("$weft_index_catalog_report$", StringSplitOptions.None)[1];
    private static readonly string ViewSource = Migration.Split("CREATE VIEW weft.catalog_search_reports WITH (security_barrier = true) AS", StringSplitOptions.None)[1].Split(';')[0];

    internal static async Task<bool> ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT CASE WHEN to_regclass('weft.catalog_search_report_index') IS NULL THEN true ELSE
                (SELECT r.relkind = 'r' AND r.relpersistence = 'p' AND r.relrowsecurity AND NOT r.relforcerowsecurity
                    AND NOT r.relispartition AND o.rolname = 'weft_migrator'
                    AND NOT EXISTS (SELECT 1 FROM pg_inherits WHERE inhrelid = r.oid OR inhparent = r.oid)
                    AND NOT EXISTS (SELECT 1 FROM pg_rewrite WHERE ev_class = r.oid)
                    AND NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = r.oid AND NOT tgisinternal)
                    AND NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = r.oid AND tgisinternal AND tgenabled <> 'O')
                  FROM pg_class r JOIN pg_roles o ON o.oid = r.relowner WHERE r.oid = to_regclass('weft.catalog_search_report_index'))
                AND EXISTS (SELECT 1 FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
                    JOIN pg_roles o ON o.oid = n.nspowner WHERE e.extname = 'pg_trgm'
                    AND n.nspname = 'weft_search_extensions' AND o.rolname = 'weft_migrator')
                AND NOT has_schema_privilege('public','weft_search_extensions','USAGE,CREATE')
                AND NOT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                    WHERE n.nspname = 'weft_search_extensions' AND has_function_privilege('public',p.oid,'EXECUTE'))
                AND (SELECT r.relkind = 'v' AND o.rolname = 'weft_migrator' AND r.reloptions = ARRAY['security_barrier=true']
                 FROM pg_class r JOIN pg_roles o ON o.oid = r.relowner WHERE r.oid = to_regclass('weft.catalog_search_reports'))
                AND (SELECT count(*) = 1 AND bool_and(o.rolname = 'weft_migrator' AND l.lanname = 'plpgsql'
                    AND p.prosecdef AND p.proconfig = ARRAY['search_path=pg_catalog, weft, pg_temp']
                    AND p.prosrc = $1 AND p.prorettype = 'boolean'::regtype AND NOT p.proretset
                    AND p.prokind = 'f' AND p.provolatile = 'v' AND p.proparallel = 'u'
                    AND NOT p.proisstrict AND NOT p.proleakproof AND p.pronargdefaults = 0
                    AND p.provariadic = 0 AND p.proallargtypes IS NULL AND p.proargmodes IS NULL AND p.prosupport = 0
                    AND p.proargnames = ARRAY['actor','organization','project','mapping','revision','derivation','digest','entries']
                    AND p.proargdefaults IS NULL AND p.probin IS NULL AND p.prosqlbody IS NULL AND p.procost = 100 AND p.prorows = 0
                    AND (SELECT count(*) = 2 AND bool_and(a.grantor = p.proowner AND NOT a.is_grantable
                        AND a.privilege_type = 'EXECUTE' AND (a.grantee = p.proowner
                            OR a.grantee = (SELECT oid FROM pg_roles WHERE rolname = 'weft_deriver')))
                        FROM aclexplode(COALESCE(p.proacl,acldefault('f',p.proowner))) a)
                    AND p.oid = to_regprocedure('weft.index_catalog_report(uuid,uuid,uuid,uuid,uuid,uuid,text,jsonb)'))
                 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                 JOIN pg_roles o ON o.oid = p.proowner JOIN pg_language l ON l.oid = p.prolang
                 WHERE n.nspname = 'weft' AND p.proname = 'index_catalog_report')
                END,
                pg_get_viewdef(to_regclass('weft.catalog_search_reports'), false)
            """, connection, transaction);
        command.Parameters.AddWithValue(IndexBody);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0) || !reader.GetBoolean(0) ||
            !reader.IsDBNull(1) && Canonical(reader.GetString(1)) != Canonical(ViewSource))
            throw new InvalidOperationException("Catalog search requires the accepted restricted index schema.");
        if (reader.IsDBNull(1)) return false;
        await reader.DisposeAsync();
        await ValidateIndexTableAsync(connection, transaction, token);
        await using var indexes = new NpgsqlCommand("""
            SELECT count(*) = 5 AND bool_and(r.relkind = 'i' AND r.relpersistence = 'p' AND NOT r.relispartition
                AND o.rolname = 'weft_migrator' AND a.amname = 'gin' AND r.reloptions IS NULL
                AND i.indisvalid AND i.indisready AND i.indislive AND NOT i.indisunique AND NOT i.indisexclusion
                AND i.indnkeyatts = 1 AND i.indnatts = 1 AND i.indkey[0] = 0 AND i.indexprs IS NOT NULL AND i.indpred IS NULL
                AND i.indcollation[0] = '"C"'::regcollation AND i.indoption[0] = 0
                AND c.opcname = 'gin_trgm_ops' AND cn.nspname = 'weft_search_extensions'
                AND t.relname = expected.table_name)
            FROM unnest($1::text[], $2::text[]) expected(index_name,table_name)
            JOIN pg_class r ON r.relname = expected.index_name
            JOIN pg_namespace n ON n.oid = r.relnamespace AND n.nspname = 'weft'
            JOIN pg_roles o ON o.oid = r.relowner JOIN pg_am a ON a.oid = r.relam
            JOIN pg_index i ON i.indexrelid = r.oid JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_opclass c ON c.oid = i.indclass[0] JOIN pg_namespace cn ON cn.oid = c.opcnamespace
            """, connection, transaction);
        indexes.Parameters.AddWithValue(IndexNames);
        indexes.Parameters.AddWithValue(new[] { "projects", "mappings", "mapping_revisions", "revision_ingestions", "catalog_search_report_index" });
        if (await indexes.ExecuteScalarAsync(token) is not true)
            throw new InvalidOperationException("Catalog search requires its complete ready trigram indexes.");
        await using var expressions = new NpgsqlCommand("""
            SELECT r.relname, pg_get_expr(i.indexprs, i.indrelid, false)
            FROM pg_index i JOIN pg_class r ON r.oid = i.indexrelid
            JOIN pg_namespace n ON n.oid = r.relnamespace
            WHERE n.nspname = 'weft' AND r.relname = ANY($1::text[])
            """, connection, transaction);
        expressions.Parameters.AddWithValue(IndexNames);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IndexNames[0]] = "lower((name || E'\\n' || description) COLLATE \"C\")",
            [IndexNames[1]] = "lower((name || E'\\n' || description || E'\\n' || backend_id) COLLATE \"C\")",
            [IndexNames[2]] = "lower(native_digest COLLATE \"C\")",
            [IndexNames[3]] = "lower(display_filename COLLATE \"C\")",
            [IndexNames[4]] = "lower(search_text COLLATE \"C\")"
        };
        await using var rows = await expressions.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
            if (!expected.Remove(rows.GetString(0), out var expression) || Canonical(rows.GetString(1)) != Canonical(expression))
                throw new InvalidOperationException("Catalog search index expressions must match their literal query semantics.");
        if (expected.Count != 0) throw new InvalidOperationException("Catalog search indexes are incomplete.");
        return true;
    }

    private static async Task ValidateIndexTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken token)
    {
        await using var columns = new NpgsqlCommand("""
            SELECT array_agg(a.attname::text || ':' || format_type(a.atttypid,a.atttypmod) ORDER BY a.attnum),
                bool_and(a.attnotnull AND a.attidentity = '' AND a.attgenerated = '' AND d.oid IS NULL)
            FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attrelid = 'weft.catalog_search_report_index'::regclass AND a.attnum > 0 AND NOT a.attisdropped
            """, connection, transaction);
        await using (var rows = await columns.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token) || !rows.GetBoolean(1) || !rows.GetFieldValue<string[]>(0).SequenceEqual(new[]
                { "organization_id:uuid", "project_id:uuid", "mapping_id:uuid", "revision_id:uuid", "derivation_id:uuid", "derived_digest:text", "terms:jsonb", "search_text:text" }))
                throw new InvalidOperationException("Catalog search index columns must retain their exact bounded representation.");
        }
        await using var constraints = new NpgsqlCommand("""
            SELECT contype::text, pg_get_constraintdef(oid,false),
                convalidated AND NOT condeferrable AND NOT condeferred AND (contype <> 'c' OR NOT connoinherit)
            FROM pg_constraint WHERE conrelid = 'weft.catalog_search_report_index'::regclass
            """, connection, transaction);
        var expected = new HashSet<string>(new[]
        {
            "PRIMARY KEY (organization_id, project_id, mapping_id, revision_id, derivation_id)",
            "FOREIGN KEY (organization_id, project_id, mapping_id, revision_id, derivation_id) REFERENCES weft.revision_derivation_attempts(organization_id, project_id, mapping_id, revision_id, derivation_id)",
            "CHECK (derived_digest ~ '^[0-9a-f]{64}$')",
            "CHECK (jsonb_typeof(terms) = 'array' AND jsonb_array_length(terms) <= 8192)"
        }.Select(Canonical), StringComparer.Ordinal);
        await using (var rows = await constraints.ExecuteReaderAsync(token))
        {
            while (await rows.ReadAsync(token))
                if (!rows.GetBoolean(2) || !expected.Remove(Canonical(rows.GetString(1))))
                    throw new InvalidOperationException("Catalog search index constraints must retain exact report ancestry and bounds.");
        }
        if (expected.Count != 0) throw new InvalidOperationException("Catalog search index constraints are incomplete.");
        await using var policy = new NpgsqlCommand("""
            SELECT count(*) = 1 AND bool_and(polname = 'catalog_search_report_index_organization_scope'
                AND polcmd = '*' AND polpermissive AND polroles = ARRAY[0::oid]
                AND pg_get_expr(polqual,polrelid,false) = $1 AND pg_get_expr(polwithcheck,polrelid,false) = $1)
            FROM pg_policy WHERE polrelid = 'weft.catalog_search_report_index'::regclass
            """, connection, transaction);
        policy.Parameters.AddWithValue("(organization_id = (NULLIF(current_setting('weft.organization_id'::text, true), ''::text))::uuid)");
        if (await policy.ExecuteScalarAsync(token) is not true)
            throw new InvalidOperationException("Catalog search index requires its exact organization policy.");
    }

    // pg_get_viewdef adds grouping parentheses and text casts. Preserve every literal and
    // non-grouping token: a changed admission predicate must not be adopted as equivalent.
    private static string Canonical(string sql) => string.Join('|', Regex.Matches(sql.Replace("E'\\n'", "'\n'", StringComparison.Ordinal),
        "'(?:''|[^'])*'|\"(?:\"\"|[^\"])*\"|::[a-z_]+|[a-z_][a-z_0-9]*|[^\\s()]", RegexOptions.CultureInvariant)
        .Select(match => match.Value).Where(value => value is not ("::text" or ";")));

    private static string LoadMigration()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Weft.Persistence.Postgres.Migrations.0029_catalog_search.sql") ??
            throw new InvalidOperationException("The catalog search migration is missing.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))) != MigrationChecksum)
            throw new InvalidOperationException("Catalog search requires its accepted source migration.");
        return sql;
    }
}
