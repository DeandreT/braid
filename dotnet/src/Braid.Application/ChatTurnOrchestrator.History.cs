using System.Text;
using Braid.Ai;
using Braid.Domain;

namespace Braid.Application;

public sealed partial class ChatTurnOrchestrator
{
    private async Task AddHistoryAsync(State s, IReadOnlyList<Guid> history)
    {
        int scanned = 0, bytes = 0;
        var omitted = false;
        DateTimeOffset? lastFinished = null;
        foreach (var id in history)
        {
            CheckTime(s.Execution, s.Facts.Intent);
            var turn = await _turns.ReadTurnAsync(s.Actor, s.Conversation.Key, id, s.Execution.Token).ConfigureAwait(false);
            if (turn is null || turn.Intent.TurnId != id || turn.Phase != ChatTurnPhase.Completed || turn.FinishedAtUtc is null ||
                turn.FinishedAtUtc > s.Facts.CreatedAtUtc || lastFinished is { } prior && turn.CreatedAtUtc < prior)
                throw Stop(ChatOrchestrationOutcome.InvalidRequest);
            lastFinished = turn.FinishedAtUtc;
            if (_contextPort?.IncludeHistory(turn.Intent) == false)
            {
                if (!omitted)
                {
                    s.Messages.Add(AiUserMessage.Create("Some earlier turns are not included because they may contain sample data. " +
                        "Use the currently offered tools to read current sources again if needed."));
                    CheckContext(s.Messages);
                    omitted = true;
                }
                continue;
            }
            // Bound the full scan before loading any event page; no partially truncated history.
            if (turn.EventCount > _limits.MaxHistoryEvents - scanned) throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
            scanned += turn.EventCount;
            bytes = AddHistoryBytes(bytes, Encoding.UTF8.GetByteCount(turn.Intent.OriginalRequest));
            var deltas = new List<(Guid Round, string Text)>();
            ChatStoredEvent? final = null;
            long sequence = 0;
            while (sequence < turn.EventCount)
            {
                CheckTime(s.Execution, s.Facts.Intent);
                var page = await _turns.ReadEventsAsync(s.Actor, s.Conversation.Key, id, sequence,
                    Math.Min(100, turn.EventCount - (int)sequence), s.Execution.Token).ConfigureAwait(false);
                if (page.Count == 0 || page.Count > Math.Min(100, turn.EventCount - sequence)) throw Stop(ChatOrchestrationOutcome.Interrupted);
                foreach (var item in page)
                {
                    if (item.Sequence != sequence + 1 || item.Sequence > turn.EventCount) throw Stop(ChatOrchestrationOutcome.Interrupted);
                    sequence = item.Sequence;
                    if (item.Kind == ChatEventKind.TextDelta)
                    {
                        if (item.RoundId is not { } round) throw Stop(ChatOrchestrationOutcome.InvalidRequest);
                        var text = ChatOrchestrationEventCodec.ReadText(item.Text);
                        // Count all loaded private text, including intermediate rounds not selected for history.
                        bytes = AddHistoryBytes(bytes, Encoding.UTF8.GetByteCount(text));
                        deltas.Add((round, text));
                    }
                    if (item.Sequence == turn.EventCount) final = item;
                }
            }
            if (final is null || final.Kind != ChatEventKind.Completed || final.TerminalMetadata is not
                { FinishReason: ChatProviderFinishReason.Stop, Usage: { } usage, Failure: null })
                throw Stop(ChatOrchestrationOutcome.InvalidRequest);
            var roundFacts = await _turns.ReadRoundAsync(s.Actor, s.Conversation.Key, id, usage.RoundId, s.Execution.Token).ConfigureAwait(false);
            if (roundFacts is null || roundFacts.Outcome != ChatRoundOutcome.Stopped || roundFacts.Ordinal != turn.RoundCount ||
                roundFacts.Metadata != final.TerminalMetadata) throw Stop(ChatOrchestrationOutcome.Interrupted);
            var answer = new StringBuilder();
            foreach (var delta in deltas) if (delta.Round == usage.RoundId) answer.Append(delta.Text);
            // Empty Stop has no useful assistant history and is omitted as a complete exchange.
            if (answer.Length > 0)
            {
                s.Messages.Add(AiUserMessage.Create(turn.Intent.OriginalRequest));
                s.Messages.Add(AiAssistantMessage.Create(answer.ToString(), []));
                CheckContext(s.Messages);
            }
        }
    }

    private int AddHistoryBytes(int current, int addition)
    {
        if (addition > _limits.MaxHistoryBytes - current) throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
        return current + addition;
    }
}
