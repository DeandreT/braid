using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

internal static partial class RegressionCaseRoleGuard
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Bodies = new(LoadBodies);

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        RegressionCaseDatabaseProfile profile, CancellationToken token)
    {
        var role = profile == RegressionCaseDatabaseProfile.Reader ? "braid_case_reader" : "braid_case_writer";
        var permitted = profile == RegressionCaseDatabaseProfile.Reader
            ? new[] { "read_regression_case", "read_regression_case_metadata", "list_regression_cases" }
            : new[] { "read_regression_case", "read_regression_case_metadata", "list_regression_cases", "save_regression_case" };
        await using (var inspect = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=@role AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL
                AND current_setting('transaction_isolation')='read committed'
                AND current_setting('session_replication_role')='origin' AND current_setting('row_security')='on'
                AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on'
                AND coalesce(nullif(current_setting('braid.organization_id',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.case_principal_id',true),''),'')=''
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
                AND NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')
                    AND NOT (n.nspname='braid' AND p.proname=ANY(@permitted)))
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            inspect.Parameters.AddWithValue("role", role);
            inspect.Parameters.AddWithValue("permitted", permitted);
            if (await inspect.ExecuteScalarAsync(token) is not true) throw Refused();
        }
        await using var functions = new NpgsqlCommand("""
            SELECT p.proname,p.prosrc,p.prosecdef,p.provolatile,l.lanname,
                p.proowner=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname='braid_migrator')
                AND NOT p.proleakproof AND NOT p.proisstrict AND p.prokind='f' AND p.proparallel='u'
                AND p.pronargdefaults=0 AND p.proconfig=ARRAY['search_path=pg_catalog, braid, pg_temp']::text[]
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a WHERE a.grantee=0),
                pg_catalog.oidvectortypes(p.proargtypes),p.proargnames[1:p.pronargs],pg_catalog.pg_get_function_result(p.oid)
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_catalog.pg_language l ON l.oid=p.prolang
            WHERE n.nspname='braid' AND p.proname=ANY(@names)
            """, connection, transaction);
        functions.Parameters.AddWithValue("names", Bodies.Value.Keys.ToArray());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await functions.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var name = reader.GetString(0);
            var privileged = name is "save_regression_case" or "read_regression_case" or "read_regression_case_metadata" or "list_regression_cases";
            var sql = name is "case_authorized" or "read_regression_case" or "read_regression_case_metadata" or "list_regression_cases";
            var signature = Signatures[name];
            if (!seen.Add(name) || reader.GetString(1) != Bodies.Value[name] || reader.GetBoolean(2) != privileged ||
                reader.GetChar(3) != (sql ? 's' : 'v') || reader.GetString(4) != (sql ? "sql" : "plpgsql") || !reader.GetBoolean(5) ||
                reader.GetString(6) != signature.Types || reader.GetString(8) != signature.Result ||
                !(reader.IsDBNull(7) ? Array.Empty<string>() : reader.GetFieldValue<string[]>(7)).SequenceEqual(signature.Names))
                throw Refused();
        }
        if (seen.Count != Bodies.Value.Count) throw Refused();
        await reader.DisposeAsync();
        await RegressionCaseSchemaContract.ValidateAsync(connection, transaction, token);
    }

    private static readonly IReadOnlyDictionary<string, (string Types, string[] Names, string Result)> Signatures =
        new Dictionary<string, (string, string[], string)>(StringComparer.Ordinal)
        {
            ["case_authorized"] = ("uuid, uuid, uuid, text[]", ["org", "project", "actor", "allowed"], "boolean"),
            ["guard_regression_case"] = ("", [], "trigger"),
            ["guard_regression_version"] = ("", [], "trigger"),
            ["save_regression_case"] = ("uuid, uuid, uuid, uuid, bigint, uuid, uuid, text, text, text, integer, bytea",
                ["org", "project", "mapping", "id", "requested_version", "creator", "revision", "native_sha", "case_name", "content_sha", "content_bytes", "body"], "integer"),
            ["read_regression_case"] = ("uuid, uuid, uuid, uuid, bigint", ["org", "project", "mapping", "id", "requested_version"],
                "TABLE(creator_id uuid, revision_id uuid, native_digest text, content_digest text, byte_count integer, envelope bytea)"),
            ["read_regression_case_metadata"] = ("uuid, uuid, uuid, uuid, bigint", ["org", "project", "mapping", "id", "requested_version"],
                "TABLE(case_id uuid, version bigint, creator_id uuid, revision_id uuid, name text, saved_by uuid, created_at timestamp with time zone)"),
            ["list_regression_cases"] = ("uuid, uuid, uuid, uuid, integer", ["org", "project", "mapping", "after_id", "page_size"],
                "TABLE(case_id uuid, version bigint, creator_id uuid, revision_id uuid, name text, saved_by uuid, created_at timestamp with time zone)")
        };

    private static IReadOnlyDictionary<string, string> LoadBodies()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Braid.Persistence.Postgres.Migrations.0014_regression_cases.sql")
            ?? throw Refused();
        using var reader = new StreamReader(resource);
        var text = reader.ReadToEnd();
        var result = FunctionPattern().Matches(text).ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        using var supplemental = Assembly.GetExecutingAssembly().GetManifestResourceStream("Braid.Persistence.Postgres.Migrations.0033_preview_reads.sql") ?? throw Refused();
        using var supplementalReader = new StreamReader(supplemental);
        var source = supplementalReader.ReadToEnd();
        var start = source.IndexOf("CREATE FUNCTION braid.read_regression_case_metadata(", StringComparison.Ordinal);
        if (start < 0) throw Refused();
        var metadata = FunctionPattern().Match(source, start);
        if (!metadata.Success || metadata.Index != start || metadata.Groups[1].Value != "read_regression_case_metadata") throw Refused();
        result.Add("read_regression_case_metadata", metadata.Groups[2].Value);
        if (result.Count != 7) throw Refused();
        return result;
    }

    [GeneratedRegex(@"CREATE FUNCTION braid\.([a-z_]+)\(.*?AS \$body\$(.*?)\$body\$;", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FunctionPattern();
    private static InvalidOperationException Refused() => new("Regression case database contract is unavailable.");
}
