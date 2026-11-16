using System.Runtime.Versioning;
using System.Text.Json;
using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RevisionReportReadRepairTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task RealMissingAndCorruptSelectedObjectsRequireExplicitExactRepairAndRetainOriginalAttempts()
    {
        foreach (var corrupt in new[] { false, true })
        {
            await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
            var source = await test.SourceSnapshotAsync();
            var original = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
            var originalRow = await test.AttemptSnapshotAsync(original.Metadata.DerivationId);
            var healthy = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision,
                RevisionReportOperation.Projection, original.Metadata.DerivationId));
            Assert.Equal(original.Metadata, healthy.Metadata);
            Assert.Equal(0, test.MetadataPort.RepairCalls);
            var path = test.PhysicalRoot.FinalPath(original.Metadata.DerivedObject);
            if (corrupt)
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await File.WriteAllBytesAsync(path, "Actual owned corrupted blob"u8.ToArray());
                File.SetUnixFileMode(path, PrivateStorageTestRoot.FinalMode);
            }
            else File.Delete(path);
            var expected = corrupt ? RevisionReportReadOutcome.StorageIntegrity : RevisionReportReadOutcome.ObjectUnavailable;
            var read = await test.Service.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
            Assert.Equal(expected, read.Outcome);
            Assert.Equal(original.Metadata, read.SelectedResult);
            Assert.Null(read.Report);
            var unchanged = await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
            Assert.Equal(RevisionReportEnsureOutcome.Observed, unchanged.Outcome);
            Assert.Equal(expected, unchanged.Read!.Outcome);
            Assert.Equal(1, test.BackendPort.Calls);
            Assert.Equal(0, test.MetadataPort.RepairCalls);
            var beforeWrongSelectionReads = test.StoragePort.DerivedReads;
            Assert.Equal(RevisionReportEnsureOutcome.SelectionChanged, (await test.Service.EnsureAsync(test.Actor, test.Revision,
                RevisionReportOperation.Projection, Guid.NewGuid())).Outcome);
            Assert.Equal(beforeWrongSelectionReads, test.StoragePort.DerivedReads);
            Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());

            var repaired = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision,
                RevisionReportOperation.Projection, original.Metadata.DerivationId));
            Assert.NotEqual(original.Metadata.DerivationId, repaired.Metadata.DerivationId);
            Assert.NotEqual(original.Metadata.DerivedObject.Id, repaired.Metadata.DerivedObject.Id);
            Assert.Equal(original.Metadata.CacheEntryId, repaired.Metadata.CacheEntryId);
            Assert.Equal(original.Metadata.Generation + 1, repaired.Metadata.Generation);
            var claim = test.MetadataPort.Claims[^1];
            Assert.Equal(original.Metadata.DerivationId, claim.RepairTargetDerivationId);
            Assert.Equal(repaired.Metadata.DerivedObject.Id, claim.CandidateObjectId);
            Assert.Equal(1, test.MetadataPort.RepairCalls);
            Assert.Equal(2, test.BackendPort.Calls);
            Assert.Equal(originalRow, await test.AttemptSnapshotAsync(original.Metadata.DerivationId));
            Assert.Equal(original.Metadata, (await test.Reader.GetRetainedAsync(test.Actor, test.Revision,
                RevisionReportOperation.Projection, original.Metadata.DerivationId))!.Result);
            Assert.Equal(repaired.Metadata, (await test.Reader.GetCurrentAsync(test.Actor, test.Key(RevisionReportOperation.Projection)))!.SelectedResult);
            Assert.Equal(expected, (await test.Service.GetRetainedAsync(test.Actor, test.Revision,
                RevisionReportOperation.Projection, original.Metadata.DerivationId)).Outcome);
            Assert.Equal(source, await test.SourceSnapshotAsync());
            Assert.Equal(test.NativeBytes, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, test.Source.NativeObject));
            Assert.Equal(new long[] { 1, 2, 2, 0 }, await test.CountsAsync());
        }
    }

    [RevisionIngestionFact]
    public async Task SyntheticDishonestReadPortCannotSubstituteTruncateOrMutateAcceptedRealCachedReports()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var original = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        var row = await test.AttemptSnapshotAsync(original.Metadata.DerivationId);
        // Each negative input follows a real verified filesystem read; only the forwarding return is dishonest.
        foreach (var substitute in new Func<byte[], byte[]>[]
        {
            bytes => bytes[..^1],
            bytes => bytes.Concat(new byte[] { (byte)' ' }).ToArray(),
            bytes => { var changed = bytes.ToArray(); changed[0] ^= 1; return changed; }
        })
        {
            test.StoragePort.ReadSubstitution = (reference, bytes) => reference.Id.Class == PrivateObjectClass.DerivedArtifact
                ? substitute(bytes) : bytes;
            var refused = await test.Service.GetRetainedAsync(test.Actor, test.Revision, RevisionReportOperation.Projection, original.Metadata.DerivationId);
            Assert.Equal(RevisionReportReadOutcome.StorageIntegrity, refused.Outcome);
            Assert.Equal(original.Metadata, refused.SelectedResult);
            Assert.Null(refused.Report);
        }
        byte[]? returned = null;
        test.StoragePort.ReadSubstitution = (_, bytes) => returned = bytes;
        var accepted = Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(
            await test.Service.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)));
        var serialized = JsonSerializer.SerializeToUtf8Bytes(accepted.Report, ProjectionJson.CreateOptions());
        Assert.NotNull(returned);
        Array.Fill(returned, (byte)'!');
        Assert.Equal(serialized, JsonSerializer.SerializeToUtf8Bytes(accepted.Report, ProjectionJson.CreateOptions()));
        test.StoragePort.ReadSubstitution = null;
        Assert.Equal(original.Metadata, RevisionReportTestSupport.Available(await test.Service.GetCurrentAsync(test.Actor,
            test.Revision, RevisionReportOperation.Projection)).Metadata);
        Assert.Equal(row, await test.AttemptSnapshotAsync(original.Metadata.DerivationId));
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(1, test.StoragePort.PublishCalls);
        Assert.Equal(0, test.MetadataPort.RepairCalls);
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
    }

    [RevisionIngestionFact]
    public async Task ActualSourceAndDerivedReferenceCapsRefuseBeforeLazyStoreResolutionWithoutRepair()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var original = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        var counts = await test.CountsAsync();
        var resolutions = 0;
        IPrivateObjectStore ResolveForbidden()
        {
            Interlocked.Increment(ref resolutions);
            throw new InvalidOperationException("A refused bounded read resolved storage.");
        }
        var caps = new RevisionReportStorageRegistration(PrivateStorageTestRoot.NativeLimit,
            PrivateStorageTestRoot.DerivedLimit, PrivateStorageTestRoot.DerivedLimit, ResolveForbidden);
        var read = new RevisionReportService(test.MetadataPort, test.MetadataPort, test.Registry, caps, Guid.NewGuid(),
            new(storedObjectByteLimit: 1));
        var refused = await read.GetRetainedAsync(test.Actor, test.Revision, RevisionReportOperation.Projection, original.Metadata.DerivationId);
        Assert.Equal(RevisionReportReadOutcome.ResourceLimit, refused.Outcome);
        Assert.Equal(original.Metadata, refused.SelectedResult);
        Assert.Null(refused.Report);
        var nativeLimit = new RevisionReportService(test.MetadataPort, test.MetadataPort,
            test.NewRegistry(new(nativeByteLimit: 1)), caps, Guid.NewGuid());
        var beforeClaims = test.MetadataPort.ClaimCalls;
        var error = await Assert.ThrowsAsync<RevisionReportServiceException>(() => nativeLimit.EnsureAsync(
            test.Actor, test.Revision, RevisionReportOperation.Projection));
        Assert.Equal(RevisionReportFailureCategory.ResourceLimit, error.Failure);
        Assert.Equal(beforeClaims, test.MetadataPort.ClaimCalls);
        Assert.Equal(0, resolutions);
        Assert.Equal(0, test.MetadataPort.RepairCalls);
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(counts, await test.CountsAsync());
        // An unsafe owned root is not proof that this particular selected object is missing or corrupt.
        File.SetUnixFileMode(test.PhysicalRoot.MarkerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            var unsafeRead = await Assert.ThrowsAsync<RevisionReportServiceException>(() => test.Service.GetCurrentAsync(
                test.Actor, test.Revision, RevisionReportOperation.Projection));
            Assert.Equal(RevisionReportFailureCategory.StorageIo, unsafeRead.Failure);
            var unsafeRepair = await Assert.ThrowsAsync<RevisionReportServiceException>(() => test.Service.EnsureAsync(
                test.Actor, test.Revision, RevisionReportOperation.Projection, original.Metadata.DerivationId));
            Assert.Equal(RevisionReportFailureCategory.StorageIo, unsafeRepair.Failure);
        }
        finally { File.SetUnixFileMode(test.PhysicalRoot.MarkerPath, PrivateStorageTestRoot.FinalMode); }
        Assert.Equal(0, test.MetadataPort.RepairCalls);
        Assert.Equal(counts, await test.CountsAsync());
        Assert.Equal(original.Metadata, RevisionReportTestSupport.Available(await test.Service.GetCurrentAsync(
            test.Actor, test.Revision, RevisionReportOperation.Projection)).Metadata);
    }

    [RevisionIngestionFact]
    public async Task UnexpectedAlreadyPresentFromRealCreateOnlyStoreIsNeverAdoptedEvenForIdenticalBytes()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var collision = new PublicationCollisionStore(test.PhysicalStore);
        var service = test.NewService(store: collision);
        var result = await service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportEnsureOutcome.Observed, result.Outcome);
        Assert.Equal(RevisionReportState.Failed, result.Read!.State);
        Assert.Equal(RevisionReportFailureCategory.StorageIntegrity, result.Read.Failure);
        Assert.Null(result.Read.Report);
        var reference = Assert.IsType<PrivateObjectReference>(collision.Reference);
        Assert.Equal(PrivateObjectPublishResult.Created, collision.FirstPublication);
        Assert.Equal(PrivateObjectPublishResult.AlreadyPresent, collision.SecondPublication);
        Assert.Equal(collision.Bytes, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, reference));
        var claim = Assert.Single(test.MetadataPort.Claims);
        Assert.Equal(claim.CandidateObjectId, reference.Id);
        var finish = Assert.Single(test.MetadataPort.Finishes);
        Assert.False(finish.Completion.IsUsable);
        Assert.Null(finish.Completion.DerivedObject);
        Assert.True(finish.Completion.Observation!.Provenance!.Confirmed);
        Assert.Equal(RevisionReportBackendStatus.Succeeded, finish.Completion.Observation.Status);
        Assert.Equal(new long[] { 1, 1, 0, 1 }, await test.CountsAsync());
        Assert.Equal(RevisionReportEnsureOutcome.RetryDelayed,
            (await service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)).Outcome);
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(collision.Bytes, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, reference));
    }

    [RevisionIngestionFact]
    public async Task RealCompetingRepairChangesSelectionBeforeCasAndTheOlderServiceCannotAdoptIt()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var original = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        var row = await test.AttemptSnapshotAsync(original.Metadata.DerivationId);
        File.Delete(test.PhysicalRoot.FinalPath(original.Metadata.DerivedObject));
        var gate = new RevisionReportBoundaryGate();
        test.MetadataPort.BeforeRepairAsync = (_, token) => gate.HoldAsync(token);
        var first = test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection, original.Metadata.DerivationId);
        VerifiedRevisionReport replacement;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await test.AssertPoolsUnboundAsync();
            // Independent production service, using direct real metadata ports, changes the selection legitimately.
            var contender = test.NewService(reader: test.Reader, metadata: test.Store);
            replacement = RevisionReportTestSupport.Available(await contender.EnsureAsync(test.Actor, test.Revision,
                RevisionReportOperation.Projection, original.Metadata.DerivationId));
            Assert.NotEqual(original.Metadata.DerivationId, replacement.Metadata.DerivationId);
        }
        finally { gate.Release(); }
        var lost = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RevisionReportEnsureOutcome.SelectionChanged, lost.Outcome);
        Assert.Null(lost.Read);
        Assert.Null(lost.Snapshot);
        Assert.Equal(replacement.Metadata, (await test.Reader.GetCurrentAsync(test.Actor, test.Key(RevisionReportOperation.Projection)))!.SelectedResult);
        Assert.Equal(row, await test.AttemptSnapshotAsync(original.Metadata.DerivationId));
        Assert.Equal(2, test.BackendPort.Calls);
        Assert.Equal(new long[] { 1, 2, 2, 0 }, await test.CountsAsync());
    }

    /// <summary>Explicit injected collision: both publications and their outcomes are from the actual store.</summary>
    private sealed class PublicationCollisionStore(IPrivateObjectStore actual) : IPrivateObjectStore
    {
        internal PrivateObjectReference? Reference { get; private set; }
        internal byte[]? Bytes { get; private set; }
        internal PrivateObjectPublishResult? FirstPublication { get; private set; }
        internal PrivateObjectPublishResult? SecondPublication { get; private set; }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default) =>
            actual.ReadVerifiedAsync(scope, reference, cancellationToken);
        public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference,
            Stream source, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var read = await source.ReadAsync(chunk, cancellationToken);
                if (read == 0) break;
                Assert.True(buffer.Length + read <= PrivateStorageTestRoot.DerivedLimit);
                buffer.Write(chunk, 0, read);
            }
            Reference = reference;
            var bytes = buffer.ToArray();
            Bytes = bytes;
            using var first = new MemoryStream(bytes);
            FirstPublication = await actual.PublishCreateOnlyAsync(scope, reference, first, cancellationToken);
            using var second = new MemoryStream(bytes);
            var result = await actual.PublishCreateOnlyAsync(scope, reference, second, cancellationToken);
            SecondPublication = result;
            return result;
        }
    }
}
