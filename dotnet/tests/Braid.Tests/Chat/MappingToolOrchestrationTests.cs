using System.Text.Json;
using Braid.Ai;
using Braid.Application;
using Braid.Application.MappingTools;
using Braid.Domain;
using static Braid.Tests.ChatOrchestrationFixture;

namespace Braid.Tests;

/// <summary>The real mapping tool port registered in the real turn orchestrator over the loopback provider fixture.</summary>
public sealed class MappingToolOrchestrationTests
{
    [Fact]
    public async Task Orchestrated_tool_calls_feed_exact_mapping_results_back_in_provider_order()
    {
        var mapping = new MappingToolFixture();
        var tools = mapping.Service();
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1
                ? Call(0, "call-rule", "{\"ruleId\":\"rule.first\"}", "get_rule_details") +
                    Call(1, "call-issues", "{\"limit\":1}", "get_validation_issues") + Finish("tool_calls") + Done
                : Text("rule.first reads field.in") + Finish("stop") + Done), ct),
            actor: MappingToolFixture.Actor, conversation: mapping.Conversation, tools: MappingToolRegistry.Definitions, toolPort: tools);
        var intent = f.Intent(nodes: 1_024, resultBytes: 32_768);
        Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(f.Begin(intent))).Outcome);

        using var request = f.Server.Requests[1].Json();
        var messages = request.RootElement.GetProperty("messages");
        var advertised = request.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray();
        Assert.Equal(MappingToolRegistry.Definitions.Select(tool => tool.Name), advertised);
        var rule = await mapping.InvokeAsync(tools, "get_rule_details", """{"ruleId":"rule.first"}""");
        var issues = await mapping.InvokeAsync(tools, "get_validation_issues", """{"limit":1}""");
        Assert.Equal("call-rule", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal(rule.Result.Json, messages[3].GetProperty("content").GetString());
        Assert.Equal("call-issues", messages[4].GetProperty("tool_call_id").GetString());
        Assert.Equal(issues.Result.Json, messages[4].GetProperty("content").GetString());

        var completed = f.Store.Events(intent.TurnId).Where(e => e.Kind == ChatEventKind.ToolCompleted).ToArray();
        Assert.Equal(2, completed.Length);
        foreach (var (stored, reply) in completed.Zip(new[] { rule, issues }))
        {
            using var activity = JsonDocument.Parse(stored.Text);
            Assert.Equal(reply.Result.ByteCount, activity.RootElement.GetProperty("resultBytes").GetInt32());
            Assert.Equal(reply.RetrievedNodes, activity.RootElement.GetProperty("retrievedNodes").GetInt32());
            Assert.DoesNotContain(MappingToolFixture.PrivateText, stored.Text, StringComparison.Ordinal);
        }

        // Without a citation port the candidates are never verified, so nothing is published as a citation.
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.Citation);
        var check = await tools.ValidateCitationsAsync(MappingToolFixture.Actor, mapping.Conversation,
            rule.Citations.Concat(issues.Citations).ToArray());
        Assert.Equal(MappingCitationOutcome.Valid, check.Outcome);
        Assert.All(check.Citations, citation => _ = new ChatCitationEventData(Guid.NewGuid(), citation));
    }

    [Fact]
    public async Task Verified_citations_follow_each_tool_completion_once_per_turn_in_candidate_order()
    {
        var mapping = new MappingToolFixture();
        var tools = mapping.Service();
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1
                ? Call(0, "call-rule", "{\"ruleId\":\"rule.first\"}", "get_rule_details") +
                    Call(1, "call-issues", "{\"limit\":1}", "get_validation_issues") +
                    Call(2, "call-again", "{\"ruleId\":\"rule.first\"}", "get_rule_details") + Finish("tool_calls") + Done
                : Text("Cites {\"kind\":\"rule\",\"resourceId\":\"rule.second\"}") + Finish("stop") + Done), ct),
            actor: MappingToolFixture.Actor, conversation: mapping.Conversation, tools: MappingToolRegistry.Definitions,
            toolPort: tools, citationPort: tools);
        var intent = f.Intent(nodes: 1_024, resultBytes: 32_768);
        Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(f.Begin(intent))).Outcome);

        var events = f.Store.Events(intent.TurnId);
        var round = f.Store.Rounds(intent.TurnId)[0].Intent;
        Assert.Equal(new[]
        {
            ChatEventKind.ToolStarted, ChatEventKind.ToolCompleted, ChatEventKind.Citation, ChatEventKind.Citation, ChatEventKind.Citation,
            ChatEventKind.ToolStarted, ChatEventKind.ToolCompleted, ChatEventKind.Citation, ChatEventKind.Citation,
            // The repeated call's candidates were already cited in this turn.
            ChatEventKind.ToolStarted, ChatEventKind.ToolCompleted, ChatEventKind.RoundCompleted
        }, events.Skip(1).TakeWhile(e => e.RoundId == round.RoundId).Select(e => e.Kind));
        var citations = events.Where(e => e.Kind == ChatEventKind.Citation).Select(e => e.Citation!).ToArray();
        Assert.Equal(new ChatCitationTarget[]
        {
            new(ChatCitationKind.ProjectionReport), new(ChatCitationKind.Rule, "rule.first"), new(ChatCitationKind.Control, "control.filter"),
            new(ChatCitationKind.ValidationReport), new(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 0)
        }, citations.Select(c => c.Target));
        Assert.All(citations.Take(3), c => Assert.Equal(mapping.Conversation.PinnedProjection, c.Report));
        Assert.All(citations.Skip(3), c => Assert.Equal(mapping.Conversation.PinnedValidation, c.Report));
        Assert.All(citations, c => Assert.Equal(MappingToolFixture.Scope, c.Revision));
        Assert.All(events.Where(e => e.Kind == ChatEventKind.Citation), e => Assert.Equal(round.RoundId, e.RoundId));
        // Model text naming another rule is answer text only; it is not a citation candidate.
        Assert.DoesNotContain(citations, c => c.Target.ResourceId == "rule.second");
        var facts = f.Store.Facts(intent.TurnId);
        Assert.Equal(events.Length, facts.EventCount);
        Assert.Equal(events.Sum(ChatOrchestrationFixture.Ledger.CountedEventBytes), facts.EventBytes);
        Assert.Equal(2, f.Server.RequestCount);
    }

    [Fact]
    public async Task Unverifiable_candidates_from_the_tool_port_are_dropped_and_genuine_ones_still_published()
    {
        var mapping = new MappingToolFixture();
        var tools = mapping.Service();
        var forging = new ForgingPort(tools, [new(ChatCitationKind.Rule, "foreign.rule"), new(ChatCitationKind.Field, "RULE.FIRST"),
            new(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 2), new(ChatCitationKind.Rule, "rule.first")]);
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Call(0, "call-summary", "{}", "get_mapping_summary") + Finish("tool_calls") + Done
                : Text("done") + Finish("stop") + Done), ct),
            actor: MappingToolFixture.Actor, conversation: mapping.Conversation, tools: MappingToolRegistry.Definitions,
            toolPort: forging, citationPort: tools);
        var intent = f.Intent(nodes: 1_024, resultBytes: 32_768);
        Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(f.Begin(intent))).Outcome);
        Assert.Equal(new ChatCitationTarget[] { new(ChatCitationKind.ProjectionReport), new(ChatCitationKind.Rule, "rule.first") },
            f.Store.Events(intent.TurnId).Where(e => e.Kind == ChatEventKind.Citation).Select(e => e.Citation!.Target));
    }

    [Fact]
    public async Task A_citation_port_reply_outside_the_candidates_or_pins_is_never_published()
    {
        var mapping = new MappingToolFixture();
        var tools = mapping.Service();
        // A different report body for the same revision: a citation verified against it carries another pin.
        var other = new MappingToolFixture(partial: false);
        Assert.NotEqual(mapping.Conversation.PinnedProjection, other.Conversation.PinnedProjection);
        // A trusted port must answer only for the offered candidates of this conversation; anything else is omitted.
        var substituted = new SubstitutingCitations(async (actor, conversation, candidates, ct) =>
        {
            var genuine = await tools.FilterCitationsAsync(actor, conversation, candidates, ct);
            var unrequested = await tools.ValidateCitationsAsync(actor, conversation, [new ChatCitationTarget(ChatCitationKind.Rule, "rule.second")], ct);
            return [.. unrequested.Citations, .. genuine.Citations.Where(c => c.Facts!.Target.Kind != ChatCitationKind.ProjectionReport),
                ChatValidatedCitation.Validate(other.Conversation, other.Projection, new(ChatCitationKind.ProjectionReport))];
        });
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Call(0, "call-rule", "{\"ruleId\":\"rule.first\"}", "get_rule_details") + Finish("tool_calls") + Done
                : Text("done") + Finish("stop") + Done), ct),
            actor: MappingToolFixture.Actor, conversation: mapping.Conversation, tools: MappingToolRegistry.Definitions,
            toolPort: tools, citationPort: substituted);
        var intent = f.Intent(nodes: 1_024, resultBytes: 32_768);
        Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(f.Begin(intent))).Outcome);
        Assert.Equal(new ChatCitationTarget[] { new(ChatCitationKind.Rule, "rule.first"), new(ChatCitationKind.Control, "control.filter") },
            f.Store.Events(intent.TurnId).Where(e => e.Kind == ChatEventKind.Citation).Select(e => e.Citation!.Target));
    }

    [Fact]
    public async Task Revocation_before_citation_verification_publishes_no_citation_and_stops_the_turn()
    {
        var mapping = new MappingToolFixture();
        var tools = mapping.Service();
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Call(0, "call-summary", "{}", "get_mapping_summary") + Finish("tool_calls") + Done
                : Text("unexpected") + Finish("stop") + Done), ct),
            actor: MappingToolFixture.Actor, conversation: mapping.Conversation, tools: MappingToolRegistry.Definitions,
            toolPort: tools, citationPort: tools);
        // Tool execution authorizes twice; the citation check's first authority read observes the revocation.
        mapping.OnAuthority = call => { if (call == 3) { mapping.Role = null; f.Store.Authorized = false; } };
        var intent = f.Intent(nodes: 1_024, resultBytes: 32_768);
        // The ledger also refuses the revoked creator's terminal write, so the outcome is not an acknowledged phase.
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, (await FinishExecution(f.Begin(intent))).Outcome);
        Assert.Equal(3, mapping.AuthorityCalls);
        Assert.Contains(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.ToolCompleted);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.Citation);
        Assert.Equal(0, f.Store.Count("append-citation"));
        Assert.Equal(1, f.Server.RequestCount);
    }

    private sealed class ForgingPort(IChatReadOnlyToolPort inner, IReadOnlyList<ChatCitationTarget> forged) : IChatReadOnlyToolPort
    {
        public Task<bool> ValidateAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct) => inner.ValidateAsync(context, intent, ct);
        public async Task<ChatReadOnlyToolResult> ExecuteAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct)
        {
            var result = await inner.ExecuteAsync(context, intent, ct);
            return new(result.Result, result.RetrievedNodes, result.Citations.Concat(forged));
        }
    }

    private sealed class SubstitutingCitations(
        Func<ActorContext, ChatConversationIntent, IReadOnlyList<ChatCitationTarget>, CancellationToken, Task<IReadOnlyList<ChatValidatedCitation>>> reply)
        : IChatCitationPort
    {
        public Task<IReadOnlyList<ChatValidatedCitation>> VerifyAsync(ActorContext actor, ChatConversationIntent conversation,
            IReadOnlyList<ChatCitationTarget> candidates, CancellationToken ct) => reply(actor, conversation, candidates, ct);
    }

    [Fact]
    public async Task A_malformed_mapping_call_fails_the_batch_before_any_authority_or_report_read()
    {
        var mapping = new MappingToolFixture();
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            Call(0, "call-summary", "{}", "get_mapping_summary") +
            Call(1, "call-forged", "{\"ruleId\":\"rule.first\",\"organizationId\":\"x\"}", "get_rule_details") + Finish("tool_calls") + Done), ct),
            actor: MappingToolFixture.Actor, conversation: mapping.Conversation, tools: MappingToolRegistry.Definitions, toolPort: mapping.Service());
        var intent = f.Intent(nodes: 1_024, resultBytes: 32_768);
        Assert.Equal(ChatOrchestrationOutcome.InvalidRequest, (await FinishExecution(f.Begin(intent))).Outcome);
        Assert.Equal(0, mapping.AuthorityCalls);
        Assert.Empty(mapping.Reads);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind is ChatEventKind.ToolStarted or ChatEventKind.ToolCompleted);
        Assert.Equal(1, f.Server.RequestCount);
    }

    [Fact]
    public async Task A_revoked_reader_gets_a_categorical_result_and_the_turn_stops_without_another_provider_round()
    {
        var mapping = new MappingToolFixture();
        var tools = mapping.Service();
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Call(0, "call-summary", "{}", "get_mapping_summary") + Finish("tool_calls") + Done
                : Text("unexpected") + Finish("stop") + Done), ct),
            actor: MappingToolFixture.Actor, conversation: mapping.Conversation, tools: MappingToolRegistry.Definitions, toolPort: tools);
        // Membership revocation during the read removes both report authority and conversation access.
        mapping.OnRead = _ => { mapping.Role = null; f.Store.Authorized = false; };
        var intent = f.Intent(nodes: 1_024, resultBytes: 32_768);
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, (await FinishExecution(f.Begin(intent))).Outcome);
        Assert.Equal(1, f.Server.RequestCount);
        Assert.Single(mapping.Reads);
        Assert.Equal(1, mapping.AuthorityCalls);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.ToolCompleted);
    }
}
