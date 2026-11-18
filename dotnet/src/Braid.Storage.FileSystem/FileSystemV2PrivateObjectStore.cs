using Microsoft.Win32.SafeHandles;
using Braid.Application;
using Braid.Application.Lifecycle;
using Braid.Domain;

namespace Braid.Storage.FileSystem;

/// <summary>
/// The unactivated v2 local component. Publication retains the actual original
/// workflow claim; physical retirement consumes an already issued retirement.
/// </summary>
public sealed partial class FileSystemV2PrivateObjectStore : IPrivateObjectPublicationStore,
    IPrivateObjectRetirementStore, IPrivateObjectDeletionStore, IDisposable, IAsyncDisposable
{
    /// <summary>The explicit kind this store registers; it selects the filesystem publication format.</summary>
    public const PrivatePublicationStoreKind PublicationStoreKind = PrivatePublicationStoreKind.FileSystem;
    private const int ChunkSize = 64 * 1024;
    private readonly FileSystemStorageOptions _options;
    private readonly SafeFileHandle _root;
    private readonly SafeFileHandle _control;
    private readonly LinuxFileSystem.Identity _rootIdentity;
    private readonly LinuxFileSystem.Identity _controlIdentity;
    private readonly Marker _marker;
    private readonly LinuxFileSystem.Identity _metaIdentity;
    private readonly uint _userId;
    private readonly Func<IPrivateObjectPublicationRegistry>? _registryFactory;
    private readonly object _registryLock = new();
    private IPrivateObjectPublicationRegistry? _registry;
    private readonly IV2StorageObserver? _observer;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _lifetime = new();
    private readonly TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private bool _disposing;
    private Task? _disposeTask;

    private FileSystemV2PrivateObjectStore(FileSystemStorageOptions options, SafeFileHandle root,
        SafeFileHandle control, Marker marker, LinuxFileSystem.Identity metaIdentity,
        Func<IPrivateObjectPublicationRegistry>? factory, IV2StorageObserver? observer)
    {
        _options = options;
        _root = root;
        _control = control;
        _rootIdentity = LinuxFileSystem.Inspect(root).Identity;
        _controlIdentity = LinuxFileSystem.Inspect(control).Identity;
        _marker = marker;
        _metaIdentity = metaIdentity;
        _userId = LinuxFileSystem.EffectiveUserId;
        _registryFactory = factory;
        _observer = observer;
        _concurrency = new(options.MaximumConcurrency, options.MaximumConcurrency);
    }

    // A legacy caller cannot accidentally obtain the v2 publication path.
    public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
        PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) =>
        Task.FromException<PrivateObjectPublishResult>(Failure(PrivateObjectStorageError.Unsupported));

    public void Dispose() => BeginDispose().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(BeginDispose());

    private ValueTask ObserveAsync(V2StoragePhase phase, CancellationToken cancellationToken,
        PrivatePublicationKey? key = null, V2RecordKind? kind = null,
        LinuxFileSystem.Identity? file = null) => _observer?.ObserveAsync(
            new V2StorageObservation(phase, kind, key,
                file is { } identity ? LinuxFileSystem.PublicationIdentity(identity) : null), cancellationToken) ??
            ValueTask.CompletedTask;

    private async Task<Operation> AcquireOperationAsync(CancellationToken cancellationToken)
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _operations++;
        }
        try
        {
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Operation(this);
        }
        catch { ReleaseOperation(false); throw; }
    }

    private void ReleaseOperation(bool acquired)
    {
        if (acquired) _concurrency.Release();
        lock (_lifetime)
        {
            _operations--;
            if (_disposing && _operations == 0) _drained.TrySetResult(true);
        }
    }

    private Task BeginDispose()
    {
        lock (_lifetime)
        {
            if (_disposeTask is not null) return _disposeTask;
            _disposing = true;
            if (_operations == 0) _drained.TrySetResult(true);
            return _disposeTask = DisposeCoreAsync();
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _drained.Task.ConfigureAwait(false);
        _control.Dispose();
        _root.Dispose();
        _concurrency.Dispose();
    }

    private sealed class Operation(FileSystemV2PrivateObjectStore owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.ReleaseOperation(true);
        }
    }

    private long LimitFor(PrivateObjectClass @class) => @class switch
    {
        PrivateObjectClass.NativeArtifact => _options.NativeByteLimit,
        PrivateObjectClass.DerivedArtifact => _options.DerivedByteLimit,
        _ => throw Failure(PrivateObjectStorageError.Unsupported)
    };

    private IPrivateObjectPublicationRegistry GetRegistry()
    {
        lock (_registryLock)
            return _registry ??= _registryFactory?.Invoke() ?? throw Failure(PrivateObjectStorageError.Unsupported);
    }

    private void ValidateReference(RevisionScope scope, PrivateObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reference);
        if (scope != reference.Id.Scope) throw Failure(PrivateObjectStorageError.ScopeMismatch);
        if (reference.ByteCount > LimitFor(reference.Id.Class) ||
            (reference.Id.Class == PrivateObjectClass.NativeArtifact && reference.ByteCount == 0))
            throw Failure(PrivateObjectStorageError.Limit);
    }

    private static PrivateObjectStorageException NormalizeUnexpected(Exception error) => Failure(error is
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            ? PrivateObjectStorageError.Unsupported : PrivateObjectStorageError.Io);
    private static PrivateObjectStorageException Failure(PrivateObjectStorageError error) => new(error);
}
