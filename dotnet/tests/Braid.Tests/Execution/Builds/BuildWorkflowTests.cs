using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Braid.Application;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Tests;

public sealed class BuildWorkflowTests
{
    [Fact]
    public async Task Fixture_artifact_has_exact_independent_offsets_and_rejects_tampering()
    {
        var test = new Context();
        var built = await new FixtureBuildCompiler().BuildAsync(test.Intent,
            new BuildBytes(test.Native, FixtureBuildCompiler.MaximumBytes));
        var bytes = built.Copy();
        Assert.Equal(141 + test.Native.Length, bytes.Length);
        Assert.Equal("BRAIDFIX1"u8.ToArray(), bytes[..9]);
        Assert.Equal(test.Intent.Fingerprint.Value, Encoding.ASCII.GetString(bytes, 9, 64));
        Assert.Equal(test.Intent.NativeDigest.Value, Encoding.ASCII.GetString(bytes, 73, 64));
        Assert.Equal(test.Native.Length, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(137, 4)));
        Assert.Equal(test.Native, bytes[141..]);
        Assert.Equal(test.Native, FixtureBuildCompiler.ReadVerified(test.Intent, bytes).Copy());
        foreach (var offset in new[] { 0, 9, 73, 137, 141 })
        {
            var changed = (byte[])bytes.Clone();
            changed[offset] ^= 1;
            Assert.Throws<BuildContractException>(() => FixtureBuildCompiler.ReadVerified(test.Intent, changed));
        }
        Assert.Throws<BuildContractException>(() => FixtureBuildCompiler.ReadVerified(test.Intent, bytes[..^1]));
        Assert.Throws<BuildContractException>(() => FixtureBuildCompiler.ReadVerified(test.Intent, [.. bytes, 0]));
    }

    [Fact]
    public async Task Verified_artifact_reopens_without_running_the_compiler_again()
    {
        var test = new Context();
        var first = await test.Workflow.ExecuteAsync(test.Actor, test.Intent);
        Assert.Equal(BuildExecutionOutcome.Created, first.Outcome);
        var second = await test.Workflow.ExecuteAsync(test.Actor, test.Intent);
        Assert.Equal(BuildExecutionOutcome.Replayed, second.Outcome);
        Assert.Same(first.Publication, second.Publication);
        Assert.Equal(1, test.Compiler.Calls);
        var bytes = await test.Objects.ReadVerifiedAsync(test.Intent.Revision, first.Publication!.Object);
        Assert.Equal(test.Native, FixtureBuildCompiler.ReadVerified(test.Intent, bytes).Copy());
    }

    [Fact]
    public async Task Same_build_id_cannot_adopt_different_approval_or_environment()
    {
        var test = new Context();
        await test.Workflow.ExecuteAsync(test.Actor, test.Intent);
        var changed = new BuildIntent(test.Intent.BuildId, test.Intent.Revision, test.Intent.NativeDigest, test.Intent.Backend,
            test.Intent.Profile, test.Intent.Boundaries, test.Intent.Environment, Guid.NewGuid(), test.Intent.RegressionEvidenceId);
        Assert.Equal(BuildExecutionOutcome.Conflict, (await test.Workflow.ExecuteAsync(test.Actor, changed)).Outcome);
        Assert.Equal(1, test.Compiler.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Fresh_admission_is_required_before_each_sensitive_boundary(int refusedRead)
    {
        var test = new Context { RefuseAt = refusedRead };
        Assert.Equal(BuildExecutionOutcome.Refused, (await test.Workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
        Assert.Empty(test.Catalog.Values);
        if (refusedRead <= 2) Assert.Equal(0, test.Compiler.Calls);
        if (refusedRead <= 4) Assert.Equal(0, test.Objects.Publications);
    }

    [Fact]
    public async Task Tampered_native_bytes_never_reach_the_compiler()
    {
        var test = new Context();
        test.Objects.Corrupt = reference => reference.Id.Class == PrivateObjectClass.NativeArtifact;
        Assert.Equal(BuildExecutionOutcome.IntegrityFailure, (await test.Workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
        Assert.Equal(0, test.Compiler.Calls);
    }

    [Fact]
    public async Task Tampered_artifact_bytes_do_not_publish_or_reopen_as_success()
    {
        var test = new Context();
        test.Objects.Corrupt = reference => reference.Id.Class == PrivateObjectClass.DerivedArtifact;
        Assert.Equal(BuildExecutionOutcome.IntegrityFailure, (await test.Workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
        Assert.Empty(test.Catalog.Values);
        test.Objects.Corrupt = _ => false;
        Assert.Equal(BuildExecutionOutcome.Created, (await test.Workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
        test.Objects.Corrupt = reference => reference.Id.Class == PrivateObjectClass.DerivedArtifact;
        Assert.Equal(BuildExecutionOutcome.IntegrityFailure, (await test.Workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
    }

    [Fact]
    public async Task Unsupported_runtime_is_refused_without_source_read_or_build()
    {
        var test = new Context();
        var wrong = new BuildIntent(test.Intent.BuildId, test.Intent.Revision, test.Intent.NativeDigest,
            new("ferrule", "engine", "build", "adapter"), test.Intent.Profile, test.Intent.Boundaries,
            test.Intent.Environment, test.Intent.ApprovalId, test.Intent.RegressionEvidenceId);
        Assert.Equal(BuildExecutionOutcome.Unsupported, (await test.Workflow.ExecuteAsync(test.Actor, wrong)).Outcome);
        Assert.Equal(0, test.Objects.Reads);
        Assert.Equal(0, test.Compiler.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task One_failed_build_does_not_prevent_other_builds_or_duplicate_successful_work(int failed)
    {
        var test = new Context();
        var intents = Enumerable.Range(0, 3).Select(_ => test.WithId(Guid.NewGuid())).ToArray();
        test.Compiler.Fail = intent => intent.BuildId == intents[failed].BuildId;
        var results = new List<BuildExecutionResult>();
        foreach (var intent in intents) results.Add(await test.Workflow.ExecuteAsync(test.Actor, intent));
        Assert.Equal(2, results.Count(result => result.Outcome == BuildExecutionOutcome.Created));
        Assert.Equal(BuildExecutionOutcome.Unavailable, results[failed].Outcome);
        test.Compiler.Fail = _ => false;
        for (var i = 0; i < 3; i++)
            Assert.Equal(i == failed ? BuildExecutionOutcome.Created : BuildExecutionOutcome.Replayed,
                (await test.Workflow.ExecuteAsync(test.Actor, intents[i])).Outcome);
        Assert.Equal(4, test.Compiler.Calls);
    }

    [Fact]
    public async Task Lost_publication_acknowledgement_recovers_the_exact_committed_artifact()
    {
        var test = new Context();
        test.Catalog.LoseAcknowledgement = true;
        Assert.Equal(BuildExecutionOutcome.Unavailable, (await test.Workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
        Assert.Single(test.Catalog.Values);
        Assert.Equal(BuildExecutionOutcome.Replayed, (await test.Workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
        Assert.Equal(1, test.Compiler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ambiguous_stage_or_object_publication_recovers_original_nondeterministic_bytes(bool stage)
    {
        var test = new Context();
        test.Compiler.ChangingOutput = true;
        test.Catalog.LoseStageAcknowledgement = stage;
        test.Objects.LoseAcknowledgement = !stage;
        Assert.Equal(BuildExecutionOutcome.Unavailable, (await test.Workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
        var original = Assert.Single(test.Catalog.Staged).Value;
        Assert.Empty(test.Catalog.Values);
        var result = await test.Workflow.ExecuteAsync(test.Actor, test.Intent);
        Assert.Equal(BuildExecutionOutcome.Created, result.Outcome);
        Assert.Equal(original.Bytes.Digest, result.Publication!.Artifact.ContentDigest);
        Assert.Equal(1, test.Compiler.Calls);
    }

    [Fact]
    public async Task Concurrent_different_compiler_outputs_publish_the_first_staged_artifact_only()
    {
        var test = new Context();
        test.Compiler.ChangingOutput = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Compiler.BeforeBuild = () => gate.Task;
        var attempts = Enumerable.Range(0, 4).Select(_ => test.Workflow.ExecuteAsync(test.Actor, test.Intent)).ToArray();
        gate.SetResult();
        var outcomes = await Task.WhenAll(attempts);
        Assert.Single(outcomes, result => result.Outcome == BuildExecutionOutcome.Created);
        Assert.Equal(3, outcomes.Count(result => result.Outcome == BuildExecutionOutcome.Replayed));
        Assert.Single(outcomes.Select(result => result.Publication!.Artifact.ContentDigest).Distinct());
        Assert.Single(test.Catalog.Staged);
        Assert.Single(test.Catalog.Values);
    }

    [Fact]
    public async Task Cancellation_leaves_no_completed_publication_and_retry_is_possible()
    {
        var test = new Context();
        using var stop = new CancellationTokenSource();
        test.Compiler.BeforeBuild = () => { stop.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Workflow.ExecuteAsync(test.Actor, test.Intent, stop.Token));
        Assert.Empty(test.Catalog.Values);
        test.Compiler.BeforeBuild = null;
        Assert.Equal(BuildExecutionOutcome.Created, (await test.Workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
    }

    [Fact]
    public async Task Only_four_builds_are_admitted_without_an_unbounded_waiting_queue()
    {
        var test = new Context();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Compiler.BeforeBuild = () => gate.Task;
        var active = Enumerable.Range(0, 4).Select(_ => test.Workflow.ExecuteAsync(test.Actor, test.WithId(Guid.NewGuid()))).ToArray();
        try
        {
            Assert.Equal(BuildExecutionOutcome.Busy, (await test.Workflow.ExecuteAsync(test.Actor, test.WithId(Guid.NewGuid()))).Outcome);
        }
        finally { gate.TrySetResult(); await Task.WhenAll(active); }
    }

    [Fact]
    public async Task Fixture_target_retries_have_one_effect_and_conflicting_intent_cannot_replace_it()
    {
        var test = new Context();
        var build = await test.Workflow.ExecuteAsync(test.Actor, test.Intent);
        var target = new FixtureBuildTarget(test, test.Objects);
        var id = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => target.DeployAsync(test.Actor, id, build.Publication!)));
        Assert.Single(results, result => result.Outcome == FixtureDeploymentOutcome.Created);
        Assert.Equal(3, results.Count(result => result.Outcome == FixtureDeploymentOutcome.Replayed));
        Assert.Equal(1, target.Count);
        var another = await test.Workflow.ExecuteAsync(test.Actor, test.WithId(Guid.NewGuid()));
        Assert.Equal(FixtureDeploymentOutcome.Conflict, (await target.DeployAsync(test.Actor, id, another.Publication!)).Outcome);
        Assert.Equal(1, target.Count);
    }

    [Fact]
    public async Task Fixture_target_rechecks_authority_and_artifact_integrity()
    {
        var test = new Context();
        var build = await test.Workflow.ExecuteAsync(test.Actor, test.Intent);
        var target = new FixtureBuildTarget(test, test.Objects);
        test.Allowed = false;
        Assert.Equal(FixtureDeploymentOutcome.Refused, (await target.DeployAsync(test.Actor, Guid.NewGuid(), build.Publication!)).Outcome);
        test.Allowed = true;
        test.Objects.Corrupt = _ => true;
        Assert.Equal(FixtureDeploymentOutcome.IntegrityFailure, (await target.DeployAsync(test.Actor, Guid.NewGuid(), build.Publication!)).Outcome);
        Assert.Equal(0, target.Count);
    }

    [Fact]
    public async Task Malformed_admission_identity_is_not_an_authorization_grant()
    {
        var test = new Context { WrongActor = true };
        Assert.Equal(BuildExecutionOutcome.IntegrityFailure, (await test.Workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
        Assert.Equal(0, test.Compiler.Calls);
    }

    private sealed class Context : IBuildAdmissionReader
    {
        internal readonly ActorContext Actor = new(new(Guid.NewGuid()));
        internal readonly byte[] Native = Encoding.UTF8.GetBytes("fixture-native-source\n");
        internal readonly Objects Objects = new();
        internal readonly Catalog Catalog = new();
        internal readonly Compiler Compiler = new();
        internal BuildIntent Intent { get; }
        internal BuildWorkflow Workflow { get; }
        private readonly PrivateObjectReference native;
        private int reads;
        internal int RefuseAt { get; init; }
        internal bool Allowed { get; set; } = true;
        internal bool WrongActor { get; init; }
        internal Context()
        {
            var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            native = new(PrivateObjectId.Create(scope, PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes(Native), Native.Length);
            Objects.Values[native.Id] = Native;
            Intent = new(Guid.NewGuid(), scope, native.Digest, FixtureBuildCompiler.Backend, FixtureBuildCompiler.Profile,
                [new(BuildBoundaryKind.PrimaryInput, null, "fixture-bytes"), new(BuildBoundaryKind.PrimaryOutput, null, "fixture-bytes")],
                new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
            Workflow = new(this, Compiler, Catalog, Objects);
        }
        internal BuildIntent WithId(Guid id) => new(id, Intent.Revision, Intent.NativeDigest, Intent.Backend, Intent.Profile,
            Intent.Boundaries, Intent.Environment, Intent.ApprovalId, Intent.RegressionEvidenceId);
        public Task<BuildAdmission?> ReadAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var count = Interlocked.Increment(ref reads);
            return Task.FromResult<BuildAdmission?>(!Allowed || count == RefuseAt ? null : new(WrongActor ? new(Guid.NewGuid()) : actor.PrincipalId, intent.Fingerprint, native));
        }
    }
    private sealed class Compiler : IBuildCompiler
    {
        private readonly FixtureBuildCompiler inner = new();
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal Func<BuildIntent, bool> Fail { get; set; } = _ => false;
        internal Func<Task>? BeforeBuild { get; set; }
        internal bool ChangingOutput { get; set; }
        public Task<BuildCapabilitySnapshot> InspectAsync(BuildIntent intent, CancellationToken token = default) => inner.InspectAsync(intent, token);
        public async Task<BuildBytes> BuildAsync(BuildIntent intent, BuildBytes native, CancellationToken token = default)
        {
            Interlocked.Increment(ref calls);
            if (BeforeBuild is not null) await BeforeBuild();
            token.ThrowIfCancellationRequested();
            if (Fail(intent)) throw new IOException("Synthetic bounded build failure.");
            if (ChangingOutput) return new(Guid.NewGuid().ToByteArray(), 1024);
            return await inner.BuildAsync(intent, native, token);
        }
    }
    private sealed class Catalog : IBuildPublicationCatalog
    {
        internal readonly ConcurrentDictionary<(Guid, Guid), BuildPublication> Values = new();
        internal readonly ConcurrentDictionary<(Guid, Guid), StagedBuild> Staged = new();
        internal bool LoseAcknowledgement { get; set; }
        internal bool LoseStageAcknowledgement { get; set; }
        public Task<BuildPublication?> ReadAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default) =>
            Task.FromResult(Values.GetValueOrDefault((organizationId, buildId)));
        public Task<StagedBuild?> ReadStagedAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default) =>
            Task.FromResult(Staged.GetValueOrDefault((organizationId, buildId)));
        public Task<StagedBuild?> StageAsync(ActorContext actor, StagedBuild build, CancellationToken token = default)
        {
            var intent = build.Publication.Artifact.Intent;
            var saved = Staged.GetOrAdd((intent.Revision.OrganizationId, intent.BuildId), build);
            if (LoseStageAcknowledgement) { LoseStageAcknowledgement = false; throw new IOException("Synthetic lost staging acknowledgement."); }
            return Task.FromResult<StagedBuild?>(saved);
        }
        public Task<BuildPublicationOutcome> PublishAsync(ActorContext actor, BuildPublication publication, CancellationToken token = default)
        {
            var intent = publication.Artifact.Intent;
            if (!Values.TryAdd((intent.Revision.OrganizationId, intent.BuildId), publication)) return Task.FromResult(BuildPublicationOutcome.Replayed);
            if (LoseAcknowledgement) { LoseAcknowledgement = false; throw new IOException("Synthetic lost acknowledgement."); }
            return Task.FromResult(BuildPublicationOutcome.Created);
        }
    }
    private sealed class Objects : IPrivateObjectStore
    {
        internal readonly ConcurrentDictionary<PrivateObjectId, byte[]> Values = new();
        internal Func<PrivateObjectReference, bool> Corrupt { get; set; } = _ => false;
        internal int Reads { get; private set; }
        internal int Publications { get; private set; }
        internal bool LoseAcknowledgement { get; set; }
        public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            Publications++;
            using var bytes = new MemoryStream(); await source.CopyToAsync(bytes, cancellationToken);
            var data = bytes.ToArray();
            if (reference.Id.Scope != expectedScope || reference.Digest != Sha256Digest.FromBytes(data) || reference.ByteCount != data.Length)
                throw new PrivateObjectStorageException(PrivateObjectStorageError.Integrity);
            if (Values.TryAdd(reference.Id, data))
            {
                if (LoseAcknowledgement) { LoseAcknowledgement = false; throw new IOException("Synthetic lost object acknowledgement."); }
                return PrivateObjectPublishResult.Created;
            }
            if (!Values[reference.Id].SequenceEqual(data)) throw new PrivateObjectStorageException(PrivateObjectStorageError.Conflict);
            return PrivateObjectPublishResult.AlreadyPresent;
        }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            return Task.FromResult(Corrupt(reference) ? [1, 2, 3] : Values[reference.Id].ToArray());
        }
    }
}
