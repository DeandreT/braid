using Weft.Contracts.Projections;

namespace Weft.Application;

public static partial class RevisionReportProjectionValidation
{
    private sealed class GraphContext(RevisionReportValidationBudget budget, ProjectionDocumentV1 document)
    {
        private readonly Dictionary<string, ProjectionNodeReference> nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProjectionBoundary> boundaries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProjectionField> fields = new(StringComparer.Ordinal);
        private readonly Dictionary<NativeLocator, HashSet<ProjectionNodeReference>> origins = [];
        private readonly HashSet<(RelationshipKind Kind, ProjectionNodeReference From, ProjectionNodeReference To)> expected = [];

        private static void Require(bool condition) => RevisionReportValidationBudget.Require(condition);

        internal RevisionReportProjectionSummary Validate(int operationDiagnosticCount)
        {
            budget.Required(document.Coverage);
            budget.Known(document.Coverage.Status);
            var gapCount = budget.Array(document.Coverage.Gaps, budget.Caps.DiagnosticLimit);
            var boundaryCount = budget.Array(document.Boundaries);
            var fieldCount = budget.Array(document.Fields);
            var parameterCount = budget.Array(document.Parameters);
            var ruleCount = budget.Array(document.Rules);
            var controlCount = budget.Array(document.Controls);
            var relationshipCount = budget.Array(document.Relationships);
            var diagnosticCount = budget.Array(document.Diagnostics, budget.Caps.DiagnosticLimit);
            var capabilityCount = budget.Array(document.Capabilities);
            Require(document.Coverage.Status == CoverageStatus.Complete ? gapCount == 0 : gapCount > 0);

            for (var index = 0; index < boundaryCount; index++)
            {
                budget.Check();
                var boundary = budget.Required(document.Boundaries[index]);
                budget.Known(boundary.Role); budget.Known(boundary.Shape);
                Add(boundary.Id, new BoundaryNodeReference(boundary.Id), boundary.NativeLocator);
                boundaries.Add(boundary.Id, boundary);
                budget.Text(boundary.Name); budget.Text(boundary.Format, false);
            }
            for (var index = 0; index < fieldCount; index++)
            {
                budget.Check();
                var field = budget.Required(document.Fields[index]);
                budget.Known(field.Requirement); budget.Known(field.Nullability);
                var reference = new FieldNodeReference(field.Id);
                Add(field.Id, reference, field.NativeLocator);
                fields.Add(field.Id, field);
                Reference(new BoundaryNodeReference(field.BoundaryId));
                budget.Text(field.Name); Path(field.Path); Type(field.DataType); Cardinality(field.Cardinality);
                expected.Add((RelationshipKind.BoundaryMember, reference, new BoundaryNodeReference(field.BoundaryId)));
            }
            for (var index = 0; index < boundaryCount; index++)
            {
                budget.Check();
                var boundary = document.Boundaries[index];
                if (boundary.RootFieldId is not { } root) continue;
                budget.Text(root, false);
                Require(fields.TryGetValue(root, out var field) && field.BoundaryId == boundary.Id && field.Path.Count == 0);
            }
            for (var index = 0; index < parameterCount; index++)
            {
                budget.Check();
                var parameter = budget.Required(document.Parameters[index]);
                budget.Known(parameter.Requirement);
                Add(parameter.Id, new ParameterNodeReference(parameter.Id), parameter.NativeLocator);
                budget.Text(parameter.Name); Type(parameter.DataType);
                if (parameter.DefaultValue is not null) budget.Literal(parameter.DefaultValue);
            }
            for (var index = 0; index < ruleCount; index++)
            {
                budget.Check();
                var rule = budget.Required(document.Rules[index]);
                Add(rule.Id, new RuleNodeReference(rule.Id), rule.NativeLocator);
            }
            for (var index = 0; index < controlCount; index++)
            {
                budget.Check();
                var control = budget.Required(document.Controls[index]);
                budget.Known(control.Kind);
                Add(control.Id, new ControlNodeReference(control.Id), control.NativeLocator);
            }
            for (var index = 0; index < ruleCount; index++)
            {
                budget.Check();
                var rule = document.Rules[index];
                var reference = new RuleNodeReference(rule.Id);
                budget.Text(rule.Summary, false); budget.Text(rule.OutputFieldId, false);
                Require(fields.TryGetValue(rule.OutputFieldId, out var output) &&
                    boundaries[output.BoundaryId].Role is BoundaryRole.PrimaryOutput or BoundaryRole.NamedOutput);
                expected.Add((RelationshipKind.WritesTo, reference, new FieldNodeReference(rule.OutputFieldId)));
                Dependencies(reference, rule.Dependencies); Details(reference, rule.Details);
            }
            for (var index = 0; index < controlCount; index++)
            {
                budget.Check();
                var control = document.Controls[index];
                var reference = new ControlNodeReference(control.Id);
                budget.Text(control.Summary, false);
                var count = budget.Array(control.AppliesTo);
                for (var targetIndex = 0; targetIndex < count; targetIndex++)
                {
                    budget.Check();
                    var target = control.AppliesTo[targetIndex];
                    Reference(target);
                    expected.Add((RelationshipKind.GovernedBy, target, reference));
                }
                Dependencies(reference, control.Dependencies); Details(reference, control.Details);
            }
            Relationships(relationshipCount);
            for (var index = 0; index < gapCount; index++)
            {
                budget.Check();
                var gap = budget.Required(document.Coverage.Gaps[index]);
                budget.Text(gap.Code, false); budget.Text(gap.Message); budget.Locator(gap.NativeLocator);
                if (gap.ProjectionLocator is not null) LinkedOrigin(gap.NativeLocator, gap.ProjectionLocator);
            }
            for (var index = 0; index < diagnosticCount; index++)
            {
                budget.Check();
                var diagnostic = budget.Required(document.Diagnostics[index]);
                budget.Text(diagnostic.Code, false); budget.Text(diagnostic.Message); budget.Known(diagnostic.Severity);
                if (diagnostic.NativeLocator is not null) budget.Locator(diagnostic.NativeLocator);
                if (diagnostic.ProjectionLocator is not null) LinkedOrigin(diagnostic.NativeLocator, diagnostic.ProjectionLocator);
            }
            Capabilities(capabilityCount);
            var coverage = document.Coverage.Status switch
            {
                CoverageStatus.Complete => RevisionReportCoverage.Complete,
                CoverageStatus.Partial => RevisionReportCoverage.Partial,
                CoverageStatus.Unavailable => RevisionReportCoverage.Unavailable,
                _ => throw RevisionReportValidationBudget.Integrity()
            };
            // Metadata deliberately counts top-level document entries only; traversal has a separate aggregate budget.
            return new(coverage, boundaryCount + fieldCount + parameterCount + ruleCount + controlCount +
                relationshipCount + diagnosticCount + capabilityCount + gapCount, diagnosticCount, gapCount, operationDiagnosticCount);
        }

        private void Add(string id, ProjectionNodeReference reference, NativeLocator locator)
        {
            budget.Text(id, false); budget.Locator(locator);
            Require(nodes.TryAdd(id, reference));
            Origin(locator, reference);
        }

        private void Origin(NativeLocator locator, ProjectionNodeReference owner)
        {
            budget.Check();
            if (!origins.TryGetValue(locator, out var owners)) origins.Add(locator, owners = []);
            owners.Add(owner);
        }

        private void Reference(ProjectionNodeReference? reference)
        {
            var id = budget.ReferenceId(reference);
            Require(nodes.TryGetValue(id, out var existing) && existing == reference);
        }

        private void LinkedOrigin(NativeLocator? native, ProjectionNodeReference reference)
        {
            Reference(reference);
            Require(native is not null && native is not DocumentNativeLocator &&
                native is not JsonPointerNativeLocator { Pointer.Length: 0 });
            Require(origins.TryGetValue(native!, out var owners) && owners.Count == 1 && owners.Contains(reference));
        }

        private void Dependencies(ProjectionNodeReference from, IReadOnlyList<RuleDependency> dependencies)
        {
            var count = budget.Array(dependencies);
            for (var index = 0; index < count; index++)
            {
                budget.Check();
                var dependency = budget.Required(dependencies[index]);
                budget.Known(dependency.Role);
                switch (dependency)
                {
                    case FieldDependency field:
                        var fieldReference = new FieldNodeReference(field.FieldId);
                        Reference(fieldReference);
                        expected.Add((RelationshipKind.ReadsFrom, from, fieldReference));
                        break;
                    case ParameterDependency parameter:
                        var parameterReference = new ParameterNodeReference(parameter.ParameterId);
                        Reference(parameterReference);
                        expected.Add((RelationshipKind.DependsOn, from, parameterReference));
                        break;
                    case NodeDependency node:
                        Reference(node.Node);
                        expected.Add((RelationshipKind.DependsOn, from, node.Node));
                        break;
                    case ConstantDependency constant: budget.Literal(constant.Value); break;
                    default: throw RevisionReportValidationBudget.Integrity();
                }
            }
        }

        private void Details(ProjectionNodeReference owner, IReadOnlyList<DetailNode> details, int depth = 0)
        {
            var count = budget.Array(details);
            if (count > 0 && depth > budget.Caps.DetailDepthLimit) throw RevisionReportValidationBudget.Limit();
            for (var index = 0; index < count; index++)
            {
                budget.Check();
                switch (details[index])
                {
                    case GroupDetail group: budget.Text(group.Label); Details(owner, group.Children, depth + 1); break;
                    case ExpressionDetail expression: budget.Text(expression.Operation, false); Details(owner, expression.Arguments, depth + 1); break;
                    case LiteralDetail literal: budget.Literal(literal.Value); break;
                    case ReferenceDetail reference: Reference(reference.Reference); break;
                    case UnsupportedDetail unsupported:
                        budget.Text(unsupported.Construct, false); budget.Text(unsupported.Description, false);
                        budget.Locator(unsupported.NativeLocator); Origin(unsupported.NativeLocator, owner);
                        break;
                    default: throw RevisionReportValidationBudget.Integrity();
                }
            }
        }

        private void Relationships(int count)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var actual = new HashSet<(RelationshipKind Kind, ProjectionNodeReference From, ProjectionNodeReference To)>();
            var declared = new HashSet<(RelationshipKind Kind, ProjectionNodeReference From, ProjectionNodeReference To)>();
            for (var index = 0; index < count; index++)
            {
                budget.Check();
                var relationship = budget.Required(document.Relationships[index]);
                budget.Known(relationship.Kind); budget.Text(relationship.Id, false);
                Require(!nodes.ContainsKey(relationship.Id) && ids.Add(relationship.Id));
                Reference(relationship.From); Reference(relationship.To);
                var edge = (relationship.Kind, relationship.From, relationship.To);
                Require((relationship.Kind != RelationshipKind.Produces || relationship.From != relationship.To) && declared.Add(edge));
                // Produces is an explicit provider declaration. v1 imposes no endpoint-kind direction rule.
                if (relationship.Kind != RelationshipKind.Produces) actual.Add(edge);
            }
            Require(expected.SetEquals(actual));
        }

        private void Capabilities(int count)
        {
            var seen = new HashSet<(MappingOperation Operation, string Profile)>();
            for (var index = 0; index < count; index++)
            {
                budget.Check();
                var capability = budget.Required(document.Capabilities[index]);
                budget.Known(capability.Operation); budget.Known(capability.Status); budget.Text(capability.Profile, false);
                Require(seen.Add((capability.Operation, capability.Profile)));
                var boundaryCount = budget.Array(capability.BoundaryIds);
                var boundaryIds = new HashSet<string>(StringComparer.Ordinal);
                for (var boundaryIndex = 0; boundaryIndex < boundaryCount; boundaryIndex++)
                {
                    budget.Check();
                    var id = capability.BoundaryIds[boundaryIndex];
                    Reference(new BoundaryNodeReference(id));
                    Require(boundaryIds.Add(id));
                }
                var conditionCount = budget.Array(capability.Conditions);
                Require(capability.Status != CapabilityStatus.Conditional || conditionCount > 0);
                for (var conditionIndex = 0; conditionIndex < conditionCount; conditionIndex++)
                {
                    budget.Check();
                    var condition = budget.Required(capability.Conditions[conditionIndex]);
                    budget.Text(condition.Code, false); budget.Text(condition.Description, false);
                }
            }
        }

        private void Path(IReadOnlyList<PathSegment> path)
        {
            var count = budget.Array(path);
            for (var index = 0; index < count; index++)
            {
                budget.Check();
                switch (path[index])
                {
                    case FieldPathSegment field: budget.Text(field.Name); budget.OptionalText(field.NamespaceUri); break;
                    case AttributePathSegment attribute: budget.Text(attribute.Name); budget.OptionalText(attribute.NamespaceUri); break;
                    case IndexPathSegment item: budget.Unsigned(item.Index); break;
                    case AnyItemPathSegment: break;
                    default: throw RevisionReportValidationBudget.Integrity();
                }
            }
        }

        private void Type(FieldDataType? type)
        {
            budget.Required(type);
            budget.Known(type!.Kind);
            var count = budget.Array(type.ScalarTypes);
            var seen = new HashSet<ScalarDataType>();
            for (var index = 0; index < count; index++)
            {
                budget.Check(); budget.Known(type.ScalarTypes[index]);
                Require(seen.Add(type.ScalarTypes[index]));
            }
            Require(type.Kind switch
            {
                FieldDataTypeKind.Scalar => count == 1,
                FieldDataTypeKind.ScalarUnion => count >= 2,
                FieldDataTypeKind.Array => true,
                _ => count == 0
            });
            budget.OptionalText(type.NativeName);
        }

        private void Cardinality(FieldCardinality? value)
        {
            budget.Required(value); budget.Known(value!.Kind);
            if (value.Minimum is not null) budget.Unsigned(value.Minimum);
            if (value.Maximum is not null) budget.Unsigned(value.Maximum);
            Require(value.Kind switch
            {
                CardinalityKind.Unknown => value.Minimum is null && value.Maximum is null,
                CardinalityKind.ExactlyOne => value.Minimum is null or "1" && value.Maximum is null or "1",
                CardinalityKind.ZeroOrOne => value.Minimum is null or "0" && value.Maximum is null or "1",
                CardinalityKind.ZeroOrMore => value.Minimum is null or "0" && value.Maximum is null,
                CardinalityKind.OneOrMore => value.Minimum is null or "1" && value.Maximum is null,
                CardinalityKind.Bounded => (value.Minimum is not null || value.Maximum is not null) &&
                    (value.Minimum is null || value.Maximum is null || CompareUnsigned(value.Minimum, value.Maximum) <= 0),
                _ => false
            });
        }

        private static int CompareUnsigned(string left, string right) => left.Length != right.Length ?
            left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
    }
}
