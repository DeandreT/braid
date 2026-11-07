using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Weft.Cli;
using Weft.Cli.Http;
using Weft.Contracts.Chat;

namespace Weft.Tests.Cli;

/// <summary>Workspace commands over authenticated HTTP, validated against the shipped OpenAPI document.</summary>
public sealed class CliWorkspaceHttpCommandTests : IAsyncLifetime
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
        stub.Reply("GetWorkspaceChatAvailability", _ => CliStubReply.Of(200,
            new ChatWorkspaceAvailabilityV1(stub.OrganizationId, [new(data.ChatAvailability.Profiles[0], true)], data.ChatAvailability.Limits)));
        stub.Reply("CreateWorkspaceChatConversation", request => CliStubReply.Of(201,
            Conversation(request.Json!["conversationId"]!.GetValue<string>(), request.Json["focus"]?.Deserialize<ChatFocusV1>(WeftApiClient.Json))));
        stub.Reply("CreateWorkspaceChatTurn", request => CliStubReply.Of(201,
            data.Turn(request.Path["conversationId"], request.Json!["turnId"]!.GetValue<string>())));
    }

    public async Task DisposeAsync()
    {
        stub.AssertClean();
        cli.Dispose();
        await stub.DisposeAsync();
        await issuer.DisposeAsync();
    }

    private ChatOrganizationConversationV1 Conversation(string id, ChatFocusV1? focus = null) =>
        new(id, ChatConversationKindV1.Workspace, null, focus, CliStubData.Time, CliStubData.Time.AddDays(1), null, 0, null);

    private static CliStubReply Events(params ChatTurnEventV1[] events) => new()
    {
        ContentType = "text/event-stream; charset=utf-8",
        Stream = async context =>
        {
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            foreach (var item in events)
            {
                var json = JsonSerializer.SerializeToNode(item, WeftApiClient.Json)!;
                await context.Response.WriteAsync($"id: {item.Sequence}\nevent: {json["type"]!.GetValue<string>()}\ndata: {json.ToJsonString()}\n\n");
                await context.Response.Body.FlushAsync();
            }
        }
    };

    [Fact]
    public async Task AvailabilityAndBothConversationKindsExposePolicyAndBoundedContinuation()
    {
        var available = await cli.RunAsync("chat", "workspace", "availability");
        Assert.True(available.Exit == 0, available.ToString());
        Assert.Contains("PAYLOADS ALLOWED", available.Out, StringComparison.Ordinal);
        Assert.Contains("current grant", available.Out, StringComparison.Ordinal);
        var first = CliStubData.Id();
        var legacy = data.Conversation(CliStubData.Id());
        stub.Reply("GetOrganizationChatConversations", _ => CliStubReply.Of(200, new ChatOrganizationConversationPageV1([
            Conversation(first), new(legacy.ConversationId, ChatConversationKindV1.Revision, legacy.Revision, null,
                legacy.CreatedAtUtc, legacy.RetainUntilUtc, legacy.Title, legacy.TurnCount, legacy.LastTurnAtUtc)], true)));
        var listed = await cli.RunAsync("chat", "workspace", "conversations", "list", "--limit", "2", "--after", first, "--revision", data.Revision);
        Assert.True(listed.Exit == 0, listed.ToString());
        Assert.Contains("Continue with --after " + legacy.ConversationId, listed.Out, StringComparison.Ordinal);
        Assert.Contains("Legacy revision conversations", listed.Out, StringComparison.Ordinal);
        var request = Assert.Single(stub.Calls("GetOrganizationChatConversations"));
        Assert.Equal("2", request.Query["limit"]);
        Assert.Equal(first, request.Query["after"]);
        Assert.Equal(data.Revision, request.Query["revisionId"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationAndRecoveryPreserveTheExactOptionalFocus(bool exact)
    {
        var id = CliStubData.Id();
        string[] args = ["chat", "workspace", "conversations", "create", "--conversation-id", id,
            "--focus-project", data.Project, "--focus-mapping", data.Mapping];
        if (exact) args = [.. args, "--focus-revision", data.Revision];
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.True((await cli.RunAsync(args)).Exit == 0);
        var requests = stub.Calls("CreateWorkspaceChatConversation");
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].Body, requests[1].Body);
        Assert.Equal(id, requests[0].Json!["conversationId"]!.GetValue<string>());
        Assert.Equal(data.Mapping, requests[0].Json!["focus"]!["mappingId"]!.GetValue<string>());
        Assert.Equal(exact ? data.Revision : null, requests[0].Json!["focus"]!["revisionId"]?.GetValue<string>());
        stub.Reply("GetWorkspaceChatConversation", _ => CliStubReply.Of(200,
            new ChatWorkspaceConversationDetailV1(Conversation(id), [], true)));
        var read = await cli.RunAsync("chat", "workspace", "conversations", "get", id);
        Assert.True(read.Exit == 0, read.ToString());
        Assert.Contains("Earlier turns", read.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCarriesVerifiedWorkspaceCitationsAndDataSharingAndReplaysAnExplicitTurn()
    {
        var round = CliStubData.Id();
        var conversation = CliStubData.Id();
        var turn = CliStubData.Id();
        var citation = new ChatWorkspaceCitationV1(ChatWorkspaceCitationKindV1.Mapping, stub.OrganizationId, data.Project,
            data.Mapping, null, null, null, null, null, null);
        stub.Reply("StreamWorkspaceChatTurnEvents", _ => Events(new ChatTurnStartedEventV1(1),
            new ChatTextDeltaEventV1(2, round, "Grounded answer."), new ChatWorkspaceCitationEventV1(3, round, citation),
            new ChatDataSharedEventV1(4, round, data.Project, ChatPayloadSourceV1.PreviewInput, CliStubData.Id(), null, 23),
            new ChatTurnCompletedEventV1(5)));
        var result = await cli.RunAsync("chat", "workspace", "ask", "Question", "--conversation", conversation, "--turn-id", turn, "--json");
        Assert.True(result.Exit == 0, result.ToString());
        var answer = JsonNode.Parse(result.Out)!;
        Assert.Equal("Grounded answer.", answer["text"]!.GetValue<string>());
        Assert.Equal(data.Mapping, answer["citations"]![0]!["citation"]!["mappingId"]!.GetValue<string>());
        Assert.Equal(23, answer["sharedData"]![0]!["bytes"]!.GetValue<int>());
        Assert.Empty(stub.Calls("CreateWorkspaceChatConversation"));
        Assert.Equal(turn, Assert.Single(stub.Calls("CreateWorkspaceChatTurn")).Json!["turnId"]!.GetValue<string>());
        var human = await cli.RunAsync("chat", "workspace", "follow", "--conversation", conversation, turn);
        Assert.True(human.Exit == 0, human.ToString());
        Assert.Contains("Citation: Mapping", human.Out, StringComparison.Ordinal);
        Assert.Contains("Data shared", human.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectedStreamsResumeOnceWithoutDuplicatingTextOrDispatchingAnotherTurn()
    {
        var round = CliStubData.Id();
        stub.Enqueue("StreamWorkspaceChatTurnEvents", Events(new ChatTurnStartedEventV1(1), new ChatTextDeltaEventV1(2, round, "First ")),
            Events(new ChatTextDeltaEventV1(2, round, "First "), new ChatTextDeltaEventV1(3, round, "second."), new ChatTurnCompletedEventV1(4)));
        var result = await cli.RunAsync("chat", "workspace", "ask", "Question");
        Assert.True(result.Exit == 0, result.ToString());
        Assert.Contains("First second.", result.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("First First", result.Out, StringComparison.Ordinal);
        Assert.Single(stub.Calls("CreateWorkspaceChatTurn"));
        Assert.Equal("2", stub.Calls("StreamWorkspaceChatTurnEvents")[1].Query["after"]);
    }

    [Fact]
    public async Task ForeignCitationsAreRefusedAndExplicitCancellationUsesOnlyTheWorkspaceRoute()
    {
        var round = CliStubData.Id();
        var conversation = CliStubData.Id();
        var turn = CliStubData.Id();
        stub.Reply("StreamWorkspaceChatTurnEvents", _ => Events(new ChatWorkspaceCitationEventV1(1, round,
            new(ChatWorkspaceCitationKindV1.Project, CliStubData.Id(), data.Project, null, null, null, null, null, null, null))));
        var refused = await cli.RunAsync("chat", "workspace", "follow", "--conversation", conversation, turn);
        Assert.Equal(CliExitCode.Transport, refused.Code);
        Assert.Contains("does not belong", refused.Error, StringComparison.Ordinal);
        stub.Reply("CancelWorkspaceChatTurn", _ => CliStubReply.Of(200, data.Turn(conversation, turn, ChatTurnPhaseV1.Cancelled)));
        var cancelled = await cli.RunAsync("chat", "workspace", "cancel", "--conversation", conversation, turn);
        Assert.True(cancelled.Exit == 0, cancelled.ToString());
        Assert.Empty(Assert.Single(stub.Calls("CancelWorkspaceChatTurn")).Body);
        Assert.Empty(stub.Calls("CancelChatTurn"));
    }

    [Fact]
    public async Task CancellingTheCommandCancelsTheSameWorkspaceTurn()
    {
        var started = new TaskCompletionSource();
        stub.Reply("StreamWorkspaceChatTurnEvents", _ => new CliStubReply
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
        stub.Reply("CancelWorkspaceChatTurn", request => CliStubReply.Of(200,
            data.Turn(request.Path["conversationId"], request.Path["turnId"], ChatTurnPhaseV1.Cancelled)));
        using var cancellation = new CancellationTokenSource();
        var environment = new CliEnvironment
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
        var running = CliApplication.RunAsync(["chat", "workspace", "ask", "Question"], environment, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();
        Assert.Equal((int)CliExitCode.Canceled, await running.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(Assert.Single(stub.Calls("CreateWorkspaceChatTurn")).Json!["turnId"]!.GetValue<string>(),
            Assert.Single(stub.Calls("CancelWorkspaceChatTurn")).Path["turnId"]);
    }
}
