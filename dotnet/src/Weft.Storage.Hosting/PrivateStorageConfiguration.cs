using Weft.Application;
using Weft.Domain;
using Weft.Storage.Blob;
using Weft.Storage.FileSystem;

namespace Weft.Storage.Hosting;

/// <summary>One host's bound private storage: exactly one claim-aware store kind and its options.</summary>
public sealed record PrivateStorageConfiguration
{
    private PrivateStorageConfiguration(FileSystemStorageOptions? fileSystem, BlobStorageOptions? blob)
    {
        FileSystem = fileSystem;
        Blob = blob;
    }

    public PrivateStorageKind Kind => FileSystem is not null ? PrivateStorageKind.FileSystem : PrivateStorageKind.AzureBlob;
    public FileSystemStorageOptions? FileSystem { get; }
    public BlobStorageOptions? Blob { get; }
    public long NativeByteLimit => FileSystem?.NativeByteLimit ?? Blob!.NativeByteLimit;
    public long DerivedByteLimit => FileSystem?.DerivedByteLimit ?? Blob!.DerivedByteLimit;

    public static PrivateStorageConfiguration ForFileSystem(FileSystemStorageOptions options) =>
        new(options ?? throw new ArgumentNullException(nameof(options)), null);

    public static PrivateStorageConfiguration ForBlob(BlobStorageOptions options) =>
        new(null, options ?? throw new ArgumentNullException(nameof(options)));

    /// <summary>Admits an existing initialized store. It never initializes, activates or adopts storage.</summary>
    public async Task<PrivateObjectStoreHandle> OpenAsync(Func<IPrivateObjectPublicationRegistry>? registryFactory = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return FileSystem is { } options
            ? new(FileSystemV2PrivateObjectStore.Open(options, registryFactory))
            : new(await BlobPrivateObjectStore.OpenAsync(Blob!, registryFactory, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Explicit provisioning of a fresh claim-aware store for an operator tool.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FileSystem is { } options)
        {
            using var store = FileSystemV2PrivateObjectStore.Initialize(options);
            return;
        }
        await using var blob = await BlobPrivateObjectStore.InitializeAsync(Blob!, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>One opened claim-aware store of either kind, behind its public capabilities.</summary>
public sealed class PrivateObjectStoreHandle : IPrivateObjectPublicationStore, IPrivateObjectRetirementStore,
    IPrivateObjectStorageObserver, IDisposable, IAsyncDisposable
{
    private readonly IPrivateObjectPublicationStore _publication;
    private readonly IPrivateObjectRetirementStore _retirement;
    private readonly IPrivateObjectStorageObserver _observer;
    private readonly IAsyncDisposable _lifetime;

    internal PrivateObjectStoreHandle(FileSystemV2PrivateObjectStore store)
    {
        _publication = store;
        _retirement = store;
        _observer = store;
        _lifetime = store;
    }

    internal PrivateObjectStoreHandle(BlobPrivateObjectStore store)
    {
        _publication = store;
        _retirement = store;
        _observer = store;
        _lifetime = store;
    }

    public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope, PrivateObjectReference reference,
        Stream source, CancellationToken cancellationToken = default) =>
        _publication.PublishCreateOnlyAsync(expectedScope, reference, source, cancellationToken);

    public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(PrivatePublicationOwner owner, RevisionScope expectedScope,
        PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) =>
        _publication.PublishCreateOnlyAsync(owner, expectedScope, reference, source, cancellationToken);

    public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference,
        CancellationToken cancellationToken = default) =>
        _publication.ReadVerifiedAsync(expectedScope, reference, cancellationToken);

    public Task<PrivateObjectRetirementOutcome> RetireAsync(ObjectCleanupClaim claim, CancellationToken cancellationToken = default) =>
        _retirement.RetireAsync(claim, cancellationToken);

    public Task<PrivateStorageObservationPage> ObserveAsync(ProjectScope project, CancellationToken cancellationToken = default) =>
        _observer.ObserveAsync(project, cancellationToken);

    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
    public void Dispose() => _lifetime.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
