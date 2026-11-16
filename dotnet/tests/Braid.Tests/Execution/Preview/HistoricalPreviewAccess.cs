using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Fixture-only access to an exact pre-XML prefix; never a production admission override.</summary>
internal static class HistoricalPreviewAccess
{
    internal sealed record Snapshot(int State, string RequestFingerprint);

    internal static Task<PreviewSubmitResult> SubmitAsync(PostgresCase test, int prefix, ActorContext actor, PreviewRunRecord record) =>
        WithAsync(test, prefix, true, async (connection, transaction) =>
        {
            var submission = record.Submission;
            var draft = submission.Subject as DraftPreviewSubject;
            Require(draft is null || prefix >= 38);
            var revision = submission.Subject.ExecutionScope;
            var inputs = submission.NamedInputs.Prepend(submission.Primary).ToArray();
            await using var command = new NpgsqlCommand(draft is null
                ? "SELECT outcome,run_id FROM braid.preview_submit($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)"
                : "SELECT outcome,run_id FROM braid.preview_submit_draft($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)", connection, transaction);
            foreach (var value in new object[] { revision.OrganizationId, revision.ProjectId, revision.MappingId, draft?.DraftId ?? revision.RevisionId, actor.PrincipalId.Value })
                command.Parameters.AddWithValue(value);
            if (draft is not null) command.Parameters.AddWithValue(draft.Version);
            foreach (var value in new object[] { submission.RequestKey, record.RequestDocument, record.CurrentDateTime, record.MaxAttempts, checked((int)record.Retention.TotalSeconds) })
                command.Parameters.AddWithValue(value);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, inputs.Select(input => input.Name).ToArray());
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, inputs.Select(input => input.CopyBytes()).ToArray());
            await using var row = await command.ExecuteReaderAsync();
            Require(await row.ReadAsync());
            var outcome = row.GetString(0) switch
            {
                "created" => PreviewSubmitOutcome.Created,
                "replayed" => PreviewSubmitOutcome.Replayed,
                "conflict" => PreviewSubmitOutcome.Conflict,
                "notAuthorizedOrNotFound" => PreviewSubmitOutcome.NotAuthorizedOrNotFound,
                _ => throw Refused()
            };
            var result = new PreviewSubmitResult(outcome, row.IsDBNull(1) ? null : row.GetGuid(1));
            Require(!await row.ReadAsync());
            return result;
        });

    internal static Task<Guid> ClaimAsync(PostgresCase test, int prefix, Guid organization, Guid run) =>
        WithAsync(test, prefix, true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT outcome,lease_token FROM braid.preview_claim($1,$2,$3,90)", connection, transaction);
            command.Parameters.AddWithValue(organization); command.Parameters.AddWithValue(run); command.Parameters.AddWithValue(Guid.NewGuid());
            await using var row = await command.ExecuteReaderAsync();
            Require(await row.ReadAsync() && row.GetString(0) == "claimed");
            var token = row.GetGuid(1);
            Require(!await row.ReadAsync());
            return token;
        });

    internal static Task<bool> CompleteAsync(PostgresCase test, int prefix, Guid organization, Guid run, Guid token,
        string profile, string mediaType, byte[] content, string targetName = "result") => WithAsync(test, prefix, true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT braid.preview_complete($1,$2,$3,'succeeded',NULL,NULL,NULL,NULL,NULL,true,'engine','image','adapter',$4,
                    1,ARRAY['primary-output'],ARRAY[$7],ARRAY[false],ARRAY[$5],ARRAY[1::bigint],ARRAY[$6])
                """, connection, transaction);
            command.Parameters.AddWithValue(organization); command.Parameters.AddWithValue(run); command.Parameters.AddWithValue(token);
            command.Parameters.AddWithValue(profile); command.Parameters.AddWithValue(mediaType); command.Parameters.AddWithValue(content); command.Parameters.AddWithValue(targetName);
            return (string)(await command.ExecuteScalarAsync())! == "completed";
        });

    internal static Task<Snapshot> ReadAsync(PostgresCase test, int prefix, ActorContext actor, Guid organization, Guid run) =>
        WithAsync(test, prefix, false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT state,request_fingerprint FROM braid.preview_read($1,$2,$3)", connection, transaction);
            command.Parameters.AddWithValue(organization); command.Parameters.AddWithValue(run); command.Parameters.AddWithValue(actor.PrincipalId.Value);
            await using var row = await command.ExecuteReaderAsync();
            Require(await row.ReadAsync());
            var result = new Snapshot(row.GetInt32(0), row.GetString(1));
            Require(!await row.ReadAsync());
            return result;
        });

    internal static Task<byte[]> ReadContentAsync(PostgresCase test, int prefix, ActorContext actor, Guid organization, Guid run, bool input) =>
        WithAsync(test, prefix, false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(input
                ? "SELECT content FROM braid.preview_input($1,$2,$3,0)"
                : "SELECT content FROM braid.preview_output($1,$2,$3,0)", connection, transaction);
            command.Parameters.AddWithValue(organization); command.Parameters.AddWithValue(run); command.Parameters.AddWithValue(actor.PrincipalId.Value);
            return (byte[])(await command.ExecuteScalarAsync())!;
        });

    private static async Task<T> WithAsync<T>(PostgresCase test, int prefix, bool writer, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action)
    {
        Require(prefix is 13 or 24 or 33 or 36 or 40 or 41);
        await using var owner = PostgresServerSessionTestSupport.Unpooled(test.MigrationConnectionString);
        await owner.OpenAsync();
        await using var admission = await owner.BeginTransactionAsync();
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock_shared(6287392987644524871)", owner, admission))
            await gate.ExecuteNonQueryAsync();
        var history = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT version,checksum FROM ONLY braid.schema_migrations ORDER BY version", owner, admission))
        await using (var rows = await command.ExecuteReaderAsync())
            while (await rows.ReadAsync()) { Require(rows.GetInt32(0) == history.Count + 1); history.Add(rows.GetString(1)); }
        Require(history.Count == prefix && history.SequenceEqual(MigrationRunner.EmbeddedMigrations.Take(prefix).Select(script => script.Checksum)));
        Require(!await XmlPreviewSchemaAdmission.InstalledAsync(owner, admission, default));
        // Reconstruct exactly the base's subject-aware function rewrite before
        // validating the callable definitions, including their security properties.
        var definitions = new Dictionary<string, Match>(StringComparer.Ordinal);
        const string pattern = @"CREATE (?:OR REPLACE )?FUNCTION braid\.(?<name>preview_[a-z_]+)\((?<arguments>.*?)\)\s+RETURNS\s+(?<returns>.*?)\s+LANGUAGE (?<language>\w+) (?<volatility>IMMUTABLE|STABLE|VOLATILE)(?: PARALLEL UNSAFE)? SECURITY (?<security>DEFINER|INVOKER) SET search_path\s*=\s*pg_catalog,braid,pg_temp\s+AS \$(?<tag>\w+)\$(?<body>.*?)\$\k<tag>\$;";
        foreach (var script in MigrationRunner.EmbeddedMigrations.Take(prefix))
        {
            if (script.Version == 38)
                foreach (var name in new[] { "preview_claim", "preview_cancel", "preview_read", "preview_outputs", "preview_output", "preview_input_documents", "preview_input" })
                {
                    var text = definitions[name].Value
                        .Replace("braid.preview_authorized(p_org,run.project_id,run.submitter_id)", "braid.preview_subject_authorized(p_org,p_run)", StringComparison.Ordinal)
                        .Replace("braid.preview_authorized(p_org,run.project_id,p_actor)", "braid.preview_subject_authorized(p_org,p_run)", StringComparison.Ordinal)
                        .Replace("braid.preview_authorized(p_org,r.project_id,p_actor)", "braid.preview_subject_authorized(p_org,p_run)", StringComparison.Ordinal);
                    definitions[name] = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant);
                }
            foreach (Match match in Regex.Matches(script.Sql, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant))
                definitions[match.Groups["name"].Value] = match;
        }
        await ValidateFunctionsAsync(owner, admission, definitions);
        var role = writer ? "braid_preview_writer" : "braid_preview_reader";
        await using var connection = PostgresServerSessionTestSupport.Unpooled(writer ? test.PreviewWriterConnectionString : test.PreviewReaderConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
            await settings.ExecuteNonQueryAsync();
        await using (var guard = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=$1 AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid OR roleid=r.oid)
                AND current_setting('row_security')='on' AND current_setting('session_replication_role')='origin'
                AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='braid' AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
            FROM pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            guard.Parameters.AddWithValue(role);
            Require(await guard.ExecuteScalarAsync() is true);
        }
        var result = await action(connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task ValidateFunctionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Dictionary<string, Match> definitions)
    {
        // This parser has named tag/body captures. BuildRoleGuard's unrelated
        // numeric capture layout cannot describe historical preview routines.
        await using var command = new NpgsqlCommand("""
            SELECT p.proname,p.prosrc,p.prosecdef,p.provolatile,l.lanname,pg_catalog.oidvectortypes(p.proargtypes),p.proargnames[1:p.pronargs],
                p.proowner=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname='braid_migrator')
                AND NOT p.proleakproof AND NOT p.proisstrict AND p.prokind='f' AND p.proparallel='u' AND p.pronargdefaults=0
                AND p.proconfig=ARRAY['search_path=pg_catalog, braid, pg_temp']::text[]
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a WHERE a.grantee=0),
                pg_catalog.pg_get_function_result(p.oid)
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_catalog.pg_language l ON l.oid=p.prolang WHERE n.nspname='braid' AND p.proname=ANY($1)
            """, connection, transaction);
        command.Parameters.AddWithValue(definitions.Keys.ToArray());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var rows = await command.ExecuteReaderAsync();
        while (await rows.ReadAsync())
        {
            var name = rows.GetString(0);
            var definition = definitions[name];
            var args = definition.Groups["arguments"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => Regex.Split(value, @"\s+", RegexOptions.CultureInvariant)).ToArray();
            Require(seen.Add(name) && rows.GetString(1) == definition.Groups["body"].Value &&
                rows.GetBoolean(2) == (definition.Groups["security"].Value == "DEFINER") &&
                rows.GetChar(3) == (definition.Groups["volatility"].Value switch { "IMMUTABLE" => 'i', "STABLE" => 's', _ => 'v' }) &&
                rows.GetString(4) == definition.Groups["language"].Value &&
                rows.GetString(5) == string.Join(", ", args.Select(arg => string.Join(" ", arg.Skip(1)))) &&
                (rows.IsDBNull(6) ? Array.Empty<string>() : rows.GetFieldValue<string[]>(6)).SequenceEqual(args.Select(arg => arg[0])) &&
                rows.GetBoolean(7) && NormalizeResult(rows.GetString(8)) == NormalizeResult(definition.Groups["returns"].Value));
        }
        Require(seen.Count == definitions.Count && seen.Count > 0);
    }

    private static string NormalizeResult(string result) => Regex.Replace(
        Regex.Replace(Regex.Replace(result.Trim(), @"\btimestamptz\b", "timestamp with time zone"), @"\s+", " "), @",\s*", ", ");

    private static void Require(bool accepted) { if (!accepted) throw Refused(); }
    private static InvalidOperationException Refused() => new("Historical preview fixture requires its exact restricted prefix.");
}
