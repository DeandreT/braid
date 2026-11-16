using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Braid.Ai;
using Braid.Api.Chat.Workspace;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends;
using static Braid.Tests.CreationProviderContractFixture;

namespace Braid.Tests;

public sealed class CreationProviderContractTests
{
    public static IEnumerable<object[]> Pairs => from source in Formats from target in Formats select new object[] { source, target };

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Advertised_wire_calls_fit_and_generate_all_36_boundary_pairs(string source, string target)
    {
        var f = new CreationRuntimeFixture("Synthetic specification: copy the code and preserve explicit envelope controls.");
        using var provider = CreationRuntimeToolTests.Provider(f); var service = CreationRuntimeToolTests.Service(provider);
        var args = Arguments(f, Plan(source, target));
        Assert.InRange(Encoding.UTF8.GetByteCount(args), 1, 16_384);
        var result = await service.ExecuteAsync(f.Catalog.Context(service, payloads: true),
            AiToolIntent.Create("synthetic", "propose_mapping_creation", AiJsonObject.CopyFrom(args)), default);
        Assert.True(f.State is not null, result.Result.Json);
        Assert.Equal(2, f.State.Proposal.Plan.Version);
        Assert.Equal(MappingCreationPlan.Parse(Encoding.UTF8.GetBytes(Plan(source, target).ToJsonString())).Digest, f.State.Proposal.Plan.Digest);
        Assert.Contains(f.State.Proposal.Fingerprint.Value, result.Result.Json);
        Assert.Equal(1, f.Compiles); Assert.Equal(0, f.Creates); Assert.Equal(0, f.Leases); Assert.False(f.State.Approved);
    }

    [MappingAuthoringBackendTests.NativeAuthoringFact]
    public async Task Synthetic_provider_wire_uses_advertised_contract_and_real_compiler_for_every_format_pair()
    {
        foreach (var source in Formats)
            foreach (var target in Formats)
            {
                var f = new CreationRuntimeFixture("Synthetic specification: copy code; retain explicit envelope fields.");
                var registrations = new ServiceCollection(); f.Register(registrations);
                registrations.AddSingleton<IMappingCreationCompiler>(new FerruleMappingCreationCompiler(new(new MappingAuthoringBackendTests.NativeRunner())));
                using var provider = registrations.BuildServiceProvider(); var service = CreationRuntimeToolTests.Service(provider);
                var arguments = Arguments(f, Plan(source, target));
                await using var wire = new ChatOrchestrationFixture(async (exchange, ct) =>
                {
                    using var request = exchange.Request.Json();
                    if (exchange.Ordinal == 1)
                    {
                        var instruction = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                        Assert.Contains("braid.creation-plan.v2", instruction); Assert.Contains("Position/position", instruction);
                        Assert.Contains("root scope iteration:null", instruction);
                        var definition = request.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function"))
                            .Single(t => t.GetProperty("name").GetString() == "propose_mapping_creation");
                        Assert.Contains("braid.creation-plan.v2", definition.GetProperty("description").GetString());
                        await exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(ChatOrchestrationFixture.Call(0, "synthetic-call", arguments,
                            "propose_mapping_creation") + ChatOrchestrationFixture.Finish("tool_calls") + ChatOrchestrationFixture.Done), ct);
                    }
                    else
                    {
                        var last = request.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "tool");
                        Assert.True(f.State is not null, source + "->" + target + ": " + last.GetProperty("content").GetString());
                        Assert.Contains(f.State.Proposal.Fingerprint.Value, last.GetProperty("content").GetString());
                        await exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(ChatOrchestrationFixture.Text("Review the retained proposal.") +
                            ChatOrchestrationFixture.Finish("stop") + ChatOrchestrationFixture.Done), ct);
                    }
                }, actor: WorkspaceToolFixture.Actor, conversation: f.Catalog.Conversation(), tools: service.PayloadManifest!.Value,
                    toolPort: service, instructions: WorkspaceChatGrounding.Instructions, workspaceCitationPort: service, contextPort: service, payloadsAllowed: true);
                var budget = new ChatProviderBudget(600_000_000, 3, 1_048_576, 4_194_304, 131_072, 8, 2048);
                var turn = new ChatTurnIntent(Guid.NewGuid(), new(Guid.NewGuid(), "Create the requested synthetic mapping.", [], budget,
                    service.PayloadGrounding!, ChatTurnFocus.None), ChatAiBridge.PinProfile("fixture", wire.Profile), new(256, 262_144, 3),
                    ChatOrchestrationFixture.Now.AddTicks(budget.DurationTicks));
                Assert.Equal(ChatOrchestrationOutcome.Completed, (await ChatOrchestrationFixture.FinishExecution(wire.Begin(turn))).Outcome);
                Assert.Equal(2, wire.Server.RequestCount);
                var compilation = f.State!.Proposal.Compilation;
                Assert.Equal(FerruleInspectionBackend.EngineRevision, compilation.EngineRevision);
                Assert.True(JsonSerializer.Deserialize<MappingAuthoringValidation>(compilation.CopyValidation())!.IsValid);
                Assert.Equal(Sha256Digest.FromBytes(compilation.CopyNative()), compilation.NativeDigest);
                Assert.Single(wire.Store.Events(turn.TurnId), e => e.Kind == ChatEventKind.WorkspaceCitation);
                Assert.Equal(0, f.Creates); Assert.Equal(0, f.Leases); Assert.False(f.State.Approved);
            }
    }

    [Theory]
    [InlineData("position")]
    [InlineData("frame")]
    [InlineData("xml-root-iteration")]
    [InlineData("unknown-option")]
    [InlineData("missing-node-key")]
    public async Task Unsupported_advertised_boundaries_refuse_before_compilation(string fault)
    {
        var f = new CreationRuntimeFixture(); var plan = Plan("json", "xml");
        var value = plan["scope"]!["bindings"]![0]!["value"]!;
        switch (fault)
        {
            case "position": value["kind"] = "position"; break;
            case "frame": value["frame"] = new JsonArray(); break;
            case "xml-root-iteration": plan["scope"]!["iteration"] = new JsonArray(); break;
            case "unknown-option": plan["target"]!["options"]!["guess"] = true; break;
            case "missing-node-key": plan["target"]!["schema"]!.AsObject().Remove("xml"); break;
        }
        using var provider = CreationRuntimeToolTests.Provider(f); var service = CreationRuntimeToolTests.Service(provider);
        var result = await service.ExecuteAsync(f.Catalog.Context(service, payloads: true), AiToolIntent.Create("invalid", "propose_mapping_creation",
            AiJsonObject.CopyFrom(Arguments(f, plan))), default);
        // The runtime currently categorizes strict domain-codec failures as unavailable.
        // The provider must receive a bounded refusal, never a compilation or retained proposal.
        using var response = JsonDocument.Parse(result.Result.Json);
        Assert.Equal("unavailable", response.RootElement.GetProperty("outcome").GetString());
        Assert.Null(f.State); Assert.Equal(0, f.Compiles); Assert.Equal(0, f.Creates);
    }

    [MappingAuthoringBackendTests.NativeAuthoringFact]
    public async Task Advertised_absolute_paths_lower_to_frames_under_a_singular_xml_root()
    {
        var plan = Plan("json", "xml");
        var rows = Node("rows", Node("code")); rows["repeating"] = true;
        var items = Node("item", Node("value")); items["repeating"] = true;
        plan["source"]!["schema"] = Node("Input", rows);
        plan["target"]!["schema"] = Node("Document", items);
        plan["scope"] = Scope(null, [], new JsonArray(Scope("item", new JsonArray(Binding("value", Source("rows", "code"))), [], Strings("rows"))));
        var f = new CreationRuntimeFixture("Synthetic specification: copy each row code into an XML item.");
        var registrations = new ServiceCollection(); f.Register(registrations);
        registrations.AddSingleton<IMappingCreationCompiler>(new FerruleMappingCreationCompiler(new(new MappingAuthoringBackendTests.NativeRunner())));
        using var provider = registrations.BuildServiceProvider(); var service = CreationRuntimeToolTests.Service(provider);
        var result = await service.ExecuteAsync(f.Catalog.Context(service, payloads: true), AiToolIntent.Create("iterate", "propose_mapping_creation",
            AiJsonObject.CopyFrom(Arguments(f, plan))), default);
        Assert.True(f.State is not null, result.Result.Json);
        using var lowered = JsonDocument.Parse(f.State.Proposal.Plan.CopyCompilerPlanBytes());
        var expression = lowered.RootElement.GetProperty("edit").GetProperty("operations").EnumerateArray()
            .Single(o => o.GetProperty("op").GetString() == "bind").GetProperty("expression");
        Assert.Equal("rows", Assert.Single(expression.GetProperty("frame").EnumerateArray()).GetString());
        Assert.Equal("code", Assert.Single(expression.GetProperty("path").EnumerateArray()).GetString());
        Assert.True(JsonSerializer.Deserialize<MappingAuthoringValidation>(f.State.Proposal.Compilation.CopyValidation())!.IsValid);
        Assert.Equal(0, f.Creates); Assert.False(f.State.Approved);
    }

    [Fact]
    public void Proposal_context_parser_requires_exact_identity_and_excludes_other_modes_arguments()
    {
        var manifest = WorkspaceToolRegistry.Definitions(new(false, false, false, false, Creation: true), true);
        var args = new JsonObject
        {
            ["projectId"] = Guid.NewGuid().ToString("D"),
            ["mode"] = "proposal",
            ["proposalId"] = Guid.NewGuid().ToString("D"),
            ["fingerprint"] = new string('a', 64)
        };
        var call = WorkspaceToolRegistry.Parse("read_specification", args.ToJsonString(), manifest);
        Assert.Equal(new string('a', 64), call.Text("fingerprint", 64));
        foreach (var key in new[] { "proposalId", "fingerprint" })
        { var invalid = args.DeepClone().AsObject(); invalid.Remove(key); Assert.Throws<WorkspaceToolRefusal>(() => WorkspaceToolRegistry.Parse("read_specification", invalid.ToJsonString(), manifest)); }
        foreach (var key in new[] { "bundleId", "extractionIds", "sourceIndex", "offset", "limit", "afterBundle", "afterObject", "afterProposal" })
        {
            var invalid = args.DeepClone();
            invalid[key] = key == "extractionIds" ? Strings(Guid.NewGuid().ToString("D")) : key is "sourceIndex" or "offset" or "limit" ? JsonValue.Create(1) : JsonValue.Create(Guid.NewGuid().ToString("D"));
            Assert.Throws<WorkspaceToolRefusal>(() => WorkspaceToolRegistry.Parse("read_specification", invalid.ToJsonString(), manifest));
        }
        foreach (var fingerprint in new[] { new string('A', 64), new string('g', 64), new string('a', 63) })
        { var invalid = args.DeepClone(); invalid["fingerprint"] = fingerprint; Assert.Throws<WorkspaceToolRefusal>(() => WorkspaceToolRegistry.Parse("read_specification", invalid.ToJsonString(), manifest)); }
        foreach (var mode in new[] { "selections", "proposals", "evidence" })
        { var invalid = args.DeepClone(); invalid["mode"] = mode; Assert.Throws<WorkspaceToolRefusal>(() => WorkspaceToolRegistry.Parse("read_specification", invalid.ToJsonString(), manifest)); }
    }

    [Fact]
    public void Tool_budget_stays_16KiB_despite_larger_valid_plan_budget()
    {
        var f = new CreationRuntimeFixture(); var plan = Plan("json", "json");
        var columns = plan["target"]!["schema"]!["shape"]!["children"]!.AsArray();
        var bindings = plan["scope"]!["bindings"]!.AsArray();
        for (var i = 0; i < 4; i++) { columns.Add(Node("extra" + i)); bindings.Add(Binding("extra" + i, Constant(new string('x', 4000)))); }
        var application = MappingCreationPlan.Parse(Encoding.UTF8.GetBytes(plan.ToJsonString()));
        Assert.InRange(application.CopyBytes().Length, 16_385, 65_536);
        var manifest = WorkspaceToolRegistry.Definitions(new(false, false, false, false, Creation: true), true);
        Assert.Throws<WorkspaceToolRefusal>(() => WorkspaceToolRegistry.Parse("propose_mapping_creation", Arguments(f, plan), manifest));
        var small = Arguments(f, Plan("json", "json"));
        var exact = small + new string(' ', 16_384 - Encoding.UTF8.GetByteCount(small));
        Assert.NotNull(AiToolIntent.Create("exact", "propose_mapping_creation", AiJsonObject.CopyFrom(exact)));
        Assert.Throws<ArgumentException>(() => AiToolIntent.Create("over", "propose_mapping_creation", AiJsonObject.CopyFrom(exact + " ")));
        Assert.NotNull(WorkspaceToolRegistry.Parse("propose_mapping_creation", exact, manifest));
        Assert.Throws<WorkspaceToolRefusal>(() => WorkspaceToolRegistry.Parse("propose_mapping_creation", exact + " ", manifest));
    }

    [Fact]
    public void Provider_depth_refuses_a_domain_valid_plan_before_the_workspace_depth_ceiling()
    {
        var f = new CreationRuntimeFixture(); var plan = Plan("json", "json");
        JsonObject node = Node("code");
        var path = new List<string> { "code" };
        for (var i = 0; i < 5; i++) { var name = "group" + i; node = Node(name, node); path.Insert(0, name); }
        plan["source"]!["schema"] = Node("Document", node);
        plan["scope"]!["bindings"]![0]!["value"] = Source(path.ToArray());
        Assert.NotNull(MappingCreationPlan.Parse(Encoding.UTF8.GetBytes(plan.ToJsonString())));
        var args = Arguments(f, plan);
        var manifest = WorkspaceToolRegistry.Definitions(new(false, false, false, false, Creation: true), true);
        Assert.NotNull(WorkspaceToolRegistry.Parse("propose_mapping_creation", args, manifest));
        Assert.Throws<ArgumentException>(() => AiJsonObject.CopyFrom(args));
    }

    [Fact]
    public async Task Legacy_v1_wire_remains_accepted_and_policy_pins_are_updated()
    {
        var f = new CreationRuntimeFixture(); using var provider = CreationRuntimeToolTests.Provider(f); var service = CreationRuntimeToolTests.Service(provider);
        var legacy = JsonNode.Parse("""{"schema":"braid.creation-plan.v1","format":"json-record","source":[{"Name":"a","Type":1,"Required":true}],"target":[{"Name":"b","Type":1,"Required":true}],"bindings":[{"Source":"a","Target":"b"}]}""")!.AsObject();
        var result = await service.ExecuteAsync(f.Catalog.Context(service, payloads: true), AiToolIntent.Create("legacy", "propose_mapping_creation",
            AiJsonObject.CopyFrom(Arguments(f, legacy))), default);
        Assert.True(f.State is not null, result.Result.Json); Assert.Equal(1, f.State.Proposal.Plan.Version);
        Assert.Equal(8, service.Grounding.Version); Assert.Equal(8, service.PayloadGrounding!.Version);
        Assert.NotEqual(service.Grounding.ToolsFingerprint, service.PayloadGrounding.ToolsFingerprint);
        Assert.Equal(Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(WorkspaceChatGrounding.Instructions)), service.PayloadGrounding.InstructionsFingerprint);
    }
}
