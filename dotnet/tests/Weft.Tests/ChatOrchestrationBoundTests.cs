using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using static Weft.Tests.ChatOrchestrationFixture;

namespace Weft.Tests;

public sealed class ChatOrchestrationBoundTests
{
    [Fact]
    public async Task History_is_private_ledger_oldest_first_and_only_final_Stop_text_enters_the_request()
    {
        await using var f = new ChatOrchestrationFixture();
        var oldest = f.Intent("oldest question"); var recent = f.Intent("recent question");
        f.Store.SeedHistory(oldest, Now.AddMinutes(-5), "oldest final", "PRIVATE intermediate oldest");
        f.Store.SeedHistory(recent, Now.AddMinutes(-3), "recent final", "PRIVATE intermediate recent");
        var current = f.Intent("current question");
        Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(f.Begin(current, [oldest.TurnId, recent.TurnId]))).Outcome);
        using var document = f.Server.Requests[0].Json(); var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(new[] { "system", "user", "assistant", "user", "assistant", "user" },
            messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(new[] { Instructions, "oldest question", "oldest final", "recent question", "recent final", "current question" },
            messages.EnumerateArray().Select(m => m.GetProperty("content").GetString()));
        Assert.False(f.Server.Requests[0].ContainsUtf8("PRIVATE intermediate"));
        Assert.Contains(f.Store.Calls, c => c.Method == "read-events" && c.TurnId == oldest.TurnId);
        Assert.Contains(f.Store.Calls, c => c.Method == "read-events" && c.TurnId == recent.TurnId);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("reversed")]
    [InlineData("missing")]
    [InlineData("self")]
    [InlineData("too-many")]
    public async Task Untrusted_or_unbounded_history_selection_never_dispatches(string failure)
    {
        await using var f = new ChatOrchestrationFixture(); var oldest = f.Intent(); var recent = f.Intent(); var current = f.Intent();
        f.Store.SeedHistory(oldest, Now.AddMinutes(-5), "oldest final");
        f.Store.SeedHistory(recent, Now.AddMinutes(-3), "recent final");
        Guid[] ids = failure switch
        {
            "duplicate" => [oldest.TurnId, oldest.TurnId],
            "reversed" => [recent.TurnId, oldest.TurnId],
            "missing" => [Guid.NewGuid()],
            "self" => [current.TurnId],
            _ => [oldest.TurnId, recent.TurnId]
        };
        var limits = new ChatOrchestrationLimits(failure == "too-many" ? 1 : 8, 4096, 65536, 131072);
        if (failure is "duplicate" or "self" or "too-many")
        {
            Assert.Throws<ArgumentException>(() => f.Begin(current, ids, limits: limits));
            Assert.Equal(0, f.Store.Count("start"));
        }
        else Assert.NotEqual(ChatOrchestrationOutcome.Completed, (await FinishExecution(f.Begin(current, ids, limits: limits))).Outcome);
        Assert.Equal(0, f.Credentials.Calls); Assert.Equal(0, f.Server.ConnectionCount);
    }

    [Fact]
    public async Task Completed_history_spanning_real_100_event_pages_keeps_every_final_text_piece_in_order()
    {
        await using var f = new ChatOrchestrationFixture(); var prior = f.Intent("prior question");
        var parts = Enumerable.Range(0, 128).Select(i => i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + "|").ToArray();
        var answer = string.Concat(parts);
        f.Store.SeedHistory(prior, Now.AddMinutes(-5), answer, finalTextParts: parts);
        var priorEvents = f.Store.Events(prior.TurnId); Assert.True(priorEvents.Length > 100);
        Assert.Equal(priorEvents.Sum(Ledger.CountedEventBytes), f.Store.Facts(prior.TurnId).EventBytes);
        Assert.Null(priorEvents[^1].RoundId); Assert.NotNull(priorEvents[^1].Usage);
        var result = await FinishExecution(f.Begin(f.Intent("current question"), [prior.TurnId]));
        Assert.Equal(ChatOrchestrationOutcome.Completed, result.Outcome);
        var pages = f.Store.HistoryPages.Where(p => p.TurnId == prior.TurnId).ToArray(); Assert.True(pages.Length >= 2);
        long after = 0;
        foreach (var page in pages)
        {
            Assert.InRange(page.Limit, 1, 100); Assert.InRange(page.Returned, 0, page.Limit);
            Assert.Equal(after, page.AfterSequence); after += page.Returned;
        }
        Assert.Equal(priorEvents.Length, after); Assert.Contains(pages, page => page.AfterSequence > 0);
        using var sent = f.Server.Requests[0].Json(); var messages = sent.RootElement.GetProperty("messages");
        Assert.Equal(new[] { Instructions, "prior question", answer, "current question" },
            messages.EnumerateArray().Select(message => message.GetProperty("content").GetString()));
        Assert.All(f.Store.Calls.Where(call => call.Method == "read-events"), call => Assert.InRange(call.PageLimit, 1, 100));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("missing-terminal")]
    [InlineData("sequence-gap")]
    [InlineData("raw-text")]
    [InlineData("malformed-envelope")]
    public async Task Incomplete_or_malformed_history_is_not_silently_repaired_into_provider_context(string failure)
    {
        await using var f = new ChatOrchestrationFixture(); var prior = f.Intent();
        f.Store.SeedHistory(prior, Now.AddMinutes(-5), "final");
        if (failure == "active") f.Store.TurnRead = t => t is not null && t.Intent.TurnId == prior.TurnId ? Ledger.Copy(t, phase: ChatTurnPhase.Active) : t;
        f.Store.EventsRead = (id, page) => id != prior.TurnId ? page : failure switch
        {
            "missing-terminal" => page.Where(e => e.Kind != ChatEventKind.Completed).ToArray(),
            "sequence-gap" => page.Select(e => e.Kind == ChatEventKind.TextDelta ? e with { Sequence = e.Sequence + 10 } : e).ToArray(),
            "raw-text" => page.Select(e => e.Kind == ChatEventKind.TextDelta ? e with { Text = "raw prior text" } : e).ToArray(),
            "malformed-envelope" => page.Select(e => e.Kind == ChatEventKind.TextDelta ? e with { Text = "{\"v\":2,\"text\":\"final\"}" } : e).ToArray(),
            _ => page
        };
        var result = await FinishExecution(f.Begin(history: [prior.TurnId]));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(0, f.Credentials.Calls); Assert.Equal(0, f.Server.ConnectionCount);
    }

    [Theory]
    [InlineData("events")]
    [InlineData("history-bytes")]
    [InlineData("context-bytes")]
    [InlineData("serialized-request-bytes")]
    public async Task Cumulative_history_scan_text_context_and_serialized_request_bounds_fail_before_provider_work(string bound)
    {
        await using var f = new ChatOrchestrationFixture(); var first = f.Intent("first"); var second = f.Intent("second");
        f.Store.SeedHistory(first, Now.AddMinutes(-5), new string('a', 40));
        f.Store.SeedHistory(second, Now.AddMinutes(-3), new string('b', 40));
        var current = f.Intent("current", requestBytes: bound == "serialized-request-bytes" ? 32 : 1048576);
        var limits = new ChatOrchestrationLimits(8, bound == "events" ? 9 : 4096,
            bound == "history-bytes" ? 70 : 65536, bound == "context-bytes" ? 70 : 131072);
        var execution = f.Begin(current, [first.TurnId, second.TurnId], limits: limits);
        var result = await FinishExecution(execution);
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(0, f.Server.RequestCount); Assert.Equal(0, f.Credentials.Calls);
        Assert.Empty(f.Port.Executions); Assert.True(execution.ReleaseSafe);
    }

    [Fact]
    public async Task Output_bytes_are_cumulative_across_tool_round_prose_and_final_answer_even_with_unknown_usage()
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Text("12345678") + Call(0, "a") + Finish("tool_calls") + Done : Text("ééééé") + Finish("stop") + Done), ct));
        var intent = f.Intent(outputBytes: 16); var result = await FinishExecution(f.Begin(intent));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        var text = string.Concat(f.Store.Events(intent.TurnId).Where(e => e.Kind == ChatEventKind.TextDelta).Select(e => ChatOrchestrationEventCodec.ReadText(e.Text)));
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 8, 16);
        Assert.Single(f.Port.Executions); Assert.Equal(2, f.Server.RequestCount); Assert.Equal(2, f.Store.Count("reserve"));
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.Completed);
    }

    [Theory]
    [InlineData("events")]
    [InlineData("event-bytes")]
    public async Task Ledger_count_and_byte_limits_do_not_trigger_retry_or_an_unbounded_event_loop(string bound)
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            Text(new string('a', 3000)) + Text("second") + Text("third") + Finish("stop") + Done), ct));
        var intent = f.Intent(events: bound == "events" ? 4 : 256, eventBytes: bound == "event-bytes" ? 1024 : 262144);
        var result = await FinishExecution(f.Begin(intent));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(1, f.Server.RequestCount); Assert.Equal(1, f.Store.Count("reserve"));
        var events = f.Store.Events(intent.TurnId);
        Assert.InRange(events.Length, 1, intent.Budget.MaxEvents);
        Assert.InRange(events.Sum(Ledger.CountedEventBytes), 0, intent.Budget.MaxEventBytes);
        Assert.InRange(f.Store.Count("finish"), 0, 1);
    }
}
