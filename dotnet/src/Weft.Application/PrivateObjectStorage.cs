using Weft.Domain;

namespace Weft.Application;

public enum PrivateObjectPublishResult
{
    Created = 1,
    AlreadyPresent = 2
}

public enum PrivateObjectStorageError
{
    Missing = 1,
    ScopeMismatch = 2,
    Conflict = 3,
    Integrity = 4,
    Limit = 5,
    UnsafeStorage = 6,
    Io = 7,
    Unsupported = 8
}

/// <summary>Reports a bounded storage failure without physical paths or caller-supplied text.</summary>
public sealed class PrivateObjectStorageException : Exception
{
    public PrivateObjectStorageException(PrivateObjectStorageError error) : base(MessageFor(error)) => Error = error;

    public PrivateObjectStorageError Error { get; }

    private static string MessageFor(PrivateObjectStorageError error) => error switch
    {
        PrivateObjectStorageError.Missing => "The private object is unavailable.",
        PrivateObjectStorageError.ScopeMismatch => "The private object does not belong to the expected revision scope.",
        PrivateObjectStorageError.Conflict => "The private object already exists with different content.",
        PrivateObjectStorageError.Integrity => "The private object failed integrity verification.",
        PrivateObjectStorageError.Limit => "The private object exceeds a storage limit.",
        PrivateObjectStorageError.UnsafeStorage => "The private storage location does not meet safety requirements.",
        PrivateObjectStorageError.Io => "The private storage operation could not be completed.",
        PrivateObjectStorageError.Unsupported => "The private storage operation is not supported.",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "A private object storage error must be known.")
    };
}

/// <summary>
/// Stores private objects after the caller obtains fresh permission and trusted
/// scoped references. This boundary checks complete revision scope and integrity;
/// it does not establish authorization. Storage failures use
/// <see cref="PrivateObjectStorageException"/>; cancellation uses
/// <see cref="OperationCanceledException"/>.
/// </summary>
public interface IPrivateObjectStore
{
    /// <summary>
    /// Publishes without replacing an existing object. The expected scope must
    /// equal the reference's entire revision scope. The source is consumed from
    /// its current position and remains owned by the caller. Publication verifies
    /// the exact digest and byte count within adapter limits. An existing object
    /// must also be verified before returning AlreadyPresent; different content
    /// produces Conflict and never replaces existing bytes.
    /// </summary>
    Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
        PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns caller-owned bytes only after checking the complete expected scope,
    /// adapter limits, and the reference's exact digest and byte count. A missing
    /// object is a storage error rather than a successful empty result.
    /// </summary>
    Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference,
        CancellationToken cancellationToken = default);
}
