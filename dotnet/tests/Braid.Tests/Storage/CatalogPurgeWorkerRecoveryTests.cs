using System.Runtime.Versioning;
using Braid.Application;
using Braid.Application.Lifecycle;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[SupportedOSPlatform("linux")]
[Collection(PostgresCollection.Name)]
public sealed class CatalogPurgeWorkerRecoveryTests(PostgresFixture fixture)
{
    public static TheoryData<string> Stores() => PrivateObjectStoreContractTests.Stores();

    [PostgresTheory, MemberData(nameof(Stores))]
    public async Task CatalogPurgeWorkerReclaimsAnUnacknowledgedPhysicalDeletion(string kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture, installCatalog: false);
        var bytes = "task-local recovery fixture"u8.ToArray();
        var committed = await test.CommitCreateAsync(body: bytes);
        await test.Case.MigrateAsync();
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='administrator',version=version+1 WHERE principal_id=$1 AND project_id=$2", test.Actor.PrincipalId.Value, test.Project.ProjectId);
        var azurite = new AzuriteFixture();
        if (kind == StoreHarness.Blob) await azurite.InitializeAsync();
        try
        {
            await using var harness = await StoreHarness.CreateAsync(kind, azurite);
            var registry = new PrivateStorageV2PhysicalRegistry();
            var native = committed.Facts.NativeObject;
            var resource = new ResourceKey(test.Project, native.Id.Scope.MappingId);
            var db = test.Case;
            await using var admin = new PostgresAdministrationStore(db.RegressionConnectionString("braid_administration"));
            var worker = db.RegressionConnectionString("braid_lifecycle_worker");
            DeletionJobClaim oldClaim;
            CatalogObjectDeletionClaim oldObject;
            await using (var jobs = new PostgresDeletionJobStore(worker))
            await using (var physical = await harness.InitializeAsync(() => registry))
            {
                await physical.PublishAsync(PrivateStorageV2Data.Owner(native), native, new MemoryStream(bytes));
                var archive = await admin.ChangeAsync(test.Actor, new(resource, ResourceLifecycleAction.Archive, Guid.NewGuid(), new(1, 1)), token);
                var deletion = await admin.RequestAsync(test.Actor, new(resource, Guid.NewGuid(), Guid.NewGuid(), archive.Receipt!.Version), token);
                Assert.Equal(ResourcePurgeOutcome.Accepted, deletion.Outcome);
                oldClaim = Assert.IsType<DeletionJobClaim>(await jobs.ClaimAsync(Guid.NewGuid(), token));
                var step = DeletionJobStepOutcome.Advanced;
                for (int i = 0; i < 24 && step == DeletionJobStepOutcome.Advanced; i++) step = await jobs.AdvanceAsync(oldClaim, token);
                Assert.Equal(DeletionJobStepOutcome.Objects, step);
                oldObject = Assert.Single(await jobs.ClaimObjectsAsync(oldClaim, 16, token));
                Assert.Equal(native.Id, oldObject.Target.Object);
                Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await ((IPrivateObjectDeletionStore)physical.Retirement).DeleteAsync(oldObject, token));
                // No FinishObjectAsync and no ReleaseAsync: simulate losing the process here.
            }
            // Advance only the two claim deadlines in this case database. No guard is disabled.
            await db.ExecuteAsync("UPDATE braid.deletion_jobs SET claim_expires_at=clock_timestamp(),version=version+1 WHERE job_id=$1", oldClaim.JobId);
            await db.ExecuteAsync("UPDATE braid.deletion_objects SET claim_expires_at=clock_timestamp() WHERE job_id=$1", oldClaim.JobId);
            await using var recovered = new PostgresDeletionJobStore(worker);
            await using var reopened = await harness.OpenAsync();
            var next = Assert.IsType<DeletionJobClaim>(await recovered.ClaimAsync(Guid.NewGuid(), token));
            Assert.Equal(oldClaim.JobId, next.JobId);
            Assert.True(next.Generation > oldClaim.Generation);
            var nextObject = Assert.Single(await recovered.ClaimObjectsAsync(next, 16, token));
            Assert.True(nextObject.Generation > oldObject.Generation);
            Assert.Equal(oldObject.ObjectTargetId, nextObject.ObjectTargetId);
            Assert.False(await recovered.FinishObjectAsync(oldClaim, oldObject, PrivateObjectRetirementOutcome.Deleted, token));
            var observed = await ((IPrivateObjectDeletionStore)reopened.Retirement).DeleteAsync(nextObject, token);
            Assert.Equal(PrivateObjectRetirementOutcome.Missing, observed);
            Assert.True(await recovered.FinishObjectAsync(next, nextObject, observed, token));
            Assert.Equal(DeletionJobStepOutcome.Completed, await recovered.AdvanceAsync(next, token));
            Assert.Null(await harness.FinalBytesAsync(native));
            Assert.Null(await recovered.ClaimAsync(Guid.NewGuid(), token));
        }
        finally { if (kind == StoreHarness.Blob) await azurite.DisposeAsync(); }
    }
}
