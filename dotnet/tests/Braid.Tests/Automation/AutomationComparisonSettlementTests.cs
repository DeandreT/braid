using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Braid.Application;
using Braid.Application.Automation;
using Braid.Application.Regression;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Xunit.Abstractions;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AutomationComparisonSettlementTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Settlement_schema_has_an_exact_measured_fingerprint()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var actual = await RegressionCaseSchemaContract.CaptureAsync(connection, tx, AutomationSchemaAdmission.Tables, default, validateAutomation: false);
        output.WriteLine("ComparisonFingerprint=" + actual);
        Assert.Equal(AutomationSchemaAdmission.ComparisonFingerprint, actual);
    }

    public static TheoryData<string, string, string> Comparisons => new()
    {
        { "x12", "same", "passed" }, { "edifact", "same", "passed" },
        { "x12", "headerless", "notEvaluated" }, { "edifact", "headerless", "notEvaluated" },
        { "x12", "media", "failed" }, { "edifact", "media", "failed" },
        { "x12", "dates", "passed" }, { "edifact", "dates", "passed" },
        { "x12", "controls", "passed" }, { "edifact", "controls", "passed" },
        { "x12", "changed", "failed" }, { "edifact", "changed", "failed" },
        { "x12", "forged", "notEvaluated" }, { "edifact", "forged", "notEvaluated" },
        { "x12", "runtime", "notEvaluated" }, { "edifact", "nul-parameter", "passed" },
        { "x12", "strict-dates", "failed" }, { "x12", "replay", "notEvaluated" }
    };

    [RegressionPostgresTheory]
    [MemberData(nameof(Comparisons))]
    public async Task Stored_policy_not_the_submitted_pass_claim_controls_settlement_and_build_gate(string family, string change, string outcome)
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.RoleAsync("administrator");
        var text = change == "headerless" ? family == "x12" ? "BGM*220*VALUE~" : "BGM+220+VALUE'" : family == "x12" ? DocumentPreviewTestData.X12 : DocumentPreviewTestData.Edifact;
        var value = Document(data.Version(1), text, family);
        if (change == "strict-dates")
            value = new(value.CaseId, value.Version, value.Owner, value.Name, value.Revision, value.NativeDigest, value.Runtime,
                value.Replay, value.ComparisonPolicy, value.Expected, new(false, true, false));
        if (change == "nul-parameter")
            value = new(value.CaseId, value.Version, value.Owner, value.Name, value.Revision, value.NativeDigest, value.Runtime,
                new(value.Replay.Target, value.Replay.CurrentDateTime, value.Replay.Limits, value.Replay.Primary, [], [new("parameter", PreviewParameterKind.String, "a\0b")]),
                value.ComparisonPolicy, value.Expected, value.EdiOptions);
        var saved = await data.Writer.SaveAsync(data.Actor, value);
        await using var queue = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        await Enable(queue, data);
        var claim = await Recheck(queue, data, value);
        var actual = change switch
        {
            "dates" or "strict-dates" => text.Replace("261024", "261025", StringComparison.Ordinal),
            "controls" => text.Replace("330", "331", StringComparison.Ordinal).Replace("BRAID-331", "BRAID-330", StringComparison.Ordinal),
            "changed" => text.Replace("BRAID-330", "OTHER", StringComparison.Ordinal),
            _ => text
        };
        var outputs = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, Encoding.UTF8.GetBytes(actual),
            mediaType: change == "media" ? "application/json" : Media(family))]);
        // Deliberately manufacture a caller-side pass; the settlement authority must ignore it.
        var claimedPass = new RegressionComparisonService().Compare(value.Expected, new(Braid.Contracts.Operations.OperationStatusV1.Succeeded, value.Expected));
        var result = RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, outputs, claimedPass);
        if (change is "forged" or "replay")
        {
            var body = JsonNode.Parse(new RegressionRunResultCodec().Encode(claim.SuiteId, claim.Original, result))!;
            if (change == "forged") body["comparison"] = "passed";
            else body["runId"] = Guid.NewGuid().ToString("D");
            Assert.True(await RawFinish(data.Case, claim, Encoding.UTF8.GetBytes(body.ToJsonString())));
        }
        else Assert.True(await queue.FinishEvaluationAsync(claim, result, default));
        Assert.Equal("running", await State(data.Case, claim));
        Assert.False(await Gate(data));
        Assert.False(await queue.FinishEvaluationAsync(claim, result, default));
        var selectedRuntime = change == "runtime" ? new RegressionRuntimeIdentity(value.Runtime.BackendId, "wrong-engine", value.Runtime.BackendBuild, value.Runtime.AdapterBuild, value.Runtime.Profile) : value.Runtime;
        await using var verifier = new PostgresAutomationComparisonSettler(data.Case.RegressionConnectionString("braid_comparison_settler"), selectedRuntime);
        Assert.True(await verifier.SettleNextAsync(default));
        Assert.False(await verifier.SettleNextAsync(default));
        Assert.Equal(outcome, await data.Case.ScalarAsync<string>("SELECT comparison_outcome FROM braid.automation_evaluations WHERE suite_id=$1", claim.SuiteId));
        Assert.Equal(outcome == "passed" ? "passed" : "failed", await State(data.Case, claim));
        Assert.Equal(outcome == "passed", await Gate(data));
        Assert.False(await queue.FinishEvaluationAsync(claim, result, default));
        Assert.Equal(saved.Reference, new RegressionCaseEnvelopeCodec().Encode((await data.Reader.ReadAsync(data.Actor, data.Mapping, value.CaseId, 1))!).Reference);
    }

    [PostgresFact]
    public async Task Writer_cannot_settle_and_revoked_or_expired_claims_cannot_publish_comparisons()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.RoleAsync("administrator");
        var value = Document(data.Version(1), DocumentPreviewTestData.X12, "x12");
        await data.Writer.SaveAsync(data.Actor, value);
        await using var queue = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        var claim = await Recheck(queue, data, value);
        var result = Success(value);
        Assert.True(await queue.FinishEvaluationAsync(claim, result, default));
        await using (var wrongRole = new PostgresAutomationComparisonSettler(data.Case.RegressionConnectionString("braid_run_writer")))
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrongRole.SettleNextAsync(default));
        await using (var connection = new NpgsqlConnection(Unpooled(data.Case.RegressionConnectionString("braid_run_writer"))))
        {
            await connection.OpenAsync();
            await using var denied = new NpgsqlCommand("SELECT braid.automation_complete_comparison($1,$2,$3,$4,$5,$6,$7,$8,$9)", connection);
            foreach (var arg in new object[] { claim.OrganizationId, claim.SuiteId, claim.CaseId, claim.Token, claim.Attempt, claim.Original.ContentDigest.Value, new string('a', 64), value.ComparisonPolicy, "passed" }) denied.Parameters.AddWithValue(arg);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteScalarAsync())).SqlState);
        }
        await data.Case.ExecuteAsync("UPDATE braid.automation_evaluations SET lease_expires=statement_timestamp()-interval '1 second' WHERE suite_id=$1", claim.SuiteId);
        var replacement = (await queue.ClaimEvaluationAsync(default))!;
        Assert.NotEqual(claim.Token, replacement.Token);
        Assert.False(await queue.FinishEvaluationAsync(claim, result, default));
        Assert.True(await queue.FinishEvaluationAsync(replacement, result, default));
        await data.RoleAsync("viewer");
        await using var verifier = new PostgresAutomationComparisonSettler(data.Case.RegressionConnectionString("braid_comparison_settler"), value.Runtime);
        Assert.False(await verifier.SettleNextAsync(default));
        Assert.Equal("revoked", await State(data.Case, claim));
        Assert.True(await data.Case.ScalarAsync<bool>("SELECT pending_comparison IS NULL AND result_body IS NULL FROM braid.automation_evaluations WHERE suite_id=$1", claim.SuiteId));
        await data.RoleAsync("administrator");
        var retry = await Recheck(queue, data, value);
        Assert.True(await queue.FinishEvaluationAsync(retry, result, default));
        Assert.True(await verifier.SettleNextAsync(default));
        Assert.Equal("passed", await State(data.Case, retry));
    }

    [PostgresFact]
    public async Task Upgrade_retains_historical_bytes_but_unverified_document_pass_cannot_open_the_gate()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture, 35);
        await data.RoleAsync("administrator");
        var value = Document(data.Version(1), "BGM*220*VALUE~", "x12");
        await data.Writer.SaveAsync(data.Actor, value);
        await using var queue = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        await Enable(queue, data);
        var claim = await Recheck(queue, data, value);
        Assert.True(await queue.FinishEvaluationAsync(claim, Success(value), default));
        Assert.Equal("passed", await State(data.Case, claim));
        Assert.True(await Gate(data));
        var body = await data.Case.ScalarAsync<byte[]>("SELECT result_body FROM braid.automation_evaluations WHERE suite_id=$1", claim.SuiteId);
        await data.Case.MigrateAsync();
        Assert.Equal(body, await data.Case.ScalarAsync<byte[]>("SELECT result_body FROM braid.automation_evaluations WHERE suite_id=$1", claim.SuiteId));
        Assert.Equal("passed", await State(data.Case, claim));
        Assert.False(await Gate(data));
    }

    [PostgresFact]
    public async Task Legacy_exact_byte_settlement_is_unchanged_and_does_not_need_the_verifier()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.RoleAsync("administrator");
        var value = data.Version(1); await data.Writer.SaveAsync(data.Actor, value);
        await using var queue = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        await Enable(queue, data);
        var claim = await Recheck(queue, data, value);
        Assert.True(await queue.FinishEvaluationAsync(claim, Success(value), default));
        Assert.Equal("passed", await State(data.Case, claim)); Assert.True(await Gate(data));
        await using var verifier = new PostgresAutomationComparisonSettler(data.Case.RegressionConnectionString("braid_comparison_settler"));
        Assert.False(await verifier.SettleNextAsync(default));
    }

    [PostgresFact]
    public async Task Verifier_privileges_are_closed_and_completion_rechecks_authority_after_reading()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.RoleAsync("administrator");
        var value = Document(data.Version(1), DocumentPreviewTestData.X12, "x12");
        await data.Writer.SaveAsync(data.Actor, value);
        await using var queue = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        var claim = await Recheck(queue, data, value);
        var result = Success(value);
        Assert.True(await queue.FinishEvaluationAsync(claim, result, default));
        await using var verifier = new PostgresAutomationComparisonSettler(data.Case.RegressionConnectionString("braid_comparison_settler"), value.Runtime);
        await data.Case.BootstrapAsync("GRANT SELECT ON braid.automation_evaluations TO braid_comparison_settler");
        await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.SettleNextAsync(default));
        await data.Case.BootstrapAsync("REVOKE SELECT ON braid.automation_evaluations FROM braid_comparison_settler");
        await using (var connection = new NpgsqlConnection(Unpooled(data.Case.RegressionConnectionString("braid_comparison_settler"))))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using (var read = new NpgsqlCommand("SELECT * FROM braid.automation_read_comparison()", connection, tx))
            await using (var rows = await read.ExecuteReaderAsync()) Assert.True(await rows.ReadAsync());
            await using var finish = new NpgsqlCommand("SELECT braid.automation_complete_comparison($1,$2,$3,$4,$5,$6,$7,$8,$9)", connection, tx);
            foreach (var arg in new object[] { claim.OrganizationId, claim.SuiteId, claim.CaseId, claim.Token, claim.Attempt,
                claim.Original.ContentDigest.Value, Sha256Digest.FromBytes(new RegressionRunResultCodec().Encode(claim.SuiteId, claim.Original, result)).Value, value.ComparisonPolicy, "passed" }) finish.Parameters.AddWithValue(arg);
            foreach (var (index, wrong) in new (int, object)[] { (3, Guid.NewGuid()), (5, new string('a', 64)), (6, new string('b', 64)), (7, RegressionComparisonService.ExactBytesPolicy) })
            {
                var original = finish.Parameters[index].Value;
                finish.Parameters[index].Value = wrong;
                Assert.Equal(false, await finish.ExecuteScalarAsync());
                finish.Parameters[index].Value = original;
            }
            await data.RoleAsync("viewer");
            Assert.Equal(false, await finish.ExecuteScalarAsync());
            await tx.CommitAsync();
        }
        Assert.Equal("revoked", await State(data.Case, claim));
        Assert.False(await verifier.SettleNextAsync(default));
    }

    [PostgresFact]
    public async Task Inherited_authorization_drift_cannot_settle_a_revoked_actor_and_restoration_preserves_successful_rows()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.RoleAsync("administrator");
        var value = Document(data.Version(1), DocumentPreviewTestData.X12, "x12");
        var independent = Document(data.Version(1, caseId: Guid.NewGuid()), DocumentPreviewTestData.X12, "x12");
        await data.Writer.SaveAsync(data.Actor, value);
        await data.Writer.SaveAsync(data.Actor, independent);
        await using var queue = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        await using var verifier = new PostgresAutomationComparisonSettler(data.Case.RegressionConnectionString("braid_comparison_settler"), value.Runtime);
        var first = await Recheck(queue, data, value);
        Assert.True(await queue.FinishEvaluationAsync(first, Success(value), default));
        Assert.True(await verifier.SettleNextAsync(default));
        var successful = await EvaluationRow(data.Case, first);
        var pending = Assert.IsType<AutomationClaim>(await queue.ClaimEvaluationAsync(default));
        Assert.True(await queue.FinishEvaluationAsync(pending, Success(value), default));
        var before = await EvaluationRow(data.Case, pending);
        var original = await data.Case.ScalarAsync<string>("SELECT pg_get_functiondef('braid.regression_run_authorized(uuid,uuid,uuid)'::regprocedure)");
        try
        {
            await data.Case.ExecuteAsync("""
                CREATE OR REPLACE FUNCTION braid.regression_run_authorized(org uuid, project uuid, actor uuid) RETURNS boolean
                LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
                AS $body$ SELECT true $body$;
                """);
            await data.RoleAsync("viewer");
            var failure = await Record.ExceptionAsync(() => verifier.SettleNextAsync(default));
            output.WriteLine("Settlement state after authorization drift: " + await State(data.Case, pending));
            Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal(before, await EvaluationRow(data.Case, pending));
            Assert.Equal(successful, await EvaluationRow(data.Case, first));
            Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.automation_evaluations WHERE suite_id=$1 AND state='passed'", first.SuiteId));
        }
        finally { await data.Case.ExecuteAsync(original); }
        Assert.False(await verifier.SettleNextAsync(default));
        Assert.Equal("revoked", await State(data.Case, pending));
        Assert.Equal(successful, await EvaluationRow(data.Case, first));
        await data.RoleAsync("administrator");
        var retry = await Recheck(queue, data, value);
        for (var index = 0; index < 2; index++)
        {
            var current = index == 0 ? retry : Assert.IsType<AutomationClaim>(await queue.ClaimEvaluationAsync(default));
            Assert.True(await queue.FinishEvaluationAsync(current, Success(value), default));
            Assert.True(await verifier.SettleNextAsync(default));
        }
        Assert.Null(await queue.ClaimEvaluationAsync(default));
        Assert.False(await verifier.SettleNextAsync(default));
        Assert.False(await queue.FinishEvaluationAsync(first, Success(value), default));
        Assert.Equal(successful, await EvaluationRow(data.Case, first));
        Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.automation_evaluations WHERE suite_id=$1 AND state='passed'", retry.SuiteId));
        Assert.Equal(4L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.automation_evaluations"));
        Assert.False(await data.Case.ScalarAsync<bool>("SELECT has_table_privilege('braid_comparison_settler','braid.regression_runs','SELECT,INSERT,UPDATE,DELETE')"));
        Assert.False(await data.Case.ScalarAsync<bool>("SELECT has_function_privilege('braid_comparison_settler','braid.regression_run_authorized(uuid,uuid,uuid)','EXECUTE')"));
    }

    [PostgresFact]
    public async Task Settlement_admits_the_complete_inherited_run_contract_not_only_the_authorization_function()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        var value = Document(data.Version(1), DocumentPreviewTestData.Edifact, "edifact");
        await data.Writer.SaveAsync(data.Actor, value);
        await using var queue = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        var claim = await Recheck(queue, data, value);
        Assert.True(await queue.FinishEvaluationAsync(claim, Success(value), default));
        var before = await EvaluationRow(data.Case, claim);
        var original = await data.Case.ScalarAsync<string>("SELECT pg_get_functiondef('braid.guard_regression_run()'::regprocedure)");
        var changed = original.Replace("AS $function$", "AS $function$\n-- unqualified routine drift", StringComparison.Ordinal);
        Assert.NotEqual(original, changed);
        await using var verifier = new PostgresAutomationComparisonSettler(data.Case.RegressionConnectionString("braid_comparison_settler"), value.Runtime);
        try
        {
            await data.Case.ExecuteAsync(changed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.SettleNextAsync(default));
            Assert.Equal(before, await EvaluationRow(data.Case, claim));
        }
        finally { await data.Case.ExecuteAsync(original); }
        Assert.True(await verifier.SettleNextAsync(default));
        Assert.Equal("passed", await State(data.Case, claim));
        Assert.False(await verifier.SettleNextAsync(default));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.automation_evaluations WHERE state='passed'"));
    }

    private static Task<string> EvaluationRow(PostgresCase test, AutomationClaim claim) => test.ScalarAsync<string>(
        "SELECT to_jsonb(e)::text FROM braid.automation_evaluations e WHERE suite_id=$1 AND case_id=$2", claim.SuiteId, claim.CaseId);

    internal static RegressionCaseVersion Document(RegressionCaseVersion old, string baseline, string family) => new(old.CaseId, old.Version, old.Owner, old.Name,
        old.Revision, old.NativeDigest, new(old.Runtime.BackendId, old.Runtime.EngineRevision, old.Runtime.BackendBuild, "document-adapter", DocumentPreviewPolicy.Profile),
        old.Replay, RegressionComparisonService.EdiSegmentsPolicy,
        RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, Encoding.UTF8.GetBytes(baseline), mediaType: Media(family))]), new(false, true, true));
    private static string Media(string family) => family == "x12" ? "application/edi-x12" : "application/edifact";
    private static RegressionCaseExecution Success(RegressionCaseVersion value) => RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, value.Expected,
        new RegressionComparisonService().Compare(value.Expected, new(Braid.Contracts.Operations.OperationStatusV1.Succeeded, value.Expected)));
    private static Task<string> State(PostgresCase test, AutomationClaim claim) => test.ScalarAsync<string>("SELECT state FROM braid.automation_evaluations WHERE suite_id=$1 AND case_id=$2", claim.SuiteId, claim.CaseId);
    private static Task<bool> Gate(RegressionCasePostgresTestSupport data) => data.Case.ScalarAsync<bool>("SELECT braid.automation_gate($1,$2,$3,$4)", data.Mapping.OrganizationId, data.Mapping.ProjectId, data.Mapping.MappingId, data.Version(1).Revision.RevisionId);
    private static Task<bool> Enable(PostgresAutomationStore queue, RegressionCasePostgresTestSupport data) => queue.WriteAsync(data.Actor, new(data.Mapping.OrganizationId, data.Mapping.ProjectId), "policy", null,
        JsonSerializer.SerializeToElement(new { requirePassingRegression = true, errorRatePercent = 20, minimumRequests = 10, windowSeconds = 300 }));
    private static async Task<AutomationClaim> Recheck(PostgresAutomationStore queue, RegressionCasePostgresTestSupport data, RegressionCaseVersion value)
    {
        Assert.True(await queue.WriteAsync(data.Actor, new(data.Mapping.OrganizationId, data.Mapping.ProjectId), "recheck", Guid.NewGuid(), JsonSerializer.SerializeToElement(new { mappingId = data.Mapping.MappingId, revisionId = value.Revision.RevisionId })));
        return Assert.IsType<AutomationClaim>(await queue.ClaimEvaluationAsync(default));
    }
    private static async Task<bool> RawFinish(PostgresCase test, AutomationClaim claim, byte[] body)
    {
        await using var connection = new NpgsqlConnection(Unpooled(test.RegressionConnectionString("braid_run_writer"))); await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var bind = new NpgsqlCommand("SELECT set_config('braid.organization_id',$1,true)", connection, tx))
        { bind.Parameters.AddWithValue(claim.OrganizationId.ToString("D")); await bind.ExecuteScalarAsync(); }
        await using var command = new NpgsqlCommand("SELECT braid.automation_finish_evaluation($1,$2,$3,$4,$5,$6)", connection, tx);
        foreach (var arg in new object[] { claim.OrganizationId, claim.SuiteId, claim.CaseId, claim.Token, claim.Attempt, body }) command.Parameters.AddWithValue(arg);
        var result = await command.ExecuteScalarAsync() is true;
        await tx.CommitAsync();
        return result;
    }
    private static string Unpooled(string connection) => new NpgsqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString;
}
