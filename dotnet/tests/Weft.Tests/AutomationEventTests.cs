using System.Text.Json;
using Weft.Application;
using Weft.Deployment;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AutomationEventTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Release_activation_rollback_and_qualified_builds_emit_from_the_actual_transactions()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await using var store = new PostgresAutomationStore(data.Case.RegressionConnectionString("weft_run_writer"), true);
        var scope = data.Target.Reference.Mapping;
        await Subscribe(store, data.Actor, new(scope.OrganizationId, scope.ProjectId), ["buildQualified", "releaseActivated", "releaseRolledBack"]);
        var first = await data.Request(data.First, null); Assert.Equal(ReleaseTransitionOutcome.Activated, await data.Apply(first));
        var proof = await data.Build([5, 6]);
        var second = await data.Request(proof, first.ReleaseId); Assert.Equal(ReleaseTransitionOutcome.Activated, await data.Apply(second));
        var rollback = await data.Request(data.First, second.ReleaseId, ReleaseAction.Rollback, first.ReleaseId);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await data.Apply(rollback));
        var inbox = await store.ReadInboxAsync(data.Actor, scope.OrganizationId, null, 20);
        var events = inbox!.Value.GetProperty("notifications").EnumerateArray().Select(x => x.GetProperty("event").GetString()).ToArray();
        Assert.Equal(2, events.Count(x => x == "releaseActivated")); Assert.Single(events, x => x == "releaseRolledBack"); Assert.Single(events, x => x == "buildQualified");
    }

    [PostgresFact]
    public async Task Failed_build_emits_once_without_exposing_the_compiler_result()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using var store = new PostgresAutomationStore(data.Runs.Cases.Case.RegressionConnectionString("weft_run_writer"), true);
        await Subscribe(store, data.Actor, data.Intent.Environment.Project, ["buildFailed"]);
        await data.Approve();
        await using var runs = new PostgresBuildRunStore(data.Runs.Cases.Case.RegressionConnectionString("weft_build_run_writer"), BuildRunDatabaseProfile.Writer);
        await using var dispatch = new PostgresBuildRunStore(data.Runs.Cases.Case.RegressionConnectionString("weft_build_dispatcher"), BuildRunDatabaseProfile.Dispatcher);
        await runs.SubmitAsync(data.Actor, data.Intent);
        var claim = (await dispatch.ClaimAsync(data.Intent.Revision.OrganizationId, data.Intent.BuildId, Guid.NewGuid()))!;
        Assert.True(await dispatch.CompleteAsync(claim, new(BuildExecutionOutcome.Unsupported)));
        Assert.False(await dispatch.CompleteAsync(claim, new(BuildExecutionOutcome.Unsupported)));
        var inbox = await store.ReadInboxAsync(data.Actor, data.Intent.Revision.OrganizationId, null, 20);
        Assert.Equal("buildFailed", Assert.Single(inbox!.Value.GetProperty("notifications").EnumerateArray()).GetProperty("event").GetString());
    }

    [PostgresFact]
    public async Task Endpoint_threshold_is_per_target_once_per_window_and_keeps_the_endpoint_identifier()
    {
        await using var data = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture);
        await using var store = new PostgresAutomationStore(data.Case.RegressionConnectionString("weft_run_writer"), true);
        var scope = data.Target.Mapping; var project = new ProjectScope(scope.OrganizationId, scope.ProjectId);
        await Subscribe(store, data.Actor, project, ["endpointErrorRate"]);
        await store.WriteAsync(data.Actor, project, "policy", null, JsonSerializer.SerializeToElement(new
        { requirePassingRegression = false, errorRatePercent = 50, minimumRequests = 2, windowSeconds = 60 }));
        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid(); var claim = await data.Claim(id);
            Assert.Equal(MachineInvocationFinishStatus.Recorded, await data.Finish(id, claim, i == 0 ? MachineInvocationOutcome.Completed : MachineInvocationOutcome.InvalidInput, i == 0 ? [1] : null));
        }
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.notification_events WHERE kind='endpointErrorRate'"));
        // At high volume a clipped denominator must not manufacture a threshold crossing.
        await data.Case.ExecuteAsync("UPDATE weft.notification_endpoint_windows SET total=1000000,errors=499999,crossed=false");
        var highVolumeRequest = Guid.NewGuid(); var highVolumeClaim = await data.Claim(highVolumeRequest);
        await data.Finish(highVolumeRequest, highVolumeClaim, MachineInvocationOutcome.InvalidInput, null);
        Assert.Equal(1000001L, await data.Case.ScalarAsync<long>("SELECT total FROM weft.notification_endpoint_windows"));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.notification_events WHERE kind='endpointErrorRate'"));
        await data.Case.ExecuteAsync("UPDATE weft.notification_endpoint_windows SET window_start=clock_timestamp()-interval '61 seconds'");
        for (var i = 0; i < 2; i++) { var id = Guid.NewGuid(); var claim = await data.Claim(id); await data.Finish(id, claim, MachineInvocationOutcome.InvalidInput, null); }
        var inbox = await store.ReadInboxAsync(data.Actor, scope.OrganizationId, null, 20);
        var events = inbox!.Value.GetProperty("notifications").EnumerateArray().ToArray(); Assert.Equal(2, events.Length);
        Assert.All(events, e => Assert.Equal(data.Target.TargetId, e.GetProperty("resourceId").GetGuid()));
    }

    private static async Task Subscribe(PostgresAutomationStore store, ActorContext actor, ProjectScope project, string[] events) =>
        Assert.True(await store.WriteAsync(actor, project, "subscription", null, JsonSerializer.SerializeToElement(new { events, inApp = true, email = false, webhooks = Array.Empty<string>() })));
}
