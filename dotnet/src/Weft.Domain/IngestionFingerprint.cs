using System.Buffers.Binary;

namespace Weft.Domain;

/// <summary>Versioned exact intent identity; reserved deployment profiles and generated IDs are excluded.</summary>
public sealed record CanonicalFingerprint
{
    private CanonicalFingerprint(Sha256Digest digest) => Digest = digest;

    public int Version => 1;
    public Sha256Digest Digest { get; }

    public static CanonicalFingerprint Restore(int version, Sha256Digest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (version != 1)
            throw new ArgumentOutOfRangeException(nameof(version), "The ingestion fingerprint version is unsupported.");
        return new(digest);
    }

    public static CanonicalFingerprint Create(PrincipalId originalActor, ProjectScope project, IngestionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(originalActor);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Target is { } target &&
            (target.OrganizationId != project.OrganizationId || target.ProjectId != project.ProjectId))
            throw new ArgumentException("The append target must belong to the intent's project.", nameof(intent));

        using var buffer = new MemoryStream();
        WriteText(buffer, "weft.ingestion-intent");
        WriteUInt32(buffer, 1);
        WriteUInt32(buffer, (uint)intent.Kind);
        WriteId(buffer, project.OrganizationId);
        WriteId(buffer, project.ProjectId);
        WriteId(buffer, originalActor.Value);
        buffer.WriteByte(intent.Target is null ? (byte)0 : (byte)1);
        if (intent.Target is { } mapping)
            WriteId(buffer, mapping.MappingId);
        switch (intent.HeadCondition)
        {
            case AnyHead: buffer.WriteByte(0); break;
            case EqualHead equal: buffer.WriteByte(1); WriteId(buffer, equal.RevisionId); break;
            default: throw new ArgumentException("The head condition must be known.", nameof(intent));
        }
        WriteText(buffer, intent.BackendId);
        WriteText(buffer, intent.LogicalDocument);
        WriteText(buffer, intent.DependencyIdentity);
        WriteText(buffer, intent.OriginalFilename);
        WriteNullableText(buffer, intent.CreateMetadata?.Name);
        WriteNullableText(buffer, intent.CreateMetadata?.Description);
        buffer.Write(Convert.FromHexString(intent.NativeDigest.Value));
        Span<byte> count = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(count, (ulong)intent.NativeByteCount);
        buffer.Write(count);
        // Preserve v1 native-only fingerprints exactly. Imported intents extend the domain
        // with a tagged digest binding original bytes, conversion engine and reviewed report.
        if (intent.ImportProvenance is { } provenance)
        {
            WriteText(buffer, "weft.interchange-provenance.v1");
            buffer.Write(Convert.FromHexString(provenance.Digest.Value));
        }
        return new(Sha256Digest.FromBytes(buffer.ToArray()));
    }

    private static void WriteUInt32(Stream destination, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        destination.Write(bytes);
    }

    private static void WriteId(Stream destination, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out var written) || written != bytes.Length)
            throw new InvalidOperationException("An ingestion UUID could not be encoded.");
        destination.Write(bytes);
    }

    private static void WriteText(Stream destination, string value)
    {
        var bytes = IngestionValue.StrictUtf8.GetBytes(value);
        WriteUInt32(destination, (uint)bytes.Length);
        destination.Write(bytes);
    }

    private static void WriteNullableText(Stream destination, string? value)
    {
        destination.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null)
            WriteText(destination, value);
    }
}
