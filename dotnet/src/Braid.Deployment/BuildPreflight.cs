using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Deployment;

/// <summary>Capability facts from a trusted backend adapter, not from a browser or model.</summary>
public sealed class BuildCapabilitySnapshot
{
    public BuildCapabilitySnapshot(RevisionScope revision, Sha256Digest nativeDigest, BuildBackendIdentity backend,
        BuildRuntimeProfile profile, IReadOnlyList<BuildBoundary> boundaries, CapabilityStatus status,
        IReadOnlyList<string>? conditions = null, BuildPreflightReason refusalReason = BuildPreflightReason.None)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(status)) throw new BuildContractException(BuildContractFailure.InvalidInput);
        if (refusalReason != BuildPreflightReason.None && (status != CapabilityStatus.Unsupported || refusalReason is not
            (BuildPreflightReason.UnsupportedCsvPlan or BuildPreflightReason.UnsupportedXmlBuild or BuildPreflightReason.UnsupportedFlexTextBuild or
             BuildPreflightReason.UnsupportedXlsxBuild or BuildPreflightReason.UnsupportedProtobufBuild or BuildPreflightReason.UnsupportedXbrlBuild)))
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        var count = conditions?.Count ?? 0;
        if (count is < 0 or > 32 || (status == CapabilityStatus.Conditional) != (count > 0))
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        var ownedConditions = new string[count];
        for (var index = 0; index < count; index++) ownedConditions[index] = BuildValues.Text(conditions![index], 128);
        Revision = revision;
        NativeDigest = nativeDigest;
        Backend = backend;
        Profile = profile;
        Boundaries = BuildValues.Boundaries(boundaries);
        Status = status;
        Conditions = Array.AsReadOnly(ownedConditions);
        RefusalReason = refusalReason;
    }

    public RevisionScope Revision { get; }
    public Sha256Digest NativeDigest { get; }
    public BuildBackendIdentity Backend { get; }
    public BuildRuntimeProfile Profile { get; }
    public IReadOnlyList<BuildBoundary> Boundaries { get; }
    public CapabilityStatus Status { get; }
    public IReadOnlyList<string> Conditions { get; }
    public BuildPreflightReason RefusalReason { get; }
    public override string ToString() => nameof(BuildCapabilitySnapshot);
}

public enum BuildPreflightOutcome { Supported, Unsupported, NotQualified, ConditionsRequired, MismatchedEvidence }
public enum BuildPreflightReason { None, RevisionMismatch, NativeDigestMismatch, BackendMismatch, ProfileMismatch, BoundaryMismatch, UnsupportedCsvPlan, UnsupportedXmlBuild, UnsupportedFlexTextBuild, UnsupportedXlsxBuild, UnsupportedProtobufBuild, UnsupportedXbrlBuild }

public sealed record BuildPreflightResult(BuildPreflightOutcome Outcome, BuildPreflightReason Reason)
{
    public override string ToString() => $"{nameof(BuildPreflightResult)}:{Outcome}:{Reason}";
}

/// <summary>Pure compatibility check. A Supported result is not authorization or permission to run a worker.</summary>
public sealed class BuildPreflightService
{
    public static BuildPreflightReason DocumentRefusal(BuildIntent intent)
    {
        foreach (var boundary in intent.Boundaries)
        {
            var refusal = boundary.Format switch
            {
                "xml" when intent.Profile.Id != BuildWorkerProtocol.XmlProfile => BuildPreflightReason.UnsupportedXmlBuild,
                "flexText" => BuildPreflightReason.UnsupportedFlexTextBuild,
                "xlsx" => BuildPreflightReason.UnsupportedXlsxBuild,
                "protobuf" => BuildPreflightReason.UnsupportedProtobufBuild,
                "xbrl" => BuildPreflightReason.UnsupportedXbrlBuild,
                _ => BuildPreflightReason.None
            };
            if (refusal != BuildPreflightReason.None) return refusal;
        }
        return BuildPreflightReason.None;
    }

    public BuildPreflightResult Evaluate(BuildIntent intent, BuildCapabilitySnapshot capability,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(capability);
        var refusal = DocumentRefusal(intent);
        if (refusal != BuildPreflightReason.None) return new(BuildPreflightOutcome.Unsupported, refusal);
        if (intent.Revision != capability.Revision) return Mismatch(BuildPreflightReason.RevisionMismatch);
        if (intent.NativeDigest != capability.NativeDigest) return Mismatch(BuildPreflightReason.NativeDigestMismatch);
        if (intent.Backend != capability.Backend) return Mismatch(BuildPreflightReason.BackendMismatch);
        if (intent.Profile != capability.Profile) return Mismatch(BuildPreflightReason.ProfileMismatch);
        if (intent.Boundaries.Count != capability.Boundaries.Count) return Mismatch(BuildPreflightReason.BoundaryMismatch);
        for (var index = 0; index < intent.Boundaries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (intent.Boundaries[index] != capability.Boundaries[index]) return Mismatch(BuildPreflightReason.BoundaryMismatch);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(capability.Status switch
        {
            CapabilityStatus.Supported => BuildPreflightOutcome.Supported,
            CapabilityStatus.Unsupported => BuildPreflightOutcome.Unsupported,
            CapabilityStatus.NotQualified => BuildPreflightOutcome.NotQualified,
            CapabilityStatus.Conditional => BuildPreflightOutcome.ConditionsRequired,
            _ => throw new BuildContractException(BuildContractFailure.InvalidInput)
        }, capability.RefusalReason);
    }

    private static BuildPreflightResult Mismatch(BuildPreflightReason reason) => new(BuildPreflightOutcome.MismatchedEvidence, reason);
    public override string ToString() => nameof(BuildPreflightService);
}
