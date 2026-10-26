using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Weft.Persistence.Postgres;

internal static partial class RegressionRunRoleGuard
{
    internal const string Fingerprint = "92679f5a0bc28eb38e758022eec378a29ecde9856e3c7678ed9b6c9718dabae4";
    private static readonly Lazy<Dictionary<string, Match>> Functions = new(Load);
    private static readonly string[] WriterFunctions = ["submit_regression_run", "claim_regression_run", "observe_regression_run",
        "finish_regression_run", "release_regression_run", "cancel_regression_run", "read_regression_run", "list_ready_regression_runs", "list_regression_run_history"];

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool writer, CancellationToken token)
    {
        var role = writer ? "weft_run_writer" : "weft_run_reader";
        await using (var check = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=@role AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL
                AND current_setting('transaction_isolation')='read committed' AND current_setting('session_replication_role')='origin'
                AND current_setting('row_security')='on' AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on'
                AND coalesce(nullif(current_setting('weft.organization_id',true),''),'')=''
                AND coalesce(nullif(current_setting('weft.run_principal_id',true),''),'')=''
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
            check.Parameters.AddWithValue("role", role);
            check.Parameters.AddWithValue("functions", writer ? WriterFunctions.Concat(AutomationSchemaAdmission.WriteFunctions).ToArray() :
                new[] { "read_regression_run", "list_regression_run_history" }.Concat(AutomationSchemaAdmission.ReadFunctions).ToArray());
            if (await check.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await using (var check = new NpgsqlCommand("""
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
            check.Parameters.AddWithValue("names", Functions.Value.Keys.ToArray());
            var seen = new HashSet<string>();
            await using var rows = await check.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
            {
                var name = rows.GetString(0);
                var definition = Functions.Value[name];
                var arguments = definition.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(item => item.Split(' ', 2)).ToArray();
                if (!seen.Add(name) || rows.GetString(1) != definition.Groups[7].Value ||
                    rows.GetBoolean(2) != (definition.Groups[6].Value == "DEFINER") ||
                    rows.GetChar(3) != (definition.Groups[5].Value == "STABLE" ? 's' : 'v') || rows.GetString(4) != definition.Groups[4].Value ||
                    rows.GetString(5) != string.Join(", ", arguments.Select(arg => arg[1])) ||
                    !(rows.IsDBNull(6) ? Array.Empty<string>() : rows.GetFieldValue<string[]>(6)).SequenceEqual(arguments.Select(arg => arg[0])) ||
                    !rows.GetBoolean(7) || rows.GetString(8) != ResultTypes[name]) throw Invalid();
            }
            if (seen.Count != Functions.Value.Count) throw Invalid();
        }
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, ["regression_runs"], token) != Fingerprint) throw Invalid();
    }

    private static readonly Dictionary<string, string> ResultTypes = new()
    {
        ["regression_run_authorized"] = "boolean",
        ["guard_regression_run"] = "trigger",
        ["submit_regression_run"] = "integer",
        ["claim_regression_run"] = "TABLE(outcome integer, attempt_number integer, lease_token uuid, expires_at timestamp with time zone, project uuid, mapping uuid, case_key uuid, case_version bigint, creator uuid, revision uuid, native_sha text, content_sha text, content_bytes integer, actor uuid)",
        ["observe_regression_run"] = "boolean",
        ["finish_regression_run"] = "boolean",
        ["release_regression_run"] = "boolean",
        ["cancel_regression_run"] = "boolean",
        ["read_regression_run"] = "SETOF weft.regression_runs",
        ["list_ready_regression_runs"] = "TABLE(organization_id uuid, run_id uuid)",
        ["list_regression_run_history"] = "TABLE(run_id uuid, case_id uuid, version bigint, revision_id uuid, state text, attempt integer, created_at timestamp with time zone)"
    };

    private static Dictionary<string, Match> Load()
    {
        var text = string.Concat(new[] { "0015_regression_runs", "0016_regression_dispatch", "0017_regression_run_history" }.Select(name =>
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Weft.Persistence.Postgres.Migrations.{name}.sql") ?? throw Invalid();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
        var functions = FunctionPattern().Matches(text).ToDictionary(match => match.Groups[1].Value, StringComparer.Ordinal);
        if (functions.Count != ResultTypes.Count) throw Invalid();
        return functions;
    }
    [GeneratedRegex(@"CREATE FUNCTION weft\.([a-z_]+)\((.*?)\)\s+RETURNS\s+(.*?)\s+LANGUAGE (\w+) (STABLE|VOLATILE) SECURITY (DEFINER|INVOKER) SET search_path=pg_catalog,weft,pg_temp\s+AS \$body\$(.*?)\$body\$;", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FunctionPattern();
    private static InvalidOperationException Invalid() => new("Regression run database contract is unavailable.");
}
