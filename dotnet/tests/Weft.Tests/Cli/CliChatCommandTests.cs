using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Weft.Cli;
using Weft.Cli.Http;
using Weft.Contracts.Chat;

namespace Weft.Tests.Cli;

/// <summary>Chat commands: streamed answers with citations, stream resumption and turn cancellation.</summary>
public sealed class CliChatCommandTests : IAsyncLifetime
{
    private CliTestIssuer issuer = null!;
    private CliApiStub stub = null!;
    private CliHarness cli = null!;
    private CliStubData data = null!;

    public async Task InitializeAsync()
    {
        issuer = await CliTestIssuer.StartAsync();
        stub = await CliApiStub.StartAsync(issuer);
        cli = new CliHarness(issuer, stub.Handler);
        data = new CliStubData(stub.OrganizationId);
        await cli.ConfigureAsync();
        await cli.LoginAsync();
        stub.Reply("GetChatAvailability", _ => CliStubReply.Of(200, data.ChatAvailability));
        stub.Reply("CreateChatConversation", request => CliStubReply.Of(201, data.Conversation(request.Json!["conversationId"]!.GetValue<string>())));
        stub.Reply("CreateChatTurn", request => CliStubReply.Of(201,
            data.Turn(request.Path["conversationId"], request.Json!["turnId"]!.GetValue<string>())));
    }

    public async Task DisposeAsync()
    {
        stub.AssertClean();
        cli.Dispose();
        await stub.DisposeAsync();
        await issuer.DisposeAsync();
    }

    private string[] Revision(params string[] args) => [.. args, "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision];

    private static CliStubReply Events(IEnumerable<ChatTurnEventV1> events, bool drop = false) => new()
    {
        ContentType = "text/event-stream; charset=utf-8",
        Stream = async context =>
        {
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            await context.Response.WriteAsync(": keep-alive\n\n");
            foreach (var item in events)
            {
                var name = item switch
                {
                    ChatTurnStartedEventV1 => "turn.started",
                    ChatTextDeltaEventV1 => "text.delta",
                    ChatCitationEventV1 => "citation",
                    ChatUsageEventV1 => "usage",
                    ChatTurnCompletedEventV1 => "turn.completed",
                    ChatTurnFailedEventV1 => "turn.failed",
                    _ => "turn.cancelled"
                };
                await context.Response.WriteAsync($"id: {item.Sequence}\r\nevent: {name}\r\ndata: {JsonSerializer.Serialize(item, WeftApiClient.Json)}\r\n\r\n");
                await context.Response.Body.FlushAsync();
            }
            if (drop)
            {
                // Let the client read what was sent, then drop the connection without a terminal event.
                await Task.Delay(TimeSpan.FromMilliseconds(300));
                context.Abort();
            }
        }
    };

    [Fact]
    public async Task AskStreamsTheAnswerAndListsItsVerifiedCitations()
    {
        var round = CliStubData.Id();
        var report = CliStubData.Id();
        stub.Reply("StreamChatTurnEvents", _ => Events([
            new ChatTurnStartedEventV1(1),
            new ChatTextDeltaEventV1(2, round, "Total is \u001b[2Jthe "),
            new ChatTextDeltaEventV1(3, round, "order sum.\nIt is a decimal."),
            new ChatCitationEventV1(4, round, new ChatSelectorV1(ChatSelectorKindV1.Field, "field-total", null), report),
            new ChatUsageEventV1(5, round, ChatUsageOriginV1.RoundTerminal, 10, 20, 30),
            new ChatTurnCompletedEventV1(6)]));

        var asked = await cli.RunAsync(Revision("chat", "ask", "--select", "field=field-total", "--select", "validationDiagnostic=2",
            "--select", "projectionReport", "What does total mean?"));
        Assert.True(asked.Exit == 0, asked.ToString());
        Assert.Contains("Total is �[2Jthe order sum.\nIt is a decimal.", asked.Out, StringComparison.Ordinal);
        Assert.Contains("[1] field field-total (report " + report + ")", asked.Out, StringComparison.Ordinal);
        var turn = Assert.Single(stub.Calls("CreateChatTurn")).Json!;
        Assert.Equal("local", turn["profileId"]!.GetValue<string>());
        Assert.Equal(2, turn["profileVersion"]!.GetValue<int>());
        Assert.Equal("field", turn["selectors"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(2, turn["selectors"]![1]!["diagnosticOrdinal"]!.GetValue<int>());
        Assert.Equal("0", Assert.Single(stub.Calls("StreamChatTurnEvents")).Query["after"]);
        var conversation = Assert.Single(stub.Calls("CreateChatConversation")).Json!["conversationId"]!.GetValue<string>();
        Assert.Equal(conversation, stub.Calls("CreateChatTurn")[0].Path["conversationId"]);

        var json = await cli.RunAsync(Revision("chat", "ask", "--conversation", conversation, "--json", "Again?"));
        var answer = JsonNode.Parse(json.Out)!;
        Assert.Equal("completed", answer["outcome"]!.GetValue<string>());
        Assert.Equal("Total is \u001b[2Jthe order sum.\nIt is a decimal.", answer["text"]!.GetValue<string>());
        Assert.Equal("field-total", answer["citations"]![0]!["target"]!["resourceId"]!.GetValue<string>());
        Assert.Single(stub.Calls("CreateChatConversation"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConversationReadsDiscloseAnOmittedTurnWindowWithoutChangingJson(bool earlier)
    {
        var id = CliStubData.Id();
        var original = data.Conversation(id);
        var conversation = new ChatConversationV1(id, original.Revision, original.CreatedAtUtc, original.RetainUntilUtc,
            original.ProjectionDerivationId, original.ValidationDerivationId, "Title\u001b[2J", earlier ? 101 : 100, CliStubData.Time);
        var turns = Enumerable.Range(0, 100).Select(_ => data.Turn(id, CliStubData.Id())).ToArray();
        stub.Reply("GetChatConversation", _ => CliStubReply.Of(200, new ChatConversationDetailV1(conversation, turns, earlier)));
        var human = await cli.RunAsync(Revision("chat", "conversations", "get", id));
        Assert.True(human.Exit == 0, human.ToString());
        Assert.Equal(earlier, human.Out.Contains("Earlier turns are omitted", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', human.Out);
        Assert.True(human.Out.Length < 30_000);
        var json = await cli.RunAsync(Revision("chat", "conversations", "get", id, "--json"));
        Assert.True(json.Exit == 0, json.ToString());
        Assert.Equal(earlier, JsonNode.Parse(json.Out)!["hasEarlierTurns"]!.GetValue<bool>());
        Assert.DoesNotContain("Earlier turns are omitted", json.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADroppedStreamResumesAfterTheLastDeliveredEvent()
    {
        var round = CliStubData.Id();
        stub.Enqueue("StreamChatTurnEvents",
            Events([new ChatTurnStartedEventV1(1), new ChatTextDeltaEventV1(2, round, "Half ")], drop: true),
            Events([new ChatTextDeltaEventV1(2, round, "Half "), new ChatTextDeltaEventV1(3, round, "answer."), new ChatTurnCompletedEventV1(4)]));

        var asked = await cli.RunAsync(Revision("chat", "ask", "Question"));
        Assert.True(asked.Exit == 0, asked.ToString());
        Assert.Contains("Half answer.", asked.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Half Half", asked.Out, StringComparison.Ordinal);
        var streams = stub.Calls("StreamChatTurnEvents");
        Assert.Equal(2, streams.Count);
        Assert.Equal("2", streams[1].Query["after"]);
    }

    [Fact]
    public async Task FailedTurnsAndMissingReportsExitWithTheirCodes()
    {
        stub.Reply("StreamChatTurnEvents", _ => Events([new ChatTurnStartedEventV1(1),
            new ChatTurnFailedEventV1(2, ChatTurnFailureV1.Provider, ChatProviderFailureV1.ProviderRateLimited)]));
        var failed = await cli.RunAsync(Revision("chat", "ask", "Question"));
        Assert.Equal(CliExitCode.OperationFailed, failed.Code);
        Assert.Contains("The turn failed (provider, providerRateLimited).", failed.Error, StringComparison.Ordinal);

        stub.Enqueue("CreateChatConversation", CliStubReply.Of(409, new ChatErrorV1(ChatErrorCodeV1.ReportsUnavailable)));
        var noReports = await cli.RunAsync(Revision("chat", "ask", "Question"));
        Assert.Equal(CliExitCode.Conflict, noReports.Code);
        Assert.Contains("weft reports projection ensure", noReports.Error, StringComparison.Ordinal);

        stub.Enqueue("CreateChatTurn", CliStubReply.Of(409, new ChatErrorV1(ChatErrorCodeV1.TurnActive)));
        var active = await cli.RunAsync(Revision("chat", "ask", "--conversation", CliStubData.Id(), "Question"));
        Assert.Equal(CliExitCode.Conflict, active.Code);
        Assert.Contains("turnActive", active.Error, StringComparison.Ordinal);

        var tooLong = await cli.RunAsync(Revision("chat", "ask", new string('x', 5000)));
        Assert.Equal(CliExitCode.Usage, tooLong.Code);
        var unknownModel = await cli.RunAsync(Revision("chat", "ask", "--model", "other:1", "Question"));
        Assert.Equal(CliExitCode.Usage, unknownModel.Code);
        var badSelector = await cli.RunAsync(Revision("chat", "ask", "--select", "projectionReport=x", "Question"));
        Assert.Equal(CliExitCode.Usage, badSelector.Code);
    }

    [Fact]
    public async Task TurnsCanBeFollowedAndCancelledAndConversationsListed()
    {
        var conversation = CliStubData.Id();
        var turn = CliStubData.Id();
        stub.Reply("StreamChatTurnEvents", _ => Events([new ChatTurnCancelledEventV1(5)]));
        var followed = await cli.RunAsync(Revision("chat", "follow", "--conversation", conversation, "--after", "4", turn));
        Assert.Equal(CliExitCode.OperationFailed, followed.Code);
        Assert.Equal("4", stub.Calls("StreamChatTurnEvents")[0].Query["after"]);
        Assert.Contains("cancelled", followed.Error, StringComparison.Ordinal);

        stub.Reply("CancelChatTurn", _ => CliStubReply.Of(200, data.Turn(conversation, turn, ChatTurnPhaseV1.Cancelled)));
        var cancelled = await cli.RunAsync(Revision("chat", "cancel", "--conversation", conversation, turn));
        Assert.True(cancelled.Exit == 0, cancelled.ToString());
        Assert.Contains($"Turn {turn} is cancelled.", cancelled.Out, StringComparison.Ordinal);
        Assert.Empty(Assert.Single(stub.Calls("CancelChatTurn")).Body);

        stub.Reply("GetChatConversations", _ => CliStubReply.Of(200, new ChatConversationPageV1([data.Conversation(conversation)], true)));
        var list = await cli.RunAsync(Revision("chat", "conversations", "list", "--limit", "5"));
        Assert.Contains(conversation, list.Out, StringComparison.Ordinal);
        Assert.Contains("raise --limit", list.Out, StringComparison.Ordinal);
        Assert.Equal("5", stub.Calls("GetChatConversations")[0].Query["limit"]);
        stub.Reply("GetChatConversation", _ => CliStubReply.Of(200, new ChatConversationDetailV1(data.Conversation(conversation),
            [data.Turn(conversation, turn, ChatTurnPhaseV1.Completed)], false)));
        var detail = await cli.RunAsync(Revision("chat", "conversations", "get", conversation));
        Assert.Contains(turn, detail.Out, StringComparison.Ordinal);
        var availability = await cli.RunAsync(Revision("chat", "availability"));
        Assert.Contains("qwen", availability.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTheCommandStopsTheTurnOnTheServer()
    {
        var started = new TaskCompletionSource();
        stub.Reply("StreamChatTurnEvents", _ => new CliStubReply
        {
            ContentType = "text/event-stream; charset=utf-8",
            Stream = async context =>
            {
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                await context.Response.WriteAsync("id: 1\nevent: turn.started\ndata: {\"type\":\"turn.started\",\"sequence\":1}\n\n");
                await context.Response.Body.FlushAsync();
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
        });
        stub.Reply("CancelChatTurn", request => CliStubReply.Of(200, data.Turn(request.Path["conversationId"], request.Path["turnId"],
            ChatTurnPhaseV1.Cancelled)));
        using var cancel = new CancellationTokenSource();
        var environment = new Weft.Cli.CliEnvironment
        {
            Out = new StringWriter(),
            Error = new StringWriter(),
            OpenStandardInput = () => new MemoryStream(),
            OpenStandardOutput = () => new MemoryStream(),
            ConfigurationDirectory = cli.ConfigurationDirectory,
            TokenCaches = cli.Caches,
            ApiHandler = stub.Handler,
            IdentityHttp = issuer.MsalHttp,
            Time = cli.Time
        };
        var running = CliApplication.RunAsync(Revision("chat", "ask", "Question"), environment, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancel.CancelAsync();
        Assert.Equal((int)CliExitCode.Canceled, await running.WaitAsync(TimeSpan.FromSeconds(30)));
        var cancelled = Assert.Single(stub.Calls("CancelChatTurn"));
        Assert.Equal(stub.Calls("CreateChatTurn")[0].Json!["turnId"]!.GetValue<string>(), cancelled.Path["turnId"]);
    }
}
