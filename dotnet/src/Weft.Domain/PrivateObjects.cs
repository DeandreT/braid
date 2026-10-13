namespace Weft.Domain;

public enum PrivateObjectClass
{
    NativeArtifact = 1,
    DerivedArtifact = 2
}

/// <summary>
/// A Weft-generated identity within a complete revision scope. It is not a
/// physical storage key, a path, or evidence that its holder has access.
/// </summary>
public sealed record PrivateObjectId
{
    private PrivateObjectId(RevisionScope scope, Guid objectId, PrivateObjectClass @class)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (objectId == Guid.Empty)
            throw new ArgumentException("A private object identifier cannot be empty.", nameof(objectId));
        if (!Enum.IsDefined(@class))
            throw new ArgumentOutOfRangeException(nameof(@class), @class, "A private object class must be known.");

        Scope = scope;
        ObjectId = objectId;
        Class = @class;
    }

    public RevisionScope Scope { get; }
    public Guid ObjectId { get; }
    public PrivateObjectClass Class { get; }

    public static PrivateObjectId Create(RevisionScope scope, PrivateObjectClass @class) =>
        new(scope, Guid.NewGuid(), @class);

    /// <summary>Reconstitutes typed identity facts obtained from trusted persisted metadata.</summary>
    public static PrivateObjectId Restore(RevisionScope scope, Guid objectId, PrivateObjectClass @class) =>
        new(scope, objectId, @class);
}

/// <summary>
/// The expected identity, exact-byte digest, and byte count of a private object.
/// Store-specific limits and nonempty native-artifact policy are enforced by the adapter.
/// </summary>
public sealed record PrivateObjectReference
{
    public PrivateObjectReference(PrivateObjectId id, Sha256Digest digest, long byteCount)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        Id = id;
        Digest = digest;
        ByteCount = byteCount;
    }

    public PrivateObjectId Id { get; }
    public Sha256Digest Digest { get; }
    public long ByteCount { get; }
}
