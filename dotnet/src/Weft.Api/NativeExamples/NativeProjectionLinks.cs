using Weft.Contracts.Projections;
using Weft.MappingBackends;

namespace Weft.Api;

/// <summary>Navigation uses emitted native evidence; it does not infer native paths or parse expressions.</summary>
internal sealed class NativeProjectionLinks
{
    private readonly Dictionary<NativeLocator, HashSet<ProjectionNodeReference>> _origins = [];
    private int _items;

    public NativeProjectionLinks(ProjectionDocumentV1 document)
    {
        foreach (var boundary in document.Boundaries) Add(boundary.NativeLocator, new BoundaryNodeReference(boundary.Id));
        foreach (var field in document.Fields) Add(field.NativeLocator, new FieldNodeReference(field.Id));
        foreach (var parameter in document.Parameters) Add(parameter.NativeLocator, new ParameterNodeReference(parameter.Id));
        foreach (var rule in document.Rules)
        {
            var owner = new RuleNodeReference(rule.Id);
            Add(rule.NativeLocator, owner);
            Details(rule.Details, owner);
        }
        foreach (var control in document.Controls)
        {
            var owner = new ControlNodeReference(control.Id);
            Add(control.NativeLocator, owner);
            Details(control.Details, owner);
        }
    }

    public ProjectionNodeReference? Find(NativeLocator? native)
        => native is not null && _origins.TryGetValue(native, out var owners) && owners.Count == 1 ? owners.Single() : null;

    private void Add(NativeLocator native, ProjectionNodeReference owner)
    {
        Charge();
        Origin(native, owner);
    }

    private void Origin(NativeLocator native, ProjectionNodeReference owner)
    {
        if (native is DocumentNativeLocator or JsonPointerNativeLocator { Pointer.Length: 0 }) return;
        if (!_origins.TryGetValue(native, out var owners)) _origins.Add(native, owners = []);
        owners.Add(owner);
    }

    private void Details(IReadOnlyList<DetailNode> details, ProjectionNodeReference owner, int depth = 0)
    {
        if (details.Count > InspectionPolicy.MaximumItems || details.Count > 0 && depth > InspectionPolicy.MaximumProjectionDetailDepth)
            throw new NativeExampleLimitException();
        foreach (var detail in details)
        {
            Charge();
            switch (detail)
            {
                case GroupDetail group: Details(group.Children, owner, depth + 1); break;
                case ExpressionDetail expression: Details(expression.Arguments, owner, depth + 1); break;
                case UnsupportedDetail unsupported: Origin(unsupported.NativeLocator, owner); break;
            }
        }
    }

    private void Charge()
    {
        if (++_items > InspectionPolicy.MaximumItems) throw new NativeExampleLimitException();
    }
}

internal sealed class NativeExampleLimitException : Exception { }
