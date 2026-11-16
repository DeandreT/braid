using Braid.Ai;
using Braid.Api.Chat;
using Braid.Api.Chat.Workspace;
using Braid.Application;
using Braid.Domain;
using static Braid.Tests.ChatOrchestrationFixture;

namespace Braid.Tests;

/// <summary>Live local provider qualification with the complete workspace manifest and explicitly synthetic catalog data.</summary>
public sealed class WorkspaceLiveOllamaTests
{
    [LiveOllamaFact]
    public async Task Live_model_uses_the_workspace_manifest_and_produces_a_freshly_verified_mapping_citation()
    {
        var data = new WorkspaceToolFixture();
        var tools = new WorkspaceToolService(data.Services, new ChatApiOptions(), new(true, true, true, true), new WorkspaceToolFixture.Guide());
        var observed = new ObservedTools(tools);
        var conversation = data.Conversation();
        var profile = AiProfileSpec.Create(AiProfileKey.Create("live-workspace", 1), AiDialect.OllamaChatNdjsonV1,
            Environment.GetEnvironmentVariable(LiveOllamaFactAttribute.ModelVariable)!,
            new Uri(Environment.GetEnvironmentVariable(LiveOllamaFactAttribute.EndpointVariable)!), true, AiAuthentication.None, null,
            AiCapability.Text | AiCapability.FunctionTools, "live.synthetic-metadata", 1, true, false);
        using var credentials = new Resolver();
        await using var provider = new AiRoundClient(AiProfileCatalog.Create([profile]), credentials, TimeProvider.System);
        var trace = new TraceLog();
        var store = new Ledger(conversation, WorkspaceToolFixture.Actor, profile, trace);
        var budget = new ChatProviderBudget(TimeSpan.FromMinutes(3).Ticks, 3, 1_048_576, 4_194_304, 131_072, 8, 512);
        var turn = new ChatTurnIntent(Guid.NewGuid(), new(Guid.NewGuid(),
            $"Use get_mapping once with exactly {{\"mappingId\":\"{WorkspaceToolFixture.Mapping.MappingId:D}\"}}. Do not add arguments. Then summarize the returned mapping's name and purpose in one sentence, citing the result.",
            [], budget, tools.Grounding, ChatTurnFocus.None), ChatAiBridge.PinProfile("ollama", profile),
            new(256, 262_144, 3), Now.AddMinutes(3));
        var orchestrator = new ChatTurnOrchestrator(store, store, provider, profile, WorkspaceChatGrounding.Instructions,
            tools.Manifest, new Policy(trace), observed, new Clock(), Guid.NewGuid(), new(8, 4096, 65536, 131072),
            workspaceCitationPort: tools, contextPort: tools);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var execution = orchestrator.Begin(WorkspaceToolFixture.Actor, conversation.Key, turn, [], deadline.Token);
        try
        {
            var outcome = await execution.Completion.WaitAsync(deadline.Token);
            var failures = store.Events(turn.TurnId).Where(item => item.TerminalMetadata?.Failure is not null)
                .Select(item => item.TerminalMetadata!.Failure!.Code).Distinct();
            Assert.True(outcome.Outcome == ChatOrchestrationOutcome.Completed,
                $"Live workspace outcome: {outcome.Outcome}; provider failures: {string.Join(", ", failures)}; tool validations: {string.Join(", ", observed.Validations)}.");
            Assert.Contains(store.Events(turn.TurnId), item => item.Kind == ChatEventKind.WorkspaceCitation &&
                ChatMetadataCodec.ReadWorkspaceCitation(item.MetadataJson!).MappingId == WorkspaceToolFixture.Mapping.MappingId);
            Assert.Contains(store.Events(turn.TurnId), item => item.Kind == ChatEventKind.TextDelta && !string.IsNullOrWhiteSpace(item.Text));
            Assert.DoesNotContain(store.Events(turn.TurnId), item => item.Kind == ChatEventKind.DataShared);
        }
        finally { await execution.CancelAsync(); }
    }

    private sealed class ObservedTools(IChatReadOnlyToolPort inner) : IChatReadOnlyToolPort
    {
        internal List<string> Validations { get; } = [];
        public async Task<bool> ValidateAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken cancellationToken)
        {
            var valid = await inner.ValidateAsync(context, intent, cancellationToken);
            // The live check contains only the authored synthetic catalog. Keep malformed
            // arguments bounded so a failure explains a provider/schema mismatch.
            var arguments = intent.Arguments.Json;
            Validations.Add($"{intent.Name}:{valid}" + (valid ? "" : ":" + arguments[..Math.Min(arguments.Length, 256)]));
            return valid;
        }
        public Task<ChatReadOnlyToolResult> ExecuteAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken cancellationToken) =>
            inner.ExecuteAsync(context, intent, cancellationToken);
    }
}
