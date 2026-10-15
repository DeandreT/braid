using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>
/// Actual PostgreSQL original claims, registry commits and local descriptors.
/// The source revision/C1 metadata is seeded by the existing metadata fixture;
/// these tests do not claim native-worker or report-codec qualification.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresPrivateStorageV2Tests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ActualNativeAndReportOwnersCommitExactObservedBindingsBeforeAnyPayloadRead()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var native = await test.ReserveNativeAsync();
        var nativeOwner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(native));
        var reportClaim = await test.ReserveReportAsync();
        var reportBytes = "opaque derived storage fixture; not a verified report"u8.ToArray();
        var reportReference = new PrivateObjectReference(reportClaim.CandidateObjectId,
            Sha256Digest.FromBytes(reportBytes), reportBytes.LongLength);
        var targets = new (PrivateObjectReference Reference, byte[] Bytes, PrivatePublicationOwner Owner,
            IPrivateObjectPublicationRegistry Registry)[]
        {
            (native.NativeObject, PostgresIngestionTestSupport.SyntheticBody, nativeOwner, test.NativePublications),
            (reportReference, reportBytes, PrivatePublicationOwner.ForReport(test.Actor, reportClaim), test.ReportPublications)
        };
        foreach (var (reference, bytes, owner, registry) in targets)
        {
            using var root = new PrivateStorageTestRoot();
            using var input = new PrivateStorageInput(bytes, chunkSize: 17);
            PrivatePublicationRecord? committed = null;
            var observer = new PrivateStorageV2Observer(async (observation, token) =>
            {
                if (observation.Phase is not (V2StoragePhase.ReservationAcknowledged or V2StoragePhase.BindingAcknowledged)) return;
                Assert.Equal(0, input.ReadCalls);
                var key = Assert.IsType<PrivatePublicationKey>(observation.Publication);
                var persisted = await registry.GetAsync(owner, key.RootId, key.Object, key.TempId, token);
                Assert.NotNull(persisted);
                Assert.True(persisted.CanWrite);
                committed = persisted.Record;
                Assert.Equal(owner, committed.Reservation.Owner);
                if (observation.Phase == V2StoragePhase.ReservationAcknowledged)
                {
                    Assert.Null(committed.Binding);
                    Assert.False(File.Exists(PrivateStorageV2Data.TempPath(root, key)));
                    return;
                }
                var binding = Assert.IsType<PrivatePublicationBinding>(committed.Binding);
                var temp = PrivateStorageV2Data.TempPath(root, key);
                Assert.Equal(0, new FileInfo(temp).Length);
                Assert.Equal(PrivateStorageV2Identity.Read(temp), binding.ActualIdentity.FileIdentity);
                Assert.Equal(PrivateStorageV2Identity.Read(Path.GetDirectoryName(temp)!), binding.ActualIdentity.DirectoryIdentity);
                var boundBytes = File.ReadAllBytes(Path.Combine(PrivateStorageV2Data.TempLeaf(root, key), "bound"));
                Assert.Equal(Sha256Digest.FromBytes(boundBytes), binding.BoundRecordDigest);
            });
            using (var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry, observer))
                Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, input));
            Assert.NotNull(committed);
            var key = committed.Reservation.Key;
            Assert.Equal(committed, (await registry.GetAsync(owner, key.RootId, key.Object, key.TempId))!.Record);
            Assert.Equal(committed.Binding!.ActualIdentity.FileIdentity, PrivateStorageV2Identity.Read(root.FinalPath(reference)));
            using var readOnly = FileSystemV2PrivateObjectStore.Open(root.Options);
            Assert.Equal(bytes, await readOnly.ReadVerifiedAsync(reference.Id.Scope, reference));
        }
        Assert.Equal(new long[] { 0, 0, 2 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task ActualRegistryRefusesASubstitutedOriginalClaimBeforeAnySourceReadOrTemporaryCreation()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var actual = await test.ClaimNativeAsync(facts);
        var substituted = new IngestionClaim(actual.Project, actual.OperationId, actual.OriginalActor,
            actual.ServerInstanceId, Guid.NewGuid(), actual.Generation, actual.LeaseExpiresAtUtc);
        var wrongOwner = PrivatePublicationOwner.ForIngestion(test.Actor, substituted);
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => test.NativePublications);
        using var input = new PrivateStorageInput(PostgresIngestionTestSupport.SyntheticBody);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io,
            () => store.PublishCreateOnlyAsync(wrongOwner, facts.FutureRevision, facts.NativeObject, input));
        Assert.Equal(0, input.ReadCalls);
        Assert.Empty(Directory.GetFiles(root.RootPath, "*.tmp", SearchOption.AllDirectories));
        Assert.False(File.Exists(root.FinalPath(facts.NativeObject)));
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
        Assert.Single(Directory.GetFiles(root.RootPath, "absence", SearchOption.AllDirectories));
    }
}
