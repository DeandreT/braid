using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Application.MappingTools;

public sealed partial class MappingToolService
{
    private MappingToolReply Projection(VerifiedProjectionReport verified, ParsedMappingToolCall call,
        MappingToolWork work, ResultBudget budget, CancellationToken token)
    {
        var document = verified.Report.Document ?? throw new MappingToolRefusal(MappingToolOutcome.ReportUnavailable);
        var citations = new List<ChatCitationTarget> { new(ChatCitationKind.ProjectionReport) };
        MappingToolData? data;
        int nodes;
        switch (call.Name)
        {
            case "get_mapping_summary":
                nodes = document.Boundaries.Count + document.Controls.Count + document.Capabilities.Count +
                    document.Diagnostics.Count + verified.Report.Diagnostics.Count;
                work.Charge(nodes);
                data = new MappingSummaryData(document.Boundaries, document.Controls, document.Capabilities,
                    document.Diagnostics, verified.Report.Diagnostics, document.Fields.Count, document.Rules.Count, document.Parameters.Count);
                break;
            case "search_mapping":
                var search = Search(document, call, citations, work);
                (data, nodes) = (search, search.Hits.Count);
                break;
            case "get_rule_details":
                var rule = RuleDetails(document, call.Text!, citations, work);
                (data, nodes) = (rule, rule is null ? 0 : 1 + rule.Controls.Count);
                break;
            case "get_field_lineage":
                var lineage = FieldLineage(document, call, citations, work);
                (data, nodes) = (lineage, lineage is null ? 0 : lineage.UpstreamNodes.Union(lineage.DownstreamNodes).Count());
                break;
            default: throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
        }
        return Prepare(data is null ? MappingToolOutcome.Unknown : MappingToolOutcome.Available,
            document.Coverage, data, Array.AsReadOnly(citations.ToArray()), nodes, budget, token);
    }

    private static ChatCitationTarget Cite(ProjectionNodeReference node) => node switch
    {
        BoundaryNodeReference item => new(ChatCitationKind.Boundary, item.BoundaryId),
        FieldNodeReference item => new(ChatCitationKind.Field, item.FieldId),
        ParameterNodeReference item => new(ChatCitationKind.Parameter, item.ParameterId),
        RuleNodeReference item => new(ChatCitationKind.Rule, item.RuleId),
        ControlNodeReference item => new(ChatCitationKind.Control, item.ControlId),
        _ => throw new MappingToolRefusal(MappingToolOutcome.DependencyFailure)
    };

    private static MappingSearchData Search(ProjectionDocumentV1 document, ParsedMappingToolCall call,
        List<ChatCitationTarget> citations, MappingToolWork work)
    {
        var hits = new List<MappingSearchHit>();
        var truncated = false;
        foreach (var field in document.Fields)
        {
            work.Charge();
            var found = work.Contains(field.Id, call.Text!) || work.Contains(field.Name, call.Text!);
            foreach (var segment in field.Path)
            {
                work.Charge();
                found |= segment switch
                {
                    FieldPathSegment item => work.Contains(item.Name, call.Text!) ||
                        item.NamespaceUri is { } ns && work.Contains(ns, call.Text!),
                    AttributePathSegment item => work.Contains(item.Name, call.Text!) ||
                        item.NamespaceUri is { } ns && work.Contains(ns, call.Text!),
                    IndexPathSegment item => work.Contains(item.Index, call.Text!),
                    AnyItemPathSegment => false,
                    _ => throw new MappingToolRefusal(MappingToolOutcome.DependencyFailure)
                };
            }
            if (!found) continue;
            if (hits.Count == call.Limit) { truncated = true; break; }
            var node = new FieldNodeReference(field.Id);
            hits.Add(new(node, field.Name, field.Path, field.NativeLocator));
            citations.Add(Cite(node));
        }
        if (!truncated)
        {
            foreach (var rule in document.Rules)
            {
                work.Charge();
                if (!work.Contains(rule.Id, call.Text!) && !work.Contains(rule.Summary, call.Text!)) continue;
                if (hits.Count == call.Limit) { truncated = true; break; }
                var node = new RuleNodeReference(rule.Id);
                hits.Add(new(node, rule.Summary, null, rule.NativeLocator));
                citations.Add(Cite(node));
            }
        }
        return new(Array.AsReadOnly(hits.ToArray()), truncated);
    }

    private static MappingRuleData? RuleDetails(ProjectionDocumentV1 document, string ruleId,
        List<ChatCitationTarget> citations, MappingToolWork work)
    {
        ProjectionRule? selected = null;
        foreach (var rule in document.Rules)
        {
            work.Charge(rule.Id.Length + 1);
            if (StringComparer.Ordinal.Equals(rule.Id, ruleId)) { selected = rule; break; }
        }
        if (selected is null) return null;
        var node = new RuleNodeReference(selected.Id);
        citations.Add(Cite(node));
        var graph = MappingLineageGraph.Create(document, work);
        var reachable = graph.Walk(node, false, int.MaxValue, work, controlsOnly: true);
        var outputControls = graph.Walk(new FieldNodeReference(selected.OutputFieldId), false, int.MaxValue, work, controlsOnly: true);
        var controls = new List<ProjectionControl>();
        foreach (var control in document.Controls)
        {
            work.Charge(control.Id.Length + 1);
            var controlNode = new ControlNodeReference(control.Id);
            if (!reachable.Nodes.Contains(controlNode) && !outputControls.Nodes.Contains(controlNode)) continue;
            controls.Add(control);
            citations.Add(Cite(controlNode));
        }
        return new(selected, Array.AsReadOnly(controls.ToArray()));
    }

    private static MappingFieldLineageData? FieldLineage(ProjectionDocumentV1 document, ParsedMappingToolCall call,
        List<ChatCitationTarget> citations, MappingToolWork work)
    {
        ProjectionField? selected = null;
        foreach (var field in document.Fields)
        {
            work.Charge(field.Id.Length + 1);
            if (StringComparer.Ordinal.Equals(field.Id, call.Text)) { selected = field; break; }
        }
        if (selected is null) return null;
        var node = new FieldNodeReference(selected.Id);
        citations.Add(Cite(node));
        var graph = MappingLineageGraph.Create(document, work);
        var upstream = graph.Walk(node, false, call.MaxNodes, work);
        var downstream = graph.Walk(node, true, call.MaxNodes, work);
        var outputBoundaries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var boundary in document.Boundaries)
        {
            work.Charge(boundary.Id.Length + 1);
            if (boundary.Role is BoundaryRole.NamedOutput or BoundaryRole.PrimaryOutput) outputBoundaries.Add(boundary.Id);
        }
        var affected = new List<FieldNodeReference>();
        foreach (var field in document.Fields)
        {
            work.Charge(field.Id.Length + field.BoundaryId.Length + 1);
            var reference = new FieldNodeReference(field.Id);
            if (reference != node && outputBoundaries.Contains(field.BoundaryId) && downstream.Nodes.Contains(reference))
                affected.Add(reference);
        }
        return new(selected, upstream.OrderedNodes, upstream.Edges, downstream.OrderedNodes, downstream.Edges,
            Array.AsReadOnly(affected.ToArray()), upstream.Truncated || downstream.Truncated);
    }

    private MappingToolReply Validation(VerifiedValidationReport verified, ParsedMappingToolCall call,
        MappingToolWork work, ResultBudget budget, CancellationToken token)
    {
        var report = verified.Report;
        var citations = new List<ChatCitationTarget> { new(ChatCitationKind.ValidationReport) };
        var issues = new List<MappingValidationIssue>();
        var count = report.Diagnostics.Count;
        var end = (int)Math.Min(count, (long)call.Offset + call.Limit);
        for (var index = call.Offset; index < end; index++)
        {
            work.Charge();
            if (index > 32_767) throw new MappingToolRefusal(MappingToolOutcome.ResourceLimit);
            issues.Add(new(index, report.Diagnostics[index]));
            citations.Add(new(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: index));
        }
        var data = new MappingValidationData(report.Status, report.IsValid, report.TotalIssueCount,
            report.EmittedIssueCount, report.Truncated, call.Offset, call.Offset > 0 && count > 0 || end < count,
            Array.AsReadOnly(issues.ToArray()));
        return Prepare(MappingToolOutcome.Available, null, data, Array.AsReadOnly(citations.ToArray()), issues.Count, budget, token);
    }
}
