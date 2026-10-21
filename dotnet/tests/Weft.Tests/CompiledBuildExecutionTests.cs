using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Tests;

public sealed class CompiledBuildExecutionTests
{
    [Fact]
    public async Task Restored_metadata_identity_is_value_bound_not_reference_bound()
    {
        var test = new Fixture();
        var result = await test.Execute();
        Assert.Equal(CompiledBuildOutcome.Completed, result.Outcome);
        Assert.Equal("{}\n"u8.ToArray(), Assert.Single(result.Outputs!.Outputs).CopyBytes());
        Assert.Equal(1, test.RuntimeCalls); Assert.Equal(1, test.ObjectReads); Assert.Equal(4, test.Admissions);
    }

    [Theory]
    [InlineData(BuildRunState.Queued)]
    [InlineData(BuildRunState.Running)]
    [InlineData(BuildRunState.Failed)]
    [InlineData(BuildRunState.Uncertain)]
    [InlineData(BuildRunState.Canceled)]
    public async Task Only_confirmed_successful_builds_can_be_invoked(BuildRunState state)
    {
        var test = new Fixture { State = state };
        Assert.Equal(CompiledBuildOutcome.Refused, (await test.Execute()).Outcome);
        Assert.Equal(0, test.RuntimeCalls); Assert.Equal(0, test.ObjectReads);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Revoked_admission_never_delivers_a_result_or_dispatches_late_work(int denyAt)
    {
        var test = new Fixture { DenyAt = denyAt };
        var result = await test.Execute();
        Assert.Equal(CompiledBuildOutcome.Refused, result.Outcome); Assert.Null(result.Outputs);
        Assert.Equal(denyAt < 4 ? 0 : 1, test.RuntimeCalls);
    }

    [Fact]
    public async Task Corrupt_artifact_bytes_are_never_executed()
    {
        var test = new Fixture { Corrupt = true };
        Assert.Equal(CompiledBuildOutcome.IntegrityFailure, (await test.Execute()).Outcome);
        Assert.Equal(0, test.RuntimeCalls);
    }

    [Fact]
    public async Task Incomplete_success_output_inventory_is_not_promoted()
    {
        var test = new Fixture { EmptyOutputs = true };
        var result = await test.Execute();
        Assert.Equal(CompiledBuildOutcome.IntegrityFailure, result.Outcome); Assert.Null(result.Outputs);
    }

    [Fact]
    public async Task A_run_under_another_mapping_is_opaque_before_private_reads()
    {
        var test = new Fixture();
        var result = await test.Service.ExecuteAsync(test.Actor, new(test.Mapping.OrganizationId, test.Mapping.ProjectId, Guid.NewGuid()), test.Intent.BuildId, test.Replay);
        Assert.Equal(CompiledBuildOutcome.Refused, result.Outcome);
        Assert.Equal(0, test.ObjectReads); Assert.Equal(0, test.Admissions); Assert.Equal(0, test.RuntimeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Failed_invocations_do_not_poison_independent_calls_or_acknowledge_them_as_successful(int failed)
    {
        var items = new[] { new Fixture(), new Fixture(), new Fixture() };
        items[failed].Corrupt = true;
        for (var index = 0; index < items.Length; index++)
            Assert.Equal(index == failed ? CompiledBuildOutcome.IntegrityFailure : CompiledBuildOutcome.Completed,
                (await items[index].Execute()).Outcome);
        items[failed].Corrupt = false;
        Assert.Equal(CompiledBuildOutcome.Completed, (await items[failed].Execute()).Outcome);
        Assert.All(items, item => Assert.Equal(1, item.RuntimeCalls));
    }

    [Fact]
    public async Task Admission_capacity_refuses_an_unbounded_waiting_queue()
    {
        var test = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        test.BeforeRuntime = async _ =>
        {
            if (Interlocked.Increment(ref started) == CompiledBuildExecution.MaximumConcurrentInvocations) entered.TrySetResult();
            await release.Task;
        };
        var pending = Enumerable.Range(0, CompiledBuildExecution.MaximumConcurrentInvocations).Select(_ => test.Execute()).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CompiledBuildOutcome.Busy, (await test.Execute()).Outcome);
        }
        finally { release.TrySetResult(); await Task.WhenAll(pending); }
    }

    [Fact]
    public async Task Requested_cancellation_does_not_become_an_execution_result()
    {
        var test = new Fixture();
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.BeforeRuntime = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var pending = test.Service.ExecuteAsync(test.Actor, test.Mapping, test.Intent.BuildId, test.Replay, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class Fixture : IBuildRunReader, IBuildAdmissionReader, IBuildPublicationCatalog, IPrivateObjectStore, ICompiledBuildRuntime
    {
        internal ActorContext Actor { get; } = new(new(Guid.NewGuid()));
        internal BuildIntent Intent { get; }
        internal MappingScope Mapping => new(Intent.Revision.OrganizationId, Intent.Revision.ProjectId, Intent.Revision.MappingId);
        internal RegressionReplay Replay { get; } = new(PreviewTargetSelection.All, "2026-10-21T00:00:00Z", PreviewLimits.Default, new(null, "{}"u8), [], []);
        internal CompiledBuildExecution Service { get; }
        internal BuildRunState State = BuildRunState.Succeeded;
        internal int Admissions, DenyAt, ObjectReads, RuntimeCalls;
        internal bool Corrupt, EmptyOutputs;
        internal Func<CancellationToken, Task>? BeforeRuntime;
        private readonly byte[] bytes = [1, 2, 3];
        private readonly PrivateObjectReference native;
        private BuildArtifactReference Artifact => BuildArtifactReference.Restore(Intent, Sha256Digest.FromBytes(bytes), bytes.Length);
        internal Fixture()
        {
            var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            native = new(PrivateObjectId.Restore(scope, Guid.NewGuid(), PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes([4]), 1);
            Intent = new(Guid.NewGuid(), scope, native.Digest, FixtureBuildCompiler.Backend, FixtureBuildCompiler.Profile,
                [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")],
                new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
            Service = new(this, this, this, this, this);
        }
        internal Task<CompiledBuildResult> Execute() => Service.ExecuteAsync(Actor, Mapping, Intent.BuildId, Replay);
        Task<BuildRunSnapshot?> IBuildRunReader.ReadAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token) =>
            Task.FromResult<BuildRunSnapshot?>(new(Actor.PrincipalId, Intent, State, 1, DateTimeOffset.UtcNow, BuildExecutionOutcome.Created, Artifact));
        Task<BuildAdmission?> IBuildAdmissionReader.ReadAsync(ActorContext actor, BuildIntent intent, CancellationToken token) =>
            Task.FromResult<BuildAdmission?>(++Admissions == DenyAt ? null : new(Actor.PrincipalId, Intent.Fingerprint, native));
        public Task<BuildPublication?> ReadAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token) =>
            Task.FromResult<BuildPublication?>(new(Actor.PrincipalId, Artifact, new(PrivateObjectId.Restore(Intent.Revision, Intent.BuildId,
                PrivateObjectClass.DerivedArtifact), Artifact.ContentDigest, Artifact.ByteCount)));
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken token)
        { ObjectReads++; return Task.FromResult(Corrupt ? new byte[] { 9 } : bytes.ToArray()); }
        public async Task<CompiledBuildResult> ExecuteAsync(BuildArtifactReference artifact, BuildBytes verifiedArtifact, RegressionReplay replay, CancellationToken cancellationToken)
        {
            RuntimeCalls++;
            if (BeforeRuntime is { } before) await before(cancellationToken);
            return new(CompiledBuildOutcome.Completed,
                RegressionOutputSet.Capture(EmptyOutputs ? [] : [RegressionOutput.Capture(RegressionOutputKey.Primary, "{}\n"u8)]));
        }
        public Task<StagedBuild?> ReadStagedAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token) => throw new NotSupportedException();
        public Task<StagedBuild?> StageAsync(ActorContext actor, StagedBuild build, CancellationToken token) => throw new NotSupportedException();
        public Task<BuildPublicationOutcome> PublishAsync(ActorContext actor, BuildPublication publication, CancellationToken token) => throw new NotSupportedException();
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream stream, CancellationToken token) => throw new NotSupportedException();
    }
}
