using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// Fixture access to a genuine pre-metadata-reader prefix. Like HistoricalIngestionSeed, this admits the
/// frozen source before invoking its restricted SQL ports; it never installs a later function early or
/// relaxes the current application's admission. Once upgraded, fixtures use the current stores again.
/// </summary>
internal static class HistoricalRegressionAccess
{
    private static readonly Dictionary<string, Match> Cases = Load("0014_regression_cases.sql", 6);
    private static readonly Dictionary<string, Match> Runs = new[]
        {
            Load("0015_regression_runs.sql", 9),
            Load("0016_regression_dispatch.sql", 1),
            Load("0017_regression_run_history.sql", 1)
        }.SelectMany(definitions => definitions).ToDictionary(pair => pair.Key, pair => pair.Value);
    internal static (int Cases, int Runs) SourceCounts => (Cases.Count, Runs.Count);
    private static int MetadataVersion => MigrationRunner.EmbeddedMigrations.Single(
        script => script.Checksum == PreviewSchemaTestContract.ReadsMigrationChecksum).Version;

    internal static async Task<bool> AppliesAsync(RegressionCasePostgresTestSupport data)
    {
        if (data.HistoricalPrefix is not { } prefix || prefix >= MetadataVersion) return false;
        var history = await data.Case.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations");
        if (history.Length >= MetadataVersion) return false;
        Assert.InRange(prefix, 17, MetadataVersion - 1);
        // A fixture can advance through other historical prefixes before installing the metadata readers.
        Assert.InRange(history.Length, prefix, MetadataVersion - 1);
        Assert.Equal(MigrationRunner.EmbeddedMigrations.Take(history.Length).Select(script => script.Checksum), history);
        return true;
    }

    internal static Task<RegressionCaseSaveResult> SaveAsync(RegressionCasePostgresTestSupport data, ActorContext actor, RegressionCaseVersion value) =>
        WithAsync(data, true, true, actor, async (connection, transaction) =>
        {
            var envelope = new RegressionCaseEnvelopeCodec().Encode(value);
            await using var command = new NpgsqlCommand("SELECT weft.save_regression_case($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)", connection, transaction);
            foreach (var parameter in new object[] { value.Revision.OrganizationId, value.Revision.ProjectId, value.Revision.MappingId,
                value.CaseId, value.Version, value.Owner.Value, value.Revision.RevisionId, value.NativeDigest.Value, value.Name,
                envelope.Reference.ContentDigest.Value, envelope.Reference.ByteCount, envelope.CopyBytes() }) command.Parameters.AddWithValue(parameter);
            var outcome = (RegressionCaseSaveOutcome)(int)(await command.ExecuteScalarAsync())!;
            return outcome is RegressionCaseSaveOutcome.Created or RegressionCaseSaveOutcome.Appended or RegressionCaseSaveOutcome.Replayed
                ? RegressionCaseSaveResult.Saved(outcome, envelope.Reference) : RegressionCaseSaveResult.Refused(outcome);
        });

    internal static Task<RegressionCaseVersion?> ReadAsync(RegressionCasePostgresTestSupport data, ActorContext actor, MappingScope mapping, Guid id, long version) =>
        WithAsync<RegressionCaseVersion?>(data, true, false, actor, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT * FROM weft.read_regression_case($1,$2,$3,$4,$5)", connection, transaction);
            foreach (var parameter in new object[] { mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, id, version }) command.Parameters.AddWithValue(parameter);
            await using var row = await command.ExecuteReaderAsync();
            if (!await row.ReadAsync()) return null;
            var reference = new RegressionCaseReference(id, version, new(row.GetGuid(0)),
                new(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, row.GetGuid(1)),
                Sha256Digest.Parse(row.GetString(2)), Sha256Digest.Parse(row.GetString(3)), row.GetInt32(4));
            var result = new RegressionCaseEnvelopeCodec().Decode(reference, row.GetFieldValue<byte[]>(5));
            Assert.False(await row.ReadAsync());
            return result;
        });

    internal static Task<RegressionRunSubmitOutcome> SubmitAsync(RegressionRunPostgresTestSupport data) =>
        WithAsync(data.Cases, false, true, data.Actor, async (connection, transaction) =>
        {
            var reference = data.Reference;
            await using var command = new NpgsqlCommand("SELECT weft.submit_regression_run($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)", connection, transaction);
            foreach (var parameter in new object[] { data.Organization, reference.Revision.ProjectId, reference.Revision.MappingId,
                data.RunId, reference.CaseId, reference.Version, reference.Owner.Value, reference.Revision.RevisionId,
                reference.NativeDigest.Value, reference.ContentDigest.Value, reference.ByteCount }) command.Parameters.AddWithValue(parameter);
            return (RegressionRunSubmitOutcome)(int)(await command.ExecuteScalarAsync())!;
        });

    internal static Task<RegressionRunClaim> ClaimAsync(RegressionRunPostgresTestSupport data) =>
        WithAsync(data.Cases, false, true, null, async (connection, transaction) =>
        {
            var worker = Guid.NewGuid();
            await using var command = new NpgsqlCommand("SELECT * FROM weft.claim_regression_run($1,$2,$3)", connection, transaction);
            command.Parameters.AddWithValue(data.Organization); command.Parameters.AddWithValue(data.RunId); command.Parameters.AddWithValue(worker);
            await using var row = await command.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync()); Assert.Equal(0, row.GetInt32(0));
            var reference = new RegressionCaseReference(row.GetGuid(6), row.GetInt64(7), new(row.GetGuid(8)),
                new(data.Organization, row.GetGuid(4), row.GetGuid(5), row.GetGuid(9)),
                Sha256Digest.Parse(row.GetString(10)), Sha256Digest.Parse(row.GetString(11)), row.GetInt32(12));
            Assert.Equal(data.Reference, reference);
            var claim = new RegressionRunClaim(data.RunId, new(row.GetGuid(13)), reference, row.GetInt32(1), worker, row.GetGuid(2), new(row.GetDateTime(3)));
            Assert.False(await row.ReadAsync());
            return claim;
        });

    internal static Task<bool> CompleteAsync(RegressionRunPostgresTestSupport data, RegressionRunClaim claim, RegressionCaseExecution result) =>
        WithAsync(data.Cases, false, true, null, async (connection, transaction) =>
        {
            var bytes = new RegressionRunResultCodec().Encode(claim.RunId, claim.Case, result);
            await using var command = new NpgsqlCommand("SELECT weft.finish_regression_run($1,$2,$3,$4,$5,$6,$7,$8)", connection, transaction);
            foreach (var parameter in new object[] { data.Organization, claim.RunId, claim.Worker, claim.Token, claim.Attempt,
                bytes, Sha256Digest.FromBytes(bytes).Value, bytes.Length }) command.Parameters.AddWithValue(parameter);
            return (bool)(await command.ExecuteScalarAsync())!;
        });

    internal static Task<RegressionRunSnapshot?> ReadRunAsync(RegressionRunPostgresTestSupport data, ActorContext actor, Guid organization, Guid id) =>
        WithAsync<RegressionRunSnapshot?>(data.Cases, false, false, actor, async (connection, transaction) =>
        {
            Assert.Equal(data.Organization, organization);
            await using var command = new NpgsqlCommand("""
                SELECT project_id,mapping_id,submitter_id,case_id,version,creator_id,revision_id,native_digest,
                    case_digest,case_bytes,state,attempt,created_at,result_digest,result_bytes,result_body
                FROM weft.read_regression_run($1,$2)
                """, connection, transaction);
            command.Parameters.AddWithValue(organization); command.Parameters.AddWithValue(id);
            await using var row = await command.ExecuteReaderAsync();
            if (!await row.ReadAsync()) return null;
            var reference = new RegressionCaseReference(row.GetGuid(3), row.GetInt64(4), new(row.GetGuid(5)),
                new(organization, row.GetGuid(0), row.GetGuid(1), row.GetGuid(6)),
                Sha256Digest.Parse(row.GetString(7)), Sha256Digest.Parse(row.GetString(8)), row.GetInt32(9));
            Assert.Equal(data.Reference, reference);
            Assert.True(Enum.TryParse<RegressionRunState>(row.GetString(10), true, out var state));
            byte[]? bytes = null;
            Sha256Digest? digest = null;
            if (state == RegressionRunState.Completed)
            {
                bytes = row.GetFieldValue<byte[]>(15); Assert.Equal(row.GetInt32(14), bytes.Length);
                digest = Sha256Digest.Parse(row.GetString(13));
            }
            else Assert.True(row.IsDBNull(13) && row.IsDBNull(14) && row.IsDBNull(15));
            var snapshot = new RegressionRunSnapshot(id, new(row.GetGuid(2)), reference, state,
                row.GetInt32(11), new(row.GetDateTime(12)), bytes, digest);
            Assert.False(await row.ReadAsync());
            return snapshot;
        });

    internal static async Task SeedAutomationFailureAsync(RegressionRunPostgresTestSupport data, Guid suiteId)
    {
        foreach (var (operation, id, body) in new[]
        {
            ("subscription", (Guid?)null, JsonSerializer.Serialize(new { events = new[] { "regressionFailed" }, inApp = true, email = false, webhooks = Array.Empty<string>() })),
            ("recheck", (Guid?)suiteId, JsonSerializer.Serialize(new { mappingId = data.Reference.Revision.MappingId, revisionId = data.Reference.Revision.RevisionId }))
        })
            Assert.True(await WithAsync(data.Cases, false, true, data.Actor, async (connection, transaction) =>
            {
                await using var command = new NpgsqlCommand("SELECT weft.automation_write($1,$2,$3,$4,$5)", connection, transaction);
                command.Parameters.AddWithValue(data.Organization); command.Parameters.AddWithValue(data.Reference.Revision.ProjectId);
                command.Parameters.AddWithValue(operation); command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)id ?? DBNull.Value);
                command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Jsonb, body);
                return (bool)(await command.ExecuteScalarAsync())!;
            }));
        var claim = await WithAsync(data.Cases, false, true, null, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT weft.automation_claim_evaluation()", connection, transaction);
            using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
            var value = document.RootElement;
            Assert.Equal(data.Organization, value.GetProperty("organizationId").GetGuid());
            Assert.Equal(suiteId, value.GetProperty("suiteId").GetGuid());
            Assert.Equal(data.Reference.CaseId, value.GetProperty("caseId").GetGuid());
            Assert.Equal(data.Reference.Version, value.GetProperty("version").GetInt64());
            return (Token: value.GetProperty("token").GetGuid(), Attempt: value.GetProperty("attempt").GetInt32());
        }, organizationScope: false);
        var bytes = new RegressionRunResultCodec().Encode(suiteId, data.Reference,
            RegressionCaseExecution.Refused(RegressionCaseExecutionOutcome.ResultRejected));
        Assert.True(await WithAsync(data.Cases, false, true, null, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT weft.automation_finish_evaluation($1,$2,$3,$4,$5,$6)", connection, transaction);
            foreach (var value in new object[] { data.Organization, suiteId, data.Reference.CaseId, claim.Token, claim.Attempt, bytes }) command.Parameters.AddWithValue(value);
            return (bool)(await command.ExecuteScalarAsync())!;
        }));
    }

    private static async Task<T> WithAsync<T>(RegressionCasePostgresTestSupport data, bool cases, bool writer, ActorContext? actor,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action, bool organizationScope = true)
    {
        Assert.True(await AppliesAsync(data), "Historical fixture access requires its exact pre-metadata-reader prefix.");
        var role = cases ? writer ? "weft_case_writer" : "weft_case_reader" : writer ? "weft_run_writer" : "weft_run_reader";
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(data.Case.RegressionConnectionString(role)) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(); await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'", connection, transaction))
            await settings.ExecuteNonQueryAsync();
        if (!writer)
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync();
        var permitted = cases ? writer ? new[] { "read_regression_case", "list_regression_cases", "save_regression_case" } : ["read_regression_case", "list_regression_cases"]
            : writer ? new[] { "submit_regression_run", "claim_regression_run", "observe_regression_run", "finish_regression_run", "release_regression_run", "cancel_regression_run", "read_regression_run", "list_ready_regression_runs", "list_regression_run_history" }.Concat(AutomationSchemaAdmission.WriteFunctions).ToArray()
            : new[] { "read_regression_run", "list_regression_run_history" }.Concat(AutomationSchemaAdmission.ReadFunctions).ToArray();
        await using (var header = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=$1 AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL AND current_setting('row_security')='on'
                AND current_setting('session_replication_role')='origin' AND current_setting('transaction_isolation')='read committed'
                AND NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid OR roleid=r.oid)
                AND NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')
                        AND NOT(n.nspname='weft' AND p.proname=ANY($2)))
            FROM pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            header.Parameters.AddWithValue(role); header.Parameters.AddWithValue(permitted);
            if (await header.ExecuteScalarAsync() is not true) throw new InvalidOperationException("Historical regression role refused.");
        }
        await ValidateFunctionsAsync(connection, transaction, cases ? Cases : Runs);
        if (cases) await RegressionCaseSchemaContract.ValidateAsync(connection, transaction, default);
        else if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, ["regression_runs"], default) != RegressionRunRoleGuard.Fingerprint)
            throw new InvalidOperationException("Historical regression schema refused.");
        await using (var settings = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true),set_config($2,$3,true)", connection, transaction))
        {
            settings.Parameters.AddWithValue(organizationScope ? data.Mapping.OrganizationId.ToString("D") : "");
            settings.Parameters.AddWithValue(cases ? "weft.case_principal_id" : "weft.run_principal_id");
            settings.Parameters.AddWithValue(actor?.PrincipalId.Value.ToString("D") ?? "");
            await settings.ExecuteNonQueryAsync();
        }
        var result = await action(connection, transaction); await transaction.CommitAsync(); return result;
    }

    private static async Task ValidateFunctionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Dictionary<string, Match> definitions)
    {
        await using var command = new NpgsqlCommand("""
            SELECT p.proname,p.prosrc,p.prosecdef,p.provolatile,l.lanname,pg_catalog.oidvectortypes(p.proargtypes),
                p.proargnames[1:p.pronargs],pg_catalog.pg_get_function_result(p.oid),
                p.proowner=(SELECT oid FROM pg_roles WHERE rolname='weft_migrator') AND NOT p.proleakproof
                AND NOT p.proisstrict AND p.prokind='f' AND p.proparallel='u' AND p.pronargdefaults=0
                AND p.proconfig=ARRAY['search_path=pg_catalog, weft, pg_temp']::text[]
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a WHERE a.grantee=0)
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_language l ON l.oid=p.prolang
            WHERE n.nspname='weft' AND p.proname=ANY($1)
            """, connection, transaction);
        command.Parameters.AddWithValue(definitions.Keys.ToArray());
        var seen = new HashSet<string>();
        await using var rows = await command.ExecuteReaderAsync();
        while (await rows.ReadAsync())
        {
            var definition = definitions[rows.GetString(0)];
            var arguments = definition.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(value => value.Split(' ', 2)).ToArray();
            if (!seen.Add(rows.GetString(0)) || rows.GetString(1) != definition.Groups[7].Value ||
                rows.GetBoolean(2) != (definition.Groups[6].Value == "DEFINER") || rows.GetChar(3) != (definition.Groups[5].Value == "STABLE" ? 's' : 'v') ||
                rows.GetString(4) != definition.Groups[4].Value || Normalize(rows.GetString(5)) != Normalize(string.Join(',', arguments.Select(arg => arg[1]))) ||
                !(rows.IsDBNull(6) ? Array.Empty<string>() : rows.GetFieldValue<string[]>(6)).SequenceEqual(arguments.Select(arg => arg[0])) ||
                Normalize(rows.GetString(7)) != Normalize(definition.Groups[3].Value) || !rows.GetBoolean(8))
                throw new InvalidOperationException("Historical regression function refused.");
        }
        if (seen.Count != definitions.Count) throw new InvalidOperationException("Historical regression functions incomplete.");
    }

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"\s+", "").Replace("timestamptz", "timestampwithtimezone", StringComparison.Ordinal);

    private static Dictionary<string, Match> Load(string name, int count)
    {
        using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream("Weft.Persistence.Postgres.Migrations." + name)
            ?? throw new InvalidOperationException("Historical regression source unavailable.");
        using var reader = new StreamReader(stream);
        // The original case trigger declarations omit VOLATILE, PostgreSQL's default.
        var definitions = Regex.Matches(reader.ReadToEnd(),
            @"CREATE FUNCTION weft\.([a-z_]+)\((.*?)\)\s+RETURNS\s+(.*?)\s+LANGUAGE (\w+)(?: (STABLE|VOLATILE))? SECURITY (DEFINER|INVOKER) SET search_path=pg_catalog,weft,pg_temp\s+AS \$body\$(.*?)\$body\$;",
            RegexOptions.Singleline | RegexOptions.CultureInvariant).ToDictionary(match => match.Groups[1].Value);
        if (definitions.Count != count) throw new InvalidOperationException("Historical regression declarations incomplete.");
        return definitions;
    }
}
