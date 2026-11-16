using System.Buffers.Binary;

namespace Braid.Domain;

/// <summary>Accepted project metadata with the unchanged revision-creation text rules.</summary>
public sealed record ProjectCreationMetadata
{
    public ProjectCreationMetadata(string name, string description)
    {
        var accepted = new IngestionCreateMetadata(name, description);
        Name = accepted.Name;
        Description = accepted.Description;
    }

    public string Name { get; }
    public string Description { get; }
}

/// <summary>
/// Exact version-one creation intent. Generated project IDs, clocks and current
/// authority are excluded; an immutable receipt must recompute this identity.
/// </summary>
public sealed record ProjectCreationFingerprint
{
    private ProjectCreationFingerprint(Sha256Digest digest) => Digest = digest;

    public int Version => 1;
    public Sha256Digest Digest { get; }

    public static ProjectCreationFingerprint Restore(int version, Sha256Digest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (version != 1)
            throw new ArgumentOutOfRangeException(nameof(version), "The project creation fingerprint version is unsupported.");
        return new(digest);
    }

    public static ProjectCreationFingerprint Create(Guid organizationId, PrincipalId originalActor,
        Guid idempotencyKey, ProjectCreationMetadata metadata)
    {
        IngestionValue.RequireId(organizationId, nameof(organizationId));
        ArgumentNullException.ThrowIfNull(originalActor);
        IngestionValue.RequireId(idempotencyKey, nameof(idempotencyKey));
        ArgumentNullException.ThrowIfNull(metadata);

        using var buffer = new MemoryStream();
        WriteText(buffer, "braid.project-creation-intent");
        WriteUInt32(buffer, 1);
        WriteId(buffer, organizationId);
        WriteId(buffer, originalActor.Value);
        WriteId(buffer, idempotencyKey);
        WriteText(buffer, metadata.Name);
        WriteText(buffer, metadata.Description);
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
            throw new InvalidOperationException("A project creation UUID could not be encoded.");
        destination.Write(bytes);
    }

    private static void WriteText(Stream destination, string value)
    {
        var bytes = IngestionValue.StrictUtf8.GetBytes(value);
        WriteUInt32(destination, (uint)bytes.Length);
        destination.Write(bytes);
    }
}
