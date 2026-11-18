using Braid.Domain;

namespace Braid.Application.Regression;

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
        var native = NativeDocumentFormat.FromProfile(runtime.Profile);
        if (NativeDocumentFormat.FromExecution(replay.Profile) != native || native is not null && (comparisonPolicy != RegressionComparisonService.ExactBytesPolicy || expected.Outputs.Count != 1 || !expected.Outputs[0].Key.IsPrimary || expected.Outputs[0].DocumentSettings?.Format != native) || native is null && expected.Outputs.Any(o => o.DocumentSettings is not null)) throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        if (native is null && runtime.Profile is not (PreviewPolicy.Profile or DocumentPreviewPolicy.Profile or CsvDocumentPolicy.Profile or XmlPreviewAdmission.Profile) ||
            comparisonPolicy is not (RegressionComparisonService.ExactBytesPolicy or RegressionComparisonService.EdiSegmentsPolicy or RegressionComparisonService.CsvRowsPolicy) ||
            (comparisonPolicy == RegressionComparisonService.EdiSegmentsPolicy) != (ediOptions is not null) ||
            comparisonPolicy == RegressionComparisonService.CsvRowsPolicy && runtime.Profile != CsvDocumentPolicy.Profile ||
            runtime.Profile == CsvDocumentPolicy.Profile && (ediOptions is not null || replay.Profile != PreviewExecutionProfile.CsvDocument ||
                replay.Primary.CsvDialect is null || replay.NamedInputs.Count != 0 || replay.Target.Kind == PreviewTargetKind.Named || expected.Outputs.Count != 1 ||
                !expected.Outputs[0].Key.IsPrimary || expected.Outputs[0].CsvDialect is null) ||
            runtime.Profile != CsvDocumentPolicy.Profile && (replay.Profile == PreviewExecutionProfile.CsvDocument || expected.Outputs.Any(output => output.CsvDialect is not null)) ||
            runtime.Profile == PreviewPolicy.Profile && (comparisonPolicy != RegressionComparisonService.ExactBytesPolicy || expected.Outputs.Any(output => output.MediaType != PreviewPolicy.MediaType)))
            throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        // XML regression is exact bytes only, with role-valid metadata on every expected output.
        if (runtime.Profile == XmlPreviewAdmission.Profile ?
            replay.Profile != PreviewExecutionProfile.XmlDocument || comparisonPolicy != RegressionComparisonService.ExactBytesPolicy || ediOptions is not null ||
            expected.Outputs.Any(output => output.XmlMetadata is null) :
            replay.Profile == PreviewExecutionProfile.XmlDocument || expected.Outputs.Any(output => output.XmlMetadata is not null))
            throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        if (runtime.Profile == XmlPreviewAdmission.Profile)
        {
            try
            {
                XmlDocumentMetadataJson.MeasureOperation(replay.NamedInputs.Prepend(replay.Primary).Select(input => input.XmlMetadata!).ToArray(),
                    expected.Outputs.Select(output => output.XmlMetadata!).ToArray());
            }
            catch (XmlDocumentException error)
            {
                throw new RegressionCaseException(error.IsResourceLimit ? RegressionCaseFailure.ResourceLimit : RegressionCaseFailure.InvalidInput);
            }
        }
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
        if (NativeDocumentFormat.FromProfile(Runtime.Profile) is { } native && NativeDocumentNativeSettings.Read(artifact, native) != (Replay.Primary.DocumentSettings, Expected.Outputs.Single().DocumentSettings)) throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        if (Runtime.Profile == CsvDocumentPolicy.Profile)
        {
            var dialect = CsvNativeDialect.Read(artifact);
            if (dialect.Output != Expected.Outputs.Single().CsvDialect || dialect.Input != Replay.Primary.CsvDialect)
                throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        }
        if (Runtime.Profile == XmlPreviewAdmission.Profile)
        {
            try
            {
                if (!XmlPreviewAdmission.MatchesCase(artifact, Replay, Expected)) throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
            }
            catch (XmlDocumentException error)
            {
                throw new RegressionCaseException(error.IsResourceLimit ? RegressionCaseFailure.ResourceLimit : RegressionCaseFailure.InvalidInput);
            }
        }
        return new(artifact, Replay.Target, Replay.Primary, Replay.NamedInputs, Replay.Parameters,
            Replay.CurrentDateTime, Replay.Limits)
        {
            Profile = NativeDocumentFormat.FromProfile(Runtime.Profile)?.Execution ?? Runtime.Profile switch
            {
                XmlPreviewAdmission.Profile => PreviewExecutionProfile.XmlDocument,
                CsvDocumentPolicy.Profile => PreviewExecutionProfile.CsvDocument,
                DocumentPreviewPolicy.Profile => PreviewExecutionProfile.Document,
                _ => PreviewExecutionProfile.LegacyJson
            }
        };
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
