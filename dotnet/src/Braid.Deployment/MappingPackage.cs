using System.Buffers.Binary;

namespace Braid.Deployment;

/// <summary>Portable manifest and DLL. Decoding proves consistency, not publication or authority.</summary>
public sealed class MappingPackage
{
    public MappingPackage(MappingPackageManifest manifest, BuildBytes bytes)
    {
        if (!manifest.Matches(manifest.Artifact, bytes)) throw Invalid();
        Manifest = manifest;
        Bytes = bytes;
    }

    public MappingPackageManifest Manifest { get; }
    public BuildBytes Bytes { get; }

    public async Task WriteAsync(Stream destination, CancellationToken token)
    {
        var metadata = Manifest.CopyCanonicalBytes();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, metadata.Length);
        await destination.WriteAsync(header, token);
        await destination.WriteAsync(metadata, token);
        await destination.WriteAsync(Bytes.Copy(), token);
    }

    public static async Task<MappingPackage> ReadAsync(Stream source, long maximumArtifactBytes, CancellationToken token)
    {
        if (maximumArtifactBytes is < 1 or > BuildRuntimeProfile.MaximumAllowedArtifactBytes) throw Invalid();
        var header = new byte[4];
        try
        {
            await source.ReadExactlyAsync(header, token);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is < 1 or > MappingPackageManifest.MaximumBytes) throw Invalid();
            var metadata = new byte[length];
            await source.ReadExactlyAsync(metadata, token);
            var manifest = MappingPackageManifest.Decode(metadata);
            if (manifest.Artifact.ByteCount > maximumArtifactBytes) throw Invalid();
            var data = new byte[(int)manifest.Artifact.ByteCount];
            await source.ReadExactlyAsync(data, token);
            if (await source.ReadAsync(header, token) != 0) throw Invalid();
            return new(manifest, new(data, maximumArtifactBytes));
        }
        catch (EndOfStreamException) { throw Invalid(); }
    }

    private static BuildContractException Invalid() => new(BuildContractFailure.InvalidInput);
}
