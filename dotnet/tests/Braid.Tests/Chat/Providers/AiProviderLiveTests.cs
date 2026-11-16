using Braid.Ai;

namespace Braid.Tests;

/// <summary>Opt-in checks against an actual local Ollama service; ordinary runs skip them.</summary>
public sealed class LiveOllamaFactAttribute : FactAttribute
{
    internal const string EndpointVariable = "BRAID_LIVE_OLLAMA_ENDPOINT";
    internal const string ModelVariable = "BRAID_LIVE_OLLAMA_MODEL";

    public LiveOllamaFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EndpointVariable)) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ModelVariable)))
            Skip = $"Set {EndpointVariable} (loopback http://127.0.0.1:<port>/api/chat) and {ModelVariable} to run live provider checks.";
    }
}

public sealed partial class AiProviderTests
{
    private static readonly AiProfileKey LiveKey = AiProfileKey.Create("live-ollama", 1);

    // A local service is reached over loopback HTTP, which profiles admit only through the loopback flag.
    // Mapping metadata may go to it because it stays on this machine.
    private static AiRoundClient LiveClient() => new(AiProfileCatalog.Create([AiProfileSpec.Create(LiveKey,
        AiDialect.OllamaChatNdjsonV1, Environment.GetEnvironmentVariable(LiveOllamaFactAttribute.ModelVariable)!,
        new Uri(Environment.GetEnvironmentVariable(LiveOllamaFactAttribute.EndpointVariable)!), true, AiAuthentication.None, null,
        AiCapability.Text | AiCapability.FunctionTools, "live.metadata", 1, true, false)]), new Resolver(), TimeProvider.System);

    private static AiTurnLease LiveTurn(AiRoundClient client) =>
        Require<TurnOpened>(client.OpenTurn(LiveKey, Limits(TimeSpan.FromSeconds(170)), CancellationToken.None)).Turn;

    [LiveOllamaFact]
    public async Task LiveOllamaTextRoundStreamsTextWithOneTerminalAndKnownUsage()
    {
        await using var client = LiveClient(); await using var turn = LiveTurn(client);
        var opened = Require<RoundOpened>(await client.OpenRoundAsync(turn, AiRoundRequest.Create(Guid.NewGuid(),
            [AiSystemMessage.Create("Answer in one short sentence."), AiUserMessage.Create("Say hello to the mapping reviewer.")], [], 48),
            CancellationToken.None));
        await using var stream = opened.Stream;
        var events = await Read(stream);
        var terminal = Assert.IsType<AiRoundFinished>(events[^1]);
        Assert.Single(events, value => value is AiRoundFinished or AiRoundFailed);
        Assert.Contains(terminal.Reason, new[] { AiFinishReason.Stop, AiFinishReason.OutputLimit });
        Assert.False(string.IsNullOrWhiteSpace(Text(events)), "A live round produced no text.");
        Assert.True(terminal.Usage.InputTokens > 0 && terminal.Usage.OutputTokens > 0, "Live usage was not reported.");
        Assert.True(terminal.Usage.OutputTokens <= 48, "The output budget was not applied.");
    }

    [LiveOllamaFact]
    public async Task LiveOllamaToolRoundReturnsValidatedIntentThenAnswersFromTheToolResult()
    {
        var tool = AiToolDefinition.Create("lookup_field", "Look up where an output field's value comes from",
            AiJsonObject.CopyFrom("{\"type\":\"object\",\"properties\":{\"field\":{\"type\":\"string\",\"description\":\"Output field name\"}},\"required\":[\"field\"],\"additionalProperties\":false}"));
        var system = AiSystemMessage.Create("You explain data mappings. Always call lookup_field before answering a question about a field.");
        var question = AiUserMessage.Create("Where does the output field customer_id come from?");
        await using var client = LiveClient(); await using var turn = LiveTurn(client);

        var first = Require<RoundOpened>(await client.OpenRoundAsync(turn, AiRoundRequest.Create(Guid.NewGuid(), [system, question], [tool], 128),
            CancellationToken.None));
        List<AiRoundEvent> calls;
        await using (var stream = first.Stream) calls = await Read(stream);
        Finished(calls, AiFinishReason.ToolCalls);
        var intents = calls.OfType<AiToolIntentReady>().Select(value => value.Intent).ToArray();
        Assert.NotEmpty(intents);
        Assert.All(intents, intent => Assert.Equal("lookup_field", intent.Name));

        var results = intents.Select(intent => AiToolMessage.Create(intent.CallId, intent.Name,
            AiJsonObject.CopyFrom("{\"field\":\"customer_id\",\"source\":\"input.account.id\",\"rule\":\"copied unchanged\"}")));
        var second = Require<RoundOpened>(await client.OpenRoundAsync(turn, AiRoundRequest.Create(Guid.NewGuid(),
            [system, question, AiAssistantMessage.Create("", intents), .. results], [tool], 128), CancellationToken.None));
        Assert.Equal(2, second.Ordinal);
        await using var answer = second.Stream;
        var events = await Read(answer);
        var terminal = Assert.IsType<AiRoundFinished>(events[^1]);
        Assert.Contains(terminal.Reason, new[] { AiFinishReason.Stop, AiFinishReason.OutputLimit });
        Assert.False(string.IsNullOrWhiteSpace(Text(events)), "The live answer after a tool result was empty.");
    }
}
