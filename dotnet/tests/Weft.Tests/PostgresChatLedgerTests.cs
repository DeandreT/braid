using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresChatLedgerTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Report_operation_identity_and_original_profile_cannot_be_substituted()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var swapped = new ChatConversationIntent(new(f.Key.PinnedRevision, f.Actor.PrincipalId, Guid.NewGuid()),
            f.Conversation.PinnedValidation, f.Conversation.PinnedProjection, f.Conversation.RetainUntilUtc);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.CreateAsync(f.Actor, swapped));
        Assert.Equal("Private chat persistence failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Null(await f.Reader.ReadAsync(f.Actor, swapped.Key));
        var turn = await f.StartAsync();
        var changed = new ChatTurnIntent(turn.Intent.TurnId, turn.Intent.Envelope, ChatTurnPolicyTests.Profile(2), turn.Intent.Budget, turn.Intent.DeadlineUtc);
        Assert.Equal(ChatStoreOutcome.Conflict, (await f.Writer.StartAsync(f.Actor, f.Key, changed)).Outcome);
    }

    [PostgresFact]
    public async Task Expired_chat_requires_explicit_closure_and_delete_preserves_retained_reports()
    {
        // Fixture-only deadline, not an application retention default.
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture, TimeSpan.FromSeconds(10));
        var turn = await f.StartAsync();
        var round = PostgresChatTestSupport.Round();
        var reserved = await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round);
        Assert.Equal(ChatStoreOutcome.Applied, reserved.Outcome);
        var reportsBefore = await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable");
        Assert.Equal(ChatStoreOutcome.InvalidState, await f.Writer.DeleteExpiredAsync(f.Actor, f.Key, f.ErasurePolicy));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (new DateTimeOffset(await f.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()")) < f.Conversation.RetainUntilUtc)
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        Assert.Null(await f.Reader.ReadAsync(f.Actor, f.Key));
        Assert.Null(await f.Reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
        Assert.Equal(ChatStoreOutcome.InvalidState, await f.Writer.DeleteExpiredAsync(f.Actor, f.Key, f.ErasurePolicy));
        var current = Assert.IsType<ChatTurnFacts>(reserved.Turn);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, current.Version, ChatTurnPhase.Interrupted)).Outcome);
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.DeleteExpiredAsync(f.Actor, f.Key, f.ErasurePolicy));
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_turns"));
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_rounds"));
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_events"));
        Assert.Equal(reportsBefore, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable"));
    }

    [PostgresFact]
    public async Task Exact_original_report_profile_request_pins_and_restricted_owner_survive_reopen()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        Assert.Equal(ChatStoreOutcome.Replayed, (await f.Writer.StartAsync(f.Actor, f.Key, turn.Intent)).Outcome);
        var changed = new ChatTurnIntent(turn.Intent.TurnId, new ChatOriginalEnvelope(turn.Intent.Envelope.RequestId, "changed original", [], turn.Intent.Envelope.ProviderBudget, turn.Intent.Envelope.GroundingPolicy), turn.Intent.Profile, turn.Intent.Budget, turn.Intent.DeadlineUtc);
        Assert.Equal(ChatStoreOutcome.Conflict, (await f.Writer.StartAsync(f.Actor, f.Key, changed)).Outcome);
        var other = new ChatConversationKey(f.Key.PinnedRevision, f.OtherActor.PrincipalId, f.Key.ConversationId);
        await f.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='administrator',version=version+1 WHERE principal_id=$1", f.OtherActor.PrincipalId.Value);
        Assert.Null(await f.Reader.ReadAsync(f.OtherActor, f.Key));
        Assert.Null(await f.Reader.ReadAsync(f.OtherActor, other));
        Assert.Null(await f.Reader.ReadTurnAsync(f.OtherActor, other, turn.Intent.TurnId));
        Assert.Empty(await f.Reader.ReadEventsAsync(f.OtherActor, other, turn.Intent.TurnId, 0, 100));
        await using var reopened = new PostgresChatDatabase(f.Case.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        var reader = new PostgresChatStore(reopened);
        Assert.True(await reader.ReadAsync(f.Actor, f.Key) == f.Conversation, "Exact private conversation pins changed.");
        var persisted = await reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId);
        Assert.True(persisted == turn, "Exact original private turn facts changed.");
        await f.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", f.Actor.PrincipalId.Value);
        Assert.Null(await reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
        Assert.Equal(ChatStoreOutcome.NotAuthorizedOrNotFound, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Cancelled)).Outcome);
    }

    [PostgresFact]
    public async Task Two_rounds_have_distinct_fences_ordered_events_and_cas_terminal_publication()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(); var first = PostgresChatTestSupport.Round();
        var reserved = await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, first);
        Assert.Equal(ChatStoreOutcome.Applied, reserved.Outcome); turn = Assert.IsType<ChatTurnFacts>(reserved.Turn);
        Assert.Equal(ChatStoreOutcome.Replayed, (await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, 1, first)).Outcome);
        Assert.Equal(ChatStoreOutcome.InvalidState, (await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, PostgresChatTestSupport.Round())).Outcome);
        var data = new ChatEventData(Guid.NewGuid(), ChatEventKind.TextDelta, "bounded private delta");
        var appended = await f.Writer.AppendAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, first.RoundId, first.Fence, data);
        Assert.Equal(ChatStoreOutcome.Applied, appended.Outcome); turn = Assert.IsType<ChatTurnFacts>(appended.Facts!);
        Assert.Equal(ChatStoreOutcome.Replayed, (await f.Writer.AppendAsync(f.Actor, f.Key, turn.Intent.TurnId, 1, first.RoundId, first.Fence, data)).Outcome);
        var completion = await f.Writer.CompleteRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, first.RoundId, first.Fence, ChatRoundOutcome.ToolCalls);
        Assert.Equal(ChatStoreOutcome.Applied, completion.Outcome); turn = Assert.IsType<ChatTurnFacts>(completion.Facts!);
        var second = PostgresChatTestSupport.Round(first.InstanceId);
        reserved = await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, second);
        Assert.Equal(ChatStoreOutcome.Applied, reserved.Outcome); turn = Assert.IsType<ChatTurnFacts>(reserved.Turn);
        Assert.Equal(2, reserved.Round!.Ordinal);
        Assert.Equal(ChatStoreOutcome.InvalidState, (await f.Writer.AppendAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, first.RoundId, first.Fence, new(Guid.NewGuid(), ChatEventKind.TextDelta, "late"))).Outcome);
        completion = await f.Writer.CompleteRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, second.RoundId, second.Fence, ChatRoundOutcome.Stopped);
        Assert.Equal(ChatStoreOutcome.Applied, completion.Outcome); turn = Assert.IsType<ChatTurnFacts>(completion.Facts!);
        var terminal = await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Completed);
        Assert.Equal(ChatStoreOutcome.Applied, terminal.Outcome);
        Assert.Equal(ChatStoreOutcome.Conflict, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Cancelled)).Outcome);
        var events = await f.Reader.ReadEventsAsync(f.Actor, f.Key, turn.Intent.TurnId, 0, 100);
        Assert.Equal(new long[] { 1, 2, 3 }, events.Select(e => e.Sequence));
        Assert.Equal(new[] { ChatEventKind.Started, ChatEventKind.TextDelta, ChatEventKind.Completed }, events.Select(e => e.Kind));
        Assert.Single(await f.Reader.ReadEventsAsync(f.Actor, f.Key, turn.Intent.TurnId, 2, 1));
    }

    [PostgresFact]
    public async Task Concurrent_reservations_have_one_dispatch_permit_and_cancellation_preserves_uncertainty()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(); var round = PostgresChatTestSupport.Round();
        await using var independent = new PostgresChatDatabase(f.Case.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
        var second = new PostgresChatStore(independent);
        var results = await Task.WhenAll(f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round),
            second.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round));
        Assert.Single(results, result => result.Outcome == ChatStoreOutcome.Applied);
        Assert.Single(results, result => result.Outcome == ChatStoreOutcome.Replayed);
        turn = Assert.IsType<ChatTurnFacts>(await f.Reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Cancelled)).Outcome);
        Assert.Equal(ChatRoundOutcome.Uncertain, (await f.Reader.ReadRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, round.RoundId))!.Outcome);
        var cancelled = Assert.IsType<ChatTurnFacts>(await f.Reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
        Assert.Equal(ChatStoreOutcome.InvalidState, (await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, cancelled.Version, PostgresChatTestSupport.Round())).Outcome);
        Assert.Equal(ChatStoreOutcome.InvalidState, (await f.Writer.CompleteRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, cancelled.Version, round.RoundId, round.Fence, ChatRoundOutcome.Stopped)).Outcome);
    }

    [PostgresFact]
    public async Task Restricted_role_grants_exclude_parent_writes_payloads_and_other_readers()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        foreach (var role in new[] { "weft_chat_reader", "weft_chat_writer" })
        {
            Assert.False(await f.Case.ScalarAsync<bool>("SELECT has_table_privilege($1,'weft.principals','UPDATE,DELETE,INSERT,TRUNCATE') OR has_any_column_privilege($1,'weft.principals','UPDATE')", role));
            Assert.False(await f.Case.ScalarAsync<bool>("SELECT has_table_privilege($1,'weft.mapping_revisions','UPDATE,DELETE,INSERT,TRUNCATE') OR has_any_column_privilege($1,'weft.mapping_revisions','SELECT,UPDATE')", role));
        }
        foreach (var role in new[] { "weft_runtime", "weft_catalog_reader", "weft_report_reader", "weft_session_reader" })
            Assert.False(await f.Case.ScalarAsync<bool>("SELECT has_table_privilege($1,'weft.chat_events','SELECT') OR has_any_column_privilege($1,'weft.chat_events','SELECT')", role));
        Assert.False(await f.Case.ScalarAsync<bool>("SELECT has_table_privilege('weft_chat_reader','weft.chat_turns','INSERT,UPDATE,DELETE,TRUNCATE') OR has_any_column_privilege('weft_chat_reader','weft.chat_turns','UPDATE')"));
        Assert.False(await f.Case.ScalarAsync<bool>("SELECT has_table_privilege('weft_chat_writer','weft.chat_events','UPDATE,DELETE,TRUNCATE') OR has_any_column_privilege('weft_chat_writer','weft.chat_events','UPDATE')"));
    }

    [PostgresFact]
    public async Task Fresh_authority_after_actual_conversation_lock_wait_refuses_revoked_dispatch()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(); var round = PostgresChatTestSupport.Round();
        await using var owner = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(f.Case.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await owner.OpenAsync(); await using var held = await owner.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand("SELECT conversation_id FROM weft.chat_conversations WHERE conversation_id=$1 FOR UPDATE", owner, held))
        { locked.Parameters.AddWithValue(f.Key.ConversationId); await locked.ExecuteScalarAsync(); }
        const string application = "weft-chat-lock-wait";
        var routed = new NpgsqlConnectionStringBuilder(f.Case.ChatWriterConnectionString) { ApplicationName = application }.ConnectionString;
        await using var database = new PostgresChatDatabase(routed, ChatDatabaseProfile.Writer);
        var writer = new PostgresChatStore(database);
        await using var observer = await f.Case.Owner.OpenConnectionAsync();
        await using var probe = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, owner.ProcessID);
        Task<ChatRoundWriteResult>? operation = null;
        var released = false;
        try
        {
            operation = writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round);
            await PostgresServerSessionTestSupport.WaitBlockedAsync(probe, operation);
            // Separate connection: observer is held only for pg_blocking_pids, never a timing-based sleep.
            await using (var revoke = new NpgsqlCommand("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", owner, held))
            { revoke.Parameters.AddWithValue(f.Actor.PrincipalId.Value); await revoke.ExecuteNonQueryAsync(); }
            await held.CommitAsync(); released = true;
            Assert.Equal(ChatStoreOutcome.NotAuthorizedOrNotFound, (await operation).Outcome);
        }
        finally
        {
            if (!released) await held.RollbackAsync();
            if (operation is not null) await PostgresServerSessionTestSupport.SettleAsync(operation);
        }
    }

    [PostgresFact]
    public async Task Real_commit_acknowledgement_loss_does_not_reissue_a_potentially_billed_round()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(); var round = PostgresChatTestSupport.Round();
        await using var relay = new ServerSessionCommitAckLossRelay(f.Case.ChatWriterConnectionString);
        await using var routed = new PostgresChatDatabase(relay.RoutedConnectionString, ChatDatabaseProfile.Writer);
        var writer = new PostgresChatStore(routed);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var operation = writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round, timeout.Token);
        try
        {
            await Task.WhenAny(operation, relay.CommitAcknowledgementWithheld).WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
            Assert.True(relay.CommitAcknowledgementWithheld.IsCompletedSuccessfully && !operation.IsCompleted, "A real commit acknowledgement was not withheld.");
            Assert.True(relay.FrontendCommitSeen && relay.BackendCommitCompleteSeen && relay.BackendIdleReadySeen);
            var observed = await f.Reader.ReadRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, round.RoundId, timeout.Token);
            Assert.True(observed?.Intent == round && observed.Outcome == ChatRoundOutcome.Uncertain, "An exact uncertain reservation was not durable before acknowledgement loss.");
            relay.DropAcknowledgement();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await operation.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token));
            Assert.Null(error.InnerException);
            Assert.False(timeout.IsCancellationRequested);
            var replay = await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round, timeout.Token);
            Assert.Equal(ChatStoreOutcome.Replayed, replay.Outcome); // Observation only: no dispatch permission.
            var current = Assert.IsType<ChatTurnFacts>(await f.Reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
            Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, current.Version, ChatTurnPhase.Interrupted)).Outcome);
        }
        finally
        {
            relay.DropAcknowledgement();
            await PostgresServerSessionTestSupport.SettleAsync(operation);
        }
    }

    [PostgresFact]
    public async Task Same_volume_restart_keeps_uncertain_dispatch_and_never_grants_a_new_permit()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(); var round = PostgresChatTestSupport.Round();
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round)).Outcome);
        await fixture.RestartAsync();
        await using var reopened = new PostgresChatDatabase(f.Case.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
        var writer = new PostgresChatStore(reopened);
        var replay = await writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round);
        Assert.Equal(ChatStoreOutcome.Replayed, replay.Outcome);
        Assert.True(replay.Round?.Intent == round && replay.Round.Outcome == ChatRoundOutcome.Uncertain, "Restart changed the exact private dispatch facts.");
    }
}
