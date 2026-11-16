using System.Globalization;
using System.Numerics;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.MappingBackends.Ferrule;

namespace Braid.MappingBackends;

public sealed partial class FerruleInspectionBackend
{
    private static void ValidateProjection(HostProjectionResult projection) =>
        ValidateProjection(projection, DefaultReportLimits, CancellationToken.None, reportOnly: false);

    private static void ValidateProjection(HostProjectionResult projection, RevisionReportPolicy limits,
        CancellationToken cancellationToken, bool reportOnly = true)
    {
        var budget = new ProjectionBudget(limits, cancellationToken, reportOnly);
        budget.Array(projection.Boundaries); budget.Array(projection.Fields); budget.Array(projection.Parameters);
        budget.Array(projection.Rules); budget.Array(projection.Controls); budget.Array(projection.Relationships);
        budget.Array(projection.Diagnostics, limits.DiagnosticLimit); budget.Array(projection.Capabilities);
        Require(projection.Coverage is not null && Enum.IsDefined(projection.Coverage.Status));
        budget.Array(projection.Coverage.Gaps, limits.DiagnosticLimit);
        Require(projection.Coverage.Status == CoverageStatus.Complete ? projection.Coverage.Gaps.Count == 0 : projection.Coverage.Gaps.Count > 0);
        if (projection.Coverage.Status == CoverageStatus.Unavailable)
            Require(projection.Boundaries.Count + projection.Fields.Count + projection.Parameters.Count + projection.Rules.Count
                + projection.Controls.Count + projection.Relationships.Count == 0);

        var nodes = new Dictionary<string, ProjectionNodeReference>(StringComparer.Ordinal);
        var origins = new Dictionary<NativeLocator, HashSet<ProjectionNodeReference>>();
        var expected = new HashSet<(RelationshipKind Kind, ProjectionNodeReference From, ProjectionNodeReference To)>();
        var unsupported = new List<(NativeLocator Locator, ProjectionNodeReference Owner)>();
        void Add(string id, ProjectionNodeReference reference, NativeLocator locator)
        {
            budget.Text(id, false); budget.Locator(locator);
            Require(nodes.TryAdd(id, reference));
            Origin(locator, reference);
        }
        void Origin(NativeLocator locator, ProjectionNodeReference reference)
        {
            if (!origins.TryGetValue(locator, out var owners))
                origins.Add(locator, owners = []);
            owners.Add(reference);
        }
        void Reference(ProjectionNodeReference reference)
        {
            var id = ProjectionReferenceId(reference);
            budget.Text(id, false);
            Require(nodes.TryGetValue(id, out var existing) && existing == reference);
        }
        void LinkedOrigin(NativeLocator? native, ProjectionNodeReference reference)
        {
            Reference(reference);
            if (native is GraphNodeNativeLocator graph)
                Require(origins.TryGetValue(graph, out var owners) && owners.Count == 1 && owners.Contains(reference));
        }
        void Dependencies(ProjectionNodeReference from, IReadOnlyList<RuleDependency> dependencies)
        {
            budget.Array(dependencies);
            foreach (var dependency in dependencies)
            {
                budget.Check();
                Require(dependency is not null && Enum.IsDefined(dependency.Role));
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
                    case ConstantDependency constant:
                        budget.Literal(constant.Value);
                        break;
                    default: throw new ProtocolException();
                }
            }
        }
        void Details(ProjectionNodeReference owner, IReadOnlyList<DetailNode> details, int depth = 0)
        {
            budget.Array(details);
            if (details.Count > 0 && depth > limits.DetailDepthLimit)
                throw new ProjectionLimitException();
            foreach (var detail in details)
            {
                budget.Check();
                switch (detail)
                {
                    case GroupDetail group:
                        budget.Text(group.Label);
                        Details(owner, group.Children, depth + 1);
                        break;
                    case ExpressionDetail expression:
                        budget.Text(expression.Operation, false);
                        Details(owner, expression.Arguments, depth + 1);
                        break;
                    case LiteralDetail literal:
                        budget.Literal(literal.Value);
                        break;
                    case ReferenceDetail reference:
                        Reference(reference.Reference);
                        break;
                    case UnsupportedDetail gap:
                        budget.Text(gap.Construct, false); budget.Text(gap.Description, false); budget.Locator(gap.NativeLocator);
                        unsupported.Add((gap.NativeLocator, owner));
                        Origin(gap.NativeLocator, owner);
                        break;
                    default: throw new ProtocolException();
                }
            }
        }

        var boundaries = new Dictionary<string, ProjectionBoundary>(StringComparer.Ordinal);
        var previousRole = -1;
        foreach (var boundary in projection.Boundaries)
        {
            budget.Check();
            Require(boundary is not null && Enum.IsDefined(boundary.Role) && Enum.IsDefined(boundary.Shape));
            Add(boundary.Id, new BoundaryNodeReference(boundary.Id), boundary.NativeLocator);
            boundaries.Add(boundary.Id, boundary);
            budget.Text(boundary.Name); budget.Text(boundary.Format, false);
            var rank = boundary.Role switch { BoundaryRole.PrimaryInput => 0, BoundaryRole.NamedInput => 1, BoundaryRole.PrimaryOutput => 2, BoundaryRole.NamedOutput => 3, _ => -1 };
            Require(rank >= previousRole);
            previousRole = rank;
        }
        if (projection.Coverage.Status != CoverageStatus.Unavailable)
        {
            Require(projection.Boundaries.Count(boundary => boundary.Role == BoundaryRole.PrimaryInput) == 1);
            Require(projection.Boundaries.Count(boundary => boundary.Role == BoundaryRole.PrimaryOutput) == 1);
        }
        var fields = new Dictionary<string, ProjectionField>(StringComparer.Ordinal);
        foreach (var field in projection.Fields)
        {
            budget.Check();
            Require(field is not null && Enum.IsDefined(field.Requirement) && Enum.IsDefined(field.Nullability));
            var reference = new FieldNodeReference(field.Id);
            Add(field.Id, reference, field.NativeLocator);
            fields.Add(field.Id, field);
            budget.Text(field.BoundaryId, false);
            Require(boundaries.ContainsKey(field.BoundaryId));
            budget.Text(field.Name); ProjectionPath(field.Path, budget); ProjectionType(field.DataType, budget); budget.Cardinality(field.Cardinality);
            expected.Add((RelationshipKind.BoundaryMember, reference, new BoundaryNodeReference(field.BoundaryId)));
        }
        foreach (var boundary in projection.Boundaries)
        {
            budget.Check();
            if (boundary.RootFieldId is { } root)
            {
                budget.Text(root, false);
                Require(fields.TryGetValue(root, out var field) && field.BoundaryId == boundary.Id && field.Path.Count == 0);
            }
        }
        foreach (var parameter in projection.Parameters)
        {
            budget.Check();
            Require(parameter is not null && Enum.IsDefined(parameter.Requirement));
            Add(parameter.Id, new ParameterNodeReference(parameter.Id), parameter.NativeLocator);
            budget.Text(parameter.Name); ProjectionType(parameter.DataType, budget);
            if (parameter.DefaultValue is not null) budget.Literal(parameter.DefaultValue);
        }
        foreach (var rule in projection.Rules)
        {
            budget.Check();
            Require(rule is not null);
            Add(rule.Id, new RuleNodeReference(rule.Id), rule.NativeLocator);
        }
        foreach (var control in projection.Controls)
        {
            budget.Check();
            Require(control is not null && Enum.IsDefined(control.Kind));
            var reference = new ControlNodeReference(control.Id);
            Add(control.Id, reference, control.NativeLocator);
            if (control.Kind == ControlKind.Unknown) unsupported.Add((control.NativeLocator, reference));
        }
        foreach (var rule in projection.Rules)
        {
            budget.Check();
            var reference = new RuleNodeReference(rule.Id);
            budget.Text(rule.Summary, false);
            budget.Text(rule.OutputFieldId, false);
            Require(fields.TryGetValue(rule.OutputFieldId, out var output)
                && boundaries[output.BoundaryId].Role is BoundaryRole.PrimaryOutput or BoundaryRole.NamedOutput);
            expected.Add((RelationshipKind.WritesTo, reference, new FieldNodeReference(rule.OutputFieldId)));
            Dependencies(reference, rule.Dependencies);
            Details(reference, rule.Details);
        }
        foreach (var control in projection.Controls)
        {
            budget.Check();
            var reference = new ControlNodeReference(control.Id);
            budget.Text(control.Summary, false);
            budget.Array(control.AppliesTo);
            foreach (var target in control.AppliesTo)
            {
                budget.Check();
                Reference(target);
                expected.Add((RelationshipKind.GovernedBy, target, reference));
            }
            Dependencies(reference, control.Dependencies);
            Details(reference, control.Details);
        }
        var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
        var actual = new HashSet<(RelationshipKind Kind, ProjectionNodeReference From, ProjectionNodeReference To)>();
        foreach (var relationship in projection.Relationships)
        {
            budget.Check();
            Require(relationship is not null && Enum.IsDefined(relationship.Kind));
            budget.Text(relationship.Id, false);
            Require(!nodes.ContainsKey(relationship.Id) && relationshipIds.Add(relationship.Id));
            Reference(relationship.From); Reference(relationship.To);
            Require(actual.Add((relationship.Kind, relationship.From, relationship.To)));
        }
        Require(expected.SetEquals(actual));
        foreach (var gap in projection.Coverage.Gaps)
        {
            budget.Check();
            Require(gap is not null);
            budget.Text(gap.Code, false); budget.Text(gap.Message, false); budget.Locator(gap.NativeLocator);
            if (gap.ProjectionLocator is not null) LinkedOrigin(gap.NativeLocator, gap.ProjectionLocator);
        }
        if (unsupported.Count > 0)
        {
            Require(projection.Coverage.Status == CoverageStatus.Partial);
            foreach (var item in unsupported)
                Require(projection.Coverage.Gaps.Any(gap => gap.NativeLocator == item.Locator
                    && (gap.ProjectionLocator is null || gap.ProjectionLocator == item.Owner)));
        }
        foreach (var diagnostic in projection.Diagnostics)
        {
            budget.Check();
            Require(diagnostic is not null && Enum.IsDefined(diagnostic.Severity));
            budget.Text(diagnostic.Code, false); budget.Text(diagnostic.Message, false);
            if (diagnostic.NativeLocator is not null) budget.Locator(diagnostic.NativeLocator);
            if (diagnostic.ProjectionLocator is not null)
                LinkedOrigin(diagnostic.NativeLocator, diagnostic.ProjectionLocator);
        }
        ProjectionCapabilities(projection.Capabilities, projection.Boundaries, budget);
    }

    private static void ProjectionCapabilities(IReadOnlyList<OperationCapability> capabilities, IReadOnlyList<ProjectionBoundary> boundaries, ProjectionBudget budget)
    {
        budget.Check();
        Require(capabilities.Count == Enum.GetValues<MappingOperation>().Length);
        var seen = new HashSet<MappingOperation>();
        var outputs = boundaries.Where(boundary => boundary.Role is BoundaryRole.PrimaryOutput or BoundaryRole.NamedOutput).Select(boundary => boundary.Id).ToArray();
        foreach (var capability in capabilities)
        {
            budget.Check();
            Require(capability is not null && Enum.IsDefined(capability.Operation) && seen.Add(capability.Operation));
            Require(capability.Status == (capability.Operation is MappingOperation.Inspect or MappingOperation.Validate or MappingOperation.Project
                ? CapabilityStatus.Supported : CapabilityStatus.NotQualified));
            budget.Text(capability.Profile, false); budget.Array(capability.BoundaryIds); budget.Array(capability.Conditions);
            foreach (var id in capability.BoundaryIds) budget.Text(id, false);
            Require(capability.BoundaryIds.SequenceEqual(capability.Operation is MappingOperation.Preview or MappingOperation.Build ? outputs : [], StringComparer.Ordinal));
            Require(capability.Conditions.Count > 0);
            foreach (var condition in capability.Conditions)
            {
                budget.Check();
                Require(condition is not null);
                budget.Text(condition.Code, false); budget.Text(condition.Description, false);
            }
            if (capability.Operation == MappingOperation.Project)
                Require(capability.Profile == "ferrule.projection.v1" && capability.Conditions.Any(condition => condition.Code == "boundedReadOnlyProjection"));
        }
    }

    private static void ProjectionPath(IReadOnlyList<PathSegment> path, ProjectionBudget budget)
    {
        budget.Check();
        budget.Array(path);
        foreach (var segment in path)
        {
            budget.Check();
            switch (segment)
            {
                case FieldPathSegment field: budget.Text(field.Name); if (field.NamespaceUri is not null) budget.Text(field.NamespaceUri); break;
                case AttributePathSegment attribute: budget.Text(attribute.Name); if (attribute.NamespaceUri is not null) budget.Text(attribute.NamespaceUri); break;
                case IndexPathSegment index: budget.Unsigned(index.Index); break;
                case AnyItemPathSegment: break;
                default: throw new ProtocolException();
            }
        }
    }

    private static void ProjectionType(FieldDataType type, ProjectionBudget budget)
    {
        budget.Check();
        Require(type is not null && Enum.IsDefined(type.Kind));
        budget.Array(type.ScalarTypes);
        Require(type.ScalarTypes.All(Enum.IsDefined) && type.ScalarTypes.Distinct().Count() == type.ScalarTypes.Count);
        Require(type.Kind switch
        {
            FieldDataTypeKind.Scalar => type.ScalarTypes.Count == 1,
            FieldDataTypeKind.ScalarUnion => type.ScalarTypes.Count >= 2,
            FieldDataTypeKind.Array => true,
            _ => type.ScalarTypes.Count == 0
        });
        if (type.NativeName is not null) budget.Text(type.NativeName);
    }

    private static void ProjectionCardinality(FieldCardinality cardinality, ProjectionBudget budget)
    {
        budget.Check();
        Require(cardinality is not null && Enum.IsDefined(cardinality.Kind));
        BigInteger? minimum = cardinality.Minimum is null ? null : budget.Unsigned(cardinality.Minimum);
        BigInteger? maximum = cardinality.Maximum is null ? null : budget.Unsigned(cardinality.Maximum);
        Require(cardinality.Kind switch
        {
            CardinalityKind.Unknown => minimum is null && maximum is null,
            CardinalityKind.ExactlyOne => (minimum is null || minimum == 1) && (maximum is null || maximum == 1),
            CardinalityKind.ZeroOrOne => (minimum is null || minimum == 0) && (maximum is null || maximum == 1),
            CardinalityKind.ZeroOrMore => (minimum is null || minimum == 0) && maximum is null,
            CardinalityKind.OneOrMore => (minimum is null || minimum == 1) && maximum is null,
            CardinalityKind.Bounded => (minimum is not null || maximum is not null)
                && (minimum is null || maximum is null || minimum <= maximum),
            _ => false
        });
    }

    private static void ProjectionLiteral(LiteralValue literal, ProjectionBudget budget)
    {
        budget.Check();
        switch (literal)
        {
            case StringLiteralValue text: budget.Text(text.Value); break;
            case BooleanLiteralValue: break;
            case NullLiteralValue nil: Require(Enum.IsDefined(nil.NullKind)); break;
            case ExactNumberLiteralValue number:
                Require(Enum.IsDefined(number.Flavor));
                budget.Text(number.Value, false);
                if (number.Flavor == ExactNumberFlavor.BinaryFloat)
                {
                    Require(number.Value.Length == 16 && number.Value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
                    Require(ulong.TryParse(number.Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits)
                        && (bits & 0x7ff0000000000000UL) != 0x7ff0000000000000UL);
                }
                else
                    Require(ProjectionNumberLexeme(number.Value, number.Flavor == ExactNumberFlavor.Integer, budget));
                break;
            default: throw new ProtocolException();
        }
    }

    private static bool ProjectionNumberLexeme(string value, bool integerOnly, ProjectionBudget budget)
    {
        budget.Check();
        var index = value[0] == '-' ? 1 : 0;
        if (index == value.Length) return false;
        if (value[index] == '0') index++;
        else
        {
            if (value[index] is < '1' or > '9') return false;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
            {
                budget.Check();
                index++;
            }
        }
        if (integerOnly) return index == value.Length;
        if (index < value.Length && value[index] == '.')
        {
            var start = ++index;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
            {
                budget.Check();
                index++;
            }
            if (start == index) return false;
        }
        if (index < value.Length && value[index] is 'e' or 'E')
        {
            index++;
            if (index < value.Length && value[index] is '+' or '-') index++;
            var start = index;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
            {
                budget.Check();
                index++;
            }
            if (start == index) return false;
        }
        return index == value.Length;
    }

    private static BigInteger ProjectionUnsignedInteger(string value, ProjectionBudget budget)
    {
        budget.Check();
        budget.Text(value, false);
        Require(value.Length > 0 && (value == "0" || value[0] != '0') && value.All(char.IsAsciiDigit));
        Require(BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var integer));
        return integer;
    }

    private static void ProjectionLocator(NativeLocator locator, ProjectionBudget budget)
    {
        budget.Check();
        Require(locator is not null);
        budget.Text(locator.DocumentId, false);
        Require(locator.DocumentId == InspectionPolicy.DocumentId);
        switch (locator)
        {
            case DocumentNativeLocator: break;
            case GraphNodeNativeLocator graph:
                budget.Unsigned(graph.NodeId);
                Require(uint.TryParse(graph.NodeId, NumberStyles.None, CultureInfo.InvariantCulture, out _));
                if (graph.FunctionId is not null)
                {
                    budget.Unsigned(graph.FunctionId);
                    Require(ulong.TryParse(graph.FunctionId, NumberStyles.None, CultureInfo.InvariantCulture, out _));
                }
                break;
            case JsonPointerNativeLocator pointer:
                budget.Text(pointer.Pointer);
                Require(pointer.Pointer.Length == 0 || pointer.Pointer[0] == '/');
                for (var index = 0; index < pointer.Pointer.Length; index++)
                {
                    budget.Check();
                    if (pointer.Pointer[index] == '~') Require(++index < pointer.Pointer.Length && pointer.Pointer[index] is '0' or '1');
                }
                break;
            case TextSpanNativeLocator span:
                budget.Unsigned(span.StartOffset); budget.Unsigned(span.Length); Require(Enum.IsDefined(span.Unit));
                break;
            case OpaqueNativeLocator opaque: budget.Text(opaque.Scheme, false); budget.Text(opaque.Value); break;
            default: throw new ProtocolException();
        }
    }

    private static string ProjectionReferenceId(ProjectionNodeReference reference) => reference switch
    {
        BoundaryNodeReference boundary => boundary.BoundaryId,
        FieldNodeReference field => field.FieldId,
        ParameterNodeReference parameter => parameter.ParameterId,
        RuleNodeReference rule => rule.RuleId,
        ControlNodeReference control => control.ControlId,
        _ => throw new ProtocolException()
    };

    private sealed class ProjectionLimitException : Exception { }
}
