using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Tests;

public sealed class BuildQualificationTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("x12")]
    [InlineData("edifact")]
    [InlineData("xml")]
    public void Document_qualification_keeps_exact_bytes_media_and_the_declared_replay_identity(string format)
    {
        var value = new Fixture(format); var id = Guid.NewGuid();
        var proof = value.Create(id);
        var restored = BuildQualification.Restore(id, value.Actor, value.Build, value.Evidence, value.Case, value.Runtime,
            proof.CopyCanonicalBytes(), proof.CopyResultBytes());
        Assert.Equal(proof.Fingerprint, restored.Fingerprint);
        var execution = new RegressionRunResultCodec().Decode(id, value.Case, restored.CopyResultBytes());
        Assert.Equal(BuildWorkerProtocol.MediaType(format), Assert.Single(execution.Outputs!.Outputs).MediaType);
        var wrongMedia = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary,
            value.Case.Expected.Outputs[0].CopyBytes(), mediaType: format == "json" ? "application/edi-x12" : "application/json")]);
        Assert.Equal(BuildQualificationFailure.OutputMismatch, Assert.Throws<BuildQualificationException>(() =>
            value.Create(Guid.NewGuid(), new(CompiledBuildOutcome.Completed, wrongMedia))).Failure);
    }
    [Fact]
    public void Equal_exact_outputs_produce_stable_metadata_and_owned_private_result_bytes()
    {
        var value = new Fixture(); var id = Guid.NewGuid();
        var qualified = value.Create(id);
        Assert.Equal(qualified.Fingerprint, value.Create(id).Fingerprint);
        Assert.Equal(value.Build.Artifact!.ContentDigest, qualified.Artifact.ContentDigest);
        Assert.Equal(value.Evidence.RunId, qualified.EvidenceId);
        var copy = qualified.CopyResultBytes(); copy[0] ^= 1;
        Assert.Equal(qualified.ResultDigest, Sha256Digest.FromBytes(qualified.CopyResultBytes()));
        Assert.Equal(nameof(BuildQualification), qualified.ToString());
        Assert.Equal(RegressionComparisonOutcome.Passed,
            new RegressionRunResultCodec().Decode(id, value.Case, qualified.CopyResultBytes()).Comparison!.Outcome);
        var reopened = BuildQualification.Restore(id, value.Actor, value.Build, value.Evidence, value.Case, value.Runtime,
            qualified.CopyCanonicalBytes(), qualified.CopyResultBytes());
        Assert.Equal(qualified.Fingerprint, reopened.Fingerprint);
    }

    [Fact]
    public void Stored_proof_cannot_substitute_the_runtime_or_metadata()
    {
        var value = new Fixture(); var original = value.Create(Guid.NewGuid());
        var metadata = original.CopyCanonicalBytes(); metadata[^2] ^= 1;
        Assert.Throws<BuildQualificationException>(() => BuildQualification.Restore(original.Id, value.Actor, value.Build, value.Evidence,
            value.Case, value.Runtime, metadata, original.CopyResultBytes()));
        Assert.Throws<BuildQualificationException>(() => BuildQualification.Restore(original.Id, value.Actor, value.Build, value.Evidence,
            value.Case, "sha256:" + new string('a', 64), original.CopyCanonicalBytes(), original.CopyResultBytes()));
    }

    [Fact]
    public void Byte_changes_do_not_become_a_pass_from_equal_shape_or_execution_status()
    {
        var value = new Fixture();
        var changed = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "{\"ok\":true}"u8)]);
        Assert.Equal(BuildQualificationFailure.OutputMismatch, Assert.Throws<BuildQualificationException>(() => value.Create(Guid.NewGuid(),
            new(CompiledBuildOutcome.Completed, changed))).Failure);
    }

    [Theory]
    [InlineData(BuildRunState.Queued)]
    [InlineData(BuildRunState.Uncertain)]
    [InlineData(BuildRunState.Failed)]
    public void Only_confirmed_successful_builds_can_supply_qualification(BuildRunState state)
    {
        var value = new Fixture();
        Assert.Equal(BuildQualificationFailure.NotSuccessful, Assert.Throws<BuildQualificationException>(() =>
            BuildQualification.Create(Guid.NewGuid(), value.Actor, value.Build with { State = state }, value.Evidence,
                value.Case, value.Runtime, value.Result)).Failure);
    }

    [Fact]
    public void Missing_named_output_baselines_do_not_qualify_a_whole_build()
    {
        var value = new Fixture();
        var original = value.Build.Intent;
        var changed = new BuildIntent(original.BuildId, original.Revision, original.NativeDigest, original.Backend, original.Profile,
            [.. original.Boundaries, new(BuildBoundaryKind.NamedOutput, "summary", "json")], original.Environment, original.ApprovalId, original.RegressionEvidenceId);
        var build = value.Build with { Intent = changed, Artifact = BuildArtifactReference.Restore(changed, value.Build.Artifact!.ContentDigest, value.Build.Artifact.ByteCount) };
        Assert.Equal(BuildQualificationFailure.IncompleteEvidence, Assert.Throws<BuildQualificationException>(() =>
            BuildQualification.Create(Guid.NewGuid(), value.Actor, build, value.Evidence, value.Case, value.Runtime, value.Result)).Failure);
    }

    [Fact]
    public void A_selected_target_case_does_not_qualify_an_entire_artifact()
    {
        var value = new Fixture(); var original = value.Case;
        var replay = new RegressionReplay(PreviewTargetSelection.Primary, original.Replay.CurrentDateTime, original.Replay.Limits,
            original.Replay.Primary, original.Replay.NamedInputs, original.Replay.Parameters);
        var selected = new RegressionCaseVersion(original.CaseId, original.Version, original.Owner, original.Name, original.Revision,
            original.NativeDigest, original.Runtime, replay, original.ComparisonPolicy, original.Expected);
        var reference = new RegressionCaseEnvelopeCodec().Encode(selected).Reference;
        var bytes = new RegressionRunResultCodec().EncodeObservation(value.Evidence.RunId, selected, MappingPreviewStatus.Succeeded, selected.Expected);
        var evidence = new RegressionRunSnapshot(value.Evidence.RunId, value.Actor, reference, RegressionRunState.Completed, 1,
            DateTimeOffset.UtcNow, bytes, Sha256Digest.FromBytes(bytes));
        Assert.Equal(BuildQualificationFailure.IncompleteEvidence, Assert.Throws<BuildQualificationException>(() =>
            BuildQualification.Create(Guid.NewGuid(), value.Actor, value.Build, evidence, selected, value.Runtime, value.Result)).Failure);
    }

    [Fact]
    public void Foreign_actors_and_mutable_runtime_names_cannot_be_qualified()
    {
        var value = new Fixture();
        Assert.Equal(BuildQualificationFailure.IdentityMismatch, Assert.Throws<BuildQualificationException>(() =>
            BuildQualification.Create(Guid.NewGuid(), new(Guid.NewGuid()), value.Build, value.Evidence, value.Case, value.Runtime, value.Result)).Failure);
        Assert.Equal(BuildQualificationFailure.InvalidInput, Assert.Throws<BuildQualificationException>(() =>
            BuildQualification.Create(Guid.NewGuid(), value.Actor, value.Build, value.Evidence, value.Case, "latest", value.Result)).Failure);
    }

    [Fact]
    public void Legacy_backend_qualification_does_not_require_a_Ferrule_document_codec()
    {
        var value = new Fixture(customProfile: true);
        Assert.NotNull(value.Create(Guid.NewGuid()));
    }

    internal sealed class Fixture
    {
        internal PrincipalId Actor { get; } = new(Guid.NewGuid());
        internal string Runtime => "sha256:" + new string('e', 64);
        internal RegressionCaseVersion Case { get; }
        internal RegressionRunSnapshot Evidence { get; }
        internal BuildRunSnapshot Build { get; }
        internal CompiledBuildResult Result { get; }
        internal Fixture(string? format = null, bool customProfile = false)
        {
            var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var native = Sha256Digest.FromBytes([1]);
            var backend = new BuildBackendIdentity("ferrule", BuildWorkerProtocol.Engine, "sha256:" + new string('a', 64), "adapter");
            var xml = format == "xml" ? XmlDocumentMetadata.WithoutHints : null;
            var payload = xml is not null ? "<order/>"u8.ToArray() : format is "x12" or "edifact" ? System.Text.Encoding.UTF8.GetBytes(format == "x12" ? DocumentPreviewTestData.X12 : DocumentPreviewTestData.Edifact) : "{\"ok\":true}\n"u8.ToArray();
            var outputs = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, payload, mediaType: BuildWorkerProtocol.MediaType(format ?? "json"), xmlMetadata: xml)]);
            Case = new(Guid.NewGuid(), 1, Actor, "qualification fixture", scope, native,
                new(backend.BackendId, backend.EngineRevision, backend.EngineBuild, backend.AdapterVersion, xml is not null ? XmlPreviewAdmission.Profile : format is null ? PreviewPolicy.Profile : DocumentPreviewPolicy.Profile),
                new(PreviewTargetSelection.All, "2026-10-21T00:00:00Z", PreviewLimits.Default, new(null, xml is null ? "{}"u8 : "<order/>"u8, xmlMetadata: xml), [], []),
                RegressionComparisonService.ExactBytesPolicy, outputs);
            var evidenceId = Guid.NewGuid();
            var reference = new RegressionCaseEnvelopeCodec().Encode(Case).Reference;
            var bytes = new RegressionRunResultCodec().EncodeObservation(evidenceId, Case, MappingPreviewStatus.Succeeded, outputs);
            Evidence = new(evidenceId, Actor, reference, RegressionRunState.Completed, 1, DateTimeOffset.UtcNow, bytes, Sha256Digest.FromBytes(bytes));
            var intent = new BuildIntent(Guid.NewGuid(), scope, native, backend,
                new(customProfile ? "braid.fixture-compiler.v1" : xml is not null ? BuildWorkerProtocol.XmlProfile : format is null ? BuildWorkerProtocol.Profile : BuildWorkerProtocol.DocumentProfile, 1, "dotnet", BuildWorkerProtocol.RuntimeVersion, "braid.dotnet-assembly.v1", BuildWorkerProtocol.MaximumSourceBytes),
                [new(BuildBoundaryKind.PrimaryInput, null, xml is null ? "json" : "xml", xmlMetadata: xml), new(BuildBoundaryKind.PrimaryOutput, null, customProfile ? "fixture-bytes" : format ?? "json", xmlMetadata: xml)],
                new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), evidenceId);
            Build = new(Actor, intent, BuildRunState.Succeeded, 1, DateTimeOffset.UtcNow, BuildExecutionOutcome.Created,
                BuildArtifactReference.Restore(intent, Sha256Digest.FromBytes([2, 3]), 2));
            Result = new(CompiledBuildOutcome.Completed, outputs);
        }
        internal BuildQualification Create(Guid id, CompiledBuildResult? result = null) =>
            BuildQualification.Create(id, Actor, Build, Evidence, Case, Runtime, result ?? Result);
    }
}
