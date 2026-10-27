using System.Text.Json;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Operations;
using Weft.Domain;

namespace Weft.Deployment;

public enum BuildQualificationFailure { InvalidInput, IdentityMismatch, NotSuccessful, IncompleteEvidence, OutputMismatch }
public sealed class BuildQualificationException(BuildQualificationFailure failure) : Exception("Build qualification refused.")
{
    public BuildQualificationFailure Failure { get; } = failure;
}

/// <summary>Owned candidate evidence from exact comparisons. It is not a stored release or an authorization grant.</summary>
public sealed class BuildQualification
{
    private readonly byte[] result;
    private BuildQualification(Guid id, PrincipalId actor, BuildArtifactReference artifact, string runtimeImage,
        RegressionCaseReference sourceCase, Guid evidenceId, Sha256Digest evidenceDigest, Sha256Digest replayDigest, byte[] result)
    {
        Id = id; Actor = actor; Artifact = artifact; RuntimeImage = runtimeImage; Case = sourceCase;
        EvidenceId = evidenceId; EvidenceDigest = evidenceDigest; ReplayDigest = replayDigest;
        this.result = result.ToArray(); ResultDigest = Sha256Digest.FromBytes(result);
        Fingerprint = Sha256Digest.FromBytes(CopyCanonicalBytes());
    }
    public Guid Id { get; }
    public PrincipalId Actor { get; }
    public BuildArtifactReference Artifact { get; }
    public string RuntimeImage { get; }
    public RegressionCaseReference Case { get; }
    public Guid EvidenceId { get; }
    public Sha256Digest EvidenceDigest { get; }
    public Sha256Digest ReplayDigest { get; }
    public Sha256Digest ResultDigest { get; }
    public int ResultByteCount => result.Length;
    public Sha256Digest Fingerprint { get; }
    public byte[] CopyResultBytes() => result.ToArray();
    public override string ToString() => nameof(BuildQualification);

    public static BuildQualification Create(Guid id, PrincipalId actor, BuildRunSnapshot build, RegressionRunSnapshot evidence,
        RegressionCaseVersion sourceCase, string runtimeImage, CompiledBuildResult execution, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(sourceCase); ArgumentNullException.ThrowIfNull(execution);
        if (id == Guid.Empty || runtimeImage is not { Length: 71 } || !runtimeImage.StartsWith("sha256:", StringComparison.Ordinal) ||
            runtimeImage[7..].Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw Invalid(BuildQualificationFailure.InvalidInput);
        var (artifact, reference) = ValidateSource(actor, build, evidence, sourceCase, token);
        if (execution.Outcome != CompiledBuildOutcome.Completed || execution.Outputs is null) throw Invalid(BuildQualificationFailure.NotSuccessful);
        var outputs = artifact.Intent.Boundaries.Where(value => value.Kind is BuildBoundaryKind.PrimaryOutput or BuildBoundaryKind.NamedOutput)
            .Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        if (!outputs.SetEquals(execution.Outputs.Outputs.Select(value => value.Key.Name)) || execution.Outputs.ByteCount > sourceCase.Replay.Limits.MaximumOutputBytes ||
            execution.Outputs.Outputs.Count > sourceCase.Replay.Limits.MaximumArtifacts) throw Invalid(BuildQualificationFailure.IncompleteEvidence);
        var comparison = new RegressionComparisonService().Compare(sourceCase.Expected, new(OperationStatusV1.Succeeded, execution.Outputs), cancellationToken: token);
        if (comparison.Outcome != RegressionComparisonOutcome.Passed) throw Invalid(BuildQualificationFailure.OutputMismatch);
        var bytes = new RegressionRunResultCodec().EncodeObservation(id, sourceCase, MappingPreviewStatus.Succeeded, execution.Outputs, token);
        var replayDigest = Sha256Digest.Parse(CompiledRuntimeProtocol.For(artifact, sourceCase.Replay).ReplayDigest);
        token.ThrowIfCancellationRequested();
        return new(id, actor, artifact, runtimeImage, reference, evidence.RunId, evidence.CompletionDigest!, replayDigest, bytes);
    }

    /// <summary>Reopens persisted proof under separately loaded immutable source identities, never by trusting a stored pass flag.</summary>
    public static BuildQualification Restore(Guid id, PrincipalId actor, BuildRunSnapshot build, RegressionRunSnapshot evidence,
        RegressionCaseVersion sourceCase, string runtimeImage, ReadOnlyMemory<byte> canonical, ReadOnlyMemory<byte> completion,
        CancellationToken token = default)
    {
        if (canonical.Length is < 1 or > 16384 || completion.Length is < 1 or > RegressionRunResultCodec.MaximumBytes)
            throw Invalid(BuildQualificationFailure.InvalidInput);
        var metadata = canonical.ToArray(); var bytes = completion.ToArray();
        var execution = new RegressionRunResultCodec().Decode(id, sourceCase, bytes, token);
        if (execution.PreviewStatus != MappingPreviewStatus.Succeeded || execution.Outputs is null)
            throw Invalid(BuildQualificationFailure.NotSuccessful);
        var result = Create(id, actor, build, evidence, sourceCase, runtimeImage, new(CompiledBuildOutcome.Completed, execution.Outputs), token);
        if (!result.CopyCanonicalBytes().AsSpan().SequenceEqual(metadata) || !result.CopyResultBytes().AsSpan().SequenceEqual(bytes))
            throw Invalid(BuildQualificationFailure.IdentityMismatch);
        token.ThrowIfCancellationRequested();
        return result;
    }

    internal static (BuildArtifactReference Artifact, RegressionCaseReference Case) ValidateSource(PrincipalId actor,
        BuildRunSnapshot build, RegressionRunSnapshot evidence, RegressionCaseVersion sourceCase, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var intent = build.Intent;
        if (build.Actor != actor || evidence.Submitter != actor || sourceCase.Owner != actor ||
            evidence.RunId != intent.RegressionEvidenceId || sourceCase.Revision != intent.Revision || sourceCase.NativeDigest != intent.NativeDigest ||
            sourceCase.Runtime.BackendId != intent.Backend.BackendId || sourceCase.Runtime.EngineRevision != intent.Backend.EngineRevision ||
            sourceCase.Runtime.BackendBuild != intent.Backend.EngineBuild || sourceCase.Runtime.AdapterBuild != intent.Backend.AdapterVersion ||
            sourceCase.Runtime.Profile != (BuildWorkerProtocol.IsDocumentProfile(intent.Profile.Id) ? DocumentPreviewPolicy.Profile : PreviewPolicy.Profile))
            throw Invalid(BuildQualificationFailure.IdentityMismatch);
        var reference = new RegressionCaseEnvelopeCodec().Encode(sourceCase, token).Reference;
        if (reference != evidence.Case) throw Invalid(BuildQualificationFailure.IdentityMismatch);
        if (build.State != BuildRunState.Succeeded || build.Attempt is < 1 or > BuildRunPolicy.MaximumAttempts ||
            build.Outcome is not (BuildExecutionOutcome.Created or BuildExecutionOutcome.Replayed) ||
            build.Artifact is not { } artifact || !artifact.MatchesIntent(intent) ||
            evidence.State != RegressionRunState.Completed || evidence.Attempt is < 1 or > 3 || evidence.CompletionDigest is null)
            throw Invalid(BuildQualificationFailure.NotSuccessful);
        if (sourceCase.Replay.Target.Kind != PreviewTargetKind.All || sourceCase.ComparisonPolicy != RegressionComparisonService.ExactBytesPolicy)
            throw Invalid(BuildQualificationFailure.IncompleteEvidence);
        var outputs = intent.Boundaries.Where(value => value.Kind is BuildBoundaryKind.PrimaryOutput or BuildBoundaryKind.NamedOutput)
            .Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        if (outputs.Count == 0 || !outputs.SetEquals(sourceCase.Expected.Outputs.Select(value => value.Key.Name))) throw Invalid(BuildQualificationFailure.IncompleteEvidence);
        if (BuildWorkerProtocol.IsDocumentProfile(intent.Profile.Id) && sourceCase.Expected.Outputs.Any(output => !intent.Boundaries.Any(boundary =>
            boundary.Kind == (output.Key.IsPrimary ? BuildBoundaryKind.PrimaryOutput : BuildBoundaryKind.NamedOutput) && boundary.Name == output.Key.Name &&
            BuildWorkerProtocol.AdmitsFormat(intent.Profile.Id, boundary.Format) && BuildWorkerProtocol.MediaType(boundary.Format) == output.MediaType)))
            throw Invalid(BuildQualificationFailure.IncompleteEvidence);
        var inputs = intent.Boundaries.Where(value => value.Kind == BuildBoundaryKind.NamedInput).Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        if (!inputs.SetEquals(sourceCase.Replay.NamedInputs.Select(value => value.Name))) throw Invalid(BuildQualificationFailure.IncompleteEvidence);
        var interpreted = new RegressionRunResultCodec().Decode(evidence.RunId, sourceCase, evidence.CopyCompletion()!, token);
        if (interpreted.Comparison?.Outcome != RegressionComparisonOutcome.Passed) throw Invalid(BuildQualificationFailure.NotSuccessful);
        return (artifact, reference);
    }

    public byte[] CopyCanonicalBytes()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("schema", "weft.build-qualification.v1");
            json.WriteString("qualificationId", Id.ToString("D"));
            json.WriteString("actorId", Actor.Value.ToString("D"));
            json.WriteString("buildId", Artifact.Intent.BuildId.ToString("D"));
            json.WriteString("intentDigest", Artifact.Intent.Fingerprint.Value);
            json.WriteString("artifactDigest", Artifact.ContentDigest.Value);
            json.WriteNumber("artifactBytes", Artifact.ByteCount);
            json.WriteString("runtimeImage", RuntimeImage);
            json.WriteString("evidenceId", EvidenceId.ToString("D"));
            json.WriteString("evidenceDigest", EvidenceDigest.Value);
            json.WriteString("caseId", Case.CaseId.ToString("D"));
            json.WriteNumber("caseVersion", Case.Version);
            json.WriteString("caseDigest", Case.ContentDigest.Value);
            json.WriteString("replayDigest", ReplayDigest.Value);
            json.WriteString("policy", RegressionComparisonService.ExactBytesPolicy);
            json.WriteString("resultDigest", ResultDigest.Value);
            json.WriteNumber("resultBytes", ResultByteCount);
            json.WriteEndObject();
        }
        return stream.ToArray();
    }
    private static BuildQualificationException Invalid(BuildQualificationFailure failure) => new(failure);
}
