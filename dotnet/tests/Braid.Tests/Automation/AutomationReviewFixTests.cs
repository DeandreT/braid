using System.Runtime.ExceptionServices;
using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Application.Automation;
using Braid.Application.Regression;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AutomationReviewFixTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Completion_rechecks_authority_after_the_executor_returns_and_never_keeps_a_revoked_pass()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.RoleAsync("administrator");
        var value = data.Version(1); await data.Writer.SaveAsync(data.Actor, value);
        await using var store = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        var scope = new ProjectScope(data.Mapping.OrganizationId, data.Mapping.ProjectId);
        await store.WriteAsync(data.Actor, scope, "policy", null, JsonSerializer.SerializeToElement(new
        { requirePassingRegression = true, errorRatePercent = 20, minimumRequests = 10, windowSeconds = 300 }));
        foreach (var revocation in new[] { "role", "organization", "principal" })
        {
            await data.Case.ExecuteAsync("UPDATE braid.principals SET active=true WHERE id=$1", data.Seed.Alice);
            await data.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2", scope.OrganizationId, data.Seed.Alice);
            await data.RoleAsync("publisher");
            var id = Guid.NewGuid();
            Assert.True(await store.WriteAsync(data.Actor, scope, "recheck", id,
                JsonSerializer.SerializeToElement(new { mappingId = data.Mapping.MappingId, revisionId = value.Revision.RevisionId })));
            var claim = Assert.IsType<AutomationClaim>(await store.ClaimEvaluationAsync(default));
            // This is the result handed back by the executor's final authorized boundary.
            var result = RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, value.Expected,
                new RegressionComparisonService().Compare(value.Expected, new(Braid.Contracts.Operations.OperationStatusV1.Succeeded, value.Expected), RegressionComparisonService.ExactBytesPolicy));
            if (revocation == "role") await data.RoleAsync("viewer");
            else if (revocation == "organization") await data.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", scope.OrganizationId, data.Seed.Alice);
            else await data.Case.ExecuteAsync("UPDATE braid.principals SET active=false WHERE id=$1", data.Seed.Alice);
            Assert.False(await store.FinishEvaluationAsync(claim, result, default));
            Assert.Equal("revoked", await data.Case.ScalarAsync<string>("SELECT state FROM braid.automation_evaluations WHERE suite_id=$1", id));
            Assert.True(await data.Case.ScalarAsync<bool>("SELECT result_body IS NULL AND token IS NULL AND lease_expires IS NULL FROM braid.automation_evaluations WHERE suite_id=$1", id));
            Assert.Equal("failed", await data.Case.ScalarAsync<string>("SELECT state FROM braid.automation_suites WHERE suite_id=$1", id));
            Assert.False(await data.Case.ScalarAsync<bool>("SELECT braid.automation_gate($1,$2,$3,$4)", scope.OrganizationId, scope.ProjectId, data.Mapping.MappingId, value.Revision.RevisionId));
            Assert.False(await store.FinishEvaluationAsync(claim, result, default));
        }
    }

    [PostgresFact]
    public async Task Global_cursor_keeps_its_position_after_anchor_project_revocation_without_exposing_it()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.Seed.AddMembershipAsync(data.Case, data.Seed.OrganizationA, data.Seed.HiddenProjectA, data.Seed.Alice, "publisher");
        await using var store = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        foreach (var project in new[] { data.Seed.ProjectA, data.Seed.HiddenProjectA })
            Assert.True(await store.WriteAsync(data.Actor, new(data.Seed.OrganizationA, project), "subscription", null,
                JsonSerializer.SerializeToElement(new { events = new[] { "revisionUploaded" }, inApp = true, email = false, webhooks = Array.Empty<string>() })));
        var older = Guid.NewGuid(); var hidden = Guid.NewGuid();
        await data.Case.ExecuteAsync("SELECT braid.notification_emit($1,$2,'revisionUploaded',NULL,NULL,$3,NULL)", data.Seed.OrganizationA, data.Seed.ProjectA, older);
        await data.Case.ExecuteAsync("SELECT braid.notification_emit($1,$2,'revisionUploaded',NULL,NULL,$3,NULL)", data.Seed.OrganizationA, data.Seed.HiddenProjectA, hidden);
        var first = (await store.ReadInboxAsync(data.Actor, data.Seed.OrganizationA, null, 1))!.Value;
        Assert.Equal(hidden, Assert.Single(first.GetProperty("notifications").EnumerateArray()).GetProperty("resourceId").GetGuid());
        var cursor = first.GetProperty("nextId").GetGuid();
        await data.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", data.Seed.OrganizationA, data.Seed.HiddenProjectA, data.Seed.Alice);
        var next = (await store.ReadInboxAsync(data.Actor, data.Seed.OrganizationA, cursor, 10))!.Value;
        Assert.Equal(older, Assert.Single(next.GetProperty("notifications").EnumerateArray()).GetProperty("resourceId").GetGuid());
        Assert.DoesNotContain(hidden.ToString(), next.GetRawText());
        Assert.DoesNotContain(data.Seed.HiddenProjectA.ToString(), next.GetRawText());
        var foreign = (await store.ReadInboxAsync(data.Actor, data.Seed.OrganizationA, Guid.NewGuid(), 10))!.Value;
        Assert.Empty(foreign.GetProperty("notifications").EnumerateArray());
    }

    [PostgresFact]
    public async Task Recheck_reauthorizes_after_waiting_for_the_actor_quota_lock_and_preserves_the_previous_passing_gate()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.RoleAsync("administrator");
        var value = data.Version(1); await data.Writer.SaveAsync(data.Actor, value);
        var org = data.Seed.OrganizationA; var project = data.Seed.ProjectA;
        await using var store = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        var scope = new ProjectScope(org, project);
        await store.WriteAsync(data.Actor, scope, "policy", null, JsonSerializer.SerializeToElement(new
        { requirePassingRegression = true, errorRatePercent = 20, minimumRequests = 10, windowSeconds = 300 }));
        var body = JsonSerializer.SerializeToElement(new { mappingId = data.Mapping.MappingId, revisionId = value.Revision.RevisionId });
        Assert.True(await store.WriteAsync(data.Actor, scope, "recheck", Guid.NewGuid(), body));
        var first = (await store.ClaimEvaluationAsync(default))!;
        var success = RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, value.Expected,
            new RegressionComparisonService().Compare(value.Expected, new(Braid.Contracts.Operations.OperationStatusV1.Succeeded, value.Expected), RegressionComparisonService.ExactBytesPolicy));
        Assert.True(await store.FinishEvaluationAsync(first, success, default));

        await using var owner = new NpgsqlConnection(Unpooled(data.Case.MigrationConnectionString));
        await owner.OpenAsync();
        await using var held = await owner.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,337))", owner, held))
        { hold.Parameters.AddWithValue(org.ToString() + data.Seed.Alice.ToString()); await hold.ExecuteNonQueryAsync(); }
        await using var writer = new NpgsqlConnection(Unpooled(data.Case.RegressionConnectionString("braid_run_writer")));
        await writer.OpenAsync(); await using var admission = await writer.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SELECT set_config('braid.organization_id',$1,true),set_config('braid.run_principal_id',$2,true)", writer, admission))
        { settings.Parameters.AddWithValue(org.ToString()); settings.Parameters.AddWithValue(data.Seed.Alice.ToString()); await settings.ExecuteNonQueryAsync(); }
        var id = Guid.NewGuid();
        await using var request = new NpgsqlCommand("SELECT braid.automation_write($1,$2,'recheck',$3,$4::jsonb)", writer, admission);
        request.Parameters.AddWithValue(org); request.Parameters.AddWithValue(project); request.Parameters.AddWithValue(id); request.Parameters.AddWithValue(body.GetRawText());
        await WithContendedTaskAsync(held, token => request.ExecuteScalarAsync(token), async (pending, commitBlocker) =>
        {
            var waiting = false;
            for (var attempt = 0; attempt < 200 && !waiting; attempt++)
            {
                waiting = await data.Case.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_locks WHERE pid=$1 AND locktype='advisory' AND NOT granted)", writer.ProcessID);
                if (!waiting) await Task.Delay(25);
            }
            Assert.True(waiting, "The request must reach the held actor lock before revocation.");
            await data.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", org, project, data.Seed.Alice);
            await commitBlocker();
            Assert.Equal(false, await pending);
            await admission.CommitAsync();
            Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.automation_suites WHERE suite_id=$1", id));
            Assert.True(await data.Case.ScalarAsync<bool>("SELECT braid.automation_gate($1,$2,$3,$4)", org, project, data.Mapping.MappingId, value.Revision.RevisionId));
        });
    }

    [PostgresFact]
    public async Task Parallel_rechecks_in_different_projects_share_the_actor_pending_quota()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        var value = data.Version(1); await data.Writer.SaveAsync(data.Actor, value);
        var org = data.Seed.OrganizationA;
        var other = new ProjectScope(org, data.Seed.HiddenProjectA);
        await data.Seed.AddMembershipAsync(data.Case, org, other.ProjectId, data.Seed.Alice, "publisher");
        await using var ingestion = new PostgresIngestionDatabase(data.Case.IngestionConnectionString);
        var ingestions = new PostgresRevisionIngestionStore(ingestion);
        var reserved = await ingestions.ReserveAsync(data.Actor, other, Guid.NewGuid(), PostgresIngestionTestSupport.CreateIntent(),
            PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, reserved.Outcome);
        var facts = reserved.Receipt!.Facts;
        var claim = (await ingestions.ClaimAsync(data.Actor, other, facts.OperationId, Guid.NewGuid())).Claim!;
        Assert.Equal(IngestionStoreOutcome.Applied, (await ingestions.MarkStagedAsync(data.Actor, claim, facts.NativeObject)).Outcome);
        Assert.Equal(IngestionPhase.Ready, (await ingestions.CompleteAttemptAsync(data.Actor, claim, PostgresIngestionTestSupport.SyntheticCompletion(facts))).Receipt!.Phase);
        var receipt = (await ingestions.FinalizeAsync(data.Actor, claim)).Receipt!;
        Assert.Equal(IngestionPhase.Committed, receipt.Phase);
        var second = new RegressionCaseVersion(Guid.NewGuid(), 1, value.Owner, "Other project", receipt.Facts.FutureRevision,
            receipt.Facts.NativeObject.Digest, value.Runtime, value.Replay, value.ComparisonPolicy, value.Expected);
        await data.Writer.SaveAsync(data.Actor, second);
        await using var store = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        var scope = new ProjectScope(org, data.Mapping.ProjectId);
        var body = JsonSerializer.SerializeToElement(new { mappingId = data.Mapping.MappingId, revisionId = value.Revision.RevisionId });
        for (var i = 0; i < 31; i++) Assert.True(await store.WriteAsync(data.Actor, scope, "recheck", Guid.NewGuid(), body));
        await using var connection = new NpgsqlConnection(Unpooled(data.Case.RegressionConnectionString("braid_run_writer")));
        await connection.OpenAsync(); await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SELECT set_config('braid.organization_id',$1,true),set_config('braid.run_principal_id',$2,true)", connection, transaction))
        { settings.Parameters.AddWithValue(org.ToString()); settings.Parameters.AddWithValue(data.Seed.Alice.ToString()); await settings.ExecuteNonQueryAsync(); }
        await using (var command = new NpgsqlCommand("SELECT braid.automation_write($1,$2,'recheck',$3,$4::jsonb)", connection, transaction))
        {
            command.Parameters.AddWithValue(org); command.Parameters.AddWithValue(scope.ProjectId);
            command.Parameters.AddWithValue(Guid.NewGuid()); command.Parameters.AddWithValue(body.GetRawText());
            Assert.Equal(true, await command.ExecuteScalarAsync());
        }
        await WithContendedTaskAsync(transaction, token => store.WriteAsync(data.Actor, other, "recheck", Guid.NewGuid(), JsonSerializer.SerializeToElement(new
        { mappingId = second.Revision.MappingId, revisionId = second.Revision.RevisionId }), token), async (competing, commitBlocker) =>
        {
            var beforeCommit = await Task.WhenAny(competing, Task.Delay(TimeSpan.FromMilliseconds(750)));
            Assert.NotSame(competing, beforeCommit);
            await commitBlocker();
            Assert.False(await competing);
            Assert.Equal(32L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.automation_suites WHERE actor_id=$1 AND state='queued'", data.Seed.Alice));
        });
    }

    private static async Task WithContendedTaskAsync<T>(NpgsqlTransaction held, Func<CancellationToken, Task<T>> start,
        Func<Task<T>, Func<Task>, Task> assertions)
    {
        using var cancellation = new CancellationTokenSource();
        var pending = start(cancellation.Token);
        Exception? primary = null;
        var cleanup = new List<Exception>();
        var blockerCompleted = false;
        async Task CommitBlockerAsync()
        {
            await held.CommitAsync();
            blockerCompleted = true;
        }
        try { await assertions(pending, CommitBlockerAsync); }
        catch (Exception error) { primary = error; }
        finally
        {
            // Release the blocker before joining/canceling its waiter, while all of the waiter's
            // command, connection and transaction are still alive in the calling test.
            try
            {
                if (!blockerCompleted)
                {
                    await held.RollbackAsync();
                    blockerCompleted = true;
                }
            }
            catch (Exception error) { cleanup.Add(error); }
            try { if (!pending.IsCompleted) await cancellation.CancelAsync(); }
            catch (Exception error) { cleanup.Add(error); }
            try { await pending; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { if (!ReferenceEquals(error, primary)) cleanup.Add(error); }
        }
        if (primary is not null && cleanup.Count > 0)
            throw new AggregateException("Quota assertion and owned task cleanup failed.", new[] { primary }.Concat(cleanup));
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Owned quota task cleanup failed.", cleanup);
    }

    // These intentionally held connections must not retain fixture databases in the global pool,
    // or occupy the case owner's single pooled connection while the observer needs it.
    private static string Unpooled(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
}
