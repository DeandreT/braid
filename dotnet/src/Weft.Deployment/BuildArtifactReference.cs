using Weft.Domain;

namespace Weft.Deployment;

/// <summary>
/// Immutable artifact metadata bound to one complete intent. This is not proof that bytes exist or were verified.
/// The build/store boundary must verify exact content, and deployment must recheck authority and approvals.
/// </summary>
public sealed class BuildArtifactReference
{
    private BuildArtifactReference(BuildIntent intent, Sha256Digest contentDigest, long byteCount)
    {
        Intent = intent;
        ContentDigest = contentDigest;
        ByteCount = byteCount;
    }

    public BuildIntent Intent { get; }
    public Sha256Digest ContentDigest { get; }
    public long ByteCount { get; }

    public static BuildArtifactReference Create(BuildIntent intent, BuildCapabilitySnapshot capability,
        Sha256Digest contentDigest, long byteCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(contentDigest);
        var preflight = new BuildPreflightService().Evaluate(intent, capability, cancellationToken);
        if (preflight.Outcome != BuildPreflightOutcome.Supported)
            throw new BuildContractException(BuildContractFailure.CapabilityUnavailable);
        if (byteCount < 1 || byteCount > intent.Profile.MaximumArtifactBytes)
            throw new BuildContractException(BuildContractFailure.ResourceLimit);
        cancellationToken.ThrowIfCancellationRequested();
        return new(intent, contentDigest, byteCount);
    }

    public bool MatchesIntent(BuildIntent expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return Intent.Fingerprint == expected.Fingerprint;
    }

    public override string ToString() => nameof(BuildArtifactReference);
}
