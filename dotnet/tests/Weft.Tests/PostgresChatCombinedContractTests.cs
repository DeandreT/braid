using System.Text;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresChatCombinedContractTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task LifecycleHasNoPrivateColumnOrUnscopedReadAndAllProfilesAdmitAllThreeSiblings()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        await using var database = new PostgresChatLifecycleDatabase(f.Case.ChatLifecycleConnectionString);
        var lifecycle = new PostgresChatLifecycleStore(database);
        Assert.NotNull(await lifecycle.ObserveAsync(f.Key, turn.Intent.TurnId));
        await using var connection = PostgresChatSecurityTests.Open(f.Case.ChatLifecycleConnectionString); await connection.OpenAsync();
        await using (var privileges = new NpgsqlCommand("""
            SELECT NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='weft' AND c.relname=ANY($1::text[]) AND
                    (has_table_privilege(current_user,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                     OR has_any_column_privilege(current_user,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT has_function_privilege(current_user,'weft.chat_reserve_identity(uuid,uuid,uuid,uuid,integer,uuid,uuid)','EXECUTE')
                AND NOT EXISTS(SELECT 1 FROM pg_proc WHERE oid IN ('weft.check_chat_counters()'::regprocedure,
                    'weft.check_chat_identity_erasure()'::regprocedure) AND prosecdef)
            """, connection))
        { privileges.Parameters.AddWithValue(ChatSchemaContract.ChatTables); Assert.True(Assert.IsType<bool>(await privileges.ExecuteScalarAsync())); }
        foreach (var table in ChatSchemaContract.ChatTables)
        {
            await using var forbidden = new NpgsqlCommand($"SELECT * FROM weft.{table}", connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        var before = await PostgresChatSecurityTests.RowsAsync(f);
        foreach (var role in new[] { "weft_chat_reader", "weft_chat_writer", "weft_chat_lifecycle" })
        {
            await f.Case.ExecuteAsync($"GRANT SELECT(private_text) ON weft.chat_events TO {role} WITH GRANT OPTION");
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(f.Actor, f.Key));
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.StartAsync(f.Actor, f.Key, turn.Intent));
                await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.ObserveAsync(f.Key, turn.Intent.TurnId));
                PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
            }
            finally
            {
                if (role == "weft_chat_lifecycle") await f.Case.ExecuteAsync($"REVOKE SELECT(private_text) ON weft.chat_events FROM {role}");
                else await f.Case.ExecuteAsync($"REVOKE GRANT OPTION FOR SELECT(private_text) ON weft.chat_events FROM {role}");
            }
            Assert.NotNull(await lifecycle.ObserveAsync(f.Key, turn.Intent.TurnId));
            Assert.NotNull(await f.Reader.ReadAsync(f.Actor, f.Key));
        }
    }

    [PostgresFact]
    public async Task TypedCompletionConservesOneVersionAndCannotBeHiddenByMalformedFinalScope()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(); var round = PostgresChatTestSupport.Round();
        var reserved = await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round);
        var metadata = ChatMetadataCodec.Terminal(new(new(round.RoundId, ChatUsageOrigin.ProviderRoundTerminal, 0, 4, null),
            finishReason: ChatProviderFinishReason.Stop));
        await using var connection = PostgresChatSecurityTests.Open(f.Case.ChatWriterConnectionString); await connection.OpenAsync();
        // An accounting event without its resolved round is not a valid extra
        // event/version; no scope change may hide that broken joint transition.
        var before = await PostgresChatSecurityTests.RowsAsync(f);
        foreach (var complete in new[] { false, true })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await PostgresChatSecurityTests.BindAsync(connection, transaction, f);
            await using (var advance = new NpgsqlCommand("UPDATE weft.chat_turns SET version=version+1,event_count=event_count+1,event_bytes=event_bytes+$2 WHERE turn_id=$1", connection, transaction))
            { advance.Parameters.AddWithValue(turn.Intent.TurnId); advance.Parameters.AddWithValue(Encoding.UTF8.GetByteCount(metadata)); Assert.Equal(1, await advance.ExecuteNonQueryAsync()); }
            await using (var append = new NpgsqlCommand("""
                INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence,metadata_json)
                SELECT organization_id,creator_id,conversation_id,turn_id,event_count,$2,12,'',$3,$4,$5 FROM weft.chat_turns WHERE turn_id=$1
                """, connection, transaction))
            {
                append.Parameters.AddWithValue(turn.Intent.TurnId); append.Parameters.AddWithValue(Guid.NewGuid());
                append.Parameters.AddWithValue(round.RoundId); append.Parameters.AddWithValue(round.Fence); append.Parameters.AddWithValue(metadata);
                Assert.Equal(1, await append.ExecuteNonQueryAsync());
            }
            if (complete)
            {
                await using var finish = new NpgsqlCommand("UPDATE weft.chat_rounds SET outcome=3,finished_at=statement_timestamp(),result_metadata=$2 WHERE turn_id=$1", connection, transaction);
                finish.Parameters.AddWithValue(turn.Intent.TurnId); finish.Parameters.AddWithValue(metadata); Assert.Equal(1, await finish.ExecuteNonQueryAsync());
            }
            await using (var change = new NpgsqlCommand("SELECT set_config('weft.organization_id','bad-org',true),set_config('weft.chat_principal_id','bad-owner',true)", connection, transaction)) await change.ExecuteNonQueryAsync();
            if (!complete)
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
                PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
            }
            else
            {
                await using (var flush = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction)) await flush.ExecuteNonQueryAsync();
                await using (var restored = new NpgsqlCommand("SELECT current_setting('weft.organization_id')='bad-org' AND current_setting('weft.chat_principal_id')='bad-owner'", connection, transaction))
                    Assert.True(Assert.IsType<bool>(await restored.ExecuteScalarAsync()));
                await transaction.CommitAsync();
            }
        }
        var facts = Assert.IsType<ChatTurnFacts>(await f.Reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
        Assert.Equal(reserved.Turn!.Version + 1, facts.Version); Assert.Equal(2, facts.EventCount);
        Assert.Equal(metadata, (await f.Reader.ReadEventsAsync(f.Actor, f.Key, turn.Intent.TurnId, 0, 100))[^1].MetadataJson);
    }

    [PostgresFact]
    public async Task RevokedLifecycleCloseFlushesInvokerChecksWithoutContentGrantsAndPreservesCallerScope()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        await f.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", f.Actor.PrincipalId.Value);
        await using var connection = PostgresChatSecurityTests.Open(f.Case.ChatLifecycleConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('weft.organization_id','bad-org',true),set_config('weft.chat_principal_id','bad-owner',true)", connection, transaction)) await scope.ExecuteNonQueryAsync();
        await using (var close = new NpgsqlCommand("SELECT weft.chat_lifecycle_close($1,$2,$3,$4,$5,$6,$7,$8,5,$9,NULL)", connection, transaction))
        {
            close.Parameters.AddWithValue(f.Key.PinnedRevision.OrganizationId); close.Parameters.AddWithValue(f.Key.Creator.Value);
            close.Parameters.AddWithValue(f.Key.ConversationId); close.Parameters.AddWithValue(f.Key.PinnedRevision.ProjectId);
            close.Parameters.AddWithValue(f.Key.PinnedRevision.MappingId); close.Parameters.AddWithValue(f.Key.PinnedRevision.RevisionId);
            close.Parameters.AddWithValue(turn.Intent.TurnId); close.Parameters.AddWithValue(turn.Version); close.Parameters.AddWithValue(Guid.NewGuid());
            Assert.Equal("Applied", await close.ExecuteScalarAsync());
        }
        await using (var flush = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction)) await flush.ExecuteNonQueryAsync();
        await using (var restored = new NpgsqlCommand("SELECT current_setting('weft.organization_id')='bad-org' AND current_setting('weft.chat_principal_id')='bad-owner' AND current_user=session_user", connection, transaction))
            Assert.True(Assert.IsType<bool>(await restored.ExecuteScalarAsync()));
        await transaction.CommitAsync();
        Assert.Equal(5, await f.Case.ScalarAsync<int>("SELECT phase FROM weft.chat_turns WHERE turn_id=$1", turn.Intent.TurnId));
        Assert.Null(await f.Reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
    }

    [PostgresFact]
    public async Task LifecycleSchemaLocksPersistThroughTransactionAndNewIdentityDriftRefusesEveryPort()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        await using var database = new PostgresChatLifecycleDatabase(f.Case.ChatLifecycleConnectionString);
        await using (var held = await database.BeginAsync(f.Key, CancellationToken.None))
        {
            await using var owner = PostgresChatSecurityTests.Open(f.Case.MigrationConnectionString); await owner.OpenAsync();
            await using var transaction = await owner.BeginTransactionAsync();
            // ADD COLUMN requests ACCESS EXCLUSIVE, which conflicts with the held
            // ACCESS SHARE capability. DISABLE TRIGGER's weaker lock does not.
            // Roll back even if this negative probe unexpectedly succeeds.
            await using var ddl = new NpgsqlCommand("SET LOCAL lock_timeout='100ms'; ALTER TABLE weft.chat_identities ADD COLUMN chat_lock_probe integer", owner, transaction);
            var error = await Assert.ThrowsAsync<PostgresException>(() => ddl.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }
        foreach (var (change, restore) in new[]
        {
            ("ALTER TABLE weft.chat_identities DISABLE TRIGGER chat_identity_erasure", "ALTER TABLE weft.chat_identities ENABLE TRIGGER chat_identity_erasure"),
            // Re-adding a setting appends it, so restore both in their original order.
            ("ALTER FUNCTION weft.check_chat_identity_erasure() RESET weft.organization_id",
                "ALTER FUNCTION weft.check_chat_identity_erasure() RESET weft.chat_principal_id; ALTER FUNCTION weft.check_chat_identity_erasure() SET weft.organization_id=''; ALTER FUNCTION weft.check_chat_identity_erasure() SET weft.chat_principal_id=''"),
            ("ALTER FUNCTION weft.check_chat_counters() SECURITY DEFINER", "ALTER FUNCTION weft.check_chat_counters() SECURITY INVOKER"),
            ("DROP INDEX weft.chat_identities_conversation", "CREATE INDEX chat_identities_conversation ON weft.chat_identities(organization_id,creator_id,conversation_id)")
        })
        {
            var before = await PostgresChatSecurityTests.RowsAsync(f); await f.Case.ExecuteAsync(change);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(f.Actor, f.Key));
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.StartAsync(f.Actor, f.Key, turn.Intent));
                await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresChatLifecycleStore(database).ObserveAsync(f.Key, turn.Intent.TurnId));
                PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
            }
            finally { await f.Case.ExecuteAsync(restore); }
            Assert.NotNull(await f.Reader.ReadAsync(f.Actor, f.Key));
        }
    }

    [PostgresFact]
    public async Task TombstoneMarkWithoutContentErasureCannotBeHiddenFromInvokerCheckByFinalScope()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture, TimeSpan.FromSeconds(10));
        var turn = await f.StartAsync();
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Cancelled)).Outcome);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (new DateTimeOffset(await f.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()")) < f.Conversation.RetainUntilUtc)
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        var before = await PostgresChatSecurityTests.RowsAsync(f);
        foreach (var finalScope in new[] { "", "malformed-scope" })
            foreach (var immediate in new[] { false, true })
            {
                await using var connection = PostgresChatSecurityTests.Open(f.Case.ChatWriterConnectionString); await connection.OpenAsync(deadline.Token);
                await using var transaction = await connection.BeginTransactionAsync(deadline.Token);
                await PostgresChatSecurityTests.BindAsync(connection, transaction, f);
                await using (var mark = new NpgsqlCommand("""
                    UPDATE weft.chat_identities SET erased_at=statement_timestamp(),retirement_policy_id=$2,
                        retirement_policy_version=$3,retirement_not_before=$4 WHERE conversation_id=$1
                    """, connection, transaction))
                {
                    mark.Parameters.AddWithValue(f.Key.ConversationId); mark.Parameters.AddWithValue(f.ErasurePolicy.PolicyId);
                    mark.Parameters.AddWithValue(f.ErasurePolicy.Version); mark.Parameters.AddWithValue(f.ErasurePolicy.IdentityRetirementNotBeforeUtc);
                    Assert.Equal(3, await mark.ExecuteNonQueryAsync(deadline.Token));
                }
                await using (var scope = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true),set_config('weft.chat_principal_id',$1,true)", connection, transaction))
                { scope.Parameters.AddWithValue(finalScope); await scope.ExecuteNonQueryAsync(deadline.Token); }
                var error = await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    if (immediate)
                    {
                        await using var flush = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction);
                        await flush.ExecuteNonQueryAsync(deadline.Token);
                    }
                    await transaction.CommitAsync(deadline.Token);
                });
                Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
                PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
            }
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.DeleteExpiredAsync(f.Actor, f.Key, f.ErasurePolicy));
        Assert.Equal(3L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_identities WHERE erased_at IS NOT NULL"));
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_conversations"));
    }
}
