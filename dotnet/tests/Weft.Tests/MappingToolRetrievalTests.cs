using System.Text.Json;
using Weft.Application.MappingTools;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Tests;

public sealed class MappingToolRetrievalTests
{
    [Fact]
    public async Task Exactly_five_closed_tools_use_the_same_retained_pins_deterministically()
    {
        Assert.Equal(new[] { "get_mapping_summary", "search_mapping", "get_field_lineage", "get_rule_details", "get_validation_issues" },
            MappingToolRegistry.Definitions.Select(item => item.Name).ToArray());
        foreach (var definition in MappingToolRegistry.Definitions)
        {
            using var schema = JsonDocument.Parse(definition.Parameters.Json);
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        }
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var calls = new[]
        {
            ("get_mapping_summary", "{}"), ("search_mapping", """{"query":"field"}"""),
            ("get_field_lineage", """{"fieldId":"field.in"}"""),
            ("get_rule_details", """{"ruleId":"rule.first"}"""), ("get_validation_issues", "{}")
        };
        foreach (var (name, arguments) in calls)
        {
            var first = await fixture.InvokeAsync(service, name, arguments);
            var second = await fixture.InvokeAsync(service, name, arguments);
            Assert.Equal(MappingToolOutcome.Available, first.Outcome);
            Assert.Equal(first.Result.Json, second.Result.Json);
            Assert.Equal(first.RetrievedNodes, second.RetrievedNodes);
            Assert.Equal(MappingCitationOutcome.Valid,
                (await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, first.Citations)).Outcome);
            Assert.InRange(first.Result.ByteCount, 1, 24_576);
            Assert.InRange(first.RetrievedNodes, 1, 1_024);
            Assert.DoesNotContain("privateObject", first.Result.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cacheEntryId", first.Result.Json, StringComparison.Ordinal);
            Assert.DoesNotContain("originalActor", first.Result.Json, StringComparison.Ordinal);
            Assert.DoesNotContain("dependencyIdentity", first.Result.Json, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Projection.Metadata.DerivationId.ToString("D"), first.Result.Json, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(first.Result.Json);
            Assert.Equal("explanationOnly", json.RootElement.GetProperty("evidence").GetString());
            Assert.Equal("weft.mapping-tool-result.v1", json.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal("available", json.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(first.Citations.Count, json.RootElement.GetProperty("citations").GetArrayLength());
        }
        Assert.All(fixture.Reads, read =>
        {
            Assert.Equal(read.Operation == RevisionReportOperation.Projection ? fixture.Projection.Metadata.DerivationId :
                fixture.Validation.Metadata.DerivationId, read.Id);
            Assert.Equal(read.Operation == RevisionReportOperation.Validation ? fixture.Projection.Metadata.DerivationId : (Guid?)null,
                read.ProjectionId);
        });
    }

    [Fact]
    public async Task Port_execution_returns_the_complete_result_and_honest_node_count()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var context = fixture.Context();
        var call = MappingToolFixture.Call("get_rule_details", """{"ruleId":"rule.first"}""");
        Assert.True(await service.ValidateAsync(context, call, CancellationToken.None));
        Assert.Empty(fixture.Reads);
        Assert.Equal(0, fixture.AuthorityCalls);
        var result = await service.ExecuteAsync(context, call, CancellationToken.None);
        var reply = await service.InvokeAsync(context, call);
        Assert.Equal(reply.Result.Json, result.Result.Json);
        Assert.Equal(2, result.RetrievedNodes);
        Assert.Equal(reply.RetrievedNodes, result.RetrievedNodes);
        Assert.Equal(new[] { new ChatCitationTarget(ChatCitationKind.ProjectionReport), new ChatCitationTarget(ChatCitationKind.Rule, "rule.first"),
            new ChatCitationTarget(ChatCitationKind.Control, "control.filter") }, reply.Citations);
        using var json = JsonDocument.Parse(result.Result.Json);
        var citations = json.RootElement.GetProperty("citations");
        Assert.Equal("rule", citations[1].GetProperty("kind").GetString());
        Assert.Equal("rule.first", citations[1].GetProperty("resourceId").GetString());
        Assert.Equal("ruleDetails", json.RootElement.GetProperty("data").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Summary_preserves_coverage_controls_and_capabilities_without_claiming_a_run()
    {
        var fixture = new MappingToolFixture();
        var result = await fixture.InvokeAsync("get_mapping_summary", "{}");
        var data = Assert.IsType<MappingSummaryData>(result.Data);
        Assert.Equal(3, data.FieldCount);
        Assert.Equal(2, data.RuleCount);
        Assert.Equal(ControlKind.Filter, Assert.Single(data.Controls).Kind);
        Assert.Equal(CapabilityStatus.Conditional, Assert.Single(data.Capabilities).Status);
        Assert.Equal(CoverageStatus.Partial, result.Coverage!.Status);
        Assert.Single(result.Coverage.Gaps);
        Assert.Equal(4, result.RetrievedNodes);
        using var json = JsonDocument.Parse(result.Result.Json);
        Assert.Equal("partial", json.RootElement.GetProperty("coverage").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Search_preserves_typed_paths_and_marks_omitted_matches()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var result = await fixture.InvokeAsync(service, "search_mapping", """{"query":"field","limit":1}""");
        var data = Assert.IsType<MappingSearchData>(result.Data);
        Assert.True(data.Truncated);
        var hit = Assert.Single(data.Hits);
        Assert.Equal(new FieldNodeReference("field.in"), hit.Node);
        Assert.Equal(new FieldPathSegment("a.b", "urn:source"), hit.Path![0]);
        Assert.Equal(new AttributePathSegment("id", "urn:attribute"), hit.Path[1]);
        Assert.Equal(new IndexPathSegment("9007199254740993"), hit.Path[2]);
        Assert.Equal(new ChatCitationTarget(ChatCitationKind.Field, "field.in"), result.Citations[1]);
        Assert.Equal(1, result.RetrievedNodes);
        var noMatch = await fixture.InvokeAsync(service, "search_mapping", """{"query":"not-present"}""");
        Assert.Empty(Assert.IsType<MappingSearchData>(noMatch.Data).Hits);
        Assert.False(Assert.IsType<MappingSearchData>(noMatch.Data).Truncated);
        Assert.Equal(CoverageStatus.Partial, noMatch.Coverage!.Status);
        Assert.Equal(0, noMatch.RetrievedNodes);
    }

    [Fact]
    public async Task Rule_details_retain_exact_literals_native_function_identity_and_conditions()
    {
        var fixture = new MappingToolFixture();
        var result = await fixture.InvokeAsync("get_rule_details", """{"ruleId":"rule.first"}""");
        var rule = Assert.IsType<MappingRuleData>(result.Data);
        Assert.Equal("function.one", Assert.IsType<GraphNodeNativeLocator>(rule.Rule.NativeLocator).FunctionId);
        var literals = rule.Rule.Details.OfType<LiteralDetail>().Select(item => item.Value).ToArray();
        Assert.Contains(new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "9007199254740993"), literals);
        Assert.Contains(new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "8000000000000000"), literals);
        Assert.Contains(new NullLiteralValue(NullLiteralKind.Absent), literals);
        Assert.Contains(new NullLiteralValue(NullLiteralKind.ExplicitNull), literals);
        Assert.Contains(new NullLiteralValue(NullLiteralKind.XmlNil), literals);
        Assert.Contains(rule.Rule.Dependencies, item => item is ConstantDependency
        { Role: DependencyRole.Default, Value: ExactNumberLiteralValue { Value: "-0.000E+07" } });
        Assert.Contains(Assert.Single(rule.Controls).Dependencies,
            item => item is FieldDependency { Role: DependencyRole.Condition, FieldId: "field.in" });
        Assert.DoesNotContain(MappingToolFixture.PrivateText, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(MappingToolFixture.PrivateText, rule.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(MappingToolFixture.PrivateText, fixture.Context().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(MappingToolFixture.PrivateText, result.Result.ToString(), StringComparison.Ordinal);
        Assert.Contains(MappingToolFixture.PrivateText, result.Result.Json, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(result.Result.Json);
        var strings = Strings(json.RootElement).ToArray();
        Assert.Contains("9007199254740993", strings);
        Assert.Contains("8000000000000000", strings);
        Assert.Contains("-0.000E+07", strings);
    }

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Strings(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        _ => []
    };

    [Fact]
    public async Task Lineage_separates_value_default_and_control_edges_and_transitive_affected_outputs()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var upstreamResult = await fixture.InvokeAsync(service, "get_field_lineage", """{"fieldId":"field.out"}""");
        var upstream = Assert.IsType<MappingFieldLineageData>(upstreamResult.Data);
        Assert.False(upstream.Truncated);
        Assert.Contains(upstream.UpstreamEdges, edge => edge.Kind == MappingLineageEdgeKind.RuleDependency &&
            edge.Dependency is FieldDependency { Role: DependencyRole.Value, FieldId: "field.in" });
        Assert.Contains(upstream.UpstreamEdges, edge => edge.Kind == MappingLineageEdgeKind.RuleDependency &&
            edge.Dependency is ConstantDependency { Role: DependencyRole.Default });
        Assert.Contains(upstream.UpstreamEdges, edge => edge.Kind == MappingLineageEdgeKind.ControlDependency &&
            edge.Dependency is FieldDependency { Role: DependencyRole.Condition });
        Assert.Contains(upstream.UpstreamEdges, edge => edge.Kind == MappingLineageEdgeKind.ParameterDefault &&
            edge.Dependency is ConstantDependency { Value: StringLiteralValue { Value: "exact fallback" } });
        Assert.Equal(upstream.UpstreamNodes.Union(upstream.DownstreamNodes).Count(), upstreamResult.RetrievedNodes);
        var downstreamResult = await fixture.InvokeAsync(service, "get_field_lineage", """{"fieldId":"field.in"}""");
        var downstream = Assert.IsType<MappingFieldLineageData>(downstreamResult.Data);
        Assert.Equal(new[] { new FieldNodeReference("field.out"), new FieldNodeReference("field.downstream") }, downstream.AffectedOutputs);
        var bounded = await fixture.InvokeAsync(service, "get_field_lineage", """{"fieldId":"field.in","maxNodes":1}""");
        Assert.True(Assert.IsType<MappingFieldLineageData>(bounded.Data).Truncated);
        Assert.Single(Assert.IsType<MappingFieldLineageData>(bounded.Data).DownstreamNodes);
        Assert.Equal(CoverageStatus.Partial, bounded.Coverage!.Status);
        Assert.Equal(1, bounded.RetrievedNodes);
        using var json = JsonDocument.Parse(bounded.Result.Json);
        Assert.True(json.RootElement.GetProperty("data").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Validation_uses_exact_report_ordinals_and_honest_window_and_report_truncation()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var result = await fixture.InvokeAsync(service, "get_validation_issues", """{"offset":1,"limit":1}""");
        var data = Assert.IsType<MappingValidationData>(result.Data);
        Assert.False(data.IsValid);
        Assert.Equal(3, data.TotalIssueCount);
        Assert.Equal(2, data.EmittedIssueCount);
        Assert.True(data.ReportTruncated);
        Assert.True(data.WindowTruncated);
        var issue = Assert.Single(data.Issues);
        Assert.Equal(1, issue.Ordinal);
        Assert.Equal("invalid.second", issue.Diagnostic.Code);
        // The exact conversation projection links the diagnostic's native owner to its projected rule.
        Assert.Equal(new RuleNodeReference("rule.second"), issue.Diagnostic.ProjectionLocator);
        Assert.Null(result.Coverage);
        Assert.Equal(1, result.RetrievedNodes);
        Assert.Equal(new[] { new ChatCitationTarget(ChatCitationKind.ValidationReport),
            new ChatCitationTarget(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 1) }, result.Citations);
        var check = await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, result.Citations);
        Assert.Equal(MappingCitationOutcome.Valid, check.Outcome);
        Assert.All(check.Citations, citation => Assert.Equal(fixture.Conversation.PinnedValidation, citation.Facts!.Report));
        Assert.Equal(new[] { (RevisionReportOperation.Validation, fixture.Validation.Metadata.DerivationId, (Guid?)fixture.Projection.Metadata.DerivationId),
            (RevisionReportOperation.Projection, fixture.Projection.Metadata.DerivationId, (Guid?)null) }, fixture.Reads.Take(2));
        var empty = await fixture.InvokeAsync(service, "get_validation_issues", """{"offset":5}""");
        var window = Assert.IsType<MappingValidationData>(empty.Data);
        Assert.Empty(window.Issues);
        Assert.True(window.WindowTruncated);
        Assert.Equal(0, empty.RetrievedNodes);
    }

    [Theory]
    [InlineData("get_field_lineage", "{\"fieldId\":\"absent\"}")]
    [InlineData("get_rule_details", "{\"ruleId\":\"absent\"}")]
    public async Task Absent_nodes_remain_unknown_with_the_actual_partial_coverage(string name, string args)
    {
        var fixture = new MappingToolFixture();
        var result = await fixture.InvokeAsync(name, args);
        Assert.Equal(MappingToolOutcome.Unknown, result.Outcome);
        Assert.Null(result.Data);
        Assert.Equal(CoverageStatus.Partial, result.Coverage!.Status);
        Assert.Equal(ChatCitationKind.ProjectionReport, Assert.Single(result.Citations).Kind);
        Assert.Equal(0, result.RetrievedNodes);
        using var json = JsonDocument.Parse(result.Result.Json);
        Assert.Equal("unknown", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task Complete_coverage_is_reported_only_when_the_retained_projection_claims_it()
    {
        var fixture = new MappingToolFixture(partial: false);
        var result = await fixture.InvokeAsync("get_mapping_summary", "{}");
        Assert.Equal(CoverageStatus.Complete, result.Coverage!.Status);
        Assert.Empty(result.Coverage.Gaps);
        var failure = await new MappingToolFixture { Missing = true }.InvokeAsync("get_mapping_summary", "{}");
        Assert.Null(failure.Coverage);
        using var json = JsonDocument.Parse(failure.Result.Json);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("coverage").ValueKind);
    }
}
