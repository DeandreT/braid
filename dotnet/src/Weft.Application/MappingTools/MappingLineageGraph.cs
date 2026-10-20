using Weft.Contracts.Projections;

namespace Weft.Application.MappingTools;

/// <summary>Conservative structural reachability, not an expression evaluator or claim of runtime influence.</summary>
internal sealed class MappingLineageGraph
{
    private readonly Dictionary<ProjectionNodeReference, List<MappingLineageEdge>> upstream = [];
    private readonly Dictionary<ProjectionNodeReference, List<MappingLineageEdge>> downstream = [];

    internal static MappingLineageGraph Create(ProjectionDocumentV1 document, MappingToolWork work)
    {
        var graph = new MappingLineageGraph();
        foreach (var rule in document.Rules)
        {
            work.Charge(rule.Id.Length + rule.OutputFieldId.Length + 1);
            var reference = new RuleNodeReference(rule.Id);
            graph.Add(new(new FieldNodeReference(rule.OutputFieldId), reference, MappingLineageEdgeKind.ProducedBy), work);
            foreach (var dependency in rule.Dependencies)
                graph.Add(new(reference, Target(dependency), MappingLineageEdgeKind.RuleDependency, dependency), work);
        }
        foreach (var parameter in document.Parameters)
        {
            work.Charge(parameter.Id.Length + 1);
            if (parameter.DefaultValue is { } value)
                graph.Add(new(new ParameterNodeReference(parameter.Id), null, MappingLineageEdgeKind.ParameterDefault,
                    new ConstantDependency(value, DependencyRole.Default)), work);
        }
        foreach (var control in document.Controls)
        {
            work.Charge(control.Id.Length + 1);
            var reference = new ControlNodeReference(control.Id);
            foreach (var target in control.AppliesTo)
                graph.Add(new(target, reference, MappingLineageEdgeKind.GovernedBy), work);
            foreach (var dependency in control.Dependencies)
                graph.Add(new(reference, Target(dependency), MappingLineageEdgeKind.ControlDependency, dependency), work);
        }
        foreach (var relationship in document.Relationships)
        {
            work.Charge(relationship.Id.Length + 1);
            switch (relationship.Kind)
            {
                case RelationshipKind.ReadsFrom:
                case RelationshipKind.DependsOn:
                case RelationshipKind.GovernedBy:
                    graph.Add(new(relationship.From, relationship.To, MappingLineageEdgeKind.Relationship,
                        Relationship: relationship), work);
                    break;
                case RelationshipKind.WritesTo:
                case RelationshipKind.Produces:
                    graph.Add(new(relationship.To, relationship.From, MappingLineageEdgeKind.Relationship,
                        Relationship: relationship), work);
                    break;
                case RelationshipKind.BoundaryMember:
                    break; // Membership alone is not a value/control dependency.
                default: throw new MappingToolRefusal(MappingToolOutcome.DependencyFailure);
            }
        }
        return graph;
    }

    private static ProjectionNodeReference? Target(RuleDependency dependency) => dependency switch
    {
        FieldDependency field => new FieldNodeReference(field.FieldId),
        ParameterDependency parameter => new ParameterNodeReference(parameter.ParameterId),
        NodeDependency node => node.Node,
        ConstantDependency => null,
        _ => throw new MappingToolRefusal(MappingToolOutcome.DependencyFailure)
    };

    private void Add(MappingLineageEdge edge, MappingToolWork work)
    {
        ChargeNode(edge.From, work);
        if (edge.To is { } target) ChargeNode(target, work);
        if (!upstream.TryGetValue(edge.From, out var forward)) upstream[edge.From] = forward = [];
        forward.Add(edge);
        if (edge.To is not { } to) return;
        if (!downstream.TryGetValue(to, out var reverse)) downstream[to] = reverse = [];
        reverse.Add(edge);
    }

    private static void ChargeNode(ProjectionNodeReference node, MappingToolWork work)
    {
        var id = node switch
        {
            BoundaryNodeReference item => item.BoundaryId,
            FieldNodeReference item => item.FieldId,
            ParameterNodeReference item => item.ParameterId,
            RuleNodeReference item => item.RuleId,
            ControlNodeReference item => item.ControlId,
            _ => throw new MappingToolRefusal(MappingToolOutcome.DependencyFailure)
        };
        work.Charge(id.Length + 1);
    }

    internal MappingLineageWalk Walk(ProjectionNodeReference root, bool reverse, int maximumNodes, MappingToolWork work,
        bool controlsOnly = false)
    {
        var index = reverse ? downstream : upstream;
        var seen = new HashSet<ProjectionNodeReference> { root };
        var nodes = new List<ProjectionNodeReference> { root };
        var edges = new List<MappingLineageEdge>();
        var truncated = false;
        for (var offset = 0; offset < nodes.Count; offset++)
        {
            ChargeNode(nodes[offset], work);
            if (!index.TryGetValue(nodes[offset], out var adjacent)) continue;
            foreach (var edge in adjacent)
            {
                work.Charge();
                if (controlsOnly && edge.Kind != MappingLineageEdgeKind.GovernedBy &&
                    edge.Relationship?.Kind != RelationshipKind.GovernedBy) continue;
                var next = reverse ? edge.From : edge.To;
                if (next is not null && !seen.Contains(next))
                {
                    if (seen.Count == maximumNodes) { truncated = true; continue; }
                    seen.Add(next);
                    nodes.Add(next);
                }
                edges.Add(edge);
            }
        }
        return new(seen, Array.AsReadOnly(nodes.ToArray()), Array.AsReadOnly(edges.ToArray()), truncated);
    }
}

internal sealed record MappingLineageWalk(HashSet<ProjectionNodeReference> Nodes,
    IReadOnlyList<ProjectionNodeReference> OrderedNodes, IReadOnlyList<MappingLineageEdge> Edges, bool Truncated)
{
    public override string ToString() => nameof(MappingLineageWalk);
}
