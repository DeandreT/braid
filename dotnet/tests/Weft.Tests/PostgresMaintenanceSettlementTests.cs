using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual PostgreSQL acknowledgements. Source admission, clock aging and physical dispositions are authored metadata; no deletion is asserted.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresMaintenanceSettlementTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task EitherPreCanceledTokenRefusesBeforeTheOnlyPoolLeaseAndLeavesLegacyFinishAvailable()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        var before = await test.CleanupSnapshotAsync(claim.Retirement.RetirementId);
        using var work = new CancellationTokenSource(); work.Cancel();
        using var settlement = new CancellationTokenSource(); settlement.Cancel();
        await using (var held = await test.MaintenanceDatabase.DataSource.OpenConnectionAsync())
        {
            var workError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Missing, work.Token, CancellationToken.None));
            Assert.Equal(work.Token, workError.CancellationToken);
            var settlementError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Missing, CancellationToken.None, settlement.Token));
            Assert.Equal(settlement.Token, settlementError.CancellationToken);
            var bothError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Missing, work.Token, settlement.Token));
            Assert.Equal(work.Token, bothError.CancellationToken);
            Assert.Equal(before, await test.CleanupSnapshotAsync(claim.Retirement.RetirementId));
        }
        // The old signature does not acquire a new settlement dependency.
        var result = await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Missing);
        Assert.Equal(ObjectCleanupFinishOutcome.Applied, result.Outcome);
        Assert.Equal(claim, result.Attempt!.Claim);
        Assert.Equal(ObjectCleanupDisposition.Missing, result.Attempt.Disposition);
    }

    [PostgresFact]
    public async Task SharedSettlementFinishReplaysOnlyItsExactClaimAndDispositionWithoutRewritingEvidence()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var forged = new[]
        {
            CopyClaim(claim, attemptId: Guid.NewGuid()), CopyClaim(claim, instance: Guid.NewGuid()),
            CopyClaim(claim, token: Guid.NewGuid())
        };
        var pending = await test.CleanupSnapshotAsync(claim.Retirement.RetirementId);
        foreach (var other in forged)
        {
            Assert.Equal(ObjectCleanupFinishOutcome.ClaimLost,
                (await test.Maintenance.FinishAsync(other, ObjectCleanupDisposition.Deleted, CancellationToken.None, settlement.Token)).Outcome);
            Assert.Equal(pending, await test.CleanupSnapshotAsync(claim.Retirement.RetirementId));
        }
        var applied = await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Deleted, CancellationToken.None, settlement.Token);
        Assert.Equal(ObjectCleanupFinishOutcome.Applied, applied.Outcome);
        Assert.Equal(claim, applied.Attempt!.Claim);
        Assert.Equal(ObjectCleanupDisposition.Deleted, applied.Attempt.Disposition);
        Assert.NotNull(applied.Attempt.FinishedAtUtc);
        var committed = await test.CleanupSnapshotAsync(claim.Retirement.RetirementId);
        var replay = await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Deleted, CancellationToken.None, settlement.Token);
        Assert.Equal(ObjectCleanupFinishOutcome.AlreadyApplied, replay.Outcome);
        Assert.Equal(applied.Attempt, replay.Attempt);
        Assert.Equal(ObjectCleanupFinishOutcome.ClaimLost,
            (await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Missing, CancellationToken.None, settlement.Token)).Outcome);
        Assert.Equal(applied.Attempt, await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, claim.Generation, settlement.Token));
        Assert.Equal(committed, await test.CleanupSnapshotAsync(claim.Retirement.RetirementId));
    }

    [PostgresFact]
    public async Task WorkCancellationAtAnObservedOriginLockSettlesBeforeExactReadAndSameBackendRecovery()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        var before = await test.CleanupSnapshotAsync(claim.Retirement.RetirementId);
        var pid = await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(test.MaintenanceDatabase.DataSource);
        using var work = new CancellationTokenSource();
        using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var gate = await LockOriginAsync(test, claim))
        await using (var observer = await test.OpenBlockingObserverAsync(pid, gate.Backend))
        {
            var finishing = test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.StorageIo, work.Token, settlement.Token);
            try
            {
                await observer.WaitForBlockedAsync(finishing);
                work.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finishing);
                Assert.Equal(work.Token, error.CancellationToken);
                // The original Finish has settled while the blocker is still held.
                // This retained read therefore cannot be an overlapping transaction.
                var retained = await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, claim.Generation, settlement.Token);
                Assert.Equal(claim, retained!.Claim);
                Assert.Null(retained.Disposition);
                Assert.Null(retained.FinishedAtUtc);
                Assert.Equal(before, await test.CleanupSnapshotAsync(claim.Retirement.RetirementId));
                await PostgresMaintenanceObservationTestSupport.AssertUnboundAsync(test, pid);
            }
            finally
            {
                work.Cancel();
                await gate.ReleaseAsync();
                try { await finishing; } catch (OperationCanceledException) { }
            }
        }
        Assert.Equal(ObjectCleanupFinishOutcome.Applied,
            (await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.StorageIo, CancellationToken.None, settlement.Token)).Outcome);
        await PostgresMaintenanceObservationTestSupport.AssertUnboundAsync(test, pid);
    }

    [PostgresFact]
    public async Task SettlementCancellationAfterAdmissionDoesNotHideTheActualNormalCommitAcknowledgement()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        var pid = await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(test.MaintenanceDatabase.DataSource);
        using var work = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var settlement = new CancellationTokenSource();
        ObjectCleanupFinishResult result;
        await using (var gate = await LockOriginAsync(test, claim))
        await using (var observer = await test.OpenBlockingObserverAsync(pid, gate.Backend))
        {
            var finishing = test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Missing, work.Token, settlement.Token);
            try
            {
                await observer.WaitForBlockedAsync(finishing);
                settlement.Cancel();
                await gate.ReleaseAsync();
                result = await finishing;
            }
            finally
            {
                await gate.ReleaseAsync();
                if (!finishing.IsCompleted) work.Cancel();
                try { await finishing; } catch (OperationCanceledException) { }
            }
        }
        Assert.Equal(ObjectCleanupFinishOutcome.Applied, result.Outcome);
        Assert.Equal(claim, result.Attempt!.Claim);
        Assert.Equal(ObjectCleanupDisposition.Missing, result.Attempt.Disposition);
        var retained = await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, claim.Generation);
        Assert.Equal(result.Attempt, retained);
        Assert.Equal(ObjectCleanupFinishOutcome.AlreadyApplied,
            (await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Missing)).Outcome);
        await PostgresMaintenanceObservationTestSupport.AssertUnboundAsync(test, pid);
    }

    [PostgresFact]
    public async Task ActualCommitAckLossWithExpiredSharedSettlementRefusesFreshRecoveryAndRetainsExactFacts()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        var actual = new NpgsqlConnectionStringBuilder(test.Case.MaintenanceConnectionString);
        var upstreamHost = actual.Host ?? throw new InvalidOperationException("The owned PostgreSQL fixture requires a host.");
        await using var relay = new RevisionReportCommitAckLossRelay(upstreamHost, actual.Port);
        await using var routedDatabase = new PostgresPrivateObjectMaintenanceDatabase(relay.Route(test.Case.MaintenanceConnectionString));
        var routed = new PostgresPrivateObjectMaintenanceStore(routedDatabase);
        using var work = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var settlement = new CancellationTokenSource();
        var finishing = routed.FinishAsync(claim, ObjectCleanupDisposition.Missing, work.Token, settlement.Token);
        try
        {
            await relay.CommitAcknowledgementWithheld.WaitAsync(work.Token);
            Assert.True(relay.FrontendCommitSeen);
            Assert.True(relay.BackendCommitCompleteSeen);
            Assert.True(relay.BackendIdleReadySeen);
            Assert.False(finishing.IsCompleted);
            // A separate actual connection proves commit while the original caller
            // has neither received COMMIT completion nor settled its connection.
            var committed = await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, claim.Generation, work.Token);
            Assert.Equal(claim, committed!.Claim);
            Assert.Equal(ObjectCleanupDisposition.Missing, committed.Disposition);
            Assert.NotNull(committed.FinishedAtUtc);
            var durable = await test.CleanupSnapshotAsync(claim.Retirement.RetirementId);
            settlement.Cancel();
            Assert.False(work.IsCancellationRequested);
            relay.DisconnectCaller();
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finishing);
            Assert.Equal(settlement.Token, canceled.CancellationToken);
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            // Only after the original call has settled may this caller resolve its
            // exact attempt. The canceled token did not invent a new five-second read.
            Assert.Equal(committed, await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, claim.Generation));
            Assert.Equal(durable, await test.CleanupSnapshotAsync(claim.Retirement.RetirementId));
            Assert.Equal(ObjectCleanupFinishOutcome.AlreadyApplied,
                (await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Missing)).Outcome);
        }
        finally
        {
            settlement.Cancel();
            relay.DisconnectCaller();
            work.Cancel();
            try { await finishing; } catch (OperationCanceledException) { }
        }
    }

    private static ObjectCleanupClaim CopyClaim(ObjectCleanupClaim original, Guid? attemptId = null,
        Guid? instance = null, Guid? token = null) => new(original.Retirement, attemptId ?? original.AttemptId,
            original.Generation, instance ?? original.ServerInstanceId, token ?? original.LeaseToken,
            original.StartedAtUtc, original.LeaseExpiresAtUtc);

    private static async Task<OriginGate> LockOriginAsync(PostgresPrivateObjectMaintenanceTestSupport test, ObjectCleanupClaim claim)
    {
        var connection = await test.OpenOwnerAsync();
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT pg_backend_pid() FROM weft.revision_ingestions
                WHERE organization_id=$1 AND project_id=$2 AND operation_id=$3 FOR UPDATE
                """, connection, transaction);
            command.Parameters.AddWithValue(test.Project.OrganizationId);
            command.Parameters.AddWithValue(test.Project.ProjectId);
            command.Parameters.AddWithValue(claim.Retirement.Origin.IngestionOperationId ?? throw new InvalidOperationException("Expected the owned native fixture origin."));
            var backend = (int)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected the owned origin lock."));
            return new(connection, transaction, backend);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class OriginGate(NpgsqlConnection connection, NpgsqlTransaction transaction, int backend) : IAsyncDisposable
    {
        private bool released;
        internal int Backend => backend;
        internal async Task ReleaseAsync()
        {
            if (released) return;
            await transaction.RollbackAsync();
            released = true;
        }
        public async ValueTask DisposeAsync()
        {
            try { await transaction.DisposeAsync(); }
            finally { await connection.DisposeAsync(); }
        }
    }
}
