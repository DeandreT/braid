using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresChatSemanticSuccessorTests(PostgresFixture fixture)
{
    private static Task WaitExpiredAsync(PostgresChatTestSupport f) => WaitUntilAsync(f, f.Conversation.RetainUntilUtc);
    private static async Task WaitUntilAsync(PostgresChatTestSupport f, DateTimeOffset instant)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (new DateTimeOffset(await f.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()")) < instant)
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
    }

    [PostgresFact]
    public async Task Erasure_keeps_no_content_but_rejects_every_old_identity_after_new_conversation_admission()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture, TimeSpan.FromSeconds(10));
        var turn = await f.StartAsync(); var round = PostgresChatTestSupport.Round();
        var reserved = await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round);
        Assert.Equal(ChatStoreOutcome.Applied, reserved.Outcome);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, reserved.Turn!.Version, ChatTurnPhase.Interrupted)).Outcome);
        await WaitExpiredAsync(f);
        var policyMinimum = new DateTimeOffset(await f.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()")).AddSeconds(5);
        var policy = new ChatErasurePolicy("fixture-retirement", 1, policyMinimum); // Fixture only, not production policy.
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.DeleteExpiredAsync(f.Actor, f.Key, policy));
        Assert.Equal(4L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.chat_identities WHERE erased_at IS NOT NULL"));
        var columns = await f.Case.ScalarAsync<string[]>("SELECT array_agg(attname::text ORDER BY attnum) FROM pg_attribute WHERE attrelid='braid.chat_identities'::regclass AND attnum>0 AND NOT attisdropped");
        Assert.Equal(new[] { "organization_id", "creator_id", "identity_kind", "identity_id", "conversation_id", "turn_id", "erased_at", "retirement_policy_id", "retirement_policy_version", "retirement_not_before" }, columns);
        foreach (var table in new[] { "chat_conversations", "chat_turns", "chat_rounds", "chat_events" })
            Assert.Equal(0L, await f.Case.ScalarAsync<long>($"SELECT count(*) FROM braid.{table}")); // Fixed fixture-owned table set.
        await WaitUntilAsync(f, policyMinimum); // Policy time passing never releases replay protection.
        var now = new DateTimeOffset(await f.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
        var clone = new ChatConversationIntent(f.Key, f.Conversation.PinnedProjection, f.Conversation.PinnedValidation, now.AddHours(1));
        Assert.Equal(ChatStoreOutcome.IdentityRetired, await f.Writer.CreateAsync(f.Actor, clone));
        var fresh = new ChatConversationIntent(new(f.Key.PinnedRevision, f.Actor.PrincipalId, Guid.NewGuid()), clone.PinnedProjection, clone.PinnedValidation, clone.RetainUntilUtc);
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.CreateAsync(f.Actor, fresh));
        var prototype = new ChatTurnIntent(Guid.NewGuid(), new ChatOriginalEnvelope(Guid.NewGuid(), "new request", [], ChatTurnPolicyTests.ProviderBudget(), ChatTurnPolicyTests.Grounding()), ChatTurnPolicyTests.Profile(), new(16, 65536, 3), now.AddMinutes(1));
        var reusedTurn = new ChatTurnIntent(turn.Intent.TurnId, prototype.Envelope, prototype.Profile, prototype.Budget, prototype.DeadlineUtc);
        Assert.Equal(ChatStoreOutcome.IdentityRetired, (await f.Writer.StartAsync(f.Actor, fresh.Key, reusedTurn)).Outcome);
        var reusedRequest = new ChatTurnIntent(Guid.NewGuid(), new ChatOriginalEnvelope(turn.Intent.Envelope.RequestId, "changed", [], prototype.Envelope.ProviderBudget, prototype.Envelope.GroundingPolicy), prototype.Profile, prototype.Budget, prototype.DeadlineUtc);
        Assert.Equal(ChatStoreOutcome.IdentityRetired, (await f.Writer.StartAsync(f.Actor, fresh.Key, reusedRequest)).Outcome);
        var started = await f.Writer.StartAsync(f.Actor, fresh.Key, prototype);
        Assert.Equal(ChatStoreOutcome.Applied, started.Outcome);
        var reusedRound = new ChatRoundIntent(round.RoundId, Guid.NewGuid(), Guid.NewGuid(), "changed round body");
        Assert.Equal(ChatStoreOutcome.IdentityRetired, (await f.Writer.ReserveRoundAsync(f.Actor, fresh.Key, prototype.TurnId, started.Facts!.Version, reusedRound)).Outcome);
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.chat_rounds"));
    }

    [PostgresFact]
    public async Task Revoked_owner_has_no_content_access_but_exact_trusted_lifecycle_can_interrupt_then_erase()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture, TimeSpan.FromSeconds(10));
        var turn = await f.StartAsync(); var round = PostgresChatTestSupport.Round();
        var reserved = await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round);
        Assert.Equal(ChatStoreOutcome.Applied, reserved.Outcome);
        await f.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", f.Actor.PrincipalId.Value);
        Assert.Null(await f.Reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
        Assert.Equal(ChatStoreOutcome.NotAuthorizedOrNotFound, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, reserved.Turn!.Version, ChatTurnPhase.Cancelled)).Outcome);
        // Property is supplied by the independent owned fixture/provisioning lane; no operational source is duplicated here.
        await using var database = new PostgresChatLifecycleDatabase(f.Case.ChatLifecycleConnectionString);
        var lifecycle = new PostgresChatLifecycleStore(database);
        var observed = Assert.IsType<ChatLifecycleObservation>(await lifecycle.ObserveAsync(f.Key, turn.Intent.TurnId));
        Assert.Equal(ChatTurnPhase.Active, observed.Phase);
        Assert.Equal(ChatStoreOutcome.Conflict, await lifecycle.CloseAsync(f.Key, turn.Intent.TurnId, observed.Version + 1, ChatTurnPhase.Interrupted));
        var usage = new ChatTerminalMetadata(new(round.RoundId, ChatUsageOrigin.ProviderRoundTerminal, null, 7, null), new(round.RoundId, ChatProviderFailureCode.Cancelled, ChatDispatchObservation.Attempted));
        Assert.Equal(ChatStoreOutcome.Applied, await lifecycle.CloseWithMetadataAsync(f.Key, turn.Intent.TurnId, observed.Version, ChatTurnPhase.Interrupted, usage));
        Assert.Equal(ChatRoundOutcome.Uncertain, (ChatRoundOutcome)await f.Case.ScalarAsync<int>("SELECT outcome FROM braid.chat_rounds WHERE round_id=$1", round.RoundId));
        await WaitExpiredAsync(f);
        Assert.Equal(ChatStoreOutcome.Applied, await lifecycle.EraseExpiredAsync(f.Key, f.ErasurePolicy));
        Assert.Null(await lifecycle.ObserveAsync(f.Key, turn.Intent.TurnId));
        Assert.Equal(4L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.chat_identities WHERE erased_at IS NOT NULL"));
    }

    [PostgresFact]
    public async Task Reconnect_restores_optional_usage_closed_failure_and_exact_verified_citation_metadata_in_order()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(); var round = PostgresChatTestSupport.Round();
        var reserved = await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round);
        turn = Assert.IsType<ChatTurnFacts>(reserved.Turn);
        var unknown = new ChatUsageFact(round.RoundId, ChatUsageOrigin.ProviderObservation, null, null, null);
        var known = new ChatUsageFact(round.RoundId, ChatUsageOrigin.ProviderObservation, 0, 7, null);
        foreach (var usage in new[] { unknown, known })
        {
            var result = await f.Writer.AppendAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round.RoundId, round.Fence, ChatEventData.ForUsage(Guid.NewGuid(), usage));
            Assert.Equal(ChatStoreOutcome.Applied, result.Outcome); turn = Assert.IsType<ChatTurnFacts>(result.Facts!);
        }
        var citation = ChatValidatedCitation.Validate(f.Conversation, f.VerifiedValidation, new(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 0));
        var appended = await f.Writer.AppendCitationAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round.RoundId, round.Fence, new(Guid.NewGuid(), citation));
        Assert.Equal(ChatStoreOutcome.Applied, appended.Outcome); turn = Assert.IsType<ChatTurnFacts>(appended.Facts!);
        var terminal = new ChatTerminalMetadata(new(round.RoundId, ChatUsageOrigin.ProviderRoundTerminal, 0, 7, null), new(round.RoundId, ChatProviderFailureCode.TruncatedResponse, ChatDispatchObservation.Attempted));
        var completion = await f.Writer.CompleteRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round.RoundId, round.Fence, new ChatRoundCompletion(ChatRoundOutcome.Failed, terminal));
        Assert.Equal(ChatStoreOutcome.Applied, completion.Outcome); turn = Assert.IsType<ChatTurnFacts>(completion.Facts!);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Failed)).Outcome);
        await using var reopened = new PostgresChatDatabase(f.Case.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        var events = await new PostgresChatStore(reopened).ReadEventsAsync(f.Actor, f.Key, turn.Intent.TurnId, 0, 100);
        Assert.Equal(new[] { ChatEventKind.Started, ChatEventKind.Usage, ChatEventKind.Usage, ChatEventKind.Citation, ChatEventKind.RoundCompleted, ChatEventKind.Failed }, events.Select(e => e.Kind));
        Assert.False(events[1].Usage!.IsKnown); Assert.Null(events[1].Usage!.TotalTokens);
        Assert.True(events[2].Usage!.IsKnown); Assert.Equal(0L, events[2].Usage!.InputTokens); Assert.Null(events[2].Usage!.TotalTokens);
        Assert.Equal(f.Conversation.PinnedValidation, events[3].Citation!.Report); Assert.Equal(0, events[3].Citation!.Target.DiagnosticOrdinal);
        Assert.Equal(terminal, events[^1].TerminalMetadata); Assert.Equal(terminal, events[^2].TerminalMetadata);
    }

    [PostgresFact]
    public async Task Exhausted_content_budget_still_preserves_failure_accounting_without_reclassifying_uncertain_dispatch()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(await f.IntentAsync(budget: new(2, 1, 1))); var round = PostgresChatTestSupport.Round();
        var reserved = await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round);
        turn = Assert.IsType<ChatTurnFacts>(reserved.Turn);
        var metadata = new ChatTerminalMetadata(new(round.RoundId, ChatUsageOrigin.ProviderRoundTerminal, null, null, null), new(round.RoundId, ChatProviderFailureCode.BudgetExceeded, ChatDispatchObservation.Attempted));
        Assert.Equal(ChatStoreOutcome.LimitReached, (await f.Writer.CompleteRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round.RoundId, round.Fence, new ChatRoundCompletion(ChatRoundOutcome.Failed, metadata))).Outcome);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishWithMetadataAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Failed, metadata)).Outcome);
        var events = await f.Reader.ReadEventsAsync(f.Actor, f.Key, turn.Intent.TurnId, 0, 100);
        Assert.Equal(2, events.Count); Assert.Equal(metadata, events[^1].TerminalMetadata);
        Assert.Equal(ChatRoundOutcome.Uncertain, (await f.Reader.ReadRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, round.RoundId))!.Outcome);
    }
}
