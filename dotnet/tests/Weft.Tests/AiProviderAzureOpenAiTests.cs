using Weft.Ai;

namespace Weft.Tests;

/// <summary>Synthetic Azure OpenAI v1 Chat Completions streams, recorded in the documented wire shape.</summary>
public sealed partial class AiProviderTests
{
    private const string AzurePath = "/openai/v1/chat/completions";
    private const string AzureSafe = "{\"hate\":{\"filtered\":false,\"severity\":\"safe\"},\"self_harm\":{\"filtered\":false,\"severity\":\"safe\"}," +
        "\"sexual\":{\"filtered\":false,\"severity\":\"safe\"},\"violence\":{\"filtered\":false,\"severity\":\"safe\"}}";
    private const string AzureIdentity = "\"created\":1792900000,\"id\":\"chatcmpl-PRIVATE\",\"model\":\"PRIVATE-deployment-model\"," +
        "\"object\":\"chat.completion.chunk\",\"system_fingerprint\":\"fp_PRIVATE\"";
    // Prompt results precede every completion, with empty identity fields and no choices.
    private static string AzurePrompt => ChatData("{\"choices\":[],\"created\":0,\"id\":\"\",\"model\":\"\",\"object\":\"\"," +
        "\"prompt_filter_results\":[{\"prompt_index\":0,\"content_filter_results\":" + AzureSafe + "}]}");
    private static string AzureText(string text, string filter = AzureSafe) => ChatData("{\"choices\":[{\"content_filter_results\":" + filter +
        ",\"delta\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(text) + "},\"finish_reason\":null,\"index\":0,\"logprobs\":null}]," +
        AzureIdentity + "}");
    private static string AzureFinish(string reason, string filter = "{}") => ChatData("{\"choices\":[{\"content_filter_results\":" + filter +
        ",\"delta\":{},\"finish_reason\":\"" + reason + "\",\"index\":0,\"logprobs\":null}]," + AzureIdentity + "}");
    // An asynchronous filter's result for text already sent: no delta, offsets into the streamed text.
    private static string AzureAnnotation(string? finish = null, string filter = AzureSafe) => ChatData("{\"choices\":[{\"index\":0,\"finish_reason\":" +
        (finish is null ? "null" : "\"" + finish + "\"") + ",\"content_filter_results\":" + filter +
        ",\"content_filter_offsets\":{\"check_offset\":12,\"start_offset\":12,\"end_offset\":44}}],\"created\":0,\"id\":\"\",\"model\":\"\",\"object\":\"\",\"usage\":null}");
    private static string AzureUsage => ChatData("{\"choices\":[]," + AzureIdentity + ",\"usage\":{\"completion_tokens\":7," +
        "\"completion_tokens_details\":{\"accepted_prediction_tokens\":0,\"reasoning_tokens\":0,\"rejected_prediction_tokens\":0}," +
        "\"prompt_tokens\":42,\"prompt_tokens_details\":{\"cached_tokens\":0},\"total_tokens\":49}}");
    private static string AzureFirst => ChatData("{\"choices\":[{\"content_filter_results\":{},\"delta\":{\"content\":\"\",\"refusal\":null," +
        "\"role\":\"assistant\"},\"finish_reason\":null,\"index\":0,\"logprobs\":null}]," + AzureIdentity + ",\"obfuscation\":\"PRIVATE\"}");

    internal static string AzureDefaultFilterWire => AzurePrompt + AzureFirst + AzureText("invoice.total comes") + AzureText(" from line totals.") +
        AzureFinish("stop") + AzureUsage + Done;
    private static string AzureAsynchronousFilterWire => AzurePrompt + AzureFirst + AzureText("invoice.total comes", "{}") + AzureAnnotation() +
        AzureText(" from line totals.", "{}") + AzureFinish("stop") + AzureAnnotation() + AzureUsage + Done;

    private static AiProfileSpec AzureProfile(AiHttpFixture server) => AiProfileSpec.Create(Key, AiDialect.ChatCompletionsSseV1, Model,
        server.Endpoint(AzurePath), true, AiAuthentication.Bearer, "azure-openai", AiCapability.Text | AiCapability.FunctionTools,
        "fixture.metadata", 1, true, false);

    [Theory]
    [InlineData("default")]
    [InlineData("asynchronous")]
    public async Task AzureOpenAiFilterAnnotationsAndExtraFieldsReadAsTheSameCompletion(string filter)
    {
        var wire = filter == "default" ? AzureDefaultFilterWire : AzureAsynchronousFilterWire;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream; charset=utf-8", AiHttpFixture.Utf8(wire), ct, fragmented: true));
        await using var client = Client(AzureProfile(server), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream;
        var events = await Read(stream); var terminal = Finished(events, AiFinishReason.Stop);
        Same("invoice.total comes from line totals.", Text(events), "Azure text changed.");
        Assert.Equal(42, terminal.Usage.InputTokens); Assert.Equal(7, terminal.Usage.OutputTokens); Assert.Equal(49, terminal.Usage.TotalTokens);
        Assert.False(Text(events).Contains("PRIVATE", StringComparison.Ordinal), "Azure metadata escaped as text.");
        var request = server.Requests[0];
        Assert.True(request.IsPostAt(AzurePath), "Azure OpenAI v1 path changed.");
        Assert.True(request.HeaderIs("Authorization", "Bearer " + Token), "Azure bearer changed.");
    }

    [Theory]
    [InlineData("default")]
    [InlineData("asynchronous")]
    [InlineData("after-stop")]
    public async Task AzureOpenAiContentFilterSignalsEndTheRoundAsFiltered(string signal)
    {
        const string filtered = "{\"self_harm\":{\"filtered\":true,\"severity\":\"medium\"}}";
        var wire = AzurePrompt + AzureFirst + AzureText("partial") + ChatTool(0, "PRIVATE-call", "read_mapping", "{}") + signal switch
        {
            "default" => AzureFinish("content_filter", filtered),
            "asynchronous" => AzureAnnotation("content_filter", filtered),
            _ => AzureFinish("tool_calls") + AzureAnnotation("content_filter", filtered)
        } + Done;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(AzureProfile(server), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn, Request(tools: [Tool()])); await using var stream = opened.Stream;
        var events = await Read(stream); Finished(events, AiFinishReason.ContentFiltered);
        Assert.False(events.Any(value => value is AiToolIntentReady), "A filtered completion released tool intents.");
        Same("partial", Text(events), "Filtered completion erased its streamed text.");
    }

    [Theory]
    [InlineData("text", AiFailureCode.MalformedResponse)]
    [InlineData("empty-delta", AiFailureCode.MalformedResponse)]
    [InlineData("usage", AiFailureCode.MalformedResponse)]
    [InlineData("stop", AiFailureCode.MalformedResponse)]
    [InlineData("missing-choices", AiFailureCode.MalformedResponse)]
    [InlineData("two-choices", AiFailureCode.UnsupportedResponse)]
    [InlineData("second-choice", AiFailureCode.UnsupportedResponse)]
    public async Task AzureOpenAiAnnotationsCannotCarryTextToolsUsageOrOtherFinishes(string variant, AiFailureCode expected)
    {
        const string empty = "\"created\":0,\"id\":\"\",\"model\":\"\",\"object\":\"\"";
        var annotation = variant switch
        {
            "text" => ChatData("{\"choices\":[{\"index\":0,\"finish_reason\":null,\"delta\":{\"content\":\"PRIVATE late\"}}]," + empty + "}"),
            "empty-delta" => ChatData("{\"choices\":[{\"index\":0,\"finish_reason\":null,\"delta\":{}}]," + empty + "}"),
            "usage" => ChatData("{\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}," + empty + "}"),
            "stop" => AzureAnnotation("stop"),
            "missing-choices" => ChatData("{" + empty + ",\"prompt_filter_results\":[]}"),
            "two-choices" => ChatData("{\"choices\":[{\"index\":0,\"finish_reason\":null},{\"index\":1,\"finish_reason\":null}]," + empty + "}"),
            _ => ChatData("{\"choices\":[{\"index\":1,\"finish_reason\":null,\"content_filter_results\":{}}]," + empty + "}")
        };
        var wire = AzurePrompt + AzureText("partial") + annotation + AzureFinish("stop") + Done;
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(wire), ct));
        await using var client = Client(AzureProfile(server), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream;
        var events = await Read(stream); Failed(events, expected);
        Assert.False(Text(events).Contains("PRIVATE", StringComparison.Ordinal), "Annotation text escaped.");
    }

    [Theory]
    [InlineData("https://example.invalid/openai/v1/chat/completions", AiDialect.ChatCompletionsSseV1, true)]
    [InlineData("https://example.invalid/v1/chat/completions", AiDialect.ChatCompletionsSseV1, true)]
    [InlineData("https://example.invalid/openai/v1/chat/completions/", AiDialect.ChatCompletionsSseV1, false)]
    [InlineData("https://example.invalid/openai/deployments/model/chat/completions", AiDialect.ChatCompletionsSseV1, false)]
    [InlineData("https://example.invalid/other/v1/chat/completions", AiDialect.ChatCompletionsSseV1, false)]
    [InlineData("https://example.invalid/openai/v1/chat/completions?api-version=preview", AiDialect.ChatCompletionsSseV1, false)]
    [InlineData("https://example.invalid/openai/v1/chat/completions", AiDialect.OllamaChatNdjsonV1, false)]
    public void ChatCompletionsEndpointsAdmitOnlyTheOpenAiAndAzureOpenAiPaths(string endpoint, AiDialect dialect, bool admitted)
    {
        AiProfileSpec Create() => AiProfileSpec.Create(Key, dialect, Model, new Uri(endpoint), false, AiAuthentication.None, null,
            AiCapability.Text, "fixture.metadata", 1, true, false);
        if (admitted) Assert.Equal(new Uri(endpoint), Create().Endpoint);
        else Assert.ThrowsAny<ArgumentException>(Create);
    }

    [Fact]
    public void BearerCopiesAreIndependentOfTheirSourceAndOfEachOther()
    {
        using var source = AiBearerSecret.CopyFrom(Token.AsSpan());
        var copy = source.Copy();
        source.Dispose();
        Same(Token, copy.HeaderValue(), "A copy depended on its source.");
        Assert.Equal(nameof(AiBearerSecret), copy.ToString());
        copy.Dispose();
        Assert.Throws<ObjectDisposedException>(() => copy.Copy());
        Assert.Throws<ObjectDisposedException>(() => source.Copy());
    }
}
