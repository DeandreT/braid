using System.Runtime.Versioning;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>Real service publication with real withheld PostgreSQL COMMIT replies; no synthetic success or after-commit throw.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresClaimAwareStorageIntegrationAcknowledgementTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public Task ActualNativeServiceReserveAcknowledgementLossSettlesAndRecoversItsOriginalClaimBeforeCreateNew() => RunAsync(report: false);

    [RevisionIngestionFact]
    public Task ActualReportServiceBindAcknowledgementLossSettlesAndRecoversItsOriginalCandidateBeforePayloadRead() => RunAsync(report: true);

    private async Task RunAsync(bool report)
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
        RevisionScope? revision = null;
        if (report)
        {
            var created = await test.CreateNativeAsync(bytes);
            Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
            revision = (await test.ReceiptAsync(created)).Facts.FutureRevision;
        }
        var registry = report ? test.ReportPublicationRegistry : test.NativeRegistry;
        var connectionString = report ? test.Case.DerivationConnectionString : test.Case.IngestionConnectionString;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        await using var relay = new RevisionReportCommitAckLossRelay(connection.Host!, connection.Port);
        await using var routed = new RoutedRegistry(report, relay.Route(connectionString));
        var route = new SelectedCommitRegistry(registry.Actual, routed.Port, bind: report);
        registry.Override = route;
        using var cancellation = new CancellationTokenSource();
        Task<RevisionIngestionResult>? native = report ? null : test.CreateNativeAsync(bytes, cancellationToken: cancellation.Token);
        Task<RevisionReportEnsureResult>? derived = report
            ? test.Reports.EnsureAsync(test.Actor, revision!, RevisionReportOperation.Projection, cancellationToken: cancellation.Token) : null;
        var operation = (Task?)native ?? derived!;
        try
        {
            await relay.CommitAcknowledgementWithheld.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(relay.FrontendCommitSeen);
            Assert.True(relay.BackendCommitCompleteSeen);
            Assert.True(relay.BackendIdleReadySeen);
            Assert.False(route.Settled);
            Assert.False(operation.IsCompleted);
            var publication = Assert.Single(test.Storage.Publications, item =>
                item.Reference.Id.Class == (report ? PrivateObjectClass.DerivedArtifact : PrivateObjectClass.NativeArtifact));
            var owner = publication.Owner;
            if (report)
                Assert.Equal(PrivatePublicationOwner.ForReport(test.Actor, Assert.Single(test.ReportMetadata.Claims)), owner);
            else
                Assert.Equal(PrivatePublicationOwner.ForIngestion(test.Actor, Assert.Single(test.Metadata.Claims)), owner);
            Assert.Equal(owner, route.Owner);
            Assert.Equal(0, publication.ReadCalls);
            Assert.Equal(0, publication.BytesRead);
            var key = Assert.IsType<PrivatePublicationKey>(route.Key);
            Assert.Equal(publication.Reference.Id, key.Object);
            Assert.False(File.Exists(test.Root.FinalPath(publication.Reference)));

            // Independent direct restricted Get proves the durable exact original
            // owner/key while the actual routed product Task remains pending.
            var durable = await registry.Actual.GetAsync(owner, PrivatePublicationStoreKind.FileSystem, key.RootId, key.Object, key.TempId);
            Assert.NotNull(durable);
            Assert.True(durable.CanWrite);
            Assert.Equal(owner, durable.Record.Reservation.Owner);
            Assert.Equal(key, durable.Record.Reservation.Key);
            Assert.Equal(PrivateStorageV2Data.Marker(test.Root).Root, key.RootId);
            var leaf = PrivateStorageV2Data.TempLeaf(test.Root, key);
            Assert.Equal(Sha256Digest.FromBytes(File.ReadAllBytes(Path.Combine(leaf, "absence"))),
                durable.Record.Reservation.AbsenceRecordDigest);
            var temp = PrivateStorageV2Data.TempPath(test.Root, key);
            if (!report)
            {
                Assert.Null(durable.Record.Binding);
                Assert.False(File.Exists(temp));
                Assert.False(File.Exists(Path.Combine(leaf, "reservation")));
                Assert.Empty(test.Native.Runner.NativeBodies);
            }
            else
            {
                var binding = Assert.IsType<PrivatePublicationBinding>(durable.Record.Binding);
                Assert.Equal(0, new FileInfo(temp).Length);
                Assert.Equal(PrivateStorageV2Identity.Read(temp), binding.ActualIdentity.FileIdentity);
                Assert.Equal(PrivateStorageV2Identity.Read(Path.GetDirectoryName(temp)!), binding.ActualIdentity.DirectoryIdentity);
                Assert.Equal(Sha256Digest.FromBytes(File.ReadAllBytes(Path.Combine(leaf, "bound"))), binding.BoundRecordDigest);
                Assert.False(File.Exists(Path.Combine(leaf, "prepared")));
                Assert.Empty(test.ReportMetadata.Finishes);
                Assert.Single(test.ReportBackend.Completions);
                Assert.True(test.ReportBackend.Completions[0].IsUsable);
            }
            await test.AssertPoolsUnboundAsync(); // The intentionally held routed connection is a separate pool.
            relay.DisconnectCaller();
            if (native is not null)
            {
                var result = await native;
                Assert.Equal(RevisionIngestionOutcome.CommittedValid, result.Outcome);
                var receipt = await test.ReceiptAsync(result);
                Assert.Equal(key.Object, receipt.Facts.NativeObject.Id);
                Assert.Equal(owner.IngestionClaim, Assert.Single(test.Metadata.Claims));
                Assert.Equal(bytes, Assert.Single(test.Native.Runner.NativeBodies));
                Assert.Equal(bytes, await test.Physical.ReadVerifiedAsync(receipt.Facts.FutureRevision, receipt.Facts.NativeObject));
            }
            else
            {
                var available = Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(await derived!));
                Assert.Equal(key.Object, available.Metadata.DerivedObject.Id);
                Assert.Equal(owner.ReportClaim!.DerivationId, available.Metadata.DerivationId);
                Assert.Equal(RevisionReportStoreOutcome.Applied, Assert.Single(test.ReportMetadata.Finishes).Result.Outcome);
                var returned = await test.Physical.ReadVerifiedAsync(revision!, available.Metadata.DerivedObject);
                Assert.Equal(available.Metadata.DerivedObject.Digest, Sha256Digest.FromBytes(returned));
            }
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(route.Settled);
            Assert.True(route.Threw);
            Assert.NotEmpty(route.Recoveries);
            Assert.All(route.Recoveries, observation =>
            {
                Assert.True(observation.Settled);
                Assert.Equal(owner, observation.Owner);
                Assert.Equal(key, observation.Key);
            });
            var retained = await registry.Actual.GetAsync(owner, PrivatePublicationStoreKind.FileSystem, key.RootId, key.Object, key.TempId);
            Assert.Equal(durable.Record.Reservation, retained!.Record.Reservation);
            Assert.NotNull(retained.Record.Binding);
            if (report) Assert.Equal(durable.Record, retained.Record);
            Assert.Equal(PrivateObjectPublishResult.Created, publication.Result);
            Assert.Equal(0, test.Storage.PlainCalls);
            Assert.False(File.Exists(temp));
            await test.AssertPoolsUnboundAsync();
        }
        finally
        {
            cancellation.Cancel();
            relay.DisconnectCaller();
            try { await relay.DisposeAsync(); }
            finally
            {
                try { await operation.WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (Exception) { /* Settle the actual owned service before pool/root disposal. */ }
                registry.Override = null;
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
                database = pool; Port = new PostgresRevisionReportPublicationRegistry(pool);
            }
            else
            {
                var pool = new PostgresIngestionDatabase(connection);
                database = pool; Port = new PostgresIngestionPublicationRegistry(pool);
            }
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }

    private sealed class SelectedCommitRegistry(IPrivateObjectPublicationRegistry direct,
        IPrivateObjectPublicationRegistry routed, bool bind) : IPrivateObjectPublicationRegistry
    {
        internal PrivatePublicationKey? Key { get; private set; }
        internal PrivatePublicationOwner? Owner { get; private set; }
        internal bool Settled { get; private set; }
        internal bool Threw { get; private set; }
        internal List<(PrivatePublicationOwner Owner, PrivatePublicationKey Key, bool Settled)> Recoveries { get; } = [];
        public async Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default)
        {
            Owner = owner; Key = new(rootId, objectId, tempId);
            if (bind) return await direct.ReserveAsync(owner, storeKind, rootId, objectId, tempId, absenceRecordDigest, cancellationToken);
            try { return await routed.ReserveAsync(owner, storeKind, rootId, objectId, tempId, absenceRecordDigest, cancellationToken); }
            catch { Threw = true; throw; }
            finally { Settled = true; }
        }
        public async Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
            PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default)
        {
            if (!bind) return await direct.BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken);
            try { return await routed.BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken); }
            catch { Threw = true; throw; }
            finally { Settled = true; }
        }
        public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default)
        {
            Recoveries.Add((owner, new(rootId, objectId, tempId), Settled));
            return direct.GetAsync(owner, storeKind, rootId, objectId, tempId, cancellationToken);
        }
    }
}
