using System.Runtime.Versioning;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Braid.Application;
using Braid.Domain;
using Braid.Storage.Blob;

namespace Braid.Tests;

/// <summary>Blob-specific admission, fencing, recovery and integrity checks against the owned emulator.</summary>
[SupportedOSPlatform("linux")]
[Collection(AzuriteCollection.Name)]
public sealed class BlobPrivateObjectStoreTests(AzuriteFixture azurite)
{
    [Theory]
    [InlineData("https://account.blob.core.windows.net", BlobStorageCredentialKind.ManagedIdentity, true)]
    [InlineData("https://account.blob.core.windows.net/", BlobStorageCredentialKind.WorkloadIdentity, true)]
    [InlineData("http://account.blob.core.windows.net", BlobStorageCredentialKind.ManagedIdentity, false)]
    [InlineData("https://account.blob.core.windows.net/container", BlobStorageCredentialKind.ManagedIdentity, false)]
    [InlineData("https://account.blob.core.windows.net/?sv=2024&sig=x", BlobStorageCredentialKind.ManagedIdentity, false)]
    [InlineData("https://user:secret@account.blob.core.windows.net", BlobStorageCredentialKind.ManagedIdentity, false)]
    [InlineData("https://account.blob.core.windows.net:8443", BlobStorageCredentialKind.ManagedIdentity, false)]
    [InlineData("https://10.0.0.4", BlobStorageCredentialKind.ManagedIdentity, false)]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1", BlobStorageCredentialKind.Emulator, true)]
    [InlineData("http://localhost:10000/devstoreaccount1", BlobStorageCredentialKind.Emulator, true)]
    [InlineData("http://10.0.0.4:10000/devstoreaccount1", BlobStorageCredentialKind.Emulator, false)]
    [InlineData("https://127.0.0.1:10000/devstoreaccount1", BlobStorageCredentialKind.Emulator, false)]
    [InlineData("http://127.0.0.1:10000/otheraccount", BlobStorageCredentialKind.Emulator, false)]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1", BlobStorageCredentialKind.ManagedIdentity, false)]
    public void EndpointsAreAdmittedOnlyForTheirCredentialSource(string endpoint, BlobStorageCredentialKind credential, bool admitted)
    {
        if (admitted) _ = new BlobStorageOptions(new Uri(endpoint), "braid-private", credential);
        else Assert.ThrowsAny<ArgumentException>(() => new BlobStorageOptions(new Uri(endpoint), "braid-private", credential));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("Braid-private")]
    [InlineData("-braid")]
    [InlineData("braid-")]
    [InlineData("braid--private")]
    [InlineData("braid_private")]
    [InlineData("braid.private")]
    public void ContainerNamesMustBeCanonical(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => new BlobStorageOptions(new Uri("https://a.blob.core.windows.net"), name,
            BlobStorageCredentialKind.ManagedIdentity));

    [Fact]
    public void ClientIdsLimitsAndConcurrencyAreBounded()
    {
        var endpoint = new Uri("https://a.blob.core.windows.net");
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), new BlobStorageOptions(endpoint, "braid-private",
            BlobStorageCredentialKind.ManagedIdentity, Guid.Parse("11111111-2222-3333-4444-555555555555")).ManagedIdentityClientId);
        Assert.Throws<ArgumentException>(() => new BlobStorageOptions(endpoint, "braid-private", BlobStorageCredentialKind.ManagedIdentity, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new BlobStorageOptions(endpoint, "braid-private", BlobStorageCredentialKind.WorkloadIdentity, Guid.NewGuid()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlobStorageOptions(endpoint, "braid-private",
            BlobStorageCredentialKind.ManagedIdentity, nativeByteLimit: BlobStorageOptions.NativeHardMaximum + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlobStorageOptions(endpoint, "braid-private",
            BlobStorageCredentialKind.ManagedIdentity, derivedByteLimit: BlobStorageOptions.DerivedHardMaximum + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlobStorageOptions(endpoint, "braid-private",
            BlobStorageCredentialKind.ManagedIdentity, maximumConcurrency: 5));
    }

    [Fact]
    public void SealsRoundTripCanonicallyAndRefuseAlternateEncodings()
    {
        var bytes = PrivateStorageData.Pattern(33);
        var reference = PrivateStorageData.Reference(bytes, PrivateObjectClass.DerivedArtifact);
        var owner = PrivateStorageV2Data.Owner(reference);
        var context = BlobPublicationContext.For(Guid.NewGuid(), Guid.NewGuid(), reference, Guid.NewGuid(), owner);
        var absence = BlobRecordCodec.Encode(new(BlobRecordKind.Absence, context));
        var bound = BlobRecordCodec.Encode(new(BlobRecordKind.Bound, context, BlobRecordCodec.Digest(absence), Guid.NewGuid(),
            Incarnation: 0x1234, AttemptETag: "\"0x8DCE0123456789A\""));
        var prepared = BlobRecordCodec.Encode(new(BlobRecordKind.Prepared, context, BlobRecordCodec.Digest(bound), Guid.NewGuid(),
            DateTimeOffset.UtcNow, 0x1234, null, reference.Digest, reference.ByteCount, 1));
        foreach (var record in new[] { absence, bound, prepared })
        {
            Assert.Equal(record, BlobRecordCodec.Encode(BlobRecordCodec.Decode(record)));
            PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => BlobRecordCodec.Decode([.. record, 0]));
            PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => BlobRecordCodec.Decode(record[..^1]));
            var flipped = (byte[])record.Clone();
            flipped[13] ^= 1;
            PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => BlobRecordCodec.Decode(flipped));
        }
        Assert.Equal(219, absence.Length);
        Assert.Equal(327, prepared.Length);
        // Verified facts must equal the exact reference.
        Assert.Throws<PrivateObjectStorageException>(() => BlobRecordCodec.Encode(new(BlobRecordKind.Prepared, context,
            BlobRecordCodec.Digest(bound), Guid.NewGuid(), DateTimeOffset.UtcNow, 0x1234, null, reference.Digest, reference.ByteCount + 1, 1)));
        // The owner digest covers the report claim's full canonical key.
        Assert.NotEqual(BlobRecordCodec.OwnerDigest(owner), BlobRecordCodec.OwnerDigest(PrivateStorageV2Data.Owner(reference)));
        Assert.Equal(BlobRecordCodec.OwnerDigest(owner), BlobRecordCodec.OwnerDigest(owner));
    }

    [AzuriteFact]
    public async Task AdmissionRequiresAnExistingPrivateInitializedContainerAndLeavesNoProbe()
    {
        var missing = azurite.Service.GetBlobContainerClient("braid-" + Guid.NewGuid().ToString("N"));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => BlobPrivateObjectStore.InitializeAsync(
            azurite.Options(missing), azurite.Client(missing), null, null));
        Assert.False((await missing.ExistsAsync()).Value);

        foreach (var access in new[] { PublicAccessType.Blob, PublicAccessType.BlobContainer })
        {
            var exposed = await azurite.CreateContainerAsync(access);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => BlobPrivateObjectStore.InitializeAsync(
                azurite.Options(exposed), azurite.Client(exposed), null, null));
            await Assert.ThrowsAsync<RequestFailedException>(() => exposed.GetBlobClient(BlobLayout.MarkerName).GetPropertiesAsync());
        }

        var container = await azurite.CreateContainerAsync();
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => BlobPrivateObjectStore.OpenAsync(
            azurite.Options(container), azurite.Client(container), null, null));
        await using (var store = await BlobPrivateObjectStore.InitializeAsync(azurite.Options(container), azurite.Client(container), null, null))
        {
            var marker = (await container.GetBlobClient(BlobLayout.MarkerName).DownloadContentAsync()).Value;
            Assert.Equal("braid-private-storage-blob-v1\n" + store.RootId.ToString("N") + "\n" + store.ControlId.ToString("N") + "\n",
                marker.Content.ToString());
            Assert.Equal("marker", marker.Details.Metadata["braidkind"]);
        }
        await using (await BlobPrivateObjectStore.OpenAsync(azurite.Options(container), azurite.Client(container), null, null)) { }
        Assert.Equal([BlobLayout.MarkerName], await NamesAsync(container));
        // An existing store is never re-initialized or adopted.
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => BlobPrivateObjectStore.InitializeAsync(
            azurite.Options(container), azurite.Client(container), null, null));

        var occupied = await azurite.CreateContainerAsync();
        await occupied.UploadBlobAsync("unrelated", BinaryData.FromString("x"));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => BlobPrivateObjectStore.InitializeAsync(
            azurite.Options(occupied), azurite.Client(occupied), null, null));
        Assert.Equal(["unrelated"], await NamesAsync(occupied));
    }

    [AzuriteTheory]
    [InlineData("braid-private-storage-blob-v1\n")]
    [InlineData("braid-private-storage-v2\n00000000000000000000000000000001\n00000000000000000000000000000002\n")]
    [InlineData("braid-private-storage-blob-v1\n0000000000000000000000000000000A\n00000000000000000000000000000002\n")]
    [InlineData("braid-private-storage-blob-v1\n00000000000000000000000000000001\n00000000000000000000000000000001\n")]
    [InlineData("braid-private-storage-blob-v1\n00000000000000000000000000000001\n00000000000000000000000000000002")]
    public async Task MalformedMarkersAreRefused(string marker)
    {
        var container = await azurite.CreateContainerAsync();
        await container.GetBlockBlobClient(BlobLayout.MarkerName).UploadAsync(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(marker)),
            new BlobUploadOptions { Metadata = new Dictionary<string, string> { ["braidkind"] = "marker" } });
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => BlobPrivateObjectStore.OpenAsync(
            azurite.Options(container), azurite.Client(container), null, null));
    }

    [AzuriteFact]
    public async Task AMarkerWithoutItsMetadataFormIsRefused()
    {
        var container = await azurite.CreateContainerAsync();
        await using (await BlobPrivateObjectStore.InitializeAsync(azurite.Options(container), azurite.Client(container), null, null)) { }
        await container.GetBlobClient(BlobLayout.MarkerName).SetMetadataAsync(new Dictionary<string, string>());
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => BlobPrivateObjectStore.OpenAsync(
            azurite.Options(container), azurite.Client(container), null, null));
    }

    [AzuriteFact]
    public async Task StagedBlocksAreInvisibleUntilTheCreateOnlyCommit()
    {
        await using var harness = await BlobHarnessAsync();
        var registry = new PrivateStorageV2PhysicalRegistry();
        var boundary = new BlobBoundary(BlobStoragePhase.PayloadStaged);
        await using var store = await InitializeAsync(harness, registry, boundary);
        var bytes = PrivateStorageData.Pattern(3 * 1024 * 1024 + 5);
        var reference = PrivateStorageData.Reference(bytes, PrivateObjectClass.DerivedArtifact);
        var write = store.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes));
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
            var blocks = (await harness.Container.GetBlockBlobClient(BlobLayout.FinalName(reference.Id))
                .GetBlockListAsync(BlockListTypes.Uncommitted)).Value;
            Assert.Equal(4, blocks.UncommittedBlocks.Count());
            Assert.Empty(blocks.CommittedBlocks);
        }
        finally { boundary.Release(); }
        Assert.Equal(PrivateObjectPublishResult.Created, await write);
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
    }

    [AzuriteFact]
    public async Task ARetirementFenceDefeatsAStalePublisherWhoseLeasesExpiredBeforeItsCommit()
    {
        await using var harness = await BlobHarnessAsync();
        var registry = new PrivateStorageV2PhysicalRegistry();
        var boundary = new BlobBoundary(BlobStoragePhase.CommitPending);
        await using var stale = await InitializeAsync(harness, registry, boundary);
        await using var cleanup = await harness.OpenStoreAsync();
        var bytes = PrivateStorageData.Pattern(2049);
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        var write = stale.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, new MemoryStream(bytes));
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Simulate the stalled holder's lease expiring; the object gate is then free.
            Assert.Equal(PrivateObjectRetirementOutcome.Busy, await cleanup.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
            await BreakAsync(harness.Container, BlobLayout.ObjectGate(harness.ControlId, reference.Id));
            Assert.Equal(PrivateObjectRetirementOutcome.Missing, await cleanup.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
        }
        finally { boundary.Release(); }
        // The create-only commit cannot replace the permanent fence on the final name.
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, async () => { _ = await write; });
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => cleanup.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Null(await harness.FinalBytesAsync(reference));
        Assert.Equal(0, await harness.TemporarySlotsAsync());
    }

    [AzuriteFact]
    public async Task AnAbandonedPublicationIsRecoveredAfterItsLeaseLapsesAndItsWriterCannotContinue()
    {
        await using var harness = await BlobHarnessAsync();
        var registry = new PrivateStorageV2PhysicalRegistry();
        var boundary = new BlobBoundary(BlobStoragePhase.PayloadPrepared);
        await using var writer = await InitializeAsync(harness, registry, boundary);
        await using var cleanup = await harness.OpenStoreAsync();
        var bytes = PrivateStorageData.Pattern(70_001);
        var reference = PrivateStorageData.Reference(bytes);
        var write = writer.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes));
        var row = default(PrivatePublicationRecord);
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            row = registry.Single;
            var claim = PrivateStorageV2Data.TemporaryClaim(row);
            Assert.Equal(PrivateObjectRetirementOutcome.Busy, await cleanup.RetireAsync(claim));
            Assert.Equal(1, await harness.TemporarySlotsAsync());
            await BreakAsync(harness.Container, BlobLayout.TempGate(harness.ControlId, reference.Id, row.Reservation.Key.TempId));
            await BreakAsync(harness.Container, BlobLayout.AttemptName(reference.Id, row.Reservation.Key.TempId));
            await BreakAsync(harness.Container, BlobLayout.ObjectGate(harness.ControlId, reference.Id));
            Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await cleanup.RetireAsync(claim));
            Assert.Equal(PrivateObjectRetirementOutcome.Missing, await cleanup.RetireAsync(claim));
            Assert.Equal(0, await harness.TemporarySlotsAsync());
        }
        finally { boundary.Release(); }
        // The resumed writer lost its gate; it cannot publish its now-fenced attempt.
        var error = await Assert.ThrowsAsync<PrivateObjectStorageException>(async () => { _ = await write; });
        Assert.Contains(error.Error, new[] { PrivateObjectStorageError.Io, PrivateObjectStorageError.Missing });
        Assert.Null(await harness.FinalBytesAsync(reference));
        // A fenced attempt name can never hold a new attempt marker.
        Assert.False(await TryCreateAsync(harness.Container, BlobLayout.AttemptName(reference.Id, row!.Reservation.Key.TempId)));
    }

    [AzuriteFact]
    public async Task AGateLeaseLeftByADeadHolderExpiresWithoutIntervention()
    {
        await using var harness = await BlobHarnessAsync();
        var registry = new PrivateStorageV2PhysicalRegistry();
        await using var store = await InitializeAsync(harness, registry);
        var bytes = "expires"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        await store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, new MemoryStream(bytes));
        // A finite lease that its holder never renews or releases, as after process death.
        await harness.Container.GetBlobClient(BlobLayout.ObjectGate(harness.ControlId, reference.Id)).GetBlobLeaseClient()
            .AcquireAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(PrivateObjectRetirementOutcome.Busy, await store.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
        await Task.Delay(TimeSpan.FromSeconds(16));
        Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await store.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
    }

    [AzuriteFact]
    public async Task AReplacedAttemptMarkerCannotBePublishedOrRetiredAsTheOriginal()
    {
        await using var harness = await BlobHarnessAsync();
        var registry = new PrivateStorageV2PhysicalRegistry();
        var boundary = new BlobBoundary(BlobStoragePhase.PayloadPrepared);
        await using var store = await InitializeAsync(harness, registry, boundary);
        var bytes = PrivateStorageData.Pattern(113);
        var reference = PrivateStorageData.Reference(bytes);
        var write = store.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes));
        var attempt = "";
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            attempt = BlobLayout.AttemptName(reference.Id, registry.Single.Reservation.Key.TempId);
            await BreakAsync(harness.Container, attempt);
            await harness.Container.GetBlockBlobClient(attempt).UploadAsync(new MemoryStream([]),
                new BlobUploadOptions { Metadata = BlobFacts.AttemptMetadata(0x0fedcba987654321) });
        }
        finally { boundary.Release(); }
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, async () => { _ = await write; });
        Assert.Null(await harness.FinalBytesAsync(reference));
        Assert.True((await harness.Container.GetBlobClient(attempt).ExistsAsync()).Value);
        Assert.Equal(PrivateObjectRetirementOutcome.UnsafeStorage, await store.RetireAsync(PrivateStorageV2Data.TemporaryClaim(registry.Single)));
        Assert.True((await harness.Container.GetBlobClient(attempt).ExistsAsync()).Value);
    }

    [AzuriteFact]
    public async Task TamperedObjectsAndSealsFailClosed()
    {
        await using var harness = await BlobHarnessAsync();
        var registry = new PrivateStorageV2PhysicalRegistry();
        await using var store = await InitializeAsync(harness, registry);
        var bytes = PrivateStorageData.Pattern(4096);
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        await store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, new MemoryStream(bytes));
        var row = registry.Single;
        var final = harness.Container.GetBlockBlobClient(BlobLayout.FinalName(reference.Id));
        var metadata = (await final.GetPropertiesAsync()).Value.Metadata;

        var altered = (byte[])bytes.Clone();
        altered[100] ^= 0x40;
        await final.UploadAsync(new MemoryStream(altered), new BlobUploadOptions { Metadata = metadata });
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Integrity, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Conflict, () => store.PublishCreateOnlyAsync(
            owner, reference.Id.Scope, reference, new MemoryStream(bytes)));

        await final.UploadAsync(new MemoryStream(bytes), new BlobUploadOptions());
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(PrivateObjectRetirementOutcome.UnsafeStorage, await store.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
        Assert.Equal(bytes, (await final.DownloadContentAsync()).Value.Content.ToArray());

        var seal = harness.Container.GetBlockBlobClient(harness.SealName(row.Reservation.Key, "reservation"));
        var sealBytes = (await seal.DownloadContentAsync()).Value.Content.ToArray();
        sealBytes[^1] ^= 1;
        await seal.UploadAsync(new MemoryStream(sealBytes), new BlobUploadOptions { Metadata = BlobFacts.Metadata(BlobForm.Seal) });
        Assert.Equal(PrivateObjectRetirementOutcome.UnsafeStorage, await store.RetireAsync(PrivateStorageV2Data.TemporaryClaim(row)));
    }

    [AzuriteFact]
    public async Task ObservationSkipsFencesAndReportsUnknownEntriesWithoutTraversingThem()
    {
        await using var harness = await BlobHarnessAsync();
        var registry = new PrivateStorageV2PhysicalRegistry();
        await using var store = await InitializeAsync(harness, registry);
        var scope = PrivateStorageData.Scope();
        var kept = PrivateStorageData.Reference("kept"u8.ToArray(), scope: scope);
        var retired = PrivateStorageData.Reference("retired"u8.ToArray(), scope: scope);
        foreach (var reference in new[] { kept, retired })
            await store.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), scope, reference,
                new MemoryStream(reference == kept ? "kept"u8.ToArray() : "retired"u8.ToArray()));
        Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await store.RetireAsync(
            PrivateStorageV2Data.FinalClaim(retired, PrivateStorageV2Data.Owner(retired))));
        var revision = BlobLayout.RevisionPrefix(PrivateObjectClass.NativeArtifact, scope);
        var project = BlobLayout.ProjectPrefix(PrivateObjectClass.NativeArtifact, new(scope.OrganizationId, scope.ProjectId));
        await harness.Container.UploadBlobAsync(revision + "notes.txt", BinaryData.FromString("x"));
        await harness.Container.UploadBlobAsync(project + "not-a-mapping/child", BinaryData.FromString("x"));
        await harness.Container.UploadBlobAsync(revision + Guid.NewGuid().ToString("N") + ".blob", BinaryData.FromString("unformed"));

        var page = await store.ObserveAsync(new(scope.OrganizationId, scope.ProjectId));
        var slot = Assert.Single(page.Entries, entry => entry.Kind == PrivateStorageEntryKind.CandidateSlot).Slot!;
        Assert.Equal(kept.Id, slot.ObjectId);
        Assert.Equal(2, page.Entries.Count(entry => entry.Kind == PrivateStorageEntryKind.LayoutDirectory));
        var unproved = page.Entries.Where(entry => entry.Kind == PrivateStorageEntryKind.Unproved).ToArray();
        Assert.Equal(3, unproved.Length);
        Assert.Contains(unproved, entry => entry.Reason == PrivateStorageUnprovedReason.UnknownName &&
            entry.Level == PrivateStorageEntryLevel.Mapping);
        Assert.Contains(unproved, entry => entry.Reason == PrivateStorageUnprovedReason.UnknownName &&
            entry.Level == PrivateStorageEntryLevel.Payload);
        Assert.Contains(unproved, entry => entry.Reason == PrivateStorageUnprovedReason.UnsafeFacts);
        Assert.Equal(1, page.UntraversedSubtreesObserved);
        Assert.False(page.TraversalComplete);
        Assert.True(page.NativeTraversalComplete);
        Assert.True(page.DerivedTraversalComplete);
    }

    [AzuriteFact]
    public async Task ObservationStopsAtItsEntryBound()
    {
        await using var harness = await BlobHarnessAsync();
        await using var store = await InitializeAsync(harness, new PrivateStorageV2PhysicalRegistry());
        var scope = PrivateStorageData.Scope();
        var revision = BlobLayout.RevisionPrefix(PrivateObjectClass.NativeArtifact, scope);
        for (var index = 0; index < 105; index++)
            await harness.Container.UploadBlobAsync(revision + $"unknown-{index:D3}", BinaryData.FromString("x"));
        var page = await store.ObserveAsync(new(scope.OrganizationId, scope.ProjectId));
        Assert.Equal(100, page.ExaminedCount);
        Assert.True(page.EntryLimitReached);
        Assert.False(page.NativeTraversalComplete);
    }

    private async Task<BlobStoreHarness> BlobHarnessAsync() =>
        (BlobStoreHarness)await StoreHarness.CreateAsync(StoreHarness.Blob, azurite);

    private async Task<BlobPrivateObjectStore> InitializeAsync(BlobStoreHarness harness, PrivateStorageV2PhysicalRegistry registry,
        BlobBoundary? boundary = null) => await BlobPrivateObjectStore.InitializeAsync(harness.Options, azurite.Client(harness.Container),
            () => registry, boundary is null ? null : new BlobObserver(boundary.ObserveAsync));

    private static async Task BreakAsync(BlobContainerClient container, string name) =>
        await container.GetBlobClient(name).GetBlobLeaseClient().BreakAsync(TimeSpan.Zero);

    [AzuriteFact]
    public async Task ACommitDelayedAfterValidationCannotPublishAfterItsTemporaryWasRetired()
    {
        await using var harness = await BlobHarnessAsync();
        var registry = new PrivateStorageV2PhysicalRegistry();
        var delay = new DelayedCommit();
        var clientOptions = new BlobClientOptions();
        clientOptions.AddPolicy(delay, HttpPipelinePosition.PerCall);
        var client = new BlobContainerClient(harness.Options.ContainerUri,
            new StorageSharedKeyCredential(BlobStorageOptions.EmulatorAccount, AzuriteFixture.EmulatorKey), clientOptions);
        await using var writer = await BlobPrivateObjectStore.InitializeAsync(harness.Options, client, () => registry, null);
        await using var cleanup = await harness.OpenStoreAsync();
        var bytes = PrivateStorageData.Pattern(2049);
        var reference = PrivateStorageData.Reference(bytes);
        delay.Path = harness.Container.Uri.AbsolutePath + "/" + BlobLayout.FinalName(reference.Id);
        var write = writer.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes));
        try
        {
            await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var row = registry.Single;
            var claim = PrivateStorageV2Data.TemporaryClaim(row);
            Assert.Equal(PrivateObjectRetirementOutcome.Busy, await cleanup.RetireAsync(claim));
            await BreakAsync(harness.Container, BlobLayout.TempGate(harness.ControlId, reference.Id, row.Reservation.Key.TempId));
            await BreakAsync(harness.Container, BlobLayout.AttemptName(reference.Id, row.Reservation.Key.TempId));
            await BreakAsync(harness.Container, BlobLayout.ObjectGate(harness.ControlId, reference.Id));
            Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await cleanup.RetireAsync(claim));
        }
        finally { delay.Release.TrySetResult(); }
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io, async () => { _ = await write; });
        Assert.Null(await harness.FinalBytesAsync(reference));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => cleanup.ReadVerifiedAsync(reference.Id.Scope, reference));

        // Retiring one failed attempt does not retire the final name or unrelated work.
        delay.Path = null;
        Assert.Equal(PrivateObjectPublishResult.Created, await writer.PublishCreateOnlyAsync(
            PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes)));
        Assert.Equal(bytes, await cleanup.ReadVerifiedAsync(reference.Id.Scope, reference));
        var otherBytes = "independent publication"u8.ToArray();
        var other = PrivateStorageData.Reference(otherBytes);
        Assert.Equal(PrivateObjectPublishResult.Created, await writer.PublishCreateOnlyAsync(
            PrivateStorageV2Data.Owner(other), other.Id.Scope, other, new MemoryStream(otherBytes)));
        Assert.Equal(otherBytes, await cleanup.ReadVerifiedAsync(other.Id.Scope, other));
    }

    private sealed class DelayedCommit : HttpPipelinePolicy
    {
        internal string? Path;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) => ProcessNext(message, pipeline);
        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            var uri = message.Request.Uri.ToUri();
            if (uri.AbsolutePath == Path && uri.Query.Contains("comp=blocklist", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(message.CancellationToken);
            }
            await ProcessNextAsync(message, pipeline);
        }
    }

    [AzuriteFact]
    public async Task ADelayedTemporaryFenceCannotWriteAfterItsOwnTargetLeaseIsLost()
    {
        await using var harness = await BlobHarnessAsync();
        var registry = new PrivateStorageV2PhysicalRegistry();
        var boundary = new BlobBoundary(BlobStoragePhase.PayloadPrepared);
        await using var writer = await InitializeAsync(harness, registry, boundary);
        var delay = new DelayedCommit();
        var options = new BlobClientOptions();
        options.AddPolicy(delay, HttpPipelinePosition.PerCall);
        var client = new BlobContainerClient(harness.Options.ContainerUri,
            new StorageSharedKeyCredential(BlobStorageOptions.EmulatorAccount, AzuriteFixture.EmulatorKey), options);
        await using var cleanup = await BlobPrivateObjectStore.OpenAsync(harness.Options, client, null, null);
        var bytes = PrivateStorageData.Pattern(1025);
        var reference = PrivateStorageData.Reference(bytes);
        var write = writer.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes));
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var row = registry.Single;
            var attemptName = BlobLayout.AttemptName(reference.Id, row.Reservation.Key.TempId);
            await BreakAsync(harness.Container, BlobLayout.TempGate(harness.ControlId, reference.Id, row.Reservation.Key.TempId));
            await BreakAsync(harness.Container, attemptName);
            await BreakAsync(harness.Container, BlobLayout.ObjectGate(harness.ControlId, reference.Id));
            delay.Path = harness.Container.Uri.AbsolutePath + "/" + attemptName;
            var retire = cleanup.RetireAsync(PrivateStorageV2Data.TemporaryClaim(row));
            try
            {
                await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await BreakAsync(harness.Container, attemptName);
            }
            finally { delay.Release.TrySetResult(); }
            Assert.Equal(PrivateObjectRetirementOutcome.Io, await retire);
            Assert.Equal("attempt", (await harness.Container.GetBlobClient(attemptName).GetPropertiesAsync()).Value.Metadata[BlobFacts.KindKey]);
        }
        finally { boundary.Release(); }
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io, async () => { _ = await write; });
        Assert.Null(await harness.FinalBytesAsync(reference));
    }

    private static async Task<bool> TryCreateAsync(BlobContainerClient container, string name)
    {
        try
        {
            await container.GetBlockBlobClient(name).UploadAsync(new MemoryStream([]), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                Metadata = BlobFacts.AttemptMetadata(0x42)
            });
            return true;
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412) { return false; }
    }

    private static async Task<List<string>> NamesAsync(BlobContainerClient container)
    {
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(new GetBlobsOptions())) names.Add(item.Name);
        return names;
    }

    private sealed class BlobBoundary(BlobStoragePhase phase)
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entered;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => release.TrySetResult();
        internal async ValueTask ObserveAsync(BlobStorageObservation observation, CancellationToken cancellationToken)
        {
            if (observation.Phase != phase || Interlocked.Exchange(ref entered, 1) != 0) return;
            Entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
    }
}
