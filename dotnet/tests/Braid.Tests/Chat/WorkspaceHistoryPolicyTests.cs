using Braid.Ai;
using Braid.Api.Chat.Workspace;
using Braid.Application;
using Braid.Domain;
using static Braid.Tests.ChatOrchestrationFixture;

namespace Braid.Tests;

public sealed class WorkspaceHistoryPolicyTests
{
    [Fact]
    public async Task Earlier_metadata_policy_history_is_not_replayed_after_runtime_text_is_excluded()
    {
        var catalog = new WorkspaceToolFixture();
        var service = catalog.Service();
        await using var fixture = new ChatOrchestrationFixture(actor: WorkspaceToolFixture.Actor, conversation: catalog.Conversation(),
            tools: service.Manifest, toolPort: service, contextPort: service, instructions: WorkspaceChatGrounding.Instructions);
        var oldPolicy = ChatAiBridge.PinGroundingPolicy(WorkspaceChatGrounding.PolicyId, 1, WorkspaceChatGrounding.Instructions,
            service.Manifest, WorkspaceChatGrounding.MaxRetrievedNodes, WorkspaceChatGrounding.MaxToolResultBytes, WorkspaceChatGrounding.MaxToolIterations);
        ChatTurnIntent Intent(string text, ChatGroundingPolicyPin policy)
        {
            var original = fixture.Intent(text);
            return new(original.TurnId, new(original.Envelope.RequestId, text, [], original.Envelope.ProviderBudget, policy, ChatTurnFocus.None),
                original.Profile, original.Budget, original.DeadlineUtc);
        }
        var previous = Intent("Earlier metadata request", oldPolicy);
        fixture.Store.SeedHistory(previous, Now.AddMinutes(-3), "Payload value retained by the earlier metadata policy",
            historyTools: service.Manifest, historyInstructions: WorkspaceChatGrounding.Instructions,
            historyCall: AiToolIntent.Create("seed-call", "list_projects", AiJsonObject.CopyFrom("{}")));
        Assert.False(service.IncludeHistory(previous));
        Assert.Equal(ChatOrchestrationOutcome.Completed,
            (await FinishExecution(fixture.Begin(Intent("Current request", service.Grounding), [previous.TurnId]))).Outcome);
        Assert.DoesNotContain(fixture.Store.Calls, call => call.Method == "read-events" && call.TurnId == previous.TurnId);
        Assert.False(fixture.Server.Requests[0].ContainsUtf8("Payload value retained"));
        Assert.True(fixture.Server.Requests[0].ContainsUtf8("Some earlier turns are not included"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Payload_capable_history_stays_in_the_ledger_but_is_never_loaded_or_sent_as_automatic_context(bool currentPayloads)
    {
        var catalog = new WorkspaceToolFixture(payloads: true);
        var service = catalog.Service(previews: true);
        await using var fixture = new ChatOrchestrationFixture(actor: WorkspaceToolFixture.Actor,
            conversation: catalog.Conversation(), tools: currentPayloads ? service.PayloadManifest!.Value : service.Manifest, toolPort: service,
            workspaceCitationPort: service, contextPort: service, instructions: WorkspaceChatGrounding.Instructions);
        ChatTurnIntent Intent(string text, bool payloads)
        {
            var original = fixture.Intent(text);
            return new(original.TurnId, new(original.Envelope.RequestId, text, [], original.Envelope.ProviderBudget,
                payloads ? service.PayloadGrounding! : service.Grounding, ChatTurnFocus.None), original.Profile,
                original.Budget, original.DeadlineUtc);
        }
        var privateTurn = Intent("Private previous sample question", true);
        var safeTurn = Intent("Metadata previous question", false);
        var call = AiToolIntent.Create("seed-call", "list_projects", AiJsonObject.CopyFrom("{}"));
        fixture.Store.SeedHistory(privateTurn, Now.AddMinutes(-5), "PRIVATE sample-derived answer",
            historyTools: service.PayloadManifest!.Value, historyInstructions: WorkspaceChatGrounding.Instructions, historyCall: call);
        fixture.Store.SeedHistory(safeTurn, Now.AddMinutes(-3), "Safe mapping description",
            historyTools: service.Manifest, historyInstructions: WorkspaceChatGrounding.Instructions, historyCall: call);
        var current = Intent("Current question", currentPayloads);
        var result = await FinishExecution(fixture.Begin(current, [privateTurn.TurnId, safeTurn.TurnId]));
        Assert.Equal(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(fixture.Store.Calls, call => call.Method == "read-events" && call.TurnId == privateTurn.TurnId);
        Assert.Contains(fixture.Store.Calls, call => call.Method == "read-events" && call.TurnId == safeTurn.TurnId);
        Assert.Contains(fixture.Store.Events(privateTurn.TurnId), item => item.Text.Contains("PRIVATE sample-derived answer", StringComparison.Ordinal));
        Assert.False(fixture.Server.Requests[0].ContainsUtf8("Private previous sample question"));
        Assert.False(fixture.Server.Requests[0].ContainsUtf8("PRIVATE sample-derived answer"));
        Assert.True(fixture.Server.Requests[0].ContainsUtf8("Safe mapping description"));
        Assert.True(fixture.Server.Requests[0].ContainsUtf8("Some earlier turns are not included"));
    }
}
