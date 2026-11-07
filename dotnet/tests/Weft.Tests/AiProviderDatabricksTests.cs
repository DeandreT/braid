using System.Text.Json;
using Weft.Ai;

namespace Weft.Tests;

/// <summary>
/// Synthetic Azure Databricks Chat Completions streams with self-authored content, in the wire shapes the documentation
/// describes and that were observed against an Azure Databricks workspace, 2026-11-01:
/// - chunks are chat.completion.chunk objects with a non-null id; the first delta carries role and content, and some
///   models repeat role in every delta, including beside tool_calls;
/// - usage is on every chunk and cumulative, the finish chunk holding the total ("usage is returned in every chunk",
///   https://docs.databricks.com/aws/en/machine-learning/model-serving/score-foundation-models#prompt-caching). Claude
///   models send partial usage (null completion and total) with cache fields until the finish chunk; other models send
///   complete usage with prompt_tokens_details. Usage "might not be present on streaming responses" and a documented
///   response id may be null (https://learn.microsoft.com/azure/databricks/machine-learning/foundation-model-apis/api-reference#chat-response);
/// - reasoning models stream reasoning_content deltas without content before their answer;
/// - streams end with data: [DONE] (observed; MLflow's Databricks client,
///   https://github.com/mlflow/mlflow/blob/master/mlflow/deployments/databricks/__init__.py), with Content-Type
///   text/event-stream, optionally with charset=utf-8;
/// - tool calls are OpenAI-compatible (https://docs.databricks.com/aws/en/machine-learning/model-serving/function-calling);
/// - failures, including a rejected streaming request, are a non-2xx application/json error object with error_code and
///   message (observed; https://docs.databricks.com/api/workspace/errors).
/// </summary>
public sealed partial class AiProviderTests
{
    private const string ServingPath = "/serving-endpoints/chat/completions";
    private const string DatabricksIdentity = "\"id\":\"PRIVATE-chatcmpl\",\"object\":\"chat.completion.chunk\",\"created\":1792900000," +
        "\"model\":\"PRIVATE-served-model\"";
    // Complete usage as Llama and gpt-oss models send it on every chunk.
    private static string DatabricksUsage(int prompt, int completion) => ",\"usage\":{\"prompt_tokens\":" + prompt + ",\"completion_tokens\":" +
        completion + ",\"total_tokens\":" + (prompt + completion) + ",\"prompt_tokens_details\":{\"cached_tokens\":0}}";
    // Claude models: partial usage until the finish chunk, then complete, both with cache fields.
    private static string ClaudePartialUsage(int prompt) => ",\"usage\":{\"prompt_tokens\":" + prompt +
        ",\"completion_tokens\":null,\"total_tokens\":null,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}";
    private static string ClaudeUsage(int prompt, int completion) => ",\"usage\":{\"prompt_tokens\":" + prompt + ",\"completion_tokens\":" +
        completion + ",\"total_tokens\":" + (prompt + completion) + ",\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}";
    private static string DatabricksChunk(string delta, string usage, string? finish = null) => ChatData("{" + DatabricksIdentity +
        ",\"choices\":[{\"index\":0,\"delta\":" + delta + ",\"finish_reason\":" + (finish is null ? "null" : JsonSerializer.Serialize(finish)) + "}]" + usage + "}");
    private static string DatabricksText(string text, string usage = "") =>
        DatabricksChunk("{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) + "}", usage);
    private static string DatabricksReasoning(string text, string usage = "") =>
        DatabricksChunk("{\"role\":\"assistant\",\"reasoning_content\":" + JsonSerializer.Serialize(text) + "}", usage);
    private static string DatabricksFinish(string reason, string usage = "") => DatabricksChunk("{\"role\":\"assistant\",\"content\":\"\"}", usage, reason);
    private static string DatabricksTool(int index, string? id, string? name, string arguments, string usage = "") =>
        DatabricksChunk("{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"index\":" + index +
            (id is null ? "" : ",\"id\":" + JsonSerializer.Serialize(id) + ",\"type\":\"function\"") + ",\"function\":{" +
            (name is null ? "" : "\"name\":" + JsonSerializer.Serialize(name) + ",") + "\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}", usage);

    private static AiProfileSpec DatabricksProfile(AiHttpFixture server, string path = ServingPath) => AiProfileSpec.Create(Key,
        AiDialect.DatabricksChatCompletionsSseV1, Model, server.Endpoint(path), true, AiAuthentication.Bearer, "databricks",
        AiCapability.Text | AiCapability.FunctionTools, "fixture.metadata", 1, true, false);

    private static async Task<List<AiRoundEvent>> DatabricksRound(string wire, string media = "text/event-stream", AiTurnLimits? limits = null,
        AiRoundRequest? request = null)
    {
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync(media, AiHttpFixture.Utf8(wire), ct, fragmented: true));
        await using var client = Client(DatabricksProfile(server), new Resolver()); await using var turn = Turn(client, limits);
        var opened = await Open(client, turn, request); await using var stream = opened.Stream;
        return await Read(stream);
    }

    private static void Counts(AiTokenUsage usage, long? input, long? output, long? total)
    {
        Assert.Equal(input, usage.InputTokens); Assert.Equal(output, usage.OutputTokens); Assert.Equal(total, usage.TotalTokens);
    }

    [Fact]
    public async Task DatabricksCumulativeUsageReportsTheFinalTotalOnceAndReasoningIsNotText()
    {
        // A reasoning model: reasoning_content deltas first, then content; usage cumulative on every chunk.
        var wire = DatabricksReasoning("PRIVATE-reasoning one", DatabricksUsage(40, 1)) + DatabricksReasoning("PRIVATE-reasoning two", DatabricksUsage(40, 1)) +
            DatabricksReasoning("PRIVATE-reasoning three", DatabricksUsage(40, 2)) + DatabricksText("invoice.total comes", DatabricksUsage(40, 7)) +
            DatabricksText(" from", DatabricksUsage(40, 12)) + DatabricksText(" line", DatabricksUsage(40, 17)) +
            DatabricksText(" totals.", DatabricksUsage(40, 22)) + DatabricksText("", DatabricksUsage(40, 27)) + DatabricksFinish("stop", DatabricksUsage(40, 27)) + Done;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream; charset=utf-8", AiHttpFixture.Utf8(wire), ct, fragmented: true));
        await using var client = Client(DatabricksProfile(server), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream;
        var events = await Read(stream); var terminal = Finished(events, AiFinishReason.Stop);
        Same("invoice.total comes from line totals.", Text(events), "Databricks text changed.");
        Assert.False(Text(events).Contains("PRIVATE", StringComparison.Ordinal), "Reasoning or metadata escaped as text.");
        var usage = Assert.Single(events.OfType<AiUsageObserved>());
        Assert.True(events.IndexOf(usage) == events.Count - 2, "Databricks usage was published before the stream completed.");
        Counts(usage.Usage, 40, 27, 67); Counts(terminal.Usage, 40, 27, 67);

        var request = Assert.Single(server.Requests);
        Assert.True(request.IsPostAt(ServingPath), "Databricks path changed.");
        Assert.True(request.HeaderIs("Authorization", "Bearer " + Token), "Databricks bearer changed.");
        Assert.True(request.HeaderIs("Accept", "text/event-stream"), "Databricks stream media type changed.");
        using var document = request.Json(); var root = document.RootElement;
        Assert.True(root.GetProperty("model").GetString() == Model, "Serving endpoint name changed.");
        Assert.True(root.GetProperty("stream").GetBoolean(), "Streaming request flag changed.");
        Assert.True(root.GetProperty("max_tokens").GetInt32() == 128, "Output cap changed.");
        foreach (var unsent in new[] { "max_completion_tokens", "store", "n", "stream_options", "think", "options" })
            Assert.False(root.TryGetProperty(unsent, out _), "Databricks request carried a field Weft does not send it.");
    }

    [Fact]
    public async Task DatabricksPartialUsageIsIgnoredUntilTheCompleteFinishReport()
    {
        var wire = DatabricksText("invoice.total", ClaudePartialUsage(14)) + DatabricksText(" is summed.", ClaudePartialUsage(14)) +
            DatabricksFinish("stop", ClaudeUsage(14, 6)) + Done;
        var events = await DatabricksRound(wire);
        Counts(Finished(events, AiFinishReason.Stop).Usage, 14, 6, 20);
        Counts(Assert.Single(events.OfType<AiUsageObserved>()).Usage, 14, 6, 20);
    }

    [Theory]
    [InlineData("partial-after-complete")]
    [InlineData("empty-after-complete")]
    [InlineData("missing-total-after-complete")]
    public async Task DatabricksIncompleteUsageNeverReplacesKnownCounts(string variant)
    {
        var later = variant switch
        {
            "partial-after-complete" => ClaudePartialUsage(99),
            "empty-after-complete" => ",\"usage\":{}",
            _ => ",\"usage\":{\"prompt_tokens\":99,\"completion_tokens\":99}"
        };
        var wire = DatabricksText("first", ClaudeUsage(14, 5)) + DatabricksText(" second", later) + DatabricksFinish("stop", later) + Done;
        var events = await DatabricksRound(wire);
        Counts(Finished(events, AiFinishReason.Stop).Usage, 14, 5, 19);
    }

    [Fact]
    public async Task DatabricksPartialUsageAloneStaysUnknown()
    {
        var events = await DatabricksRound(DatabricksText("ok", ClaudePartialUsage(14)) + DatabricksFinish("stop", ClaudePartialUsage(14)) + Done);
        Assert.DoesNotContain(events, value => value is AiUsageObserved);
        Counts(Finished(events, AiFinishReason.Stop).Usage, null, null, null);
    }

    [Fact]
    public async Task DatabricksUsageOnlyChunkAfterTheFinishIsTheReportedTotal()
    {
        var wire = DatabricksText("ok", DatabricksUsage(8, 1)) + DatabricksFinish("stop") +
            ChatData("{" + DatabricksIdentity + ",\"choices\":[]" + DatabricksUsage(8, 2) + "}") + Done;
        var events = await DatabricksRound(wire);
        Counts(Finished(events, AiFinishReason.Stop).Usage, 8, 2, 10);
        Assert.Single(events.OfType<AiUsageObserved>());
    }

    [Fact]
    public async Task DatabricksStreamsWithoutUsageLeaveItUnknown()
    {
        // The documented response example carries a null id.
        var wire = ChatData("{\"id\":null,\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"}," +
            "\"finish_reason\":null}]}") + DatabricksFinish("stop") + Done;
        var events = await DatabricksRound(wire);
        Assert.DoesNotContain(events, value => value is AiUsageObserved);
        Counts(Finished(events, AiFinishReason.Stop).Usage, null, null, null);
    }

    [Fact]
    public async Task DatabricksToolCallsWithRepeatedRoleCompleteAndTheFollowUpOmitsAssistantContent()
    {
        var tools = DatabricksText("Checking.", ClaudePartialUsage(90)) +
            DatabricksTool(0, "PRIVATE-call-a", "read_mapping", "", ClaudePartialUsage(90)) +
            DatabricksTool(0, null, null, "{\"revision\":", ClaudePartialUsage(90)) + DatabricksTool(0, null, null, "\"left\"}", ClaudePartialUsage(90)) +
            DatabricksFinish("tool_calls", ClaudeUsage(90, 12)) + Done;
        var answer = DatabricksText("found", ClaudePartialUsage(120)) + DatabricksFinish("stop", ClaudeUsage(120, 2)) + Done;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream",
            AiHttpFixture.Utf8(exchange.Ordinal == 1 ? tools : answer), ct, fragmented: true));
        await using var client = Client(DatabricksProfile(server), new Resolver()); await using var turn = Turn(client);
        var first = await Open(client, turn, Request(tools: [Tool()])); await using var firstStream = first.Stream;
        var firstEvents = await Read(firstStream);
        Counts(Finished(firstEvents, AiFinishReason.ToolCalls).Usage, 90, 12, 102);
        var call = Assert.Single(firstEvents.OfType<AiToolIntentReady>()).Intent;
        Same("PRIVATE-call-a", call.CallId, "Databricks tool identity changed.");
        using (var arguments = AiHttpFixture.Json(call.Arguments.Json))
            Assert.True(arguments.RootElement.GetProperty("revision").GetString() == "left", "Fragmented Databricks arguments changed.");

        var transcript = new AiMessage[] { AiUserMessage.Create("PRIVATE fixture question"), AiAssistantMessage.Create(Text(firstEvents), [call]),
            AiToolMessage.Create(call.CallId, call.Name, AiJsonObject.CopyFrom("{\"found\":true}")) };
        var second = await Open(client, turn, Request(messages: transcript, tools: [Tool()])); await using var secondStream = second.Stream;
        var secondEvents = await Read(secondStream); Finished(secondEvents, AiFinishReason.Stop);
        Same("found", Text(secondEvents), "Databricks follow-up text changed.");
        using var payload = server.Requests[1].Json(); var messages = payload.RootElement.GetProperty("messages");
        Assert.True(messages[1].GetProperty("role").GetString() == "assistant", "Assistant transcript role changed.");
        Assert.False(messages[1].TryGetProperty("content", out _), "The Databricks tool-call message form changed.");
        Assert.True(messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString() == call.CallId, "Tool call transcript changed.");
        Assert.True(messages[2].GetProperty("tool_call_id").GetString() == call.CallId, "Tool result identity changed.");
        Assert.True(payload.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString() == "read_mapping",
            "Tool definitions changed.");
    }

    [Theory]
    [InlineData(429, "RESOURCE_EXHAUSTED", AiFailureCode.ProviderRateLimited)]
    [InlineData(403, "PERMISSION_DENIED", AiFailureCode.ProviderAuthentication)]
    [InlineData(401, "UNAUTHENTICATED", AiFailureCode.ProviderAuthentication)]
    [InlineData(404, "ENDPOINT_NOT_FOUND", AiFailureCode.ProviderRejected)]
    [InlineData(400, "INVALID_PARAMETER_VALUE", AiFailureCode.ProviderRejected)]
    [InlineData(503, "TEMPORARILY_UNAVAILABLE", AiFailureCode.ProviderUnavailable)]
    public async Task DatabricksJsonErrorsOnAStreamRequestFailByStatusWithoutTheirMessage(int status, string code, AiFailureCode expected)
    {
        var body = "{\"error_code\":\"" + code + "\",\"message\":\"PRIVATE provider detail\"}";
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json", AiHttpFixture.Utf8(body), ct, status: status));
        await using var client = Client(DatabricksProfile(server), new Resolver()); await using var turn = Turn(client);
        var rejected = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(), CancellationToken.None));
        Assert.Equal(expected, rejected.Failure.Code); Assert.Equal(AiDispatchState.Attempted, rejected.Failure.Dispatch);
        Assert.Equal(nameof(AiFailure), rejected.Failure.ToString());
    }

    [Fact]
    public async Task DatabricksSuccessWithoutAnEventStreamIsUnsupported()
    {
        var body = "{\"id\":\"PRIVATE\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"PRIVATE\"}," +
            "\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json", AiHttpFixture.Utf8(body), ct));
        await using var client = Client(DatabricksProfile(server), new Resolver()); await using var turn = Turn(client);
        var rejected = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(), CancellationToken.None));
        Assert.Equal(AiFailureCode.UnsupportedResponse, rejected.Failure.Code); Assert.Equal(AiDispatchState.Attempted, rejected.Failure.Dispatch);
    }

    [Fact]
    public async Task DatabricksErrorObjectsInsideAStreamFailTheRoundAndKeepReceivedUsage()
    {
        var wire = DatabricksText("partial", DatabricksUsage(10, 1)) +
            ChatData("{\"error_code\":\"INTERNAL_ERROR\",\"message\":\"PRIVATE provider detail\"}") + DatabricksFinish("stop") + Done;
        var events = await DatabricksRound(wire);
        Counts(Failed(events, AiFailureCode.ProviderUnavailable).Usage, 10, 1, 11);
        Assert.DoesNotContain(events, value => value is AiUsageObserved);
        Assert.False(Text(events).Contains("PRIVATE", StringComparison.Ordinal), "Databricks error detail escaped.");
    }

    [Fact]
    public async Task DatabricksBudgetFailuresKeepTheUsageAlreadyReceived()
    {
        // Text budget: the over-budget frame's own usage was already latched.
        var text = DatabricksText("abcd", DatabricksUsage(10, 1)) + DatabricksText("efghijkl", DatabricksUsage(10, 2)) + DatabricksFinish("stop", DatabricksUsage(10, 3)) + Done;
        var events = await DatabricksRound(text, limits: Limits(text: 6));
        Counts(Failed(events, AiFailureCode.BudgetExceeded).Usage, 10, 2, 12);
        Same("abcd", Text(events), "Over-budget text escaped or erased earlier text.");

        // Tool cap: two calls against a cap of one fail at the terminal, after its usage.
        var tools = DatabricksTool(0, "PRIVATE-a", "read_mapping", "{}", ClaudePartialUsage(30)) + DatabricksTool(1, "PRIVATE-b", "read_mapping", "{}", ClaudePartialUsage(30)) +
            DatabricksFinish("tool_calls", ClaudeUsage(30, 9)) + Done;
        events = await DatabricksRound(tools, limits: Limits(tools: 1), request: Request(tools: [Tool()]));
        Counts(Failed(events, AiFailureCode.BudgetExceeded).Usage, 30, 9, 39);

        // Truncation after usage.
        events = await DatabricksRound(DatabricksText("partial", DatabricksUsage(10, 4)));
        Counts(Failed(events, AiFailureCode.TruncatedResponse).Usage, 10, 4, 14);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabricksCancellationOrDeadlineKeepsTheUsageAlreadyReceived(bool deadline)
    {
        var clock = new FixtureClock(); var closed = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await exchange.HeadersAsync("text/event-stream", ct);
            await exchange.WriteAsync(AiHttpFixture.Utf8(DatabricksText("first", DatabricksUsage(21, 3))), ct);
            await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult();
        });
        await using var client = Client(DatabricksProfile(server), new Resolver(), clock);
        using var cancellation = new CancellationTokenSource();
        await using var turn = Turn(client, Limits(duration: TimeSpan.FromSeconds(10)), cancellation.Token);
        var opened = await Open(client, turn); await using var stream = opened.Stream;
        await using var reader = stream.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), "The first pull did not produce a delta.");
        var events = new List<AiRoundEvent> { reader.Current }; Require<AiTextDelta>(reader.Current);
        if (deadline) clock.Advance(TimeSpan.FromSeconds(11)); else cancellation.Cancel();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        while (await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
        { Assert.True(events.Count < 16385, "Public event bound exceeded."); events.Add(reader.Current); }
        Counts(Failed(events, deadline ? AiFailureCode.DeadlineExceeded : AiFailureCode.Cancelled).Usage, 21, 3, 24);
    }

    [Fact]
    public async Task OtherChatCompletionsProvidersStillRefuseRepeatedOrEarlyUsageAndNullIds()
    {
        foreach (var wire in new[]
        {
            ChatText("a") + ChatData("{\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}") + ChatFinish("stop") + Done,
            ChatData("{\"id\":null,\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"},\"finish_reason\":null}]}") + ChatFinish("stop") + Done
        })
        {
            await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct));
            await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
            var opened = await Open(client, turn); await using var stream = opened.Stream;
            Failed(await Read(stream), AiFailureCode.MalformedResponse);
        }
    }

    [Theory]
    [InlineData("https://adb-1234567890123456.7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, true)]
    [InlineData("https://adb-1.12.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, true)]
    [InlineData("https://ADB-1234567890123456.7.AzureDatabricks.net:443/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, true)]
    // Unity AI Gateway is deferred until its endpoint is confirmed.
    [InlineData("https://adb-1234567890123456.7.azuredatabricks.net/ai-gateway/mlflow/v1/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("http://adb-1234567890123456.7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1234567890123456.7.azuredatabricks.net:8443/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1234567890123456.7.azuredatabricks.net./serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1234567890123456.7.azuredatabrícks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://аdb-1234567890123456.7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://www.adb-1234567890123456.7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1234567890123456.7.privatelink.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://accounts.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://westus.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-.7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-12a4.7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1234567890123456.x7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://dbc-1234.7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.azuredatabricks.net.example.invalid/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.notazuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://dbc-1234.cloud.databricks.com/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://10.0.0.4/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.azuredatabricks.net/serving-endpoints/my-endpoint/invocations", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.azuredatabricks.net/serving-endpoints/chat/completions/", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.azuredatabricks.net/serving-endpoints/responses", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.azuredatabricks.net/v1/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.azuredatabricks.net/openai/v1/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.azuredatabricks.net/serving-endpoints/chat/completions?model=x", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.azuredatabricks.net/serving-endpoints/chat/completions#x", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://token:PRIVATE@adb-1.7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.DatabricksChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.ChatCompletionsSseV1, false)]
    [InlineData("https://adb-1.7.azuredatabricks.net/serving-endpoints/chat/completions", AiDialect.OllamaChatNdjsonV1, false)]
    public void DatabricksEndpointsAdmitOnlyTheServingPathOnAnAzureDatabricksWorkspaceHost(string endpoint, AiDialect dialect, bool admitted)
    {
        // Every case is a well-formed URI, so a refusal is the admission rule's, not the parser's.
        var uri = new Uri(endpoint);
        AiProfileSpec Create() => AiProfileSpec.Create(Key, dialect, Model, uri, false, AiAuthentication.None, null,
            AiCapability.Text, "fixture.metadata", 1, true, false);
        if (admitted) Assert.Equal(uri, Create().Endpoint);
        else Assert.ThrowsAny<ArgumentException>(Create);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4100/serving-endpoints/chat/completions", true)]
    [InlineData("http://[::1]:4100/serving-endpoints/chat/completions", true)]
    [InlineData("http://127.0.0.1:4100/ai-gateway/mlflow/v1/chat/completions", false)]
    [InlineData("https://127.0.0.1:4100/serving-endpoints/chat/completions", false)]
    [InlineData("http://adb-1.7.azuredatabricks.net/serving-endpoints/chat/completions", false)]
    [InlineData("http://127.0.0.1:4100/v1/chat/completions", false)]
    public void DatabricksLoopbackFixturesStayPlainHttpLoopbackOnly(string endpoint, bool admitted)
    {
        AiProfileSpec Create() => AiProfileSpec.Create(Key, AiDialect.DatabricksChatCompletionsSseV1, Model, new Uri(endpoint), true,
            AiAuthentication.None, null, AiCapability.Text, "fixture.metadata", 1, true, false);
        if (admitted) Assert.Equal(new Uri(endpoint), Create().Endpoint);
        else Assert.ThrowsAny<ArgumentException>(Create);
        Assert.ThrowsAny<ArgumentException>(() => AiProfileSpec.Create(Key, AiDialect.DatabricksChatCompletionsSseV1, Model, new Uri(endpoint), true,
            AiAuthentication.None, null, AiCapability.Text, "fixture.metadata", 1, true, requestThinkFalse: true));
    }
}
