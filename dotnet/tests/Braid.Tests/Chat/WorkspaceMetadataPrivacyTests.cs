using Braid.Ai;
using Braid.Api.Chat.Workspace;
using Braid.Application;
using Braid.Domain;
using static Braid.Tests.ChatOrchestrationFixture;

namespace Braid.Tests;

public sealed class WorkspaceMetadataPrivacyTests
{
    [Theory]
    [InlineData("Failure rule evaluated customer secret-value-9347", "rejected secret-value-9347")]
    [InlineData("JSON constraint rejected value secret-value-9347", "input secret-value-9347")]
    public async Task Runtime_failure_text_is_not_metadata_and_cannot_contaminate_automatic_history(string message, string subject)
    {
        var data = new WorkspaceToolFixture(payloads: true);
        data.Payloads!.SnapshotOverride = WorkspacePayloadFakes.Snapshot with
        {
            Outcome = PreviewRunOutcome.ExecutionFailed,
            Failure = new("failure-rule", message, false, subject, false),
            Outputs = [],
            RecordsWritten = null
        };
        var tools = data.Service(previews: true);
        var args = $$"""{"kind":"preview","runId":"{{WorkspacePayloadFakes.RunId:D}}"}""";
        var metadata = await data.CallAsync(tools, "get_run", args);
        Assert.Equal("available", metadata.Outcome);
        Assert.True(metadata.Data.GetProperty("failure").GetProperty("detailsOmitted").GetBoolean());
        Assert.DoesNotContain("secret-value-9347", metadata.Json, StringComparison.Ordinal);
        Assert.Null(metadata.Result.DataShared);
        Assert.Equal(0, data.Payloads.Observations);

        await using var fixture = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Call(0, "read-status", args, "get_run") + Finish("tool_calls") + Done
                : Text("The preview failed; private failure details were not shared.") + Finish("stop") + Done), ct),
            actor: WorkspaceToolFixture.Actor, conversation: data.Conversation(), tools: tools.Manifest, toolPort: tools,
            instructions: WorkspaceChatGrounding.Instructions, workspaceCitationPort: tools, contextPort: tools);
        ChatTurnIntent Intent(string question)
        {
            var original = fixture.Intent(question);
            return new(original.TurnId, new(original.Envelope.RequestId, question, [], original.Envelope.ProviderBudget,
                tools.Grounding, ChatTurnFocus.None), original.Profile, original.Budget, original.DeadlineUtc);
        }
        var first = Intent("Why did this preview fail?");
        Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(fixture.Begin(first))).Outcome);
        // The in-memory ledger uses a fixed clock. Present this acknowledged exchange
        // in the earlier history window while retaining its actual events and answer.
        fixture.Store.TurnRead = facts => facts?.Intent.TurnId == first.TurnId
            ? new(facts.Intent, facts.Phase, facts.Version, facts.EventCount, facts.EventBytes, facts.RoundCount,
                Now.AddSeconds(-2), Now.AddSeconds(-1)) : facts;
        Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(fixture.Begin(Intent("What did you find?"), [first.TurnId]))).Outcome);
        Assert.Equal(3, fixture.Server.RequestCount);
        Assert.All(fixture.Server.Requests, request => Assert.False(request.ContainsUtf8("secret-value-9347")));
        Assert.DoesNotContain(fixture.Store.Events(first.TurnId), item => item.Kind == ChatEventKind.DataShared);
    }

    [Fact]
    public async Task Draft_preview_runs_are_not_visible_to_the_workspace()
    {
        var data = new WorkspaceToolFixture(payloads: true);
        var revision = WorkspacePayloadFakes.Snapshot.Subject.ExecutionScope;
        data.Payloads!.SnapshotOverride = WorkspacePayloadFakes.Snapshot with
        {
            Subject = new DraftPreviewSubject(revision, Guid.NewGuid(), 1, Sha256Digest.FromBytes([1]), 1)
        };
        var tools = data.Service(previews: true);
        var run = $$"""{"kind":"preview","runId":"{{WorkspacePayloadFakes.RunId:D}}"}""";
        Assert.Equal("notFound", (await data.CallAsync(tools, "get_run", run)).Outcome);
        Assert.Equal(0, data.Payloads.Observations);
    }
}
