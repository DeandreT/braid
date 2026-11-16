using Npgsql;
using Braid.Application;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Real dedicated pools and transactions over synthetic report metadata; no report storage or worker qualification.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRevisionDerivationTransactionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task BothDedicatedPoolsCommitAcrossOrganizationsAndReturnTheSameUnboundBackend()
    {
        foreach (var writable in new[] { false, true })
        {
            await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
            var pool = writable ? test.DerivationDatabase.DataSource : test.ReportDatabase.DataSource;
            var role = writable ? "braid_deriver" : "braid_report_reader";
            Func<Guid, CancellationToken, Task<PostgresTransaction>> begin = writable
                ? test.DerivationDatabase.BeginTransactionAsync : test.ReportDatabase.BeginTransactionAsync;
            var backend = await PostgresRevisionDerivationTestSupport.BackendAsync(pool);
            foreach (var (organization, projects) in new[] { (test.Seed.OrganizationA, 2L), (test.Seed.OrganizationB, 1L) })
            {
                await using var transaction = await begin(organization, CancellationToken.None);
                Assert.Equal(backend, await ScalarAsync<int>(transaction, "SELECT pg_backend_pid()"));
                Assert.Equal(role, await ScalarAsync<string>(transaction, "SELECT current_user"));
                Assert.Equal(organization.ToString("D"), await ScalarAsync<string>(transaction, "SHOW braid.organization_id"));
                Assert.Equal(writable ? "off" : "on", await ScalarAsync<string>(transaction, "SHOW transaction_read_only"));
                Assert.Equal("read committed", await ScalarAsync<string>(transaction, "SHOW transaction_isolation"));
                Assert.Equal("1s", await ScalarAsync<string>(transaction, "SHOW lock_timeout"));
                Assert.Equal("5s", await ScalarAsync<string>(transaction, "SHOW statement_timeout"));
                Assert.Equal(projects, await ScalarAsync<long>(transaction, "SELECT count(*) FROM braid.projects"));
                Assert.Equal(0L, await ScalarAsync<long>(transaction,
                    "SELECT count(*) FROM braid.projects WHERE organization_id<>$1", organization));
                await transaction.CommitAsync();
                Assert.Throws<InvalidOperationException>(() => transaction.CreateCommand("SELECT 1"));
            }
            await AssertUnboundAsync(pool, backend, role);
            Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
        }
    }

    [PostgresFact]
    public async Task ExplicitRollbackAndAbandonedScopesUndoClaimsAndReuseBothMaxOnePoolsAcrossOrganizations()
    {
        foreach (var writable in new[] { false, true })
        {
            await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
            var pool = writable ? test.DerivationDatabase.DataSource : test.ReportDatabase.DataSource;
            Func<Guid, CancellationToken, Task<PostgresTransaction>> begin = writable
                ? test.DerivationDatabase.BeginTransactionAsync : test.ReportDatabase.BeginTransactionAsync;
            var backend = await PostgresRevisionDerivationTestSupport.BackendAsync(pool);
            var source = await test.ImmutableSourceSnapshotAsync();
            foreach (var explicitRollback in new[] { true, false })
            {
                await using (var transaction = await begin(test.Seed.OrganizationA, CancellationToken.None))
                {
                    Assert.Equal(backend, await ScalarAsync<int>(transaction, "SELECT pg_backend_pid()"));
                    if (writable)
                    {
                        var result = await test.Store.TryClaimInTransactionAsync(transaction, test.Actor, test.Key, Guid.NewGuid());
                        Assert.Equal(RevisionReportStoreOutcome.Claimed, result.Outcome);
                        Assert.Equal(1L, await ScalarAsync<long>(transaction, "SELECT count(*) FROM braid.revision_derivation_attempts"));
                    }
                    else Assert.Equal(1L, await ScalarAsync<long>(transaction, "SELECT count(*) FROM braid.mapping_revisions"));
                    Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
                    if (explicitRollback) await transaction.RollbackAsync();
                }
                await AssertUnboundAsync(pool, backend, writable ? "braid_deriver" : "braid_report_reader");
                await using (var other = await begin(test.Seed.OrganizationB, CancellationToken.None))
                {
                    Assert.Equal(backend, await ScalarAsync<int>(other, "SELECT pg_backend_pid()"));
                    Assert.Equal(test.Seed.OrganizationB.ToString("D"), await ScalarAsync<string>(other, "SHOW braid.organization_id"));
                    Assert.Equal(0L, await ScalarAsync<long>(other, "SELECT count(*) FROM braid.revision_derivations"));
                    await other.RollbackAsync();
                }
                Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
            }
            Assert.Equal(source, await test.ImmutableSourceSnapshotAsync());
            Assert.NotNull(await test.Reader.GetSourceAsync(test.Actor, test.Revision));
            Assert.Equal(RevisionReportStoreOutcome.Claimed,
                (await test.Store.TryClaimAsync(test.Actor, test.Key, Guid.NewGuid())).Outcome);
            await AssertUnboundAsync(pool, backend, writable ? "braid_deriver" : "braid_report_reader");
        }
    }

    [PostgresFact]
    public async Task ObservedQueryCancellationAndActualSqlFailureReuseEachBackendWithoutMaskingErrorsAsMissingMetadata()
    {
        foreach (var writable in new[] { false, true })
        {
            await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
            var pool = writable ? test.DerivationDatabase.DataSource : test.ReportDatabase.DataSource;
            var observerConnectionString = writable ? test.Case.DerivationConnectionString : test.Case.ReportConnectionString;
            Func<Guid, CancellationToken, Task<PostgresTransaction>> begin = writable
                ? test.DerivationDatabase.BeginTransactionAsync : test.ReportDatabase.BeginTransactionAsync;
            var backend = await PostgresRevisionDerivationTestSupport.BackendAsync(pool);
            await using (var transaction = await begin(test.Seed.OrganizationA, CancellationToken.None))
            {
                using var cancellation = new CancellationTokenSource();
                await using var command = transaction.CreateCommand("SELECT pg_sleep(10)");
                var pending = command.ExecuteNonQueryAsync(cancellation.Token);
                try
                {
                    await WaitForActualSleepAsync(observerConnectionString, backend, pending);
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally
                {
                    cancellation.Cancel();
                    await DrainAsync(pending);
                }
            }
            await AssertUnboundAsync(pool, backend, writable ? "braid_deriver" : "braid_report_reader");
            await using (var failed = await begin(test.Seed.OrganizationB, CancellationToken.None))
            {
                Assert.Equal(backend, await ScalarAsync<int>(failed, "SELECT pg_backend_pid()"));
                await using var division = failed.CreateCommand("SELECT 1/0");
                var error = await Assert.ThrowsAsync<PostgresException>(() => division.ExecuteScalarAsync());
                Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
            }
            await AssertUnboundAsync(pool, backend, writable ? "braid_deriver" : "braid_report_reader");
            Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
            var source = Assert.IsType<AuthorizedRevisionReportSource>(await test.Reader.GetSourceAsync(test.Actor, test.Revision));
            Assert.Equal(test.Source, source.Source);
            Assert.Equal(RevisionReportState.NotDerived, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.State);
            Assert.Equal(RevisionReportStoreOutcome.Claimed,
                (await test.Store.TryClaimAsync(test.Actor, test.Key, Guid.NewGuid())).Outcome);
            await AssertUnboundAsync(pool, backend, writable ? "braid_deriver" : "braid_report_reader");
        }
    }

    [PostgresFact]
    public async Task PresetReboundAndChangedOrganizationContextsRefuseWithoutReturningEmptyMetadataAndThenRecover()
    {
        foreach (var writable in new[] { false, true })
        {
            await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
            var pool = writable ? test.DerivationDatabase.DataSource : test.ReportDatabase.DataSource;
            Func<Guid, CancellationToken, Task<PostgresTransaction>> begin = writable
                ? test.DerivationDatabase.BeginTransactionAsync : test.ReportDatabase.BeginTransactionAsync;
            var backend = await PostgresRevisionDerivationTestSupport.BackendAsync(pool);
            var preset = new NpgsqlConnectionStringBuilder(writable ? test.Case.DerivationConnectionString : test.Case.ReportConnectionString)
            { Options = $"-c braid.organization_id={test.Seed.OrganizationB:D}" };
            if (writable)
            {
                await using var unsafePool = new PostgresRevisionDerivationDatabase(preset.ConnectionString);
                await Assert.ThrowsAsync<InvalidOperationException>(() => unsafePool.BeginTransactionAsync(test.Seed.OrganizationA));
                await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresRevisionDerivationStore(unsafePool)
                    .TryClaimAsync(test.Actor, test.Key, Guid.NewGuid()));
            }
            else
            {
                await using var unsafePool = new PostgresRevisionReportDatabase(preset.ConnectionString);
                await Assert.ThrowsAsync<InvalidOperationException>(() => unsafePool.BeginTransactionAsync(test.Seed.OrganizationA));
                var reader = new PostgresRevisionReportMetadataReader(unsafePool);
                await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetSourceAsync(test.Actor, test.Revision));
                await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetCurrentAsync(test.Actor, test.Key));
            }
            await using (var transaction = await begin(test.Seed.OrganizationA, CancellationToken.None))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.BindOrganizationAsync(test.Seed.OrganizationA));
                await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.BindOrganizationAsync(test.Seed.OrganizationB));
                Assert.Equal(test.Seed.OrganizationA.ToString("D"), await ScalarAsync<string>(transaction, "SHOW braid.organization_id"));
                await ScalarAsync<string>(transaction, "SELECT set_config('braid.organization_id',$1,true)", test.Seed.OrganizationB.ToString("D"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RequireOrganizationAsync(test.Seed.OrganizationA, CancellationToken.None));
                if (writable)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => test.Store.TryClaimInTransactionAsync(
                        transaction, test.Actor, test.Key, Guid.NewGuid()));
                await transaction.RollbackAsync();
            }
            Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
            await AssertUnboundAsync(pool, backend, writable ? "braid_deriver" : "braid_report_reader");
            Assert.NotNull(await test.Reader.GetCurrentAsync(test.Actor, test.Key));
            Assert.Equal(RevisionReportStoreOutcome.Claimed,
                (await test.Store.TryClaimAsync(test.Actor, test.Key, Guid.NewGuid())).Outcome);
        }
    }

    [PostgresFact]
    public async Task CancellationWhileTheOnlyPoolConnectionIsHeldCannotReserveWorkAndTheNextOperationSucceeds()
    {
        foreach (var writable in new[] { false, true })
        {
            await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
            var pool = writable ? test.DerivationDatabase.DataSource : test.ReportDatabase.DataSource;
            Func<Guid, CancellationToken, Task<PostgresTransaction>> begin = writable
                ? test.DerivationDatabase.BeginTransactionAsync : test.ReportDatabase.BeginTransactionAsync;
            var backend = await PostgresRevisionDerivationTestSupport.BackendAsync(pool);
            using var cancellation = new CancellationTokenSource();
            await using (var held = await begin(test.Seed.OrganizationB, CancellationToken.None))
            {
                Task pending = writable
                    ? test.Store.TryClaimAsync(test.Actor, test.Key, Guid.NewGuid(), cancellation.Token)
                    : test.Reader.GetCurrentAsync(test.Actor, test.Key, cancellation.Token);
                try
                {
                    Assert.False(pending.IsCompleted);
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally
                {
                    cancellation.Cancel();
                    await DrainAsync(pending);
                }
                Assert.Equal(backend, await ScalarAsync<int>(held, "SELECT pg_backend_pid()"));
            }
            using (var preCanceled = new CancellationTokenSource())
            {
                preCanceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => begin(test.Seed.OrganizationA, preCanceled.Token));
                if (writable)
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Store.TryClaimAsync(
                        test.Actor, test.Key, Guid.NewGuid(), preCanceled.Token));
                else
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Reader.GetCurrentAsync(
                        test.Actor, test.Key, preCanceled.Token));
            }
            Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
            await AssertUnboundAsync(pool, backend, writable ? "braid_deriver" : "braid_report_reader");
            Assert.Equal(RevisionReportState.NotDerived, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.State);
            Assert.Equal(RevisionReportStoreOutcome.Claimed,
                (await test.Store.TryClaimAsync(test.Actor, test.Key, Guid.NewGuid())).Outcome);
            await AssertUnboundAsync(pool, backend, writable ? "braid_deriver" : "braid_report_reader");
        }
    }

    [PostgresFact]
    public async Task ActualKeyLockDeadlineRaisesPersistenceErrorAndLeavesTheSamePoolUsableWithoutAnotherAttempt()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        var before = await test.AttemptSnapshotAsync(claim.DerivationId);
        var backend = await PostgresRevisionDerivationTestSupport.BackendAsync(test.DerivationDatabase.DataSource);
        await using var owner = await PostgresRevisionDerivationTestSupport.OpenOwnerAsync(test.Case);
        await using var blocker = await owner.BeginTransactionAsync();
        await using (var keyLock = new NpgsqlCommand("""
            SELECT cache_entry_id FROM braid.revision_derivations
            WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4 AND cache_entry_id=$5 FOR UPDATE
            """, owner, blocker))
        {
            PostgresRevisionDerivationTestSupport.AddScope(keyLock, test.Revision);
            keyLock.Parameters.AddWithValue(claim.CacheEntryId);
            Assert.Equal(claim.CacheEntryId, await keyLock.ExecuteScalarAsync());
        }
        int ownerPid;
        await using (var command = new NpgsqlCommand("SELECT pg_backend_pid()", owner, blocker))
            ownerPid = (int)(await command.ExecuteScalarAsync())!;
        await using var observer = await PostgresRevisionDerivationTestSupport.OpenBlockingObserverAsync(test.Case, backend, ownerPid);
        var pending = test.Store.TryClaimAsync(test.Actor, test.Key, Guid.NewGuid());
        try
        {
            await observer.WaitForBlockedAsync(pending);
            var error = await Assert.ThrowsAsync<PostgresException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }
        finally
        {
            await blocker.DisposeAsync();
            await DrainAsync(pending);
        }
        Assert.Equal(before, await test.AttemptSnapshotAsync(claim.DerivationId));
        Assert.Equal(new long[] { 1, 1, 0, 0 }, await test.CountsAsync());
        await AssertUnboundAsync(test.DerivationDatabase.DataSource, backend, "braid_deriver");
        Assert.Equal(RevisionReportStoreOutcome.Pending, (await test.Store.TryClaimAsync(test.Actor, test.Key, Guid.NewGuid())).Outcome);
        Assert.Equal(RevisionReportStoreOutcome.Claimed, (await test.Store.TryClaimAsync(test.Actor,
            test.ProjectionKey(schema: "synthetic.recovery.schema.v1"), Guid.NewGuid())).Outcome);
        await AssertUnboundAsync(test.DerivationDatabase.DataSource, backend, "braid_deriver");
    }

    [PostgresFact]
    public async Task ReportReaderObservedRelationLockDeadlineRaisesAnErrorAndRecoversTheSameBackend()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var source = await test.ImmutableSourceSnapshotAsync();
        var backend = await PostgresRevisionDerivationTestSupport.BackendAsync(test.ReportDatabase.DataSource);
        await using var owner = await PostgresRevisionDerivationTestSupport.OpenOwnerAsync(test.Case);
        await using var blocker = await owner.BeginTransactionAsync();
        await using (var lockSource = new NpgsqlCommand("LOCK TABLE ONLY braid.mapping_revisions IN ACCESS EXCLUSIVE MODE", owner, blocker))
            await lockSource.ExecuteNonQueryAsync();
        int ownerPid;
        await using (var command = new NpgsqlCommand("SELECT pg_backend_pid()", owner, blocker))
            ownerPid = (int)(await command.ExecuteScalarAsync())!;
        await using var observer = await PostgresRevisionDerivationTestSupport.OpenBlockingObserverAsync(test.Case, backend, ownerPid);
        var pending = test.Reader.GetCurrentAsync(test.Actor, test.Key);
        try
        {
            await observer.WaitForBlockedAsync(pending);
            var error = await Assert.ThrowsAsync<PostgresException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }
        finally
        {
            await blocker.DisposeAsync();
            await DrainAsync(pending);
        }
        await AssertUnboundAsync(test.ReportDatabase.DataSource, backend, "braid_report_reader");
        Assert.Equal(test.Source, (await test.Reader.GetSourceAsync(test.Actor, test.Revision))!.Source);
        Assert.Equal(RevisionReportState.NotDerived, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.State);
        Assert.Equal(source, await test.ImmutableSourceSnapshotAsync());
        Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
        await AssertUnboundAsync(test.ReportDatabase.DataSource, backend, "braid_report_reader");
    }

    [PostgresFact]
    public async Task TypedPublicationFollowedBySqlFailureRollsBackAndNormalFinishRecoversTheSameBackend()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        var before = await test.AttemptSnapshotAsync(claim.DerivationId);
        var source = await test.ImmutableSourceSnapshotAsync();
        var backend = await PostgresRevisionDerivationTestSupport.BackendAsync(test.DerivationDatabase.DataSource);
        await using (var failed = await test.DerivationDatabase.BeginTransactionAsync(test.Seed.OrganizationA))
        {
            Assert.Equal(backend, await ScalarAsync<int>(failed, "SELECT pg_backend_pid()"));
            var held = await test.Store.FinishInTransactionAsync(failed, test.Actor, claim,
                PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim));
            Assert.Equal(RevisionReportStoreOutcome.Applied, held.Outcome);
            Assert.Equal(RevisionReportState.Pending, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.State);
            await using var division = failed.CreateCommand("SELECT 1/0");
            var error = await Assert.ThrowsAsync<PostgresException>(() => division.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        Assert.Equal(before, await test.AttemptSnapshotAsync(claim.DerivationId));
        Assert.Equal(new long[] { 1, 1, 0, 0 }, await test.CountsAsync());
        await AssertUnboundAsync(test.DerivationDatabase.DataSource, backend, "braid_deriver");
        Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.Actor, claim,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim))).Outcome);
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
        Assert.Equal(source, await test.ImmutableSourceSnapshotAsync());
        await AssertUnboundAsync(test.DerivationDatabase.DataSource, backend, "braid_deriver");
    }

    private static async Task<T> ScalarAsync<T>(PostgresTransaction transaction, string statement, params object[] values)
    {
        await using var command = transaction.CreateCommand(statement);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("A real transaction scalar was required."));
    }

    private static async Task AssertUnboundAsync(NpgsqlDataSource pool, int expectedBackend, string role)
    {
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(pool.ConnectionString).MaxPoolSize);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var connection = await pool.OpenConnectionAsync(deadline.Token);
        await using (var identity = new NpgsqlCommand("""
            SELECT pg_backend_pid(),NULLIF(current_setting('braid.organization_id',true),''),
                current_user,session_user,txid_current_if_assigned()
            """, connection))
        await using (var reader = await identity.ExecuteReaderAsync(deadline.Token))
        {
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.Equal(expectedBackend, reader.GetInt32(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(role, reader.GetString(2));
            Assert.Equal(role, reader.GetString(3));
            Assert.True(reader.IsDBNull(4));
        }
        foreach (var (setting, expected) in new[] { ("transaction_read_only", "off"),
            ("transaction_isolation", "read committed"), ("lock_timeout", "0"), ("statement_timeout", "0") })
        {
            await using var command = new NpgsqlCommand($"SHOW {setting}", connection);
            Assert.Equal(expected, await command.ExecuteScalarAsync(deadline.Token));
        }
        foreach (var relation in new[] { "projects", "mappings", "mapping_revisions", "revision_derivations", "revision_derivation_attempts" })
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM braid.{relation}", connection);
            Assert.Equal(0L, await command.ExecuteScalarAsync(deadline.Token));
        }
        await using var locks = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_locks held
            LEFT JOIN pg_catalog.pg_class relation ON relation.oid=held.relation
            LEFT JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
            WHERE held.pid=pg_backend_pid() AND
                (held.locktype IN ('transactionid','tuple','advisory') OR namespace.nspname='braid')
            """, connection);
        Assert.Equal(0L, await locks.ExecuteScalarAsync(deadline.Token));
        await using var fresh = await connection.BeginTransactionAsync(deadline.Token);
        await fresh.RollbackAsync(deadline.Token);
    }

    private static async Task WaitForActualSleepAsync(string connectionString, int backend, Task operation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        // The same restricted login may observe its own backend in pg_stat_activity;
        // no pg_monitor grant or superuser access is added to a product role.
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
        { Pooling = false }.ConnectionString);
        await observer.OpenAsync(deadline.Token);
        while (true)
        {
            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException("The actual backend sleep finished before cancellation observation.");
            }
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity
                    WHERE pid=$1 AND datname=current_database() AND state='active' AND wait_event='PgSleep')
                """, observer);
            command.Parameters.AddWithValue(backend);
            if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }
    }

    private static async Task DrainAsync(Task pending)
    {
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) when (pending.IsCanceled || pending.IsFaulted) { }
    }
}
