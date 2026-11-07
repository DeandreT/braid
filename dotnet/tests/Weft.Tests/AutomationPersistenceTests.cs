using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Application.Automation;
using Weft.Application.Regression;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Xunit.Abstractions;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AutomationPersistenceTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Automation_schema_fingerprint_matches_the_pristine_full_installation()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, AutomationSchemaAdmission.Tables, default, validateAutomation: false);
        output.WriteLine("AutomationFingerprint=" + fingerprint);
        Assert.Equal(AutomationSchemaAdmission.ComparisonFingerprint, fingerprint);
    }
    [PostgresFact]
    public async Task Automation_admission_rejects_new_table_drift_and_expanded_runtime_privileges()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var store = new PostgresAutomationStore(test.RegressionConnectionString("weft_run_writer"), true);
        var actor = new ActorContext(new(Guid.NewGuid()));
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(await store.ReadAsync(actor, project, "policy", null, null, 1));
        await test.BootstrapAsync("GRANT SELECT ON weft.automation_projects TO weft_run_writer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(actor, project, "policy", null, null, 1));
        await test.BootstrapAsync("REVOKE SELECT ON weft.automation_projects FROM weft_run_writer");
        Assert.Null(await store.ReadAsync(actor, project, "policy", null, null, 1));
        await test.BootstrapAsync("GRANT EXECUTE ON FUNCTION weft.notification_emit(uuid,uuid,text,uuid,uuid,uuid,uuid) TO weft_catalog_reader");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(actor, project, "policy", null, null, 1));
        await test.BootstrapAsync("REVOKE EXECUTE ON FUNCTION weft.notification_emit(uuid,uuid,text,uuid,uuid,uuid,uuid) FROM weft_catalog_reader");
        Assert.Null(await store.ReadAsync(actor, project, "policy", null, null, 1));
        await test.ExecuteAsync("ALTER TABLE weft.automation_projects ADD COLUMN unqualified boolean");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(actor, project, "policy", null, null, 1));
        await test.ExecuteAsync("ALTER TABLE weft.automation_projects DROP COLUMN unqualified");
        // Dropping a column leaves a catalog tombstone: do not accept it as the pristine admitted table.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(actor, project, "policy", null, null, 1));
    }

    [PostgresFact]
    public async Task Exact_schema_and_populated_upgrade_preserve_original_cases_runs_and_revision_bytes()
    {
        await using var runs = await RegressionRunPostgresTestSupport.CreateAsync(fixture, 29);
        var data = runs.Cases;
        Assert.Equal(RegressionRunSubmitOutcome.Created, await runs.SubmitAsync());
        var before = await data.Case.ScalarAsync<string>("SELECT md5(string_agg(t::text,',' ORDER BY t.case_id)) FROM weft.regression_case_versions t");
        // Observe only Automation's delta before later document-profile replacements.
        await using (var runner = new MigrationRunner(data.Case.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(30).ToArray())) await runner.MigrateAsync();
        Assert.Equal(30L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
        Assert.Equal(RegressionSchemaTestContract.AutomationChecksum,
            await data.Case.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=30"));
        Assert.Equal(before, await data.Case.ScalarAsync<string>("SELECT md5(string_agg(t::text,',' ORDER BY t.case_id)) FROM weft.regression_case_versions t"));
        Assert.Equal(runs.Reference, (await runs.ReadAsync(data.Actor, data.Mapping.OrganizationId, runs.RunId))!.Case);
        await using var connection = await data.Case.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await AutomationSchemaAdmission.ValidateAsync(connection, transaction, default);
        output.WriteLine("AutomationFingerprint=" + await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, AutomationSchemaAdmission.Tables, default));
    }

    [PostgresFact]
    public async Task New_revision_snapshots_all_cases_and_isolates_failures_without_cloning_old_evidence()
    {
        await using var data = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var initial = await data.CommitCreateAsync();
        var revision = initial.Facts.FutureRevision;
        var mapping = new MappingScope(revision.OrganizationId, revision.ProjectId, revision.MappingId);
        await using var writer = new PostgresRegressionCaseStore(data.Case.RegressionConnectionString("weft_case_writer"), RegressionCaseDatabaseProfile.Writer);
        var originals = new List<RegressionCaseVersion>();
        for (var i = 0; i < 3; i++)
        {
            var value = new RegressionCaseVersion(Guid.NewGuid(), 1, data.Actor.PrincipalId, "synthetic", revision, initial.Facts.NativeObject.Digest,
                new("ferrule", "engine", "build", "adapter", PreviewPolicy.Profile),
                new(PreviewTargetSelection.All, "2026-10-25T12:00:00Z", PreviewLimits.Default, new(null, "{}"u8), [], []),
                RegressionComparisonService.ExactBytesPolicy, RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "{}"u8)]));
            originals.Add(value); Assert.Equal(RegressionCaseSaveOutcome.Created, (await writer.SaveAsync(data.Actor, value)).Outcome);
        }
        var facts = await data.ReserveAsync(intent: PostgresIngestionTestSupport.AppendIntent(initial, body: "new exact revision"u8.ToArray()));
        var claim = await data.ClaimAsync(facts); await data.ReadyAsync(facts, claim);
        Assert.Equal(IngestionStoreOutcome.Applied, (await data.Store.FinalizeAsync(data.Actor, claim)).Outcome);
        await using var automation = new PostgresAutomationStore(data.Case.RegressionConnectionString("weft_run_writer"), true);
        var seen = new HashSet<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var next = Assert.IsType<AutomationClaim>(await automation.ClaimEvaluationAsync(default));
            Assert.True(seen.Add(next.CaseId)); Assert.Equal(facts.FutureRevision, next.Target.Revision);
            Assert.Equal(revision, next.Original.Revision); Assert.Equal(facts.NativeObject.Digest, next.Target.NativeDigest);
            var result = i == 1 ? RegressionCaseExecution.Refused(RegressionCaseExecutionOutcome.ResultRejected) : Pass();
            Assert.True(await automation.FinishEvaluationAsync(next, result, default));
            Assert.False(await automation.FinishEvaluationAsync(next, result, default));
        }
        Assert.Null(await automation.ClaimEvaluationAsync(default));
        var suite = await automation.ReadAsync(data.Actor, data.Project, "suite", facts.FutureRevision.RevisionId, null, 1);
        Assert.Equal("failed", suite!.Value.GetProperty("state").GetString());
        Assert.Equal(2, suite.Value.GetProperty("passedCases").GetInt32());
        Assert.Equal(3L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_case_versions"));
        Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_runs"));
    }

    [PostgresFact]
    public async Task Subscription_is_owner_scoped_and_outbox_delivery_is_leased_and_bounded()
    {
        await using var data = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var store = new PostgresAutomationStore(data.Case.RegressionConnectionString("weft_run_writer"), true);
        Assert.True(await store.WriteAsync(data.Actor, data.Project, "subscription", null,
            JsonSerializer.SerializeToElement(new { events = new[] { "revisionUploaded" }, inApp = true, email = false, webhooks = new[] { "test" } })));
        var receipt = await data.CommitCreateAsync();
        var page = await store.ReadAsync(data.Actor, data.Project, "notifications", null, null, 10);
        Assert.Single(page!.Value.GetProperty("notifications").EnumerateArray());
        var claim = Assert.IsType<NotificationDeliveryClaim>(await store.ClaimDeliveryAsync(default));
        Assert.Equal(receipt.Facts.FutureRevision.RevisionId, claim.Message.ResourceId);
        Assert.Equal("webhook:test", claim.Destination);
        Assert.Null(await store.ClaimDeliveryAsync(default));
        Assert.True(await store.FinishDeliveryAsync(claim, NotificationDeliveryOutcome.Delivered, default));
        Assert.False(await store.FinishDeliveryAsync(claim, NotificationDeliveryOutcome.Delivered, default));
        Assert.Null(await store.ReadAsync(new(new(data.Seed.Bob)), data.Project, "subscription", null, null, 10));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.notification_delivery_attempts"));
    }

    private static RegressionCaseExecution Pass()
    {
        var outputs = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "{}"u8)]);
        var comparison = new RegressionComparisonService().Compare(outputs, new(Weft.Contracts.Operations.OperationStatusV1.Succeeded, outputs), RegressionComparisonService.ExactBytesPolicy);
        return RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, outputs, comparison);
    }

    [PostgresFact]
    public async Task Explicit_rechecks_keep_old_results_and_reject_other_revision_or_stale_case_passes()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.RoleAsync("administrator");
        await data.Writer.SaveAsync(data.Actor, data.Version(1));
        await using var store = new PostgresAutomationStore(data.Case.RegressionConnectionString("weft_run_writer"), true);
        var scope = new ProjectScope(data.Mapping.OrganizationId, data.Mapping.ProjectId);
        Assert.True(await store.WriteAsync(data.Actor, scope, "policy", null, JsonSerializer.SerializeToElement(new
        { requirePassingRegression = true, errorRatePercent = 20, minimumRequests = 10, windowSeconds = 300 })));
        var original = data.Version(1).Revision;
        async Task<bool> Gate(Guid revision) => await data.Case.ScalarAsync<bool>("SELECT weft.automation_gate($1,$2,$3,$4)", scope.OrganizationId, scope.ProjectId, data.Mapping.MappingId, revision);
        Assert.False(await Gate(original.RevisionId));
        var id = Guid.NewGuid();
        var request = JsonSerializer.SerializeToElement(new { mappingId = data.Mapping.MappingId, revisionId = original.RevisionId });
        Assert.True(await store.WriteAsync(data.Actor, scope, "recheck", id, request));
        Assert.True(await store.WriteAsync(data.Actor, scope, "recheck", id, request));
        var claimed = Assert.IsType<AutomationClaim>(await store.ClaimEvaluationAsync(default));
        Assert.Equal(id, claimed.SuiteId); Assert.Equal(original, claimed.Target.Revision);
        await data.Case.ExecuteAsync("UPDATE weft.automation_evaluations SET lease_expires=clock_timestamp()-interval '1 second' WHERE suite_id=$1", id);
        var replacement = Assert.IsType<AutomationClaim>(await store.ClaimEvaluationAsync(default));
        Assert.Equal(2, replacement.Attempt);
        var value = data.Version(1);
        var outputs = value.Expected;
        var result = RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, outputs,
            new RegressionComparisonService().Compare(outputs, new(Weft.Contracts.Operations.OperationStatusV1.Succeeded, outputs), RegressionComparisonService.ExactBytesPolicy));
        Assert.False(await store.FinishEvaluationAsync(claimed, result, default));
        Assert.True(await store.FinishEvaluationAsync(replacement, result, default));
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => data.Case.ExecuteAsync(
            "UPDATE weft.automation_evaluations SET state='queued',result_body=NULL,result_digest=NULL WHERE suite_id=$1", id))).SqlState);
        Assert.True(await Gate(original.RevisionId));
        Assert.False(await Gate(Guid.NewGuid()));
        await data.Writer.SaveAsync(data.Actor, data.Version(2));
        Assert.False(await Gate(original.RevisionId));
        Assert.True(await store.WriteAsync(data.Actor, scope, "recheck", id, request));
        Assert.Null(await store.ClaimEvaluationAsync(default));
        var nextId = Guid.NewGuid();
        Assert.True(await store.WriteAsync(data.Actor, scope, "recheck", nextId, request));
        var retry = Assert.IsType<AutomationClaim>(await store.ClaimEvaluationAsync(default));
        Assert.Equal(2, retry.CaseVersion);
        Assert.True(await store.FinishEvaluationAsync(retry, result, default)); // Old expected output cannot pass v2.
        Assert.False(await Gate(original.RevisionId));
        Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.automation_suites"));
        Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_case_versions"));
    }

    [PostgresFact]
    public async Task Delivery_retries_are_bounded_and_repeated_failures_disable_only_the_destination()
    {
        await using var data = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var store = new PostgresAutomationStore(data.Case.RegressionConnectionString("weft_run_writer"), true);
        Assert.True(await store.WriteAsync(data.Actor, data.Project, "subscription", null,
            JsonSerializer.SerializeToElement(new { events = new[] { "revisionUploaded" }, inApp = true, email = false, webhooks = new[] { "bad", "good" } })));
        await data.CommitCreateAsync();
        var first = Assert.IsType<NotificationDeliveryClaim>(await store.ClaimDeliveryAsync(default));
        var second = Assert.IsType<NotificationDeliveryClaim>(await store.ClaimDeliveryAsync(default));
        var bad = first.Destination == "webhook:bad" ? first : second;
        var good = first.Destination == "webhook:good" ? first : second;
        Assert.True(await store.FinishDeliveryAsync(good, NotificationDeliveryOutcome.Delivered, default));
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Equal(attempt, bad.Attempt);
            Assert.True(await store.FinishDeliveryAsync(bad, NotificationDeliveryOutcome.TransportFailure, default));
            if (attempt == 5) break;
            Assert.Null(await store.ClaimDeliveryAsync(default)); // Backoff is enforced by the DB clock.
            await data.Case.ExecuteAsync("UPDATE weft.notification_deliveries SET eligible_at=clock_timestamp()-interval '1 second' WHERE delivery_id=$1", bad.Id);
            bad = Assert.IsType<NotificationDeliveryClaim>(await store.ClaimDeliveryAsync(default));
        }
        Assert.Null(await store.ClaimDeliveryAsync(default));
        Assert.Equal("failed", await data.Case.ScalarAsync<string>("SELECT state FROM weft.notification_deliveries WHERE delivery_id=$1", bad.Id));
        Assert.Equal("delivered", await data.Case.ScalarAsync<string>("SELECT state FROM weft.notification_deliveries WHERE delivery_id=$1", good.Id));
        Assert.Equal(6L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.notification_delivery_attempts"));
        // A second independent source event produces the remaining five failures.
        await data.Case.ExecuteAsync("SELECT weft.notification_emit($1,$2,'revisionUploaded',NULL,NULL,$3,NULL)", data.Project.OrganizationId, data.Project.ProjectId, Guid.NewGuid());
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var next = Assert.IsType<NotificationDeliveryClaim>(await store.ClaimDeliveryAsync(default));
            await store.FinishDeliveryAsync(next, next.Destination == "webhook:good" ? NotificationDeliveryOutcome.Delivered : NotificationDeliveryOutcome.TransportFailure, default);
            await data.Case.ExecuteAsync("UPDATE weft.notification_deliveries SET eligible_at=clock_timestamp()-interval '1 second' WHERE state='queued'");
        }
        Assert.True(await data.Case.ScalarAsync<bool>("SELECT disabled FROM weft.notification_destinations WHERE destination='webhook:bad'"));
        Assert.False(await data.Case.ScalarAsync<bool>("SELECT disabled FROM weft.notification_destinations WHERE destination='webhook:good'"));
    }

    [PostgresFact]
    public async Task A_newer_failed_or_pending_suite_cannot_be_bypassed_with_an_older_pass()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.RoleAsync("administrator");
        var value = data.Version(1); await data.Writer.SaveAsync(data.Actor, value);
        await using var store = new PostgresAutomationStore(data.Case.RegressionConnectionString("weft_run_writer"), true);
        var project = new ProjectScope(data.Mapping.OrganizationId, data.Mapping.ProjectId);
        await store.WriteAsync(data.Actor, project, "policy", null, JsonSerializer.SerializeToElement(new
        { requirePassingRegression = true, errorRatePercent = 20, minimumRequests = 10, windowSeconds = 300 }));
        var request = JsonSerializer.SerializeToElement(new { mappingId = data.Mapping.MappingId, revisionId = value.Revision.RevisionId });
        await store.WriteAsync(data.Actor, project, "recheck", Guid.NewGuid(), request);
        var first = (await store.ClaimEvaluationAsync(default))!;
        var success = RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, value.Expected,
            new RegressionComparisonService().Compare(value.Expected, new(Weft.Contracts.Operations.OperationStatusV1.Succeeded, value.Expected), RegressionComparisonService.ExactBytesPolicy));
        await store.FinishEvaluationAsync(first, success, default);
        async Task<bool> Gate() => await data.Case.ScalarAsync<bool>("SELECT weft.automation_gate($1,$2,$3,$4)", project.OrganizationId, project.ProjectId, data.Mapping.MappingId, value.Revision.RevisionId);
        Assert.True(await Gate());
        await store.WriteAsync(data.Actor, project, "recheck", Guid.NewGuid(), request);
        Assert.False(await Gate());
        var second = (await store.ClaimEvaluationAsync(default))!;
        await store.FinishEvaluationAsync(second, RegressionCaseExecution.Refused(RegressionCaseExecutionOutcome.RuntimeMismatch), default);
        Assert.False(await Gate());
        Assert.Equal("passed", await data.Case.ScalarAsync<string>("SELECT state FROM weft.automation_suites WHERE suite_id=$1", first.SuiteId));
    }
}
