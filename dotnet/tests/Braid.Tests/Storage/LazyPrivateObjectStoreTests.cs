using System.Runtime.Versioning;
using Braid.Application;
using Braid.Domain;
using Braid.Server;
using Braid.Storage.FileSystem;
using Braid.Storage.Hosting;

namespace Braid.Tests;

[SupportedOSPlatform("linux")]
public sealed class LazyPrivateObjectStoreTests
{
    [Fact]
    public async Task ConstructionAndCanceledOrForeignScopeOperationsDoNotOpenOrInitializeTheRoot()
    {
        using var root = new PrivateStorageTestRoot();
        await using var store = new LazyPrivateObjectStore(root.Options);
        var bytes = PrivateStorageData.Pattern(33);
        var reference = PrivateStorageData.Reference(bytes);
        Assert.False(Directory.Exists(root.RootPath));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadVerifiedAsync(reference.Id.Scope, reference, canceled.Token));
        using var input = new PrivateStorageInput(bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, input, canceled.Token));
        var owner = PrivateStorageV2Data.Owner(reference);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, input, canceled.Token));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.ScopeMismatch,
            () => store.PublishCreateOnlyAsync(PrivateStorageData.Scope(), reference, input));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.ScopeMismatch,
            () => store.PublishCreateOnlyAsync(owner, PrivateStorageData.Scope(), reference, input));
        Assert.Equal(0, input.ReadCalls);
        Assert.False(Directory.Exists(root.RootPath));
        await store.DisposeAsync();
        Assert.False(Directory.Exists(root.RootPath));
    }

    [Fact]
    public async Task FailedOpeningIsNotCachedAndAnExplicitlyInitializedRootCanBeUsedOnTheNextCall()
    {
        using var root = new PrivateStorageTestRoot();
        var registry = new PrivateStorageV2PhysicalRegistry();
        await using var store = new LazyPrivateObjectStore(root.Options, () => registry);
        var bytes = new byte[] { 0, 255, 13, 10, 0, 128 };
        var reference = PrivateStorageData.Reference(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.False(Directory.Exists(root.RootPath));
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        using var input = new MemoryStream(bytes);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(
            PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, input));
        Assert.True(input.CanRead);
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        using var reopened = FileSystemV2PrivateObjectStore.Open(root.Options);
        Assert.Equal(bytes, await reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(root.FinalPath(reference)));
    }

    [Fact]
    public async Task ConcurrentFirstUseSharesTheAcceptedStoreAndPreservesCreateOnlyPublication()
    {
        using var root = new PrivateStorageTestRoot();
        var options = new FileSystemStorageOptions(root.RootPath, root.ApplicationRoot, maximumConcurrency: 4);
        using (FileSystemV2PrivateObjectStore.Initialize(options)) { }
        var registry = new SynchronizedRegistry();
        await using var store = new LazyPrivateObjectStore(options, () => registry);
        var bytes = PrivateStorageData.Pattern(65_537);
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        var sources = Enumerable.Range(0, 4).Select(_ => new PrivateStorageGatedInput(bytes)).ToArray();
        var publications = new List<Task<PrivateObjectPublishResult>>();
        PrivateObjectPublishResult[] results;
        try
        {
            foreach (var source in sources)
            {
                publications.Add(store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, source));
                await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            // All first-use publications are live before any final publication.
            // Release the real nonwaiting final gates deterministically.
            for (var index = 0; index < sources.Length; index++)
            {
                sources[index].Release();
                await publications[index].WaitAsync(TimeSpan.FromSeconds(10));
            }
            results = await Task.WhenAll(publications);
        }
        finally
        {
            foreach (var source in sources) source.Release();
            await Task.WhenAll(publications).WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var source in sources) source.Dispose();
        }
        Assert.Single(results, result => result == PrivateObjectPublishResult.Created);
        Assert.Equal(3, results.Count(result => result == PrivateObjectPublishResult.AlreadyPresent));
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Empty(DataAttemptFiles(root));
    }

    [Fact]
    public async Task DisposalWaitsForActualPublicationAndRejectsNewOperationsWithoutClosingCallerSource()
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var registry = new PrivateStorageV2PhysicalRegistry();
        var store = new LazyPrivateObjectStore(root.Options, () => registry);
        var bytes = PrivateStorageData.Pattern(65_539);
        var reference = PrivateStorageData.Reference(bytes);
        using var source = new PrivateStorageGatedInput(bytes);
        var publish = store.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, source);
        Task? disposal = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEmpty(DataAttemptFiles(root));
            disposal = store.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadVerifiedAsync(reference.Id.Scope, reference));
            source.Release();
            Assert.Equal(PrivateObjectPublishResult.Created, await publish.WaitAsync(TimeSpan.FromSeconds(10)));
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(source.CanRead);
            using var reopened = FileSystemV2PrivateObjectStore.Open(root.Options);
            Assert.Equal(bytes, await reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
            Assert.Empty(DataAttemptFiles(root));
        }
        finally
        {
            source.Release();
            await publish.WaitAsync(TimeSpan.FromSeconds(10));
            await (disposal ?? store.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task LazyOpeningAndLaterOperationsPreserveMarkerSafetyAndNeverRepairExistingData()
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = PrivateStorageData.Pattern(71);
        var reference = PrivateStorageData.Reference(bytes);
        var registry = new PrivateStorageV2PhysicalRegistry();
        await using var store = new LazyPrivateObjectStore(root.Options, () => registry);
        var owner = PrivateStorageV2Data.Owner(reference);
        using (var input = new MemoryStream(bytes))
            Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, input));
        File.Delete(root.MarkerPath);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        using var retry = new MemoryStream(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, retry));
        Assert.False(File.Exists(root.MarkerPath));
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
    }

    [Fact]
    public async Task ConfiguredHostRefusesAnUnactivatedV1RootWithoutChangingItsMarkerOrFinalBytes()
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(79);
        var reference = PrivateStorageData.Reference(bytes);
        using (var legacy = root.Initialize())
            await legacy.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        var marker = File.ReadAllBytes(root.MarkerPath);
        var identity = PrivateStorageV2Identity.Read(root.FinalPath(reference));
        var resolutions = 0;
        await using var store = new LazyPrivateObjectStore(root.Options, () =>
        {
            resolutions++;
            throw new InvalidOperationException("Refused v1 roots cannot resolve publication credentials.");
        });

        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        using var input = new PrivateStorageInput(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => store.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, input));

        Assert.Equal(0, resolutions);
        Assert.Equal(0, input.ReadCalls);
        Assert.Equal(marker, File.ReadAllBytes(root.MarkerPath));
        Assert.Equal(identity, PrivateStorageV2Identity.Read(root.FinalPath(reference)));
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
        Assert.False(Directory.Exists(Path.Combine(root.RootPath, ".braid-private-control")));
    }

    [Fact]
    public async Task PlainPublicationRefusesBeforeOpeningAndNeverReadsSourceOrResolvesARegistry()
    {
        using var root = new PrivateStorageTestRoot();
        var resolutions = 0;
        await using var store = new LazyPrivateObjectStore(root.Options, () =>
        {
            resolutions++;
            throw new InvalidOperationException("Plain publication cannot resolve credentials.");
        });
        var bytes = PrivateStorageData.Pattern(83);
        var reference = PrivateStorageData.Reference(bytes);
        using var source = new PrivateStorageInput(bytes);

        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Unsupported,
            () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, source));

        Assert.Equal(0, resolutions);
        Assert.Equal(0, source.ReadCalls);
        Assert.True(source.CanRead);
        Assert.False(Directory.Exists(root.RootPath));
    }

    [Fact]
    public async Task OpeningAndReadingAnExistingV2ObjectNeverResolveAWriterRegistry()
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(89);
        var reference = PrivateStorageData.Reference(bytes);
        using (var writer = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => new PrivateStorageV2PhysicalRegistry()))
            await writer.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes));
        var before = PrivateStorageV2Data.Files(root.RootPath);
        var resolutions = 0;
        await using var store = new LazyPrivateObjectStore(root.Options, () =>
        {
            resolutions++;
            throw new InvalidOperationException("Stored reads cannot resolve writer credentials.");
        });

        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(0, resolutions);
        var after = PrivateStorageV2Data.Files(root.RootPath);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var entry in before) Assert.Equal(entry.Value, after[entry.Key]);
    }

    private static string[] DataAttemptFiles(PrivateStorageTestRoot root)
    {
        var objects = Path.Combine(root.RootPath, "objects");
        return Directory.Exists(objects) ? Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".blob", StringComparison.Ordinal)).ToArray() : [];
    }

    /// <summary>Only the authored in-memory metadata dictionary is serialized; physical store concurrency remains real.</summary>
    private sealed class SynchronizedRegistry : IPrivateObjectPublicationRegistry
    {
        private readonly object sync = new();
        private readonly PrivateStorageV2PhysicalRegistry inner = new();
        public Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default)
        { lock (sync) return inner.ReserveAsync(owner, storeKind, rootId, objectId, tempId, absenceRecordDigest, cancellationToken); }
        public Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
            PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default)
        { lock (sync) return inner.BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken); }
        public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default)
        { lock (sync) return inner.GetAsync(owner, storeKind, rootId, objectId, tempId, cancellationToken); }
    }
}
