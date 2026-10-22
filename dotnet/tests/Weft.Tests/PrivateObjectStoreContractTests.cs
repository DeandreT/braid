using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>
/// The claim-aware private store contract, run against the local filesystem store and, when the
/// owned emulator is configured, the Azure Blob store. Registries and cleanup claims are synthetic.
/// </summary>
[SupportedOSPlatform("linux")]
[Collection(AzuriteCollection.Name)]
public sealed class PrivateObjectStoreContractTests(AzuriteFixture azurite)
{
    public static TheoryData<string> Stores()
    {
        var stores = new TheoryData<string> { StoreHarness.FileSystem };
        if (AzuriteFixture.IsConfigured) stores.Add(StoreHarness.Blob);
        return stores;
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task OpeningNeverResolvesTheRegistryAndPlainPublicationRefusesBeforeAnySourceRead(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var resolutions = 0;
        IPrivateObjectPublicationRegistry Factory() { resolutions++; throw new InvalidOperationException(); }
        await using (await harness.InitializeAsync(Factory)) { }
        await using var store = await harness.OpenAsync(Factory);
        Assert.Equal(0, resolutions);
        var bytes = "opaque payload"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        using var source = new PrivateStorageInput(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Unsupported,
            () => store.Publication.PublishCreateOnlyAsync(reference.Id.Scope, reference, source));
        Assert.Equal(0, source.ReadCalls);
        await using var withoutRegistry = await harness.OpenAsync();
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Unsupported,
            () => withoutRegistry.PublishAsync(PrivateStorageV2Data.Owner(reference), reference, source));
        Assert.Equal(0, source.ReadCalls);
        Assert.Null(await harness.FinalBytesAsync(reference));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task PublicationRegistersAndBindsBeforeReadingAndTheBoundTemporaryIsTheObservedSlot(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        foreach (var objectClass in new[] { PrivateObjectClass.NativeArtifact, PrivateObjectClass.DerivedArtifact })
        {
            var bytes = PrivateStorageData.Pattern(objectClass == PrivateObjectClass.NativeArtifact ? 65539 : 2 * 1024 * 1024 + 17);
            var reference = PrivateStorageData.Reference(bytes, objectClass);
            using var input = new PrivateStorageInput(bytes, chunkSize: 997);
            var registry = new PrivateStorageV2PhysicalRegistry
            {
                BeforeReserve = () => Assert.Equal(0, input.ReadCalls),
                BeforeBind = () => Assert.Equal(0, input.ReadCalls)
            };
            var boundary = new ContractBoundary(ContractPhase.BindingAcknowledged);
            await using var store = await (objectClass == PrivateObjectClass.NativeArtifact
                ? harness.InitializeAsync(() => registry, boundary.ObserveAsync)
                : harness.OpenAsync(() => registry, boundary.ObserveAsync));
            var write = store.PublishAsync(PrivateStorageV2Data.Owner(reference), reference, input);
            try
            {
                await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(0, input.ReadCalls);
                var row = registry.Single;
                var page = await store.Observer.ObserveAsync(new(reference.Id.Scope.OrganizationId, reference.Id.Scope.ProjectId));
                var slot = Assert.Single(page.Entries, entry => entry.Kind == PrivateStorageEntryKind.CandidateSlot).Slot!;
                Assert.Equal(CleanupTargetKind.TrackedTemporary, slot.Kind);
                Assert.Equal(row.Reservation.Key.TempId, slot.TempId);
                Assert.Equal(row.Binding!.ActualIdentity, slot.ActualIdentity);
                Assert.True(page.TraversalComplete);
            }
            finally { boundary.Release(); }
            Assert.Equal(PrivateObjectPublishResult.Created, await write);
            Assert.Equal(1, registry.ReserveCalls);
            Assert.Equal(1, registry.BindCalls);
            Assert.Equal(0, await harness.TemporarySlotsAsync());
            Assert.Equal(bytes, await store.ReadAsync(reference));
            Assert.Equal(bytes, await harness.FinalBytesAsync(reference));
            Assert.True(input.CanRead);
            var final = await store.Observer.ObserveAsync(new(reference.Id.Scope.OrganizationId, reference.Id.Scope.ProjectId));
            var observed = Assert.Single(final.Entries, entry => entry.Kind == PrivateStorageEntryKind.CandidateSlot).Slot!;
            Assert.Equal(reference.Id, observed.ObjectId);
            // A final object retains its creating publication's file identity.
            Assert.Equal(registry.Single.Binding!.ActualIdentity, observed.ActualIdentity);
            Assert.Equal(PrivateObjectRetirementOutcome.Missing, await store.RetireAsync(PrivateStorageV2Data.TemporaryClaim(registry.Single)));
            Assert.Equal(bytes, await store.ReadAsync(reference));
        }
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task EmptyDerivedObjectsArePublishedAndReadExactly(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var registry = new PrivateStorageV2PhysicalRegistry();
        await using var store = await harness.InitializeAsync(() => registry);
        var reference = PrivateStorageData.Reference([], PrivateObjectClass.DerivedArtifact);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishAsync(PrivateStorageV2Data.Owner(reference), reference, new MemoryStream()));
        Assert.Empty(await store.ReadAsync(reference));
        var native = PrivateStorageData.Reference([]);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Limit,
            () => store.PublishAsync(PrivateStorageV2Data.Owner(native), native, new MemoryStream()));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task ScopeLimitsAndExactBytesAreEnforcedBeforePublicationAndOnRead(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var registry = new PrivateStorageV2PhysicalRegistry();
        await using var store = await harness.InitializeAsync(() => registry);
        var bytes = PrivateStorageData.Pattern(4099);
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.ScopeMismatch, () => store.Publication.PublishCreateOnlyAsync(
            owner, PrivateStorageData.Scope(), reference, new MemoryStream(bytes)));
        var oversized = new PrivateObjectReference(reference.Id, reference.Digest, PrivateStorageTestRoot.NativeLimit + 1L);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Limit,
            () => store.PublishAsync(owner, oversized, new MemoryStream(bytes)));
        var altered = (byte[])bytes.Clone();
        altered[^1] ^= 1;
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Integrity,
            () => store.PublishAsync(owner, reference, new MemoryStream(altered)));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Integrity,
            () => store.PublishAsync(owner, reference, new MemoryStream([.. bytes, 0])));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Integrity,
            () => store.PublishAsync(owner, reference, new MemoryStream(bytes[..^1])));
        Assert.Null(await harness.FinalBytesAsync(reference));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadAsync(reference));
        Assert.Equal(0, await harness.TemporarySlotsAsync());

        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishAsync(owner, reference, new MemoryStream(bytes)));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Integrity, () => store.ReadAsync(
            new PrivateObjectReference(reference.Id, Sha256Digest.FromBytes(altered), bytes.Length)));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Integrity, () => store.ReadAsync(
            new PrivateObjectReference(reference.Id, reference.Digest, bytes.Length - 1)));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.ScopeMismatch, () => store.Publication.ReadVerifiedAsync(
            PrivateStorageData.Scope(), reference));
        Assert.Equal(bytes, await store.ReadAsync(reference));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task IndependentAdaptersVerifyAlreadyPresentAndNeverReplaceDifferentContent(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var registry = new PrivateStorageV2PhysicalRegistry();
        var bytes = "original exact bytes"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        await using (var first = await harness.InitializeAsync(() => registry))
            Assert.Equal(PrivateObjectPublishResult.Created, await first.PublishAsync(
                PrivateStorageV2Data.Owner(reference), reference, new MemoryStream(bytes)));
        await using var second = await harness.OpenAsync(() => registry);
        Assert.Equal(PrivateObjectPublishResult.AlreadyPresent, await second.PublishAsync(
            PrivateStorageV2Data.Owner(reference), reference, new MemoryStream(bytes)));
        var replacement = "other bytes"u8.ToArray();
        var conflict = new PrivateObjectReference(reference.Id, Sha256Digest.FromBytes(replacement), replacement.LongLength);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Conflict, () => second.PublishAsync(
            PrivateStorageV2Data.Owner(conflict), conflict, new MemoryStream(replacement)));
        Assert.Equal(bytes, await harness.FinalBytesAsync(reference));
        Assert.Equal(bytes, await second.ReadAsync(reference));
        Assert.Equal(0, await harness.TemporarySlotsAsync());
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task ConcurrentIndependentPublishersCreateAtMostOnceAndNeverMixContent(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var registry = new PrivateStorageV2PhysicalRegistry();
        var gate = new object();
        await using (await harness.InitializeAsync()) { }
        var bytes = PrivateStorageData.Pattern(300_001);
        var reference = PrivateStorageData.Reference(bytes);
        var stores = new List<ContractStore>();
        try
        {
            for (var index = 0; index < 4; index++) stores.Add(await harness.OpenAsync(() => new SynchronizedRegistry(registry, gate)));
            var results = await Task.WhenAll(stores.Select(async store =>
            {
                try
                {
                    return ((PrivateObjectPublishResult?)await store.PublishAsync(PrivateStorageV2Data.Owner(reference), reference,
                        new MemoryStream(bytes)), (PrivateObjectStorageError?)null);
                }
                catch (PrivateObjectStorageException error) { return ((PrivateObjectPublishResult?)null, (PrivateObjectStorageError?)error.Error); }
            }));
            Assert.True(results.Count(result => result.Item1 == PrivateObjectPublishResult.Created) <= 1);
            Assert.All(results, result => Assert.True(result.Item1 is not null || result.Item2 == PrivateObjectStorageError.Io));
            // A retry after contention always settles on the one exact object.
            var created = results.Count(result => result.Item1 == PrivateObjectPublishResult.Created);
            Assert.Equal(created == 1 ? PrivateObjectPublishResult.AlreadyPresent : PrivateObjectPublishResult.Created,
                await stores[0].PublishAsync(PrivateStorageV2Data.Owner(reference), reference, new MemoryStream(bytes)));
            Assert.Equal(bytes, await harness.FinalBytesAsync(reference));
            var different = PrivateStorageData.Pattern(300_001).Select(value => (byte)~value).ToArray();
            var conflict = new PrivateObjectReference(reference.Id, Sha256Digest.FromBytes(different), different.Length);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Conflict, () => stores[1].PublishAsync(
                PrivateStorageV2Data.Owner(conflict), conflict, new MemoryStream(different)));
            Assert.Equal(bytes, await stores[2].ReadAsync(reference));
            Assert.Equal(0, await harness.TemporarySlotsAsync());
        }
        finally
        {
            foreach (var store in stores) await store.DisposeAsync();
        }
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task SyntheticLostReserveAndBindAcknowledgementsResolveOnlyTheOriginalNaturalKeyBeforeAnyRead(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var bytes = PrivateStorageData.Pattern(71);
        var reference = PrivateStorageData.Reference(bytes);
        using var input = new PrivateStorageInput(bytes);
        var registry = new PrivateStorageV2PhysicalRegistry
        {
            ThrowAfterReserve = true,
            ThrowAfterBind = true,
            BeforeReserve = () => Assert.Equal(0, input.ReadCalls),
            BeforeBind = () => Assert.Equal(0, input.ReadCalls)
        };
        await using var store = await harness.InitializeAsync(() => registry);
        var owner = PrivateStorageV2Data.Owner(reference);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishAsync(owner, reference, input));
        Assert.Equal(2, registry.GetCalls);
        Assert.Equal(owner, registry.Single.Reservation.Owner);
        Assert.NotNull(registry.Single.Binding);
        Assert.Equal(bytes, await store.ReadAsync(reference));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task OwnerRefusalLeavesAbsenceEvidenceButCannotCreateOrReadPayload(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var registry = new PrivateStorageV2PhysicalRegistry { RefuseOwner = true };
        await using var store = await harness.InitializeAsync(() => registry);
        var bytes = "refused"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        using var input = new PrivateStorageInput(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io, () => store.PublishAsync(
            PrivateStorageV2Data.Owner(reference), reference, input));
        var key = registry.LastReserveKey;
        Assert.Equal(0, input.ReadCalls);
        Assert.Equal(0, registry.BindCalls);
        Assert.Equal(0, await harness.TemporarySlotsAsync());
        Assert.True(await harness.SealPresentAsync(key!, "absence"));
        Assert.False(await harness.SealPresentAsync(key!, "reservation"));
        Assert.Null(await harness.FinalBytesAsync(reference));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task PermanentFinalRetirementBlocksLaterReadAndPublicationWithoutRegistryCalls(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var registry = new PrivateStorageV2PhysicalRegistry();
        var bytes = "retired object"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        await using var store = await harness.InitializeAsync(() => registry);
        await store.PublishAsync(owner, reference, new MemoryStream(bytes));
        registry.RefuseEveryCall = true;
        var claim = PrivateStorageV2Data.FinalClaim(reference, owner);
        Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await store.RetireAsync(claim));
        Assert.Null(await harness.FinalBytesAsync(reference));
        Assert.Equal(PrivateObjectRetirementOutcome.Missing, await store.RetireAsync(claim));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadAsync(reference));
        using var source = new PrivateStorageInput(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.PublishAsync(owner, reference, source));
        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(1, registry.ReserveCalls);
        Assert.Equal(1, registry.BindCalls);
        Assert.Equal(0, registry.GetCalls);
        var page = await store.Observer.ObserveAsync(new(reference.Id.Scope.OrganizationId, reference.Id.Scope.ProjectId));
        Assert.DoesNotContain(page.Entries, entry => entry.Kind == PrivateStorageEntryKind.CandidateSlot);
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task NeverWrittenObjectIsFencedMissingAndCannotBePublishedAfterwards(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var registry = new PrivateStorageV2PhysicalRegistry();
        await using var store = await harness.InitializeAsync(() => registry);
        var bytes = "missing"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadAsync(reference));
        Assert.Equal(PrivateObjectRetirementOutcome.Missing, await store.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
        using var source = new PrivateStorageInput(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.PublishAsync(owner, reference, source));
        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(0, registry.ReserveCalls);
        Assert.Null(await harness.FinalBytesAsync(reference));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task AnIndependentlyHeldObjectGateMakesRetirementBusyAndPublicationRefuseBeforeReading(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var registry = new PrivateStorageV2PhysicalRegistry();
        await using var store = await harness.InitializeAsync(() => registry);
        var bytes = "gated"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishAsync(owner, reference, new MemoryStream(bytes)));
        await using (await harness.HoldObjectGateAsync(reference.Id))
        {
            Assert.Equal(PrivateObjectRetirementOutcome.Busy, await store.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
            using var input = new PrivateStorageInput(bytes);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io, () => store.PublishAsync(owner, reference, input));
            Assert.Equal(0, input.ReadCalls);
            Assert.Equal(1, registry.ReserveCalls);
            Assert.Equal(bytes, await harness.FinalBytesAsync(reference));
        }
        Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await store.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task ALivePublisherKeepsItsTemporaryAndFinalGatesBusy(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var registry = new PrivateStorageV2PhysicalRegistry();
        var boundary = new ContractBoundary(ContractPhase.FinalGateAcquired);
        await using var store = await harness.InitializeAsync(() => registry, boundary.ObserveAsync);
        await using var cleanup = await harness.OpenAsync();
        var bytes = PrivateStorageData.Pattern(5000);
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        var write = store.PublishAsync(owner, reference, new MemoryStream(bytes));
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(PrivateObjectRetirementOutcome.Busy, await cleanup.RetireAsync(PrivateStorageV2Data.TemporaryClaim(registry.Single)));
            Assert.Equal(PrivateObjectRetirementOutcome.Busy, await cleanup.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
        }
        finally { boundary.Release(); }
        Assert.Equal(PrivateObjectPublishResult.Created, await write);
        Assert.Equal(PrivateObjectRetirementOutcome.Missing, await cleanup.RetireAsync(PrivateStorageV2Data.TemporaryClaim(registry.Single)));
        Assert.Equal(bytes, await cleanup.ReadAsync(reference));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task CapturedReservationWithoutLocalAcknowledgementCanFenceOnlyAnActuallyAbsentTemporary(string kind)
    {
        foreach (var presentBytes in new[] { -1, 0, 1 })
        {
            await using var harness = await StoreHarness.CreateAsync(kind, azurite);
            var registry = new PrivateStorageV2PhysicalRegistry();
            var reference = PrivateStorageData.Reference("absent before CreateNew"u8.ToArray());
            ValueTask Stop(ContractPhase phase, CancellationToken _) => phase == ContractPhase.ReservationAcknowledged
                ? ValueTask.FromException(new IOException("Synthetic stop after real in-memory registration.")) : ValueTask.CompletedTask;
            await using (var writer = await harness.InitializeAsync(() => registry, Stop))
                await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io, () => writer.PublishAsync(
                    PrivateStorageV2Data.Owner(reference), reference, new MemoryStream("absent before CreateNew"u8.ToArray())));
            var row = registry.Single;
            Assert.True(await harness.SealPresentAsync(row.Reservation.Key, "absence"));
            Assert.False(await harness.SealPresentAsync(row.Reservation.Key, "reservation"));
            Assert.Equal(0, await harness.TemporarySlotsAsync());
            if (presentBytes >= 0) await harness.PlantTemporaryAsync(row.Reservation.Key, presentBytes);
            registry.RefuseEveryCall = true;
            await using var cleanup = await harness.OpenAsync(() => registry);
            Assert.Equal(presentBytes < 0 ? PrivateObjectRetirementOutcome.Missing : PrivateObjectRetirementOutcome.UnsafeStorage,
                await cleanup.RetireAsync(PrivateStorageV2Data.TemporaryClaim(row)));
            Assert.Equal(presentBytes < 0 ? 0 : 1, await harness.TemporarySlotsAsync());
            Assert.Equal(0, registry.BindCalls);
            Assert.Equal(0, registry.GetCalls);
        }
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task CancellationAtCommittedBindingSettlesWithoutReadingAndLeavesSealsForMissingRecovery(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind, azurite);
        var registry = new PrivateStorageV2PhysicalRegistry();
        var boundary = new ContractBoundary(ContractPhase.BindingAcknowledged);
        await using var store = await harness.InitializeAsync(() => registry, boundary.ObserveAsync);
        var bytes = "canceled after binding"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        using var input = new PrivateStorageInput(bytes);
        using var cancellation = new CancellationTokenSource();
        var write = store.PublishAsync(PrivateStorageV2Data.Owner(reference), reference, input, cancellation.Token);
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await cancellation.CancelAsync();
        }
        finally { boundary.Release(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { _ = await write; });
        Assert.Equal(0, input.ReadCalls);
        var row = registry.Single;
        Assert.NotNull(row.Binding);
        Assert.Equal(0, await harness.TemporarySlotsAsync());
        Assert.True(await harness.SealPresentAsync(row.Reservation.Key, "bound"));
        registry.RefuseEveryCall = true;
        Assert.Equal(PrivateObjectRetirementOutcome.Missing, await store.RetireAsync(PrivateStorageV2Data.TemporaryClaim(row)));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadAsync(reference));
    }

    /// <summary>Serializes calls into one shared synthetic registry for concurrent adapters.</summary>
    private sealed class SynchronizedRegistry(PrivateStorageV2PhysicalRegistry inner, object gate) : IPrivateObjectPublicationRegistry
    {
        public Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, Guid rootId, PrivateObjectId objectId,
            Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default)
        {
            lock (gate) return inner.ReserveAsync(owner, rootId, objectId, tempId, absenceRecordDigest, cancellationToken);
        }

        public Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation, PrivatePublicationFileIdentity actualIdentity,
            Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default)
        {
            lock (gate) return inner.BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken);
        }

        public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, Guid rootId, PrivateObjectId objectId,
            Guid tempId, CancellationToken cancellationToken = default)
        {
            lock (gate) return inner.GetAsync(owner, rootId, objectId, tempId, cancellationToken);
        }
    }
}
