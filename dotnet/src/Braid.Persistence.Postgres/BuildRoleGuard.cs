using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

internal static partial class BuildRoleGuard
{
    internal const string Fingerprint = "f1ba1f6d43b368286906f5780d9ae67a953a60bb875573a9c043172f8220987a";
    internal static readonly string[] Tables = ["build_environments", "build_approvals", "build_publications"];
    internal static readonly string[] Names = ["build_json_text", "build_intent_canonical", "build_authorized", "build_evidence_passes", "build_approval_active", "guard_build_record", "read_build_record", "write_build_record"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(Load);
    private static readonly Lazy<Dictionary<string, Match>> DocumentDefinitions = new(() => LoadFunctions("0034_document_previews.sql", 2));
    private static readonly Lazy<Dictionary<string, Match>> InterchangeDefinitions = new(() => LoadFunctions("0035_x12_interchange_builds.sql", 1));
    private static readonly Lazy<Dictionary<string, Match>> CsvDefinitions = new(LoadCsvFunctions);
    internal static Match CsvQualificationDefinition => CsvDefinitions.Value["qualification_valid"];

    internal static async Task<bool> HasCsvBuildsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT prosrc FROM pg_catalog.pg_proc WHERE oid='braid.build_intent_canonical(jsonb)'::regprocedure", connection, transaction);
        return await command.ExecuteScalarAsync(token) is string source && source == CsvDefinitions.Value["build_intent_canonical"].Groups[7].Value;
    }

    internal static Match InterchangeQualificationDefinition => InterchangeDefinitions.Value["qualification_valid"];
    internal static async Task<bool> HasInterchangeBuildsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT prosrc FROM pg_catalog.pg_proc WHERE oid='braid.qualification_valid(uuid,uuid,uuid,uuid,uuid,bytea,bytea)'::regprocedure", connection, transaction);
        return await command.ExecuteScalarAsync(token) is string source && source == InterchangeQualificationDefinition.Groups[7].Value;
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool writer, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=@role AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL
                AND current_setting('transaction_isolation')='read committed' AND current_setting('session_replication_role')='origin'
                AND current_setting('row_security')='on' AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on'
                AND coalesce(nullif(current_setting('braid.organization_id',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_principal_id',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_worker_id',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_claim_token',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_claim_attempt',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_claim_intent',true),''),'')=''
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
                AND NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')
                    AND NOT (n.nspname='braid' AND p.proname=ANY(@functions)))
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("role", writer ? "braid_build_writer" : "braid_build_reader");
            command.Parameters.AddWithValue("functions", writer ? new[] { "read_build_record", "write_build_record" } : ["read_build_record"]);
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await ValidateContractAsync(connection, transaction, token);
    }

    internal static async Task ValidateContractAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var definitions = Definitions.Value;
        if (await HasCsvBuildsAsync(connection, transaction, token))
        {
            definitions = new(definitions, StringComparer.Ordinal)
            {
                ["build_intent_canonical"] = CsvDefinitions.Value["build_intent_canonical"],
                ["build_evidence_passes"] = CsvDefinitions.Value["build_evidence_passes"],
                ["write_build_record"] = CsvDefinitions.Value["write_build_record"]
            };
            await ValidateFunctionsAsync(connection, transaction,
                ["csv_dialect_valid", "csv_dialect_char_canonical", "csv_dialect_canonical", "qualification_valid"], CsvDefinitions.Value, token);
        }
        else if (await HasDocumentBuildsAsync(connection, transaction, token))
        {
            definitions = new(definitions, StringComparer.Ordinal) { ["build_evidence_passes"] = DocumentDefinitions.Value["build_evidence_passes"] };
        }
        await ValidateFunctionsAsync(connection, transaction, Names, definitions, token);
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token);
        if (await BuildRunRoleGuard.IsInstalledAsync(connection, transaction, token))
        {
            var metadata = await BuildMetadataRoleGuard.IsInstalledAsync(connection, transaction, token);
            if (fingerprint != (metadata ? BuildMetadataRoleGuard.PublicationFingerprint : BuildRunRoleGuard.PublicationFingerprint)) throw Invalid();
            await BuildRunRoleGuard.ValidateContractAsync(connection, transaction, token);
        }
        else if (fingerprint != Fingerprint) throw Invalid();
    }

    internal static async Task ValidateFunctionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string[] names, Dictionary<string, Match> definitions, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT p.proname,p.prosrc,p.prosecdef,p.provolatile,l.lanname,pg_catalog.oidvectortypes(p.proargtypes),p.proargnames[1:p.pronargs],
                p.proowner=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname='braid_migrator')
                AND NOT p.proleakproof AND NOT p.proisstrict AND p.prokind='f' AND p.proparallel='u' AND p.pronargdefaults=0
                AND p.proconfig=ARRAY['search_path=pg_catalog, braid, pg_temp']::text[]
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a WHERE a.grantee=0),
                pg_catalog.pg_get_function_result(p.oid)
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_catalog.pg_language l ON l.oid=p.prolang WHERE n.nspname='braid' AND p.proname=ANY(@names)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("names", names);
            var seen = new HashSet<string>();
            await using var rows = await command.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
            {
                var name = rows.GetString(0);
                var definition = definitions[name];
                var args = definition.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(v => v.Split(' ', 2)).ToArray();
                if (!seen.Add(name) || rows.GetString(1) != definition.Groups[7].Value || rows.GetBoolean(2) != (definition.Groups[6].Value == "DEFINER") ||
                    rows.GetChar(3) != (definition.Groups[5].Value switch { "IMMUTABLE" => 'i', "STABLE" => 's', _ => 'v' }) || rows.GetString(4) != definition.Groups[4].Value ||
                    rows.GetString(5) != string.Join(", ", args.Select(a => a[1])) ||
                    !(rows.IsDBNull(6) ? Array.Empty<string>() : rows.GetFieldValue<string[]>(6)).SequenceEqual(args.Select(a => a[0])) ||
                    !rows.GetBoolean(7) || rows.GetString(8) != definition.Groups[3].Value.Trim()) throw Invalid();
            }
            if (seen.Count != names.Length) throw Invalid();
        }
    }

    private static Dictionary<string, Match> Load()
        => LoadFunctions("0018_build_publication.sql", Names.Length);

    internal static async Task<bool> HasDocumentBuildsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT prosrc FROM pg_catalog.pg_proc WHERE oid='braid.build_evidence_passes(uuid,uuid,uuid,text,text,text,text,text)'::regprocedure", connection, transaction);
        return await command.ExecuteScalarAsync(token) is string source && source == DocumentDefinitions.Value["build_evidence_passes"].Groups[7].Value;
    }

    internal static Match DocumentQualificationDefinition => DocumentDefinitions.Value["qualification_valid"];

    internal static Dictionary<string, Match> LoadFunctions(string file, int count)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Braid.Persistence.Postgres.Migrations." + file) ?? throw Invalid();
        using var reader = new StreamReader(stream);
        var result = Function().Matches(reader.ReadToEnd()).ToDictionary(match => match.Groups[1].Value, StringComparer.Ordinal);
        if (result.Count != count) throw Invalid();
        return result;
    }
    private static Dictionary<string, Match> LoadCsvFunctions()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Braid.Persistence.Postgres.Migrations.0041_csv_documents.sql") ?? throw Invalid();
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        var result = new Dictionary<string, Match>(StringComparer.Ordinal);
        foreach (var name in new[] { "csv_dialect_valid", "csv_dialect_char_canonical", "csv_dialect_canonical", "build_intent_canonical", "build_evidence_passes", "write_build_record", "qualification_valid" })
        {
            var start = source.IndexOf("FUNCTION braid." + name + "(", StringComparison.Ordinal);
            if (start < 0) throw Invalid();
            start = source.LastIndexOf("CREATE ", start, StringComparison.Ordinal);
            var match = Function().Match(source, start);
            if (!match.Success || match.Index != start || match.Groups[1].Value != name) throw Invalid();
            result.Add(name, match);
        }
        return result;
    }
    [GeneratedRegex(@"CREATE (?:OR REPLACE )?FUNCTION braid\.([a-z_]+)\((.*?)\)\s+RETURNS\s+(.*?)\s+LANGUAGE (\w+) (IMMUTABLE|STABLE|VOLATILE) SECURITY (DEFINER|INVOKER) SET search_path=pg_catalog,braid,pg_temp\s+AS \$body\$(.*?)\$body\$;", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Function();
    private static InvalidOperationException Invalid() => new("Build database contract unavailable.");
}
