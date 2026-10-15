using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

public sealed partial class PostgresPrivateObjectMaintenanceCommandTests
{
    [RevisionIngestionFact]
    public async Task GenuineC2HistoryAndAllSelectedAndRetainedSuccessfulReportsSurviveHeadActorAndMetadataChanges()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
        var first = await test.CreateNativeAsync(bytes);
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, first.Outcome);
        var receipt = await test.ReceiptAsync(first);
        var scope = receipt.Facts.FutureRevision;
        var projection = RevisionReportTestSupport.Available(await test.Reports.EnsureAsync(test.Actor, scope, RevisionReportOperation.Projection));
        var validation = RevisionReportTestSupport.Available(await test.Reports.EnsureAsync(test.Actor, scope, RevisionReportOperation.Validation));
        var originalReport = projection.Metadata.DerivedObject;
        var originalIdentity = PrivateStorageV2Identity.Read(test.Root.FinalPath(originalReport));
        var originalBytes = File.ReadAllBytes(test.Root.FinalPath(originalReport));
        // One explicit exact-ID fault window, NOT a claim of physical Missing.
        // The original file stays intact while real service repair dispatches a
        // new worker/codec/registry publication and usable database completion.
        var readFault = new OneSelectedReadFault(test.Storage, originalReport.Id);
        test.ReportMetadata.BeforeRepairAsync = (_, _) =>
        {
            readFault.Disable();
            return Task.CompletedTask;
        };
        var repaired = RevisionReportTestSupport.Available(await test.NewReports(storage: readFault).EnsureAsync(test.Actor,
            scope, RevisionReportOperation.Projection, projection.Metadata.DerivationId));
        test.ReportMetadata.BeforeRepairAsync = null;
        // Both required exact-ID Inspect reads see the same injected fault;
        // production repair admission itself closes that window.
        Assert.Equal(2, readFault.Faults);
        Assert.NotEqual(projection.Metadata.DerivationId, repaired.Metadata.DerivationId);
        Assert.Equal(originalIdentity, PrivateStorageV2Identity.Read(test.Root.FinalPath(originalReport)));
        Assert.Equal(originalBytes, File.ReadAllBytes(test.Root.FinalPath(originalReport)));
        var append = await test.Ingestion.AppendAsync(test.Actor, first.Receipt!.FutureMapping, Guid.NewGuid(), new(), new MemoryStream(bytes));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, append.Outcome);
        var nextReceipt = await test.ReceiptAsync(append);
        Assert.NotEqual(scope.RevisionId, nextReceipt.Facts.FutureRevision.RevisionId);
        var nextReport = RevisionReportTestSupport.Available(await test.Reports.EnsureAsync(test.Actor,
            nextReceipt.Facts.FutureRevision, RevisionReportOperation.Validation));
        var objects = new[] { receipt.Facts.NativeObject, nextReceipt.Facts.NativeObject, projection.Metadata.DerivedObject,
            validation.Metadata.DerivedObject, repaired.Metadata.DerivedObject, nextReport.Metadata.DerivedObject };
        var preserved = objects.ToDictionary(x => x.Id, x => (Identity: PrivateStorageV2Identity.Read(test.Root.FinalPath(x)),
            Bytes: File.ReadAllBytes(test.Root.FinalPath(x)), Mode: File.GetUnixFileMode(test.Root.FinalPath(x))));
        Assert.Equal(4L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable"));
        await CommandTestSupport.AuthoredAgeActualOriginsAsync(test.Case, test.Project);
        await using (var editor = new PostgresMappingMetadataDatabase(test.Case.MetadataConnectionString))
        {
            var mapping = first.Receipt.FutureMapping;
            var edit = await new PostgresMappingMetadataWriter(editor).ReplaceAsync(test.Actor, mapping, new(mapping, 1),
                new("Changed after genuine publication", "Retained history"));
            Assert.Equal(MappingMetadataWriteOutcome.Updated, edit.Outcome);
        }
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
        var before = await CommandTestSupport.OriginsAsync(test.Case);
        await using (var database = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString))
        {
            var metadata = new PostgresPrivateObjectMaintenanceStore(database);
            var pid = await CommandTestSupport.AssertIdleAsync(database, test.Case.Name);
            var service = new PrivateObjectMaintenanceService(metadata, metadata, metadata, test.Physical, test.Physical, Guid.NewGuid());
            var run = await service.RunOnceAsync(test.Project);
            Assert.Equal(PrivateObjectMaintenanceRunOutcome.Finished, run.Outcome);
            Assert.Empty(run.Targets);
            Assert.Equal(0, run.PhysicalDeleted);
            Assert.Equal(before, await CommandTestSupport.OriginsAsync(test.Case));
            await CommandTestSupport.AssertIdleAsync(database, test.Case.Name, pid);
        }
        // New database pool and new strict store on the SAME database/root.
        await using var reopened = FileSystemV2PrivateObjectStore.Open(test.Root.Options);
        await using var fresh = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
        var current = new PostgresPrivateObjectMaintenanceStore(fresh);
        Assert.Empty((await new PrivateObjectMaintenanceService(current, current, current, reopened, reopened, Guid.NewGuid())
            .RunOnceAsync(test.Project)).Targets);
        foreach (var reference in objects)
        {
            var old = preserved[reference.Id];
            Assert.Equal(old.Bytes, await reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
            Assert.Equal(old.Identity, PrivateStorageV2Identity.Read(test.Root.FinalPath(reference)));
            Assert.Equal(old.Mode, File.GetUnixFileMode(test.Root.FinalPath(reference)));
            Assert.False(File.Exists(Path.Combine(PrivateStorageV2Data.ObjectLeaf(test.Root, reference.Id), "object.retired")));
        }
        Assert.Equal(before, await CommandTestSupport.OriginsAsync(test.Case));
    }

    [RevisionIngestionFact]
    public async Task ActualC2RejectionAndWorkerProducedFailedAndUnfinishedReportsAreDeletedOnlyAfterAuthoredGrace()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var rejected = await test.CreateNativeAsync("{not native JSON"u8.ToArray());
        Assert.Equal(RevisionIngestionOutcome.Rejected, rejected.Outcome);
        var terminal = await test.ReceiptAsync(rejected);
        var created = await test.CreateNativeAsync(RevisionIngestionTestSupport.Fixture("versioned-exact"));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
        var source = await test.ReceiptAsync(created);
        // Actual Created precedes this deliberate store-call failure. The normal
        // report service records failure; no positive completion is injected.
        var failedStore = new FailAfterDerivedCreated(test.Storage);
        await test.NewReports(storage: failedStore).EnsureAsync(test.Actor, source.Facts.FutureRevision, RevisionReportOperation.Projection);
        var failed = test.ReportMetadata.Claims[^1];
        Assert.False(test.ReportMetadata.Finishes[^1].Completion.IsUsable);
        Assert.Equal(RevisionReportStoreOutcome.Applied, test.ReportMetadata.Finishes[^1].Result.Outcome);
        Assert.Equal(1, failedStore.Created);
        // A bounded test-owned bookkeeping outage happens before Finish reaches
        // SQL. Real worker/codec/storage already produced the validation candidate.
        test.ReportMetadata.BeforeFinishAsync = (_, _, _) => throw new IOException("Owned pre-Finish outage; not a database completion.");
        try
        {
            await Assert.ThrowsAsync<RevisionReportServiceException>(() => test.Reports.EnsureAsync(test.Actor,
                source.Facts.FutureRevision, RevisionReportOperation.Validation));
        }
        finally { test.ReportMetadata.BeforeFinishAsync = null; }
        var unfinished = test.ReportMetadata.Claims[^1];
        Assert.NotEqual(failed.DerivationId, unfinished.DerivationId);
        Assert.True(await test.Case.ScalarAsync<bool>("SELECT finished_at IS NULL FROM weft.revision_derivation_attempts WHERE derivation_id=$1", unfinished.DerivationId));
        var failedObject = Assert.Single(test.Storage.Publications, x => x.Reference.Id == failed.CandidateObjectId).Reference;
        var unfinishedObject = Assert.Single(test.Storage.Publications, x => x.Reference.Id == unfinished.CandidateObjectId).Reference;
        var targets = new[] { terminal.Facts.NativeObject, failedObject, unfinishedObject };
        foreach (var target in targets) Assert.True(File.Exists(test.Root.FinalPath(target)));
        var protectedIdentity = PrivateStorageV2Identity.Read(test.Root.FinalPath(source.Facts.NativeObject));
        var protectedBytes = File.ReadAllBytes(test.Root.FinalPath(source.Facts.NativeObject));
        await using var database = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
        var metadata = new PostgresPrivateObjectMaintenanceStore(database);
        var observed = new CommandMetadata(metadata);
        var service = new PrivateObjectMaintenanceService(observed, metadata, metadata, test.Physical, test.Physical, Guid.NewGuid());
        Assert.Empty((await service.RunOnceAsync(test.Project)).Targets);
        await CommandTestSupport.AuthoredAgeActualOriginsAsync(test.Case, test.Project);
        var before = await CommandTestSupport.OriginsAsync(test.Case);
        var run = await service.RunOnceAsync(test.Project);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Finished, run.Outcome);
        Assert.Equal(3, run.PhysicalDeleted);
        Assert.Equal(3, run.AcknowledgedDeleted);
        Assert.Equal(3, run.ClaimedCount);
        Assert.Contains(observed.Claims, c => c.Retirement.Reason == ObjectCleanupReason.TerminalNativeStage);
        Assert.Contains(observed.Claims, c => c.Retirement.Reason == ObjectCleanupReason.FailedDerivedCandidate);
        Assert.Contains(observed.Claims, c => c.Retirement.Reason == ObjectCleanupReason.ExpiredDerivedCandidate);
        foreach (var target in targets) Assert.False(File.Exists(test.Root.FinalPath(target)));
        Assert.Equal(before, await CommandTestSupport.OriginsAsync(test.Case));
        Assert.Equal(protectedIdentity, PrivateStorageV2Identity.Read(test.Root.FinalPath(source.Facts.NativeObject)));
        Assert.Equal(protectedBytes, await test.Physical.ReadVerifiedAsync(source.Facts.FutureRevision, source.Facts.NativeObject));
        await CommandTestSupport.AssertIdleAsync(database, test.Case.Name);
    }

    private sealed class OneSelectedReadFault(IPrivateObjectPublicationStore actual, PrivateObjectId selected) : IPrivateObjectPublicationStore
    {
        private bool enabled = true;
        internal int Faults { get; private set; }
        internal void Disable() => enabled = false;
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference,
            Stream source, CancellationToken cancellationToken = default) => actual.PublishCreateOnlyAsync(scope, reference, source, cancellationToken);
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(PrivatePublicationOwner owner, RevisionScope scope,
            PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) =>
            actual.PublishCreateOnlyAsync(owner, scope, reference, source, cancellationToken);
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default)
        {
            if (reference.Id == selected && enabled)
            {
                Faults++;
                throw new PrivateObjectStorageException(PrivateObjectStorageError.Missing);
            }
            return actual.ReadVerifiedAsync(scope, reference, cancellationToken);
        }
    }

    private sealed class FailAfterDerivedCreated(IPrivateObjectPublicationStore actual) : IPrivateObjectPublicationStore
    {
        internal int Created { get; private set; }
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference,
            Stream source, CancellationToken cancellationToken = default) => actual.PublishCreateOnlyAsync(scope, reference, source, cancellationToken);
        public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(PrivatePublicationOwner owner, RevisionScope scope,
            PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            var result = await actual.PublishCreateOnlyAsync(owner, scope, reference, source, cancellationToken);
            if (reference.Id.Class == PrivateObjectClass.DerivedArtifact && result == PrivateObjectPublishResult.Created)
            {
                Created++;
                throw new PrivateObjectStorageException(PrivateObjectStorageError.Io);
            }
            return result;
        }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default) =>
            actual.ReadVerifiedAsync(scope, reference, cancellationToken);
    }
}
