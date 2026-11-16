using System.Text;
using Azure.Storage.Blobs;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Storage.Blob;

namespace Braid.Tests;

/// <summary>Actual PostgreSQL roles/transactions plus SDK/network Azurite; not real Azure qualification.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SpecBlobPersistenceTests(PostgresFixture fixture)
{
    [SpecBlobPersistenceFact]
    public async Task Real_registry_recovers_uploaded_original_and_extraction_then_revokes_and_retires()
    {
        var emulator = new AzuriteFixture();
        try
        {
            await emulator.InitializeAsync();
            await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
            var container = await emulator.CreateContainerAsync();
            var options = emulator.Options(container);
            var storeId = Guid.NewGuid();
            var location = Sha256Digest.FromBytes(Encoding.UTF8.GetBytes("braid.spec-blob-location.v1\n" +
                options.ServiceUri.AbsoluteUri.TrimEnd('/') + "/" + options.ContainerName));
            var binding = new SpecStoreBinding(storeId, SpecStoreKind.Blob, location);
            var scope = new SpecBundleScope(test.Project, Guid.NewGuid());
            await test.Case.ExecuteAsync("INSERT INTO braid.spec_stores(id,kind,location_digest,active) VALUES($1,'blob',$2,true)", storeId, location.Value);
            await test.Case.ExecuteAsync("INSERT INTO braid.project_payload_read_grants(organization_id,project_id,principal_id) VALUES($1,$2,$3)",
                test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
            PostgresSpecBundleDatabase Open(string role, SpecDatabaseProfile profile) =>
                new(fixture.RegressionConnectionString(test.Case, role), profile);
            await using (var setup = Open("braid_spec_writer", SpecDatabaseProfile.Writer))
                await setup.ForActor(test.Actor, binding).CreateAsync(scope, Guid.NewGuid(), default);

            var source = new SpecCapturedSource(new(scope, Guid.NewGuid(), 1), SpecSourceFormat.Text, "Actual durable specification evidence"u8);
            var extraction = SpecSourceExtractor.Extract(source);
            var published = new List<(SpecPublicationClaim Claim, byte[] Bytes)>();
            foreach (var kind in new[] { SpecObjectKind.Original, SpecObjectKind.Extraction })
            {
                var bytes = kind == SpecObjectKind.Original ? source.CopyBytes() : extraction.CopyEvidenceBytes();
                var reference = new SpecObjectReference(new(source.Identity, kind, Guid.NewGuid()), Sha256Digest.FromBytes(bytes), bytes.Length);
                var pin = kind == SpecObjectKind.Extraction ? extraction.Identity : null;
                var attempt = Guid.NewGuid();
                SpecObjectReservation reservation;
                long version;
                // Dispose the original DB pool and adapter before recovery; no in-memory registry state survives.
                await using (var database = Open("braid_spec_writer", SpecDatabaseProfile.Writer))
                {
                    var writer = database.ForActor(test.Actor, binding);
                    version = (await writer.InventoryAsync(scope, default)).Bundle.Version;
                    reservation = await writer.ReserveAsync(reference, source.Format, attempt, version, pin, default);
                    var fault = new LoseAcknowledgementOnce(writer);
                    var store = await BlobSpecObjectStore.InitializeAsync(options, storeId, fault);
                    await Error(SpecStorageError.Unavailable, () => Publish(store, scope, reservation.Claim, bytes));
                    Assert.True(fault.Injected);
                }
                var blob = container.GetBlobClient(SpecObjectStore.ObjectName(reference));
                Assert.Equal(bytes, (await blob.DownloadContentAsync()).Value.Content.ToArray());
                var tag = (await blob.GetPropertiesAsync()).Value.ETag;
                Assert.Equal("live", (await blob.GetPropertiesAsync()).Value.Metadata["state"]);
                await using (var database = Open("braid_spec_reader", SpecDatabaseProfile.Reader))
                {
                    var reader = database.ForActor(test.Actor, binding);
                    Assert.Equal("pending", Assert.Single((await reader.InventoryAsync(scope, default)).Objects, i => i.Reference == reference).State);
                    var store = await BlobSpecObjectStore.OpenAsync(options, storeId, reader);
                    await Error(SpecStorageError.NotAdmitted, () => store.ReadAsync(scope, reference));
                }
                await using (var database = Open("braid_spec_writer", SpecDatabaseProfile.Writer))
                {
                    var writer = database.ForActor(test.Actor, binding);
                    Assert.Equal(reservation, await writer.ReserveAsync(reference, source.Format, attempt, version, pin, default));
                    var store = await BlobSpecObjectStore.OpenAsync(options, storeId, writer);
                    Assert.Equal(SpecPublishResult.AlreadyPresent, await Publish(store, scope, reservation.Claim, bytes));
                    Assert.Equal(SpecPublishResult.AlreadyPresent, await Publish(store, scope, reservation.Claim, bytes));
                    Assert.Equal(tag, (await blob.GetPropertiesAsync()).Value.ETag);
                }
                await using (var database = Open("braid_spec_reader", SpecDatabaseProfile.Reader))
                {
                    var reader = database.ForActor(test.Actor, binding);
                    var item = Assert.Single((await reader.InventoryAsync(scope, default)).Objects, i => i.Reference == reference);
                    Assert.Equal("published", item.State);
                    Assert.Equal(pin, item.Extraction);
                    Assert.Equal(reference, await reader.FindAsync(scope, reference.Id.ObjectId, default));
                    var store = await BlobSpecObjectStore.OpenAsync(options, storeId, reader);
                    Assert.Equal(bytes, await store.ReadAsync(scope, reference));
                }
                published.Add((reservation.Claim, bytes));
            }

            await using var writerDatabase = Open("braid_spec_writer", SpecDatabaseProfile.Writer);
            await using var readerDatabase = Open("braid_spec_reader", SpecDatabaseProfile.Reader);
            await using var maintenanceDatabase = Open("braid_spec_maintenance", SpecDatabaseProfile.Maintenance);
            var writerSession = writerDatabase.ForActor(test.Actor, binding);
            var readerSession = readerDatabase.ForActor(test.Actor, binding);
            var readStore = await BlobSpecObjectStore.OpenAsync(options, storeId, readerSession);
            async Task Grant(bool active) => await test.Case.ExecuteAsync(
                "UPDATE braid.project_payload_read_grants SET active=$4,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice, active);
            await Grant(false);
            foreach (var (claim, bytes) in published)
            {
                await Error(SpecStorageError.NotAdmitted, () => readStore.ReadAsync(scope, claim.Reference));
                Assert.Equal(bytes, (await container.GetBlobClient(SpecObjectStore.ObjectName(claim.Reference)).DownloadContentAsync()).Value.Content.ToArray());
            }
            await Grant(true);
            var pendingReference = new SpecObjectReference(new(new(scope, Guid.NewGuid(), 1), SpecObjectKind.Original, Guid.NewGuid()), source.Digest, source.ByteCount);
            var currentVersion = (await writerSession.InventoryAsync(scope, default)).Bundle.Version;
            var pending = await writerSession.ReserveAsync(pendingReference, source.Format, Guid.NewGuid(), currentVersion, null, default);
            Assert.False((await container.GetBlobClient(SpecObjectStore.ObjectName(pendingReference)).ExistsAsync()).Value);
            Assert.True((await writerSession.TombstoneAsync(scope, null, pending.BundleVersion, Guid.NewGuid(), default)).Tombstoned);
            var maintenance = maintenanceDatabase.ForMaintenance(binding);
            var retireStore = await BlobSpecObjectStore.OpenAsync(options, storeId, maintenance);
            var retirements = await maintenance.RetirementsAsync(scope, default);
            Assert.Equal(3, retirements.Count);
            foreach (var retirement in retirements)
            {
                Assert.Equal(retirement.Reference == pendingReference ? SpecRetireResult.Missing : SpecRetireResult.Retired,
                    await retireStore.RetireAsync(scope, retirement));
                var blob = container.GetBlobClient(SpecObjectStore.ObjectName(retirement.Reference));
                Assert.Empty((await blob.DownloadContentAsync()).Value.Content.ToArray());
                Assert.Equal("retired", (await blob.GetPropertiesAsync()).Value.Metadata["state"]);
            }
            Assert.Empty(await maintenance.RetirementsAsync(scope, default));
            var publishStore = await BlobSpecObjectStore.OpenAsync(options, storeId, writerSession);
            foreach (var (claim, bytes) in published.Append((pending.Claim, source.CopyBytes())))
            {
                var blob = container.GetBlobClient(SpecObjectStore.ObjectName(claim.Reference));
                var tag = (await blob.GetPropertiesAsync()).Value.ETag;
                await Error(SpecStorageError.NotAdmitted, () => readStore.ReadAsync(scope, claim.Reference));
                await Error(SpecStorageError.NotAdmitted, () => Publish(publishStore, scope, claim, bytes));
                Assert.Equal(tag, (await blob.GetPropertiesAsync()).Value.ETag);
            }
        }
        finally { await emulator.DisposeAsync(); }
    }

    private static async Task<SpecPublishResult> Publish(BlobSpecObjectStore store, SpecBundleScope scope, SpecPublicationClaim claim, byte[] bytes)
    {
        using var input = new MemoryStream(bytes, false);
        return await store.PublishAsync(scope, claim, input);
    }

    private static async Task Error(SpecStorageError expected, Func<Task> action) =>
        Assert.Equal(expected, (await Assert.ThrowsAsync<SpecStorageException>(action)).Error);

    // Synthetic fault injection ONLY: every admission, transaction, acknowledgement and fence remains real PostgreSQL.
    private sealed class LoseAcknowledgementOnce(ISpecObjectRegistry inner) : ISpecObjectRegistry
    {
        internal bool Injected { get; private set; }
        public Task AdmitPublicationAsync(SpecPublicationClaim claim, CancellationToken token) => inner.AdmitPublicationAsync(claim, token);
        public Task<SpecPublishResult> ExecutePublicationAsync(SpecPublicationClaim claim, Func<CancellationToken, Task<SpecPublishResult>> publish, CancellationToken token) =>
            inner.ExecutePublicationAsync(claim, async ct =>
            {
                var result = await publish(ct); // The actual SDK upload has completed; DB completion has not run.
                if (!Injected)
                {
                    Injected = true;
                    throw new IOException("Synthetic acknowledgement loss after actual Blob upload, before PostgreSQL acknowledgement.");
                }
                return result;
            }, token);
        public Task<byte[]> ExecuteReadAsync(Guid storeId, SpecObjectReference reference, Func<CancellationToken, Task<byte[]>> read, CancellationToken token) =>
            inner.ExecuteReadAsync(storeId, reference, read, token);
        public Task<SpecRetireResult> ExecuteRetirementAsync(SpecRetirementClaim claim, Func<CancellationToken, Task<SpecRetireResult>> retire, CancellationToken token) =>
            inner.ExecuteRetirementAsync(claim, retire, token);
    }
}

public sealed class SpecBlobPersistenceFactAttribute : FactAttribute
{
    public SpecBlobPersistenceFactAttribute()
    {
        if (!PostgresFixture.IsConfigured || !AzuriteFixture.IsConfigured)
            Skip = "Configure both owned PostgreSQL and Azurite fixtures to execute durable Blob integration gates.";
    }
}
