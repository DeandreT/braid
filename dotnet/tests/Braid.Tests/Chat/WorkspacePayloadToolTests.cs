using System.Text;
using System.Text.Json;
using Braid.Ai;
using Braid.Api.Chat;
using Braid.Api.Chat.Workspace;
using Braid.Application;
using Braid.Domain;
using static Braid.Tests.ChatOrchestrationFixture;

namespace Braid.Tests;

public sealed class WorkspacePayloadToolTests
{
    private static readonly string RunId = WorkspaceToolFixture.Id(WorkspacePayloadFakes.RunId);
    private static readonly string MappingId = WorkspaceToolFixture.Id(WorkspaceToolFixture.Mapping.MappingId);

    private static string Args(string runId, int ordinal) => "{\"runId\":\"" + runId + "\",\"ordinal\":" + ordinal + "}";

    private static ChatModelProfile Profile(bool payloads, bool metadata = true) => new("Fixture model", "fixture",
        AiProfileSpec.Create(AiProfileKey.Create("payload-fixture", 1), AiDialect.ChatCompletionsSseV1, "fixture-model",
            new Uri("http://127.0.0.1:9/v1/chat/completions"), true, AiAuthentication.Bearer, "fixture-credential",
            AiCapability.Text | AiCapability.FunctionTools, "mapping-metadata", 1, metadata, false, payloads));

    [Fact]
    public void Payload_tools_exist_only_in_the_payload_manifest_of_a_host_with_the_grant_reader()
    {
        var without = new WorkspaceToolFixture().Service(previews: true);
        Assert.Null(without.PayloadManifest);
        Assert.Null(without.PayloadGrounding);
        var with = new WorkspaceToolFixture(payloads: true).Service(previews: true);
        Assert.DoesNotContain(with.Manifest, tool => WorkspaceToolRegistry.IsPayloadTool(tool.Name));
        Assert.Equal(new[] { "get_preview_input", "get_preview_output" },
            with.PayloadManifest!.Value.Where(tool => WorkspaceToolRegistry.IsPayloadTool(tool.Name)).Select(tool => tool.Name));
        Assert.NotEqual(with.Grounding, with.PayloadGrounding);
        // A host serving neither previews nor regression tests has no payload manifest even with the reader.
        Assert.Null(new WorkspaceToolFixture(payloads: true).Service().PayloadManifest);
        var all = WorkspaceToolRegistry.Definitions(new(true, true, true, true), payloads: true);
        Assert.InRange(all.Length, 1, ChatAiBridge.MaxTools);
        Assert.InRange(all.Sum(tool => tool.Parameters.ByteCount), 1, AiCeilings.SchemasTotalBytes);
        // Payload profiles imply mapping metadata, and only a set flag changes a profile's pin.
        Assert.ThrowsAny<Exception>(() => Profile(payloads: true, metadata: false));
        Assert.Equal(ChatAiBridge.PinProfile("fixture", Profile(false).Spec), ChatAiBridge.PinProfile("fixture", Profile(false).Spec));
        Assert.NotEqual(ChatAiBridge.PinProfile("fixture", Profile(false).Spec), ChatAiBridge.PinProfile("fixture", Profile(true).Spec));
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("otherProject", false)]
    [InlineData("otherProject", true)]
    [InlineData("revokedMembership", false)]
    [InlineData("revokedMembership", true)]
    [InlineData("active", false)]
    [InlineData("active", true)]
    public async Task Payload_tools_are_offered_only_when_both_the_grant_and_the_profile_allow_them(string grant, bool payloadsAllowed)
    {
        var fixture = new WorkspaceToolFixture(payloads: true);
        var service = fixture.Service(previews: true);
        if (grant is "active" or "revokedMembership") fixture.Payloads!.Grants.Add(WorkspaceToolFixture.Project);
        if (grant == "otherProject") fixture.Payloads!.Grants.Add(WorkspaceToolFixture.Hidden);
        if (grant == "revokedMembership") fixture.Role = null;
        var pinned = await service.GroundingForAsync(WorkspaceToolFixture.Actor, WorkspaceToolFixture.Organization, Profile(payloadsAllowed), CancellationToken.None);
        var offered = grant == "active" && payloadsAllowed;
        Assert.Equal(offered ? service.PayloadGrounding : service.Grounding, pinned);
        // The grant is not even observed for a profile that cannot receive payload data.
        Assert.Equal(payloadsAllowed ? 1 : 0, fixture.Payloads!.Presences);

        // The profile is part of every recheck: a payload turn never runs under a profile without the flag.
        var payloadTurn = WorkspaceToolFixture.Turn(service, payloads: true);
        Assert.Equal(payloadsAllowed, service.Allows(Profile(payloadsAllowed), payloadTurn));
        Assert.True(service.Allows(Profile(payloadsAllowed), WorkspaceToolFixture.Turn(service)));

        // Inside a payload turn every call observes the grant for the data's own project.
        fixture.Role = ProjectRole.Viewer;
        var reply = await fixture.CallAsync(service, "get_preview_output", Args(RunId, 0), fixture.Context(service, payloads: true));
        Assert.Equal(grant is "active" or "revokedMembership" ? "available" : "notPermitted", reply.Outcome);
        Assert.Equal(reply.Outcome == "available", reply.Result.DataShared is not null);
    }

    [Fact]
    public async Task A_payload_manifest_is_not_reoffered_after_its_last_grant_is_revoked()
    {
        var fixture = new WorkspaceToolFixture(payloads: true);
        var service = fixture.Service(previews: true);
        var turn = WorkspaceToolFixture.Turn(service, payloads: true);
        fixture.Payloads!.Grants.Add(WorkspaceToolFixture.Project);
        Assert.True(await service.AllowsAsync(WorkspaceToolFixture.Actor, WorkspaceToolFixture.Organization, Profile(true), turn, CancellationToken.None));
        fixture.Payloads.Grants.Clear();
        Assert.False(await service.AllowsAsync(WorkspaceToolFixture.Actor, WorkspaceToolFixture.Organization, Profile(true), turn, CancellationToken.None));
        Assert.True(await service.AllowsAsync(WorkspaceToolFixture.Actor, WorkspaceToolFixture.Organization, Profile(false),
            WorkspaceToolFixture.Turn(service), CancellationToken.None));
        Assert.False(await service.AllowsAsync(WorkspaceToolFixture.Actor, WorkspaceToolFixture.OtherOrganization, Profile(true), turn, CancellationToken.None));
    }

    [Fact]
    public async Task Every_call_observes_the_grant_before_and_after_the_read_and_records_what_was_shared()
    {
        var fixture = new WorkspaceToolFixture(payloads: true);
        var service = fixture.Service(previews: true);
        var payloads = fixture.Payloads!;
        payloads.Grants.Add(WorkspaceToolFixture.Project);
        var context = fixture.Context(service, payloads: true);

        var input = await fixture.CallAsync(service, "get_preview_input", Args(RunId, 0), context);
        Assert.Equal("available", input.Outcome);
        Assert.Equal(Encoding.UTF8.GetString(WorkspacePayloadFakes.Primary), input.Data.GetProperty("text").GetString());
        Assert.Equal("primary", input.Data.GetProperty("documentName").GetString());
        Assert.False(input.Data.GetProperty("truncated").GetBoolean());
        Assert.Equal(new ChatPayloadDisclosureFact(WorkspaceToolFixture.Project.ProjectId, ChatPayloadSource.PreviewInput, WorkspacePayloadFakes.RunId,
            null, WorkspacePayloadFakes.Primary.Length), input.Result.DataShared);
        Assert.Equal(new[] { ChatWorkspaceCitationFact.PreviewRun(WorkspaceToolFixture.Head, WorkspacePayloadFakes.RunId) }, input.Result.WorkspaceCitations);
        Assert.Equal(2, payloads.Observations);

        var output = await fixture.CallAsync(service, "get_preview_output", Args(RunId, 0), context);
        Assert.Equal("totals", output.Data.GetProperty("documentName").GetString());
        Assert.Equal("application/json", output.Data.GetProperty("mediaType").GetString());
        Assert.Equal(ChatPayloadSource.PreviewOutput, output.Result.DataShared!.Source);

        // Revoked between calls, then withdrawn during a call before release: neither shares anything.
        payloads.Grants.Clear();
        var revoked = await fixture.CallAsync(service, "get_preview_input", Args(RunId, 1), context);
        Assert.Equal("notPermitted", revoked.Outcome);
        Assert.Null(revoked.Result.DataShared);
        Assert.DoesNotContain("EUR", revoked.Json, StringComparison.Ordinal);
        payloads.Grants.Add(WorkspaceToolFixture.Project);
        payloads.OnObserved = () => payloads.Grants.Clear();
        var withdrawn = await fixture.CallAsync(service, "get_preview_input", Args(RunId, 1), context);
        Assert.Equal("notPermitted", withdrawn.Outcome);
        Assert.Null(withdrawn.Result.DataShared);
        Assert.DoesNotContain("EUR", withdrawn.Json, StringComparison.Ordinal);
        payloads.OnObserved = null;

        // The run itself becoming unreadable is the ordinary notFound.
        payloads.Grants.Add(WorkspaceToolFixture.Project);
        fixture.Role = null;
        Assert.Equal("notFound", (await fixture.CallAsync(service, "get_preview_input", Args(RunId, 0), context)).Outcome);
    }

    [Fact]
    public async Task Only_bounded_strict_text_is_shared()
    {
        var fixture = new WorkspaceToolFixture(payloads: true);
        var service = fixture.Service(previews: true);
        fixture.Payloads!.Grants.Add(WorkspaceToolFixture.Project);
        var context = fixture.Context(service, payloads: true);

        var large = await fixture.CallAsync(service, "get_preview_output", Args(RunId, 1), context);
        Assert.Equal("available", large.Outcome);
        Assert.True(large.Data.GetProperty("truncated").GetBoolean());
        var shared = large.Data.GetProperty("sharedBytes").GetInt32();
        Assert.InRange(shared, 1, WorkspaceChatGrounding.PayloadBytes);
        Assert.Equal(WorkspacePayloadFakes.Large.Length, large.Data.GetProperty("totalBytes").GetInt32());
        // The excerpt is whole UTF-8 scalars, byte for byte the leading part of the document.
        var text = large.Data.GetProperty("text").GetString()!;
        Assert.Equal(WorkspacePayloadFakes.Large.Take(shared), Encoding.UTF8.GetBytes(text));
        Assert.Equal(shared, large.Result.DataShared!.ByteCount);

        var tight = await fixture.CallAsync(service, "get_preview_output", Args(RunId, 1), fixture.Context(service, remainingBytes: 2_048, payloads: true));
        Assert.Equal("available", tight.Outcome);
        Assert.InRange(tight.Result.Result.ByteCount, 1, 2_048);
        Assert.InRange(tight.Result.DataShared!.ByteCount, 1, 2_048);

        foreach (var ordinal in new[] { 2, 3 })
        {
            var refused = await fixture.CallAsync(service, "get_preview_output", Args(RunId, ordinal), context);
            Assert.Equal("unsupportedContent", refused.Outcome);
            Assert.Null(refused.Result.DataShared);
        }
        Assert.Equal("notFound", (await fixture.CallAsync(service, "get_preview_output", Args(RunId, 9), context)).Outcome);
        Assert.Equal("notFound", (await fixture.CallAsync(service, "get_preview_input", Args(WorkspaceToolFixture.Id(Guid.NewGuid()), 0), context)).Outcome);
    }

    [Theory]
    [InlineData("application/edi-x12")]
    [InlineData("application/edifact")]
    public async Task Declared_edi_outputs_share_only_the_bounded_exact_utf8_excerpt(string media)
    {
        var fixture = new WorkspaceToolFixture(payloads: true);
        var service = fixture.Service(previews: true);
        var payloads = fixture.Payloads!;
        payloads.Grants.Add(WorkspaceToolFixture.Project);
        var bytes = DocumentPreviewTestData.Input(media == "application/edi-x12" ? "x12" : "edifact");
        payloads.OutputOverrides[0] = (bytes, media);
        var result = await fixture.CallAsync(service, "get_preview_output", Args(RunId, 0), fixture.Context(service, payloads: true));
        Assert.Equal("available", result.Outcome);
        Assert.Equal(media, result.Data.GetProperty("mediaType").GetString());
        Assert.Equal(Encoding.UTF8.GetString(bytes), result.Data.GetProperty("text").GetString());
        Assert.Equal(bytes.Length, result.Result.DataShared!.ByteCount);
        Assert.Equal(2, payloads.Observations);

        var large = Encoding.UTF8.GetBytes(new string('é', WorkspaceChatGrounding.PayloadBytes));
        payloads.OutputOverrides[0] = (large, media);
        var bounded = await fixture.CallAsync(service, "get_preview_output", Args(RunId, 0), fixture.Context(service, remainingBytes: 2_048, payloads: true));
        Assert.Equal("available", bounded.Outcome);
        Assert.True(bounded.Data.GetProperty("truncated").GetBoolean());
        var shared = bounded.Data.GetProperty("sharedBytes").GetInt32();
        Assert.InRange(shared, 1, 2_048);
        Assert.Equal(large.Length, bounded.Data.GetProperty("totalBytes").GetInt32());
        Assert.Equal(large.Take(shared), Encoding.UTF8.GetBytes(bounded.Data.GetProperty("text").GetString()!));
        Assert.Equal(shared, bounded.Result.DataShared!.ByteCount);
    }

    [Theory]
    [InlineData("application/edi-x12", "noGrant")]
    [InlineData("application/edifact", "noGrant")]
    [InlineData("application/edi-x12", "revoked")]
    [InlineData("application/edifact", "revoked")]
    [InlineData("application/edi-x12", "profile")]
    [InlineData("application/edifact", "profile")]
    [InlineData("application/edi-x12", "utf8")]
    [InlineData("application/edifact", "utf8")]
    [InlineData("application/edi-x12", "control")]
    [InlineData("application/edifact", "control")]
    [InlineData("application/edi-x12-unknown", "media")]
    [InlineData("application/edifact-binary", "media")]
    public async Task Edi_media_does_not_bypass_profile_current_grants_or_strict_text(string media, string refusal)
    {
        var fixture = new WorkspaceToolFixture(payloads: true);
        var service = fixture.Service(previews: true);
        var payloads = fixture.Payloads!;
        if (refusal != "noGrant") payloads.Grants.Add(WorkspaceToolFixture.Project);
        if (refusal == "revoked") payloads.OnObserved = payloads.Grants.Clear;
        byte[] bytes = refusal == "utf8" ? WorkspacePayloadFakes.Binary :
            Encoding.UTF8.GetBytes(new string('X', WorkspaceChatGrounding.PayloadBytes + 1) + (refusal == "control" ? "\u0007" : ""));
        payloads.OutputOverrides[0] = (bytes, media);
        var result = await fixture.CallAsync(service, "get_preview_output", Args(RunId, 0), fixture.Context(service, payloads: refusal != "profile"));
        Assert.Equal(refusal == "profile" ? "invalidCall" : refusal is "noGrant" or "revoked" ? "notPermitted" : "unsupportedContent", result.Outcome);
        Assert.Null(result.Result.DataShared);
        Assert.DoesNotContain("\"text\"", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_tools_list_and_read_the_users_own_previews_without_sharing_payload_data()
    {
        var fixture = new WorkspaceToolFixture(payloads: true);
        var service = fixture.Service(previews: true);
        var list = await fixture.CallAsync(service, "list_runs", "{\"kind\":\"preview\",\"mappingId\":\"" + MappingId + "\"}");
        Assert.Equal("available", list.Outcome);
        var item = Assert.Single(list.Data.GetProperty("items").EnumerateArray());
        Assert.Equal(RunId, item.GetProperty("runId").GetString());
        Assert.Equal("completed", item.GetProperty("state").GetString());
        Assert.Equal(new[] { ChatWorkspaceCitationFact.PreviewRun(WorkspaceToolFixture.Head, WorkspacePayloadFakes.RunId) }, list.Result.WorkspaceCitations);
        var run = await fixture.CallAsync(service, "get_run", "{\"kind\":\"preview\",\"runId\":\"" + RunId + "\"}");
        Assert.Equal("available", run.Outcome);
        Assert.Equal("succeeded", run.Data.GetProperty("outcome").GetString());
        Assert.Equal(new[] { "totals", "lines", "binary", "control" },
            run.Data.GetProperty("outputs").EnumerateArray().Select(output => output.GetProperty("targetName").GetString()));
        // Status and summaries only: no input or output bytes, no grant observation and nothing recorded as shared.
        Assert.DoesNotContain("SO-1", run.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("EUR", run.Json, StringComparison.Ordinal);
        Assert.Null(run.Result.DataShared);
        Assert.Equal(0, fixture.Payloads!.Observations);
        fixture.Role = null;
        Assert.Equal("notFound", (await fixture.CallAsync(service, "get_run", "{\"kind\":\"preview\",\"runId\":\"" + RunId + "\"}")).Outcome);
    }

    [Fact]
    public async Task Payload_tools_are_unusable_on_a_turn_pinned_without_them()
    {
        var fixture = new WorkspaceToolFixture(payloads: true);
        var service = fixture.Service(previews: true);
        fixture.Payloads!.Grants.Add(WorkspaceToolFixture.Project);
        var context = fixture.Context(service);
        var call = AiToolIntent.Create("call-1", "get_preview_input", AiJsonObject.CopyFrom(Args(RunId, 0)));
        Assert.False(await service.ValidateAsync(context, call, CancellationToken.None));
        Assert.Equal("invalidCall", (await fixture.CallAsync(service, call.Name, call.Arguments.Json, context)).Outcome);
        Assert.Equal(0, fixture.Payloads.Observations);
    }

    [Fact]
    public async Task A_payload_turn_records_shared_data_beside_the_tool_result()
    {
        var tools = new WorkspaceToolFixture(payloads: true);
        var service = tools.Service(previews: true);
        tools.Payloads!.Grants.Add(WorkspaceToolFixture.Project);
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
                exchange.Ordinal == 1 ? Call(0, "call-a", Args(RunId, 0), "get_preview_output") + Finish("tool_calls") + Done
                    : Text("The total is 42.") + Finish("stop") + Done), ct),
            actor: WorkspaceToolFixture.Actor, conversation: tools.Conversation(), tools: service.PayloadManifest!.Value, toolPort: service,
            instructions: WorkspaceChatGrounding.Instructions, workspaceCitationPort: service, contextPort: service, payloadsAllowed: true);
        var budget = new ChatProviderBudget(600_000_000, 3, 1_048_576, 4_194_304, 131_072, 8, 128);
        var intent = new ChatTurnIntent(Guid.NewGuid(), new(Guid.NewGuid(), "What was the total?", [], budget, service.PayloadGrounding!, ChatTurnFocus.None),
            ChatAiBridge.PinProfile("fixture", f.Profile), new(256, 262_144, 3), Now.AddTicks(600_000_000));
        var result = await FinishExecution(f.Begin(intent));
        Assert.Equal(ChatOrchestrationOutcome.Completed, result.Outcome);
        var events = f.Store.Events(intent.TurnId);
        var shared = Assert.Single(events, e => e.Kind == ChatEventKind.DataShared);
        Assert.Equal(new ChatPayloadDisclosureFact(WorkspaceToolFixture.Project.ProjectId, ChatPayloadSource.PreviewOutput, WorkspacePayloadFakes.RunId,
            null, WorkspacePayloadFakes.Output.Length), ChatMetadataCodec.ReadDisclosure(shared.MetadataJson!));
        // Recorded with the result's round, before the round completes.
        Assert.True(Array.IndexOf(events, shared) < Array.FindIndex(events, e => e.Kind == ChatEventKind.RoundCompleted));
        Assert.DoesNotContain("total", shared.MetadataJson, StringComparison.Ordinal);
    }
}
