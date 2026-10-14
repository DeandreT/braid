using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using Weft.Server;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

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
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.ScopeMismatch,
            () => store.PublishCreateOnlyAsync(PrivateStorageData.Scope(), reference, input));
        Assert.Equal(0, input.ReadCalls);
        Assert.False(Directory.Exists(root.RootPath));
        await store.DisposeAsync();
        Assert.False(Directory.Exists(root.RootPath));
    }

    [Fact]
    public async Task FailedOpeningIsNotCachedAndAnExplicitlyInitializedRootCanBeUsedOnTheNextCall()
    {
        using var root = new PrivateStorageTestRoot();
        await using var store = new LazyPrivateObjectStore(root.Options);
        var bytes = new byte[] { 0, 255, 13, 10, 0, 128 };
        var reference = PrivateStorageData.Reference(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.False(Directory.Exists(root.RootPath));
        using (root.Initialize()) { }
        using var input = new MemoryStream(bytes);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, input));
        Assert.True(input.CanRead);
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        using var reopened = root.Open();
        Assert.Equal(bytes, await reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(root.FinalPath(reference)));
    }

    [Fact]
    public async Task ConcurrentFirstUseSharesTheAcceptedStoreAndPreservesCreateOnlyPublication()
    {
        using var root = new PrivateStorageTestRoot();
        using (root.Initialize()) { }
        await using var store = new LazyPrivateObjectStore(root.Options);
        var bytes = PrivateStorageData.Pattern(65_537);
        var reference = PrivateStorageData.Reference(bytes);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var source = new MemoryStream(bytes);
            return await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, source);
        }));
        Assert.Single(results, result => result == PrivateObjectPublishResult.Created);
        Assert.Equal(3, results.Count(result => result == PrivateObjectPublishResult.AlreadyPresent));
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Empty(root.AttemptFiles());
    }

    [Fact]
    public async Task DisposalWaitsForActualPublicationAndRejectsNewOperationsWithoutClosingCallerSource()
    {
        using var root = new PrivateStorageTestRoot();
        using (root.Initialize()) { }
        var store = new LazyPrivateObjectStore(root.Options);
        var bytes = PrivateStorageData.Pattern(65_539);
        var reference = PrivateStorageData.Reference(bytes);
        using var source = new PrivateStorageGatedInput(bytes);
        var publish = store.PublishCreateOnlyAsync(reference.Id.Scope, reference, source);
        Task? disposal = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEmpty(root.AttemptFiles());
            disposal = store.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadVerifiedAsync(reference.Id.Scope, reference));
            source.Release();
            Assert.Equal(PrivateObjectPublishResult.Created, await publish.WaitAsync(TimeSpan.FromSeconds(10)));
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(source.CanRead);
            using var reopened = root.Open();
            Assert.Equal(bytes, await reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
            Assert.Empty(root.AttemptFiles());
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
        using (root.Initialize()) { }
        var bytes = PrivateStorageData.Pattern(71);
        var reference = PrivateStorageData.Reference(bytes);
        await using var store = new LazyPrivateObjectStore(root.Options);
        using (var input = new MemoryStream(bytes))
            Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, input));
        File.Delete(root.MarkerPath);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        using var retry = new MemoryStream(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, retry));
        Assert.False(File.Exists(root.MarkerPath));
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
    }
}
