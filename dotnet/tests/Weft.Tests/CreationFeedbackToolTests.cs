using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weft.Ai;
using Weft.Api.Chat;
using Weft.Api.Chat.Workspace;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

// Real workspace tools and HTTP/SSE orchestration with recording compiler/store ports.
public sealed class CreationFeedbackToolTests
{
    [Fact]
    public async Task Retained_v2_feedback_returns_the_complete_structured_plan_and_digest()
    {
        var f = new CreationRuntimeFixture(); var original = await f.Generate();
        var plan = MappingCreationPlan.Parse(Encoding.UTF8.GetBytes(CreationProviderContractFixture.Plan("json", "xml").ToJsonString()));
        var p = await new MappingCreationGenerationService(f, f).GenerateAsync(WorkspaceToolFixture.Actor,
            Guid.NewGuid(), Guid.NewGuid(), f.Specification, plan, original.Notes, new("Structured mapping", ""));
        f.State = new(p); var before = f.State; f.Stale = true;
        using var provider = Provider(f, sourceUnavailable: true); var service = CreationRuntimeToolTests.Service(provider);
        var result = await service.ExecuteAsync(f.Catalog.Context(service, payloads: true), Intent(p), default);
        using var json = JsonDocument.Parse(result.Result.Json);
        Assert.Equal("available", json.RootElement.GetProperty("outcome").GetString());
        var data = json.RootElement.GetProperty("data");
        var returned = MappingCreationPlan.Parse(Encoding.UTF8.GetBytes(data.GetProperty("plan").GetRawText()));
        Assert.Equal(2, returned.Version); Assert.Equal(plan.Digest, returned.Digest);
        Assert.Equal(plan.Digest.Value, data.GetProperty("planDigest").GetString());
        Assert.True(data.GetProperty("requiresNewApproval").GetBoolean());
        Assert.Same(before, f.State); Assert.False(f.State.Approved);
        Assert.Equal(2, f.Compiles); Assert.Equal(0, f.Reads); Assert.Equal(0, f.Creates); Assert.Equal(0, f.Leases);
    }

    [Fact]
    public async Task Retained_feedback_preserves_approved_proposal_after_source_retirement()
    {
        var f = new CreationRuntimeFixture(); var p = await f.Generate(criticalQuestion: false);
        f.State = new(p, new(p.Fingerprint, ["a1"])); var before = f.State;
        f.Stale = true;
        using var provider = Provider(f, sourceUnavailable: true); var service = CreationRuntimeToolTests.Service(provider);
        var context = f.Catalog.Context(service, payloads: true);
        var result = await service.ExecuteAsync(context, Intent(p), default);
        using var json = JsonDocument.Parse(result.Result.Json);
        Assert.Equal("available", json.RootElement.GetProperty("outcome").GetString());
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(p.Plan.Digest.Value, data.GetProperty("planDigest").GetString());
        Assert.Equal(p.Plan.CopyBytes(), Encoding.UTF8.GetBytes(data.GetProperty("plan").GetRawText()));
        Assert.True(data.GetProperty("requiresNewApproval").GetBoolean());
        Assert.True(data.GetProperty("assumptions")[0].GetProperty("critical").GetBoolean());
        Assert.Equal(f.Source.Identity.SourceId.ToString("D"), data.GetProperty("sources")[0].GetProperty("sourceId").GetString());
        Assert.Equal(p.Fingerprint.Value, Assert.Single(result.WorkspaceCitations).ProposalFingerprint!.Value);
        Assert.Equal(ChatPayloadSource.Specification, result.DataShared!.Source);
        Assert.Equal(p.Id, result.DataShared.SubjectId); Assert.Null(result.DataShared.Version);
        Assert.Equal(result.Result.ByteCount, result.DataShared.ByteCount);
        Assert.Equal(new ChatRetainedProposalRead(p.Project, p.Id, p.Fingerprint), result.RetainedProposal);
        Assert.InRange(result.Result.ByteCount, 1, 12_288); Assert.InRange(result.RetrievedNodes, 2, 512);
        Assert.DoesNotContain("native-fixture", result.Result.Json); Assert.DoesNotContain("Private customer evidence", result.Result.Json);
        Assert.Same(before, f.State); Assert.Equal(1, f.Compiles); Assert.Equal(0, f.Reads);
        Assert.Equal(0, f.Creates); Assert.Equal(0, f.Leases); Assert.False(service.IncludeHistory(context.Turn));
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("actor")]
    [InlineData("project")]
    [InlineData("content")]
    [InlineData("grant")]
    [InlineData("profile")]
    [InlineData("runtime")]
    [InlineData("revoke-content-after-read")]
    [InlineData("revoke-grant-after-read")]
    [InlineData("substituted-proposal")]
    public async Task Unauthorized_or_substituted_feedback_never_discloses_retained_plan(string fault)
    {
        var f = new CreationRuntimeFixture(); var p = await f.Generate(criticalQuestion: true);
        var store = new RecordingStore(f);
        using var provider = Provider(f, store: store); var service = CreationRuntimeToolTests.Service(provider);
        if (fault == "content") f.Denied = true;
        if (fault == "grant") f.Catalog.Payloads!.Grants.Clear();
        if (fault == "runtime") f.Ready = false;
        if (fault == "revoke-content-after-read") store.AfterRead = () => f.Denied = true;
        if (fault == "revoke-grant-after-read") store.AfterRead = () => f.Catalog.Payloads!.Grants.Clear();
        if (fault == "substituted-proposal")
        {
            var other = await new MappingCreationGenerationService(f, f).GenerateAsync(WorkspaceToolFixture.Actor,
                Guid.NewGuid(), Guid.NewGuid(), f.Specification, f.Plan, p.Notes, new("Other", ""));
            store.Substitution = new(other);
        }
        var context = f.Catalog.Context(service, payloads: fault != "profile",
            actor: fault == "actor" ? new(new(Guid.NewGuid())) : null);
        var result = await service.ExecuteAsync(context, Intent(p,
            fingerprint: fault == "fingerprint" ? new string('a', 64) : null,
            project: fault == "project" ? WorkspaceToolFixture.Hidden.ProjectId : null), default);
        Assert.Null(result.DataShared); Assert.Empty(result.WorkspaceCitations);
        Assert.DoesNotContain("weft.creation-plan", result.Result.Json);
        Assert.DoesNotContain("Confirm output", result.Result.Json);
        Assert.Equal(0, f.Reads); Assert.Equal(0, f.Creates); Assert.Equal(0, f.Leases);
        if (fault is "actor" or "project" or "grant" or "profile" or "runtime") Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("nodes")]
    [InlineData("whole-plan")]
    public async Task Feedback_refuses_whole_content_when_its_budget_is_exceeded(string limit)
    {
        var f = new CreationRuntimeFixture(); var p = await f.Generate();
        if (limit == "whole-plan")
        {
            var fields = Enumerable.Range(0, 60).Select(i => new CreationField("field" + i + new string('x', 95), CreationScalarType.Text, true)).ToArray();
            var plan = new MappingCreationPlan(fields, fields, fields.Select(x => new CreationBinding(x.Name, x.Name)));
            p = await new MappingCreationGenerationService(f, f).GenerateAsync(WorkspaceToolFixture.Actor, Guid.NewGuid(), Guid.NewGuid(),
                f.Specification, plan, p.Notes, new("Large", ""));
            f.State = new(p);
            Assert.True(p.Plan.CopyBytes().Length > 12_288);
        }
        using var provider = Provider(f); var service = CreationRuntimeToolTests.Service(provider);
        var result = await service.ExecuteAsync(f.Catalog.Context(service, payloads: true,
            remainingBytes: limit == "bytes" ? 256 : 32_768, remainingNodes: limit == "nodes" ? 1 : 512), Intent(p), default);
        Assert.Contains("resourceLimit", result.Result.Json); Assert.Null(result.DataShared);
        Assert.DoesNotContain("weft.creation-plan", result.Result.Json); Assert.Empty(result.WorkspaceCitations);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("profile-policy")]
    [InlineData("retained-content")]
    [InlineData("retained-fingerprint")]
    public async Task Provider_wire_receives_feedback_only_after_audit_and_current_authorization(string revoke)
    {
        var f = new CreationRuntimeFixture(); var p = await f.Generate(criticalQuestion: true);
        var replacement = await new MappingCreationGenerationService(f, f).GenerateAsync(WorkspaceToolFixture.Actor,
            p.Id, p.RequestKey, f.Specification, f.Plan, p.Notes, new("Changed retained content", ""));
        Assert.NotEqual(p.Fingerprint, replacement.Fingerprint);
        f.Stale = true;
        using var provider = Provider(f, sourceUnavailable: true); var service = CreationRuntimeToolTests.Service(provider);
        ChatOrchestrationFixture? capture = null; Guid turnId = Guid.NewGuid();
        await using var wire = new ChatOrchestrationFixture(async (exchange, ct) =>
        {
            using var request = exchange.Request.Json();
            if (exchange.Ordinal == 1)
            {
                Assert.False(exchange.Request.ContainsUtf8("Confirm output"));
                await exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(ChatOrchestrationFixture.Call(0, "feedback",
                    Intent(p).Arguments.Json, "read_specification") + ChatOrchestrationFixture.Finish("tool_calls") + ChatOrchestrationFixture.Done), ct);
                return;
            }
            Assert.Equal(2, exchange.Ordinal);
            var shared = Assert.Single(capture!.Store.Events(turnId), e => e.Kind == ChatEventKind.DataShared);
            var fact = ChatMetadataCodec.ReadDisclosure(shared.MetadataJson!);
            Assert.Equal(p.Id, fact.SubjectId); Assert.Equal(ChatPayloadSource.Specification, fact.Source);
            Assert.Equal(5, (int)fact.Source);
            var tool = request.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "tool");
            var content = tool.GetProperty("content").GetString()!;
            Assert.Equal(Encoding.UTF8.GetByteCount(content), fact.ByteCount);
            using var feedback = JsonDocument.Parse(content);
            Assert.Equal(p.Plan.Digest.Value, feedback.RootElement.GetProperty("data").GetProperty("planDigest").GetString());
            Assert.Contains("Confirm output", content);
            await exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(ChatOrchestrationFixture.Text("Please confirm the requested change.") +
                ChatOrchestrationFixture.Finish("stop") + ChatOrchestrationFixture.Done), ct);
        }, actor: WorkspaceToolFixture.Actor, conversation: f.Catalog.Conversation(), tools: service.PayloadManifest!.Value,
            toolPort: service, instructions: WorkspaceChatGrounding.Instructions, workspaceCitationPort: service, contextPort: service, payloadsAllowed: true);
        capture = wire;
        wire.Store.Returning = call =>
        {
            if (call.Event?.Kind == ChatEventKind.DataShared)
            {
                if (revoke == "grant") f.Catalog.Payloads!.Grants.Clear();
                if (revoke == "membership") f.Catalog.RevokedProjects.Add(WorkspaceToolFixture.Project);
                if (revoke == "profile-policy") wire.ProfilePolicy.Check = (_, _, _, _, _) => Task.FromResult(false);
                if (revoke == "retained-content") f.Denied = true;
                if (revoke == "retained-fingerprint") f.State = new(replacement);
            }
            return Task.CompletedTask;
        };
        var budget = ChatGrounding.ProviderBudget(new ChatApiOptions([new("Fixture", "fixture", wire.Profile)],
            TimeSpan.FromDays(1), TimeSpan.FromSeconds(60), 512), service.PayloadGrounding!);
        var turn = new ChatTurnIntent(turnId, new(Guid.NewGuid(), "Discuss my retained proposal before making a new one.", [], budget,
            service.PayloadGrounding!, ChatTurnFocus.None), ChatAiBridge.PinProfile("fixture", wire.Profile),
            ChatGrounding.TurnBudgetFor(service.PayloadGrounding!), ChatOrchestrationFixture.Now.AddTicks(budget.DurationTicks));
        Assert.Equal(revoke == "none" ? ChatOrchestrationOutcome.Completed : ChatOrchestrationOutcome.Denied,
            (await ChatOrchestrationFixture.FinishExecution(wire.Begin(turn))).Outcome);
        Assert.Equal(revoke == "none" ? 2 : 1, wire.Server.RequestCount);
        Assert.Single(wire.Store.Events(turnId), e => e.Kind == ChatEventKind.DataShared);
        Assert.Equal(2, f.Compiles); Assert.Equal(0, f.Reads); Assert.Equal(0, f.Creates); Assert.Equal(0, f.Leases);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("content")]
    [InlineData("fingerprint")]
    public async Task Retained_content_is_reauthorized_again_after_an_already_authorized_provider_round(string revoke)
    {
        var f = new CreationRuntimeFixture(); var p = await f.Generate(criticalQuestion: true);
        var replacement = await new MappingCreationGenerationService(f, f).GenerateAsync(WorkspaceToolFixture.Actor,
            p.Id, p.RequestKey, f.Specification, f.Plan, p.Notes, new("Changed retained content", ""));
        f.Stale = true;
        using var provider = Provider(f, sourceUnavailable: true); var service = CreationRuntimeToolTests.Service(provider);
        await using var wire = new ChatOrchestrationFixture(async (exchange, ct) =>
        {
            string response;
            if (exchange.Ordinal == 1)
                response = ChatOrchestrationFixture.Call(0, "feedback", Intent(p).Arguments.Json, "read_specification") +
                    ChatOrchestrationFixture.Finish("tool_calls") + ChatOrchestrationFixture.Done;
            else if (exchange.Ordinal == 2)
            {
                Assert.True(exchange.Request.ContainsUtf8("Confirm output"));
                if (revoke == "content") f.Denied = true;
                if (revoke == "fingerprint") f.State = new(replacement);
                response = ChatOrchestrationFixture.Call(0, "projects", "{}", "list_projects") +
                    ChatOrchestrationFixture.Finish("tool_calls") + ChatOrchestrationFixture.Done;
            }
            else
            {
                Assert.Equal(3, exchange.Ordinal); Assert.Equal("none", revoke);
                Assert.True(exchange.Request.ContainsUtf8("Confirm output"));
                response = ChatOrchestrationFixture.Text("The retained proposal remains authorized.") +
                    ChatOrchestrationFixture.Finish("stop") + ChatOrchestrationFixture.Done;
            }
            await exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(response), ct);
        }, actor: WorkspaceToolFixture.Actor, conversation: f.Catalog.Conversation(), tools: service.PayloadManifest!.Value,
            toolPort: service, instructions: WorkspaceChatGrounding.Instructions, workspaceCitationPort: service, contextPort: service, payloadsAllowed: true);
        var budget = ChatGrounding.ProviderBudget(new ChatApiOptions([new("Fixture", "fixture", wire.Profile)],
            TimeSpan.FromDays(1), TimeSpan.FromSeconds(60), 512), service.PayloadGrounding!);
        var turn = new ChatTurnIntent(Guid.NewGuid(), new(Guid.NewGuid(), "Discuss the retained proposal and list my projects.", [], budget,
            service.PayloadGrounding!, ChatTurnFocus.None), ChatAiBridge.PinProfile("fixture", wire.Profile),
            ChatGrounding.TurnBudgetFor(service.PayloadGrounding!), ChatOrchestrationFixture.Now.AddTicks(budget.DurationTicks));
        var result = await ChatOrchestrationFixture.FinishExecution(wire.Begin(turn));
        Assert.Equal(revoke == "none" ? ChatOrchestrationOutcome.Completed : ChatOrchestrationOutcome.Denied, result.Outcome);
        Assert.Equal(revoke == "none" ? 3 : 2, wire.Server.RequestCount);
        var fact = ChatMetadataCodec.ReadDisclosure(Assert.Single(wire.Store.Events(turn.TurnId), e => e.Kind == ChatEventKind.DataShared).MetadataJson!);
        Assert.Equal(p.Id, fact.SubjectId); Assert.Equal(ChatPayloadSource.Specification, fact.Source);
        Assert.Equal(0, f.Reads); Assert.Equal(0, f.Creates); Assert.Equal(0, f.Leases);
    }

    private static AiToolIntent Intent(MappingCreationProposal p, string? fingerprint = null, Guid? project = null) =>
        AiToolIntent.Create("feedback", "read_specification", AiJsonObject.CopyFrom(JsonSerializer.Serialize(new
        {
            mode = "proposal",
            projectId = project ?? p.Project.ProjectId,
            proposalId = p.Id,
            fingerprint = fingerprint ?? p.Fingerprint.Value
        })));

    private static ServiceProvider Provider(CreationRuntimeFixture f, bool sourceUnavailable = false, RecordingStore? store = null)
    {
        var services = new ServiceCollection(); f.Register(services);
        if (sourceUnavailable) services.AddSingleton<ISpecBundleServiceFactory>(new RetiredSources());
        if (store is not null) services.AddSingleton<IMappingCreationProposalStore>(store);
        return services.BuildServiceProvider();
    }
    private sealed class RetiredSources : ISpecBundleServiceFactory
    {
        public Task<SpecBundleService> OpenAsync(ActorContext actor, bool write, CancellationToken ct) =>
            throw new InvalidOperationException("Retained feedback must not reopen the source store.");
    }
    private sealed class RecordingStore(CreationRuntimeFixture f) : IMappingCreationProposalStore
    {
        internal int Reads;
        internal Action? AfterRead;
        internal MappingCreationState? Substitution;
        public async Task<MappingCreationState?> ReadAsync(ActorContext actor, ProjectScope project, Guid id, CancellationToken ct)
        {
            Reads++; var result = Substitution ?? await f.ReadAsync(actor, project, id, ct); AfterRead?.Invoke(); return result;
        }
        public Task<MappingCreationState?> RetainAsync(ActorContext actor, MappingCreationProposal proposal, CancellationToken ct) =>
            throw new InvalidOperationException("Feedback must not retain a replacement.");
    }
}
