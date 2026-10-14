using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Server;

/// <summary>Opens only at actual object I/O; metadata receipt and replay resolution perform no root probes.</summary>
public sealed class LazyPrivateObjectStore : IPrivateObjectStore, IDisposable, IAsyncDisposable
{
    private readonly FileSystemStorageOptions options;
    private readonly SemaphoreSlim opening = new(1, 1);
    private readonly object lifetime = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileSystemPrivateObjectStore? store;
    private int operations;
    private bool disposing;
    private Task? disposal;

    public LazyPrivateObjectStore(FileSystemStorageOptions options) =>
        this.options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
        PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
    {
        ValidateReference(expectedScope, reference);
        ArgumentNullException.ThrowIfNull(source);
        BeginOperation(cancellationToken);
        try
        {
            var current = await OpenAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await current.PublishCreateOnlyAsync(expectedScope, reference, source, cancellationToken);
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
            var current = await OpenAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await current.ReadVerifiedAsync(expectedScope, reference, cancellationToken);
        }
        finally { EndOperation(); }
    }

    private void ValidateReference(RevisionScope scope, PrivateObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reference);
        if (scope != reference.Id.Scope)
            throw new PrivateObjectStorageException(PrivateObjectStorageError.ScopeMismatch);
        var limit = reference.Id.Class == PrivateObjectClass.NativeArtifact ? options.NativeByteLimit : options.DerivedByteLimit;
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

    private async Task<FileSystemPrivateObjectStore> OpenAsync(CancellationToken cancellationToken)
    {
        await opening.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Assignment happens only after a complete successful Open. Failed
            // validation is not cached, and the web host never initializes/adopts.
            return store ??= FileSystemPrivateObjectStore.Open(options);
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
        await drained.Task;
        if (store is not null) await store.DisposeAsync();
        opening.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
