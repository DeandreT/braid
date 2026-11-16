using Braid.Domain;

namespace Braid.Application;

/// <summary>Deterministic persistence admission. It never authorizes a provider or tool call.</summary>
public static class ChatTurnPolicy
{
    public static ChatStoreOutcome Advance(ChatTurnFacts turn, long expectedVersion, DateTimeOffset observedAtUtc, bool terminal = false)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ChatBounds.Instant(observedAtUtc);
        if (expectedVersion < 1) throw ChatBounds.Invalid();
        if (turn.Version != expectedVersion) return ChatStoreOutcome.Conflict;
        if (turn.Phase != ChatTurnPhase.Active) return ChatStoreOutcome.InvalidState;
        if (turn.Version == long.MaxValue) return ChatStoreOutcome.LimitReached;
        if (observedAtUtc < turn.CreatedAtUtc) throw ChatBounds.Invalid();
        // Cancellation/interruption/failed publication must remain possible after the deadline.
        return !terminal && observedAtUtc >= turn.Intent.DeadlineUtc ? ChatStoreOutcome.Expired : ChatStoreOutcome.Applied;
    }

    public static ChatStoreOutcome Reserve(ChatTurnFacts turn, ChatRoundFacts? lastRound, long version, DateTimeOffset now, Guid instanceId)
    {
        ChatBounds.Id(instanceId);
        var advance = Advance(turn, version, now);
        if (advance != ChatStoreOutcome.Applied) return advance;
        RequireLast(turn, lastRound);
        if (lastRound is not null && lastRound.Intent.InstanceId != instanceId) return ChatStoreOutcome.InvalidState;
        if (turn.RoundCount >= turn.Intent.Budget.MaxRounds) return ChatStoreOutcome.LimitReached;
        // Uncertain includes dispatch reservation, transport attempted, and output started.
        // There is deliberately no takeover, lease expiry, resend or retry transition.
        return lastRound is null || lastRound.Outcome == ChatRoundOutcome.ToolCalls
            ? ChatStoreOutcome.Applied : ChatStoreOutcome.InvalidState;
    }

    public static ChatStoreOutcome Append(ChatTurnFacts turn, ChatRoundFacts round, long version,
        Guid fence, ChatEventData data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);
        return AppendBytes(turn, round, version, fence, data.ByteCount, now);
    }
    public static ChatStoreOutcome AppendBytes(ChatTurnFacts turn, ChatRoundFacts round, long version,
        Guid fence, int byteCount, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (byteCount is < 1 or > 8192) throw ChatBounds.Invalid();
        ChatBounds.Id(fence);
        var advance = Advance(turn, version, now);
        if (advance != ChatStoreOutcome.Applied) return advance;
        RequireLast(turn, round);
        if (round.Outcome != ChatRoundOutcome.Uncertain || round.Intent.Fence != fence) return ChatStoreOutcome.InvalidState;
        // Preserve one event slot for the zero-content terminal; budget exhaustion cannot strand it.
        return turn.EventCount >= turn.Intent.Budget.MaxEvents - 1 || byteCount > turn.Intent.Budget.MaxEventBytes - turn.EventBytes
            ? ChatStoreOutcome.LimitReached : ChatStoreOutcome.Applied;
    }

    public static ChatStoreOutcome CompleteRound(ChatTurnFacts turn, ChatRoundFacts round, long version,
        Guid fence, ChatRoundOutcome outcome, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (outcome is not (ChatRoundOutcome.ToolCalls or ChatRoundOutcome.Stopped or ChatRoundOutcome.Failed)) throw ChatBounds.Invalid();
        ChatBounds.Id(fence);
        var advance = Advance(turn, version, now);
        if (advance != ChatStoreOutcome.Applied) return advance;
        RequireLast(turn, round);
        return round.Intent.Fence == fence && round.Outcome == ChatRoundOutcome.Uncertain
            ? ChatStoreOutcome.Applied : ChatStoreOutcome.InvalidState;
    }

    public static ChatStoreOutcome Finish(ChatTurnFacts turn, ChatRoundFacts? lastRound, long version,
        ChatTurnPhase phase, DateTimeOffset now)
    {
        if (phase is not (ChatTurnPhase.Completed or ChatTurnPhase.Failed or ChatTurnPhase.Cancelled or ChatTurnPhase.Interrupted))
            throw ChatBounds.Invalid();
        var advance = Advance(turn, version, now, terminal: phase != ChatTurnPhase.Completed);
        if (advance != ChatStoreOutcome.Applied) return advance;
        RequireLast(turn, lastRound);
        if (phase == ChatTurnPhase.Completed && lastRound?.Outcome != ChatRoundOutcome.Stopped) return ChatStoreOutcome.InvalidState;
        // Failed/cancelled/interrupted do not turn an uncertain billed round into a safe retry.
        return ChatStoreOutcome.Applied;
    }

    private static void RequireLast(ChatTurnFacts turn, ChatRoundFacts? last)
    {
        if ((turn.RoundCount == 0) != (last is null) || last is not null && last.Ordinal != turn.RoundCount) throw ChatBounds.Invalid();
    }
}
