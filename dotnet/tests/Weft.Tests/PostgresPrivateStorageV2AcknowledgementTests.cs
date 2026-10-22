using System.Runtime.Versioning;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>
/// Actual restricted PostgreSQL COMMIT frames are withheld by the unchanged
/// transport relay. No decorator invents an acknowledgement-loss exception.
/// Fixture source/C1 facts remain seeded metadata, with no native-worker claim.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresPrivateStorageV2AcknowledgementTests(PostgresFixture fixture)
{
    [PostgresFact]
    public Task NativeReserveCommitLossRecoversTheOriginalCommittedNaturalKeyBeforeCreateNew() => RunAsync(report: false, bind: false);

    [PostgresFact]
    public Task ReportReserveCommitLossRecoversTheOriginalCommittedNaturalKeyBeforeCreateNew() => RunAsync(report: true, bind: false);

    [PostgresFact]
    public Task NativeBindCommitLossRecoversTheExactCommittedInodeBeforeAnyPayloadRead() => RunAsync(report: false, bind: true);

    [PostgresFact]
    public Task ReportBindCommitLossRecoversTheExactCommittedInodeBeforeAnyPayloadRead() => RunAsync(report: true, bind: true);

    private async Task RunAsync(bool report, bool bind)
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        PrivatePublicationOwner owner;
        PrivateObjectReference reference;
        byte[] bytes;
        IPrivateObjectPublicationRegistry direct;
        string connectionString;
        if (report)
        {
            var claim = await test.ReserveReportAsync();
            owner = PrivatePublicationOwner.ForReport(test.Actor, claim);
            bytes = "opaque report candidate; no verified report is asserted"u8.ToArray();
            reference = new(claim.CandidateObjectId, Sha256Digest.FromBytes(bytes), bytes.LongLength);
            direct = test.ReportPublications;
            connectionString = test.Case.DerivationConnectionString;
        }
        else
        {
            var facts = await test.ReserveNativeAsync();
            owner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
            reference = facts.NativeObject;
            bytes = PostgresIngestionTestSupport.SyntheticBody;
            direct = test.NativePublications;
            connectionString = test.Case.IngestionConnectionString;
        }
        var actual = new NpgsqlConnectionStringBuilder(connectionString);
        await using var relay = new RevisionReportCommitAckLossRelay(actual.Host!, actual.Port);
        await using var routed = new RoutedRegistry(report, relay.Route(connectionString));
        var registry = new OneOperationRoutedRegistry(direct, routed.Port, bind);
        using var root = new PrivateStorageTestRoot();
        using var source = new PrivateStorageInput(bytes, chunkSize: 23);
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry);
        var write = store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, source);
        try
        {
            await relay.CommitAcknowledgementWithheld.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(relay.FrontendCommitSeen);
            Assert.True(relay.BackendCommitCompleteSeen);
            Assert.True(relay.BackendIdleReadySeen);
            Assert.False(write.IsCompleted);
            Assert.False(registry.RoutedCallSettled);
            Assert.Equal(0, source.ReadCalls);
            Assert.False(File.Exists(root.FinalPath(reference)));
            var key = Assert.IsType<PrivatePublicationKey>(registry.Key);
            Assert.Equal(owner, registry.Owner);
            Assert.Equal(reference.Id, key.Object);
            Assert.Equal(PrivateStorageV2Data.Marker(root).Root, key.RootId);

            // This separate ordinary restricted connection observes the committed
            // row while the actual original transport still lacks both ACK frames.
            var durable = await direct.GetAsync(owner, PrivatePublicationStoreKind.FileSystem, key.RootId, key.Object, key.TempId);
            Assert.NotNull(durable);
            Assert.True(durable.CanWrite);
            Assert.Equal(owner, durable.Record.Reservation.Owner);
            Assert.Equal(key, durable.Record.Reservation.Key);
            var leaf = PrivateStorageV2Data.TempLeaf(root, key);
            var absence = File.ReadAllBytes(Path.Combine(leaf, "absence"));
            Assert.Equal(Sha256Digest.FromBytes(absence), durable.Record.Reservation.AbsenceRecordDigest);
            if (!bind)
            {
                Assert.Null(durable.Record.Binding);
                Assert.Equal(registry.AbsenceDigest, durable.Record.Reservation.AbsenceRecordDigest);
                Assert.False(File.Exists(PrivateStorageV2Data.TempPath(root, key)));
                Assert.False(File.Exists(Path.Combine(leaf, "reservation")));
                Assert.Equal(0, registry.BindCalls);
            }
            else
            {
                Assert.Equal(registry.Reservation, durable.Record.Reservation);
                var binding = Assert.IsType<PrivatePublicationBinding>(durable.Record.Binding);
                Assert.Equal(registry.Identity, binding.ActualIdentity);
                Assert.Equal(registry.BoundDigest, binding.BoundRecordDigest);
                Assert.Equal(Sha256Digest.FromBytes(File.ReadAllBytes(Path.Combine(leaf, "bound"))), binding.BoundRecordDigest);
                var temp = PrivateStorageV2Data.TempPath(root, key);
                Assert.Equal(0, new FileInfo(temp).Length);
                Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(temp));
                Assert.Equal(PrivateStorageV2Identity.Read(temp), binding.ActualIdentity.FileIdentity);
                Assert.Equal(PrivateStorageV2Identity.Read(Path.GetDirectoryName(temp)!), binding.ActualIdentity.DirectoryIdentity);
                Assert.False(File.Exists(Path.Combine(leaf, "prepared")));
            }
            Assert.Equal(new long[] { 0, 0, 1 }, await test.CountsAsync());
            relay.DisconnectCaller();
            Assert.Equal(PrivateObjectPublishResult.Created, await write);
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(registry.RoutedCallSettled);
            Assert.True(registry.RoutedCallThrew);
            Assert.Equal(1, registry.ReserveCalls);
            Assert.Equal(1, registry.BindCalls);
            Assert.NotEmpty(registry.Recoveries);
            Assert.All(registry.Recoveries, recovery =>
            {
                Assert.True(recovery.OriginalSettled);
                Assert.Equal(owner, recovery.Owner);
                Assert.Equal(key, recovery.Key);
            });
            var after = await direct.GetAsync(owner, PrivatePublicationStoreKind.FileSystem, key.RootId, key.Object, key.TempId);
            Assert.NotNull(after);
            Assert.Equal(durable.Record.Reservation, after.Record.Reservation);
            Assert.NotNull(after.Record.Binding);
            if (bind) Assert.Equal(durable.Record, after.Record);
            Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
            Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
            Assert.False(File.Exists(PrivateStorageV2Data.TempPath(root, key)));
            Assert.Equal(new long[] { 0, 0, 1 }, await test.CountsAsync());
        }
        finally
        {
            relay.DisconnectCaller();
            try { await relay.DisposeAsync(); }
            finally
            {
                try { await write.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { /* Settle the owned task before fixture/pool disposal; assertions above retain its failure. */ }
            }
        }
    }

    private sealed class RoutedRegistry : IAsyncDisposable
    {
        private readonly IAsyncDisposable database;
        internal IPrivateObjectPublicationRegistry Port { get; }
        internal RoutedRegistry(bool report, string connection)
        {
            if (report)
            {
                var pool = new PostgresRevisionDerivationDatabase(connection);
                database = pool;
                Port = new PostgresRevisionReportPublicationRegistry(pool);
            }
            else
            {
                var pool = new PostgresIngestionDatabase(connection);
                database = pool;
                Port = new PostgresIngestionPublicationRegistry(pool);
            }
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }

    // Selects the one real operation whose transaction uses the relay. Recovery
    // calls use the separate ordinary pool; the wrapped product method must have
    // actually settled before the component is allowed to issue that lookup.
    private sealed class OneOperationRoutedRegistry(IPrivateObjectPublicationRegistry direct,
        IPrivateObjectPublicationRegistry routed, bool bind) : IPrivateObjectPublicationRegistry
    {
        internal int ReserveCalls { get; private set; }
        internal int BindCalls { get; private set; }
        internal PrivatePublicationKey? Key { get; private set; }
        internal PrivatePublicationOwner? Owner { get; private set; }
        internal Sha256Digest? AbsenceDigest { get; private set; }
        internal PrivatePublicationReservation? Reservation { get; private set; }
        internal PrivatePublicationFileIdentity? Identity { get; private set; }
        internal Sha256Digest? BoundDigest { get; private set; }
        internal bool RoutedCallSettled { get; private set; }
        internal bool RoutedCallThrew { get; private set; }
        internal List<(PrivatePublicationOwner Owner, PrivatePublicationKey Key, bool OriginalSettled)> Recoveries { get; } = [];

        public async Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default)
        {
            ReserveCalls++;
            Owner = owner;
            Key = new(rootId, objectId, tempId);
            AbsenceDigest = absenceRecordDigest;
            if (bind) return await direct.ReserveAsync(owner, storeKind, rootId, objectId, tempId, absenceRecordDigest, cancellationToken);
            try { return await routed.ReserveAsync(owner, storeKind, rootId, objectId, tempId, absenceRecordDigest, cancellationToken); }
            catch { RoutedCallThrew = true; throw; }
            finally { RoutedCallSettled = true; }
        }

        public async Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
            PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default)
        {
            BindCalls++;
            Reservation = reservation;
            Identity = actualIdentity;
            BoundDigest = boundRecordDigest;
            if (!bind) return await direct.BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken);
            try { return await routed.BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken); }
            catch { RoutedCallThrew = true; throw; }
            finally { RoutedCallSettled = true; }
        }

        public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default)
        {
            Recoveries.Add((owner, new(rootId, objectId, tempId), RoutedCallSettled));
            return direct.GetAsync(owner, storeKind, rootId, objectId, tempId, cancellationToken);
        }
    }
}
