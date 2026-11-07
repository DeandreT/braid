using Weft.Domain;

namespace Weft.Application.Regression;

public enum RegressionCaseFailure { InvalidInput, ResourceLimit, StorageIntegrity }

public sealed class RegressionCaseException : Exception
{
    public RegressionCaseException(RegressionCaseFailure failure) : base("Regression case refused.")
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        Failure = failure;
    }

    public RegressionCaseFailure Failure { get; }
}

/// <summary>Exact runtime selection; declared identity is not proof of qualification or execution.</summary>
public sealed record RegressionRuntimeIdentity
{
    public RegressionRuntimeIdentity(string backendId, string engineRevision, string backendBuild, string adapterBuild, string profile)
    {
        BackendId = RegressionCaseValues.Text(backendId, 128);
        EngineRevision = RegressionCaseValues.Text(engineRevision, 256);
        BackendBuild = RegressionCaseValues.Text(backendBuild, 256);
        AdapterBuild = RegressionCaseValues.Text(adapterBuild, 128);
        Profile = RegressionCaseValues.Text(profile, 128);
    }

    public string BackendId { get; }
    public string EngineRevision { get; }
    public string BackendBuild { get; }
    public string AdapterBuild { get; }
    public string Profile { get; }
    public override string ToString() => nameof(RegressionRuntimeIdentity);
}

/// <summary>One immutable private case version. The owner and revision are facts, not an authorization grant.</summary>
public sealed class RegressionCaseVersion
{
    public RegressionCaseVersion(Guid caseId, long version, PrincipalId owner, string name, RevisionScope revision,
        Sha256Digest nativeDigest, RegressionRuntimeIdentity runtime, RegressionReplay replay,
        string comparisonPolicy, RegressionOutputSet expected, EdiComparisonOptions? ediOptions = null)
    {
        if (caseId == Guid.Empty || version < 1) throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(expected);
        if (runtime.Profile is not (PreviewPolicy.Profile or DocumentPreviewPolicy.Profile) ||
            comparisonPolicy is not (RegressionComparisonService.ExactBytesPolicy or RegressionComparisonService.EdiSegmentsPolicy) ||
            (comparisonPolicy == RegressionComparisonService.EdiSegmentsPolicy) != (ediOptions is not null) ||
            runtime.Profile == PreviewPolicy.Profile && (comparisonPolicy != RegressionComparisonService.ExactBytesPolicy || expected.Outputs.Any(output => output.MediaType != PreviewPolicy.MediaType)))
            throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        if (expected.Outputs.Count > replay.Limits.MaximumArtifacts || expected.ByteCount > replay.Limits.MaximumOutputBytes)
            throw new RegressionCaseException(RegressionCaseFailure.ResourceLimit);
        foreach (var output in expected.Outputs)
        {
            if (output.Key.Name is not null) RegressionCaseValues.Name(output.Key.Name);
            if (replay.Target.Kind == PreviewTargetKind.Primary && !output.Key.IsPrimary ||
                replay.Target.Kind == PreviewTargetKind.Named && output.Key.Name != replay.Target.Name)
                throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        }
        CaseId = caseId;
        Version = version;
        Owner = owner;
        Name = RegressionCaseValues.Text(name, 256);
        Revision = revision;
        NativeDigest = nativeDigest;
        Runtime = runtime;
        Replay = replay;
        ComparisonPolicy = comparisonPolicy;
        EdiOptions = ediOptions;
        Expected = expected;
    }

    public Guid CaseId { get; }
    public long Version { get; }
    public PrincipalId Owner { get; }
    public string Name { get; }
    public RevisionScope Revision { get; }
    public Sha256Digest NativeDigest { get; }
    public RegressionRuntimeIdentity Runtime { get; }
    public RegressionReplay Replay { get; }
    public string ComparisonPolicy { get; }
    public EdiComparisonOptions? EdiOptions { get; }
    public RegressionOutputSet Expected { get; }

    /// <summary>Constructs exact replay input, not permission to execute; callers recheck current authority first.</summary>
    public MappingPreviewRequest CreatePreviewRequest(NativeArtifact artifact, RegressionRuntimeIdentity observedRuntime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(observedRuntime);
        if (artifact.Scope != Revision || artifact.Digest != NativeDigest || observedRuntime != Runtime)
            throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        if (artifact.ByteCount > PreviewPolicy.MaximumNativeBytes)
            throw new RegressionCaseException(RegressionCaseFailure.ResourceLimit);
        return new(artifact, Replay.Target, Replay.Primary, Replay.NamedInputs, Replay.Parameters,
            Replay.CurrentDateTime, Replay.Limits)
        { Profile = Runtime.Profile == DocumentPreviewPolicy.Profile ? PreviewExecutionProfile.Document : PreviewExecutionProfile.LegacyJson };
    }

    public override string ToString() => nameof(RegressionCaseVersion);
}

/// <summary>Metadata from an authorized private store; all fields must match the loaded envelope.</summary>
public sealed record RegressionCaseReference
{
    public RegressionCaseReference(Guid caseId, long version, PrincipalId owner, RevisionScope revision,
        Sha256Digest nativeDigest, Sha256Digest contentDigest, int byteCount)
    {
        if (caseId == Guid.Empty || version < 1 || byteCount < 1 || byteCount > RegressionCaseEnvelopeCodec.MaximumEnvelopeBytes)
            throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        ArgumentNullException.ThrowIfNull(contentDigest);
        CaseId = caseId;
        Version = version;
        Owner = owner;
        Revision = revision;
        NativeDigest = nativeDigest;
        ContentDigest = contentDigest;
        ByteCount = byteCount;
    }

    public Guid CaseId { get; }
    public long Version { get; }
    public PrincipalId Owner { get; }
    public RevisionScope Revision { get; }
    public Sha256Digest NativeDigest { get; }
    public Sha256Digest ContentDigest { get; }
    public int ByteCount { get; }
    public override string ToString() => nameof(RegressionCaseReference);
}
