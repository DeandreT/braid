using System.Text;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Configuration admission and the pinned trigger path through the shared invocation core, without a database.</summary>
public sealed class TriggerRuntimeTests
{
    [Fact]
    public void Configuration_has_one_canonical_form_and_refuses_anything_else()
    {
        var configuration = Configuration(Target());
        var bytes = configuration.CopyCanonicalBytes();
        var restored = TriggerConfiguration.Parse(bytes);
        Assert.Equal(bytes, restored.CopyCanonicalBytes());
        Assert.Equal(configuration.Digest, restored.Digest);
        Assert.Equal(configuration.Policies!.Retry.RetryDelays, restored.Policies!.Retry.RetryDelays);
        var spaced = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(",\"kind\"", ", \"kind\"", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => TriggerConfiguration.Parse(spaced));
        Assert.Throws<InvalidOperationException>(() => TriggerConfiguration.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
            .Replace("\"envelope\":\"singleOrder\"", "\"envelope\":\"split\"", StringComparison.Ordinal))));
        // Missing policies are representable for drafting but never complete.
        Assert.False(Configuration(Target(), draft: true).Complete);
        Assert.Null(TriggerConfiguration.Parse(Configuration(Target(), draft: true).CopyCanonicalBytes()).Policies);
    }

    [Fact]
    public void Configuration_refuses_unsupported_media_missing_or_duplicate_routes_and_inferred_bounds()
    {
        var target = Target();
        Assert.Throws<ArgumentException>(() => Configuration(target, media: "text/csv"));
        Assert.Throws<ArgumentException>(() => Configuration(target, media: "application/octet-stream"));
        Assert.Throws<ArgumentException>(() => Configuration(target, routes: []));
        var route = new TriggerRoute(null, Guid.NewGuid(), TriggerDestinationKind.Blob, new("blob-out", 1));
        Assert.Throws<ArgumentException>(() => Configuration(target, routes: [route, route]));
        Assert.Throws<ArgumentException>(() => new TriggerConnectorReference("Has Spaces", 1));
        Assert.Throws<ArgumentException>(() => new TriggerConnectorReference("ok", 0));
        var retry = new DeliveryRetryPolicy(2, [TimeSpan.FromSeconds(1)], TimeSpan.FromSeconds(10), TimeSpan.FromHours(1));
        // Payload retention shorter than the delivery lifetime would expire bytes a queued delivery still needs.
        Assert.Throws<ArgumentException>(() => new TriggerPolicies(retry, TimeSpan.FromSeconds(30), 1024, TimeSpan.FromMinutes(30), 10, 1024));
        Assert.Throws<ArgumentException>(() => new TriggerPolicies(retry, TimeSpan.FromSeconds(91), 1024, TimeSpan.FromHours(2), 10, 1024));
        Assert.Throws<ArgumentException>(() => new TriggerPolicies(retry, TimeSpan.FromSeconds(30), 1024, TimeSpan.FromHours(2), 10, 1023));
        Assert.Throws<ArgumentException>(() => new TriggerOperation(Guid.NewGuid(), Guid.NewGuid(), TriggerAction.Enable, 0, null));
        Assert.Throws<ArgumentException>(() => new TriggerOperation(Guid.NewGuid(), Guid.NewGuid(), TriggerAction.Configure, 0, null));
    }

    [Fact]
    public void Routes_must_cover_exactly_the_produced_outputs()
    {
        var target = Target();
        var configuration = Configuration(target, routes: [new(null, Guid.NewGuid(), TriggerDestinationKind.Blob, new("blob-out", 1)),
            new(null, Guid.NewGuid(), TriggerDestinationKind.Https, new("hook", 2)), new("audit", Guid.NewGuid(), TriggerDestinationKind.Sftp, new("sftp", 1))]);
        Assert.True(configuration.RoutesCover([null, "audit"]));
        Assert.False(configuration.RoutesCover([null]));
        Assert.False(configuration.RoutesCover([null, "audit", "extra"]));
        Assert.False(configuration.RoutesCover([null, null, "audit"]));
    }

    [Fact]
    public async Task A_claimed_order_runs_once_under_its_pin_as_the_stored_publisher_and_enqueues_its_outputs()
    {
        var test = new MachineInvocationServiceTests.Fixture(format: "json"); await test.Activate();
        var ledger = new Ledger(test);
        var result = await test.Service.InvokeTriggeredAsync(ledger, test.Target.Reference, ledger.WorkId, ledger.Caller, default);
        Assert.Equal(TriggeredInvocationStatus.Completed, result.Status);
        Assert.Equal(1, test.RuntimeCalls);
        var finish = Assert.Single(ledger.Finishes);
        Assert.Equal(MachineInvocationOutcome.Completed, finish.Outcome);
        Assert.Equal(ledger.Pin.Provenance, finish.Provenance);
        Assert.Equal(ledger.Caller.ApplicationId, finish.Application);
        var output = Assert.Single(finish.Outputs!.Outputs);
        Assert.True(output.Key.IsPrimary);
        Assert.Contains(ledger.RequestId.ToString("D"), Encoding.UTF8.GetString(finish.Response!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_moved_pointer_is_never_run_under_the_new_release()
    {
        var test = new MachineInvocationServiceTests.Fixture(format: "json"); await test.Activate();
        var ledger = new Ledger(test);
        await test.Activate();
        var result = await test.Service.InvokeTriggeredAsync(ledger, test.Target.Reference, ledger.WorkId, ledger.Caller, default);
        Assert.Equal(TriggeredInvocationStatus.Retry, result.Status);
        Assert.Equal(0, test.RuntimeCalls);
        var finish = Assert.Single(ledger.Finishes);
        Assert.Equal(MachineInvocationOutcome.ReleaseChanged, finish.Outcome);
        Assert.Null(finish.Outputs);
    }

    [Fact]
    public async Task Unrouted_outputs_or_a_changed_stored_request_record_an_integrity_failure_and_enqueue_nothing()
    {
        var test = new MachineInvocationServiceTests.Fixture(format: "json"); await test.Activate();
        var unrouted = new Ledger(test, [new("named", Guid.NewGuid(), TriggerDestinationKind.Blob, new("blob-out", 1))]);
        Assert.Equal(TriggeredInvocationStatus.Failed, (await test.Service.InvokeTriggeredAsync(unrouted, test.Target.Reference, unrouted.WorkId, unrouted.Caller, default)).Status);
        Assert.Equal(MachineInvocationOutcome.IntegrityFailure, Assert.Single(unrouted.Finishes).Outcome);
        Assert.Null(unrouted.Finishes[0].Outputs); Assert.Null(unrouted.Finishes[0].Response);
        var calls = test.RuntimeCalls;
        var changed = new Ledger(test) { RequestDigest = Sha256Digest.FromBytes("other"u8) };
        Assert.Equal(TriggeredInvocationStatus.Failed, (await test.Service.InvokeTriggeredAsync(changed, test.Target.Reference, changed.WorkId, changed.Caller, default)).Status);
        Assert.Equal(MachineInvocationOutcome.IntegrityFailure, Assert.Single(changed.Finishes).Outcome);
        Assert.Equal(calls, test.RuntimeCalls);
    }

    [Theory]
    [InlineData(TriggerClaimStatus.InProgress, TriggeredInvocationStatus.InProgress)]
    [InlineData(TriggerClaimStatus.Refused, TriggeredInvocationStatus.Refused)]
    [InlineData(TriggerClaimStatus.Fenced, TriggeredInvocationStatus.Fenced)]
    [InlineData(TriggerClaimStatus.ReleaseSuperseded, TriggeredInvocationStatus.ReleaseSuperseded)]
    public async Task Unclaimed_work_runs_and_records_nothing(TriggerClaimStatus status, TriggeredInvocationStatus expected)
    {
        var test = new MachineInvocationServiceTests.Fixture(format: "json"); await test.Activate();
        var ledger = new Ledger(test) { Forced = new(status) };
        Assert.Equal(expected, (await test.Service.InvokeTriggeredAsync(ledger, test.Target.Reference, ledger.WorkId, ledger.Caller, default)).Status);
        Assert.Equal(0, test.RuntimeCalls); Assert.Empty(ledger.Finishes);
        // A recorded completion is recovered from durable outputs, never rerun.
        ledger.Forced = new(TriggerClaimStatus.Recorded, outcome: MachineInvocationOutcome.Completed);
        Assert.Equal(TriggeredInvocationStatus.Completed, (await test.Service.InvokeTriggeredAsync(ledger, test.Target.Reference, ledger.WorkId, ledger.Caller, default)).Status);
        Assert.Equal(0, test.RuntimeCalls);
    }

    [Theory]
    [InlineData(TriggerFinishStatus.Fenced, TriggeredInvocationStatus.Fenced)]
    [InlineData(TriggerFinishStatus.ReleaseSuperseded, TriggeredInvocationStatus.ReleaseSuperseded)]
    [InlineData(TriggerFinishStatus.Superseded, TriggeredInvocationStatus.Retry)]
    public async Task A_fence_or_lost_lease_at_finish_reports_no_completion(TriggerFinishStatus finish, TriggeredInvocationStatus expected)
    {
        var test = new MachineInvocationServiceTests.Fixture(format: "json"); await test.Activate();
        var ledger = new Ledger(test) { ForcedFinish = finish };
        Assert.Equal(expected, (await test.Service.InvokeTriggeredAsync(ledger, test.Target.Reference, ledger.WorkId, ledger.Caller, default)).Status);
        Assert.Equal(1, test.RuntimeCalls);
    }

    internal static ReleaseTargetReference Target()
    {
        var org = Guid.NewGuid(); var project = Guid.NewGuid();
        return new(new(org, project, Guid.NewGuid()), new(new(org, project), Guid.NewGuid(), 1), Guid.NewGuid());
    }

    internal static readonly TriggerPolicies DefaultTestPolicies = new(new DeliveryRetryPolicy(2, [TimeSpan.FromMilliseconds(1)],
        TimeSpan.FromSeconds(30), TimeSpan.FromHours(1)), TimeSpan.FromSeconds(60), 64 * 1024, TimeSpan.FromHours(2), 100, 1024 * 1024);

    internal static TriggerConfiguration Configuration(ReleaseTargetReference target, MachinePrincipal? caller = null, Guid? application = null,
        IReadOnlyList<TriggerRoute>? routes = null, string media = "application/json", Guid? triggerId = null, Guid? binding = null, string source = "orders-in",
        TriggerPolicies? policies = null, bool draft = false) =>
        new(triggerId ?? Guid.NewGuid(), target, TriggerKind.ServiceBusQueue, binding ?? Guid.NewGuid(), new(source, 1),
            caller ?? new(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid()), application ?? Guid.NewGuid(), media,
            [new("region", PreviewParameterKind.String, "west")],
            routes ?? [new(null, Guid.NewGuid(), TriggerDestinationKind.Blob, new("blob-out", 1))],
            draft ? null : policies ?? DefaultTestPolicies);

    internal sealed record Finished(MachineInvocationOutcome Outcome, MachineInvocationProvenance? Provenance, Guid Application, byte[]? Response,
        RegressionOutputSet? Outputs);

    /// <summary>A trusted claim built from the fixture's active release, as durable admission would have stored it.</summary>
    private sealed class Ledger : ITriggeredInvocationLedger
    {
        private readonly TriggerConfiguration configuration;
        private static readonly byte[] Input = "{}"u8.ToArray();

        internal Ledger(MachineInvocationServiceTests.Fixture test, IReadOnlyList<TriggerRoute>? routes = null)
        {
            Caller = new(new(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
            configuration = Configuration(test.Target.Reference, Caller.Principal, Caller.ApplicationId, routes);
            Pin = new(MachineInvocationProvenance.From(test.Release!), test.Release!.Actor, 1, Guid.NewGuid());
            RequestDigest = new MachineInvocationRequest(new(null, Input), [], configuration.Parameters, [new(null, configuration.InputMediaType)]).Digest;
        }

        internal Guid WorkId { get; } = Guid.NewGuid();
        internal Guid RequestId { get; } = Guid.NewGuid();
        internal MachineCaller Caller { get; }
        internal TriggerWorkPin Pin { get; }
        internal Sha256Digest RequestDigest { get; set; }
        internal TriggerWorkClaim? Forced { get; set; }
        internal TriggerFinishStatus? ForcedFinish { get; init; }
        internal List<Finished> Finishes { get; } = [];

        public Task<TriggerWorkClaim> ClaimPinnedAsync(ReleaseTargetReference target, Guid workId, MachineCaller caller, CancellationToken token) =>
            Task.FromResult(Forced ?? new TriggerWorkClaim(TriggerClaimStatus.Claimed, 1, Guid.NewGuid(), Pin, RequestId, RequestDigest, configuration, Input));

        public Task<TriggerFinishStatus> FinishAndEnqueueAsync(ReleaseTargetReference target, Guid workId, Guid claimId, Guid applicationId,
            MachineInvocationOutcome outcome, MachineInvocationProvenance? provenance, string? clock, byte[]? response, RegressionOutputSet? outputs,
            CancellationToken token)
        {
            Assert.Equal(WorkId, workId);
            Finishes.Add(new(outcome, provenance, applicationId, response, outputs));
            return Task.FromResult(ForcedFinish ?? TriggerFinishStatus.Recorded);
        }
    }
}
