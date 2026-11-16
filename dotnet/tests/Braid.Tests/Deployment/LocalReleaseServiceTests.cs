using Braid.Application;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Tests;

public sealed class LocalReleaseServiceTests
{
    [Fact]
    public async Task Lost_request_reply_recovers_before_current_qualification_admission_and_does_not_apply()
    {
        var test = new Fixture { LoseReply = true }; var request = test.Request();
        await Assert.ThrowsAsync<IOException>(() => test.Service.RequestAsync(test.Actor, test.Target.Reference, request));
        test.QualificationsUnavailable = true;
        var recovered = await test.Service.RequestAsync(test.Actor, test.Target.Reference, request);
        Assert.Equal(ReleaseWriteOutcome.Replayed, recovered.Outcome);
        Assert.Equal(ReleasePhase.Requested, recovered.Release!.Phase);
        Assert.Equal(1, test.QualificationReads); Assert.Equal(1, test.Writes); Assert.Equal(0, test.RuntimeCalls);
        Assert.Equal(ReleaseWriteOutcome.Conflict, (await test.Service.RequestAsync(test.Actor, test.Target.Reference,
            request with { QualificationId = Guid.NewGuid() })).Outcome);
    }

    [Fact]
    public async Task Incompatible_target_runtime_never_creates_an_intent()
    {
        var test = new Fixture();
        test.Target = new(test.Target.Reference, test.Target.Profile, "sha256:" + new string('c', 64));
        Assert.Equal(ReleaseWriteOutcome.Refused, (await test.Service.RequestAsync(test.Actor, test.Target.Reference, test.Request())).Outcome);
        Assert.Equal(0, test.Writes);
    }

    [Fact]
    public async Task Returned_qualification_identifier_is_checked_before_request_persistence()
    {
        var test = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.RequestAsync(test.Actor, test.Target.Reference,
            test.Request() with { QualificationId = Guid.NewGuid() }));
        Assert.Equal(0, test.Writes);
    }

    [Fact]
    public async Task Current_active_release_executes_its_exact_qualified_artifact()
    {
        var test = new Fixture();
        await test.Activate();
        var result = await test.Invocation.ExecuteAsync(test.Actor, test.Target.Reference, test.Data.Case.Replay);
        Assert.Equal(CompiledBuildOutcome.Completed, result.Outcome);
        Assert.Equal(1, test.RuntimeCalls); Assert.Equal(1, test.ObjectReads);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("runtime")]
    public async Task A_pointer_change_prevents_late_dispatch_or_result_delivery(string at)
    {
        var test = new Fixture { ChangePointerAt = at }; await test.Activate();
        var result = await test.Invocation.ExecuteAsync(test.Actor, test.Target.Reference, test.Data.Case.Replay);
        Assert.Equal(CompiledBuildOutcome.Refused, result.Outcome); Assert.Null(result.Outputs);
        Assert.Equal(at == "read" ? 0 : 1, test.RuntimeCalls);
    }

    [Fact]
    public async Task Disabled_target_never_reads_private_bytes()
    {
        var test = new Fixture(); await test.Activate(); test.Enabled = false;
        Assert.Equal(CompiledBuildOutcome.Refused, (await test.Invocation.ExecuteAsync(test.Actor, test.Target.Reference, test.Data.Case.Replay)).Outcome);
        Assert.Equal(0, test.ObjectReads); Assert.Equal(0, test.RuntimeCalls);
    }

    private sealed class Fixture : ILocalReleaseStore, IBuildQualificationStore, IBuildRunReader, IBuildAdmissionReader,
        IBuildPublicationCatalog, IPrivateObjectStore, ICompiledBuildRuntime
    {
        internal BuildQualificationTests.Fixture Data { get; } = new();
        internal ActorContext Actor => new(Data.Actor);
        internal BuildQualification Qualification { get; }
        internal LocalReleaseTarget Target;
        internal LocalReleaseService Service { get; }
        internal LocalReleaseInvocation Invocation { get; }
        internal int QualificationReads, Writes, RuntimeCalls, ObjectReads;
        internal bool LoseReply, QualificationsUnavailable, Enabled = true;
        internal string? ChangePointerAt;
        private Guid? active;
        private LocalReleaseSnapshot? saved;
        public string RuntimeImage => Data.Runtime;
        internal Fixture()
        {
            Qualification = Data.Create(Guid.NewGuid());
            var revision = Data.Build.Intent.Revision;
            Target = new(new(new(revision.OrganizationId, revision.ProjectId, revision.MappingId), Data.Build.Intent.Environment, Guid.NewGuid()),
                Data.Build.Intent.Profile, Data.Runtime);
            Service = new(this, this);
            Invocation = new(this, new(this, this, this, this, this));
        }
        internal LocalReleaseRequest Request() => new(Guid.NewGuid(), Qualification.Id, active);
        internal async Task Activate()
        {
            var request = Request();
            Assert.Equal(ReleaseWriteOutcome.Created, (await Service.RequestAsync(Actor, Target.Reference, request)).Outcome);
            Assert.Equal(ReleaseTransitionOutcome.Activated, await Service.ApplyAsync(Actor, Target.Reference, request.ReleaseId));
        }
        Task<BuildQualification?> IBuildQualificationStore.ReadAsync(ActorContext actor, MappingScope mapping, Guid id, CancellationToken token)
        {
            QualificationReads++;
            if (QualificationsUnavailable) throw new InvalidOperationException("Must recover before reading current qualification.");
            return Task.FromResult<BuildQualification?>(Qualification);
        }
        public Task<BuildQualificationWriteOutcome> SaveAsync(ActorContext actor, BuildQualification qualification, CancellationToken token) => throw new NotSupportedException();
        public Task<ReleaseWriteOutcome> RegisterAsync(ActorContext actor, LocalReleaseTarget target, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> DisableAsync(ActorContext actor, ReleaseTargetReference target, CancellationToken token) => throw new NotSupportedException();
        public Task<LocalReleaseTargetSnapshot?> ReadTargetAsync(ActorContext actor, ReleaseTargetReference target, CancellationToken token) =>
            Task.FromResult<LocalReleaseTargetSnapshot?>(new(Target, Enabled, active));
        public Task<LocalReleaseSnapshot?> ReadAsync(ActorContext actor, ReleaseTargetReference target, Guid id, CancellationToken token) =>
            Task.FromResult(saved?.Intent.ReleaseId == id ? saved : null);
        public Task<ReleaseWriteOutcome> RequestAsync(ActorContext actor, ReleaseIntent intent, CancellationToken token)
        {
            Writes++;
            saved = new(intent, ReleasePhase.Requested, [new(1, ReleaseAuditKind.Requested, DateTimeOffset.UtcNow, ReleaseRejection.None)]);
            if (LoseReply) throw new IOException("Synthetic lost request reply.");
            return Task.FromResult(ReleaseWriteOutcome.Created);
        }
        public Task<ReleaseTransitionOutcome> ApplyAsync(ActorContext actor, ReleaseTargetReference target, Guid id, CancellationToken token)
        {
            active = id;
            saved = new(saved!.Intent, ReleasePhase.Active, [.. saved.Audit, new(2, ReleaseAuditKind.Activated, DateTimeOffset.UtcNow, ReleaseRejection.None)]);
            return Task.FromResult(ReleaseTransitionOutcome.Activated);
        }
        public Task<ReleaseTransitionOutcome> CancelAsync(ActorContext actor, ReleaseTargetReference target, Guid id, CancellationToken token) => throw new NotSupportedException();
        Task<BuildRunSnapshot?> IBuildRunReader.ReadAsync(ActorContext actor, Guid org, Guid id, CancellationToken token) => Task.FromResult<BuildRunSnapshot?>(Data.Build);
        public Task<BuildAdmission?> ReadAsync(ActorContext actor, BuildIntent intent, CancellationToken token) =>
            Task.FromResult<BuildAdmission?>(new(Data.Actor, intent.Fingerprint,
                new(PrivateObjectId.Restore(intent.Revision, Guid.NewGuid(), PrivateObjectClass.NativeArtifact), intent.NativeDigest, 1)));
        Task<BuildPublication?> IBuildPublicationCatalog.ReadAsync(ActorContext actor, Guid org, Guid id, CancellationToken token) =>
            Task.FromResult<BuildPublication?>(new(Data.Actor, Data.Build.Artifact!,
                new(PrivateObjectId.Restore(Data.Build.Intent.Revision, id, PrivateObjectClass.DerivedArtifact), Data.Build.Artifact!.ContentDigest, Data.Build.Artifact.ByteCount)));
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken token)
        {
            ObjectReads++;
            if (ChangePointerAt == "read") active = Guid.NewGuid();
            return Task.FromResult(new byte[] { 2, 3 });
        }
        public Task<CompiledBuildResult> ExecuteAsync(BuildArtifactReference artifact, BuildBytes verifiedArtifact,
            Braid.Application.Regression.RegressionReplay replay, CancellationToken token)
        {
            RuntimeCalls++;
            if (ChangePointerAt == "runtime") active = Guid.NewGuid();
            return Task.FromResult(Data.Result);
        }
        public Task<StagedBuild?> ReadStagedAsync(ActorContext actor, Guid org, Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<StagedBuild?> StageAsync(ActorContext actor, StagedBuild build, CancellationToken token) => throw new NotSupportedException();
        public Task<BuildPublicationOutcome> PublishAsync(ActorContext actor, BuildPublication publication, CancellationToken token) => throw new NotSupportedException();
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream bytes, CancellationToken token) => throw new NotSupportedException();
    }
}
