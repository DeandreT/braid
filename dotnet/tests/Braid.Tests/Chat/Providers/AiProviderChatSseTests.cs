using System.Text;
using System.Text.Json;
using Braid.Ai;

namespace Braid.Tests;

public sealed partial class AiProviderTests
{
    private static string ChatData(string json, string newline = "\n") => "data: " + json + newline + newline;
    private static string ChatText(string text) => ChatData("{\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) + "},\"finish_reason\":null}]}");
    private static string ChatFinish(string reason) => ChatData("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":" + JsonSerializer.Serialize(reason) + "}]}");
    private const string Done = "data: [DONE]\n\n";
    private static string ChatTool(int index, string? id, string? name, string? arguments)
    {
        var call = new Dictionary<string, object?> { ["index"] = index };
        if (id is not null) { call["id"] = id; call["type"] = "function"; }
        var function = new Dictionary<string, object?>();
        if (name is not null) function["name"] = name;
        if (arguments is not null) function["arguments"] = arguments;
        if (function.Count != 0) call["function"] = function;
        return ChatData(JsonSerializer.Serialize(new
        {
            model = "ignored-alias",
            choices = new[] {
            new { index = 0, delta = new { tool_calls = new[] { call } }, finish_reason = (string?)null } }
        }));
    }
    private static string ChatToolsWire => ChatTool(0, "PRIVATE-call-a", "read_mapping", "{\"revision\":") +
        ChatTool(0, "PRIVATE-call-a", "read_mapping", "\"left\"}") + ChatTool(1, "PRIVATE-call-b", "read_mapping", "{\"revision\":\"right\"}") +
        ChatFinish("tool_calls") + Done;

    [Fact]
    public async Task SseIncrementalUnicodeCrLfCrMultilineAndIgnoredFieldsPreserveOnlyPublicText()
    {
        var wire = ": bounded comment\r\nid: PRIVATE-ignored\rretry: 999999\revent: message\r" +
            "data: {\"choices\":\rdata: [{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"A😀\",\"reasoning_content\":\"PRIVATE-hidden\"},\"finish_reason\":null}]}\r\r" +
            ChatText("éZ") + ChatFinish("stop") +
            ChatData("{\"choices\":[],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3,\"total_tokens\":10},\"metadata\":{\"private\":\"ignored\"}}", "\r\n") + Done;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream; charset=utf-8", AiHttpFixture.Utf8(wire), ct, fragmented: true));
        var resolver = new Resolver(); await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true), resolver);
        await using var turn = Turn(client); var opened = await Open(client, turn); await using var stream = opened.Stream;
        var events = await Read(stream); var terminal = Finished(events, AiFinishReason.Stop);
        Same("A😀éZ", Text(events), "SSE text order or Unicode changed.");
        Assert.Equal(7, terminal.Usage.InputTokens); Assert.Equal(3, terminal.Usage.OutputTokens); Assert.Equal(10, terminal.Usage.TotalTokens);
        Assert.False(Text(events).Contains("PRIVATE", StringComparison.Ordinal), "Hidden SSE content escaped.");
        Assert.Equal(1, opened.Ordinal); Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
        var request = server.Requests[0]; Assert.True(request.IsPostAt("/v1/chat/completions"), "Chat endpoint/method/version changed.");
        Assert.True(request.HeaderIs("Authorization", "Bearer " + Token), "Bearer was not confined to the admitted request.");
        using var document = request.Json(); var root = document.RootElement;
        Assert.True(root.GetProperty("model").GetString() == Model, "Profile model changed.");
        Assert.True(root.GetProperty("stream").GetBoolean(), "Streaming request flag changed.");
        Assert.False(root.GetProperty("store").GetBoolean(), "Provider storage request changed.");
        Assert.True(root.GetProperty("n").GetInt32() == 1, "Choice count changed.");
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(), "Usage request flag changed.");
        Assert.True(root.GetProperty("max_completion_tokens").GetInt32() == 128, "Output cap changed.");
        Assert.False(root.TryGetProperty("think", out _), "Chat request contained another dialect's option.");
        var closed = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(), CancellationToken.None));
        Assert.Equal(AiDispatchState.NotAttempted, closed.Failure.Dispatch); Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task CompleteToolTerminalThenNewRoundCarriesExactOrderedResultsAndSameProfile()
    {
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream",
            exchange.Ordinal == 1 ? AiHttpFixture.Utf8(ChatToolsWire) : StopWire(AiDialect.ChatCompletionsSseV1), ct, fragmented: true,
            extra: exchange.Ordinal == 1 ? "Set-Cookie: PRIVATE=ignored; Path=/\r\n" : ""));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        var firstRequest = Request(tools: [Tool()]); var first = await Open(client, turn, firstRequest); await using var firstStream = first.Stream;
        var firstEvents = await Read(firstStream); Finished(firstEvents, AiFinishReason.ToolCalls);
        var calls = firstEvents.OfType<AiToolIntentReady>().Select(value => value.Intent).ToArray();
        Assert.True(calls.Length == 2, "Staged tool count changed.");
        Same("PRIVATE-call-a", calls[0].CallId, "First native tool identity changed."); Same("PRIVATE-call-b", calls[1].CallId, "Second native tool identity changed.");
        using (var left = AiHttpFixture.Json(calls[0].Arguments.Json))
            Assert.True(left.RootElement.GetProperty("revision").GetString() == "left", "Fragmented first arguments changed.");
        using (var right = AiHttpFixture.Json(calls[1].Arguments.Json))
            Assert.True(right.RootElement.GetProperty("revision").GetString() == "right", "Second arguments changed.");
        var transcript = new AiMessage[] { AiUserMessage.Create("PRIVATE fixture question"), AiAssistantMessage.Create(Text(firstEvents), calls),
            AiToolMessage.Create(calls[0].CallId, calls[0].Name, AiJsonObject.CopyFrom("{\"found\":true}")),
            AiToolMessage.Create(calls[1].CallId, calls[1].Name, AiJsonObject.CopyFrom("{\"found\":false}")) };
        var second = await Open(client, turn, Request(messages: transcript, tools: [Tool()])); await using var secondStream = second.Stream;
        var secondTerminal = Finished(await Read(secondStream), AiFinishReason.Stop); Assert.Equal(2, second.Ordinal);
        Assert.Null(secondTerminal.Usage.InputTokens); Assert.Null(secondTerminal.Usage.OutputTokens); Assert.Null(secondTerminal.Usage.TotalTokens);
        Assert.False(server.Requests[1].HasHeader("Cookie"), "Provider cookie was retained across rounds.");
        Assert.Equal(2, server.RequestCount); Assert.Equal(2, server.ConnectionCount);
        using var payload = server.Requests[1].Json(); var root = payload.RootElement;
        Assert.True(root.GetProperty("model").GetString() == Model, "Follow-up used a provider alias.");
        var messages = root.GetProperty("messages");
        Assert.True(messages.GetArrayLength() == 4, "Tool transcript cardinality changed.");
        Assert.True(messages[1].GetProperty("role").GetString() == "assistant", "Assistant transcript role changed.");
        Assert.True(messages[1].GetProperty("tool_calls").GetArrayLength() == 2, "Assistant call transcript changed.");
        for (var i = 0; i < 2; i++)
        {
            Assert.True(messages[i + 2].GetProperty("role").GetString() == "tool", "Tool transcript role changed.");
            Assert.True(messages[i + 2].GetProperty("tool_call_id").GetString() == calls[i].CallId, "Tool result identity/order changed.");
            using var body = AiHttpFixture.Json(messages[i + 2].GetProperty("content").GetString()!);
            Assert.True(body.RootElement.GetProperty("found").GetBoolean() == (i == 0), "Tool result body/order changed.");
        }
    }

    [Fact]
    public async Task BufferedToolsCannotEscapeWhileNativeDoneIsWithheld()
    {
        var release = AiHttpFixture.NewSignal(); var prefixSent = AiHttpFixture.NewSignal();
        var prefix = AiHttpFixture.Utf8(ChatText("leading") + ChatToolsWire[..^Done.Length]);
        var tail = AiHttpFixture.Utf8(Done);
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await exchange.HeadersAsync("text/event-stream", ct, length: prefix.Length + tail.Length);
            await exchange.WriteAsync(prefix, ct); prefixSent.TrySetResult();
            await release.Task.WaitAsync(ct); await exchange.WriteAsync(tail, ct);
        });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        try
        {
            var opened = await Open(client, turn, Request(tools: [Tool()])); await using var stream = opened.Stream;
            await using var reader = stream.ReadAllAsync().GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), "Leading public text was absent.");
            Same("leading", Require<AiTextDelta>(reader.Current).Text, "Leading public text changed.");
            await prefixSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var values = new List<AiRoundEvent>();
            async Task ObserveSignificantAsync()
            {
                while (await reader.MoveNextAsync())
                {
                    Assert.True(values.Count < 16385, "Public event bound exceeded before native completion.");
                    var value = reader.Current; values.Add(value);
                    if (value is AiToolIntentReady or AiRoundFinished or AiRoundFailed) return;
                }
                Assert.Fail("Stream ended without its native terminal.");
            }
            var pending = ObserveSignificantAsync();
            await Assert.ThrowsAsync<TimeoutException>(async () => await pending.WaitAsync(TimeSpan.FromMilliseconds(100)));
            release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
            while (await reader.MoveNextAsync()) values.Add(reader.Current);
            Finished(values, AiFinishReason.ToolCalls);
            Assert.True(values.OfType<AiToolIntentReady>().Count() == 2, "Completed tool intent cardinality changed.");
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ReusedRoundIdentityAndRoundBudgetCannotRedispatchAToolRound()
    {
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(ChatToolsWire), ct));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver());
        var id = Guid.NewGuid();
        await using (var turn = Turn(client))
        {
            var first = await Open(client, turn, Request(id, tools: [Tool()])); await using var stream = first.Stream;
            var events = await Read(stream); Finished(events, AiFinishReason.ToolCalls);
            var calls = events.OfType<AiToolIntentReady>().Select(value => value.Intent).ToArray();
            var history = new AiMessage[] { AiUserMessage.Create("question"), AiAssistantMessage.Create("", calls),
                AiToolMessage.Create(calls[0].CallId, calls[0].Name, AiJsonObject.CopyFrom("{}")), AiToolMessage.Create(calls[1].CallId, calls[1].Name, AiJsonObject.CopyFrom("{}")) };
            var repeated = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(id, history, [Tool()]), CancellationToken.None));
            Assert.Equal(AiFailureCode.InvalidRequest, repeated.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, repeated.Failure.Dispatch);
        }
        await using (var turn = Turn(client, Limits(rounds: 1)))
        {
            var first = await Open(client, turn, Request(tools: [Tool()])); await using var stream = first.Stream;
            var events = await Read(stream); Finished(events, AiFinishReason.ToolCalls);
            var calls = events.OfType<AiToolIntentReady>().Select(value => value.Intent).ToArray();
            var history = new AiMessage[] { AiUserMessage.Create("question"), AiAssistantMessage.Create("", calls),
                AiToolMessage.Create(calls[0].CallId, calls[0].Name, AiJsonObject.CopyFrom("{}")), AiToolMessage.Create(calls[1].CallId, calls[1].Name, AiJsonObject.CopyFrom("{}")) };
            var exhausted = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(messages: history, tools: [Tool()]), CancellationToken.None));
            Assert.Equal(AiFailureCode.BudgetExceeded, exhausted.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, exhausted.Failure.Dispatch);
        }
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task ToolBudgetIsCumulativeAcrossSuccessfulToolFollowupRounds()
    {
        var secondWire = ChatTool(0, "PRIVATE-call-c", "read_mapping", "{}") + ChatFinish("tool_calls") + Done;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream",
            AiHttpFixture.Utf8(exchange.Ordinal == 1 ? ChatToolsWire : secondWire), ct));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client, Limits(tools: 2));
        var first = await Open(client, turn, Request(tools: [Tool()])); await using var firstStream = first.Stream;
        var firstEvents = await Read(firstStream); Finished(firstEvents, AiFinishReason.ToolCalls);
        var calls = firstEvents.OfType<AiToolIntentReady>().Select(value => value.Intent).ToArray();
        Assert.True(calls.Length == 2, "Initial tool budget fixture changed.");
        var history = new AiMessage[] { AiUserMessage.Create("question"), AiAssistantMessage.Create("", calls),
            AiToolMessage.Create(calls[0].CallId, calls[0].Name, AiJsonObject.CopyFrom("{}")), AiToolMessage.Create(calls[1].CallId, calls[1].Name, AiJsonObject.CopyFrom("{}")) };
        var second = await Open(client, turn, Request(messages: history, tools: [Tool()])); await using var secondStream = second.Stream;
        Failed(await Read(secondStream), AiFailureCode.BudgetExceeded); Assert.Equal(2, server.RequestCount); Assert.Equal(2, server.ConnectionCount);
    }

    [Theory]
    [InlineData("length", AiFinishReason.OutputLimit)]
    [InlineData("content_filter", AiFinishReason.ContentFiltered)]
    [InlineData("refusal", AiFinishReason.Refused)]
    public async Task NonToolTerminalNeverReleasesBufferedIntents(string kind, AiFinishReason reason)
    {
        var call = ChatTool(0, "PRIVATE-call", "read_mapping", "{}");
        var wire = ChatText("partial") + call + (kind == "refusal" ? ChatData("{\"choices\":[{\"index\":0,\"delta\":{\"refusal\":\"PRIVATE refusal\"},\"finish_reason\":null}]}") : "") + ChatFinish(kind == "refusal" ? "stop" : kind) + Done;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn, Request(tools: [Tool()])); await using var stream = opened.Stream;
        var events = await Read(stream); Finished(events, reason);
        Assert.False(events.Any(value => value is AiToolIntentReady), "A non-tool terminal released intents.");
        Assert.False(Text(events).Contains("PRIVATE refusal", StringComparison.Ordinal), "Provider refusal escaped as text.");
        Same("partial", Text(events), "Partial public text was erased.");
    }

    [Theory]
    [InlineData("invalid-json", AiFailureCode.MalformedResponse)]
    [InlineData("duplicate-key", AiFailureCode.MalformedResponse)]
    [InlineData("duplicate-usage", AiFailureCode.MalformedResponse)]
    [InlineData("conflicting-tool-id", AiFailureCode.MalformedResponse)]
    [InlineData("duplicate-tool-id", AiFailureCode.MalformedResponse)]
    [InlineData("tool-array-arguments", AiFailureCode.MalformedResponse)]
    [InlineData("unpaired-json-surrogate", AiFailureCode.MalformedResponse)]
    [InlineData("eof-before-done", AiFailureCode.TruncatedResponse)]
    [InlineData("done-before-finish", AiFailureCode.TruncatedResponse)]
    [InlineData("content-after-finish", AiFailureCode.MalformedResponse)]
    public async Task SseMalformedAndTruncatedWireCannotBecomeSuccessOrExecutableTools(string variant, AiFailureCode expected)
    {
        var usage = ChatData("{\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}");
        var wire = variant switch
        {
            "invalid-json" => "data: {not-json}\n\n" + Done,
            "duplicate-key" => ChatData("{\"choices\":[],\"choices\":[]}") + Done,
            "duplicate-usage" => ChatFinish("stop") + usage + usage + Done,
            "conflicting-tool-id" => ChatTool(0, "PRIVATE-a", "read_mapping", "{}") + ChatTool(0, "PRIVATE-b", null, null) + ChatFinish("tool_calls") + Done,
            "duplicate-tool-id" => ChatTool(0, "PRIVATE-a", "read_mapping", "{}") + ChatTool(1, "PRIVATE-a", "read_mapping", "{}") + ChatFinish("tool_calls") + Done,
            "tool-array-arguments" => ChatTool(0, "PRIVATE-a", "read_mapping", "[]") + ChatFinish("tool_calls") + Done,
            "unpaired-json-surrogate" => "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\\ud800\"},\"finish_reason\":null}]}\n\n" + ChatFinish("stop") + Done,
            "eof-before-done" => ChatText("partial") + ChatFinish("stop"),
            "done-before-finish" => ChatText("partial") + Done,
            _ => ChatFinish("stop") + ChatText("late") + Done
        };
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn, Request(tools: [Tool()])); await using var stream = opened.Stream;
        var events = await Read(stream); Failed(events, expected); Assert.Equal(1, server.RequestCount);
        if (variant is "eof-before-done" or "done-before-finish") Same("partial", Text(events), "Truncation erased prior public text.");
    }


    [Fact]
    public async Task ReportedUsageAndPartialTextSurviveMissingNativeDone()
    {
        var wire = ChatText("partial") + ChatFinish("stop") + ChatData("{\"choices\":[],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3,\"total_tokens\":10}}");
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream; var events = await Read(stream);
        var failed = Failed(events, AiFailureCode.TruncatedResponse);
        Same("partial", Text(events), "Truncation erased partial text.");
        Assert.Equal(7, failed.Usage.InputTokens); Assert.Equal(3, failed.Usage.OutputTokens); Assert.Equal(10, failed.Usage.TotalTokens);
    }

    [Fact]
    public async Task SameFrameUsageIsRetainedWhenItsTextExhaustsTheTurnBudget()
    {
        var wire = ChatText("a") + ChatData("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"too much\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3,\"total_tokens\":10}}") + Done;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client, Limits(text: 1));
        var opened = await Open(client, turn); await using var stream = opened.Stream; var events = await Read(stream);
        var failed = Failed(events, AiFailureCode.BudgetExceeded);
        Same("a", Text(events), "Over-budget text escaped or erased earlier text.");
        Assert.Equal(7, failed.Usage.InputTokens); Assert.Equal(3, failed.Usage.OutputTokens); Assert.Equal(10, failed.Usage.TotalTokens);
        var observed = events.OfType<AiUsageObserved>().ToArray(); Assert.Single(observed);
        Assert.Equal(7, observed[0].Usage.InputTokens); Assert.Equal(3, observed[0].Usage.OutputTokens); Assert.Equal(10, observed[0].Usage.TotalTokens);
        Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
    }

    [Theory]
    [InlineData(AiDialect.ChatCompletionsSseV1, "depth")]
    [InlineData(AiDialect.ChatCompletionsSseV1, "members")]
    [InlineData(AiDialect.ChatCompletionsSseV1, "bytes")]
    [InlineData(AiDialect.OllamaChatNdjsonV1, "depth")]
    [InlineData(AiDialect.OllamaChatNdjsonV1, "members")]
    [InlineData(AiDialect.OllamaChatNdjsonV1, "bytes")]
    public async Task WellFormedToolArgumentsKeepBudgetCategoryAcrossParserAndValueObjectBoundaries(AiDialect dialect, string budget)
    {
        var arguments = budget switch
        {
            "depth" => "{\"nested\":" + new string('[', 16) + "0" + new string(']', 16) + "}",
            "members" => "{" + string.Join(",", Enumerable.Range(0, 2049).Select(i => "\"" + i.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\":0")) + "}",
            _ => "{\"value\":\"" + new string('x', 16384) + "\"}"
        };
        // Establish valid JSON independently with a larger depth bound, without
        // calling the public value factory that intentionally rejects budgets.
        using (var validated = JsonDocument.Parse(arguments, new JsonDocumentOptions { MaxDepth = 64 }))
            Assert.True(validated.RootElement.ValueKind == JsonValueKind.Object, "Argument fixture was not an object.");
        var wire = dialect == AiDialect.ChatCompletionsSseV1
            ? string.Concat(Enumerable.Range(0, (arguments.Length + 4095) / 4096).Select(i =>
                ChatTool(0, "PRIVATE-call", "read_mapping", arguments.Substring(i * 4096, Math.Min(4096, arguments.Length - i * 4096))))) + ChatFinish("tool_calls") + Done
            : "{\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"function\":{\"name\":\"read_mapping\",\"arguments\":" + arguments + "}}]},\"done\":true,\"done_reason\":\"stop\"}\n";
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync(Media(dialect), AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, dialect), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn, Request(tools: [Tool()])); await using var stream = opened.Stream;
        var events = await Read(stream); Failed(events, AiFailureCode.BudgetExceeded);
        Assert.False(events.Any(value => value is AiRoundFinished), "Over-budget arguments became a successful terminal.");
        Assert.Equal(1, server.ConnectionCount); Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public void PublicJsonFactoryStillUsesCategoricalArgumentExceptionForBudgetViolations()
    {
        var arguments = "{\"nested\":" + new string('[', 16) + "0" + new string(']', 16) + "}";
        var error = Assert.ThrowsAny<ArgumentException>(() => AiJsonObject.CopyFrom(arguments));
        Assert.Null(error.InnerException); Assert.False(error.ToString().Contains("nested", StringComparison.Ordinal), "Public factory leaked rejected JSON.");
    }

    [Theory]
    [InlineData("choice-index")]
    [InlineData("choices")]
    [InlineData("finish")]
    public async Task ValidButUnadmittedChatVariantsHaveExactUnsupportedCategory(string variant)
    {
        var wire = variant switch
        {
            "choice-index" => ChatData("{\"choices\":[{\"index\":1,\"delta\":{},\"finish_reason\":\"stop\"}]}") + Done,
            "choices" => ChatData("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"},{\"index\":1,\"delta\":{},\"finish_reason\":\"stop\"}]}") + Done,
            _ => ChatFinish("new_native_reason") + Done
        };
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream; Failed(await Read(stream), AiFailureCode.UnsupportedResponse);
    }

    [Theory]
    [InlineData(AiDialect.ChatCompletionsSseV1)]
    [InlineData(AiDialect.OllamaChatNdjsonV1)]
    public async Task InvalidRawUtf8AcrossNetworkFragmentsIsRefused(AiDialect dialect)
    {
        var prefix = AiHttpFixture.Utf8(dialect == AiDialect.ChatCompletionsSseV1
            ? "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" : "{\"message\":{\"role\":\"assistant\",\"content\":\"");
        var suffix = AiHttpFixture.Utf8(dialect == AiDialect.ChatCompletionsSseV1
            ? "\"},\"finish_reason\":null}]}\n\n" + ChatFinish("stop") + Done : "\"},\"done\":true,\"done_reason\":\"stop\"}\n");
        var wire = prefix.Concat(new byte[] { 0xf0, 0x28, 0x8c, 0xbc }).Concat(suffix).ToArray();
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync(Media(dialect), wire, ct, fragmented: true));
        await using var client = Client(Profile(server, dialect), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream; Failed(await Read(stream), AiFailureCode.MalformedResponse);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("depth")]
    [InlineData("members")]
    public async Task ParserCeilingsCountIgnoredMaterialAndRefuseWithoutPartialSuccess(string kind)
    {
        var hidden = kind switch
        {
            "line" => JsonSerializer.Serialize(new string('x', 131073)),
            "depth" => new string('[', 18) + "0" + new string(']', 18),
            _ => "{" + string.Join(",", Enumerable.Range(0, 2049).Select(i => "\"k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\":0")) + "}"
        };
        var wire = ChatData("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":null}],\"thinking\":" + hidden + "}") + ChatFinish("stop") + Done;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream; Failed(await Read(stream), AiFailureCode.BudgetExceeded);
    }

    [Fact]
    public async Task LargeTextIsSplitAtScalarBoundariesWithinThePublicDeltaCeiling()
    {
        var text = new string('a', 8191) + "😀z";
        var wire = ChatText(text) + ChatFinish("stop") + Done;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct, fragmented: true));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream; var events = await Read(stream); Finished(events, AiFinishReason.Stop);
        Same(text, Text(events), "Large scalar text changed.");
        var strict = new UTF8Encoding(false, true); var count = 0;
        foreach (var delta in events.OfType<AiTextDelta>())
        { Assert.True(strict.GetByteCount(delta.Text) <= 8192, "A public text delta exceeded its scalar byte limit."); count++; }
        Assert.True(count >= 2, "Large native text was not bounded into deltas.");
    }
}
