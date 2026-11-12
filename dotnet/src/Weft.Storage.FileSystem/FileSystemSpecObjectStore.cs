using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.FileSystem;

/// <summary>
/// Explicit unavailable adapter, not a path-based fallback. Existing descriptor-relative v2 publication records and
/// retirement seals are revision-owned. Bundle publication requires its own durable temporary-file ownership and
/// fenced recovery protocol before those primitives can safely be enabled. Windows reparse points and Linux symlinks
/// are never followed: this capability performs no filesystem I/O on any platform. Revision storage is unchanged.
/// </summary>
public sealed class FileSystemSpecObjectStore : SpecObjectStore
{
    public FileSystemSpecObjectStore(FileSystemStorageOptions options, Guid storeId, ISpecObjectRegistry? registry)
        : base(storeId, registry) => ArgumentNullException.ThrowIfNull(options);

    protected override void RequireCapability() => throw new SpecStorageException(SpecStorageError.Unavailable);
    protected override Task<SpecPublishResult> PublishCoreAsync(SpecPublicationClaim claim, byte[] verifiedBytes, CancellationToken token) =>
        throw new SpecStorageException(SpecStorageError.Unavailable);
    protected override Task<byte[]> ReadCoreAsync(SpecObjectReference reference, CancellationToken token) =>
        throw new SpecStorageException(SpecStorageError.Unavailable);
    protected override Task<SpecRetireResult> RetireCoreAsync(SpecRetirementClaim claim, CancellationToken token) =>
        throw new SpecStorageException(SpecStorageError.Unavailable);
}
