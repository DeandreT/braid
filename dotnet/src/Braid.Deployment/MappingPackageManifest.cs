using System.Text.Json;
using Braid.Domain;

namespace Braid.Deployment;

/// <summary>
/// Portable identity of one immutable mapping artifact. This is metadata, not authorization:
/// consumers must compare it with an authenticated publication and recheck target authority.
/// </summary>
public sealed class MappingPackageManifest
{
    public const string SchemaVersion = "braid.mapping-package.v1";
    public const int MaximumBytes = 2 * 1024 * 1024;
    private readonly byte[] canonical;

    private MappingPackageManifest(BuildArtifactReference artifact)
    {
        Artifact = artifact;
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteString("schema", SchemaVersion);
            json.WriteString("artifact", BuildCompilation.Output);
            json.WriteString("sha256", artifact.ContentDigest.Value);
            json.WriteNumber("byteCount", artifact.ByteCount);
            json.WritePropertyName("intent");
            json.WriteRawValue(artifact.Intent.CopyCanonicalBytes());
            json.WriteEndObject();
        }
        canonical = output.ToArray();
        if (canonical.Length > MaximumBytes) throw Invalid();
        Fingerprint = Sha256Digest.FromBytes(canonical);
    }

    public BuildArtifactReference Artifact { get; }
    public Sha256Digest Fingerprint { get; }
    public byte[] CopyCanonicalBytes() => (byte[])canonical.Clone();

    public static MappingPackageManifest Create(BuildArtifactReference artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new(artifact);
    }

    /// <summary>Decoding proves canonical shape only, never publisher provenance or permission.</summary>
    public static MappingPackageManifest Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.GetProperty("schema").GetString() != SchemaVersion ||
                root.GetProperty("artifact").GetString() != BuildCompilation.Output) throw Invalid();
            var intent = BuildIntentCodec.Decode(System.Text.Encoding.UTF8.GetBytes(root.GetProperty("intent").GetRawText()));
            var artifact = BuildArtifactReference.Restore(intent,
                Sha256Digest.Parse(root.GetProperty("sha256").GetString() ?? throw Invalid()),
                root.GetProperty("byteCount").GetInt64());
            var manifest = new MappingPackageManifest(artifact);
            if (!bytes.SequenceEqual(manifest.canonical)) throw Invalid();
            return manifest;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { throw Invalid(); }
    }

    /// <summary>Bind untrusted package metadata to independently loaded publication facts and exact bytes.</summary>
    public bool Matches(BuildArtifactReference expected, BuildBytes bytes)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(bytes);
        return Artifact.Intent.Fingerprint == expected.Intent.Fingerprint &&
            Artifact.ContentDigest == expected.ContentDigest && Artifact.ByteCount == expected.ByteCount &&
            bytes.Digest == expected.ContentDigest && bytes.Length == expected.ByteCount;
    }

    public override string ToString() => nameof(MappingPackageManifest);
    private static BuildContractException Invalid() => new(BuildContractFailure.InvalidInput);
}
