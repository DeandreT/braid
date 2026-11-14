using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.Blob;

namespace Weft.Tests;

/// <summary>Actual SDK/network Blob qualification. Registry below is test-only, not database durability evidence.</summary>
[Collection(AzuriteCollection.Name)]
public sealed class BlobSpecObjectStoreAzuriteTests(AzuriteFixture emulator)
{
    [AzuriteTheory]
    [InlineData(SpecObjectKind.Original)]
    [InlineData(SpecObjectKind.Extraction)]
    public async Task Publication_read_replay_and_retirement_preserve_physical_fence(SpecObjectKind kind)
    {
        var test = await Create(kind);
        using var source = new MemoryStream(test.Bytes);
        Assert.Equal(SpecPublishResult.Created, await test.Store.PublishAsync(test.Scope, test.Claim, source));
        Assert.True(source.CanRead);
        Assert.Equal(test.Bytes, await test.Store.ReadAsync(test.Scope, test.Reference));
        var tag = (await test.Blob.GetPropertiesAsync()).Value.ETag;
        Assert.Equal(SpecPublishResult.AlreadyPresent, await test.Publish());
        Assert.Equal(tag, (await test.Blob.GetPropertiesAsync()).Value.ETag);
        var retirement = test.Registry.IssueRetirement(test.Claim);
        Assert.Equal(SpecRetireResult.Retired, await test.Store.RetireAsync(test.Scope, retirement));
        Assert.Equal(SpecRetireResult.Missing, await test.Store.RetireAsync(test.Scope, retirement));
        await AssertFence(test.Blob);
        await Error(SpecStorageError.NotAdmitted, () => test.Store.ReadAsync(test.Scope, test.Reference));
        await Error(SpecStorageError.NotAdmitted, test.Publish);
        test.Registry.Tombstoned = false; // Deliberately stale admission must not revive the physical name.
        await Error(SpecStorageError.Conflict, test.Publish);
        await AssertFence(test.Blob);
    }

    [AzuriteTheory]
    [InlineData(SpecObjectKind.Original, false)]
    [InlineData(SpecObjectKind.Original, true)]
    [InlineData(SpecObjectKind.Extraction, false)]
    [InlineData(SpecObjectKind.Extraction, true)]
    public async Task Missing_retirement_fences_delayed_publication_including_empty_payloads(SpecObjectKind kind, bool empty)
    {
        var test = await Create(kind, empty);
        Assert.False((await test.Blob.ExistsAsync()).Value);
        var retirement = test.Registry.IssueRetirement(test.Claim);
        Assert.Equal(SpecRetireResult.Missing, await test.Store.RetireAsync(test.Scope, retirement));
        test.Registry.Tombstoned = false;
        await Error(SpecStorageError.Conflict, test.Publish);
        await AssertFence(test.Blob);
    }

    [AzuriteTheory]
    [InlineData(SpecObjectKind.Original)]
    [InlineData(SpecObjectKind.Extraction)]
    public async Task Synthetic_registry_ack_loss_after_actual_upload_recovers_same_claim(SpecObjectKind kind)
    {
        var test = await Create(kind);
        test.Registry.LoseAcknowledgement = true;
        await Error(SpecStorageError.Io, test.Publish);
        // Verify the real object BEFORE retry; the injected failure is after the awaited network upload.
        Assert.Equal(test.Bytes, (await test.Blob.DownloadContentAsync()).Value.Content.ToArray());
        Assert.Equal("live", (await test.Blob.GetPropertiesAsync()).Value.Metadata["state"]);
        var tag = (await test.Blob.GetPropertiesAsync()).Value.ETag;
        await Error(SpecStorageError.NotAdmitted, () => test.Store.ReadAsync(test.Scope, test.Reference));
        test.Registry.LoseAcknowledgement = false;
        Assert.Equal(SpecPublishResult.AlreadyPresent, await test.Publish());
        Assert.Equal(tag, (await test.Blob.GetPropertiesAsync()).Value.ETag);
        Assert.Equal(test.Bytes, await test.Store.ReadAsync(test.Scope, test.Reference));
    }

    [AzuriteTheory]
    [InlineData(SpecObjectKind.Original)]
    [InlineData(SpecObjectKind.Extraction)]
    public async Task Corrupt_actual_content_is_not_overwritten_or_retired(SpecObjectKind kind)
    {
        var test = await Create(kind);
        await test.Publish();
        var metadata = (await test.Blob.GetPropertiesAsync()).Value.Metadata;
        byte[] corrupt = [4, 5, 6];
        await test.Blob.UploadAsync(new MemoryStream(corrupt), new BlobUploadOptions { Metadata = metadata });
        var tag = (await test.Blob.GetPropertiesAsync()).Value.ETag;
        await Error(SpecStorageError.Integrity, () => test.Store.ReadAsync(test.Scope, test.Reference));
        await Error(SpecStorageError.Conflict, test.Publish);
        var retirement = test.Registry.IssueRetirement(test.Claim);
        await Error(SpecStorageError.Integrity, () => test.Store.RetireAsync(test.Scope, retirement));
        Assert.Equal(tag, (await test.Blob.GetPropertiesAsync()).Value.ETag);
        Assert.Equal(corrupt, (await test.Blob.DownloadContentAsync()).Value.Content.ToArray());
    }

    [AzuriteTheory]
    [InlineData(SpecObjectKind.Original)]
    [InlineData(SpecObjectKind.Extraction)]
    public async Task Scope_and_bounded_source_checks_leave_no_payload(SpecObjectKind kind)
    {
        var test = await Create(kind);
        using var input = new MemoryStream(new byte[100]);
        await Error(SpecStorageError.ScopeMismatch, () => test.Store.PublishAsync(
            new(test.Scope.Project, Guid.NewGuid()), test.Claim, input));
        await Error(SpecStorageError.ScopeMismatch, () => test.Store.PublishAsync(test.Scope,
            new(Guid.NewGuid(), test.Claim.AttemptId, test.Reference), input));
        await Error(SpecStorageError.NotAdmitted, () => test.Store.PublishAsync(test.Scope,
            new(test.Claim.StoreId, Guid.NewGuid(), test.Reference), input));
        Assert.Equal(0, input.Position);
        foreach (var bytes in new byte[][] { [1], [1, 2, 4], new byte[100] })
        {
            using var source = new MemoryStream(bytes);
            await Error(SpecStorageError.Integrity, () => test.Store.PublishAsync(test.Scope, test.Claim, source));
            Assert.True(source.Position <= test.Reference.ByteCount + 1);
            Assert.True(source.CanRead);
        }
        Assert.False((await test.Blob.ExistsAsync()).Value);
        var names = new List<string>();
        await foreach (var blob in test.Container.GetBlobsAsync()) names.Add(blob.Name);
        Assert.Equal(new[] { BlobSpecObjectStore.MarkerName }, names);
    }

    [AzuriteTheory]
    [InlineData(SpecObjectKind.Original)]
    [InlineData(SpecObjectKind.Extraction)]
    public async Task Actual_container_privacy_and_marker_identity_incarnation_are_rechecked(SpecObjectKind kind)
    {
        var test = await Create(kind);
        await test.Publish();
        await test.Container.SetAccessPolicyAsync(PublicAccessType.Blob);
        await Error(SpecStorageError.UnsafeStorage, test.Publish);
        await Error(SpecStorageError.UnsafeStorage, () => test.Store.ReadAsync(test.Scope, test.Reference));
        await test.Container.SetAccessPolicyAsync(PublicAccessType.None);
        await Error(SpecStorageError.UnsafeStorage, () => BlobSpecObjectStore.OpenAsync(
            emulator.Options(test.Container), Guid.NewGuid(), test.Registry));
        var marker = test.Container.GetBlobClient(BlobSpecObjectStore.MarkerName);
        var original = (await marker.DownloadContentAsync()).Value.Content;
        var tag = (await marker.GetPropertiesAsync()).Value.ETag;
        await marker.UploadAsync(original, overwrite: true); // Same bytes, new incarnation.
        Assert.NotEqual(tag, (await marker.GetPropertiesAsync()).Value.ETag);
        await Error(SpecStorageError.UnsafeStorage, test.Publish);
        await Error(SpecStorageError.UnsafeStorage, () => test.Store.ReadAsync(test.Scope, test.Reference));
        await Error(SpecStorageError.UnsafeStorage, () => test.Store.RetireAsync(
            test.Scope, test.Registry.IssueRetirement(test.Claim)));
        Assert.Equal(test.Bytes, (await test.Blob.DownloadContentAsync()).Value.Content.ToArray());
    }

    private async Task<Case> Create(SpecObjectKind kind, bool empty = false)
    {
        var container = await emulator.CreateContainerAsync();
        var registry = new TestRegistry();
        var store = await BlobSpecObjectStore.InitializeAsync(emulator.Options(container), Guid.NewGuid(), registry);
        SpecBundleScope scope = new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        byte[] bytes = empty ? [] : [1, 2, 3];
        SpecObjectReference reference = new(new(new(scope, Guid.NewGuid(), 1), kind, Guid.NewGuid()), Sha256Digest.FromBytes(bytes), bytes.Length);
        SpecPublicationClaim claim = new(store.StoreId, Guid.NewGuid(), reference);
        registry.Claims.Add(claim);
        return new(store, container, registry, scope, claim, bytes);
    }

    private static async Task AssertFence(BlobClient blob)
    {
        Assert.Empty((await blob.DownloadContentAsync()).Value.Content.ToArray());
        Assert.Equal("retired", (await blob.GetPropertiesAsync()).Value.Metadata["state"]);
    }

    private static async Task Error(SpecStorageError expected, Func<Task> action) =>
        Assert.Equal(expected, (await Assert.ThrowsAsync<SpecStorageException>(action)).Error);

    private sealed record Case(BlobSpecObjectStore Store, BlobContainerClient Container, TestRegistry Registry,
        SpecBundleScope Scope, SpecPublicationClaim Claim, byte[] Bytes)
    {
        internal SpecObjectReference Reference => Claim.Reference;
        internal BlobClient Blob => Container.GetBlobClient(SpecObjectStore.ObjectName(Reference));
        internal async Task<SpecPublishResult> Publish()
        {
            using var source = new MemoryStream(Bytes);
            return await Store.PublishAsync(Scope, Claim, source);
        }
    }

    // Borrowed contract pattern from SpecObjectStorageTests. TEST-ONLY: no durable DB or cross-process fence.
    private sealed class TestRegistry : ISpecObjectRegistry
    {
        internal HashSet<SpecPublicationClaim> Claims { get; } = [];
        private readonly HashSet<SpecObjectReference> retained = [];
        private readonly HashSet<SpecRetirementClaim> retirements = [];
        private readonly SemaphoreSlim gate = new(1, 1);
        internal bool Tombstoned;
        internal bool LoseAcknowledgement;
        internal SpecRetirementClaim IssueRetirement(SpecPublicationClaim publication)
        {
            Tombstoned = true;
            var claim = new SpecRetirementClaim(publication.StoreId, Guid.NewGuid(), publication.Reference);
            retirements.Add(claim);
            return claim;
        }
        public Task AdmitPublicationAsync(SpecPublicationClaim claim, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Tombstoned || !Claims.Contains(claim)) throw new SpecStorageException(SpecStorageError.NotAdmitted);
            return Task.CompletedTask;
        }
        public async Task<SpecPublishResult> ExecutePublicationAsync(SpecPublicationClaim claim,
            Func<CancellationToken, Task<SpecPublishResult>> publish, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                await AdmitPublicationAsync(claim, token);
                var result = await publish(token);
                if (LoseAcknowledgement) throw new IOException("Synthetic test-only registry acknowledgement loss after actual Blob upload.");
                retained.Add(claim.Reference);
                return result;
            }
            finally { gate.Release(); }
        }
        public async Task<byte[]> ExecuteReadAsync(Guid storeId, SpecObjectReference reference,
            Func<CancellationToken, Task<byte[]>> read, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                if (Tombstoned || !retained.Contains(reference) || !Claims.Any(c => c.StoreId == storeId && c.Reference == reference))
                    throw new SpecStorageException(SpecStorageError.NotAdmitted);
                return await read(token);
            }
            finally { gate.Release(); }
        }
        public async Task<SpecRetireResult> ExecuteRetirementAsync(SpecRetirementClaim claim,
            Func<CancellationToken, Task<SpecRetireResult>> retire, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                if (!Tombstoned || !retirements.Contains(claim)) throw new SpecStorageException(SpecStorageError.NotAdmitted);
                return await retire(token);
            }
            finally { gate.Release(); }
        }
    }
}
