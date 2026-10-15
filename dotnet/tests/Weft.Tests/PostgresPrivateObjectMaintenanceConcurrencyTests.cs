using System.Data;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual backend waits and restricted SQL; clock setup and report evidence are synthetic metadata.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresPrivateObjectMaintenanceConcurrencyTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task RestrictedNativeReferenceFirstMakesTheWaitingRetirementObserveItsCommittedNonHeadProtector()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateHeadConflictNativeAsync();
        var maintenancePid = await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(test.MaintenanceDatabase.DataSource);
        await using var writer = await test.IngestionDatabase.BeginTransactionAsync(test.Project.OrganizationId);
        var writerPid = writer.Connection.ProcessID;
        Assert.Equal(1, await PostgresPrivateObjectMaintenanceTestSupport.InsertHistoricalNativeReferenceAsync(writer.Connection, writer.Transaction, facts));
        await using var observer = await test.OpenBlockingObserverAsync(maintenancePid, writerPid);
        var waiting = test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid());
        try
        {
            await observer.WaitForBlockedAsync(waiting);
            await writer.CommitAsync();
            Assert.DoesNotContain((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Claims,
                c => c.Retirement.Target.Object == facts.NativeObject.Id);
        }
        finally { await writer.DisposeAsync(); await DrainAsync(waiting); }
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions WHERE ingestion_operation_id=$1", facts.OperationId));
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task RetirementFirstFencesAnAlreadyStartedRestrictedInsertAfterItsActualWaitOrRollbackReleasesIt()
    {
        foreach (var commit in new[] { true, false })
        {
            await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
            var facts = await test.CreateHeadConflictNativeAsync();
            await using var retiring = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project);
            var batch = await test.Maintenance.ClaimInTransactionAsync(retiring, test.Project, Guid.NewGuid());
            var claim = Assert.Single(batch.Claims);
            Assert.Equal(facts.NativeObject.Id, claim.Retirement.Target.Object);
            Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
            await using var writer = await test.IngestionDatabase.BeginTransactionAsync(test.Project.OrganizationId);
            await using var observer = await test.OpenBlockingObserverAsync(writer.Connection.ProcessID, retiring.Connection.ProcessID);
            // The INSERT ... SELECT has its original outer snapshot before the
            // maintenance commit. Only the trigger's separate post-lock query sees it.
            var insertion = PostgresPrivateObjectMaintenanceTestSupport.InsertHistoricalNativeReferenceAsync(
                writer.Connection, writer.Transaction, facts);
            try
            {
                await observer.WaitForBlockedAsync(insertion);
                if (commit) await retiring.CommitAsync(); else await retiring.RollbackAsync();
                if (commit)
                {
                    var refused = await Assert.ThrowsAsync<PostgresException>(() => insertion.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal("23514", refused.SqlState);
                    Assert.Equal("Retired native objects cannot acquire references or resume.", refused.MessageText);
                    await writer.RollbackAsync();
                    Assert.Equal(new long[] { 1, 1, 0 }, await test.CountsAsync());
                }
                else
                {
                    Assert.Equal(1, await insertion.WaitAsync(TimeSpan.FromSeconds(5)));
                    await writer.CommitAsync();
                    Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
                }
            }
            finally { await retiring.DisposeAsync(); await writer.DisposeAsync(); await DrainAsync(insertion); }
            Assert.Equal(commit ? 0L : 1L,
                await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions WHERE ingestion_operation_id=$1", facts.OperationId));
            Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
        }
    }

    [PostgresFact]
    public async Task RepeatableReadAndSerializableRestrictedAttachmentAreRefusedEvenWithoutARetirement()
    {
        foreach (var isolation in new[] { IsolationLevel.RepeatableRead, IsolationLevel.Serializable })
        {
            await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
            var facts = await test.CreateHeadConflictNativeAsync();
            var opened = await test.OpenRestrictedAsync(test.Case.IngestionConnectionString, isolation);
            await using var connection = opened.Connection;
            await using var transaction = opened.Transaction;
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                PostgresPrivateObjectMaintenanceTestSupport.InsertHistoricalNativeReferenceAsync(connection, transaction, facts));
            Assert.Equal("42501", error.SqlState);
            Assert.Equal("Private object metadata requires the trusted transaction profile.", error.MessageText);
            await transaction.RollbackAsync();
            Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions WHERE ingestion_operation_id=$1", facts.OperationId));
            Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
        }
    }

    [PostgresFact]
    public async Task CompetingClaimsObserveOneCommittedLeaseOrReplaceOnlyARolledBackFirstClaim()
    {
        foreach (var commit in new[] { true, false })
        {
            await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
            var facts = await test.CreateRejectedNativeAsync();
            await using var otherDatabase = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
            var other = new PostgresPrivateObjectMaintenanceStore(otherDatabase);
            var otherPid = await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(otherDatabase.DataSource);
            await using var first = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project);
            var original = Assert.Single((await test.Maintenance.ClaimInTransactionAsync(first, test.Project, Guid.NewGuid())).Claims);
            await using var observer = await test.OpenBlockingObserverAsync(otherPid, first.Connection.ProcessID);
            var pending = other.ClaimAsync(test.Project, Guid.NewGuid());
            try
            {
                await observer.WaitForBlockedAsync(pending);
                if (commit) await first.CommitAsync(); else await first.RollbackAsync();
                var batch = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                if (commit) Assert.Empty(batch.Claims);
                else
                {
                    var replacement = Assert.Single(batch.Claims);
                    Assert.Equal(facts.NativeObject.Id, replacement.Retirement.Target.Object);
                    Assert.Equal(1, replacement.Generation);
                    Assert.NotEqual(original.Retirement.RetirementId, replacement.Retirement.RetirementId);
                    Assert.NotEqual(original.AttemptId, replacement.AttemptId);
                }
            }
            finally { await first.DisposeAsync(); await DrainAsync(pending); }
            Assert.Equal(new long[] { 1, 1, 0 }, await test.CountsAsync());
        }
    }

    [PostgresFact]
    public async Task ObservedOriginLockCancellationRollsBackAllClaimsAndTheSamePoolBackendRecoversUnbound()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var pid = await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(test.MaintenanceDatabase.DataSource);
        await using var owner = await test.OpenOwnerAsync();
        await using var held = await owner.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT operation_id FROM weft.revision_ingestions WHERE operation_id=$1 FOR UPDATE", owner, held))
        {
            command.Parameters.AddWithValue(facts.OperationId);
            Assert.Equal(facts.OperationId, await command.ExecuteScalarAsync());
        }
        using var cancel = new CancellationTokenSource();
        await using var observer = await test.OpenBlockingObserverAsync(pid, owner.ProcessID);
        var pending = test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid(), cancellationToken: cancel.Token);
        try
        {
            await observer.WaitForBlockedAsync(pending);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { await held.RollbackAsync(); await DrainAsync(pending); }
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
        Assert.Equal(pid, await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(test.MaintenanceDatabase.DataSource));
        await using (var leased = await test.MaintenanceDatabase.DataSource.OpenConnectionAsync())
        await using (var inspect = new NpgsqlCommand("SELECT ARRAY[coalesce(current_setting('weft.organization_id',true),''),coalesce(current_setting('weft.project_id',true),'')]", leased))
            Assert.Equal(new[] { "", "" }, (string[])(await inspect.ExecuteScalarAsync())!);
        Assert.Equal(facts.NativeObject.Id, (await test.ClaimSingleAsync(facts.NativeObject.Id)).Retirement.Target.Object);
    }

    [PostgresFact]
    public async Task ANewReportAttemptUsesItsNewCandidateWhileTheOldExactCandidateRemainsRetired()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var original = await test.ReserveReportAsync();
        original = await test.SyntheticAgeReportAsync(original, TimeSpan.FromHours(25));
        var reportPid = await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(test.DerivationDatabase.DataSource);
        await using var retiring = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project);
        var cleanup = Assert.Single((await test.Maintenance.ClaimInTransactionAsync(retiring, test.Project, Guid.NewGuid())).Claims);
        Assert.Equal(original.CandidateObjectId, cleanup.Retirement.Target.Object);
        await using var observer = await test.OpenBlockingObserverAsync(reportPid, retiring.Connection.ProcessID);
        var pending = test.Reports.TryClaimAsync(test.Actor, original.Key, Guid.NewGuid());
        try
        {
            await observer.WaitForBlockedAsync(pending);
            await retiring.CommitAsync();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RevisionReportStoreOutcome.Claimed, result.Outcome);
            Assert.Equal(2, result.Claim!.Generation);
            Assert.NotEqual(original.CandidateObjectId, result.Claim.CandidateObjectId);
            // INSERT's earlier transition assigns this new ID. The retirement
            // guard must use NEW, not search for a row not yet inserted.
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_derivation_attempts WHERE candidate_object_id=$1", result.Claim.CandidateObjectId.ObjectId));
        }
        finally { await retiring.DisposeAsync(); await DrainAsync(pending); }
        var stale = await test.Reports.FinishAsync(test.Actor, original,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(original));
        Assert.Equal(RevisionReportStoreOutcome.ClaimLost, stale.Outcome);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_derivation_attempts WHERE candidate_object_id=$1 AND result_usable", original.CandidateObjectId.ObjectId));
        Assert.Equal(new long[] { 1, 1, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task AShortMetadataTransactionHoldsNoAuthorityOverAnotherProjectAndRollbackExposesNoFence()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        await using var otherDatabase = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
        var other = new PostgresPrivateObjectMaintenanceStore(otherDatabase);
        ObjectCleanupClaim uncommitted;
        await using (var held = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project))
        {
            uncommitted = Assert.Single((await test.Maintenance.ClaimInTransactionAsync(held, test.Project, Guid.NewGuid())).Claims);
            Assert.Empty((await other.ClaimAsync(new(test.Seed.OrganizationB, test.Seed.ProjectB), Guid.NewGuid()).WaitAsync(TimeSpan.FromSeconds(5))).Claims);
            Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
            await held.RollbackAsync();
        }
        Assert.Null(await other.GetAttemptAsync(test.Project, uncommitted.Retirement.RetirementId, 1));
        var next = await test.ClaimSingleAsync(facts.NativeObject.Id);
        Assert.Equal(1, next.Generation);
        Assert.NotEqual(uncommitted.Retirement.RetirementId, next.Retirement.RetirementId);
    }

    private static async Task DrainAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch when (task.IsCompleted) { }
    }
}
