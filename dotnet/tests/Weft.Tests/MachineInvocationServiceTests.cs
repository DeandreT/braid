using System.Text.Json;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Tests;

public sealed class MachineInvocationServiceTests
{
    [Fact]
    public async Task A_completed_call_records_exact_provenance_and_a_retry_replays_the_same_bytes_without_running()
    {
        var test = new Fixture(); await test.Activate();
        var first = await test.InvokeAsync();
        Assert.Equal(MachineInvocationStatus.Completed, first.Status); Assert.False(first.Replayed);
        Assert.Equal(1, test.RuntimeCalls);
        var finish = Assert.Single(test.Finishes);
        Assert.Equal(MachineInvocationOutcome.Completed, finish.Outcome);
        Assert.Equal(MachineInvocationProvenance.From(test.Release!), finish.Provenance);
        using (var document = JsonDocument.Parse(first.Response!))
        {
            var root = document.RootElement;
            Assert.Equal(test.Release!.ReleaseId.ToString("D"), root.GetProperty("releaseId").GetString());
            Assert.Equal(test.Data.Build.Intent.BuildId.ToString("D"), root.GetProperty("buildId").GetString());
            Assert.Equal(test.Data.Build.Intent.Revision.RevisionId.ToString("D"), root.GetProperty("revisionId").GetString());
            Assert.Equal(finish.Clock, root.GetProperty("currentDateTime").GetString());
            var output = Assert.Single(root.GetProperty("outputs").EnumerateArray().ToArray());
            Assert.Equal(JsonValueKind.Null, output.GetProperty("name").ValueKind);
            Assert.Equal("{\"ok\":true}\n"u8.ToArray(), output.GetProperty("content").GetBytesFromBase64());
        }
        var replay = await test.InvokeAsync();
        Assert.Equal(MachineInvocationStatus.Completed, replay.Status); Assert.True(replay.Replayed);
        Assert.Equal(first.Response, replay.Response);
        Assert.Equal(1, test.RuntimeCalls); Assert.Single(test.Finishes);
    }

    [Fact]
    public async Task A_rollback_during_the_call_releases_the_attempt_instead_of_delivering_stale_output()
    {
        var test = new Fixture { ChangePointerAt = "runtime" }; await test.Activate();
        var result = await test.InvokeAsync();
        Assert.Equal(MachineInvocationStatus.Unavailable, result.Status); Assert.Null(result.Response);
        Assert.Equal(MachineInvocationOutcome.ReleaseChanged, Assert.Single(test.Finishes).Outcome);
    }

    [Fact]
    public async Task A_disabled_target_or_lost_publisher_authority_is_a_recorded_refusal_without_running()
    {
        var test = new Fixture(); await test.Activate();
        test.Enabled = false;
        var result = await test.InvokeAsync();
        Assert.Equal(MachineInvocationStatus.Failed, result.Status); Assert.Equal(MachineInvocationOutcome.Refused, result.Outcome);
        Assert.Equal(0, test.RuntimeCalls);
        test.Enabled = true; test.Unauthorized = true;
        Assert.Equal(MachineInvocationOutcome.Refused, (await test.InvokeAsync(Guid.NewGuid())).Outcome);
        Assert.Equal(0, test.RuntimeCalls);
    }

    [Theory]
    [InlineData(MachineInvocationClaimStatus.Conflict, MachineInvocationStatus.Conflict)]
    [InlineData(MachineInvocationClaimStatus.InProgress, MachineInvocationStatus.InProgress)]
    [InlineData(MachineInvocationClaimStatus.Refused, MachineInvocationStatus.Refused)]
    [InlineData(MachineInvocationClaimStatus.NoRelease, MachineInvocationStatus.NoRelease)]
    public async Task Unclaimed_requests_run_and_record_nothing(MachineInvocationClaimStatus claim, MachineInvocationStatus expected)
    {
        var test = new Fixture { ForcedClaim = claim }; await test.Activate();
        Assert.Equal(expected, (await test.InvokeAsync()).Status);
        Assert.Equal(0, test.RuntimeCalls); Assert.Empty(test.Finishes);
    }

    [Theory]
    [InlineData(MachineInvocationFinishStatus.Revoked, MachineInvocationStatus.Refused)]
    [InlineData(MachineInvocationFinishStatus.Superseded, MachineInvocationStatus.Unavailable)]
    public async Task A_revoked_or_expired_attempt_reported_at_finish_delivers_nothing(MachineInvocationFinishStatus finish, MachineInvocationStatus expected)
    {
        var test = new Fixture { ForcedFinish = finish }; await test.Activate();
        var result = await test.InvokeAsync();
        Assert.Equal(1, test.RuntimeCalls);
        Assert.Equal(expected, result.Status); Assert.Null(result.Response); Assert.Null(result.Outcome);
    }

    [Fact]
    public async Task Cancellation_releases_the_attempt_for_a_retry_under_the_same_ID()
    {
        var test = new Fixture { CancelAtRuntime = true }; await test.Activate();
        using var cancellation = new CancellationTokenSource();
        test.Cancellation = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.InvokeAsync(token: cancellation.Token));
        Assert.Equal(MachineInvocationOutcome.Unavailable, Assert.Single(test.Finishes).Outcome);
        Assert.Null(test.Finishes[0].Provenance);
    }

    [Fact]
    public async Task A_stalled_release_holds_a_canceled_call_only_for_the_hosts_release_bound()
    {
        var test = new Fixture(TimeSpan.FromMilliseconds(300)) { CancelAtRuntime = true, StallRelease = true }; await test.Activate();
        using var cancellation = new CancellationTokenSource();
        test.Cancellation = cancellation;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.InvokeAsync(token: cancellation.Token));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"The release held the call for {timer.Elapsed}.");
        Assert.Equal(MachineInvocationOutcome.Unavailable, Assert.Single(test.Finishes).Outcome);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fixture(TimeSpan.Zero));
    }

    [Fact]
    public async Task Inputs_the_replay_contract_refuses_are_recorded_as_invalid_without_running()
    {
        var test = new Fixture(); await test.Activate();
        var result = await test.InvokeAsync(request: new(new(null, "{}"u8), [new("bad\u0001name", "{}"u8)], []));
        Assert.Equal(MachineInvocationStatus.Failed, result.Status); Assert.Equal(MachineInvocationOutcome.InvalidInput, result.Outcome);
        Assert.Equal(0, test.RuntimeCalls);
        Assert.NotNull(test.Finishes[0].Provenance);
    }

    [Fact]
    public void Request_digest_binds_inputs_and_parameters_but_not_their_order()
    {
        MachineInvocationRequest Request(string primary, params (string Name, string Value)[] parameters) =>
            new(new(null, System.Text.Encoding.UTF8.GetBytes(primary)), [new("b", "2"u8), new("a", "1"u8)],
                parameters.Select(value => new PreviewParameter(value.Name, PreviewParameterKind.String, value.Value)).ToArray());
        var original = Request("{}", ("x", "1"), ("y", "2"));
        Assert.Equal(original.Digest, Request("{}", ("y", "2"), ("x", "1")).Digest);
        Assert.Equal(original.Digest, new MachineInvocationRequest(new(null, "{}"u8), [new("a", "1"u8), new("b", "2"u8)],
            [new("y", PreviewParameterKind.String, "2"), new("x", PreviewParameterKind.String, "1")]).Digest);
        Assert.NotEqual(original.Digest, Request("{ }", ("x", "1"), ("y", "2")).Digest);
        Assert.NotEqual(original.Digest, Request("{}", ("x", "1"), ("y", "3")).Digest);
        Assert.NotEqual(original.Digest, new MachineInvocationRequest(new(null, "{}"u8), [new("a", "1"u8), new("b", "2"u8)],
            [new("x", PreviewParameterKind.Integer, "1"), new("y", PreviewParameterKind.String, "2")]).Digest);
        Assert.Throws<ArgumentException>(() => new MachineInvocationRequest(new(null, "{}"u8), [new("a", "1"u8), new("a", "2"u8)], []));
        Assert.Throws<ArgumentException>(() => new MachineInvocationRequest(new("named", "{}"u8), [], []));
    }

    internal sealed record Finish(MachineInvocationOutcome Outcome, MachineInvocationProvenance? Provenance, string? Clock, byte[]? Response);

    internal sealed class Fixture : ILocalReleaseStore, IBuildRunReader, IBuildAdmissionReader, IBuildPublicationCatalog, IPrivateObjectStore,
        ICompiledBuildRuntime, IMachineInvocationLedger
    {
        private readonly Dictionary<MachineInvocationKey, Finish> recorded = [];
        private Guid? active;
        private LocalReleaseSnapshot? saved;
        internal BuildQualificationTests.Fixture Data { get; } = new();
        internal ActorContext Actor => new(Data.Actor);
        internal LocalReleaseTarget Target { get; }
        internal ReleaseIntent? Release => saved?.Intent;
        internal MachineCaller Caller { get; set; } = new(new(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
        internal Guid RequestId { get; } = Guid.NewGuid();
        internal List<Finish> Finishes { get; } = [];
        internal MachineInvocationService Service { get; }
        internal int RuntimeCalls, Claims;
        internal bool Enabled = true, Unauthorized, CancelAtRuntime, BlockAtRuntime, StallRelease;
        internal MachineInvocationFinishStatus? ForcedFinish;
        internal string? ChangePointerAt;
        internal MachineInvocationClaimStatus? ForcedClaim;
        internal CancellationTokenSource? Cancellation;
        /// <summary>Replaces the scripted execution, for a test that runs an actual runtime transport.</summary>
        internal Func<CancellationToken, Task<CompiledBuildResult>>? Execute;
        public string RuntimeImage => Data.Runtime;

        internal Fixture(TimeSpan? releaseBound = null, Func<BuildIntent, string, Guid>? targetId = null)
        {
            var revision = Data.Build.Intent.Revision;
            Target = new(new(new(revision.OrganizationId, revision.ProjectId, revision.MappingId), Data.Build.Intent.Environment,
                targetId?.Invoke(Data.Build.Intent, Data.Runtime) ?? Guid.NewGuid()), Data.Build.Intent.Profile, Data.Runtime);
            Service = new(this, this, _ => new LocalReleaseInvocation(this, new(this, this, this, this, this)), TimeProvider.System,
                new(1024 * 1024, 1024 * 1024, PreviewPolicy.MaximumArtifacts, PreviewPolicy.MaximumTextBytes), releaseBound);
        }

        internal Task Activate()
        {
            var qualification = Data.Create(Guid.NewGuid());
            var intent = new ReleaseIntent(Guid.NewGuid(), Data.Actor, Target.Reference, qualification, ReleaseAction.Activate, active);
            saved = new(intent, ReleasePhase.Active, [new(1, ReleaseAuditKind.Requested, DateTimeOffset.UtcNow, ReleaseRejection.None),
                new(2, ReleaseAuditKind.Activated, DateTimeOffset.UtcNow, ReleaseRejection.None)]);
            active = intent.ReleaseId;
            return Task.CompletedTask;
        }

        internal Task<MachineInvocationResult> InvokeAsync(Guid? requestId = null, MachineInvocationRequest? request = null, CancellationToken token = default) =>
            Service.InvokeAsync(Target.Reference, Caller, requestId ?? RequestId,
                request ?? new(Data.Case.Replay.Primary, Data.Case.Replay.NamedInputs, Data.Case.Replay.Parameters), token);

        public Task<MachineInvocationClaim> ClaimAsync(MachineInvocationKey key, Guid applicationId, Sha256Digest requestDigest, CancellationToken token)
        {
            Claims++;
            if (ForcedClaim is { } forced) return Task.FromResult(new MachineInvocationClaim(forced));
            if (recorded.TryGetValue(key, out var finish))
                return Task.FromResult(new MachineInvocationClaim(MachineInvocationClaimStatus.Recorded, 1, Outcome: finish.Outcome,
                    ReleaseId: finish.Provenance?.ReleaseId, RetainedResponse: finish.Response));
            return Task.FromResult(new MachineInvocationClaim(MachineInvocationClaimStatus.Claimed, 1, Guid.NewGuid(), saved!.Intent.Actor));
        }

        public async Task<MachineInvocationFinishStatus> FinishAsync(MachineInvocationKey key, Guid claimId, Guid applicationId, MachineInvocationOutcome outcome,
            MachineInvocationProvenance? provenance, string? clock, byte[]? response, CancellationToken token)
        {
            Finishes.Add(new(outcome, provenance, clock, response));
            if (StallRelease && outcome == MachineInvocationOutcome.Unavailable) await Task.Delay(Timeout.Infinite, token);
            if (ForcedFinish is { } forced) return forced;
            if (outcome is not (MachineInvocationOutcome.Unavailable or MachineInvocationOutcome.Busy or MachineInvocationOutcome.ReleaseChanged))
                recorded[key] = new(outcome, provenance, clock, response);
            return MachineInvocationFinishStatus.Recorded;
        }

        public Task<int> PurgeExpiredAsync(ReleaseTargetReference target, int maximum, CancellationToken token) => Task.FromResult(0);

        public Task<LocalReleaseTargetSnapshot?> ReadTargetAsync(ActorContext actor, ReleaseTargetReference target, CancellationToken token) =>
            Task.FromResult<LocalReleaseTargetSnapshot?>(Unauthorized ? null : new(Target, Enabled, active));
        public Task<LocalReleaseSnapshot?> ReadAsync(ActorContext actor, ReleaseTargetReference target, Guid id, CancellationToken token) =>
            Task.FromResult(!Unauthorized && saved?.Intent.ReleaseId == id ? saved : null);
        public Task<ReleaseWriteOutcome> RegisterAsync(ActorContext actor, LocalReleaseTarget target, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> DisableAsync(ActorContext actor, ReleaseTargetReference target, CancellationToken token) => throw new NotSupportedException();
        public Task<ReleaseWriteOutcome> RequestAsync(ActorContext actor, ReleaseIntent intent, CancellationToken token) => throw new NotSupportedException();
        public Task<ReleaseTransitionOutcome> ApplyAsync(ActorContext actor, ReleaseTargetReference target, Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<ReleaseTransitionOutcome> CancelAsync(ActorContext actor, ReleaseTargetReference target, Guid id, CancellationToken token) => throw new NotSupportedException();
        Task<BuildRunSnapshot?> IBuildRunReader.ReadAsync(ActorContext actor, Guid org, Guid id, CancellationToken token) => Task.FromResult<BuildRunSnapshot?>(Data.Build);
        public Task<BuildAdmission?> ReadAsync(ActorContext actor, BuildIntent intent, CancellationToken token) =>
            Task.FromResult<BuildAdmission?>(new(Data.Actor, intent.Fingerprint,
                new(PrivateObjectId.Restore(intent.Revision, Guid.NewGuid(), PrivateObjectClass.NativeArtifact), intent.NativeDigest, 1)));
        Task<BuildPublication?> IBuildPublicationCatalog.ReadAsync(ActorContext actor, Guid org, Guid id, CancellationToken token) =>
            Task.FromResult<BuildPublication?>(new(Data.Actor, Data.Build.Artifact!,
                new(PrivateObjectId.Restore(Data.Build.Intent.Revision, id, PrivateObjectClass.DerivedArtifact), Data.Build.Artifact!.ContentDigest, Data.Build.Artifact.ByteCount)));
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken token) => Task.FromResult(new byte[] { 2, 3 });
        public async Task<CompiledBuildResult> ExecuteAsync(BuildArtifactReference artifact, BuildBytes verifiedArtifact, RegressionReplay replay, CancellationToken token)
        {
            RuntimeCalls++;
            if (Execute is { } execute) return await execute(token);
            if (ChangePointerAt == "runtime") active = Guid.NewGuid();
            if (CancelAtRuntime) { await Cancellation!.CancelAsync(); token.ThrowIfCancellationRequested(); }
            if (BlockAtRuntime) await Task.Delay(Timeout.Infinite, token);
            return Data.Result;
        }
        public Task<StagedBuild?> ReadStagedAsync(ActorContext actor, Guid org, Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<StagedBuild?> StageAsync(ActorContext actor, StagedBuild build, CancellationToken token) => throw new NotSupportedException();
        public Task<BuildPublicationOutcome> PublishAsync(ActorContext actor, BuildPublication publication, CancellationToken token) => throw new NotSupportedException();
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream bytes, CancellationToken token) => throw new NotSupportedException();
    }
}
