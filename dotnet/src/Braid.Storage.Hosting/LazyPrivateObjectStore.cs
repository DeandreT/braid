using Braid.Application;
using Braid.Domain;
using Braid.Storage.FileSystem;

namespace Braid.Storage.Hosting;

/// <summary>Opens only at actual object I/O; metadata receipt and replay resolution perform no root probes.</summary>
public sealed class LazyPrivateObjectStore : IPrivateObjectPublicationStore, IDisposable, IAsyncDisposable
{
    private readonly PrivateStorageConfiguration configuration;
    private readonly Func<IPrivateObjectPublicationRegistry>? registryFactory;
    private readonly SemaphoreSlim opening = new(1, 1);
    private readonly object lifetime = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PrivateObjectStoreHandle? store;
    private int operations;
    private bool disposing;
    private Task? disposal;

    public LazyPrivateObjectStore(PrivateStorageConfiguration configuration,
        Func<IPrivateObjectPublicationRegistry>? registryFactory = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.registryFactory = registryFactory;
    }

    public LazyPrivateObjectStore(FileSystemStorageOptions options,
        Func<IPrivateObjectPublicationRegistry>? registryFactory = null)
        : this(PrivateStorageConfiguration.ForFileSystem(options), registryFactory) { }

    public PrivateStorageConfiguration Configuration => configuration;

    public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
        PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
    {
        ValidateReference(expectedScope, reference);
        ArgumentNullException.ThrowIfNull(source);
        BeginOperation(cancellationToken);
        try
        {
            // A capability-dropping caller cannot obtain an untracked host write.
            // This refusal needs no root opening or publication-registry resolution.
            return Task.FromException<PrivateObjectPublishResult>(
                new PrivateObjectStorageException(PrivateObjectStorageError.Unsupported));
        }
        finally { EndOperation(); }
    }

    public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(PrivatePublicationOwner owner,
        RevisionScope expectedScope,
        PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateReference(expectedScope, reference);
        ArgumentNullException.ThrowIfNull(source);
        _ = owner.OriginFor(reference.Id);
        BeginOperation(cancellationToken);
        try
        {
            var current = await OpenAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await current.PublishCreateOnlyAsync(owner, expectedScope, reference, source, cancellationToken).ConfigureAwait(false);
        }
        finally { EndOperation(); }
    }

    public async Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope,
        PrivateObjectReference reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(expectedScope, reference);
        BeginOperation(cancellationToken);
        try
        {
            var current = await OpenAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await current.ReadVerifiedAsync(expectedScope, reference, cancellationToken).ConfigureAwait(false);
        }
        finally { EndOperation(); }
    }

    private void ValidateReference(RevisionScope scope, PrivateObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reference);
        if (scope != reference.Id.Scope)
            throw new PrivateObjectStorageException(PrivateObjectStorageError.ScopeMismatch);
        var limit = reference.Id.Class == PrivateObjectClass.NativeArtifact ? configuration.NativeByteLimit : configuration.DerivedByteLimit;
        if (reference.ByteCount > limit || (reference.Id.Class == PrivateObjectClass.NativeArtifact && reference.ByteCount == 0))
            throw new PrivateObjectStorageException(PrivateObjectStorageError.Limit);
    }

    private void BeginOperation(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            operations++;
        }
    }

    private void EndOperation()
    {
        lock (lifetime)
        {
            operations--;
            if (disposing && operations == 0) drained.TrySetResult();
        }
    }

    private async Task<PrivateObjectStoreHandle> OpenAsync(CancellationToken cancellationToken)
    {
        await opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Assignment happens only after a complete successful Open. Failed
            // validation is not cached, and a host never initializes/adopts.
            return store ??= await configuration.OpenAsync(registryFactory, cancellationToken).ConfigureAwait(false);
        }
        finally { opening.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetime)
        {
            if (disposal is null)
            {
                disposing = true;
                if (operations == 0) drained.TrySetResult();
                disposal = DisposeAfterOperationsAsync();
            }
            return new(disposal);
        }
    }

    private async Task DisposeAfterOperationsAsync()
    {
        await drained.Task.ConfigureAwait(false);
        if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
        opening.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
