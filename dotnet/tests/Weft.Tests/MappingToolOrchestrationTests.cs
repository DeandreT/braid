using System.Text.Json;
using Weft.Application;
using Weft.Application.MappingTools;
using Weft.Domain;
using static Weft.Tests.ChatOrchestrationFixture;

namespace Weft.Tests;

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

        // Completing orchestration does not verify citations; final delivery checks them against the same pins.
        var check = await tools.ValidateCitationsAsync(MappingToolFixture.Actor, mapping.Conversation,
            rule.Citations.Concat(issues.Citations).ToArray());
        Assert.Equal(MappingCitationOutcome.Valid, check.Outcome);
        Assert.All(check.Citations, citation => _ = new ChatCitationEventData(Guid.NewGuid(), citation));
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
