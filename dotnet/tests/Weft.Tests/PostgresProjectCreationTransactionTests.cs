using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresProjectCreationTransactionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ObservedSameKeyLoserReplaysTheCommittedWinnerWithoutOrphanAttempts()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var contenderDatabase = new PostgresProjectCreationDatabase(test.Case.ProjectCreationConnectionString);
        var contender = new PostgresProjectCreator(contenderDatabase);
        var winnerPid = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        var contenderPid = await ProjectCreationTestSupport.BackendAsync(contenderDatabase.DataSource);
        Assert.NotEqual(winnerPid, contenderPid);
        var key = Guid.NewGuid();
        var metadata = Metadata("Same-key winner");
        await using var winner = await test.Database.BeginTransactionAsync(test.OrganizationId);
        var original = await test.Creator.CreateInTransactionAsync(winner, test.Actor, test.OrganizationId, key, metadata);
        Assert.Equal(ProjectCreationOutcome.Created, original.Outcome);
        Assert.NotNull(original.Receipt);
        Assert.Equal(new long[] { 3, 3, 0 }, await test.CountsAsync());
        await using var observer = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case, contenderPid, winnerPid);
        var pending = contender.CreateAsync(test.Actor, test.OrganizationId, key, metadata);
        try
        {
            await observer.WaitForBlockedAsync(pending);
            await winner.CommitAsync();
            var replay = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProjectCreationOutcome.Replayed, replay.Outcome);
            Assert.Equal(original.Receipt, replay.Receipt);
        }
        finally
        {
            await winner.DisposeAsync();
            await DrainAsync(pending);
        }
        Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
        await AssertUnboundAsync(test.Database, winnerPid);
        await AssertUnboundAsync(contenderDatabase, contenderPid);
    }

    [PostgresFact]
    public async Task ObservedSameKeyLoserCreatesAfterTheUncommittedWinnerRollsBack()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var contenderDatabase = new PostgresProjectCreationDatabase(test.Case.ProjectCreationConnectionString);
        var contender = new PostgresProjectCreator(contenderDatabase);
        var winnerPid = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        var contenderPid = await ProjectCreationTestSupport.BackendAsync(contenderDatabase.DataSource);
        var key = Guid.NewGuid();
        var metadata = Metadata("Rolled-back candidate");
        await using var winner = await test.Database.BeginTransactionAsync(test.OrganizationId);
        var abandoned = await test.Creator.CreateInTransactionAsync(winner, test.Actor, test.OrganizationId, key, metadata);
        Assert.Equal(ProjectCreationOutcome.Created, abandoned.Outcome);
        await using var observer = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case, contenderPid, winnerPid);
        var pending = contender.CreateAsync(test.Actor, test.OrganizationId, key, metadata);
        ProjectCreationReceipt created;
        try
        {
            await observer.WaitForBlockedAsync(pending);
            await winner.RollbackAsync();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProjectCreationOutcome.Created, result.Outcome);
            created = Assert.IsType<ProjectCreationReceipt>(result.Receipt);
            Assert.NotEqual(abandoned.Receipt!.Project, created.Project);
        }
        finally
        {
            await winner.DisposeAsync();
            await DrainAsync(pending);
        }
        Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
        Assert.Equal(0L, await test.Case.ScalarAsync<long>(
            "SELECT count(*) FROM weft.projects WHERE organization_id=$1 AND id=$2",
            test.OrganizationId, abandoned.Receipt!.Project.ProjectId));
        var replay = await test.CreateAsync(key, metadata);
        Assert.Equal(ProjectCreationOutcome.Replayed, replay.Outcome);
        Assert.Equal(created, replay.Receipt);
        await AssertUnboundAsync(test.Database, winnerPid);
        await AssertUnboundAsync(contenderDatabase, contenderPid);
    }

    [PostgresFact]
    public async Task ObservedConflictingAndCrossActorSameKeyLosersReturnNoWinnerFacts()
    {
        foreach (var anotherActor in new[] { false, true })
        {
            await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
            await using var contenderDatabase = new PostgresProjectCreationDatabase(test.Case.ProjectCreationConnectionString);
            var contender = new PostgresProjectCreator(contenderDatabase);
            var actor = anotherActor ? new ActorContext(new PrincipalId(test.Seed.Admitted)) : test.Actor;
            if (anotherActor)
                await test.SetCapabilityAsync(true, test.Seed.Admitted, test.OrganizationId);
            var winnerPid = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
            var contenderPid = await ProjectCreationTestSupport.BackendAsync(contenderDatabase.DataSource);
            var key = Guid.NewGuid();
            var metadata = Metadata("Private winning intent");
            await using var winner = await test.Database.BeginTransactionAsync(test.OrganizationId);
            var original = await test.Creator.CreateInTransactionAsync(winner, test.Actor, test.OrganizationId, key, metadata);
            Assert.Equal(ProjectCreationOutcome.Created, original.Outcome);
            await using var observer = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case, contenderPid, winnerPid);
            var pending = contender.CreateAsync(actor, test.OrganizationId, key,
                anotherActor ? metadata : Metadata("Different exact intent"));
            try
            {
                await observer.WaitForBlockedAsync(pending);
                await winner.CommitAsync();
                var conflict = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ProjectCreationOutcome.IdempotencyConflict, conflict.Outcome);
                Assert.Null(conflict.Receipt);
            }
            finally
            {
                await winner.DisposeAsync();
                await DrainAsync(pending);
            }
            Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
            Assert.Equal(original.Receipt, (await test.CreateAsync(key, metadata)).Receipt);
            await AssertUnboundAsync(contenderDatabase, contenderPid);
        }
    }

    [PostgresFact]
    public async Task DistinctKeysForTheSameActorAndOrganizationCommitBeforeTheFirstTransactionReleases()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var otherDatabase = new PostgresProjectCreationDatabase(test.Case.ProjectCreationConnectionString);
        var other = new PostgresProjectCreator(otherDatabase);
        var firstPid = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        var otherPid = await ProjectCreationTestSupport.BackendAsync(otherDatabase.DataSource);
        Assert.NotEqual(firstPid, otherPid);
        await using (var first = await test.Database.BeginTransactionAsync(test.OrganizationId))
        {
            var held = await test.Creator.CreateInTransactionAsync(first, test.Actor, test.OrganizationId,
                Guid.NewGuid(), Metadata("Independent same-name project"));
            Assert.Equal(ProjectCreationOutcome.Created, held.Outcome);
            var independent = await other.CreateAsync(test.Actor, test.OrganizationId, Guid.NewGuid(),
                Metadata("Independent same-name project")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProjectCreationOutcome.Created, independent.Outcome);
            Assert.NotEqual(held.Receipt!.Project, independent.Receipt!.Project);
            Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
            await first.CommitAsync();
        }
        Assert.Equal(new long[] { 5, 5, 2 }, await test.CountsAsync());
        await AssertUnboundAsync(test.Database, firstPid);
        await AssertUnboundAsync(otherDatabase, otherPid);
    }

    [PostgresFact]
    public async Task EachAuthorityRevocationCommittedBeforeTheObservedCreatorLockDeniesWithoutWrites()
    {
        foreach (var authority in Enum.GetValues<Authority>())
        {
            await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
            var creatorPid = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
            await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case);
            var revokerPid = await BackendAsync(owner);
            await using var revocation = await owner.BeginTransactionAsync();
            await using (var update = RevocationCommand(owner, revocation, authority, test.OrganizationId, test.Actor.PrincipalId.Value))
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            using var cancellation = new CancellationTokenSource();
            await using var observer = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case, creatorPid, revokerPid);
            var pending = test.CreateAsync(cancellationToken: cancellation.Token);
            try
            {
                await observer.WaitForBlockedAsync(pending);
                await revocation.CommitAsync();
                var denied = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(Denial(authority), denied.Outcome);
                Assert.Null(denied.Receipt);
            }
            finally
            {
                cancellation.Cancel();
                await revocation.DisposeAsync();
                await DrainAsync(pending);
            }
            Assert.Equal(new long[] { 3, 3, 0 }, await test.CountsAsync());
            await AssertUnboundAsync(test.Database, creatorPid);
        }
    }

    [PostgresFact]
    public async Task EachObservedRevokerWaitsForAcceptedCreationCommitAndThenRevokesReplay()
    {
        foreach (var authority in Enum.GetValues<Authority>())
        {
            await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
            var creatorPid = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
            var key = Guid.NewGuid();
            var metadata = Metadata("Creation before revocation");
            await using var creation = await test.Database.BeginTransactionAsync(test.OrganizationId);
            var created = await test.Creator.CreateInTransactionAsync(creation, test.Actor, test.OrganizationId, key, metadata);
            Assert.Equal(ProjectCreationOutcome.Created, created.Outcome);
            await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case);
            var revokerPid = await BackendAsync(owner);
            await using var revocation = await owner.BeginTransactionAsync();
            await using var update = RevocationCommand(owner, revocation, authority, test.OrganizationId, test.Actor.PrincipalId.Value);
            using var cancellation = new CancellationTokenSource();
            await using var observer = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case, revokerPid, creatorPid);
            var pending = update.ExecuteNonQueryAsync(cancellation.Token);
            try
            {
                await observer.WaitForBlockedAsync(pending);
                await creation.CommitAsync();
                Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                await revocation.CommitAsync();
            }
            finally
            {
                cancellation.Cancel();
                await creation.DisposeAsync();
                await DrainAsync(pending);
                await revocation.DisposeAsync();
            }
            Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
            var denied = await test.CreateAsync(key, metadata);
            Assert.Equal(Denial(authority), denied.Outcome);
            Assert.Null(denied.Receipt);
            Assert.Equal(created.Receipt!.Project.ProjectId, await test.Case.ScalarAsync<Guid>(
                "SELECT project_id FROM weft.project_creations WHERE organization_id=$1 AND idempotency_key=$2",
                test.OrganizationId, key));
            await AssertUnboundAsync(test.Database, creatorPid);
        }
    }

    [PostgresFact]
    public async Task CancellationAtTheObservedLateFactInsertRollsBackAllThreeWritesAndRecoversTheSamePool()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var creatorPid = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        var key = Guid.NewGuid();
        var metadata = Metadata("Canceled after project and first member");
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case);
        var blockerPid = await BackendAsync(owner);
        await using var blocker = await owner.BeginTransactionAsync();
        await LockFactsAsync(owner, blocker);
        using var cancellation = new CancellationTokenSource();
        await using var observer = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case, creatorPid, blockerPid);
        var pending = test.CreateAsync(key, metadata, cancellation.Token);
        try
        {
            await observer.WaitForBlockedAsync(pending);
            await AssertLateFactInsertLocksAsync(test.Case, creatorPid);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancellation.Cancel();
            await blocker.DisposeAsync();
            await DrainAsync(pending);
        }
        Assert.Equal(new long[] { 3, 3, 0 }, await test.CountsAsync());
        await AssertUnboundAsync(test.Database, creatorPid);
        var created = await test.CreateAsync(key, metadata);
        Assert.Equal(ProjectCreationOutcome.Created, created.Outcome);
        Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
        await AssertUnboundAsync(test.Database, creatorPid);
    }

    [PostgresFact]
    public async Task UnchangedOneSecondLateFactInsertTimeoutIsAnErrorWithNoOrphansAndSamePoolRecovery()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var creatorPid = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        var key = Guid.NewGuid();
        var metadata = Metadata("Timed-out late fact insert");
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case);
        var blockerPid = await BackendAsync(owner);
        await using var blocker = await owner.BeginTransactionAsync();
        await LockFactsAsync(owner, blocker);
        await using var observer = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case, creatorPid, blockerPid);
        var pending = test.CreateAsync(key, metadata);
        try
        {
            await observer.WaitForBlockedAsync(pending);
            await AssertLateFactInsertLocksAsync(test.Case, creatorPid);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Project creation persistence failed.", error.Message);
        }
        finally
        {
            await blocker.DisposeAsync();
            await DrainAsync(pending);
        }
        Assert.Equal(new long[] { 3, 3, 0 }, await test.CountsAsync());
        await AssertUnboundAsync(test.Database, creatorPid);
        Assert.Equal(ProjectCreationOutcome.Created, (await test.CreateAsync(key, metadata)).Outcome);
        Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
        await AssertUnboundAsync(test.Database, creatorPid);
    }

    [PostgresFact]
    public async Task SameKeyWaitCancellationReleasesAuthorityLocksAndTheOnlyContenderConnection()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var contenderDatabase = new PostgresProjectCreationDatabase(test.Case.ProjectCreationConnectionString);
        var contender = new PostgresProjectCreator(contenderDatabase);
        var winnerPid = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        var contenderPid = await ProjectCreationTestSupport.BackendAsync(contenderDatabase.DataSource);
        var key = Guid.NewGuid();
        var metadata = Metadata("Canceled same-key waiter");
        await using var winner = await test.Database.BeginTransactionAsync(test.OrganizationId);
        var original = await test.Creator.CreateInTransactionAsync(winner, test.Actor, test.OrganizationId, key, metadata);
        using var cancellation = new CancellationTokenSource();
        await using var observer = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case, contenderPid, winnerPid);
        var pending = contender.CreateAsync(test.Actor, test.OrganizationId, key, metadata, cancellation.Token);
        try
        {
            await observer.WaitForBlockedAsync(pending);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await AssertUnboundAsync(contenderDatabase, contenderPid);
            await winner.CommitAsync();
        }
        finally
        {
            cancellation.Cancel();
            await winner.DisposeAsync();
            await DrainAsync(pending);
        }
        Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
        var replay = await contender.CreateAsync(test.Actor, test.OrganizationId, key, metadata);
        Assert.Equal(ProjectCreationOutcome.Replayed, replay.Outcome);
        Assert.Equal(original.Receipt, replay.Receipt);
        await AssertUnboundAsync(contenderDatabase, contenderPid);
    }

    [PostgresFact]
    public async Task CommitRollbackAndIncompleteDisposalReuseOneBackendAcrossOrganizationsWithoutResidualContext()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await test.SetCapabilityAsync(true, test.Actor.PrincipalId.Value, test.Seed.OrganizationB);
        var backend = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        var committedA = await test.CreateAsync();
        Assert.Equal(ProjectCreationOutcome.Created, committedA.Outcome);
        await AssertUnboundAsync(test.Database, backend);
        await using (var rollback = await test.Database.BeginTransactionAsync(test.OrganizationId))
        {
            Assert.Equal(backend, await ScalarAsync<int>(rollback, "SELECT pg_backend_pid()"));
            var candidate = await test.Creator.CreateInTransactionAsync(rollback, test.Actor, test.OrganizationId,
                Guid.NewGuid(), Metadata("Explicit rollback"));
            Assert.Equal(ProjectCreationOutcome.Created, candidate.Outcome);
            await rollback.RollbackAsync();
        }
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
        await using (var abandoned = await test.Database.BeginTransactionAsync(test.Seed.OrganizationB))
        {
            Assert.Equal(backend, await ScalarAsync<int>(abandoned, "SELECT pg_backend_pid()"));
            var candidate = await test.Creator.CreateInTransactionAsync(abandoned, test.Actor, test.Seed.OrganizationB,
                Guid.NewGuid(), Metadata("Incomplete organization B transaction"));
            Assert.Equal(ProjectCreationOutcome.Created, candidate.Outcome);
        }
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
        var committedB = await test.Creator.CreateAsync(test.Actor, test.Seed.OrganizationB,
            Guid.NewGuid(), Metadata("Committed organization B"));
        Assert.Equal(ProjectCreationOutcome.Created, committedB.Outcome);
        Assert.Equal(test.Seed.OrganizationB, committedB.Receipt!.Project.OrganizationId);
        Assert.Equal(new long[] { 5, 5, 2 }, await test.CountsAsync());
        await AssertUnboundAsync(test.Database, backend);
    }

    [PostgresFact]
    public async Task QueueCancellationPreCanceledCallsAndSqlFailureReleaseTheOnlyConnectionWithoutSuccessOrDenial()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var backend = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        await using (var held = await test.Database.BeginTransactionAsync(test.OrganizationId))
        {
            Assert.Equal(backend, await ScalarAsync<int>(held, "SELECT pg_backend_pid()"));
            using var queuedCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.CreateAsync(cancellationToken: queuedCancellation.Token));
            Assert.Equal(backend, await ScalarAsync<int>(held, "SELECT pg_backend_pid()"));
        }
        await AssertUnboundAsync(test.Database, backend);
        using (var preCanceled = new CancellationTokenSource())
        {
            preCanceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.CreateAsync(cancellationToken: preCanceled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Database.BeginTransactionAsync(
                test.OrganizationId, preCanceled.Token));
        }
        await AssertUnboundAsync(test.Database, backend);
        await using (var failed = await test.Database.BeginTransactionAsync(test.Seed.OrganizationB))
        {
            Assert.Equal(backend, await ScalarAsync<int>(failed, "SELECT pg_backend_pid()"));
            await using var division = failed.CreateCommand("SELECT 1/0");
            var error = await Assert.ThrowsAsync<PostgresException>(() => division.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal(new long[] { 3, 3, 0 }, await test.CountsAsync());
        Assert.Equal(ProjectCreationOutcome.Created, (await test.CreateAsync()).Outcome);
        Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
        await AssertUnboundAsync(test.Database, backend);
    }

    [PostgresFact]
    public async Task CreatorTransactionsKeepExistingBoundsAndRejectPresetReboundOrChangedOrganizationContext()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var backend = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        var preset = new NpgsqlConnectionStringBuilder(test.Case.ProjectCreationConnectionString)
        {
            Options = $"-c weft.organization_id={test.Seed.OrganizationB:D}"
        };
        await using (var unsafePool = new PostgresProjectCreationDatabase(preset.ConnectionString))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => unsafePool.BeginTransactionAsync(test.OrganizationId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresProjectCreator(unsafePool).CreateAsync(
                test.Actor, test.OrganizationId, Guid.NewGuid(), Metadata("Preset context")));
        }
        await using (var transaction = await test.Database.BeginTransactionAsync(test.OrganizationId))
        {
            Assert.Equal(backend, await ScalarAsync<int>(transaction, "SELECT pg_backend_pid()"));
            Assert.Equal("off", await ScalarAsync<string>(transaction, "SHOW transaction_read_only"));
            Assert.Equal("read committed", await ScalarAsync<string>(transaction, "SHOW transaction_isolation"));
            Assert.Equal("1s", await ScalarAsync<string>(transaction, "SHOW lock_timeout"));
            Assert.Equal("5s", await ScalarAsync<string>(transaction, "SHOW statement_timeout"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.BindOrganizationAsync(test.Seed.OrganizationB));
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.BindOrganizationAsync(test.OrganizationId));
            Assert.Equal(test.OrganizationId.ToString("D"), await ScalarAsync<string>(transaction, "SHOW weft.organization_id"));
            await using (var tamper = transaction.CreateCommand("SELECT set_config('weft.organization_id',$1,true)"))
            {
                tamper.Parameters.AddWithValue(test.Seed.OrganizationB.ToString("D"));
                await tamper.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Creator.CreateInTransactionAsync(transaction,
                test.Actor, test.OrganizationId, Guid.NewGuid(), Metadata("Changed context")));
            await transaction.RollbackAsync();
            Assert.Throws<InvalidOperationException>(() => transaction.CreateCommand("SELECT 1"));
        }
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal(new long[] { 3, 3, 0 }, await test.CountsAsync());
        Assert.Equal(ProjectCreationOutcome.Created, (await test.CreateAsync()).Outcome);
        await AssertUnboundAsync(test.Database, backend);
    }

    [PostgresFact]
    public async Task RecreatedPoolAndOwnedClusterRestartReplayTheSameDurableOriginalFacts()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var key = Guid.NewGuid();
        var metadata = Metadata("Durable restart receipt");
        var created = await test.CreateAsync(key, metadata);
        Assert.Equal(ProjectCreationOutcome.Created, created.Outcome);
        Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
        await test.Database.DisposeAsync();
        await using (var recreated = new PostgresProjectCreationDatabase(test.Case.ProjectCreationConnectionString))
        {
            var replay = await new PostgresProjectCreator(recreated).CreateAsync(test.Actor, test.OrganizationId, key, metadata);
            Assert.Equal(ProjectCreationOutcome.Replayed, replay.Outcome);
            Assert.Equal(created.Receipt, replay.Receipt);
        }
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var afterRestart = new PostgresProjectCreationDatabase(test.Case.ProjectCreationConnectionString);
        var after = await new PostgresProjectCreator(afterRestart).CreateAsync(test.Actor, test.OrganizationId, key, metadata);
        Assert.Equal(ProjectCreationOutcome.Replayed, after.Outcome);
        Assert.Equal(created.Receipt, after.Receipt);
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case);
        Assert.Equal(new long[] { 4, 4, 1 }, await CountsAsync(owner));
        var backend = await ProjectCreationTestSupport.BackendAsync(afterRestart.DataSource);
        await AssertUnboundAsync(afterRestart, backend);
    }

    [PostgresFact]
    public async Task ActualServerCommitWithAcknowledgementLostFailsTheCallerAndSameKeyReconcilesExactlyOnce()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var direct = new NpgsqlConnectionStringBuilder(test.Case.ProjectCreationConnectionString);
        await using var relay = new OwnedCommitAckLossProxy(direct.Host!, direct.Port);
        await using var callerDatabase = new PostgresProjectCreationDatabase(relay.Route(direct.ConnectionString));
        var caller = new PostgresProjectCreator(callerDatabase);
        var key = Guid.NewGuid();
        var metadata = Metadata("Committed while the caller cannot see its acknowledgement");
        var pending = caller.CreateAsync(test.Actor, test.OrganizationId, key, metadata);
        string originalFacts;
        Guid originalProject;
        DateTimeOffset originalTimestamp;
        try
        {
            await relay.CommitAcknowledgementWithheld.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(relay.FrontendCommitSeen);
            Assert.True(relay.BackendCommitCompleteSeen);
            Assert.True(relay.BackendIdleReadySeen);
            Assert.False(pending.IsCompleted);
            // A separate real owner connection observes durable visibility, not
            // the relay's protocol flags or a fabricated successful response.
            Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
            originalFacts = await test.CreationSnapshotAsync(key);
            originalProject = await test.Case.ScalarAsync<Guid>(
                "SELECT project_id FROM weft.project_creations WHERE organization_id=$1 AND idempotency_key=$2",
                test.OrganizationId, key);
            var storedTimestamp = await test.Case.ScalarAsync<DateTime>(
                "SELECT created_at FROM weft.project_creations WHERE organization_id=$1 AND idempotency_key=$2",
                test.OrganizationId, key);
            Assert.Equal(DateTimeKind.Utc, storedTimestamp.Kind);
            originalTimestamp = new DateTimeOffset(storedTimestamp);
            Assert.False(pending.IsCompleted);
            relay.DisconnectCaller();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Project creation persistence failed.", error.Message);
            Assert.True(pending.IsFaulted);
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            relay.DisconnectCaller();
            try { await relay.DisposeAsync(); }
            finally { await DrainAsync(pending); }
        }
        await using var reconciledDatabase = new PostgresProjectCreationDatabase(test.Case.ProjectCreationConnectionString);
        var replay = await new PostgresProjectCreator(reconciledDatabase).CreateAsync(test.Actor, test.OrganizationId, key, metadata);
        Assert.Equal(ProjectCreationOutcome.Replayed, replay.Outcome);
        var receipt = Assert.IsType<ProjectCreationReceipt>(replay.Receipt);
        Assert.Equal(new ProjectScope(test.OrganizationId, originalProject), receipt.Project);
        Assert.Equal(originalTimestamp, receipt.CreatedAtUtc);
        Assert.Equal(metadata, receipt.Metadata);
        Assert.Equal(originalFacts, await test.CreationSnapshotAsync(key));
        Assert.Equal(new long[] { 4, 4, 1 }, await test.CountsAsync());
        var backend = await ProjectCreationTestSupport.BackendAsync(reconciledDatabase.DataSource);
        await AssertUnboundAsync(reconciledDatabase, backend);
    }

    [Theory]
    [InlineData("Enlist=true")]
    [InlineData("Multiplexing=true")]
    [InlineData("No Reset On Close=true")]
    public void CreatorPoolRejectsExplicitUnsafePoolingOptions(string option)
    {
        Assert.Throws<ArgumentException>(() => new PostgresProjectCreationDatabase(
            $"Host=localhost;Database=synthetic;Username=weft_project_creator;{option}"));
    }

    [Fact]
    public async Task DisposedLazyCreatorPoolCannotBeLeasedOrExposed()
    {
        var database = new PostgresProjectCreationDatabase(
            "Host=localhost;Database=synthetic;Username=weft_project_creator;Maximum Pool Size=1");
        await database.DisposeAsync();
        await database.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => database.DataSource);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => database.BeginTransactionAsync(Guid.NewGuid()));
    }

    private static ProjectCreationMetadata Metadata(string name) => new(name, "Exact project creation transaction intent");

    private enum Authority { Principal, OrganizationAdmission, Capability }

    private static ProjectCreationOutcome Denial(Authority authority) => authority == Authority.Capability
        ? ProjectCreationOutcome.CapabilityDenied : ProjectCreationOutcome.NotAdmitted;

    private static NpgsqlCommand RevocationCommand(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Authority authority, Guid organization, Guid principal)
    {
        var command = new NpgsqlCommand(authority switch
        {
            Authority.Principal => "UPDATE weft.principals SET active=false WHERE id=$1",
            Authority.OrganizationAdmission => "UPDATE weft.organization_memberships SET active=false WHERE principal_id=$1 AND organization_id=$2",
            Authority.Capability => "UPDATE weft.organization_project_creation_capabilities SET can_create_projects=false WHERE principal_id=$1 AND organization_id=$2",
            _ => throw new ArgumentOutOfRangeException(nameof(authority))
        }, connection, transaction);
        command.Parameters.AddWithValue(principal);
        if (authority != Authority.Principal)
            command.Parameters.AddWithValue(organization);
        return command;
    }

    private static async Task LockFactsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("LOCK TABLE ONLY weft.project_creations IN SHARE MODE", connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertLateFactInsertLocksAsync(PostgresCase test, int creatorPid)
    {
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test);
        await using var command = new NpgsqlCommand("""
            SELECT
                EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid=$1 AND locktype='relation'
                    AND relation='weft.project_creations'::regclass AND mode='RowExclusiveLock' AND NOT granted),
                EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid=$1 AND locktype='relation'
                    AND relation='weft.projects'::regclass AND mode='RowExclusiveLock' AND granted),
                EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid=$1 AND locktype='relation'
                    AND relation='weft.project_memberships'::regclass AND mode='RowExclusiveLock' AND granted)
            """, owner);
        command.Parameters.AddWithValue(creatorPid);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0), "The creator must be waiting for the late facts-table write lock.");
        Assert.True(reader.GetBoolean(1), "The creator must already hold its project write lock.");
        Assert.True(reader.GetBoolean(2), "The creator must already hold its first-membership write lock.");
        Assert.False(await reader.ReadAsync());
    }

    private static async Task AssertUnboundAsync(PostgresProjectCreationDatabase database, int expectedBackend)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        Assert.Equal(expectedBackend, await BackendAsync(connection));
        await using (var context = new NpgsqlCommand("SELECT NULLIF(current_setting('weft.organization_id',true),'')", connection))
            Assert.Equal(DBNull.Value, await context.ExecuteScalarAsync());
        foreach (var (setting, expected) in new[]
        {
            ("transaction_read_only", "off"), ("transaction_isolation", "read committed"),
            ("lock_timeout", "0"), ("statement_timeout", "0")
        })
        {
            await using var command = new NpgsqlCommand($"SHOW {setting}", connection);
            Assert.Equal(expected, await command.ExecuteScalarAsync());
        }
        await using (var roles = new NpgsqlCommand("SELECT current_user, session_user, pg_catalog.txid_current_if_assigned()", connection))
        await using (var reader = await roles.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("weft_project_creator", reader.GetString(0));
            Assert.Equal("weft_project_creator", reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.False(await reader.ReadAsync());
        }
        await using (var locks = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_locks held
            LEFT JOIN pg_catalog.pg_class relation ON relation.oid=held.relation
            LEFT JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
            WHERE held.pid=pg_backend_pid()
              AND (held.locktype IN ('transactionid','tuple','advisory') OR namespace.nspname='weft')
            """, connection))
            Assert.Equal(0L, await locks.ExecuteScalarAsync());
        // Beginning a fresh transaction on this same lease also rejects any
        // incompletely disposed transaction retained by the client connection.
        await using var fresh = await connection.BeginTransactionAsync();
        await fresh.RollbackAsync();
    }

    private static async Task<int> BackendAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        return (int)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected creator backend."));
    }

    private static async Task<T> ScalarAsync<T>(PostgresTransaction transaction, string sql)
    {
        await using var command = transaction.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected creator transaction scalar."));
    }

    private static async Task<long[]> CountsAsync(NpgsqlConnection owner)
    {
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM weft.projects), (SELECT count(*) FROM weft.project_memberships),
                (SELECT count(*) FROM weft.project_creations)
            """, owner);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var counts = new[] { reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) };
        Assert.False(await reader.ReadAsync());
        return counts;
    }

    private static async Task DrainAsync(Task task)
    {
        // Release the owned blocker first. Cleanup must not replace the original
        // assertion/error, and every request is drained before case disposal.
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }
    }

    /// <summary>
    /// A single owned plaintext PostgreSQL connection. Every ordinary frame is
    /// forwarded unchanged; only actual COMMIT completion/idle acknowledgements
    /// are withheld, and the caller is closed after independent durability proof.
    /// No authentication, query, parameter or result payload is retained/logged.
    /// </summary>
    private sealed class OwnedCommitAckLossProxy : IAsyncDisposable
    {
        private const int MaximumFrameLength = 4 * 1024 * 1024;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(20));
        private readonly TaskCompletionSource _acknowledgementWithheld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _upstreamHost;
        private readonly int _upstreamPort;
        private TcpClient? _caller;
        private TcpClient? _server;
        private int _commitSeen;
        private int _commitCompleteSeen;
        private int _idleReadySeen;
        private int _disposed;

        internal OwnedCommitAckLossProxy(string upstreamHost, int upstreamPort)
        {
            _upstreamHost = upstreamHost;
            _upstreamPort = upstreamPort;
            _listener.Start(1);
            Completion = RunAsync();
        }

        internal Task Completion { get; }
        internal Task CommitAcknowledgementWithheld => _acknowledgementWithheld.Task;
        internal bool FrontendCommitSeen => Volatile.Read(ref _commitSeen) == 1;
        internal bool BackendCommitCompleteSeen => Volatile.Read(ref _commitCompleteSeen) == 1;
        internal bool BackendIdleReadySeen => Volatile.Read(ref _idleReadySeen) == 1;

        internal string Route(string actualConnectionString)
        {
            var routed = new NpgsqlConnectionStringBuilder(actualConnectionString)
            {
                Host = IPAddress.Loopback.ToString(),
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
                SslMode = SslMode.Disable,
                GssEncryptionMode = GssEncryptionMode.Disable
            };
            return routed.ConnectionString;
        }

        internal void DisconnectCaller() => _disconnect.TrySetResult();

        private async Task RunAsync()
        {
            Task? frontend = null;
            Task? backend = null;
            try
            {
                _caller = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                _listener.Stop();
                _caller.NoDelay = true;
                _server = new TcpClient { NoDelay = true };
                await _server.ConnectAsync(_upstreamHost, _upstreamPort, _lifetime.Token);
                frontend = ForwardFrontendAsync(_caller.GetStream(), _server.GetStream(), _lifetime.Token);
                backend = ForwardBackendAsync(_server.GetStream(), _caller.GetStream(), _lifetime.Token);
                var finished = await Task.WhenAny(frontend, backend);
                await finished;
                if (!_disconnect.Task.IsCompleted || !BackendIdleReadySeen)
                    throw RelayFailure();
            }
            catch (Exception) when (_disposed == 1)
            {
                _acknowledgementWithheld.TrySetCanceled();
            }
            catch (Exception)
            {
                var failure = RelayFailure();
                _acknowledgementWithheld.TrySetException(failure);
                throw failure;
            }
            finally
            {
                _listener.Stop();
                _lifetime.Cancel();
                _caller?.Dispose();
                _server?.Dispose();
                // Both pumps are canceled and sockets are closed before draining.
                if (frontend is not null) await DrainAsync(frontend);
                if (backend is not null) await DrainAsync(backend);
            }
        }

        private async Task ForwardFrontendAsync(Stream caller, Stream server, CancellationToken cancellationToken)
        {
            var startup = new byte[8];
            var header = new byte[5];
            var smallPayload = new byte[7];
            var copy = new byte[8192];
            try
            {
                await caller.ReadExactlyAsync(startup, cancellationToken);
                var startupLength = BinaryPrimitives.ReadInt32BigEndian(startup.AsSpan(0, 4));
                if (startupLength is < 8 or > MaximumFrameLength ||
                    BinaryPrimitives.ReadInt32BigEndian(startup.AsSpan(4, 4)) != 196608)
                    throw RelayFailure();
                await server.WriteAsync(startup, cancellationToken);
                await CopyPayloadAsync(caller, server, startupLength - 8, copy, cancellationToken);
                CryptographicOperations.ZeroMemory(startup);
                while (true)
                {
                    await caller.ReadExactlyAsync(header, cancellationToken);
                    var length = FrameLength(header);
                    if (header[0] == (byte)'Q' && length == 11)
                    {
                        await caller.ReadExactlyAsync(smallPayload, cancellationToken);
                        // Pinned Npgsql 10.0.3 transaction CommitAsync sends this
                        // exact pregenerated simple-query frame, without parsing
                        // application SQL or saving any frontend payload history:
                        // https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/NpgsqlTransaction.cs#L123
                        // https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/PregeneratedMessages.cs#L18
                        if (smallPayload.AsSpan().SequenceEqual("COMMIT\0"u8))
                        {
                            if (Interlocked.CompareExchange(ref _commitSeen, 1, 0) != 0)
                                throw RelayFailure();
                        }
                        await server.WriteAsync(header, cancellationToken);
                        await server.WriteAsync(smallPayload, cancellationToken);
                        CryptographicOperations.ZeroMemory(smallPayload);
                    }
                    else
                    {
                        await server.WriteAsync(header, cancellationToken);
                        await CopyPayloadAsync(caller, server, length - 4, copy, cancellationToken);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(startup);
                CryptographicOperations.ZeroMemory(header);
                CryptographicOperations.ZeroMemory(smallPayload);
                CryptographicOperations.ZeroMemory(copy);
            }
        }

        private async Task ForwardBackendAsync(Stream server, Stream caller, CancellationToken cancellationToken)
        {
            var header = new byte[5];
            var commitTag = new byte[7];
            var status = new byte[1];
            var copy = new byte[8192];
            try
            {
                while (true)
                {
                    await server.ReadExactlyAsync(header, cancellationToken);
                    var length = FrameLength(header);
                    if (!FrontendCommitSeen)
                    {
                        await caller.WriteAsync(header, cancellationToken);
                        await CopyPayloadAsync(server, caller, length - 4, copy, cancellationToken);
                        continue;
                    }
                    // Npgsql's internal commit waits for CommandComplete then
                    // ReadyForQuery. These frames originate from the real server:
                    // https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/Internal/NpgsqlConnector.cs#L2876
                    // https://www.postgresql.org/docs/16/protocol-message-formats.html
                    if (!BackendCommitCompleteSeen)
                    {
                        if (header[0] != (byte)'C' || length != 11)
                            throw RelayFailure();
                        await server.ReadExactlyAsync(commitTag, cancellationToken);
                        if (!commitTag.AsSpan().SequenceEqual("COMMIT\0"u8))
                            throw RelayFailure();
                        CryptographicOperations.ZeroMemory(commitTag);
                        Volatile.Write(ref _commitCompleteSeen, 1);
                        continue;
                    }
                    if (header[0] != (byte)'Z' || length != 5)
                        throw RelayFailure();
                    await server.ReadExactlyAsync(status, cancellationToken);
                    if (status[0] != (byte)'I')
                        throw RelayFailure();
                    CryptographicOperations.ZeroMemory(status);
                    Volatile.Write(ref _idleReadySeen, 1);
                    _acknowledgementWithheld.TrySetResult();
                    await _disconnect.Task.WaitAsync(cancellationToken);
                    return;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(header);
                CryptographicOperations.ZeroMemory(commitTag);
                CryptographicOperations.ZeroMemory(status);
                CryptographicOperations.ZeroMemory(copy);
            }
        }

        private static int FrameLength(byte[] header)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
            return length is >= 4 and <= MaximumFrameLength ? length : throw RelayFailure();
        }

        private static async Task CopyPayloadAsync(Stream source, Stream destination, int count, byte[] buffer,
            CancellationToken cancellationToken)
        {
            while (count > 0)
            {
                var length = Math.Min(count, buffer.Length);
                try
                {
                    await source.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);
                    await destination.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
                count -= length;
            }
        }

        private static InvalidOperationException RelayFailure()
            => new("The owned PostgreSQL commit acknowledgement relay failed.");

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _disconnect.TrySetResult();
            _lifetime.Cancel();
            _listener.Stop();
            _caller?.Dispose();
            _server?.Dispose();
            try { await Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { _lifetime.Dispose(); }
        }
    }
}
