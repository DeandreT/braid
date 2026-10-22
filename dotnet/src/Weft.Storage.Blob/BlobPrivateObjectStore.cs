using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.Blob;

/// <summary>
/// Claim-aware private object storage in one Azure Blob container. Publication retains the
/// actual original workflow claim; physical retirement consumes an already issued retirement.
/// </summary>
public sealed partial class BlobPrivateObjectStore : IPrivateObjectPublicationStore,
    IPrivateObjectRetirementStore, IDisposable, IAsyncDisposable
{
    private const int ChunkSize = 64 * 1024;
    private const int BlockSize = 1024 * 1024;
    // The published development key of the Azurite emulator; it protects no deployed data.
    private const string EmulatorKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private readonly BlobStorageOptions _options;
    private readonly BlobContainerClient _container;
    private readonly Guid _rootId;
    private readonly Guid _controlId;
    private readonly Func<IPrivateObjectPublicationRegistry>? _registryFactory;
    private readonly object _registryLock = new();
    private IPrivateObjectPublicationRegistry? _registry;
    private readonly IBlobStorageObserver? _observer;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _lifetime = new();
    private readonly TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private bool _disposing;
    private Task? _disposeTask;

    private BlobPrivateObjectStore(BlobStorageOptions options, BlobContainerClient container, Guid rootId, Guid controlId,
        Func<IPrivateObjectPublicationRegistry>? factory, IBlobStorageObserver? observer)
    {
        _options = options;
        _container = container;
        _rootId = rootId;
        _controlId = controlId;
        _registryFactory = factory;
        _observer = observer;
        _concurrency = new(options.MaximumConcurrency, options.MaximumConcurrency);
    }

    internal Guid RootId => _rootId;
    internal Guid ControlId => _controlId;

    /// <summary>Creates the marker in an existing, empty, private container; it never creates the container.</summary>
    public static Task<BlobPrivateObjectStore> InitializeAsync(BlobStorageOptions options,
        Func<IPrivateObjectPublicationRegistry>? registryFactory = null, CancellationToken cancellationToken = default) =>
        OpenConfiguredAsync(options, CreateContainerClient(options), registryFactory, null, initialize: true, cancellationToken);

    /// <summary>Admits an existing initialized container. Opening never resolves the registry.</summary>
    public static Task<BlobPrivateObjectStore> OpenAsync(BlobStorageOptions options,
        Func<IPrivateObjectPublicationRegistry>? registryFactory = null, CancellationToken cancellationToken = default) =>
        OpenConfiguredAsync(options, CreateContainerClient(options), registryFactory, null, initialize: false, cancellationToken);

    internal static Task<BlobPrivateObjectStore> InitializeAsync(BlobStorageOptions options, BlobContainerClient container,
        Func<IPrivateObjectPublicationRegistry>? registryFactory, IBlobStorageObserver? observer,
        CancellationToken cancellationToken = default) =>
        OpenConfiguredAsync(options, container, registryFactory, observer, initialize: true, cancellationToken);

    internal static Task<BlobPrivateObjectStore> OpenAsync(BlobStorageOptions options, BlobContainerClient container,
        Func<IPrivateObjectPublicationRegistry>? registryFactory, IBlobStorageObserver? observer,
        CancellationToken cancellationToken = default) =>
        OpenConfiguredAsync(options, container, registryFactory, observer, initialize: false, cancellationToken);

    internal static BlobContainerClient CreateContainerClient(BlobStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var clientOptions = new BlobClientOptions();
        clientOptions.Retry.Mode = RetryMode.Exponential;
        clientOptions.Retry.MaxRetries = 3;
        clientOptions.Retry.Delay = TimeSpan.FromMilliseconds(200);
        clientOptions.Retry.MaxDelay = TimeSpan.FromSeconds(4);
        clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(30);
        clientOptions.Diagnostics.IsLoggingContentEnabled = false;
        return options.Credential switch
        {
            BlobStorageCredentialKind.ManagedIdentity => new BlobContainerClient(options.ContainerUri,
                new ManagedIdentityCredential(options.ManagedIdentityClientId is { } clientId
                    ? ManagedIdentityId.FromUserAssignedClientId(clientId.ToString("D")) : ManagedIdentityId.SystemAssigned),
                clientOptions),
            BlobStorageCredentialKind.WorkloadIdentity => new BlobContainerClient(options.ContainerUri,
                new WorkloadIdentityCredential(), clientOptions),
            BlobStorageCredentialKind.Emulator => new BlobContainerClient(options.ContainerUri,
                new StorageSharedKeyCredential(BlobStorageOptions.EmulatorAccount, EmulatorKey), clientOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
    }

    // A legacy caller cannot accidentally obtain an untracked publication path.
    public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
        PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) =>
        Task.FromException<PrivateObjectPublishResult>(Failure(PrivateObjectStorageError.Unsupported));

    public void Dispose() => BeginDispose().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(BeginDispose());

    private ValueTask ObserveAsync(BlobStoragePhase phase, CancellationToken cancellationToken,
        PrivatePublicationKey? key = null) =>
        _observer?.ObserveAsync(new BlobStorageObservation(phase, key), cancellationToken) ?? ValueTask.CompletedTask;

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
        _concurrency.Dispose();
    }

    private sealed class Operation(BlobPrivateObjectStore owner) : IDisposable
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

    private BlobClient Blob(string name) => _container.GetBlobClient(name);
    private BlockBlobClient BlockBlob(string name) => _container.GetBlockBlobClient(name);

    /// <summary>Maps provider failures to closed categories; cancellation stays cancellation only for the caller's token.</summary>
    private static Exception Normalize(Exception error, CancellationToken cancellationToken) => error switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => error,
        PrivateObjectStorageException => error,
        CredentialUnavailableException => Failure(PrivateObjectStorageError.UnsafeStorage),
        RequestFailedException { Status: 401 or 403 } => Failure(PrivateObjectStorageError.UnsafeStorage),
        _ => Failure(PrivateObjectStorageError.Io)
    };

    private static PrivateObjectStorageException Failure(PrivateObjectStorageError error) => new(error);
}

// Internal qualification seam. Each notification follows the completed phase it identifies.
// Production factories always use a null observer.
internal enum BlobStoragePhase
{
    TempGateAcquired = 1,
    ReservationAcknowledged = 2,
    AttemptCreated = 3,
    BindingAcknowledged = 4,
    PayloadPrepared = 5,
    FinalGateAcquired = 6,
    FenceInstalled = 7,
    PayloadStaged = 8,
    CommitPending = 9
}

internal readonly record struct BlobStorageObservation(BlobStoragePhase Phase, PrivatePublicationKey? Publication = null);

internal interface IBlobStorageObserver
{
    ValueTask ObserveAsync(BlobStorageObservation observation, CancellationToken cancellationToken);
}
