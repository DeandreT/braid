using Npgsql;
using Weft.Application;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresServerSessionTransactionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ExpiredCandidateDeniesWhileItsForeignGlobalDigestArbiterIsStillUncommitted()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var key = test.Key();
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var insert = PostgresServerSessionTestSupport.HistoricalInsert(blocker, transaction, key, test.Binding,
            Guid.NewGuid(), PostgresServerSessionTestSupport.Ticket())) Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        var intent = await test.IntentAsync(new(test.OtherOrganization, key.HandleDigest), await test.DeadlineAsync(-1));
        await PostgresServerSessionTestSupport.ResetAsync(test.WriterDatabase);
        using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = test.Writer.CreateOnlyAsync(intent, fallback.Token);
        var completed = false;
        try
        {
            // Denied must settle while the unique arbiter remains held. An attempted
            // INSERT/probe would wait or fail its unchanged lock timeout instead.
            PostgresServerSessionTestSupport.WithoutFacts(await pending.WaitAsync(TimeSpan.FromSeconds(5)), ServerSessionWriteOutcome.Denied);
            PostgresServerSessionTestSupport.SamePrivate("[]", await test.RowsAsync());
            completed = true;
        }
        finally
        {
            fallback.Cancel();
            try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, false, completed); }
            finally { await PostgresServerSessionTestSupport.SettleAsync(pending); }
        }
        PostgresServerSessionTestSupport.SamePrivate("[]", await test.RowsAsync());
    }

    [PostgresFact]
    public async Task ObservedAdmittedForeignDigestContenderReturnsConflictAfterCommitWithoutAdoptingWinnerFacts()
    {
        foreach (var deactivatePrincipal in new[] { false, true })
        {
            await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
            var key = test.Key();
            await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var insert = PostgresServerSessionTestSupport.HistoricalInsert(blocker, transaction, key, test.Binding,
                Guid.NewGuid(), PostgresServerSessionTestSupport.Ticket())) Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            await using var winnerSnapshot = new NpgsqlCommand($"""
                SELECT COALESCE(jsonb_agg(to_jsonb(s) ORDER BY organization_id,handle_digest),'[]'::jsonb)::text
                FROM (SELECT {PostgresServerSessionTestSupport.Columns} FROM weft.server_sessions) s
                """, blocker, transaction);
            var winningRows = Assert.IsType<string>(await winnerSnapshot.ExecuteScalarAsync());
            var application = "weft-session-collision-" + Guid.NewGuid().ToString("N");
            await using var database = await PreparedWriterDatabaseAsync(test, application);
            var writer = new PostgresServerSessionWriter(database);
            await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
            await observer.OpenAsync();
            await using var prepared = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
            await using var parentBarrier = deactivatePrincipal
                ? await PreparePrincipalBarrierAsync(observer, null, application, blocker.ProcessID, test.Principal.Value, false) : null;
            await using var arbitrationBarrier = deactivatePrincipal
                ? await PreparePrincipalBarrierAsync(blocker, transaction, application, blocker.ProcessID, test.Principal.Value, true, "COMMIT;") : null;
            var authorityBefore = await test.AuthorityAsync();
            var expectedAuthority = deactivatePrincipal ? await DeactivatedAuthorityAsync(test, authorityBefore) : authorityBefore;
            var intent = await test.IntentAsync(new(test.OtherOrganization, key.HandleDigest), ticket: [9, 8, 7]);
            using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var parent = parentBarrier?.ExecuteNonQueryAsync(fallback.Token);
            var arbitration = arbitrationBarrier?.ExecuteScalarAsync(fallback.Token);
            var pending = writer.CreateOnlyAsync(intent, fallback.Token);
            var released = false;
            var completed = false;
            try
            {
                if (deactivatePrincipal)
                {
                    Assert.Equal(1, await parent!.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.True(Assert.IsType<bool>(await arbitration!.WaitAsync(TimeSpan.FromSeconds(5))));
                    released = true;
                    await RequireArbiterReleasedAsync(blocker);
                }
                else
                {
                    await PostgresServerSessionTestSupport.WaitBlockedAsync(prepared, pending);
                    await transaction.CommitAsync();
                    released = true;
                }
                // A committed global collision has no inserted candidate and no
                // positive acknowledgement, even if its original parent changed.
                PostgresServerSessionTestSupport.WithoutFacts(await pending.WaitAsync(TimeSpan.FromSeconds(5)), ServerSessionWriteOutcome.Conflict);
                PostgresServerSessionTestSupport.SamePrivate(expectedAuthority, await test.AuthorityAsync());
                PostgresServerSessionTestSupport.SamePrivate(winningRows, await test.RowsAsync());
                completed = true;
            }
            finally
            {
                fallback.Cancel();
                if (arbitration is not null) await PostgresServerSessionTestSupport.SettleAsync(arbitration);
                if (parent is not null) await PostgresServerSessionTestSupport.SettleAsync(parent);
                try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, released, completed); }
                finally
                {
                    await PostgresServerSessionTestSupport.SettleAsync(pending);
                }
            }
            var stored = await test.StoredAsync(key);
            if (deactivatePrincipal) Assert.Null(await test.Reader.ObserveAsync(key));
            else await test.CurrentAsync(stored);
            Assert.Null(await test.Reader.ObserveAsync(intent.Key));
            Assert.True(stored.CopyProtectedTicket().AsSpan().SequenceEqual(PostgresServerSessionTestSupport.Ticket()),
                "The colliding contender replaced the private winner bytes.");
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.server_sessions"));
        }
    }

    [PostgresFact]
    public async Task ObservedRollbackArbiterCanAcknowledgeFreshCreationWithoutRemintingItsInitialLifetime()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var unrelated = await test.CreateAsync();
        var authority = await test.AuthorityAsync();
        var arbiterKey = test.Key();
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var insert = PostgresServerSessionTestSupport.HistoricalInsert(blocker, transaction, arbiterKey, test.Binding,
            Guid.NewGuid(), PostgresServerSessionTestSupport.Ticket())) Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        var application = "weft-session-rollback-create-" + Guid.NewGuid().ToString("N");
        await using var database = await PreparedWriterDatabaseAsync(test, application);
        var writer = new PostgresServerSessionWriter(database);
        await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        var intent = await test.IntentAsync(new(test.OtherOrganization, arbiterKey.HandleDigest), ticket: [19, 29, 39]);
        using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var beforeDispatch = await StatementTimeAsync(observer, fallback.Token);
        var pending = writer.CreateOnlyAsync(intent, fallback.Token);
        var released = false;
        var completed = false;
        try
        {
            await PostgresServerSessionTestSupport.WaitBlockedAsync(prepared, pending);
            var whileBlocked = await StatementTimeAsync(observer, fallback.Token);
            Assert.False(pending.IsCompleted, "The admitted create settled before its owned unique arbiter was released.");
            await transaction.RollbackAsync();
            released = true;
            var created = PostgresServerSessionTestSupport.Acknowledged(await pending.WaitAsync(TimeSpan.FromSeconds(5)), ServerSessionWriteOutcome.Created);
            Assert.True(created.Key == intent.Key && created.BindingId == intent.BindingId &&
                created.BindingVersion == intent.BindingVersion && created.LoginEpoch == intent.LoginEpoch &&
                created.CopyProtectedTicket().AsSpan().SequenceEqual(intent.CopyProtectedTicket()) &&
                created.Version == 1 && created.RevokedAtUtc is null && created.PolicyVersion == 1 && created.CodecVersion == 1,
                "The acknowledged rollback contender did not preserve its exact private creation intent.");
            Assert.True(created.Lifetime.IssuedAtUtc >= beforeDispatch && created.Lifetime.IssuedAtUtc <= whileBlocked &&
                created.Lifetime.IdleExpiresAtUtc - created.Lifetime.IssuedAtUtc == TimeSpan.FromMinutes(20) &&
                created.Lifetime.AbsoluteExpiresAtUtc - created.Lifetime.IssuedAtUtc == TimeSpan.FromMinutes(60),
                "The acknowledged rollback contender reminted or extended its initial statement lifetime.");
            await test.CurrentAsync(created);
            PostgresServerSessionTestSupport.SameFacts(unrelated, await test.StoredAsync(unrelated.Key));
            Assert.Null(await test.Reader.ObserveAsync(arbiterKey));
            PostgresServerSessionTestSupport.SamePrivate(authority, await test.AuthorityAsync());
            Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.server_sessions"));
            await PostgresServerSessionTestSupport.ResetAsync(database);
            completed = true;
        }
        finally
        {
            fallback.Cancel();
            try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, released, completed); }
            finally { await PostgresServerSessionTestSupport.SettleAsync(pending); }
        }
    }

    [PostgresFact]
    public async Task ObservedRollbackArbiterAfterCommittedPrincipalDeactivationDeniesAndUndoesOwnInsert()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var unrelated = await test.CreateAsync();
        var before = await test.RowsAsync();
        var authorityBefore = await test.AuthorityAsync();
        var deactivatedAuthority = await DeactivatedAuthorityAsync(test, authorityBefore);
        var arbiterKey = test.Key();
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var insert = PostgresServerSessionTestSupport.HistoricalInsert(blocker, transaction, arbiterKey, test.Binding,
            Guid.NewGuid(), PostgresServerSessionTestSupport.Ticket())) Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        var application = "weft-session-rollback-deny-" + Guid.NewGuid().ToString("N");
        await using var database = await PreparedWriterDatabaseAsync(test, application);
        var writer = new PostgresServerSessionWriter(database);
        await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var parentBarrier = await PreparePrincipalBarrierAsync(observer, null, application, blocker.ProcessID, test.Principal.Value, false);
        await using var arbitrationBarrier = await PreparePrincipalBarrierAsync(blocker, transaction, application, blocker.ProcessID, test.Principal.Value, true, "ROLLBACK;");
        var intent = await test.IntentAsync(new(test.OtherOrganization, arbiterKey.HandleDigest), ticket: [17, 27, 37]);
        using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var parent = parentBarrier.ExecuteNonQueryAsync(fallback.Token);
        var arbitration = arbitrationBarrier.ExecuteScalarAsync(fallback.Token);
        var pending = writer.CreateOnlyAsync(intent, fallback.Token);
        var released = false;
        var completed = false;
        try
        {
            // Both controllers are already executing in PostgreSQL before dispatch.
            // The parent commits only after the actual digest wait; the arbiter
            // rolls back only after observing that commit and the same wait edge.
            Assert.Equal(1, await parent.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(Assert.IsType<bool>(await arbitration.WaitAsync(TimeSpan.FromSeconds(5))));
            released = true;
            await RequireArbiterReleasedAsync(blocker);
            // The original call settles naturally; cancellation never manufactures
            // its ordinary denial after the committed parent change.
            PostgresServerSessionTestSupport.WithoutFacts(await pending.WaitAsync(TimeSpan.FromSeconds(5)), ServerSessionWriteOutcome.Denied);
            PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
            PostgresServerSessionTestSupport.SamePrivate(deactivatedAuthority, await test.AuthorityAsync());
            PostgresServerSessionTestSupport.SameFacts(unrelated, await test.StoredAsync(unrelated.Key));
            Assert.True(await test.Case.ScalarAsync<bool>("SELECT NOT EXISTS(SELECT 1 FROM weft.server_sessions WHERE handle_digest=$1)",
                PostgresServerSessionTestSupport.Bytes(intent.Key)), "An ordinary post-arbitration denial committed its candidate row.");
            completed = true;
        }
        finally
        {
            fallback.Cancel();
            // Join the command on the blocker before asking its transaction to undo.
            await PostgresServerSessionTestSupport.SettleAsync(arbitration);
            await PostgresServerSessionTestSupport.SettleAsync(parent);
            try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, released, completed); }
            finally
            {
                await PostgresServerSessionTestSupport.SettleAsync(pending);
            }
        }
        // Recovery is a separate explicit new handle operation after intentional
        // principal restoration, using the very same writer/database instance.
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=true WHERE id=$1", test.Principal.Value);
        PostgresServerSessionTestSupport.SamePrivate(authorityBefore, await test.AuthorityAsync());
        var recovered = PostgresServerSessionTestSupport.Acknowledged(
            await writer.CreateOnlyAsync(await test.IntentAsync()), ServerSessionWriteOutcome.Created);
        await test.CurrentAsync(recovered);
        PostgresServerSessionTestSupport.SamePrivate(authorityBefore, await test.AuthorityAsync());
        PostgresServerSessionTestSupport.SameFacts(unrelated, await test.StoredAsync(unrelated.Key));
        Assert.True(await test.Case.ScalarAsync<bool>("SELECT NOT EXISTS(SELECT 1 FROM weft.server_sessions WHERE handle_digest=$1)",
            PostgresServerSessionTestSupport.Bytes(intent.Key)), "Pool recovery adopted the previously denied candidate row.");
        await PostgresServerSessionTestSupport.ResetAsync(database);
    }

    [PostgresFact]
    public async Task CommittedPrincipalDeactivationDoesNotReleaseDigestArbitrationOrReplaceTheOriginalLockTimeoutWithDenial()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var authorityBefore = await test.AuthorityAsync();
        var key = test.Key();
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var insert = PostgresServerSessionTestSupport.HistoricalInsert(blocker, transaction, key, test.Binding,
            Guid.NewGuid(), PostgresServerSessionTestSupport.Ticket())) Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        var application = "weft-session-parent-timeout-" + Guid.NewGuid().ToString("N");
        await using var database = await PreparedWriterDatabaseAsync(test, application);
        var writer = new PostgresServerSessionWriter(database);
        await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        await using var parentBarrier = await PreparePrincipalBarrierAsync(observer, null, application, blocker.ProcessID, test.Principal.Value, false);
        // Deliberately retain the arbiter after proving both sides of the commit.
        await using var arbitrationBarrier = await PreparePrincipalBarrierAsync(blocker, transaction, application, blocker.ProcessID, test.Principal.Value, true);
        var intent = await test.IntentAsync(new(test.OtherOrganization, key.HandleDigest));
        using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var parent = parentBarrier.ExecuteNonQueryAsync(fallback.Token);
        var arbitration = arbitrationBarrier.ExecuteScalarAsync(fallback.Token);
        var pending = writer.CreateOnlyAsync(intent, fallback.Token);
        var completed = false;
        try
        {
            Assert.Equal(1, await parent.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(Assert.IsType<bool>(await arbitration.WaitAsync(TimeSpan.FromSeconds(5))));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Server session mutation failed.", error.Message);
            Assert.Null(error.InnerException);
            Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(
                () => PostgresServerSessionTestSupport.WaitBlockedAsync(prepared, pending)));
            Assert.False(fallback.IsCancellationRequested);
            PostgresServerSessionTestSupport.SamePrivate("[]", await test.RowsAsync());
            completed = true;
        }
        finally
        {
            fallback.Cancel();
            await PostgresServerSessionTestSupport.SettleAsync(arbitration);
            await PostgresServerSessionTestSupport.SettleAsync(parent);
            try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, false, completed); }
            finally { await PostgresServerSessionTestSupport.SettleAsync(pending); }
        }
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=true WHERE id=$1", test.Principal.Value);
        PostgresServerSessionTestSupport.SamePrivate(authorityBefore, await test.AuthorityAsync());
        PostgresServerSessionTestSupport.SamePrivate("[]", await test.RowsAsync());
        await PostgresServerSessionTestSupport.ResetAsync(database);
    }

    [PostgresFact]
    public async Task ParentChangesCanCommitDuringObservedSessionWaitAndFinalRenewalFreshlyDenies()
    {
        foreach (var binding in new[] { false, true })
        {
            await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
            var facts = await test.HistoricalAsync();
            var before = await test.RowsAsync();
            var authorityBefore = await test.AuthorityAsync();
            await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await LockAsync(blocker, transaction, facts.Key);
            var application = "weft-session-parent-" + Guid.NewGuid().ToString("N");
            await using var database = await PreparedWriterDatabaseAsync(test, application);
            var writer = new PostgresServerSessionWriter(database);
            await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
            await observer.OpenAsync();
            await using var prepared = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
            await using var parentConnection = binding ? null : PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
            if (parentConnection is not null) await parentConnection.OpenAsync();
            using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pending = writer.RenewAsync(facts.Key, facts.Version, fallback.Token);
            Task<ExternalIdentityBinding?>? parent = null;
            var released = false;
            var completed = false;
            try
            {
                await PostgresServerSessionTestSupport.WaitBlockedAsync(prepared, pending);
                parent = ChangeParentAsync(test, binding, parentConnection, fallback.Token);
                // It must not need the writer's session-row wait to end: no forbidden
                // parent write/row-lock power has been added to that restricted profile.
                var changedBinding = await parent.WaitAsync(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync();
                released = true;
                PostgresServerSessionTestSupport.WithoutFacts(await pending.WaitAsync(TimeSpan.FromSeconds(5)), ServerSessionWriteOutcome.Denied);
                var authority = binding
                    ? await DisabledAuthorityAsync(test, authorityBefore, Assert.IsType<ExternalIdentityBinding>(changedBinding))
                    : await DeactivatedAuthorityAsync(test, authorityBefore);
                PostgresServerSessionTestSupport.SamePrivate(authority, await test.AuthorityAsync());
                completed = true;
            }
            finally
            {
                fallback.Cancel();
                try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, released, completed); }
                finally
                {
                    await PostgresServerSessionTestSupport.SettleAsync(pending);
                    if (parent is not null) await PostgresServerSessionTestSupport.SettleAsync(parent);
                }
            }
            PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
            Assert.Null(await test.Reader.ObserveAsync(facts.Key));
            PostgresServerSessionTestSupport.Acknowledged(await writer.RevokeAsync(facts.Key), ServerSessionWriteOutcome.Revoked);
        }
    }

    [PostgresFact]
    public async Task RenewalAfterObservedUncommittedRestrictedRevokeSeesItsCommittedTombstone()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.HistoricalAsync();
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.SessionWriterConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var bind = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true)", blocker, transaction))
        {
            bind.Parameters.AddWithValue(test.Organization.ToString("D"));
            await bind.ExecuteScalarAsync();
        }
        await using (var revoke = new NpgsqlCommand("UPDATE weft.server_sessions SET revoked_at=statement_timestamp(),version=version+1 WHERE handle_digest=$1", blocker, transaction))
        {
            revoke.Parameters.AddWithValue(PostgresServerSessionTestSupport.Bytes(facts.Key));
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        }
        var application = "weft-session-revoke-race-" + Guid.NewGuid().ToString("N");
        await using var database = await PreparedWriterDatabaseAsync(test, application);
        var writer = new PostgresServerSessionWriter(database);
        await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = writer.RenewAsync(facts.Key, facts.Version, fallback.Token);
        var released = false;
        var completed = false;
        try
        {
            await PostgresServerSessionTestSupport.WaitBlockedAsync(prepared, pending);
            await transaction.CommitAsync();
            released = true;
            PostgresServerSessionTestSupport.WithoutFacts(await pending.WaitAsync(TimeSpan.FromSeconds(5)), ServerSessionWriteOutcome.Denied);
            completed = true;
        }
        finally
        {
            fallback.Cancel();
            try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, released, completed); }
            finally { await PostgresServerSessionTestSupport.SettleAsync(pending); }
        }
        var revoked = await test.StoredAsync(facts.Key);
        PostgresServerSessionTestSupport.SameImmutable(facts, revoked);
        Assert.True(revoked.Version == 2 && revoked.RevokedAtUtc is not null);
        Assert.Null(await test.Reader.ObserveAsync(facts.Key));
        PostgresServerSessionTestSupport.SameFacts(revoked, PostgresServerSessionTestSupport.Acknowledged(
            await writer.RevokeAsync(facts.Key), ServerSessionWriteOutcome.Unchanged));
    }

    [PostgresFact]
    public async Task ConcurrentSameVersionGenuineRenewalsCommitOneIncrementAndOneStaleConflict()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.HistoricalAsync();
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await LockAsync(blocker, transaction, facts.Key);
        var firstApplication = "weft-session-cas-first-" + Guid.NewGuid().ToString("N");
        var secondApplication = "weft-session-cas-second-" + Guid.NewGuid().ToString("N");
        await using var firstDatabase = await PreparedWriterDatabaseAsync(test, firstApplication);
        await using var secondDatabase = await PreparedWriterDatabaseAsync(test, secondApplication);
        var firstWriter = new PostgresServerSessionWriter(firstDatabase);
        var secondWriter = new PostgresServerSessionWriter(secondDatabase);
        await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PrepareBothBlockedAsync(observer, firstApplication, secondApplication, blocker.ProcessID);
        using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var first = firstWriter.RenewAsync(facts.Key, facts.Version, fallback.Token);
        var second = secondWriter.RenewAsync(facts.Key, facts.Version, fallback.Token);
        var released = false;
        var completed = false;
        try
        {
            await PostgresServerSessionTestSupport.WaitBlockedAsync(prepared, first, second);
            // This separate database statement starts after both actual wait edges
            // were observed, while the exact session-row blocker is still held.
            await using var markerCommand = new NpgsqlCommand("SELECT statement_timestamp()", observer);
            var marker = new DateTimeOffset(Assert.IsType<DateTime>(await markerCommand.ExecuteScalarAsync(fallback.Token)));
            Assert.False(first.IsCompleted || second.IsCompleted, "A renewal settled before the owned session-row blocker was released.");
            Assert.True(marker + TimeSpan.FromMinutes(20) < facts.Lifetime.AbsoluteExpiresAtUtc,
                "The positive post-wait renewal fixture has insufficient absolute-lifetime headroom.");
            await transaction.RollbackAsync();
            released = true;
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            var renewed = PostgresServerSessionTestSupport.Acknowledged(Assert.Single(results, value => value.Outcome == ServerSessionWriteOutcome.Renewed), ServerSessionWriteOutcome.Renewed);
            PostgresServerSessionTestSupport.WithoutFacts(Assert.Single(results, value => value.Outcome == ServerSessionWriteOutcome.Conflict), ServerSessionWriteOutcome.Conflict);
            PostgresServerSessionTestSupport.SameImmutable(facts, renewed);
            Assert.True(renewed.Version == facts.Version + 1 && renewed.Version == 2 &&
                renewed.Lifetime.IdleExpiresAtUtc > facts.Lifetime.IdleExpiresAtUtc &&
                renewed.Lifetime.IdleExpiresAtUtc < facts.Lifetime.AbsoluteExpiresAtUtc &&
                renewed.Lifetime.IdleExpiresAtUtc - TimeSpan.FromMinutes(20) >= marker && renewed.RevokedAtUtc is null,
                "The acknowledged renewal did not use a fresh post-wait statement sample and one exact increment.");
            await test.CurrentAsync(renewed);
            completed = true;
        }
        finally
        {
            fallback.Cancel();
            try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, released, completed); }
            finally { await PostgresServerSessionTestSupport.SettleAsync(Task.WhenAll(first, second)); }
        }
    }

    [PostgresFact]
    public async Task CancellationAtObservedSessionRowLockPreservesCallerTokenAndTheSameWriterPoolRecovers()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.HistoricalAsync();
        var before = await test.RowsAsync();
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await LockAsync(blocker, transaction, facts.Key);
        var application = "weft-session-cancel-write-" + Guid.NewGuid().ToString("N");
        await using var database = await PreparedWriterDatabaseAsync(test, application);
        var writer = new PostgresServerSessionWriter(database);
        await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = writer.RevokeAsync(facts.Key, cancellation.Token);
        var completed = false;
        try
        {
            await PostgresServerSessionTestSupport.WaitBlockedAsync(prepared, pending);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
            await test.CurrentAsync(facts);
            completed = true;
        }
        finally
        {
            cancellation.Cancel();
            try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, false, completed); }
            finally { await PostgresServerSessionTestSupport.SettleAsync(pending); }
        }
        PostgresServerSessionTestSupport.Acknowledged(await writer.RevokeAsync(facts.Key), ServerSessionWriteOutcome.Revoked);
        await PostgresServerSessionTestSupport.ResetAsync(database);
    }

    [PostgresFact]
    public async Task OriginalOneSecondWriterLockTimeoutIsRedactedFailureAndSettlesBeforeSamePoolRecovery()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.HistoricalAsync();
        var before = await test.RowsAsync();
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await LockAsync(blocker, transaction, facts.Key);
        var application = "weft-session-timeout-" + Guid.NewGuid().ToString("N");
        await using var database = await PreparedWriterDatabaseAsync(test, application);
        var writer = new PostgresServerSessionWriter(database);
        await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = writer.RevokeAsync(facts.Key, fallback.Token);
        var completed = false;
        try
        {
            await PostgresServerSessionTestSupport.WaitBlockedAsync(prepared, pending);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(string.Equals(error.Message, "Server session mutation failed.", StringComparison.Ordinal), "The session lock timeout returned an unexpected category.");
            Assert.Null(error.InnerException);
            PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
            completed = true;
        }
        finally
        {
            fallback.Cancel();
            try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, false, completed); }
            finally { await PostgresServerSessionTestSupport.SettleAsync(pending); }
        }
        PostgresServerSessionTestSupport.Acknowledged(await writer.RevokeAsync(facts.Key), ServerSessionWriteOutcome.Revoked);
        await PostgresServerSessionTestSupport.ResetAsync(database);
    }

    [PostgresFact]
    public async Task ObservedReaderRelationWaitCancellationIsFailureAndTheSameReaderRecovers()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.CreateAsync();
        var before = await test.RowsAsync();
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var gate = new NpgsqlCommand("LOCK TABLE ONLY weft.server_sessions IN ACCESS EXCLUSIVE MODE", blocker, transaction)) await gate.ExecuteNonQueryAsync();
        var application = "weft-session-cancel-read-" + Guid.NewGuid().ToString("N");
        await using var database = new PostgresServerSessionDatabase(PostgresServerSessionTestSupport.Application(test.Case.SessionReaderConnectionString, application), ServerSessionDatabaseProfile.Reader);
        await PostgresServerSessionTestSupport.ResetAsync(database);
        var reader = new PostgresServerSessionReader(database);
        await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = reader.ObserveAsync(facts.Key, cancellation.Token);
        var completed = false;
        try
        {
            await PostgresServerSessionTestSupport.WaitBlockedAsync(prepared, pending);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            completed = true;
        }
        finally
        {
            cancellation.Cancel();
            try { await PostgresServerSessionTestSupport.ReleaseAsync(transaction, false, completed); }
            finally { await PostgresServerSessionTestSupport.SettleAsync(pending); }
        }
        PostgresServerSessionTestSupport.SameFacts(facts, Assert.IsType<CurrentServerSession>(await reader.ObserveAsync(facts.Key)).Facts);
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
        await PostgresServerSessionTestSupport.ResetAsync(database);
    }

    private static async Task<PostgresServerSessionDatabase> PreparedWriterDatabaseAsync(PostgresServerSessionTestSupport test, string application)
    {
        var database = new PostgresServerSessionDatabase(
            PostgresServerSessionTestSupport.Application(test.Case.SessionWriterConnectionString, application), ServerSessionDatabaseProfile.Writer);
        try
        {
            // Native connection setup has its own 15-second allowance. Finish it
            // before the five-second observer and one-second lock windows begin.
            await PostgresServerSessionTestSupport.ResetAsync(database);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static async Task RequireArbiterReleasedAsync(NpgsqlConnection blocker)
    {
        // SQL COMMIT/ROLLBACK need not clear the managed transaction's Connection.
        // Inspect the backend instead: the insertion's transaction ID and all of
        // this controller's transaction-ID locks must actually be gone.
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.pg_current_xact_id_if_assigned() IS NULL AND NOT EXISTS(
                SELECT 1 FROM pg_catalog.pg_locks WHERE pid=pg_backend_pid() AND locktype='transactionid')
            """, blocker);
        Assert.True(Assert.IsType<bool>(await command.ExecuteScalarAsync()), "The owned digest arbiter transaction is still held.");
    }

    private static Task<string> DeactivatedAuthorityAsync(PostgresServerSessionTestSupport test, string authority) =>
        test.Case.ScalarAsync<string>("""
            SELECT jsonb_set($1::jsonb,'{principals,0,active}','false'::jsonb)::text
            WHERE jsonb_array_length($1::jsonb->'principals')=1
                AND $1::jsonb->'principals'->0->>'id'=$2
            """, authority, test.Principal.Value.ToString("D"));

    private static Task<string> DisabledAuthorityAsync(PostgresServerSessionTestSupport test, string authority, ExternalIdentityBinding binding)
    {
        Assert.True(binding.BindingId == test.Binding.BindingId && binding.Version == test.Binding.Version + 1 &&
            !binding.Enabled && binding.DisabledAtUtc is not null, "The parent change did not acknowledge the exact disabled binding.");
        return test.Case.ScalarAsync<string>("""
            SELECT jsonb_set(jsonb_set(jsonb_set($1::jsonb,
                '{bindings,0,enabled}','false'::jsonb),'{bindings,0,version}',to_jsonb($2::bigint)),
                '{bindings,0,disabled_at}',to_jsonb($3::timestamptz))::text
            WHERE jsonb_array_length($1::jsonb->'bindings')=1
                AND $1::jsonb->'bindings'->0->>'binding_id'=$4
            """, authority, binding.Version, binding.DisabledAtUtc!.Value, test.Binding.BindingId.ToString("D"));
    }

    private static async Task<NpgsqlBatch> PreparePrincipalBarrierAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string application, int blocker, Guid principal, bool deactivated, string ending = "")
    {
        // These invoker-only helpers live only in the controller's temporary schema.
        // VOLATILE gives each loop query a fresh data snapshot, including a parent
        // commit after this statement began; clear the separate activity cache too.
        // No product ACL, session trigger, timeout or statement snapshot is changed.
        await using (var install = new NpgsqlCommand("""
            CREATE TEMP TABLE session_barrier_marker(unused boolean);
            CREATE FUNCTION pg_temp.session_principal_barrier(application text, blocker integer, principal uuid, deactivated boolean)
            RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY INVOKER AS $barrier$
            DECLARE deadline timestamptz := clock_timestamp()+interval '5 seconds';
            BEGIN
                LOOP
                    PERFORM pg_catalog.pg_stat_clear_snapshot();
                    IF EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity a
                        WHERE a.datname=current_database() AND a.application_name=application
                            AND blocker=ANY(pg_catalog.pg_blocking_pids(a.pid))
                            AND EXISTS(SELECT 1 FROM pg_catalog.pg_locks waiting
                                WHERE waiting.pid=a.pid AND NOT waiting.granted
                                    AND waiting.locktype='transactionid' AND waiting.mode='ShareLock'))
                        AND EXISTS(SELECT 1 FROM ONLY weft.principals p
                            WHERE p.id=principal AND p.active=NOT deactivated) THEN
                        RETURN true;
                    END IF;
                    IF clock_timestamp()>=deadline THEN
                        RAISE EXCEPTION 'The owned session principal barrier was not observed.';
                    END IF;
                    PERFORM pg_catalog.pg_sleep(0.005);
                END LOOP;
            END $barrier$;
            """, connection, transaction)) await install.ExecuteNonQueryAsync();
        var batch = new NpgsqlBatch(connection, transaction);
        var gate = new NpgsqlBatchCommand("SELECT pg_temp.session_principal_barrier($1,$2,$3,$4)");
        gate.Parameters.AddWithValue(application);
        gate.Parameters.AddWithValue(blocker);
        gate.Parameters.AddWithValue(principal);
        gate.Parameters.AddWithValue(deactivated);
        batch.BatchCommands.Add(gate);
        if (!deactivated)
        {
            var update = new NpgsqlBatchCommand("UPDATE weft.principals SET active=false WHERE id=$1 AND active");
            update.Parameters.AddWithValue(principal);
            batch.BatchCommands.Add(update);
        }
        else if (ending.Length != 0) batch.BatchCommands.Add(new NpgsqlBatchCommand(ending));
        return batch;
    }

    private static async Task<DateTimeOffset> StatementTimeAsync(NpgsqlConnection observer, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT statement_timestamp()", observer);
        return new DateTimeOffset(Assert.IsType<DateTime>(await command.ExecuteScalarAsync(token)));
    }

    private static async Task<NpgsqlCommand> PrepareBothBlockedAsync(NpgsqlConnection observer,
        string firstApplication, string secondApplication, int blocker)
    {
        // PostgreSQL may queue one tuple-lock waiter behind the other. Require
        // both actual edges to reach only the owned blocker directly or via its
        // one owned peer, rather than assuming two direct blocker edges.
        var command = new NpgsqlCommand("""
            WITH waiters AS MATERIALIZED (
                SELECT a.pid,CASE WHEN a.pid IS NULL THEN ARRAY[]::integer[]
                    ELSE pg_catalog.pg_blocking_pids(a.pid) END blockers
                FROM unnest($1::text[]) requested(application)
                LEFT JOIN pg_catalog.pg_stat_activity a
                    ON a.datname=current_database() AND a.application_name=requested.application
            )
            SELECT count(*)=2 AND count(DISTINCT pid)=2 AND COALESCE(bool_and(
                $2=ANY(waiter.blockers) OR EXISTS(SELECT 1 FROM waiters peer
                    WHERE peer.pid=ANY(waiter.blockers) AND $2=ANY(peer.blockers))),false)
            FROM waiters waiter
            """, observer);
        command.Parameters.AddWithValue(new[] { firstApplication, secondApplication });
        command.Parameters.AddWithValue(blocker);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await command.PrepareAsync(deadline.Token);
            Assert.False(Assert.IsType<bool>(await command.ExecuteScalarAsync(deadline.Token)));
            return command;
        }
        catch { await command.DisposeAsync(); throw; }
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ServerSessionKey key)
    {
        await using var command = new NpgsqlCommand("SELECT version FROM weft.server_sessions WHERE organization_id=$1 AND handle_digest=$2 FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue(key.OrganizationId);
        command.Parameters.AddWithValue(PostgresServerSessionTestSupport.Bytes(key));
        Assert.Equal(1, Assert.IsType<long>(await command.ExecuteScalarAsync()));
    }

    private static async Task<ExternalIdentityBinding?> ChangeParentAsync(PostgresServerSessionTestSupport test, bool binding,
        NpgsqlConnection? connection, CancellationToken token)
    {
        if (binding)
        {
            var result = await test.IdentityOwner.DisableAsync(test.Binding.Key, test.Principal, test.Binding.Version, token);
            Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled, result.Outcome);
            return Assert.IsType<ExternalIdentityBinding>(result.Binding);
        }
        Assert.NotNull(connection);
        await using var command = new NpgsqlCommand("UPDATE weft.principals SET active=false WHERE id=$1", connection);
        command.Parameters.AddWithValue(test.Principal.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
        return null;
    }
}
