using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// An owned index of explicitly emitted native origins. This index does not verify report provenance
/// or infer native locations from display text, expressions, or broad document evidence.
/// </summary>
public sealed class ProjectionOriginLinks
{
    private readonly Dictionary<NativeLocator, ProjectionNodeReference?> _origins;
    private readonly RevisionReportPolicy _limits;

    private ProjectionOriginLinks(Dictionary<NativeLocator, ProjectionNodeReference?> origins, RevisionReportPolicy limits)
    {
        _origins = origins;
        _limits = limits;
    }

    public static ProjectionOriginLinks Create(ProjectionDocumentV1 document, RevisionReportPolicy limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(limits);
        var builder = new Builder(limits, cancellationToken);
        builder.Build(document);
        cancellationToken.ThrowIfCancellationRequested();
        return new(builder.Origins, limits);
    }

    /// <summary>Uses the actual accepted persisted report; it does not manufacture persistent metadata for examples.</summary>
    public static ProjectionOriginLinks FromVerifiedReport(VerifiedProjectionReport report, RevisionReportPolicy limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        var caps = RevisionReportWorkflowValue.EffectiveReadCaps(report.Metadata.Key, limits);
        var document = report.Report.Document ?? throw RevisionReportValidationBudget.Integrity();
        return Create(document, caps, cancellationToken);
    }

    public ProjectionNodeReference? Find(NativeLocator? native, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (native is null) return null;
        // Bound even a lookup key before hashing its exact text.
        new RevisionReportValidationBudget(_limits, cancellationToken).Locator(native);
        var owner = _origins.GetValueOrDefault(native);
        cancellationToken.ThrowIfCancellationRequested();
        return owner;
    }

    private sealed class Builder(RevisionReportPolicy limits, CancellationToken cancellationToken)
    {
        private readonly RevisionReportValidationBudget _budget = new(limits, cancellationToken);
        internal Dictionary<NativeLocator, ProjectionNodeReference?> Origins { get; } = [];

        internal void Build(ProjectionDocumentV1 document)
        {
            Visit(document.Boundaries, boundary =>
            {
                _budget.Required(boundary);
                Origin(boundary.NativeLocator, new BoundaryNodeReference(boundary.Id));
            });
            Visit(document.Fields, field =>
            {
                _budget.Required(field);
                Origin(field.NativeLocator, new FieldNodeReference(field.Id));
            });
            Visit(document.Parameters, parameter =>
            {
                _budget.Required(parameter);
                Origin(parameter.NativeLocator, new ParameterNodeReference(parameter.Id));
            });
            Visit(document.Rules, rule =>
            {
                _budget.Required(rule);
                var owner = new RuleNodeReference(rule.Id);
                Origin(rule.NativeLocator, owner);
                Details(rule.Details, owner, 0);
            });
            Visit(document.Controls, control =>
            {
                _budget.Required(control);
                var owner = new ControlNodeReference(control.Id);
                Origin(control.NativeLocator, owner);
                Details(control.Details, owner, 0);
            });
        }

        private void Origin(NativeLocator native, ProjectionNodeReference owner)
        {
            _budget.Locator(native);
            _budget.ReferenceId(owner);
            if (native is DocumentNativeLocator or JsonPointerNativeLocator { Pointer.Length: 0 }) return;

            // Closed locator/reference variants contain immutable scalar text only. Retain new records,
            // never any caller list or nested detail object.
            NativeLocator ownedNative = native switch
            {
                JsonPointerNativeLocator pointer => new JsonPointerNativeLocator(pointer.DocumentId, pointer.Pointer),
                GraphNodeNativeLocator graph => new GraphNodeNativeLocator(graph.DocumentId, graph.NodeId, graph.FunctionId),
                TextSpanNativeLocator span => new TextSpanNativeLocator(span.DocumentId, span.StartOffset, span.Length, span.Unit),
                OpaqueNativeLocator opaque => new OpaqueNativeLocator(opaque.DocumentId, opaque.Scheme, opaque.Value),
                _ => throw RevisionReportValidationBudget.Integrity()
            };
            ProjectionNodeReference ownedOwner = owner switch
            {
                BoundaryNodeReference boundary => new BoundaryNodeReference(boundary.BoundaryId),
                FieldNodeReference field => new FieldNodeReference(field.FieldId),
                ParameterNodeReference parameter => new ParameterNodeReference(parameter.ParameterId),
                RuleNodeReference rule => new RuleNodeReference(rule.RuleId),
                ControlNodeReference control => new ControlNodeReference(control.ControlId),
                _ => throw RevisionReportValidationBudget.Integrity()
            };
            _budget.Check();
            if (!Origins.TryGetValue(ownedNative, out var existing)) Origins.Add(ownedNative, ownedOwner);
            else if (existing != ownedOwner) Origins[ownedNative] = null;
        }

        private void Details(IReadOnlyList<DetailNode> details, ProjectionNodeReference owner, int depth)
        {
            Visit(details, detail =>
            {
                _budget.Required(detail);
                if (depth > limits.DetailDepthLimit) throw RevisionReportValidationBudget.Limit();
                switch (detail)
                {
                    case GroupDetail group: Details(group.Children, owner, depth + 1); break;
                    case ExpressionDetail expression: Details(expression.Arguments, owner, depth + 1); break;
                    case UnsupportedDetail unsupported: Origin(unsupported.NativeLocator, owner); break;
                    case LiteralDetail or ReferenceDetail: break;
                    default: throw RevisionReportValidationBudget.Integrity();
                }
            });
        }

        private void Visit<T>(IReadOnlyList<T> values, Action<T> visit)
        {
            _budget.Required(values);
            int count;
            try { count = _budget.Array(values); }
            catch (OperationCanceledException) { throw; }
            catch (RevisionReportValidationException) { throw; }
            catch (Exception) { throw RevisionReportValidationBudget.Integrity(); }
            for (var index = 0; index < count; index++)
            {
                _budget.Check();
                T item;
                try
                {
                    if (values.Count != count) throw RevisionReportValidationBudget.Integrity();
                    _budget.Check();
                    item = values[index];
                }
                catch (OperationCanceledException) { throw; }
                catch (RevisionReportValidationException) { throw; }
                catch (Exception) { throw RevisionReportValidationBudget.Integrity(); }
                _budget.Check();
                visit(item);
            }
            _budget.Check();
            try
            {
                if (values.Count != count) throw RevisionReportValidationBudget.Integrity();
            }
            catch (OperationCanceledException) { throw; }
            catch (RevisionReportValidationException) { throw; }
            catch (Exception) { throw RevisionReportValidationBudget.Integrity(); }
        }
    }
}
