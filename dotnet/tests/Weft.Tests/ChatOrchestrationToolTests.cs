using System.Text;
using System.Text.Json;
using Weft.Ai;
using Weft.Application;
using Weft.Domain;
using static Weft.Tests.ChatOrchestrationFixture;

namespace Weft.Tests;

public sealed class ChatOrchestrationToolTests
{
    [Fact]
    public async Task Entire_tool_batch_validates_before_any_tool_start_or_execution_and_distinct_ids_keep_provider_order()
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Text("planning") + Call(0, "call-a") + Call(1, "call-b") + Finish("tool_calls") + Done : Text("final") + Finish("stop") + Done), ct));
        var secondValidation = f.Gate(); var intent = f.Intent();
        f.Port.Validate = async (_, call, _) => { if (call.CallId == "call-b") await secondValidation.Hold(); return true; };
        f.Port.Execute = (_, call, _) => Task.FromResult(new ChatReadOnlyToolResult(AiJsonObject.CopyFrom(
            call.CallId == "call-a" ? "{\"value\":\"left\"}" : "{\"value\":\"right\"}"), 1));
        var execution = f.Begin(intent); await Bound(secondValidation.Entered.Task);
        Assert.Equal(new[] { "call-a", "call-b" }, f.Port.Validations.Select(v => v.Intent.CallId));
        Assert.Empty(f.Port.Executions);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind is ChatEventKind.ToolStarted or ChatEventKind.ToolCompleted);
        Assert.False(execution.Completion.IsCompleted); secondValidation.Open();
        Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(execution)).Outcome);
        var executed = f.Port.Executions.ToArray();
        Assert.Equal(new[] { "call-a", "call-b" }, executed.Select(e => e.Intent.CallId));
        Assert.Equal(new[] { 1, 2 }, executed.Select(e => e.Context.Ordinal));
        Assert.Equal(executed[0].Intent.Arguments.Json, executed[1].Intent.Arguments.Json);
        Assert.All(executed, e =>
        {
            Assert.Same(f.Actor, e.Context.Actor); Assert.Equal(f.Conversation, e.Context.Conversation); Assert.Same(intent, e.Context.Turn);
            Assert.Same(f.Port.Validations.Single(v => v.Intent.CallId == e.Intent.CallId).Intent, e.Intent);
        });
        var trace = f.Trace.Snapshot;
        var actions = trace.Where(m => m.Kind.StartsWith("tool.", StringComparison.Ordinal)).Select(m => (m.Kind, m.CallId));
        Assert.Equal(new (string, string?)[] { ("tool.validate", "call-a"), ("tool.validate", "call-b"), ("tool.execute", "call-a"),
            ("tool.return", "call-a"), ("tool.execute", "call-b"), ("tool.return", "call-b") }, actions);
        var events = f.Store.Events(intent.TurnId);
        Assert.Equal(new[] { ChatEventKind.Started, ChatEventKind.TextDelta, ChatEventKind.ToolStarted, ChatEventKind.ToolCompleted,
            ChatEventKind.ToolStarted, ChatEventKind.ToolCompleted, ChatEventKind.RoundCompleted, ChatEventKind.TextDelta,
            ChatEventKind.RoundCompleted, ChatEventKind.Completed }, events.Select(e => e.Kind));
        Assert.Equal(new[] { ChatRoundOutcome.ToolCalls, ChatRoundOutcome.Stopped }, f.Store.Rounds(intent.TurnId).Select(r => r.Outcome));
        foreach (var e in events.Where(e => e.Kind is ChatEventKind.ToolStarted or ChatEventKind.ToolCompleted))
        {
            using var json = JsonDocument.Parse(e.Text); var root = json.RootElement;
            Assert.Equal(1, root.GetProperty("v").GetInt32());
            Assert.Equal("read_mapping", root.GetProperty("name").GetString());
            Assert.Equal(e.Sequence < 5 ? "call-a" : "call-b", root.GetProperty("call").GetString());
            Assert.Equal(e.Sequence < 5 ? 1 : 2, root.GetProperty("ordinal").GetInt32());
            Assert.False(root.TryGetProperty("arguments", out _)); Assert.False(root.TryGetProperty("result", out _));
            Assert.False(e.Text.Contains("left", StringComparison.Ordinal)); Assert.False(e.Text.Contains("right", StringComparison.Ordinal));
            Assert.InRange(Encoding.UTF8.GetByteCount(e.Text), 1, 8192);
            if (e.Kind == ChatEventKind.ToolCompleted)
            {
                // The handoff names the byte/node facts but not their JSON keys.
                // Require the exact two additional numeric facts without inventing keys.
                var accounting = root.EnumerateObject().Where(p => p.Name is not ("v" or "ordinal") && p.Value.ValueKind == JsonValueKind.Number)
                    .Select(p => p.Value.GetInt32()).Order().ToArray();
                var resultBytes = AiJsonObject.CopyFrom(e.Sequence < 5 ? "{\"value\":\"left\"}" : "{\"value\":\"right\"}").ByteCount;
                Assert.Equal(new[] { 1, resultBytes }.Order(), accounting);
            }
        }
        Assert.Equal(2, f.Server.RequestCount);
        var secondReservation = f.Store.Rounds(intent.TurnId)[1];
        Assert.NotEqual(executed[0].Context.RoundId, secondReservation.Intent.RoundId);
        using var request = f.Server.Requests[1].Json(); var messages = request.RootElement.GetProperty("messages");
        Assert.Equal(5, messages.GetArrayLength());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("planning", messages[2].GetProperty("content").GetString());
        var calls = messages[2].GetProperty("tool_calls");
        Assert.Equal(new[] { "call-a", "call-b" }, calls.EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal("tool", messages[3 + i].GetProperty("role").GetString());
            Assert.Equal(executed[i].Intent.CallId, messages[3 + i].GetProperty("tool_call_id").GetString());
            using var result = JsonDocument.Parse(messages[3 + i].GetProperty("content").GetString()!);
            Assert.Equal(i == 0 ? "left" : "right", result.RootElement.GetProperty("value").GetString());
        }
        using var retained = JsonDocument.Parse(secondReservation.Intent.ExactRequest);
        Assert.Equal(messages[3].GetProperty("content").GetString(), retained.RootElement.GetProperty("messages")[3].GetProperty("resultJson").GetString());
        Assert.True(execution.ReleaseSafe);
    }

    [Fact]
    public async Task Invalid_last_schema_rejects_the_whole_batch_instead_of_executing_valid_prefix()
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            Call(0, "good") + Call(1, "bad", "{\"field\":4}") + Finish("tool_calls") + Done), ct));
        var intent = f.Intent(); var result = await FinishExecution(f.Begin(intent));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "good", "bad" }, f.Port.Validations.Select(v => v.Intent.CallId)); Assert.Empty(f.Port.Executions);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind is ChatEventKind.ToolStarted or ChatEventKind.ToolCompleted);
        Assert.Equal(1, f.Server.RequestCount); Assert.Equal(1, f.Store.Count("reserve"));
    }

    [Fact]
    public async Task Complete_native_tool_terminal_still_cannot_validate_or_execute_until_original_stream_cleanup_settles()
    {
        var cleanup = new AsyncGate(); var resolver = new Resolver { OnCancellation = cleanup.HoldSync };
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Text("leading") + Call(0, "a") + Finish("tool_calls") + Done : Text("final") + Finish("stop") + Done), ct), credentials: resolver);
        f.ReleaseWith(cleanup.Open); var intent = f.Intent(); var execution = f.Begin(intent);
        await Bound(cleanup.Entered.Task); await f.Server.WaitForCompletedAsync(1);
        // Ordinary stream cleanup has actually invoked the resolver callback;
        // a server-side write/close alone would not be a sufficient signal.
        Assert.Equal(1, resolver.Callbacks); Assert.Empty(f.Port.Validations); Assert.Empty(f.Port.Executions);
        Assert.Single(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.TextDelta);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind is ChatEventKind.ToolStarted or ChatEventKind.RoundCompleted);
        await Assert.ThrowsAsync<TimeoutException>(async () => await execution.WaitAsync(TimeSpan.FromMilliseconds(30)));
        Assert.False(execution.Completion.IsCompleted); Assert.False(execution.ReleaseSafe);
        cleanup.Open(); Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(execution)).Outcome);
        Assert.Single(f.Port.Executions); Assert.Equal(2, f.Server.RequestCount);
    }

    [Theory]
    [InlineData("tool-start")]
    [InlineData("tool-completed")]
    [InlineData("round-completed")]
    public async Task Tool_publication_ack_loss_does_not_retry_execute_twice_or_reserve_the_next_round(string lost)
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            Call(0, "a") + Finish("tool_calls") + Done), ct));
        var injected = 0;
        f.Store.Returning = call =>
        {
            var matches = lost == "tool-start" && call.Event?.Kind == ChatEventKind.ToolStarted ||
                lost == "tool-completed" && call.Event?.Kind == ChatEventKind.ToolCompleted ||
                lost == "round-completed" && call.Method == "complete-round";
            if (matches && Interlocked.CompareExchange(ref injected, 1, 0) == 0) throw new InvalidOperationException("PRIVATE lost tool publication acknowledgement");
            return Task.CompletedTask;
        };
        var intent = f.Intent(); var result = await FinishExecution(f.Begin(intent));
        Assert.Equal(1, injected); Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(lost == "tool-start" ? 0 : 1, f.Port.Executions.Count);
        Assert.Equal(1, f.Server.RequestCount); Assert.Equal(1, f.Store.Count("reserve"));
        Assert.Single(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.ToolStarted);
        Assert.InRange(f.Store.Count("complete-round"), 0, 1); Assert.InRange(f.Store.Count("finish"), 0, 1);
    }

    [Fact]
    public async Task Withheld_native_done_cannot_admit_tools_after_a_real_leading_event_is_persisted()
    {
        var published = AiHttpFixture.NewSignal(); var nativeTail = new AsyncGate();
        var prefix = Text("observed") + Call(0, "a") + Call(1, "b") + Finish("tool_calls");
        await using var f = new ChatOrchestrationFixture(async (exchange, ct) =>
        {
            if (exchange.Ordinal != 1) { await exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(Text("final") + Finish("stop") + Done), ct); return; }
            await exchange.HeadersAsync("text/event-stream", ct, length: AiHttpFixture.Utf8(prefix + Done).Length);
            await exchange.WriteAsync(AiHttpFixture.Utf8(prefix), ct);
            await nativeTail.Hold(); await exchange.WriteAsync(AiHttpFixture.Utf8(Done), ct);
        });
        f.ReleaseWith(nativeTail.Open);
        f.Store.Returning = call => { if (call.Event?.Kind == ChatEventKind.TextDelta) published.TrySetResult(); return Task.CompletedTask; };
        var intent = f.Intent(); var execution = f.Begin(intent);
        await Bound(published.Task); await Bound(nativeTail.Entered.Task);
        await Assert.ThrowsAsync<TimeoutException>(async () => await execution.WaitAsync(TimeSpan.FromMilliseconds(30)));
        Assert.Empty(f.Port.Validations); Assert.Empty(f.Port.Executions); Assert.False(execution.ReleaseSafe);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind is ChatEventKind.ToolStarted or ChatEventKind.RoundCompleted);
        nativeTail.Open(); Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(execution)).Outcome);
        Assert.Equal(2, f.Port.Executions.Count);
    }

    [Theory]
    [InlineData("no-done")]
    [InlineData("no-finish")]
    [InlineData("bad-arguments")]
    [InlineData("duplicate-id")]
    [InlineData("unknown-tool")]
    [InlineData("trailing-malformed")]
    [InlineData("conflicting-finish")]
    public async Task Partial_malformed_or_failed_terminal_admits_no_tool_work(string failure)
    {
        var wire = failure switch
        {
            "no-done" => Call(0, "a") + Finish("tool_calls"),
            "no-finish" => Call(0, "a") + Done,
            "bad-arguments" => Call(0, "a", "{\"field\":") + Finish("tool_calls") + Done,
            "duplicate-id" => Call(0, "a") + Call(1, "a") + Finish("tool_calls") + Done,
            "unknown-tool" => Call(0, "a", name: "run_shell") + Finish("tool_calls") + Done,
            "trailing-malformed" => Call(0, "a") + Finish("tool_calls") + Data("{broken") + Done,
            _ => Call(0, "a") + Finish("tool_calls") + Finish("stop") + Done
        };
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct));
        var intent = f.Intent(); var result = await FinishExecution(f.Begin(intent));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Empty(f.Port.Executions); Assert.Empty(f.Port.Validations);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind is ChatEventKind.ToolStarted or ChatEventKind.ToolCompleted);
        Assert.Equal(1, f.Server.RequestCount); Assert.Equal(1, f.Store.Count("reserve"));
    }

    [Theory]
    [InlineData("before-validation")]
    [InlineData("after-tool-start")]
    [InlineData("after-result")]
    public async Task Fresh_authority_is_checked_at_each_tool_boundary(string boundary)
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            Text("leading") + Call(0, "a") + Finish("tool_calls") + Done), ct));
        f.Store.Returning = call =>
        {
            if (boundary == "before-validation" && call.Event?.Kind == ChatEventKind.TextDelta ||
                boundary == "after-tool-start" && call.Event?.Kind == ChatEventKind.ToolStarted) f.Store.Authorized = false;
            return Task.CompletedTask;
        };
        f.Port.Execute = (_, _, _) =>
        {
            if (boundary == "after-result") f.Store.Authorized = false;
            return Task.FromResult(new ChatReadOnlyToolResult(AiJsonObject.CopyFrom("{\"ok\":true}"), 1));
        };
        var intent = f.Intent(); var result = await FinishExecution(f.Begin(intent));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome); Assert.Equal(1, f.Server.RequestCount);
        if (boundary == "before-validation") { Assert.Empty(f.Port.Validations); Assert.Empty(f.Port.Executions); }
        else if (boundary == "after-tool-start") { Assert.Single(f.Port.Validations); Assert.Empty(f.Port.Executions); }
        else Assert.Single(f.Port.Executions);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.ToolCompleted);
        Assert.Equal(1, f.Store.Count("reserve"));
    }

    [Theory]
    [InlineData("result-bytes")]
    [InlineData("nodes")]
    public async Task Cumulative_result_and_node_allowances_decrease_and_complete_over_budget_JSON_is_rejected_not_truncated(string budget)
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            Call(0, "a") + Call(1, "b") + Finish("tool_calls") + Done), ct));
        var resultJson = AiJsonObject.CopyFrom("{\"value\":\"same\"}");
        var bytes = resultJson.ByteCount;
        var intent = f.Intent(resultBytes: budget == "result-bytes" ? bytes * 2 - 1 : 8192, nodes: budget == "nodes" ? 3 : 64);
        f.Port.Execute = (_, _, _) => Task.FromResult(new ChatReadOnlyToolResult(resultJson, 2));
        var result = await FinishExecution(f.Begin(intent));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        var execution = f.Port.Executions.ToArray(); Assert.Equal(2, execution.Length);
        Assert.Equal(intent.Envelope.GroundingPolicy.MaxToolResultBytes, execution[0].Context.RemainingResultBytes);
        Assert.Equal(intent.Envelope.GroundingPolicy.MaxToolResultBytes - bytes, execution[1].Context.RemainingResultBytes);
        Assert.Equal(intent.Envelope.GroundingPolicy.MaxRetrievedNodes - 2, execution[1].Context.RemainingRetrievedNodes);
        Assert.Single(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.ToolCompleted);
        Assert.Equal(1, f.Server.RequestCount); Assert.Equal(1, f.Store.Count("reserve"));
    }

    [Theory]
    [InlineData("result-bytes")]
    [InlineData("nodes")]
    public async Task Tool_result_allowances_are_not_reset_by_a_new_provider_round(string budget)
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            Call(0, exchange.Ordinal == 1 ? "first" : "second") + Finish("tool_calls") + Done), ct));
        var json = AiJsonObject.CopyFrom("{\"whole\":true}");
        var intent = f.Intent(resultBytes: budget == "result-bytes" ? json.ByteCount * 2 - 1 : 8192, nodes: budget == "nodes" ? 3 : 64);
        f.Port.Execute = (_, _, _) => Task.FromResult(new ChatReadOnlyToolResult(json, 2));
        var result = await FinishExecution(f.Begin(intent));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        var executions = f.Port.Executions.ToArray(); Assert.Equal(2, executions.Length);
        Assert.NotEqual(executions[0].Context.RoundId, executions[1].Context.RoundId);
        Assert.Equal(intent.Envelope.GroundingPolicy.MaxToolResultBytes - json.ByteCount, executions[1].Context.RemainingResultBytes);
        Assert.Equal(intent.Envelope.GroundingPolicy.MaxRetrievedNodes - 2, executions[1].Context.RemainingRetrievedNodes);
        Assert.Single(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.ToolCompleted);
        Assert.Equal(2, f.Server.RequestCount); Assert.Equal(2, f.Store.Count("reserve"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Exact_node_exhaustion_allows_the_next_zero_node_result_but_rejects_positive_overage(int secondNodes)
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Call(0, "drain-nodes") + Call(1, "at-zero") + Finish("tool_calls") + Done : Text("final") + Finish("stop") + Done), ct));
        var firstJson = AiJsonObject.CopyFrom("{\"first\":\"whole\"}");
        var secondJson = AiJsonObject.CopyFrom("{\"second\":\"complete-zero-allowance-result\"}");
        var intent = f.Intent(nodes: 2, resultBytes: 4096, calls: 4, rounds: 3, iterations: 3);
        f.Port.Execute = (_, call, _) => Task.FromResult(call.CallId == "drain-nodes"
            ? new ChatReadOnlyToolResult(firstJson, 2) : new ChatReadOnlyToolResult(secondJson, secondNodes));
        var execution = f.Begin(intent); var result = await FinishExecution(execution);
        var validations = f.Port.Validations.ToArray(); var executions = f.Port.Executions.ToArray();
        Assert.Equal(new[] { "drain-nodes", "at-zero" }, validations.Select(v => v.Intent.CallId));
        Assert.Equal(new[] { "drain-nodes", "at-zero" }, executions.Select(e => e.Intent.CallId));
        var trace = f.Trace.Snapshot;
        Assert.True(trace.Where(m => m.Kind == "tool.validate").Max(m => m.Sequence) < trace.First(m => m.Kind == "tool.execute").Sequence);
        Assert.Equal(new[] { 1, 2 }, executions.Select(e => e.Context.Ordinal));
        Assert.Equal(2, executions[0].Context.RemainingRetrievedNodes); Assert.Equal(0, executions[1].Context.RemainingRetrievedNodes);
        Assert.Equal(4096, executions[0].Context.RemainingResultBytes);
        Assert.Equal(4096 - firstJson.ByteCount, executions[1].Context.RemainingResultBytes);
        Assert.True(secondJson.ByteCount < executions[1].Context.RemainingResultBytes);
        Assert.Equal(4, intent.Envelope.ProviderBudget.MaxToolCalls); Assert.Equal(3, intent.Budget.MaxRounds);
        Assert.Equal(3, intent.Envelope.GroundingPolicy.MaxToolIterations);
        Assert.All(executions, e => Assert.Same(validations.Single(v => v.Intent.CallId == e.Intent.CallId).Intent, e.Intent));
        var events = f.Store.Events(intent.TurnId);
        var started = events.Where(e => e.Kind == ChatEventKind.ToolStarted).ToArray();
        Assert.Equal(new[] { "drain-nodes", "at-zero" }, started.Select(e =>
        {
            using var body = JsonDocument.Parse(e.Text); return body.RootElement.GetProperty("call").GetString();
        }));
        var completed = events.Where(e => e.Kind == ChatEventKind.ToolCompleted).ToArray();
        if (secondNodes == 0)
        {
            Assert.Equal(ChatOrchestrationOutcome.Completed, result.Outcome); Assert.Equal(ChatTurnPhase.Completed, result.Phase);
            Assert.Equal(new[] { "drain-nodes", "at-zero" }, completed.Select(e =>
            {
                using var body = JsonDocument.Parse(e.Text); return body.RootElement.GetProperty("call").GetString();
            }));
            Assert.Equal(new[] { ChatEventKind.Started, ChatEventKind.ToolStarted, ChatEventKind.ToolCompleted,
                ChatEventKind.ToolStarted, ChatEventKind.ToolCompleted, ChatEventKind.RoundCompleted, ChatEventKind.TextDelta,
                ChatEventKind.RoundCompleted, ChatEventKind.Completed }, events.Select(e => e.Kind));
            Assert.Equal(new[] { ChatRoundOutcome.ToolCalls, ChatRoundOutcome.Stopped }, f.Store.Rounds(intent.TurnId).Select(r => r.Outcome));
            Assert.Equal(2, f.Server.RequestCount); Assert.Equal(2, f.Store.Count("reserve"));
            using var sent = f.Server.Requests[1].Json(); var messages = sent.RootElement.GetProperty("messages");
            var calls = messages[2].GetProperty("tool_calls");
            Assert.Equal(new[] { "drain-nodes", "at-zero" }, calls.EnumerateArray().Select(c => c.GetProperty("id").GetString()));
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal("tool", messages[3 + i].GetProperty("role").GetString());
                Assert.Equal(executions[i].Intent.CallId, messages[3 + i].GetProperty("tool_call_id").GetString());
                Assert.Equal(i == 0 ? firstJson.Json : secondJson.Json, messages[3 + i].GetProperty("content").GetString());
            }
            using var retained = JsonDocument.Parse(f.Store.Rounds(intent.TurnId)[1].Intent.ExactRequest);
            Assert.Equal(secondJson.Json, retained.RootElement.GetProperty("messages")[4].GetProperty("resultJson").GetString());
        }
        else
        {
            Assert.Equal(ChatOrchestrationOutcome.BudgetExceeded, result.Outcome); Assert.Equal(ChatTurnPhase.Failed, result.Phase);
            var onlyCompleted = Assert.Single(completed);
            using var body = JsonDocument.Parse(onlyCompleted.Text); Assert.Equal("drain-nodes", body.RootElement.GetProperty("call").GetString());
            Assert.DoesNotContain(f.Store.Rounds(intent.TurnId), r => r.Outcome == ChatRoundOutcome.ToolCalls);
            Assert.DoesNotContain(events, e => e.Kind == ChatEventKind.Completed || e.Kind == ChatEventKind.RoundCompleted && e.TerminalMetadata?.FinishReason == ChatProviderFinishReason.ToolCalls);
            Assert.Equal(1, f.Server.RequestCount); Assert.Equal(1, f.Store.Count("reserve"));
            Assert.Equal(2, f.Port.Executions.Count); // No execution retry after a positive result at zero allowance.
        }
        Assert.True(execution.ReleaseSafe);
    }

    [Theory]
    [InlineData("iterations")]
    [InlineData("calls")]
    [InlineData("rounds")]
    public async Task Tool_iterations_calls_and_last_round_are_cumulative_and_do_not_execute_a_prefix_to_fit(string budget)
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Call(0, "first") + Finish("tool_calls") + Done : Call(0, "second") + Call(1, "third") + Finish("tool_calls") + Done), ct));
        var intent = f.Intent(iterations: budget == "iterations" ? 1 : 3, calls: budget == "calls" ? 2 : 8, rounds: budget == "rounds" ? 2 : 3);
        var result = await FinishExecution(f.Begin(intent));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "first" }, f.Port.Executions.Select(e => e.Intent.CallId));
        Assert.Equal(2, f.Server.RequestCount); Assert.Equal(2, f.Store.Count("reserve"));
        Assert.Single(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.ToolStarted);
    }
}
