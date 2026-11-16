using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual PostgreSQL observations. Bulk metadata, clock movement and completion dispositions are authored, not physical cleanup.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresMaintenanceStatusObservationTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ZeroNinetyNineHundredAndHundredOneHeadersKeepExactBoundariesSeekAndDatabaseTime()
    {
        foreach (var count in new[] { 0, 99, 100, 101 })
        {
            await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
            var ids = await PostgresMaintenanceObservationTestSupport.SeedUnattemptedAsync(test, count);
            if (count > 1)
            {
                // Network UUID order is1 then256, opposite their mixed-endian byte-array order.
                Assert.True(ids[0].ToByteArray().AsSpan().SequenceCompareTo(ids[1].ToByteArray()) > 0);
            }
            var before = await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test);
            var lower = await test.Case.ScalarAsync<DateTime>("SELECT clock_timestamp()");
            var page = await test.Maintenance.GetStatusesAsync(test.Project);
            var upper = await test.Case.ScalarAsync<DateTime>("SELECT clock_timestamp()");
            Assert.InRange(page.ObservedAtUtc.UtcDateTime, lower, upper);
            Assert.Equal(Math.Min(count, 100), page.ExaminedCount);
            Assert.Equal(ids.Take(100), page.Headers.Select(h => h.RetirementId));
            Assert.All(page.Headers, header =>
            {
                Assert.Equal(page.ObservedAtUtc, header.ObservedAtUtc);
                Assert.Equal(ObjectCleanupObservedState.Unattempted, header.State);
                Assert.Null(header.CurrentAttempt);
                Assert.Null(header.CompletedAttemptId);
            });
            Assert.Equal(count < 100, page.IsComplete);
            if (count < 100) Assert.Null(page.Next);
            else
            {
                Assert.NotNull(page.Next);
                Assert.Equal(ids[99], page.Next.AfterRetirementId);
                var remaining = await test.Maintenance.GetStatusesAsync(test.Project, page.Next);
                Assert.True(remaining.IsComplete);
                Assert.Null(remaining.Next);
                Assert.Equal(ids.Skip(100), remaining.Headers.Select(h => h.RetirementId));
                Assert.All(remaining.Headers, h => Assert.True(CatalogReadGuard.CompareIds(h.RetirementId, page.Next.AfterRetirementId) > 0));
            }
            Assert.Equal(before, await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test));
        }
    }

    [PostgresFact]
    public async Task AuthoredFlatLifecycleHeadersUseTheSingleDatabaseClockAndActualSuccessPointer()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var ids = await PostgresMaintenanceObservationTestSupport.SeedUnattemptedAsync(test, 7);
        await SetAuthoredStateAsync(test, ids[1], 1, TimeSpan.Zero, null);
        await SetAuthoredStateAsync(test, ids[2], 1, TimeSpan.FromSeconds(5), ObjectCleanupDisposition.StorageIo);
        await SetAuthoredStateAsync(test, ids[3], 1, TimeSpan.FromSeconds(100), ObjectCleanupDisposition.UnsafeStorage);
        await SetAuthoredStateAsync(test, ids[4], 1, TimeSpan.FromSeconds(5), ObjectCleanupDisposition.Missing);
        await SetAuthoredStateAsync(test, ids[5], 8, TimeSpan.FromSeconds(100), null);
        await SetAuthoredStateAsync(test, ids[6], 8, TimeSpan.Zero, null);
        var before = await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test);
        var page = await test.Maintenance.GetStatusesAsync(test.Project);
        Assert.Equal(new[] { ObjectCleanupObservedState.Unattempted, ObjectCleanupObservedState.Leased,
            ObjectCleanupObservedState.RetryWaiting, ObjectCleanupObservedState.RetryDue,
            ObjectCleanupObservedState.Completed, ObjectCleanupObservedState.Exhausted, ObjectCleanupObservedState.Leased },
            page.Headers.Select(h => h.State));
        Assert.Equal(page.Headers[4].CurrentAttempt!.AttemptId, page.Headers[4].CompletedAttemptId);
        Assert.All(page.Headers.Where(h => h.State != ObjectCleanupObservedState.Completed), h => Assert.Null(h.CompletedAttemptId));
        Assert.True(page.Headers[2].NextAttemptAtUtc > page.ObservedAtUtc);
        Assert.True(page.Headers[3].NextAttemptAtUtc <= page.ObservedAtUtc);
        Assert.Null(page.Headers[5].NextAttemptAtUtc);
        Assert.Null(page.Headers[6].NextAttemptAtUtc);
        Assert.Equal(before, await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test));
    }

    [PostgresFact]
    public async Task EightRealRetainedClaimGenerationsBecomeObservedExhaustionOnlyAfterAuthoredExpiry()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var native = await test.CreateRejectedNativeAsync();
        var claim = await test.ClaimSingleAsync(native.NativeObject.Id);
        for (var generation = 1; generation < 8; generation++)
        {
            Assert.Equal(generation, claim.Generation);
            // Real restricted transitions; the disposition and elapsed-clock shift
            // are authored and make no claim about filesystem failure/deletion.
            Assert.Equal(ObjectCleanupFinishOutcome.Applied,
                (await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.UnsafeStorage)).Outcome);
            claim = await test.SyntheticAgeCleanupAsync(claim, TimeSpan.FromSeconds(31));
            claim = await test.ClaimSingleAsync(native.NativeObject.Id);
        }
        Assert.Equal(8, claim.Generation);
        var live = Assert.Single((await test.Maintenance.GetStatusesAsync(test.Project)).Headers);
        Assert.Equal(ObjectCleanupObservedState.Leased, live.State);
        await test.SyntheticAgeCleanupAsync(claim, TimeSpan.FromSeconds(61));
        var before = await test.CleanupSnapshotAsync(claim.Retirement.RetirementId);
        var expired = Assert.Single((await test.Maintenance.GetStatusesAsync(test.Project)).Headers);
        Assert.Equal(ObjectCleanupObservedState.Exhausted, expired.State);
        Assert.Equal(claim.AttemptId, expired.CurrentAttempt!.AttemptId);
        Assert.Null(expired.CurrentAttempt.FinishedAtUtc);
        Assert.Equal(8L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.private_object_cleanup_attempts WHERE retirement_id=$1", claim.Retirement.RetirementId));
        Assert.Equal(before, await test.CleanupSnapshotAsync(claim.Retirement.RetirementId));
        Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
    }

    [PostgresFact]
    public async Task ScopedReadOnlyObservationsPreserveRowsRightsAndUnboundSameBackendAcrossProjects()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await PostgresMaintenanceObservationTestSupport.SeedUnattemptedAsync(test, 1);
        var before = await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test);
        var origins = await test.OriginSnapshotAsync();
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(test.Case.MaintenanceConnectionString).MaxPoolSize);
        var pid = await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(test.MaintenanceDatabase.DataSource);
        Assert.Single((await test.Maintenance.GetStatusesAsync(test.Project)).Headers);
        var foreign = new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB);
        Assert.Empty((await test.Maintenance.GetStatusesAsync(foreign)).Headers);
        await Assert.ThrowsAsync<ArgumentException>(() => test.Maintenance.GetStatusesAsync(test.Project,
            new ObjectCleanupStatusCursor(foreign, Guid.NewGuid())));
        await PostgresMaintenanceObservationTestSupport.AssertUnboundAsync(test, pid);
        await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Case.MaintenanceConnectionString) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM braid.private_object_retirements", connection);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            await using var transaction = await connection.BeginTransactionAsync();
            await PostgresPrivateObjectMaintenanceTestSupport.ExecuteAsync(connection, transaction,
                "SELECT set_config('braid.organization_id',$1,true),set_config('braid.project_id',$2,true)",
                [test.Project.OrganizationId.ToString("D"), test.Seed.ProjectB.ToString("D")]);
            await using var wrong = new NpgsqlCommand("SELECT count(*) FROM braid.private_object_retirements", connection, transaction);
            Assert.Equal(0L, await wrong.ExecuteScalarAsync());
            await transaction.RollbackAsync();
            await using var denied = new NpgsqlCommand("DELETE FROM braid.private_object_retirements", connection);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync())).SqlState);
            await using var oldSource = new NpgsqlCommand("SELECT operation_id FROM braid.revision_ingestions LIMIT 1", connection);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => oldSource.ExecuteScalarAsync())).SqlState);
        }
        await using (var wrongDatabase = new PostgresPrivateObjectMaintenanceDatabase(test.Case.RuntimeConnectionString))
        {
            var wrongStore = new PostgresPrivateObjectMaintenanceStore(wrongDatabase);
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrongStore.GetStatusesAsync(test.Project));
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrongStore.MatchAsync(new(test.Project, [])));
        }
        Assert.Equal(before, await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test));
        Assert.Equal(origins, await test.OriginSnapshotAsync());
    }

    [PostgresFact]
    public async Task ObservedLockCancellationAndSqlFailureSettleBeforeSamePoolNextProjectRead()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await PostgresMaintenanceObservationTestSupport.SeedUnattemptedAsync(test, 1);
        var pid = await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(test.MaintenanceDatabase.DataSource);
        await using (var owner = await test.OpenOwnerAsync())
        await using (var held = await owner.BeginTransactionAsync())
        {
            await PostgresPrivateObjectMaintenanceTestSupport.ExecuteAsync(owner, held,
                "LOCK TABLE braid.private_object_retirements IN ACCESS EXCLUSIVE MODE");
            await using var getPid = new NpgsqlCommand("SELECT pg_backend_pid()", owner, held);
            var ownerPid = (int)(await getPid.ExecuteScalarAsync())!;
            using var cancel = new CancellationTokenSource();
            await using var observer = await test.OpenBlockingObserverAsync(pid, ownerPid);
            var reading = test.Maintenance.GetStatusesAsync(test.Project, cancellationToken: cancel.Token);
            await observer.WaitForBlockedAsync(reading);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            await held.RollbackAsync();
        }
        await using (var failed = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project))
        {
            await using var command = failed.CreateCommand("SELECT 1/0");
            Assert.Equal(PostgresErrorCodes.DivisionByZero, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
        }
        Assert.Empty((await test.Maintenance.GetStatusesAsync(new(test.Seed.OrganizationB, test.Seed.ProjectB))).Headers);
        await PostgresMaintenanceObservationTestSupport.AssertUnboundAsync(test, pid);
        Assert.Single((await test.Maintenance.GetStatusesAsync(test.Project)).Headers);
    }

    private static Task SetAuthoredStateAsync(PostgresPrivateObjectMaintenanceTestSupport test, Guid id,
        int generation, TimeSpan age, ObjectCleanupDisposition? completion) => test.OwnerEditAsync([
            ("private_object_retirements", "private_object_retirements_transition_guard"),
            ("private_object_cleanup_attempts", "private_object_cleanup_attempts_transition_guard")],
        new("UPDATE braid.private_object_retirements SET retired_at=clock_timestamp()-interval '30 minutes' WHERE retirement_id=$1", [id], 1),
        new("""
            WITH observed AS MATERIALIZED (SELECT clock_timestamp() AS at)
            INSERT INTO braid.private_object_cleanup_attempts
              (organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id,generation,
               attempt_id,instance_id,lease_token,started_at,lease_expires_at,finished_at,disposition,success)
            SELECT r.organization_id,r.project_id,r.mapping_id,r.revision_id,r.object_class,r.object_id,r.retirement_id,g,
              gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),t.at,t.at+interval '60 seconds',
              CASE WHEN $4::text='' OR g<>$2 THEN NULL ELSE t.at+interval '1 second' END,
              CASE WHEN g=$2 THEN NULLIF($4::text,'') ELSE NULL END,
              g=$2 AND coalesce($4::text IN ('deleted','missing'),false)
            FROM braid.private_object_retirements r CROSS JOIN generate_series(1,$2) g CROSS JOIN observed o
            CROSS JOIN LATERAL (SELECT o.at-$3::interval-interval '90 seconds'*($2-g) AS at) t
            WHERE r.retirement_id=$1
            """, [id, generation, age, completion is null ? "" : completion.Value switch
            { ObjectCleanupDisposition.Missing => "missing", ObjectCleanupDisposition.StorageIo => "storage_io", _ => "unsafe_storage" }], generation),
        new("""
            UPDATE braid.private_object_retirements r SET current_generation=a.generation,current_attempt_id=a.attempt_id,
              completed_generation=CASE WHEN a.success THEN a.generation END,completed_attempt_id=CASE WHEN a.success THEN a.attempt_id END
            FROM braid.private_object_cleanup_attempts a WHERE r.retirement_id=$1 AND a.retirement_id=r.retirement_id AND a.generation=$2
            """, [id, generation], 1));
}
