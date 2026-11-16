using Braid.Application;
using Braid.Domain;
using Braid.Server;

namespace Braid.Tests;

/// <summary>Composition-only authored claims and registry replies; no database or filesystem authority is implied.</summary>
public sealed class LazyPrivateObjectPublicationRegistryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructionAndEveryCanceledMethodLeaveBothCredentialFactoriesUnresolved(bool report)
    {
        var fixture = new Fixture(report);
        var resolutions = 0;
        IPrivateObjectPublicationRegistry Unexpected()
        {
            resolutions++;
            throw new InvalidOperationException("Canceled composition must not resolve credentials.");
        }
        var router = new LazyPrivateObjectPublicationRegistry(Unexpected, Unexpected);
        Assert.Equal(0, resolutions);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.ReserveAsync(fixture.Owner, PrivatePublicationStoreKind.FileSystem,
            fixture.Key.RootId, fixture.Key.Object, fixture.Key.TempId, fixture.AbsenceDigest, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.BindAsync(fixture.Reservation,
            fixture.Identity, fixture.BoundDigest, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.GetAsync(fixture.Owner, PrivatePublicationStoreKind.FileSystem,
            fixture.Key.RootId, fixture.Key.Object, fixture.Key.TempId, canceled.Token));

        Assert.Equal(0, resolutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryMethodSelectsOnlyItsCapturedOwnerBranchAndForwardsExactArgumentsTasksAndResults(bool report)
    {
        var fixture = new Fixture(report);
        var selected = new Registry(fixture);
        var resolutions = 0;
        IPrivateObjectPublicationRegistry Resolve() { resolutions++; return selected; }
        IPrivateObjectPublicationRegistry Wrong() => throw new InvalidOperationException("The other credential family was resolved.");
        var router = new LazyPrivateObjectPublicationRegistry(report ? Wrong : Resolve, report ? Resolve : Wrong);
        using var caller = new CancellationTokenSource();

        var reserve = router.ReserveAsync(fixture.Owner, PrivatePublicationStoreKind.AzureBlob, fixture.Key.RootId,
            fixture.Key.Object, fixture.Key.TempId, fixture.AbsenceDigest, caller.Token);
        Assert.Same(selected.ReserveTask, reserve);
        Assert.Same(selected.ReserveResult, await reserve);
        Assert.Same(fixture.Owner, selected.Owner);
        Assert.Equal(PrivatePublicationStoreKind.AzureBlob, selected.StoreKind);
        Assert.Same(fixture.Key.Object, selected.Object);
        Assert.Equal(fixture.Key.RootId, selected.RootId);
        Assert.Equal(fixture.Key.TempId, selected.TempId);
        Assert.Same(fixture.AbsenceDigest, selected.Digest);
        Assert.Equal(caller.Token, selected.Token);

        var bind = router.BindAsync(fixture.Reservation, fixture.Identity, fixture.BoundDigest, caller.Token);
        Assert.Same(selected.BindTask, bind);
        Assert.Same(selected.BindResult, await bind);
        Assert.Same(fixture.Reservation, selected.Reservation);
        Assert.Same(fixture.Owner, selected.Reservation!.Owner);
        Assert.Equal(PrivatePublicationStoreKind.AzureBlob, selected.StoreKind);
        Assert.Same(fixture.Identity, selected.Identity);
        Assert.Same(fixture.BoundDigest, selected.Digest);
        Assert.Equal(caller.Token, selected.Token);

        var get = router.GetAsync(fixture.Owner, PrivatePublicationStoreKind.AzureBlob, fixture.Key.RootId,
            fixture.Key.Object, fixture.Key.TempId, caller.Token);
        Assert.Same(selected.GetTask, get);
        Assert.Same(fixture.Snapshot, await get);
        Assert.Same(fixture.Owner, selected.Owner);
        Assert.Equal(PrivatePublicationStoreKind.AzureBlob, selected.StoreKind);
        Assert.Same(fixture.Key.Object, selected.Object);
        Assert.Equal(fixture.Key.RootId, selected.RootId);
        Assert.Equal(fixture.Key.TempId, selected.TempId);
        Assert.Equal(caller.Token, selected.Token);
        Assert.Equal(1, resolutions);
        Assert.Equal(3, selected.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AFailedOrNullFactoryIsNotCachedAndNeverFallsBackToTheOtherCredentialFamily(bool report, bool nullFactory)
    {
        var fixture = new Fixture(report);
        var selected = new Registry(fixture);
        var calls = 0;
        var original = new IOException("Synthetic registry configuration failure.");
        IPrivateObjectPublicationRegistry Resolve()
        {
            if (++calls == 1)
            {
                if (nullFactory) return null!;
                throw original;
            }
            return selected;
        }
        IPrivateObjectPublicationRegistry Wrong() => throw new InvalidOperationException("Credential fallback is forbidden.");
        var router = new LazyPrivateObjectPublicationRegistry(report ? Wrong : Resolve, report ? Resolve : Wrong);
        Task<PrivatePublicationSnapshot?> Get() => router.GetAsync(fixture.Owner, PrivatePublicationStoreKind.FileSystem, fixture.Key.RootId,
            fixture.Key.Object, fixture.Key.TempId);

        if (nullFactory) await Assert.ThrowsAsync<InvalidOperationException>(Get);
        else Assert.Same(original, await Assert.ThrowsAsync<IOException>(Get));
        Assert.Same(fixture.Snapshot, await Get());
        Assert.Same(fixture.Snapshot, await Get());
        Assert.Equal(2, calls);
        Assert.Equal(2, selected.Calls);
    }

    [Fact]
    public async Task SuccessfulBranchesCacheIndependentlyAndRetainTheActualOperationExceptionWithoutFallback()
    {
        var native = new Fixture(false);
        var report = new Fixture(true);
        var nativeRegistry = new Registry(native);
        var reportRegistry = new Registry(report);
        var nativeResolutions = 0;
        var reportResolutions = 0;
        var router = new LazyPrivateObjectPublicationRegistry(
            () => { nativeResolutions++; return nativeRegistry; },
            () => { reportResolutions++; return reportRegistry; });
        var original = new IOException("Synthetic selected-registry operation failure.");
        nativeRegistry.Failure = original;

        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => router.GetAsync(native.Owner, PrivatePublicationStoreKind.FileSystem,
            native.Key.RootId, native.Key.Object, native.Key.TempId)));
        Assert.Equal(0, reportResolutions);
        nativeRegistry.Failure = null;
        Assert.Same(native.Snapshot, await router.GetAsync(native.Owner, PrivatePublicationStoreKind.FileSystem, native.Key.RootId, native.Key.Object, native.Key.TempId));
        Assert.Same(report.Snapshot, await router.GetAsync(report.Owner, PrivatePublicationStoreKind.FileSystem, report.Key.RootId, report.Key.Object, report.Key.TempId));
        Assert.Same(report.Snapshot, await router.GetAsync(report.Owner, PrivatePublicationStoreKind.FileSystem, report.Key.RootId, report.Key.Object, report.Key.TempId));
        Assert.Equal(1, nativeResolutions);
        Assert.Equal(1, reportResolutions);
    }

    private sealed class Fixture
    {
        internal Fixture(bool report)
        {
            var ports = new RevisionReportServiceTestPorts();
            var claim = ports.Claim(ports.Key());
            Owner = report ? PrivatePublicationOwner.ForReport(RevisionReportServiceTestPorts.Actor, claim) :
                PrivatePublicationOwner.ForIngestion(RevisionReportServiceTestPorts.Actor,
                    new(new(ports.Source.Scope.OrganizationId, ports.Source.Scope.ProjectId), Guid.NewGuid(),
                        RevisionReportServiceTestPorts.Actor.PrincipalId, Guid.NewGuid(), Guid.NewGuid(), 3,
                        RevisionReportServiceTestPorts.Start.AddSeconds(120)));
            Key = new(Guid.NewGuid(), report ? claim.CandidateObjectId : ports.Source.NativeObject.Id, Guid.NewGuid());
            Reservation = new(Guid.NewGuid(), Key, PrivatePublicationStoreKind.AzureBlob, Owner, AbsenceDigest,
                RevisionReportServiceTestPorts.Start.AddSeconds(1));
            ReserveSnapshot = new(new(Reservation), RevisionReportServiceTestPorts.Start.AddSeconds(1), true, false, false);
            Identity = new(Key.RootId, Guid.NewGuid(), Guid.NewGuid(), 7, 101, 1000,
                new(301, 8, 1, 71), new(302, 8, 1, 71));
            var binding = new PrivatePublicationBinding(Identity, BoundDigest, RevisionReportServiceTestPorts.Start.AddSeconds(2));
            Snapshot = new(new(Reservation, binding), RevisionReportServiceTestPorts.Start.AddSeconds(3), true, false, false);
        }
        internal PrivatePublicationOwner Owner { get; }
        internal PrivatePublicationKey Key { get; }
        internal Sha256Digest AbsenceDigest { get; } = Sha256Digest.FromBytes("synthetic absence"u8);
        internal Sha256Digest BoundDigest { get; } = Sha256Digest.FromBytes("synthetic bound"u8);
        internal PrivatePublicationReservation Reservation { get; }
        internal PrivatePublicationFileIdentity Identity { get; }
        internal PrivatePublicationSnapshot Snapshot { get; }
        internal PrivatePublicationSnapshot ReserveSnapshot { get; }
    }

    private sealed class Registry(Fixture fixture) : IPrivateObjectPublicationRegistry
    {
        internal readonly PrivatePublicationReserveResult ReserveResult =
            PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Reserved, fixture.ReserveSnapshot);
        internal readonly PrivatePublicationBindResult BindResult =
            PrivatePublicationBindResult.ForSnapshot(PrivatePublicationBindOutcome.Bound, fixture.Snapshot);
        internal Task<PrivatePublicationReserveResult> ReserveTask => reserveTask ??= Task.FromResult(ReserveResult);
        internal Task<PrivatePublicationBindResult> BindTask => bindTask ??= Task.FromResult(BindResult);
        internal Task<PrivatePublicationSnapshot?> GetTask => getTask ??= Task.FromResult<PrivatePublicationSnapshot?>(fixture.Snapshot);
        private Task<PrivatePublicationReserveResult>? reserveTask;
        private Task<PrivatePublicationBindResult>? bindTask;
        private Task<PrivatePublicationSnapshot?>? getTask;
        internal int Calls;
        internal Exception? Failure;
        internal PrivatePublicationOwner? Owner;
        internal PrivatePublicationStoreKind? StoreKind;
        internal Guid RootId;
        internal Guid TempId;
        internal PrivateObjectId? Object;
        internal Sha256Digest? Digest;
        internal PrivatePublicationReservation? Reservation;
        internal PrivatePublicationFileIdentity? Identity;
        internal CancellationToken Token;

        public Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default)
        {
            Capture(owner, storeKind, rootId, objectId, tempId, cancellationToken);
            Digest = absenceRecordDigest;
            return Failure is { } failure ? Task.FromException<PrivatePublicationReserveResult>(failure) : ReserveTask;
        }
        public Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
            PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default)
        {
            Capture(reservation.Owner, reservation.StoreKind, reservation.Key.RootId, reservation.Key.Object, reservation.Key.TempId,
                cancellationToken);
            Reservation = reservation;
            Identity = actualIdentity;
            Digest = boundRecordDigest;
            return Failure is { } failure ? Task.FromException<PrivatePublicationBindResult>(failure) : BindTask;
        }
        public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default)
        {
            Capture(owner, storeKind, rootId, objectId, tempId, cancellationToken);
            return Failure is { } failure ? Task.FromException<PrivatePublicationSnapshot?>(failure) : GetTask;
        }
        private void Capture(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind, Guid rootId,
            PrivateObjectId objectId, Guid tempId, CancellationToken token)
        {
            Calls++;
            Owner = owner;
            StoreKind = storeKind;
            RootId = rootId;
            Object = objectId;
            TempId = tempId;
            Token = token;
        }
    }
}
