using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Tests;

public sealed class BuildQualificationServiceTests
{
    [Fact]
    public async Task Only_the_published_package_can_be_qualified_or_replay_its_receipt()
    {
        var test = new Fixture(); var id = Guid.NewGuid();
        var foreign = new Fixture().Package;
        Assert.Equal(BuildQualificationRequestOutcome.NotQualified, (await test.Put(id, foreign)).Outcome);
        Assert.Equal(0, test.RuntimeCalls);
        var package = test.Package;
        Assert.Equal(BuildQualificationRequestOutcome.Created, (await test.Put(id, package)).Outcome);
        Assert.Same(package.Bytes, test.Dispatched);
        Assert.Equal(BuildQualificationRequestOutcome.Conflict, (await test.Put(id, foreign)).Outcome);
        Assert.Equal(BuildQualificationRequestOutcome.Replayed, (await test.Put(id, package)).Outcome);
        Assert.Equal(1, test.RuntimeCalls);
    }
    [Fact]
    public async Task Lost_save_reply_recovers_the_original_proof_without_rerunning_or_renewing_admission()
    {
        var test = new Fixture { LoseReply = true }; var id = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => test.Put(id));
        var original = test.Saved!.Fingerprint;
        test.Admitted = false;
        var recovered = await test.Put(id);
        Assert.Equal(BuildQualificationRequestOutcome.Replayed, recovered.Outcome);
        Assert.Equal(original, recovered.Qualification!.Fingerprint);
        Assert.Equal(1, test.RuntimeCalls); Assert.Equal(1, test.SaveCalls);
        Assert.Equal(BuildQualificationRequestOutcome.Conflict, (await test.Service.PutAsync(test.Actor, test.Mapping, id, Guid.NewGuid())).Outcome);
    }

    [Fact]
    public async Task A_current_role_is_required_for_private_recovery()
    {
        var test = new Fixture(); var id = Guid.NewGuid();
        Assert.Equal(BuildQualificationRequestOutcome.Created, (await test.Put(id)).Outcome);
        test.Role = ProjectRole.Viewer;
        var denied = await test.Put(id);
        Assert.Equal(BuildQualificationRequestOutcome.Refused, denied.Outcome); Assert.Null(denied.Qualification);
        Assert.Equal(1, test.RuntimeCalls);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("build")]
    [InlineData("mapping")]
    [InlineData("evidence")]
    [InlineData("case")]
    public async Task Substituted_sources_never_dispatch(string changed)
    {
        var test = new Fixture { Substitute = changed };
        Assert.NotEqual(BuildQualificationRequestOutcome.Created, (await test.Put(Guid.NewGuid())).Outcome);
        Assert.Equal(0, test.RuntimeCalls); Assert.Equal(0, test.SaveCalls);
    }

    [Fact]
    public async Task An_artifact_changed_between_metadata_and_execution_is_not_labeled_as_the_original()
    {
        var test = new Fixture { ChangeArtifactOnExecution = true };
        Assert.Equal(BuildQualificationRequestOutcome.NotQualified, (await test.Put(Guid.NewGuid())).Outcome);
        Assert.Equal(0, test.RuntimeCalls); Assert.Equal(0, test.SaveCalls);
    }

    [Fact]
    public async Task Runtime_identity_changes_are_not_qualified()
    {
        var test = new Fixture { ChangeRuntime = true };
        Assert.Equal(BuildQualificationRequestOutcome.NotQualified, (await test.Put(Guid.NewGuid())).Outcome);
        Assert.Equal(1, test.RuntimeCalls); Assert.Equal(0, test.SaveCalls);
    }

    [Fact]
    public async Task Runtime_substitution_before_dispatch_does_not_execute_another_image()
    {
        var test = new Fixture { ChangeRuntimeBeforeDispatch = true };
        Assert.Equal(BuildQualificationRequestOutcome.NotQualified, (await test.Put(Guid.NewGuid())).Outcome);
        Assert.Equal(0, test.RuntimeCalls); Assert.Equal(0, test.SaveCalls);
    }

    [Fact]
    public async Task Malformed_retained_evidence_cannot_dispatch_or_save_a_qualification()
    {
        var test = new Fixture { MalformedEvidence = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Put(Guid.NewGuid()));
        Assert.Equal(0, test.RuntimeCalls); Assert.Equal(0, test.SaveCalls);
    }

    [Theory]
    [InlineData(CompiledBuildOutcome.Unavailable)]
    [InlineData(CompiledBuildOutcome.Busy)]
    public async Task Infrastructure_failure_is_retryable_and_retains_no_false_qualification(CompiledBuildOutcome outcome)
    {
        var test = new Fixture { RuntimeFailure = outcome };
        Assert.Equal(BuildQualificationRequestOutcome.Unavailable, (await test.Put(Guid.NewGuid())).Outcome);
        Assert.Null(test.Saved); Assert.Equal(0, test.SaveCalls);
    }

    [Theory]
    [InlineData("after-runtime")]
    [InlineData("at-store")]
    [InlineData("after-store")]
    public async Task Revocation_never_delivers_or_invents_a_qualification(string point)
    {
        var test = new Fixture { RevokeAt = point };
        var result = await test.Put(Guid.NewGuid());
        Assert.Equal(BuildQualificationRequestOutcome.Refused, result.Outcome); Assert.Null(result.Qualification);
        Assert.Equal(point == "after-runtime" ? 0 : 1, test.SaveCalls);
        Assert.Equal(point == "after-store", test.Saved is not null);
    }

    [Fact]
    public async Task Persistence_cannot_replace_the_proof_or_original_identity()
    {
        var test = new Fixture { Substitute = "saved" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Put(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Failed_items_are_independent_and_corrected_retry_does_not_repeat_successes(int failed)
    {
        var items = Enumerable.Range(0, 3).Select(_ => new Fixture()).ToArray();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        items[failed].BadOutput = true;
        for (var index = 0; index < items.Length; index++)
            Assert.Equal(index == failed ? BuildQualificationRequestOutcome.NotQualified : BuildQualificationRequestOutcome.Created,
                (await items[index].Put(ids[index])).Outcome);
        items[failed].BadOutput = false;
        for (var index = 0; index < items.Length; index++)
            Assert.Equal(index == failed ? BuildQualificationRequestOutcome.Created : BuildQualificationRequestOutcome.Replayed,
                (await items[index].Put(ids[index])).Outcome);
        for (var index = 0; index < items.Length; index++) Assert.Equal(index == failed ? 2 : 1, items[index].RuntimeCalls);
    }

    private sealed class Fixture : IBuildQualificationStore, IBuildRunReader, IRegressionRunReader, IRegressionCaseReader,
        IBuildAdmissionReader, IBuildPublicationCatalog, IPrivateObjectStore, ICompiledBuildRuntime, IProjectReader
    {
        private readonly BuildQualificationTests.Fixture data = new();
        internal ActorContext Actor => new(data.Actor);
        internal MappingScope Mapping => new(data.Build.Intent.Revision.OrganizationId, data.Build.Intent.Revision.ProjectId, data.Build.Intent.Revision.MappingId);
        internal ProjectRole Role = ProjectRole.Publisher;
        internal bool LoseReply, Admitted = true, BadOutput, ChangeRuntime, ChangeArtifactOnExecution;
        internal bool ChangeRuntimeBeforeDispatch, MalformedEvidence;
        internal CompiledBuildOutcome? RuntimeFailure;
        internal string? Substitute, RevokeAt;
        internal BuildQualification? Saved;
        internal int RuntimeCalls, SaveCalls;
        private int buildReads;
        private string image = "sha256:" + new string('a', 64);
        public string RuntimeImage => image;
        internal BuildQualificationService Service { get; }
        internal Fixture() => Service = new(this, this, this, this, new(this, this, this, this, this), this);
        internal MappingPackage Package => new(MappingPackageManifest.Create(data.Build.Artifact!), new([2, 3], 2));
        internal BuildBytes? Dispatched;
        internal Task<BuildQualificationRequestResult> Put(Guid id, MappingPackage? package = null) => Service.PutAsync(Actor, Mapping, id, data.Build.Intent.BuildId, package: package);
        public Task<BuildQualification?> ReadAsync(ActorContext actor, MappingScope mapping, Guid id, CancellationToken token) => Task.FromResult(Saved);
        public Task<BuildQualificationWriteOutcome> SaveAsync(ActorContext actor, BuildQualification value, CancellationToken token)
        {
            SaveCalls++;
            if (RevokeAt == "at-store") return Task.FromResult(BuildQualificationWriteOutcome.Refused);
            Saved = Substitute == "saved" ? data.Create(Guid.NewGuid()) : value;
            if (LoseReply) throw new IOException("Synthetic lost reply.");
            if (RevokeAt == "after-store") Role = ProjectRole.Viewer;
            return Task.FromResult(BuildQualificationWriteOutcome.Created);
        }
        Task<BuildRunSnapshot?> IBuildRunReader.ReadAsync(ActorContext actor, Guid organizationId, Guid id, CancellationToken token)
        {
            buildReads++;
            var original = data.Build;
            if (Substitute is "actor") original = original with { Actor = new(Guid.NewGuid()) };
            if (Substitute is "build" or "mapping")
            {
                var intent = original.Intent;
                var revision = intent.Revision;
                if (Substitute == "mapping") revision = new(revision.OrganizationId, revision.ProjectId, Guid.NewGuid(), revision.RevisionId);
                intent = new(Substitute == "build" ? Guid.NewGuid() : intent.BuildId, revision, intent.NativeDigest, intent.Backend,
                    intent.Profile, intent.Boundaries, intent.Environment, intent.ApprovalId, intent.RegressionEvidenceId);
                original = original with { Intent = intent };
            }
            if (ChangeArtifactOnExecution && buildReads > 1)
                original = original with { Artifact = BuildArtifactReference.Restore(original.Intent, Sha256Digest.FromBytes([9]), 1) };
            return Task.FromResult<BuildRunSnapshot?>(original);
        }
        Task<RegressionRunSnapshot?> IRegressionRunReader.ReadAsync(ActorContext actor, Guid organizationId, Guid id, CancellationToken token)
        {
            if (MalformedEvidence)
            {
                byte[] malformed = "{}"u8.ToArray();
                return Task.FromResult<RegressionRunSnapshot?>(new(data.Evidence.RunId, data.Actor, data.Evidence.Case,
                    RegressionRunState.Completed, 1, data.Evidence.CreatedAt, malformed, Sha256Digest.FromBytes(malformed)));
            }
            return Task.FromResult<RegressionRunSnapshot?>(Substitute == "evidence" ? new(Guid.NewGuid(), data.Actor, data.Evidence.Case,
                RegressionRunState.Completed, 1, data.Evidence.CreatedAt, data.Evidence.CopyCompletion(), data.Evidence.CompletionDigest) : data.Evidence);
        }
        Task<RegressionRunPage> IRegressionRunReader.ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterRunId, CancellationToken token) => throw new NotSupportedException();
        public Task<RegressionCaseVersion?> ReadAsync(ActorContext actor, MappingScope mapping, Guid id, long version, CancellationToken token) =>
            Task.FromResult<RegressionCaseVersion?>(Substitute == "case" ? new BuildQualificationTests.Fixture().Case : data.Case);
        Task<RegressionCasePage> IRegressionCaseReader.ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterCaseId, CancellationToken token) => throw new NotSupportedException();
        public Task<BuildAdmission?> ReadAsync(ActorContext actor, BuildIntent intent, CancellationToken token) => Task.FromResult<BuildAdmission?>(Admitted
            ? new(data.Actor, intent.Fingerprint, new(PrivateObjectId.Restore(intent.Revision, Guid.NewGuid(), PrivateObjectClass.NativeArtifact), intent.NativeDigest, 1)) : null);
        Task<BuildPublication?> IBuildPublicationCatalog.ReadAsync(ActorContext actor, Guid organizationId, Guid id, CancellationToken token) =>
            Task.FromResult<BuildPublication?>(new(data.Actor, data.Build.Artifact!, new(PrivateObjectId.Restore(data.Build.Intent.Revision, id,
                PrivateObjectClass.DerivedArtifact), data.Build.Artifact!.ContentDigest, data.Build.Artifact.ByteCount)));
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken token)
        {
            if (ChangeRuntimeBeforeDispatch) image = "sha256:" + new string('c', 64);
            return Task.FromResult(new byte[] { 2, 3 });
        }
        public Task<CompiledBuildResult> ExecuteAsync(BuildArtifactReference artifact, BuildBytes verifiedArtifact, RegressionReplay replay, CancellationToken token)
        {
            RuntimeCalls++;
            Dispatched = verifiedArtifact;
            if (ChangeRuntime) image = "sha256:" + new string('b', 64);
            if (RevokeAt == "after-runtime") Role = ProjectRole.Viewer;
            if (RuntimeFailure is { } failure) return Task.FromResult(new CompiledBuildResult(failure));
            return Task.FromResult(BadOutput ? new(CompiledBuildOutcome.Completed,
                RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "{}"u8)])) : data.Result);
        }
        public Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope, CancellationToken token) =>
            Task.FromResult<ProjectAccess?>(new(scope, actor.PrincipalId, Role));
        public Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit, Guid? afterProjectId, CancellationToken token) => throw new NotSupportedException();
        public Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken token) => throw new NotSupportedException();
        public Task<StagedBuild?> ReadStagedAsync(ActorContext actor, Guid organizationId, Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<StagedBuild?> StageAsync(ActorContext actor, StagedBuild build, CancellationToken token) => throw new NotSupportedException();
        public Task<BuildPublicationOutcome> PublishAsync(ActorContext actor, BuildPublication publication, CancellationToken token) => throw new NotSupportedException();
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream stream, CancellationToken token) => throw new NotSupportedException();
    }
}
