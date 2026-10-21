using System.Text;
using Weft.Ai;
using Weft.Domain;

namespace Weft.Application;

public sealed partial class ChatTurnOrchestrator
{
    private sealed record RoundBatch(string Text, IReadOnlyList<AiToolIntent> Calls, AiRoundEvent Terminal)
    { public override string ToString() => nameof(RoundBatch); }

    private async Task<RoundBatch> ReadRoundAsync(State s, RoundOpened opened)
    {
        var calls = new List<AiToolIntent>();
        var text = new StringBuilder();
        AiRoundEvent? terminal = null;
        bool staged = false;
        var reserved = s.Round ?? throw Stop(ChatOrchestrationOutcome.Interrupted);
        try
        {
            if (opened.Ordinal != reserved.Ordinal) throw Stop(ChatOrchestrationOutcome.Interrupted);
            await foreach (var item in opened.Stream.ReadAllAsync(s.Execution.Token).ConfigureAwait(false))
            {
                if (terminal is not null) throw Stop(ChatOrchestrationOutcome.Interrupted);
                // Native cancellation may yield its categorical terminal; do not discard that usage/failure.
                if (item is AiRoundFinished or AiRoundFailed)
                {
                    terminal = item;
                    s.ObservedMetadata = ChatAiBridge.Completion(reserved.Intent.RoundId, item).Metadata;
                    continue;
                }
                CheckTime(s.Execution, s.Facts.Intent);
                switch (item)
                {
                    case AiTextDelta delta:
                        if (staged) throw Stop(ChatOrchestrationOutcome.Interrupted);
                        var bytes = Encoding.UTF8.GetByteCount(delta.Text);
                        if (bytes > s.Facts.Intent.Envelope.ProviderBudget.MaxTextBytes - s.OutputBytes)
                            throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
                        s.OutputBytes += bytes;
                        text.Append(delta.Text);
                        foreach (var piece in ChatOrchestrationEventCodec.TextPieces(delta.Text))
                            await AppendAsync(s, new(Guid.NewGuid(), ChatEventKind.TextDelta, piece)).ConfigureAwait(false);
                        break;
                    case AiUsageObserved usage:
                        await AppendAsync(s, ChatEventData.ForUsage(Guid.NewGuid(),
                            ChatAiBridge.Usage(reserved.Intent.RoundId, usage.Usage, ChatUsageOrigin.ProviderObservation))).ConfigureAwait(false);
                        break;
                    case AiToolIntentReady ready:
                        staged = true;
                        if (calls.Count == 8) throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
                        calls.Add(ready.Intent); // no validation, publication or execution before full terminal/cleanup
                        break;
                    default:
                        throw Stop(ChatOrchestrationOutcome.Interrupted);
                }
            }
            if (terminal is null) throw Stop(ChatOrchestrationOutcome.Interrupted);
            if (terminal is AiRoundFinished finished &&
                (finished.Reason == AiFinishReason.ToolCalls ? calls.Count == 0 : calls.Count != 0))
                throw Stop(ChatOrchestrationOutcome.Interrupted);
        }
        finally
        {
            // Enumeration's finally does not turn a faulted cleanup into success. Join original disposal explicitly.
            await s.Execution.JoinStreamAsync(opened.Stream).ConfigureAwait(false);
        }
        return new(text.ToString(), calls.AsReadOnly(), terminal!);
    }

    private async Task ExecuteToolsAsync(State s, RoundBatch batch)
    {
        var grounding = s.Facts.Intent.Envelope.GroundingPolicy;
        if (++s.ToolIterations > grounding.MaxToolIterations || s.Round!.Ordinal >= s.Facts.Intent.Budget.MaxRounds ||
            batch.Calls.Count > s.Facts.Intent.Envelope.ProviderBudget.MaxToolCalls - s.ToolCalls)
            throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
        var assistant = AiAssistantMessage.Create(batch.Text, batch.Calls);
        var definitions = new HashSet<string>(_tools.Select(t => t.Name), StringComparer.Ordinal);
        foreach (var call in batch.Calls)
            if (!definitions.Contains(call.Name) || !s.CallIds.Add(call.CallId)) throw Stop(ChatOrchestrationOutcome.InvalidRequest);

        // Validate the ENTIRE complete batch before any intent is admitted for execution.
        for (int i = 0; i < batch.Calls.Count; i++)
        {
            await RecheckAsync(s).ConfigureAwait(false);
            var context = ToolContext(s, i + 1);
            if (!await _toolPort.ValidateAsync(context, batch.Calls[i], s.Execution.Token).ConfigureAwait(false))
                throw Stop(ChatOrchestrationOutcome.InvalidRequest);
            CheckTime(s.Execution, s.Facts.Intent);
        }

        s.Messages.Add(assistant);
        CheckContext(s.Messages);
        for (int i = 0; i < batch.Calls.Count; i++)
        {
            var call = batch.Calls[i];
            await RecheckAsync(s).ConfigureAwait(false);
            if (s.ResultBytes >= grounding.MaxToolResultBytes)
                throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
            await AppendAsync(s, new(Guid.NewGuid(), ChatEventKind.ToolStarted, ToolEvent(call, i + 1))).ConfigureAwait(false);
            // Publication may wait; freshly check authority again after its acknowledgement.
            await RecheckAsync(s).ConfigureAwait(false);
            var context = ToolContext(s, i + 1);
            var result = await _toolPort.ExecuteAsync(context, call, s.Execution.Token).ConfigureAwait(false);
            // A late result after cancellation/revocation is never accepted into provider history.
            await RecheckAsync(s).ConfigureAwait(false);
            if (result is null || result.Result.ByteCount > grounding.MaxToolResultBytes - s.ResultBytes ||
                result.RetrievedNodes > grounding.MaxRetrievedNodes - s.RetrievedNodes)
                throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
            s.ResultBytes += result.Result.ByteCount; s.RetrievedNodes += result.RetrievedNodes; s.ToolCalls++;
            var message = AiToolMessage.Create(call.CallId, call.Name, result.Result);
            s.Messages.Add(message);
            CheckContext(s.Messages);
            await AppendAsync(s, new(Guid.NewGuid(), ChatEventKind.ToolCompleted,
                ToolEvent(call, i + 1, result.Result.ByteCount, result.RetrievedNodes))).ConfigureAwait(false);
            await DeliverCitationsAsync(s, result.Citations).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes a tool result's verified citation candidates, each once per turn, in candidate order, while the
    /// round still accepts appends. Only the trusted port verifies; model text and arguments never become citations.
    /// </summary>
    private async Task DeliverCitationsAsync(State s, IReadOnlyList<ChatCitationTarget> candidates)
    {
        if (_citationPort is null) return;
        var fresh = candidates.Where(candidate => !s.Cited.Contains(candidate)).Distinct().ToArray();
        if (fresh.Length == 0) return;
        CheckTime(s.Execution, s.Facts.Intent);
        var verified = await _citationPort.VerifyAsync(s.Actor, s.Conversation, fresh, s.Execution.Token).ConfigureAwait(false);
        // A verification is not authority for publication; recheck after it returns.
        await RecheckAsync(s).ConfigureAwait(false);
        var accepted = new Dictionary<ChatCitationTarget, ChatValidatedCitation>();
        foreach (var citation in verified ?? [])
            if (citation?.Facts is { } facts && facts.Revision == s.Conversation.Key.Revision && facts.Report == Pin(s.Conversation, facts.Target))
                accepted.TryAdd(facts.Target, citation);
        // Unverified or unrequested results are omitted, never repaired or reported to the browser.
        foreach (var candidate in fresh)
            if (accepted.TryGetValue(candidate, out var citation) && s.Cited.Add(candidate))
                await AppendCitationAsync(s, new(Guid.NewGuid(), citation)).ConfigureAwait(false);
    }

    private static ChatReportPin Pin(ChatConversationIntent conversation, ChatCitationTarget target) =>
        target.Kind is ChatCitationKind.ValidationDiagnostic or ChatCitationKind.ValidationReport
            ? conversation.Validation : conversation.Projection;

    private static ChatReadOnlyToolContext ToolContext(State s, int ordinal) => new(s.Actor, s.Conversation,
        s.Facts.Intent, s.Round!.Intent.RoundId, ordinal,
        s.Facts.Intent.Envelope.GroundingPolicy.MaxToolResultBytes - s.ResultBytes,
        s.Facts.Intent.Envelope.GroundingPolicy.MaxRetrievedNodes - s.RetrievedNodes);

    private static string ToolEvent(AiToolIntent call, int ordinal, int? resultBytes = null, int? nodes = null) => ChatCanonicalJson.Write(w =>
    {
        w.WriteStartObject(); w.WriteNumber("v", 1); w.WriteString("call", call.CallId); w.WriteString("name", call.Name);
        w.WriteNumber("ordinal", ordinal);
        if (resultBytes is { } bytes) w.WriteNumber("resultBytes", bytes);
        if (nodes is { } count) w.WriteNumber("retrievedNodes", count);
        w.WriteEndObject();
    }, 8192);

    private void CheckContext(IReadOnlyList<AiMessage> messages)
    {
        if (messages.Count > 64) throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
        long bytes = 0;
        foreach (var message in messages)
        {
            bytes += message switch
            {
                AiSystemMessage system => Encoding.UTF8.GetByteCount(system.Text),
                AiUserMessage user => Encoding.UTF8.GetByteCount(user.Text),
                AiAssistantMessage assistant => Encoding.UTF8.GetByteCount(assistant.Text) + assistant.ToolCalls.Sum(c =>
                    (long)Encoding.UTF8.GetByteCount(c.CallId) + Encoding.UTF8.GetByteCount(c.Name) + c.Arguments.ByteCount),
                AiToolMessage tool => Encoding.UTF8.GetByteCount(tool.CallId) + Encoding.UTF8.GetByteCount(tool.Name) + tool.Result.ByteCount,
                _ => throw Stop(ChatOrchestrationOutcome.InvalidRequest)
            };
            if (bytes > _limits.MaxContextBytes) throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
        }
    }
}
