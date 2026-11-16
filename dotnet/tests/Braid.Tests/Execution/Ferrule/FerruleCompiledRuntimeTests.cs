using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

public sealed class FerruleCompiledRuntimeTests
{
    [Fact]
    public async Task Separate_runtime_identity_does_not_rewrite_the_approved_compiler_identity()
    {
        var value = new Fixture();
        var result = await value.Runtime.ExecuteAsync(value.Reference, value.Artifact, value.Replay);
        Assert.Equal(CompiledBuildOutcome.Completed, result.Outcome);
        Assert.Equal("{}\n"u8.ToArray(), Assert.Single(result.Outputs!.Outputs).CopyBytes());
        Assert.NotEqual(value.Runner.BuildIdentity, value.Intent.Backend.EngineBuild);
        Assert.Equal(value.Intent.Fingerprint, value.Runner.Seen!.Intent.Fingerprint);
        Assert.Equal(value.Reference.ContentDigest, value.Runner.Seen.Artifact.Digest);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("backend")]
    [InlineData("format")]
    public async Task Substitution_is_refused_before_any_child_execution(string change)
    {
        var value = new Fixture();
        var intent = value.Intent;
        if (change != "artifact") intent = new(intent.BuildId, intent.Revision, intent.NativeDigest,
            change == "backend" ? new(intent.Backend.BackendId, intent.Backend.EngineRevision, "sha256:" + new string('c', 64), intent.Backend.AdapterVersion) : intent.Backend,
            intent.Profile, change == "format" ? [new(BuildBoundaryKind.PrimaryInput, null, "xml"), new(BuildBoundaryKind.PrimaryOutput, null, "xml")] : intent.Boundaries,
            intent.Environment, intent.ApprovalId, intent.RegressionEvidenceId);
        var reference = BuildArtifactReference.Restore(intent, value.Reference.ContentDigest, value.Reference.ByteCount);
        var result = await value.Runtime.ExecuteAsync(reference, change == "artifact" ? new([9, 9], intent.Profile.MaximumArtifactBytes) : value.Artifact, value.Replay);
        Assert.Equal(change == "artifact" ? CompiledBuildOutcome.IntegrityFailure : CompiledBuildOutcome.Unsupported, result.Outcome);
        Assert.Null(value.Runner.Seen);
    }

    [Fact]
    public async Task Inconsistent_returned_bytes_are_not_execution_evidence()
    {
        var value = new Fixture(); value.Runner.Tamper = true;
        var result = await value.Runtime.ExecuteAsync(value.Reference, value.Artifact, value.Replay);
        Assert.Equal(CompiledBuildOutcome.IntegrityFailure, result.Outcome); Assert.Null(result.Outputs);
    }

    [Fact]
    public async Task Missing_static_inputs_and_unknown_selected_targets_are_refused_before_dispatch()
    {
        var value = new Fixture();
        var original = value.Intent;
        var intent = new BuildIntent(original.BuildId, original.Revision, original.NativeDigest, original.Backend, original.Profile,
            [.. original.Boundaries, new(BuildBoundaryKind.NamedInput, "rates", "json")], original.Environment, original.ApprovalId, original.RegressionEvidenceId);
        var artifact = BuildArtifactReference.Restore(intent, value.Artifact.Digest, value.Artifact.Length);
        Assert.Equal(CompiledBuildOutcome.InvalidInput, (await value.Runtime.ExecuteAsync(artifact, value.Artifact, value.Replay)).Outcome);
        var selected = new RegressionReplay(PreviewTargetSelection.Named("missing"), value.Replay.CurrentDateTime, value.Replay.Limits,
            value.Replay.Primary, [], []);
        Assert.Equal(CompiledBuildOutcome.InvalidInput, (await value.Runtime.ExecuteAsync(value.Reference, value.Artifact, selected)).Outcome);
        Assert.Null(value.Runner.Seen);
    }

    [Theory]
    [InlineData("executionFailed", CompiledBuildOutcome.ExecutionFailed)]
    [InlineData("invalidParameter", CompiledBuildOutcome.InvalidParameter)]
    [InlineData("resourceLimit", CompiledBuildOutcome.ResourceLimit)]
    public async Task Closed_failure_categories_never_include_partial_output(string status, CompiledBuildOutcome expected)
    {
        var value = new Fixture(); value.Runner.Status = status;
        var result = await value.Runtime.ExecuteAsync(value.Reference, value.Artifact, value.Replay);
        Assert.Equal(expected, result.Outcome); Assert.Null(result.Outputs);
    }

    [Fact]
    public void Compiled_profile_has_a_distinct_fixed_entrypoint_and_no_compiler_tmpfs()
    {
        Assert.Equal("/braid-compiled-runtime", DockerWorkerProfile.Compiled.Entrypoint);
        Assert.Equal(CompiledRuntimeProtocol.MaximumRequestBytes, DockerWorkerProfile.Compiled.MaximumRequestBytes);
        Assert.Equal(CompiledRuntimeProtocol.MaximumResponseBytes, DockerWorkerProfile.Compiled.MaximumOutputBytes);
        Assert.Equal(128, DockerWorkerProfile.Compiled.FileLimit);
        Assert.NotSame(DockerWorkerProfile.Build, DockerWorkerProfile.Compiled);
    }

    [BuildWorkerLinuxFact]
    public void Compiler_sandbox_cannot_be_adopted_as_a_compiled_invocation_runtime()
    {
        Assert.Throws<ArgumentException>(() => new FerruleCompiledBuildRuntime(new DockerInspectionWorkerRunner(new("/usr/bin/docker",
            "sha256:" + new string('a', 64), profile: DockerWorkerProfile.Build)), new Fixture().Intent.Backend));
    }

    private sealed class Fixture
    {
        internal BuildIntent Intent { get; }
        internal BuildBytes Artifact { get; }
        internal BuildArtifactReference Reference { get; }
        internal RegressionReplay Replay { get; } = new(PreviewTargetSelection.All, "2026-10-21T00:00:00Z", PreviewLimits.Default, new(null, "{}"u8), [], []);
        internal Runner Runner { get; } = new();
        internal FerruleCompiledBuildRuntime Runtime { get; }
        internal Fixture()
        {
            var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var backend = new BuildBackendIdentity("ferrule", BuildWorkerProtocol.Engine, "sha256:" + new string('a', 64), FerruleBuildCompiler.Adapter);
            Intent = new(Guid.NewGuid(), scope, Sha256Digest.FromBytes([4]), backend, FerruleBuildCompiler.Profile,
                [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")],
                new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
            Artifact = new([1, 2, 3], Intent.Profile.MaximumArtifactBytes);
            Reference = BuildArtifactReference.Restore(Intent, Artifact.Digest, Artifact.Length);
            Runtime = new(Runner, backend);
        }
    }

    private sealed class Runner : IInspectionWorkerRunner
    {
        public string BuildIdentity => "sha256:" + new string('b', 64);
        internal CompiledRuntimeProtocol.Invocation? Seen;
        internal string Status = "succeeded";
        internal bool Tamper;
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken token)
        {
            Seen = CompiledRuntimeProtocol.DecodeRequest(frame.ToArray());
            var output = Status == "succeeded" ? RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "{}\n"u8)]) : null;
            var bytes = CompiledRuntimeProtocol.EncodeResponse(Seen, Status, output);
            if (Tamper) bytes[^1] ^= 1;
            return Task.FromResult(new WorkerProcessResult(bytes, WorkerProcessFailure.None));
        }
    }
}
