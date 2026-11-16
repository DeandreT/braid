using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed class ChatTurnPolicyTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    internal static ChatProfilePin Profile(int version = 1) => new("fixture", version, "fixture-provider", "fixture-model", "mapping-metadata", 1, Sha256Digest.FromBytes([1, 2, 3]));
    internal static ChatProviderBudget ProviderBudget() => new(600_000_000, 3, 1048576, 4194304, 131072, 8, 2048);
    internal static ChatGroundingPolicyPin Grounding() => ChatAiBridge.PinGroundingPolicy("fixture", 1, "trusted", [], 64, 8192, 3);
    internal static ChatTurnIntent Intent(Guid? id = null, ChatTurnBudget? budget = null, string original = "private original")
    {
        var turnId = id ?? Guid.NewGuid();
        return new(turnId, new ChatOriginalEnvelope(turnId, original, [], ProviderBudget(), Grounding()), Profile(), budget ?? new(8, 1000, 3), Now.AddMinutes(1));
    }
    internal static ChatTurnFacts Turn(ChatTurnIntent? intent = null, long version = 1, int events = 1, int bytes = 0, int rounds = 0,
        ChatTurnPhase phase = ChatTurnPhase.Active) => new(intent ?? Intent(), phase, version, events, bytes, rounds, Now, phase == ChatTurnPhase.Active ? null : Now.AddSeconds(1));
    internal static ChatRoundFacts Round(int ordinal = 1, ChatRoundOutcome outcome = ChatRoundOutcome.Uncertain) =>
        new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "exact private round request"), ordinal, outcome);

    [Fact]
    public void Utf8_is_strict_bounded_and_private_in_all_default_diagnostics()
    {
        var input = Intent(original: new string('é', 32768));
        Assert.Equal(65536, System.Text.Encoding.UTF8.GetByteCount(input.OriginalRequest));
        foreach (var invalid in new[] { new string('é', 32769), "\ud800", "\0secret", " \t" })
        {
            var error = Assert.Throws<ArgumentException>(() => Intent(original: invalid));
            Assert.Equal("Invalid chat facts.", error.Message);
            Assert.Null(error.InnerException);
        }
        Assert.Equal(nameof(ChatTurnIntent), input.ToString());
        Assert.Equal(nameof(ChatProfilePin), input.Profile.ToString());
        Assert.Equal(nameof(ChatRoundFacts), Round().ToString());
        Assert.Equal(nameof(ChatEventData), new ChatEventData(Guid.NewGuid(), ChatEventKind.TextDelta, "secret").ToString());
        Assert.Throws<ArgumentException>(() => ChatBounds.Instant(Now.AddTicks(1)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Only_fully_valid_tool_calls_finish_allows_a_new_round(int outcome)
    {
        var round = Round(outcome: (ChatRoundOutcome)outcome);
        var result = ChatTurnPolicy.Reserve(Turn(rounds: 1), round, 1, Now, round.Intent.InstanceId);
        Assert.Equal(outcome == (int)ChatRoundOutcome.ToolCalls ? ChatStoreOutcome.Applied : ChatStoreOutcome.InvalidState, result);
    }

    [Fact]
    public void Uncertain_dispatch_never_becomes_retriable_with_time_or_terminal_state()
    {
        var round = Round();
        var turn = Turn(rounds: 1);
        Assert.Equal(ChatStoreOutcome.InvalidState, ChatTurnPolicy.Reserve(turn, round, 1, Now.AddSeconds(1), round.Intent.InstanceId));
        Assert.Equal(ChatStoreOutcome.Expired, ChatTurnPolicy.Reserve(turn, round, 1, Now.AddMinutes(1), round.Intent.InstanceId));
        Assert.Equal(ChatStoreOutcome.Applied, ChatTurnPolicy.Finish(turn, round, 1, ChatTurnPhase.Interrupted, Now.AddHours(1)));
        Assert.Equal(ChatStoreOutcome.InvalidState, ChatTurnPolicy.Reserve(Turn(rounds: 1, phase: ChatTurnPhase.Interrupted), round, 1, Now.AddHours(1), round.Intent.InstanceId));
        Assert.Equal(ChatRoundOutcome.Uncertain, round.Outcome);
    }

    [Fact]
    public void Ordered_output_fence_and_terminal_slot_are_enforced()
    {
        var round = Round();
        var data = new ChatEventData(Guid.NewGuid(), ChatEventKind.TextDelta, "é");
        var intent = Intent(budget: new(3, 2, 1));
        Assert.Equal(ChatStoreOutcome.Applied, ChatTurnPolicy.Append(Turn(intent, rounds: 1), round, 1, round.Intent.Fence, data, Now));
        Assert.Equal(ChatStoreOutcome.InvalidState, ChatTurnPolicy.Append(Turn(intent, rounds: 1), round, 1, Guid.NewGuid(), data, Now));
        Assert.Equal(ChatStoreOutcome.LimitReached, ChatTurnPolicy.Append(Turn(intent, events: 2, bytes: 2, rounds: 1), round, 1, round.Intent.Fence, data, Now));
        Assert.Equal(ChatStoreOutcome.Applied, ChatTurnPolicy.Finish(Turn(intent, events: 2, bytes: 2, rounds: 1), round, 1, ChatTurnPhase.Cancelled, Now));
        Assert.Equal(ChatStoreOutcome.LimitReached, ChatTurnPolicy.Advance(Turn(version: long.MaxValue), long.MaxValue, Now));
    }

    [Fact]
    public void Terminal_and_round_completion_are_conditional_and_cannot_reopen()
    {
        var round = Round();
        Assert.Equal(ChatStoreOutcome.Conflict, ChatTurnPolicy.Finish(Turn(rounds: 1), round, 2, ChatTurnPhase.Cancelled, Now));
        Assert.Equal(ChatStoreOutcome.InvalidState, ChatTurnPolicy.Finish(Turn(rounds: 1), round, 1, ChatTurnPhase.Completed, Now));
        Assert.Equal(ChatStoreOutcome.Applied, ChatTurnPolicy.CompleteRound(Turn(rounds: 1), round, 1, round.Intent.Fence, ChatRoundOutcome.ToolCalls, Now));
        Assert.Equal(ChatStoreOutcome.InvalidState, ChatTurnPolicy.CompleteRound(Turn(rounds: 1, phase: ChatTurnPhase.Cancelled), round, 1, round.Intent.Fence, ChatRoundOutcome.Stopped, Now));
        Assert.Throws<ArgumentException>(() => ChatTurnPolicy.Reserve(Turn(rounds: 2), round, 1, Now, round.Intent.InstanceId));
        var continued = Round(outcome: ChatRoundOutcome.ToolCalls);
        Assert.Equal(ChatStoreOutcome.InvalidState, ChatTurnPolicy.Reserve(Turn(rounds: 1), continued, 1, Now, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => ChatTurnPolicy.CompleteRound(Turn(rounds: 1), round, 1, round.Intent.Fence, ChatRoundOutcome.Uncertain, Now));
    }

    [Fact]
    public void Idempotency_includes_exact_original_profile_and_absolute_deadline()
    {
        var id = Guid.NewGuid();
        var original = Intent(id);
        Assert.Equal(original, Intent(id));
        Assert.NotEqual(original, Intent(id, original: "different"));
        Assert.NotEqual(original, new ChatTurnIntent(id, original.Envelope, Profile(2), original.Budget, original.DeadlineUtc));
        Assert.NotEqual(original, new ChatTurnIntent(id, original.Envelope, original.Profile, original.Budget, original.DeadlineUtc.AddSeconds(1)));
    }
}
