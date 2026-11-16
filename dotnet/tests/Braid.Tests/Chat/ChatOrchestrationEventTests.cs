using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using static Braid.Tests.ChatOrchestrationFixture;

namespace Braid.Tests;

public sealed class ChatOrchestrationEventTests
{
    [Fact]
    public async Task Text_usage_round_and_terminal_events_have_exact_encounter_order_and_serial_fences()
    {
        // #111 validates the complete terminal frame, then emits its usage before
        // its same-frame final text. Usage before any native finish is invalid.
        var terminalFrame = Data(JsonSerializer.Serialize(new
        {
            choices = new[] {
            new { index = 0, delta = new { content = "two" }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 7, completion_tokens = 2, total_tokens = 9 }
        }));
        var wire = Text("one") + terminalFrame + Done;
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct, fragmented: true));
        var intent = f.Intent(); var execution = f.Begin(intent); var result = await FinishExecution(execution);
        Assert.Equal(ChatOrchestrationOutcome.Completed, result.Outcome); Assert.Equal(ChatTurnPhase.Completed, result.Phase);
        var events = f.Store.Events(intent.TurnId);
        Assert.Equal(new[] { ChatEventKind.Started, ChatEventKind.TextDelta, ChatEventKind.Usage, ChatEventKind.TextDelta,
            ChatEventKind.RoundCompleted, ChatEventKind.Completed }, events.Select(e => e.Kind));
        Assert.Equal(Enumerable.Range(1, events.Length).Select(n => (long)n), events.Select(e => e.Sequence));
        Assert.Equal(events.Length, events.Select(e => e.EventId).Distinct().Count());
        Assert.Equal(new[] { "one", "two" }, events.Where(e => e.Kind == ChatEventKind.TextDelta).Select(e => ChatOrchestrationEventCodec.ReadText(e.Text)));
        var usage = events.Single(e => e.Kind == ChatEventKind.Usage).Usage!;
        Assert.Equal(ChatUsageOrigin.ProviderObservation, usage.Origin); Assert.Equal(7, usage.InputTokens);
        Assert.Equal(2, usage.OutputTokens); Assert.Equal(9, usage.TotalTokens);
        var round = Assert.Single(f.Store.Rounds(intent.TurnId));
        Assert.Equal(ChatRoundOutcome.Stopped, round.Outcome);
        Assert.Equal(ChatProviderFinishReason.Stop, round.Metadata!.FinishReason);
        Assert.All(events.Where(e => !Ledger.IsTurnTerminal(e) && e.Kind != ChatEventKind.Started), e => Assert.Equal(round.Intent.RoundId, e.RoundId));
        Assert.Null(events[0].RoundId);
        var terminal = events.Single(e => e.Kind == ChatEventKind.Completed);
        Assert.Null(terminal.RoundId); Assert.Equal(round.Intent.RoundId, terminal.Usage!.RoundId);
        Assert.Equal(round.Metadata, terminal.TerminalMetadata);
        Assert.Equal(events.Sum(Ledger.CountedEventBytes), f.Store.Facts(intent.TurnId).EventBytes);
        Assert.True(Encoding.UTF8.GetByteCount(terminal.MetadataJson!) > 0); // Stored, but outside content-byte accounting.
        var writes = f.Store.Calls.Where(c => c.Method is "reserve" or "append" or "complete-round" or "finish").ToArray();
        Assert.Equal(Enumerable.Range(1, writes.Length).Select(n => (long)n), writes.Select(c => c.Version));
        Assert.All(writes.Where(c => c.Method is "append" or "complete-round"), c =>
        {
            Assert.Equal(round.Intent.RoundId, c.RoundId); Assert.Equal(round.Intent.Fence, c.Fence);
        });
        Assert.True(execution.ReleaseSafe);
    }

    [Fact]
    public async Task Canonical_text_preserves_whitespace_NUL_and_multibyte_scalar_boundaries_with_per_event_byte_ceiling()
    {
        var text = " \t\n\0" + string.Concat(Enumerable.Repeat("😀é\"\\", 1500));
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream",
            AiHttpFixture.Utf8(Text(text) + Finish("stop") + Done), ct, fragmented: true));
        var intent = f.Intent(); Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(f.Begin(intent))).Outcome);
        var chunks = f.Store.Events(intent.TurnId).Where(e => e.Kind == ChatEventKind.TextDelta).ToArray();
        Assert.True(chunks.Length > 1, "Large escaped text was not split into bounded ledger events.");
        var strict = new UTF8Encoding(false, true);
        foreach (var chunk in chunks)
        {
            Assert.InRange(strict.GetByteCount(chunk.Text), 1, 8192);
            var decoded = ChatOrchestrationEventCodec.ReadText(chunk.Text);
            _ = strict.GetByteCount(decoded); // Throws on a split surrogate, including at piece edges.
            using var json = JsonDocument.Parse(chunk.Text);
            Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
            Assert.Equal(decoded, json.RootElement.GetProperty("text").GetString());
            Assert.Equal(new[] { "v", "text" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        }
        Assert.True(string.Equals(text, string.Concat(chunks.Select(e => ChatOrchestrationEventCodec.ReadText(e.Text))), StringComparison.Ordinal),
            "Persisted text was normalized, truncated or reordered.");
    }

    [Theory]
    [InlineData("length", ChatProviderFinishReason.OutputLimit)]
    [InlineData("content_filter", ChatProviderFinishReason.ContentFiltered)]
    public async Task Non_Stop_native_terminal_is_failed_not_a_successful_answer(string reason, ChatProviderFinishReason expected)
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream",
            AiHttpFixture.Utf8(Text("partial") + Finish(reason) + Done), ct));
        var intent = f.Intent(); var result = await FinishExecution(f.Begin(intent));
        Assert.Equal(ChatOrchestrationOutcome.Failed, result.Outcome); Assert.Equal(ChatTurnPhase.Failed, result.Phase);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.Completed);
        Assert.Equal(expected, Assert.Single(f.Store.Rounds(intent.TurnId)).Metadata!.FinishReason);
        Assert.Empty(f.Port.Executions); Assert.Equal(1, f.Server.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_or_partial_token_usage_is_not_coerced_to_zero_or_aggregate(bool partial)
    {
        var usage = partial ? Data("{\"choices\":[],\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":3}}") : "";
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream",
            AiHttpFixture.Utf8(Text("answer") + Finish("stop") + usage + Done), ct));
        var intent = f.Intent(); await FinishExecution(f.Begin(intent));
        var terminal = Assert.Single(f.Store.Rounds(intent.TurnId)).Metadata!.Usage!;
        Assert.Equal(ChatUsageOrigin.ProviderRoundTerminal, terminal.Origin); Assert.Null(terminal.TotalTokens);
        if (partial) { Assert.True(terminal.IsKnown); Assert.Equal(0, terminal.InputTokens); Assert.Equal(3, terminal.OutputTokens); }
        else { Assert.False(terminal.IsKnown); Assert.Null(terminal.InputTokens); Assert.Null(terminal.OutputTokens); }
        Assert.Equal(terminal, f.Store.Events(intent.TurnId).Single(e => e.Kind == ChatEventKind.Completed).Usage);
    }

    [Fact]
    public async Task Refusal_content_and_unknown_usage_are_preserved_as_failed_categorical_terminal()
    {
        var refusal = Data("{\"choices\":[{\"index\":0,\"delta\":{\"refusal\":\"PRIVATE provider refusal\"},\"finish_reason\":null}]}") + Finish("stop") + Done;
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(refusal), ct));
        var intent = f.Intent(); var result = await FinishExecution(f.Begin(intent));
        Assert.Equal(ChatOrchestrationOutcome.Failed, result.Outcome);
        Assert.Equal(ChatProviderFinishReason.Refused, Assert.Single(f.Store.Rounds(intent.TurnId)).Metadata!.FinishReason);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Text.Contains("PRIVATE provider refusal", StringComparison.Ordinal));
        Assert.Equal(nameof(ChatOrchestrationResult), result.ToString());
    }
}
