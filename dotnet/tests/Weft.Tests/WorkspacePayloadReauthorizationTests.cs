using Weft.Ai;
using Weft.Api.Chat.Workspace;
using Weft.Application;
using Weft.Domain;
using static Weft.Tests.ChatOrchestrationFixture;

namespace Weft.Tests;

public sealed class WorkspacePayloadReauthorizationTests
{
    [Theory]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("none")]
    public async Task Every_previously_shared_project_is_reauthorized_before_another_provider_round(string revoke)
    {
        var catalog = new WorkspaceToolFixture(payloads: true) { ExtraProjects = 1 };
        var data = catalog.Payloads!;
        var projectB = new ProjectScope(WorkspaceToolFixture.Organization, Guid.Parse("00000001-0000-4000-8000-000000000000"));
        var runB = Guid.Parse("72727272-7272-4272-8272-727272727272");
        data.AdditionalRuns.Add(runB, WorkspacePayloadFakes.Snapshot with
        {
            RunId = runB,
            Subject = new RevisionScope(projectB.OrganizationId, projectB.ProjectId, Guid.NewGuid(), Guid.NewGuid())
        });
        data.Grants.UnionWith([WorkspaceToolFixture.Project, projectB]);
        var service = catalog.Service(previews: true);
        string Read(Guid id) => $$"""{"runId":"{{id:D}}","ordinal":0}""";
        await using var fixture = new ChatOrchestrationFixture(async (exchange, ct) =>
        {
            if (exchange.Ordinal == 2)
            {
                // The first provider result has already delivered A. Revoke it while B is
                // being read; B remains authorized and must not keep A's cached bytes alive.
                data.OnProjectObserved = project =>
                {
                    if (project != projectB) return;
                    data.OnProjectObserved = null;
                    if (revoke == "grant") data.Grants.Remove(WorkspaceToolFixture.Project);
                    if (revoke == "membership") catalog.RevokedProjects.Add(WorkspaceToolFixture.Project);
                };
            }
            var response = exchange.Ordinal switch
            {
                1 => Call(0, "read-a", Read(WorkspacePayloadFakes.RunId), "get_preview_output") + Finish("tool_calls") + Done,
                2 => Call(0, "read-b", Read(runB), "get_preview_output") + Finish("tool_calls") + Done,
                _ => Text("Both currently authorized results were received.") + Finish("stop") + Done
            };
            await exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(response), ct);
        }, actor: WorkspaceToolFixture.Actor, conversation: catalog.Conversation(), tools: service.PayloadManifest!.Value,
            toolPort: service, instructions: WorkspaceChatGrounding.Instructions, workspaceCitationPort: service, contextPort: service,
            payloadsAllowed: true);
        var budget = new ChatProviderBudget(600_000_000, 3, 1_048_576, 4_194_304, 131_072, 8, 128);
        var turn = new ChatTurnIntent(Guid.NewGuid(), new(Guid.NewGuid(), "Read both current sample outputs.", [], budget,
            service.PayloadGrounding!, ChatTurnFocus.None), ChatAiBridge.PinProfile("fixture", fixture.Profile),
            new(256, 262_144, 3), Now.AddTicks(600_000_000));
        var result = await FinishExecution(fixture.Begin(turn));
        Assert.True(await data.AnyAsync(WorkspaceToolFixture.Actor, WorkspaceToolFixture.Organization));
        Assert.True(fixture.Server.Requests[1].ContainsUtf8(WorkspacePayloadFakes.RunId.ToString("D")));
        Assert.True(fixture.Server.Requests[1].ContainsUtf8("total"));
        var disclosures = fixture.Store.Events(turn.TurnId).Where(item => item.Kind == ChatEventKind.DataShared)
            .Select(item => ChatMetadataCodec.ReadDisclosure(item.MetadataJson!)).ToArray();
        Assert.Contains(disclosures, item => item.ProjectId == WorkspaceToolFixture.Project.ProjectId && item.SubjectId == WorkspacePayloadFakes.RunId);
        if (revoke == "none")
        {
            Assert.Equal(ChatOrchestrationOutcome.Completed, result.Outcome);
            Assert.Equal(3, fixture.Server.RequestCount);
            Assert.True(fixture.Server.Requests[2].ContainsUtf8(WorkspacePayloadFakes.RunId.ToString("D")));
            Assert.True(fixture.Server.Requests[2].ContainsUtf8(runB.ToString("D")));
            Assert.Contains(disclosures, item => item.ProjectId == projectB.ProjectId && item.SubjectId == runB);
        }
        else
        {
            Assert.Equal(ChatOrchestrationOutcome.Denied, result.Outcome);
            Assert.Equal(2, fixture.Server.RequestCount);
            Assert.Equal(ChatTurnPhase.Failed, result.Phase);
            Assert.Single(disclosures); // The already-recorded delivery of A is not erased.
        }
    }
}
