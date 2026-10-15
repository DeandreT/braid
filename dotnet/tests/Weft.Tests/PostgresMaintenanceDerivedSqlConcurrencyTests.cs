using System.Runtime.Versioning;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// Actual restricted SQL, key waits and enabled guards. Source/report facts,
/// 25h clock movement and the Missing acknowledgement are synthetic metadata;
/// this test makes no native worker, filesystem or physical-deletion claim.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresMaintenanceDerivedSqlConcurrencyTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task RestrictedExpiredCompletionAndSelectionCannotResurrectTheCandidateInEitherOriginLockOrder()
    {
        foreach (var retirementFirst in new[] { false, true })
        {
            await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
            var original = await test.ReserveReportAsync();
            original = await test.SyntheticAgeReportAsync(original, TimeSpan.FromHours(25));
            var origins = await test.OriginSnapshotAsync();
            var writerPid = await AssertWriterUnboundAsync(test.DerivationDatabase);
            var maintenancePid = await CommandTestSupport.AssertIdleAsync(test.MaintenanceDatabase, test.Case.Name);
            var cleanup = await SerializeRefusalsAsync(test, original, retirementFirst);
            Assert.Equal(original.CandidateObjectId, cleanup.Retirement.Target.Object);
            Assert.Equal(CleanupTargetKind.FinalObject, cleanup.Retirement.Target.Kind);
            Assert.Equal(ObjectCleanupReason.ExpiredDerivedCandidate, cleanup.Retirement.Reason);
            Assert.Equal(original.DerivationId, cleanup.Retirement.Origin.DerivationId);
            Assert.Equal(origins, await test.OriginSnapshotAsync());
            Assert.Equal(new long[] { 1, 1, 0 }, await test.CountsAsync());

            // Explicitly modeled metadata ACK, not evidence of an actual missing
            // file or deletion. The real physical counterpart is a separate test.
            var acknowledged = await test.Maintenance.FinishAsync(cleanup, ObjectCleanupDisposition.Missing);
            Assert.Equal(ObjectCleanupFinishOutcome.Applied, acknowledged.Outcome);
            var acknowledgedAttempt = Assert.IsType<ObjectCleanupAttempt>(acknowledged.Attempt);
            Assert.Equal(cleanup, acknowledgedAttempt.Claim);
            Assert.Equal(ObjectCleanupDisposition.Missing, acknowledgedAttempt.Disposition);
            Assert.NotNull(acknowledgedAttempt.FinishedAtUtc);
            Assert.Equal(acknowledgedAttempt, await test.Maintenance.GetAttemptAsync(test.Project,
                cleanup.Retirement.RetirementId, cleanup.Generation));
            var completedRetirement = await test.CleanupSnapshotAsync(cleanup.Retirement.RetirementId);

            // Both bypasses are also denied after the original retirement is
            // completed. No new claim or usable result replaces that history.
            await using (var writer = await test.DerivationDatabase.BeginTransactionAsync(test.Project.OrganizationId))
            {
                await LockKeyAsync(writer, original);
                await AssertGuardRefusalsAsync(test, writer, original);
                await writer.CommitAsync();
            }
            Assert.Equal(completedRetirement, await test.CleanupSnapshotAsync(cleanup.Retirement.RetirementId));
            Assert.Equal(origins, await test.OriginSnapshotAsync());
            Assert.Equal(1L, await test.Case.ScalarAsync<long>(
                "SELECT count(*) FROM weft.private_object_retirements WHERE retirement_id=$1 AND completed_attempt_id=$2",
                cleanup.Retirement.RetirementId, cleanup.AttemptId));
            Assert.Equal(0L, await test.Case.ScalarAsync<long>(
                "SELECT count(*) FROM weft.revision_derivation_attempts WHERE derivation_id=$1 AND result_usable", original.DerivationId));
            Assert.True(await test.Case.ScalarAsync<bool>(
                "SELECT selected_derivation_id IS NULL FROM weft.revision_derivations WHERE cache_entry_id=$1", original.CacheEntryId));
            Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
            Assert.Equal(new long[] { 1, 1, 0 }, await test.CountsAsync());
            Assert.Equal(writerPid, await AssertWriterUnboundAsync(test.DerivationDatabase));
            await CommandTestSupport.AssertIdleAsync(test.MaintenanceDatabase, test.Case.Name, maintenancePid);
        }
    }

    private static async Task<ObjectCleanupClaim> SerializeRefusalsAsync(
        PostgresPrivateObjectMaintenanceTestSupport test, RevisionReportClaim original, bool retirementFirst)
    {
        await using var writer = await test.DerivationDatabase.BeginTransactionAsync(test.Project.OrganizationId);
        await using var retiring = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project);
        Task? pending = null;
        try
        {
            if (retirementFirst)
            {
                var cleanup = Assert.Single((await test.Maintenance.ClaimInTransactionAsync(retiring,
                    test.Project, Guid.NewGuid())).Claims);
                await using var observer = await test.OpenBlockingObserverAsync(writer.Connection.ProcessID,
                    retiring.Connection.ProcessID);
                // This actual wait belongs to the explicit key prelock, not to
                // the later UPDATEs. It preserves production key-before-attempt order.
                var waiting = LockKeyAsync(writer, original);
                pending = waiting;
                await observer.WaitForBlockedAsync(waiting);
                await retiring.CommitAsync();
                await waiting.WaitAsync(TimeSpan.FromSeconds(5));
                var immutableFence = await test.CleanupSnapshotAsync(cleanup.Retirement.RetirementId);
                await AssertGuardRefusalsAsync(test, writer, original);
                await writer.CommitAsync();
                Assert.Equal(immutableFence, await test.CleanupSnapshotAsync(cleanup.Retirement.RetirementId));
                return cleanup;
            }
            else
            {
                await LockKeyAsync(writer, original);
                await using var observer = await test.OpenBlockingObserverAsync(retiring.Connection.ProcessID,
                    writer.Connection.ProcessID);
                var waiting = test.Maintenance.ClaimInTransactionAsync(retiring, test.Project, Guid.NewGuid());
                pending = waiting;
                await observer.WaitForBlockedAsync(waiting);
                // Guard errors are confined to savepoints, retaining the prior
                // key lock until commit so the observed maintenance wait is real.
                await AssertGuardRefusalsAsync(test, writer, original);
                Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
                await writer.CommitAsync();
                var cleanup = Assert.Single((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Claims);
                await retiring.CommitAsync();
                return cleanup;
            }
        }
        finally
        {
            if (retirementFirst) await retiring.DisposeAsync(); else await writer.DisposeAsync();
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch when (pending.IsCompleted) { }
            }
            if (retirementFirst) await writer.DisposeAsync(); else await retiring.DisposeAsync();
        }
    }

    private static async Task LockKeyAsync(PostgresTransaction writer, RevisionReportClaim original)
    {
        await using var command = writer.CreateCommand("""
            SELECT cache_entry_id FROM weft.revision_derivations
            WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4 AND cache_entry_id=$5
            FOR UPDATE
            """);
        AddScope(command, original);
        Assert.Equal(original.CacheEntryId, await command.ExecuteScalarAsync());
    }

    private static async Task AssertGuardRefusalsAsync(PostgresPrivateObjectMaintenanceTestSupport test,
        PostgresTransaction writer, RevisionReportClaim original)
    {
        var origins = await test.OriginSnapshotAsync();
        await RefuseAsync(writer, original, """
            UPDATE weft.revision_derivation_attempts SET completion_kind='usable'
            WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4 AND cache_entry_id=$5
              AND derivation_id=$6 AND generation=$7
            """, "Report completion requires the live current unfinished claim.", attempt: true);
        Assert.Equal(origins, await test.OriginSnapshotAsync());
        await RefuseAsync(writer, original, """
            UPDATE weft.revision_derivations SET selected_derivation_id=$6
            WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4 AND cache_entry_id=$5
              AND current_generation=$7 AND current_attempt_id=$6
            """, "Selection requires completed matching usable metadata.", attempt: false);
        Assert.Equal(origins, await test.OriginSnapshotAsync());
        // The unchanged 0006 transition guards run before the z-retirement
        // triggers. These are real restricted-role refusals, not isolated proof
        // that an unreachable usable-plus-retired state hit the later fence query.
    }

    private static async Task RefuseAsync(PostgresTransaction writer, RevisionReportClaim original,
        string sql, string expectedMessage, bool attempt)
    {
        await using (var savepoint = writer.CreateCommand("SAVEPOINT denied_attachment"))
            await savepoint.ExecuteNonQueryAsync();
        try
        {
            await using var command = writer.CreateCommand(sql);
            AddScope(command, original);
            command.Parameters.AddWithValue(original.DerivationId);
            command.Parameters.AddWithValue(original.Generation);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("23514", error.SqlState);
            Assert.Equal(expectedMessage, error.MessageText);
            Assert.Contains(attempt ? "guard_revision_derivation_attempts_transition" : "guard_revision_derivations_transition",
                error.Where ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            await using (var rollback = writer.CreateCommand("ROLLBACK TO SAVEPOINT denied_attachment"))
                await rollback.ExecuteNonQueryAsync();
            await using (var release = writer.CreateCommand("RELEASE SAVEPOINT denied_attachment"))
                await release.ExecuteNonQueryAsync();
        }
    }

    private static void AddScope(NpgsqlCommand command, RevisionReportClaim original)
    {
        command.Parameters.AddWithValue(original.Key.Scope.OrganizationId);
        command.Parameters.AddWithValue(original.Key.Scope.ProjectId);
        command.Parameters.AddWithValue(original.Key.Scope.MappingId);
        command.Parameters.AddWithValue(original.Key.Scope.RevisionId);
        command.Parameters.AddWithValue(original.CacheEntryId);
    }

    private static async Task<int> AssertWriterUnboundAsync(PostgresRevisionDerivationDatabase database)
    {
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString).MaxPoolSize);
        await using var connection = await database.DataSource.OpenConnectionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await using var command = new NpgsqlCommand("""
            SELECT pg_backend_pid(),coalesce(current_setting('weft.organization_id',true),''),
              coalesce(current_setting('weft.project_id',true),'')
            """, connection);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        var pid = rows.GetInt32(0);
        Assert.Equal(string.Empty, rows.GetString(1));
        Assert.Equal(string.Empty, rows.GetString(2));
        Assert.False(await rows.ReadAsync());
        return pid;
    }
}
