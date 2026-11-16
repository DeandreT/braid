using System.Runtime.Versioning;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Braid.Application;
using Braid.Domain;
using Braid.Storage.Blob;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>Store-neutral publication phases; each harness maps its own internal observer onto them.</summary>
internal enum ContractPhase
{
    TempGateAcquired = 1,
    ReservationAcknowledged = 2,
    TemporaryCreated = 3,
    BindingAcknowledged = 4,
    PayloadPrepared = 5,
    FinalGateAcquired = 6
}

internal delegate ValueTask ContractObserver(ContractPhase phase, CancellationToken cancellationToken);

/// <summary>One opened claim-aware store under test, behind its three public capabilities.</summary>
internal sealed class ContractStore(IPrivateObjectPublicationStore publication, IPrivateObjectRetirementStore retirement,
    IPrivateObjectStorageObserver observer, IAsyncDisposable lifetime) : IAsyncDisposable
{
    internal IPrivateObjectPublicationStore Publication { get; } = publication;
    internal IPrivateObjectRetirementStore Retirement { get; } = retirement;
    internal IPrivateObjectStorageObserver Observer { get; } = observer;

    internal Task<PrivateObjectPublishResult> PublishAsync(PrivatePublicationOwner owner, PrivateObjectReference reference,
        Stream source, CancellationToken cancellationToken = default) =>
        Publication.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, source, cancellationToken);

    internal Task<byte[]> ReadAsync(PrivateObjectReference reference) => Publication.ReadVerifiedAsync(reference.Id.Scope, reference);

    internal Task<PrivateObjectRetirementOutcome> RetireAsync(ObjectCleanupClaim claim) => Retirement.RetireAsync(claim);

    public ValueTask DisposeAsync() => lifetime.DisposeAsync();
}

/// <summary>
/// Physical setup and inspection for one store kind. Assertions about contract behavior use only
/// <see cref="ContractStore"/>; these helpers observe or perturb the backing storage directly.
/// </summary>
[SupportedOSPlatform("linux")]
internal abstract class StoreHarness : IAsyncDisposable
{
    internal const string FileSystem = "filesystem";
    internal const string Blob = "blob";

    internal static async Task<StoreHarness> CreateAsync(string kind, AzuriteFixture azurite) => kind switch
    {
        FileSystem => new FileSystemStoreHarness(),
        Blob => new BlobStoreHarness(azurite, await azurite.CreateContainerAsync()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal abstract Task<ContractStore> InitializeAsync(Func<IPrivateObjectPublicationRegistry>? registry = null,
        ContractObserver? observer = null);
    internal abstract Task<ContractStore> OpenAsync(Func<IPrivateObjectPublicationRegistry>? registry = null,
        ContractObserver? observer = null);
    /// <summary>The raw bytes of a published final object, or null when none is published.</summary>
    internal abstract Task<byte[]?> FinalBytesAsync(PrivateObjectReference reference);
    internal abstract Task<int> TemporarySlotsAsync();
    internal abstract Task<bool> SealPresentAsync(PrivatePublicationKey key, string seal);
    /// <summary>Places an unrelated occupant of the given length in an exact temporary slot.</summary>
    internal abstract Task PlantTemporaryAsync(PrivatePublicationKey key, int byteCount);
    /// <summary>Holds the object's exclusive gate from outside the store until disposed.</summary>
    internal abstract Task<IAsyncDisposable> HoldObjectGateAsync(PrivateObjectId id);
    public abstract ValueTask DisposeAsync();
}

[SupportedOSPlatform("linux")]
internal sealed class FileSystemStoreHarness : StoreHarness
{
    private readonly PrivateStorageTestRoot root = new();

    internal override Task<ContractStore> InitializeAsync(Func<IPrivateObjectPublicationRegistry>? registry = null,
        ContractObserver? observer = null) =>
        Task.FromResult(Wrap(FileSystemV2PrivateObjectStore.Initialize(root.Options, registry, Adapt(observer))));

    internal override Task<ContractStore> OpenAsync(Func<IPrivateObjectPublicationRegistry>? registry = null,
        ContractObserver? observer = null) =>
        Task.FromResult(Wrap(FileSystemV2PrivateObjectStore.Open(root.Options, registry, Adapt(observer))));

    internal override Task<byte[]?> FinalBytesAsync(PrivateObjectReference reference) =>
        Task.FromResult(File.Exists(root.FinalPath(reference)) ? File.ReadAllBytes(root.FinalPath(reference)) : null);

    internal override Task<int> TemporarySlotsAsync() => Task.FromResult(Directory.Exists(Path.Combine(root.RootPath, "objects"))
        ? Directory.GetFiles(Path.Combine(root.RootPath, "objects"), ".attempt-*.tmp", SearchOption.AllDirectories).Length : 0);

    internal override Task<bool> SealPresentAsync(PrivatePublicationKey key, string seal) =>
        Task.FromResult(File.Exists(Path.Combine(PrivateStorageV2Data.TempLeaf(root, key), seal)));

    internal override Task PlantTemporaryAsync(PrivatePublicationKey key, int byteCount)
    {
        var temp = PrivateStorageV2Data.TempPath(root, key);
        File.WriteAllBytes(temp, new byte[byteCount]);
        File.SetUnixFileMode(temp, PrivateStorageTestRoot.AttemptMode);
        return Task.CompletedTask;
    }

    internal override Task<IAsyncDisposable> HoldObjectGateAsync(PrivateObjectId id) =>
        Task.FromResult<IAsyncDisposable>(new Held(new PrivateStorageV2IndependentGate(
            Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, id), "object.gate"))));

    public override ValueTask DisposeAsync()
    {
        root.Dispose();
        return ValueTask.CompletedTask;
    }

    private static ContractStore Wrap(FileSystemV2PrivateObjectStore store) => new(store, store, store, store);

    private static PrivateStorageV2Observer? Adapt(ContractObserver? observer) => observer is null ? null :
        new((observation, token) => observation.Phase switch
        {
            V2StoragePhase.TempGateAcquired => observer(ContractPhase.TempGateAcquired, token),
            V2StoragePhase.ReservationAcknowledged => observer(ContractPhase.ReservationAcknowledged, token),
            V2StoragePhase.TemporaryCreated => observer(ContractPhase.TemporaryCreated, token),
            V2StoragePhase.BindingAcknowledged => observer(ContractPhase.BindingAcknowledged, token),
            V2StoragePhase.PayloadPrepared => observer(ContractPhase.PayloadPrepared, token),
            V2StoragePhase.FinalGateAcquired => observer(ContractPhase.FinalGateAcquired, token),
            _ => ValueTask.CompletedTask
        });

    private sealed class Held(IDisposable gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class BlobStoreHarness(AzuriteFixture azurite, BlobContainerClient container) : StoreHarness
{
    internal BlobContainerClient Container { get; } = container;
    internal BlobStorageOptions Options => azurite.Options(Container);

    internal override async Task<ContractStore> InitializeAsync(Func<IPrivateObjectPublicationRegistry>? registry = null,
        ContractObserver? observer = null) =>
        Wrap(await BlobPrivateObjectStore.InitializeAsync(Options, azurite.Client(Container), registry, Adapt(observer)));

    internal override async Task<ContractStore> OpenAsync(Func<IPrivateObjectPublicationRegistry>? registry = null,
        ContractObserver? observer = null) =>
        Wrap(await BlobPrivateObjectStore.OpenAsync(Options, azurite.Client(Container), registry, Adapt(observer)));

    internal async Task<BlobPrivateObjectStore> OpenStoreAsync(Func<IPrivateObjectPublicationRegistry>? registry = null,
        IBlobStorageObserver? observer = null) =>
        await BlobPrivateObjectStore.OpenAsync(Options, azurite.Client(Container), registry, observer);

    internal override async Task<byte[]?> FinalBytesAsync(PrivateObjectReference reference)
    {
        var blob = Container.GetBlobClient(BlobLayout.FinalName(reference.Id));
        try
        {
            var result = (await blob.DownloadContentAsync()).Value;
            return BlobFacts.Classify(result.Details.ETag, result.Details.ContentLength, result.Details.Metadata).Form == BlobForm.Object
                ? result.Content.ToArray() : null;
        }
        catch (RequestFailedException error) when (error.Status == 404) { return null; }
    }

    internal override async Task<int> TemporarySlotsAsync()
    {
        var count = 0;
        await foreach (var item in Container.GetBlobsAsync(new GetBlobsOptions { Prefix = "objects/", Traits = BlobTraits.Metadata }))
            if (item.Name.Contains("/.attempt-", StringComparison.Ordinal) &&
                BlobFacts.Classify(default, item.Properties.ContentLength ?? -1, item.Metadata).Form != BlobForm.Retired)
                count++;
        return count;
    }

    internal override async Task<bool> SealPresentAsync(PrivatePublicationKey key, string seal) =>
        (await Container.GetBlobClient(SealName(key, seal)).ExistsAsync()).Value;

    internal string SealName(PrivatePublicationKey key, string seal) => BlobLayout.Seal(ControlId, key.Object, key.TempId, seal switch
    {
        "absence" => BlobRecordKind.Absence,
        "reservation" => BlobRecordKind.Reservation,
        "bound" => BlobRecordKind.Bound,
        "prepared" => BlobRecordKind.Prepared,
        _ => throw new ArgumentOutOfRangeException(nameof(seal))
    });

    internal override async Task PlantTemporaryAsync(PrivatePublicationKey key, int byteCount)
    {
        // A zero-length occupant has the exact attempt form; a nonempty one has no recognized form.
        var blob = Container.GetBlockBlobClient(BlobLayout.AttemptName(key.Object, key.TempId));
        using var content = new MemoryStream(new byte[byteCount]);
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            Metadata = byteCount == 0 ? BlobFacts.AttemptMetadata(0x0123456789abcdef) : null
        });
    }

    internal override async Task<IAsyncDisposable> HoldObjectGateAsync(PrivateObjectId id)
    {
        var lease = Container.GetBlobClient(BlobLayout.ObjectGate(ControlId, id)).GetBlobLeaseClient();
        await lease.AcquireAsync(TimeSpan.FromSeconds(60));
        return new Held(lease);
    }

    internal Guid ControlId
    {
        get
        {
            var lines = Container.GetBlobClient(BlobLayout.MarkerName).DownloadContent().Value.Content.ToString().Split('\n');
            return Guid.ParseExact(lines[2], "N");
        }
    }

    public override async ValueTask DisposeAsync() => await Container.DeleteIfExistsAsync();

    private static ContractStore Wrap(BlobPrivateObjectStore store) => new(store, store, store, store);

    private static BlobObserver? Adapt(ContractObserver? observer) => observer is null ? null :
        new((observation, token) => observation.Phase switch
        {
            BlobStoragePhase.TempGateAcquired => observer(ContractPhase.TempGateAcquired, token),
            BlobStoragePhase.ReservationAcknowledged => observer(ContractPhase.ReservationAcknowledged, token),
            BlobStoragePhase.AttemptCreated => observer(ContractPhase.TemporaryCreated, token),
            BlobStoragePhase.BindingAcknowledged => observer(ContractPhase.BindingAcknowledged, token),
            BlobStoragePhase.PayloadPrepared => observer(ContractPhase.PayloadPrepared, token),
            BlobStoragePhase.FinalGateAcquired => observer(ContractPhase.FinalGateAcquired, token),
            _ => ValueTask.CompletedTask
        });

    private sealed class Held(BlobLeaseClient lease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await lease.ReleaseAsync();
    }
}

internal sealed class BlobObserver(Func<BlobStorageObservation, CancellationToken, ValueTask> observe) : IBlobStorageObserver
{
    public ValueTask ObserveAsync(BlobStorageObservation observation, CancellationToken cancellationToken) =>
        observe(observation, cancellationToken);
}

/// <summary>Pauses at the first occurrence of one phase until released.</summary>
internal sealed class ContractBoundary(ContractPhase phase)
{
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int entered;
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Release() => release.TrySetResult();
    internal async ValueTask ObserveAsync(ContractPhase observed, CancellationToken cancellationToken)
    {
        if (observed != phase || Interlocked.Exchange(ref entered, 1) != 0) return;
        Entered.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
    }
}
