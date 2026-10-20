using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Weft.Persistence.Postgres;

internal static partial class BuildRoleGuard
{
    internal const string Fingerprint = "2eb35d245dc352e8b32ff24a8893f36c3aaca9cf6bd24252eaedc50007362c17";
    internal static readonly string[] Tables = ["build_environments", "build_approvals", "build_publications"];
    internal static readonly string[] Names = ["build_json_text", "build_intent_canonical", "build_authorized", "build_evidence_passes", "build_approval_active", "guard_build_record", "read_build_record", "write_build_record"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(Load);

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool writer, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=@role AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL
                AND current_setting('transaction_isolation')='read committed' AND current_setting('session_replication_role')='origin'
                AND current_setting('row_security')='on' AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on'
                AND coalesce(nullif(current_setting('weft.organization_id',true),''),'')=''
                AND coalesce(nullif(current_setting('weft.build_principal_id',true),''),'')=''
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
                AND NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')
                    AND NOT (n.nspname='weft' AND p.proname=ANY(@functions)))
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("role", writer ? "weft_build_writer" : "weft_build_reader");
            command.Parameters.AddWithValue("functions", writer ? new[] { "read_build_record", "write_build_record" } : ["read_build_record"]);
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await using (var command = new NpgsqlCommand("""
            SELECT p.proname,p.prosrc,p.prosecdef,p.provolatile,l.lanname,pg_catalog.oidvectortypes(p.proargtypes),p.proargnames[1:p.pronargs],
                p.proowner=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname='weft_migrator')
                AND NOT p.proleakproof AND NOT p.proisstrict AND p.prokind='f' AND p.proparallel='u' AND p.pronargdefaults=0
                AND p.proconfig=ARRAY['search_path=pg_catalog, weft, pg_temp']::text[]
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a WHERE a.grantee=0),
                pg_catalog.pg_get_function_result(p.oid)
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_catalog.pg_language l ON l.oid=p.prolang WHERE n.nspname='weft' AND p.proname=ANY(@names)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("names", Names);
            var seen = new HashSet<string>();
            await using var rows = await command.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
            {
                var name = rows.GetString(0);
                var definition = Definitions.Value[name];
                var args = definition.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(v => v.Split(' ', 2)).ToArray();
                if (!seen.Add(name) || rows.GetString(1) != definition.Groups[7].Value || rows.GetBoolean(2) != (definition.Groups[6].Value == "DEFINER") ||
                    rows.GetChar(3) != (definition.Groups[5].Value == "STABLE" ? 's' : 'v') || rows.GetString(4) != definition.Groups[4].Value ||
                    rows.GetString(5) != string.Join(", ", args.Select(a => a[1])) ||
                    !(rows.IsDBNull(6) ? Array.Empty<string>() : rows.GetFieldValue<string[]>(6)).SequenceEqual(args.Select(a => a[0])) ||
                    !rows.GetBoolean(7) || rows.GetString(8) != definition.Groups[3].Value.Trim()) throw Invalid();
            }
            if (seen.Count != Names.Length) throw Invalid();
        }
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token) != Fingerprint) throw Invalid();
    }

    private static Dictionary<string, Match> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Weft.Persistence.Postgres.Migrations.0018_build_publication.sql") ?? throw Invalid();
        using var reader = new StreamReader(stream);
        var result = Function().Matches(reader.ReadToEnd()).ToDictionary(match => match.Groups[1].Value, StringComparer.Ordinal);
        if (result.Count != Names.Length) throw Invalid();
        return result;
    }
    [GeneratedRegex(@"CREATE FUNCTION weft\.([a-z_]+)\((.*?)\)\s+RETURNS\s+(.*?)\s+LANGUAGE (\w+) (STABLE|VOLATILE) SECURITY (DEFINER|INVOKER) SET search_path=pg_catalog,weft,pg_temp\s+AS \$body\$(.*?)\$body\$;", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Function();
    private static InvalidOperationException Invalid() => new("Build database contract unavailable.");
}
