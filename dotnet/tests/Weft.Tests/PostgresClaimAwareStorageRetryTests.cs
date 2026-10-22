using System.Runtime.Versioning;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresClaimAwareStorageRetryTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public Task CancellationAfterActualCreatedReclaimsTheSameKeyAndVerifiesAlreadyPresentWithoutReplacingItsInode() =>
        RetryAfterCreatedAsync(corrupt: false);

    [RevisionIngestionFact]
    public Task CorruptFinalAfterActualCreatedCancellationRefusesRetryWithoutRepairReplacementOrWorkerDispatch() =>
        RetryAfterCreatedAsync(corrupt: true);

    private async Task RetryAfterCreatedAsync(bool corrupt)
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        var store = new CancelAfterCreatedStore(test.Storage, cancellation);
        var service = test.NewIngestion(store);
        var bytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
        var key = Guid.NewGuid();
        var original = service.CreateAsync(test.Actor, test.Project, key, RevisionIngestionTestSupport.Request(),
            new MemoryStream(bytes), cancellation.Token);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
            Assert.True(original.IsCompleted);
            Assert.Equal(1, store.CreatedReturns);
            Assert.Equal(0, store.CompletedReads);
            Assert.Equal(0, test.Storage.PlainCalls);
            Assert.Empty(test.Native.Runner.NativeBodies);
            var firstClaim = Assert.Single(test.Metadata.Claims);
            var firstPublication = Assert.Single(test.Storage.Publications);
            Assert.Equal(PrivateObjectPublishResult.Created, firstPublication.Result);
            Assert.Equal(PrivatePublicationOwner.ForIngestion(test.Actor, firstClaim), firstPublication.Owner);
            var firstReceipt = Assert.IsType<IngestionReceipt>(await test.Native.Metadata.GetReceiptAsync(test.Actor,
                test.Project, firstClaim.OperationId));
            Assert.Equal(IngestionFailureCode.Canceled, Assert.Single(firstReceipt.Attempts).Completion!.Failure);
            Assert.Null(firstReceipt.VerifiedStageGeneration);
            Assert.Null(firstReceipt.CommittedRevision);
            var path = test.Root.FinalPath(firstPublication.Reference);
            var originalIdentity = PrivateStorageV2Identity.Read(path);
            Assert.Equal(Assert.Single(test.NativeRegistry.Bindings).Record.Binding!.ActualIdentity.FileIdentity, originalIdentity);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(path));
            var retainedBytes = bytes;
            if (corrupt)
            {
                retainedBytes = bytes.ToArray();
                retainedBytes[retainedBytes.Length / 2] ^= 0x80;
                File.SetUnixFileMode(path, PrivateStorageTestRoot.AttemptMode);
                File.WriteAllBytes(path, retainedBytes); // Deliberate same-inode corruption of this owned test file.
                File.SetUnixFileMode(path, PrivateStorageTestRoot.FinalMode);
                Assert.Equal(originalIdentity, PrivateStorageV2Identity.Read(path));
            }
            await test.AssertPoolsUnboundAsync();

            var retry = await service.CreateAsync(test.Actor, test.Project, key, RevisionIngestionTestSupport.Request(), new MemoryStream(bytes));
            Assert.Equal(2, test.Metadata.Claims.Count);
            var nextClaim = test.Metadata.Claims[1];
            Assert.Equal(firstClaim.OperationId, nextClaim.OperationId);
            Assert.Equal(firstClaim.Project, nextClaim.Project);
            Assert.Equal(firstClaim.OriginalActor, nextClaim.OriginalActor);
            Assert.Equal(firstClaim.ServerInstanceId, nextClaim.ServerInstanceId);
            Assert.Equal(firstClaim.Generation + 1, nextClaim.Generation);
            Assert.NotEqual(firstClaim.LeaseToken, nextClaim.LeaseToken);
            Assert.Equal(2, test.Storage.Publications.Count);
            var secondPublication = test.Storage.Publications[1];
            Assert.Equal(firstPublication.Reference, secondPublication.Reference);
            Assert.Equal(PrivatePublicationOwner.ForIngestion(test.Actor, nextClaim), secondPublication.Owner);
            Assert.Equal(bytes.LongLength, secondPublication.BytesRead);
            Assert.Equal(originalIdentity, PrivateStorageV2Identity.Read(path));
            Assert.Equal(retainedBytes, File.ReadAllBytes(path));
            Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(path));
            Assert.Equal(2, test.NativeRegistry.Reservations.Count);
            Assert.Equal(2, test.NativeRegistry.Bindings.Count);
            var firstRegistration = test.NativeRegistry.Reservations[0].Record.Reservation;
            var nextRegistration = test.NativeRegistry.Reservations[1].Record.Reservation;
            Assert.Equal(firstRegistration.Key.RootId, nextRegistration.Key.RootId);
            Assert.Equal(firstRegistration.Key.Object, nextRegistration.Key.Object);
            Assert.NotEqual(firstRegistration.Key.TempId, nextRegistration.Key.TempId);
            Assert.NotEqual(firstRegistration.PublicationId, nextRegistration.PublicationId);
            Assert.Equal(firstClaim, firstRegistration.Owner.IngestionClaim);
            Assert.Equal(nextClaim, nextRegistration.Owner.IngestionClaim);
            var receipt = Assert.IsType<IngestionReceipt>(await test.Native.Metadata.GetReceiptAsync(test.Actor,
                test.Project, firstClaim.OperationId));
            Assert.Equal(firstReceipt.Facts, receipt.Facts);
            Assert.Equal(2, receipt.Attempts.Count);
            Assert.Equal(firstReceipt.Attempts[0], receipt.Attempts[0]);
            if (corrupt)
            {
                Assert.Equal(RevisionIngestionOutcome.RetryableFailure, retry.Outcome);
                Assert.Equal(IngestionFailureCode.StorageIntegrity, retry.Failure);
                Assert.Null(secondPublication.Result);
                Assert.Empty(test.Native.Runner.NativeBodies);
                Assert.Null(receipt.CommittedRevision);
                Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
            }
            else
            {
                Assert.Equal(RevisionIngestionOutcome.CommittedValid, retry.Outcome);
                Assert.Equal(PrivateObjectPublishResult.AlreadyPresent, secondPublication.Result);
                Assert.Equal(bytes, Assert.Single(test.Native.Runner.NativeBodies));
                Assert.Equal(bytes, await test.Physical.ReadVerifiedAsync(receipt.Facts.FutureRevision, receipt.Facts.NativeObject));
                Assert.Equal(nextClaim.Generation, receipt.VerifiedStageGeneration);
                Assert.NotNull(receipt.CommittedRevision);
            }
            Assert.Empty(Directory.GetFiles(test.Root.RootPath, "*.tmp", SearchOption.AllDirectories));
            Assert.Equal(0, test.Storage.PlainCalls);
            await test.AssertPoolsUnboundAsync();
        }
        finally
        {
            cancellation.Cancel();
            try { await original.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [RevisionIngestionFact]
    public async Task OriginalNativeClaimNaturallyExpiresWhileBeforeBindWaitsWithoutHoldingAnyPoolOrTransaction()
    {
        // The actual native lease remains120s. The test budget leaves bounded
        // settlement room inside180s; no service/worker deadline is changed.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var pids = await test.AssertPoolsUnboundAsync();
        var gate = new RevisionIngestionGate();
        var observed = new ActualBindObservation(test.NativeRegistry.Actual);
        test.NativeRegistry.Override = observed;
        test.NativeRegistry.BeforeBindAsync = (_, token) => gate.HoldAsync(token);
        var original = test.CreateNativeAsync(RevisionIngestionTestSupport.Fixture("versioned-exact"), cancellationToken: deadline.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var claim = Assert.Single(test.Metadata.Claims);
            var registration = Assert.Single(test.NativeRegistry.Reservations).Record.Reservation;
            Assert.Equal(claim, registration.Owner.IngestionClaim);
            var receipt = Assert.IsType<IngestionReceipt>(await test.Native.Metadata.GetReceiptAsync(test.Actor,
                test.Project, claim.OperationId));
            Assert.Equal(TimeSpan.FromSeconds(120), claim.LeaseExpiresAtUtc - Assert.Single(receipt.Attempts).StartedAtUtc);
            var publication = Assert.Single(test.Storage.Publications);
            var temp = PrivateStorageV2Data.TempPath(test.Root, registration.Key);
            var originalIdentity = PrivateStorageV2Identity.Read(temp);
            Assert.Equal(0, new FileInfo(temp).Length);
            Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(temp));
            Assert.Equal(0, publication.ReadCalls);
            Assert.Empty(test.Native.Runner.NativeBodies);
            await test.AssertPoolsUnboundAsync(pids);

            var checkedMidpoint = false;
            var clock = await DatabaseClockAsync(test, deadline.Token);
            Assert.True(clock < claim.LeaseExpiresAtUtc);
            while (clock < claim.LeaseExpiresAtUtc)
            {
                if (!checkedMidpoint && clock >= claim.LeaseExpiresAtUtc.AddSeconds(-60))
                {
                    await test.AssertPoolsUnboundAsync(pids);
                    checkedMidpoint = true;
                }
                var remaining = claim.LeaseExpiresAtUtc - clock;
                await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), deadline.Token);
                clock = await DatabaseClockAsync(test, deadline.Token);
            }
            Assert.True(clock >= claim.LeaseExpiresAtUtc);
            Assert.True(checkedMidpoint);
            Assert.Equal(claim, Assert.Single(test.Metadata.Claims));
            Assert.Equal(originalIdentity, PrivateStorageV2Identity.Read(temp));
            Assert.Equal(0, publication.ReadCalls);
            Assert.Equal(0, new FileInfo(temp).Length);
            await test.AssertPoolsUnboundAsync(pids);
            gate.Release();
            var result = await original.WaitAsync(deadline.Token);
            Assert.Equal(RevisionIngestionOutcome.ClaimLost, result.Outcome);
            Assert.Equal(PrivatePublicationBindOutcome.OwnerUnavailable, Assert.IsType<PrivatePublicationBindResult>(observed.Result).Outcome);
            Assert.Equal(registration, observed.Reservation);
            Assert.Equal(originalIdentity, observed.Identity!.FileIdentity);
            Assert.Equal(0, publication.ReadCalls);
            Assert.Equal(0, publication.BytesRead);
            Assert.Empty(test.Native.Runner.NativeBodies);
            Assert.Empty(test.NativeRegistry.Bindings);
            Assert.False(File.Exists(temp));
            Assert.False(File.Exists(test.Root.FinalPath(publication.Reference)));
            var retained = await test.NativeRegistry.Actual.GetAsync(registration.Owner, PrivatePublicationStoreKind.FileSystem, registration.Key.RootId,
                registration.Key.Object, registration.Key.TempId, deadline.Token);
            Assert.NotNull(retained);
            Assert.Equal(registration, retained.Record.Reservation);
            Assert.Null(retained.Record.Binding);
            Assert.False(retained.OwnerIsLive);
            Assert.False(retained.CanWrite);
            Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
            await test.AssertPoolsUnboundAsync(pids);
        }
        finally
        {
            deadline.Cancel();
            gate.Release();
            try { await original.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            test.NativeRegistry.Override = null;
            test.NativeRegistry.BeforeBindAsync = null;
        }
    }

    private static async Task<DateTimeOffset> DatabaseClockAsync(PostgresClaimAwareStorageIntegrationSupport test,
        CancellationToken cancellationToken)
    {
        // Each lease is disposed before the bounded delay. No transaction or
        // connection is retained across the natural wall-clock wait.
        await using var connection = await test.Native.Database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await rows.ReadAsync(cancellationToken));
        return rows.GetFieldValue<DateTimeOffset>(0);
    }

    private sealed class CancelAfterCreatedStore(IPrivateObjectPublicationStore actual, CancellationTokenSource cancellation)
        : IPrivateObjectPublicationStore
    {
        private bool canceled;
        internal int CreatedReturns { get; private set; }
        internal int CompletedReads { get; private set; }
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference,
            Stream source, CancellationToken cancellationToken = default) => actual.PublishCreateOnlyAsync(scope, reference, source, cancellationToken);
        public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(PrivatePublicationOwner owner, RevisionScope scope,
            PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            var result = await actual.PublishCreateOnlyAsync(owner, scope, reference, source, cancellationToken);
            if (!canceled && result == PrivateObjectPublishResult.Created)
            {
                canceled = true;
                CreatedReturns++;
                cancellation.Cancel(); // Actual returned Created is preserved, not fabricated or replaced by a throw.
            }
            return result;
        }
        public async Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default)
        {
            var bytes = await actual.ReadVerifiedAsync(scope, reference, cancellationToken);
            CompletedReads++;
            return bytes;
        }
    }

    private sealed class ActualBindObservation(IPrivateObjectPublicationRegistry actual) : IPrivateObjectPublicationRegistry
    {
        internal PrivatePublicationBindResult? Result { get; private set; }
        internal PrivatePublicationReservation? Reservation { get; private set; }
        internal PrivatePublicationFileIdentity? Identity { get; private set; }
        public Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default) =>
            actual.ReserveAsync(owner, storeKind, rootId, objectId, tempId, absenceRecordDigest, cancellationToken);
        public async Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
            PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default)
        {
            Reservation = reservation;
            Identity = actualIdentity;
            var result = await actual.BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken);
            Result = result;
            return result;
        }
        public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default) =>
            actual.GetAsync(owner, storeKind, rootId, objectId, tempId, cancellationToken);
    }
}
