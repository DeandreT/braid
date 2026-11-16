using System.Text.Json;
using Braid.Ai;

namespace Braid.Tests;

public sealed partial class AiProviderTests
{
    private static string OllamaRecord(string content, bool done, string? reason = null,
        object? calls = null, long? input = null, long? output = null, string newline = "\n")
    {
        var message = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = content, ["thinking"] = "PRIVATE-hidden-thinking" };
        if (calls is not null) message["tool_calls"] = calls;
        var root = new Dictionary<string, object?>
        {
            ["model"] = "ignored-response-alias",
            ["message"] = message,
            ["done"] = done,
            ["metadata"] = new { ignored = "PRIVATE metadata" }
        };
        if (reason is not null) root["done_reason"] = reason;
        if (input is not null) root["prompt_eval_count"] = input;
        if (output is not null) root["eval_count"] = output;
        return JsonSerializer.Serialize(root) + newline;
    }
    private static object OllamaCall(string revision, int? index = null)
    {
        var function = new Dictionary<string, object?> { ["name"] = "read_mapping", ["arguments"] = new { revision } };
        if (index is not null) function["index"] = index.Value;
        return new { function };
    }

    [Fact]
    public async Task NdjsonFragmentsUnicodeCrLfAndFinalRecordWithoutNewlineKeepUnknownUsageUnknown()
    {
        // Literal multibyte UTF8, rather than JSON-escaped Unicode, crosses fragmented writes.
        var wire = "{\"message\":{\"role\":\"assistant\",\"content\":\"A😀\",\"thinking\":\"PRIVATE hidden\"},\"done\":false}\r\n" +
            "{\"message\":{\"role\":\"assistant\",\"content\":\"éZ\"},\"done\":true,\"done_reason\":\"stop\"}";
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/x-ndjson; charset=utf-8", AiHttpFixture.Utf8(wire), ct, fragmented: true));
        await using var client = Client(Profile(server, AiDialect.OllamaChatNdjsonV1, thinkFalse: true), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream; var events = await Read(stream);
        var terminal = Finished(events, AiFinishReason.Stop); Same("A😀éZ", Text(events), "NDJSON public text/Unicode/order changed.");
        Assert.Null(terminal.Usage.InputTokens); Assert.Null(terminal.Usage.OutputTokens); Assert.Null(terminal.Usage.TotalTokens);
        Assert.False(Text(events).Contains("PRIVATE", StringComparison.Ordinal), "Ignored NDJSON content escaped.");
        Assert.Equal(1, server.RequestCount); var request = server.Requests[0];
        Assert.True(request.IsPostAt("/api/chat"), "Ollama endpoint/method/version changed."); Assert.False(request.HasHeader("Authorization"), "Unauthenticated fixture received credentials.");
        using var document = request.Json(); var root = document.RootElement;
        Assert.True(root.GetProperty("model").GetString() == Model, "Ollama profile model changed."); Assert.True(root.GetProperty("stream").GetBoolean(), "Ollama streaming flag changed.");
        Assert.False(root.GetProperty("think").GetBoolean(), "Qualified think:false was not requested.");
        Assert.True(root.GetProperty("options").GetProperty("num_predict").GetInt32() == 128, "Ollama output cap changed.");
        Assert.False(root.TryGetProperty("stream_options", out _), "Ollama request used another dialect's options.");
    }

    [Fact]
    public async Task IdentityFreeRepeatedCallsAreSeparateOrderedIntentsAndToolResultsUseNativeObjectShape()
    {
        var firstWire = OllamaRecord("", false, calls: new[] { OllamaCall("same"), OllamaCall("same") }) +
            OllamaRecord("", true, "stop", input: 9, output: 2);
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/x-ndjson",
            exchange.Ordinal == 1 ? AiHttpFixture.Utf8(firstWire) : StopWire(AiDialect.OllamaChatNdjsonV1), ct, fragmented: true));
        await using var client = Client(Profile(server, AiDialect.OllamaChatNdjsonV1), new Resolver()); await using var turn = Turn(client);
        var roundId = Guid.NewGuid(); var first = await Open(client, turn, Request(roundId, tools: [Tool()])); await using var firstStream = first.Stream;
        var events = await Read(firstStream); var terminal = Finished(events, AiFinishReason.ToolCalls);
        Assert.Equal(9, terminal.Usage.InputTokens); Assert.Equal(2, terminal.Usage.OutputTokens); Assert.Null(terminal.Usage.TotalTokens);
        var calls = events.OfType<AiToolIntentReady>().Select(value => value.Intent).ToArray();
        Assert.True(calls.Length == 2, "Identity-free complete calls were collapsed.");
        Assert.True(calls[0].CallId != calls[1].CallId, "Identity-free calls reused one Braid identity.");
        foreach (var call in calls)
        {
            Assert.True(call.CallId.StartsWith("braid_" + roundId.ToString("N") + "_", StringComparison.Ordinal), "Ollama call identity was not round-scoped.");
            Assert.True(call.Name == "read_mapping", "Ollama call name changed.");
            using var arguments = AiHttpFixture.Json(call.Arguments.Json);
            Assert.True(arguments.RootElement.ValueKind == JsonValueKind.Object && arguments.RootElement.GetProperty("revision").GetString() == "same", "Ollama object arguments changed.");
        }
        var history = new AiMessage[] { AiUserMessage.Create("PRIVATE fixture question"), AiAssistantMessage.Create("", calls),
            AiToolMessage.Create(calls[0].CallId, calls[0].Name, AiJsonObject.CopyFrom("{\"found\":true}")),
            AiToolMessage.Create(calls[1].CallId, calls[1].Name, AiJsonObject.CopyFrom("{\"found\":false}")) };
        var second = await Open(client, turn, Request(messages: history, tools: [Tool()])); await using var secondStream = second.Stream;
        Finished(await Read(secondStream), AiFinishReason.Stop); Assert.Equal(2, second.Ordinal); Assert.Equal(2, server.RequestCount);
        using var payload = server.Requests[1].Json(); var root = payload.RootElement; var messages = root.GetProperty("messages");
        Assert.True(root.GetProperty("model").GetString() == Model, "Ollama follow-up used a response alias.");
        Assert.False(root.TryGetProperty("think", out _), "Unqualified think option appeared.");
        Assert.True(messages.GetArrayLength() == 4, "Ollama transcript cardinality changed.");
        var assistantCalls = messages[1].GetProperty("tool_calls"); Assert.True(assistantCalls.GetArrayLength() == 2, "Repeated assistant calls were collapsed.");
        Assert.True(assistantCalls[0].GetProperty("function").GetProperty("arguments").ValueKind == JsonValueKind.Object, "Ollama arguments were encoded as Chat fragments.");
        for (var i = 0; i < 2; i++)
        {
            Assert.True(messages[i + 2].GetProperty("role").GetString() == "tool", "Ollama tool role changed.");
            Assert.True(messages[i + 2].GetProperty("tool_name").GetString() == "read_mapping", "Ollama tool_name missing.");
            Assert.False(messages[i + 2].TryGetProperty("tool_call_id", out _), "Chat-only result identity leaked into Ollama.");
            using var body = AiHttpFixture.Json(messages[i + 2].GetProperty("content").GetString()!);
            Assert.True(body.RootElement.GetProperty("found").GetBoolean() == (i == 0), "Ollama result order/body changed.");
        }
    }

    [Fact]
    public async Task DistinctDeclaredIndicesRemainOrderedAcrossCompleteRecords()
    {
        var wire = OllamaRecord("", false, calls: new[] { OllamaCall("left", 0) }) +
            OllamaRecord("", false, calls: new[] { OllamaCall("right", 1) }) + OllamaRecord("", true, "stop");
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/x-ndjson", AiHttpFixture.Utf8(wire), ct, fragmented: true));
        await using var client = Client(Profile(server, AiDialect.OllamaChatNdjsonV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn, Request(tools: [Tool()])); await using var stream = opened.Stream; var values = await Read(stream);
        Finished(values, AiFinishReason.ToolCalls); var calls = values.OfType<AiToolIntentReady>().ToArray();
        Assert.True(calls.Length == 2, "Declared tool cardinality changed.");
        using var left = AiHttpFixture.Json(calls[0].Intent.Arguments.Json); using var right = AiHttpFixture.Json(calls[1].Intent.Arguments.Json);
        Assert.True(left.RootElement.GetProperty("revision").GetString() == "left", "First complete native call changed.");
        Assert.True(right.RootElement.GetProperty("revision").GetString() == "right", "Second complete native call changed.");
    }

    [Theory]
    [InlineData("negative-index", AiFailureCode.MalformedResponse)]
    [InlineData("duplicate-index", AiFailureCode.MalformedResponse)]
    [InlineData("conflicting-index", AiFailureCode.MalformedResponse)]
    [InlineData("string-arguments", AiFailureCode.MalformedResponse)]
    [InlineData("duplicate-key", AiFailureCode.MalformedResponse)]
    [InlineData("wrong-role", AiFailureCode.MalformedResponse)]
    [InlineData("nonboolean-done", AiFailureCode.MalformedResponse)]
    [InlineData("eof-without-done", AiFailureCode.TruncatedResponse)]
    [InlineData("unknown-finish", AiFailureCode.UnsupportedResponse)]
    [InlineData("error", AiFailureCode.ProviderUnavailable)]
    public async Task NdjsonStrictShapesIdentityAndNativeTerminalAreRequired(string variant, AiFailureCode expected)
    {
        var wire = variant switch
        {
            "negative-index" => OllamaRecord("", false, calls: new[] { OllamaCall("left", -1) }) + OllamaRecord("", true, "stop"),
            "duplicate-index" => OllamaRecord("", false, calls: new[] { OllamaCall("left", 0), OllamaCall("left", 0) }) + OllamaRecord("", true, "stop"),
            "conflicting-index" => OllamaRecord("", false, calls: new[] { OllamaCall("left", 0), OllamaCall("right", 0) }) + OllamaRecord("", true, "stop"),
            "string-arguments" => "{\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"function\":{\"name\":\"read_mapping\",\"arguments\":\"{}\"}}]},\"done\":true,\"done_reason\":\"stop\"}\n",
            "duplicate-key" => "{\"message\":{\"role\":\"assistant\",\"content\":\"x\"},\"done\":true,\"done\":false}\n",
            "wrong-role" => "{\"message\":{\"role\":\"tool\",\"content\":\"PRIVATE wrong role\"},\"done\":true,\"done_reason\":\"stop\"}\n",
            "nonboolean-done" => "{\"message\":{\"role\":\"assistant\",\"content\":\"x\"},\"done\":\"true\"}\n",
            "eof-without-done" => OllamaRecord("partial", false),
            "unknown-finish" => OllamaRecord("", true, "new_native_reason"),
            _ => "{\"error\":\"PRIVATE upstream failure\"}\n"
        };
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/x-ndjson", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, AiDialect.OllamaChatNdjsonV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn, Request(tools: [Tool()])); await using var stream = opened.Stream; var events = await Read(stream);
        Failed(events, expected); Assert.False(Text(events).Contains("PRIVATE", StringComparison.Ordinal), "NDJSON failure exposed ignored/error content.");
        if (variant == "eof-without-done") Same("partial", Text(events), "NDJSON truncation erased prior text.");
        Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task ExplicitDistinctIndicesAndLengthTerminalDoNotReleaseTools()
    {
        var wire = OllamaRecord("partial", false, calls: new[] { OllamaCall("left", 0), OllamaCall("right", 1) }) + OllamaRecord("", true, "length");
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/x-ndjson", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, AiDialect.OllamaChatNdjsonV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn, Request(tools: [Tool()])); await using var stream = opened.Stream; var events = await Read(stream);
        Finished(events, AiFinishReason.OutputLimit); Same("partial", Text(events), "Ollama partial text was erased.");
        Assert.False(events.Any(value => value is AiToolIntentReady), "Ollama length terminal released staged tools.");
    }

    [Theory]
    [InlineData(AiDialect.ChatCompletionsSseV1)]
    [InlineData(AiDialect.OllamaChatNdjsonV1)]
    public async Task WrongMediaTypeAndNonIdentityEncodingAreRejectedBeforeStreaming(AiDialect dialect)
    {
        foreach (var compressed in new[] { false, true })
        {
            await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync(compressed ? Media(dialect) : "application/json",
                StopWire(dialect), ct, extra: compressed ? "Content-Encoding: gzip\r\n" : ""));
            await using var client = Client(Profile(server, dialect), new Resolver()); await using var turn = Turn(client);
            var rejected = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(), CancellationToken.None));
            Assert.Equal(AiFailureCode.UnsupportedResponse, rejected.Failure.Code); Assert.Equal(AiDispatchState.Attempted, rejected.Failure.Dispatch);
            Assert.Equal(1, server.RequestCount);
        }
    }

    [Fact]
    public async Task NdjsonToolTurnLimitCountsRepeatedIdentityFreeCalls()
    {
        var wire = OllamaRecord("", false, calls: new[] { OllamaCall("same"), OllamaCall("same") }) + OllamaRecord("", true, "stop");
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/x-ndjson", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, AiDialect.OllamaChatNdjsonV1), new Resolver()); await using var turn = Turn(client, Limits(tools: 1));
        var opened = await Open(client, turn, Request(tools: [Tool()])); await using var stream = opened.Stream; Failed(await Read(stream), AiFailureCode.BudgetExceeded);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task OversizedIgnoredNdjsonRecordStillConsumesThePhysicalLineBudget()
    {
        var wire = OllamaRecord("", true, "stop");
        wire = wire.TrimEnd('\n').Insert(wire.TrimEnd('\n').Length - 1, ",\"ignored\":\"" + new string('x', 131073) + "\"") + "\n";
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/x-ndjson", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(Profile(server, AiDialect.OllamaChatNdjsonV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream; Failed(await Read(stream), AiFailureCode.BudgetExceeded);
    }

    [Fact]
    public void OptionalUsageIsNeverSynthesizedAndReportedBoundsRemainExact()
    {
        Assert.Null(AiTokenUsage.Unknown.InputTokens); Assert.Null(AiTokenUsage.Unknown.OutputTokens); Assert.Null(AiTokenUsage.Unknown.TotalTokens);
        var usage = AiTokenUsage.Create(1000000000000, null, null);
        Assert.Equal(1000000000000, usage.InputTokens); Assert.Null(usage.OutputTokens); Assert.Null(usage.TotalTokens);
        Assert.ThrowsAny<ArgumentException>(() => AiTokenUsage.Create(1000000000001, null, null));
    }
}
