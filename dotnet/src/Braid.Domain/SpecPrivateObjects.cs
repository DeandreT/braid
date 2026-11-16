namespace Braid.Domain;

public enum SpecStoreKind { Blob = 1, FileSystem = 2 }

/// <summary>Owner-provisioned physical location fingerprint; not a caller access grant.</summary>
public sealed record SpecStoreBinding
{
    public SpecStoreBinding(Guid storeId, SpecStoreKind kind, Sha256Digest locationDigest)
    {
        if (storeId == Guid.Empty || !Enum.IsDefined(kind)) throw new ArgumentException("A physical store binding is required.");
        ArgumentNullException.ThrowIfNull(locationDigest);
        StoreId = storeId; Kind = kind; LocationDigest = locationDigest;
    }
    public Guid StoreId { get; }
    public SpecStoreKind Kind { get; }
    public Sha256Digest LocationDigest { get; }
}

public enum SpecObjectKind { Original = 1, Extraction = 2 }

/// <summary>Opaque project/bundle-owned identity. Never a revision, filename, physical path or authority.</summary>
public sealed record SpecObjectId
{
    public SpecObjectId(SpecSourceIdentity source, SpecObjectKind kind, Guid objectId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (objectId == Guid.Empty) throw new ArgumentException("An object identifier is required.", nameof(objectId));
        Source = source;
        Kind = kind;
        ObjectId = objectId;
    }
    public SpecSourceIdentity Source { get; }
    public SpecObjectKind Kind { get; }
    public Guid ObjectId { get; }
}

public sealed record SpecObjectReference
{
    public const int MaximumBytes = SpecCapturedSource.MaximumBytes;
    public SpecObjectReference(SpecObjectId id, Sha256Digest digest, int byteCount)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(digest);
        if (byteCount is < 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(byteCount));
        Id = id;
        Digest = digest;
        ByteCount = byteCount;
    }
    public SpecObjectId Id { get; }
    public Sha256Digest Digest { get; }
    public int ByteCount { get; }
}

/// <summary>Caller-known recovery key; the registry must bind it to these exact immutable facts before any publication.</summary>
public sealed record SpecPublicationClaim
{
    public SpecPublicationClaim(Guid storeId, Guid attemptId, SpecObjectReference reference)
    {
        if (storeId == Guid.Empty || attemptId == Guid.Empty) throw new ArgumentException("Publication identities are required.");
        ArgumentNullException.ThrowIfNull(reference);
        StoreId = storeId;
        AttemptId = attemptId;
        Reference = reference;
    }
    public Guid StoreId { get; }
    public Guid AttemptId { get; }
    public SpecObjectReference Reference { get; }
}

/// <summary>Exact durable retirement, issued after tombstoning; possession does not authorize deletion.</summary>
public sealed record SpecRetirementClaim
{
    public SpecRetirementClaim(Guid storeId, Guid retirementId, SpecObjectReference reference)
    {
        if (storeId == Guid.Empty || retirementId == Guid.Empty) throw new ArgumentException("Retirement identities are required.");
        ArgumentNullException.ThrowIfNull(reference);
        StoreId = storeId;
        RetirementId = retirementId;
        Reference = reference;
    }
    public Guid StoreId { get; }
    public Guid RetirementId { get; }
    public SpecObjectReference Reference { get; }
}
