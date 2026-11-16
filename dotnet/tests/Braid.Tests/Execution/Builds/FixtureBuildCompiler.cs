using System.Buffers.Binary;
using System.Text;
using Braid.Contracts.Projections;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Deterministic test artifact, not an executable, Ferrule compilation, or an Azure package.</summary>
internal sealed class FixtureBuildCompiler : IBuildCompiler
{
    public const string BackendId = "braid-fixture";
    public const string ProfileId = "braid.fixture-artifact.v1";
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static BuildBackendIdentity Backend { get; } = new(BackendId, "fixture-engine.v1", "fixture-build.v1", "fixture-adapter.v1");
    public static BuildRuntimeProfile Profile { get; } = new(ProfileId, 1, "fixture", "1", ProfileId, MaximumBytes);
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BRAIDFIX1");
    private const int FingerprintOffset = 9;
    private const int NativeDigestOffset = FingerprintOffset + 64;
    private const int NativeLengthOffset = NativeDigestOffset + 64;
    private const int HeaderBytes = NativeLengthOffset + 4;

    public Task<BuildCapabilitySnapshot> InspectAsync(BuildIntent intent, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        token.ThrowIfCancellationRequested();
        var supported = intent.Backend == Backend && intent.Profile == Profile && intent.Boundaries.All(b => b.Format == "fixture-bytes");
        return Task.FromResult(new BuildCapabilitySnapshot(intent.Revision, intent.NativeDigest, Backend, Profile, intent.Boundaries,
            supported ? CapabilityStatus.Supported : CapabilityStatus.Unsupported));
    }

    public async Task<BuildBytes> BuildAsync(BuildIntent intent, BuildBytes native, CancellationToken token = default)
    {
        if (new BuildPreflightService().Evaluate(intent, await InspectAsync(intent, token).ConfigureAwait(false), token).Outcome != BuildPreflightOutcome.Supported)
            throw new BuildContractException(BuildContractFailure.CapabilityUnavailable);
        if (native.Digest != intent.NativeDigest || native.Length > MaximumBytes - HeaderBytes)
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        var output = new byte[HeaderBytes + native.Length];
        Magic.CopyTo(output, 0);
        Encoding.ASCII.GetBytes(intent.Fingerprint.Value).CopyTo(output, FingerprintOffset);
        Encoding.ASCII.GetBytes(intent.NativeDigest.Value).CopyTo(output, NativeDigestOffset);
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(NativeLengthOffset), native.Length);
        native.Copy().CopyTo(output, HeaderBytes);
        token.ThrowIfCancellationRequested();
        return new(output, MaximumBytes);
    }

    public static BuildBytes ReadVerified(BuildIntent intent, ReadOnlySpan<byte> artifact)
    {
        if (artifact.Length <= HeaderBytes || artifact.Length > MaximumBytes || !artifact[..FingerprintOffset].SequenceEqual(Magic) ||
            Encoding.ASCII.GetString(artifact.Slice(FingerprintOffset, 64)) != intent.Fingerprint.Value ||
            Encoding.ASCII.GetString(artifact.Slice(NativeDigestOffset, 64)) != intent.NativeDigest.Value ||
            BinaryPrimitives.ReadInt32BigEndian(artifact.Slice(NativeLengthOffset, 4)) != artifact.Length - HeaderBytes ||
            Sha256Digest.FromBytes(artifact[HeaderBytes..]) != intent.NativeDigest)
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        return new(artifact[HeaderBytes..], MaximumBytes);
    }
}
