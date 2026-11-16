using System.Globalization;
using System.Text;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Api.Fixtures;

/// <summary>Fixed templates paired with self-authored native sources, not a production artifact decoder.</summary>
internal sealed class SyntheticProjectionBuilder
{
    private const string DocumentId = "entry.synthetic.json";
    private readonly List<ProjectionBoundary> _boundaries = [];
    private readonly List<ProjectionField> _fields = [];
    private readonly List<ProjectionParameter> _parameters = [];
    private readonly List<ProjectionRule> _rules = [];
    private readonly List<ProjectionControl> _controls = [];
    private readonly ProjectionProvenance _provenance;

    private SyntheticProjectionBuilder(NativeArtifact artifact)
    {
        var scope = artifact.Scope;
        _provenance = new ProjectionProvenance(
            new ScopedRevisionId(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D")),
            new ArtifactDigest("sha256", artifact.Digest.Value),
            new BackendIdentity("braid.synthetic", "braid.synthetic.format.v1", "braid.synthetic.fixture-build.v1"),
            "braid.synthetic.adapter.v1", "braid.synthetic.projection.v1");
    }

    public static ProjectionDocumentV1 Build(string id, NativeArtifact artifact)
    {
        var builder = new SyntheticProjectionBuilder(artifact);
        if (id == "unavailable")
            return builder.Unavailable();
        if (id == "rich")
            builder.Rich();
        else
            builder.Direct();

        if (id == "partial")
            return builder.Partial();
        return builder.Complete();
    }

    public static ProjectionDocumentV1 Limited(ProjectionDocumentV1 document, string code, string message)
    {
        var locator = new DocumentNativeLocator(DocumentId);
        return document with
        {
            Coverage = new ProjectionCoverage(CoverageStatus.Unavailable, [new CoverageGap(code, message, locator)]),
            Boundaries = [],
            Fields = [],
            Parameters = [],
            Rules = [],
            Controls = [],
            Relationships = [],
            Diagnostics = [new ProjectionDiagnostic(code, DiagnosticSeverity.Warning, message, locator)],
            Capabilities = Capabilities([])
        };
    }

    private void Direct()
    {
        var input = Boundary(0, "source", BoundaryRole.PrimaryInput, "json", BoundaryShape.Object, "/inputNodes/root");
        var output = Boundary(1, "target", BoundaryRole.PrimaryOutput, "json", BoundaryShape.Object, "/outputNodes/root");
        var literal = Field(input, "customer.name", [new FieldPathSegment("customer.name")], Scalar(ScalarDataType.String),
            new GraphNodeNativeLocator(DocumentId, "9007199254740993"), CardinalityKind.ZeroOrOne, FieldRequirement.Optional, FieldNullability.Nullable);
        var nested = Field(input, "name", [new FieldPathSegment("customer"), new FieldPathSegment("name")], Scalar(ScalarDataType.String), Pointer("/inputNodes/customer/name"));
        var literalOutput = Field(output, "display.name", [new FieldPathSegment("display.name")], Scalar(ScalarDataType.String), Pointer("/outputNodes/display.name"));
        var nestedOutput = Field(output, "name", [new FieldPathSegment("display"), new FieldPathSegment("name")], Scalar(ScalarDataType.String), Pointer("/outputNodes/a~0b~1c"));
        Rule(0, literalOutput, "Copy the field whose literal name is customer.name.", [new FieldDependency(literal.Id)], [new ReferenceDetail(new FieldNodeReference(literal.Id))]);
        Rule(1, nestedOutput, "Copy name from the customer object.", [new FieldDependency(nested.Id)], [new ReferenceDetail(new FieldNodeReference(nested.Id))]);
    }

    private void Rich()
    {
        var orders = Boundary(0, "orders", BoundaryRole.PrimaryInput, "json", BoundaryShape.Rows, "/orders/root");
        var customers = Boundary(1, "customers", BoundaryRole.NamedInput, "xml", BoundaryShape.Object, "/customers/root");
        var rates = Boundary(2, "rates", BoundaryRole.NamedInput, "json", BoundaryShape.Object, "/rates/root");
        var invoices = Boundary(3, "invoices", BoundaryRole.PrimaryOutput, "json", BoundaryShape.Rows, "/invoices/root");
        var audit = Boundary(4, "audit", BoundaryRole.NamedOutput, "json", BoundaryShape.Rows, "/audit/root");
        var rejected = Boundary(5, "rejected", BoundaryRole.NamedOutput, "json", BoundaryShape.Rows, "/rejected/root");
        var customerId = Field(orders, "customerId", [new AnyItemPathSegment(), new FieldPathSegment("customerId")], Scalar(ScalarDataType.String), new GraphNodeNativeLocator(DocumentId, "12", "orders"));
        var amount = Field(orders, "amount", [new AnyItemPathSegment(), new FieldPathSegment("amount")], Scalar(ScalarDataType.Decimal), Pointer("/orders/amount"));
        Field(orders, "currency", [new AnyItemPathSegment(), new FieldPathSegment("currency")], Scalar(ScalarDataType.String), Pointer("/orders/currency"));
        var customerKey = Field(customers, "id", [new FieldPathSegment("customer", "urn:braid:customers"), new AttributePathSegment("id", "urn:braid:identity")], Scalar(ScalarDataType.String), new GraphNodeNativeLocator(DocumentId, "12", "customers"));
        var tier = Field(customers, "tier", [new FieldPathSegment("customer", "urn:braid:customers"), new FieldPathSegment("tier", "urn:braid:customers")], Scalar(ScalarDataType.String), Pointer("/customers/tier"), CardinalityKind.ZeroOrOne, FieldRequirement.Optional, FieldNullability.Unknown);
        var rate = Field(rates, "value", [new FieldPathSegment("rates"), new IndexPathSegment("0"), new FieldPathSegment("value")], Scalar(ScalarDataType.Decimal), Pointer("/rates/value"));
        var invoiceCustomer = Field(invoices, "customerId", [new AnyItemPathSegment(), new FieldPathSegment("customerId")], Scalar(ScalarDataType.String), Pointer("/invoices/customerId"));
        var total = Field(invoices, "total", [new AnyItemPathSegment(), new FieldPathSegment("total")], Scalar(ScalarDataType.Decimal), Pointer("/invoices/total"));
        var invoiceTier = Field(invoices, "tier", [new AnyItemPathSegment(), new FieldPathSegment("tier")], Scalar(ScalarDataType.String), Pointer("/invoices/tier"));
        var flag = Field(invoices, "flag", [new AnyItemPathSegment(), new FieldPathSegment("flag")], Scalar(ScalarDataType.Boolean), Pointer("/invoices/flag"));
        var sequence = Field(audit, "sequence", [new AnyItemPathSegment(), new FieldPathSegment("sequence")], Scalar(ScalarDataType.Integer), new GraphNodeNativeLocator(DocumentId, "9007199254740993", "audit"));
        var negativeZero = Field(audit, "negativeZero", [new AnyItemPathSegment(), new FieldPathSegment("negativeZero")], Scalar(ScalarDataType.BinaryFloat), Pointer("/audit/negativeZero"));
        var regionField = Field(audit, "region", [new AnyItemPathSegment(), new FieldPathSegment("region")], Scalar(ScalarDataType.String), Pointer("/audit/region"));
        var reason = Field(rejected, "reason", [new AnyItemPathSegment(), new FieldPathSegment("reason")], Scalar(ScalarDataType.String), Pointer("/rejected/reason"));
        var exactDecimal = new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "0.1000000000000000000000000001");
        var threshold = Parameter(0, "threshold", Scalar(ScalarDataType.Decimal), exactDecimal);
        var region = Parameter(1, "region", Scalar(ScalarDataType.String), new StringLiteralValue("west"));

        var customerRule = Rule(0, invoiceCustomer, "Copy the order customer ID.", [new FieldDependency(customerId.Id)], [new ReferenceDetail(new FieldNodeReference(customerId.Id))]);
        var totalRule = Rule(1, total, "Convert amounts above the threshold; otherwise use the exact default.",
            [new FieldDependency(amount.Id), new FieldDependency(rate.Id), new ParameterDependency(threshold.Id, DependencyRole.Condition), new ConstantDependency(exactDecimal, DependencyRole.Default)],
            [new GroupDetail("Conditional total", [new ExpressionDetail("greaterThan", [new ReferenceDetail(new FieldNodeReference(amount.Id)), new ReferenceDetail(new ParameterNodeReference(threshold.Id))]), new ExpressionDetail("multiply", [new ReferenceDetail(new FieldNodeReference(amount.Id)), new ReferenceDetail(new FieldNodeReference(rate.Id))]), new LiteralDetail(exactDecimal)])]);
        var tierRule = Rule(2, invoiceTier, "Use the joined customer tier, defaulting to standard.",
            [new FieldDependency(tier.Id), new ConstantDependency(new StringLiteralValue("standard"), DependencyRole.Default)],
            [new ExpressionDetail("coalesce", [new ReferenceDetail(new FieldNodeReference(tier.Id)), new LiteralDetail(new StringLiteralValue("standard"))])]);
        var flagRule = Rule(3, flag, "Flag orders whose amount exceeds the configured threshold.",
            [new FieldDependency(amount.Id, DependencyRole.Condition), new ParameterDependency(threshold.Id, DependencyRole.Condition), new ConstantDependency(new BooleanLiteralValue(true)), new ConstantDependency(new BooleanLiteralValue(false), DependencyRole.Default)],
            [new ExpressionDetail("greaterThan", [new ReferenceDetail(new FieldNodeReference(amount.Id)), new ReferenceDetail(new ParameterNodeReference(threshold.Id))]), new LiteralDetail(new BooleanLiteralValue(true)), new LiteralDetail(new BooleanLiteralValue(false))]);
        var integer = new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "9007199254740993");
        Rule(4, sequence, "Emit the exact sequence constant.", [new ConstantDependency(integer)], [new LiteralDetail(integer)]);
        var binaryFloat = new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "8000000000000000");
        Rule(5, negativeZero, "Retain the negative-zero IEEE-754 bits.", [new ConstantDependency(binaryFloat)], [new LiteralDetail(binaryFloat)]);
        Rule(6, regionField, "Record the supplied region parameter.", [new ParameterDependency(region.Id)], [new ReferenceDetail(new ParameterNodeReference(region.Id))]);
        Rule(7, reason, "Explain rejected rows with a fixed reason.", [new ConstantDependency(new StringLiteralValue("below threshold")), new ConstantDependency(new NullLiteralValue(NullLiteralKind.Absent), DependencyRole.Default)], [new LiteralDetail(new StringLiteralValue("below threshold")), new LiteralDetail(new NullLiteralValue(NullLiteralKind.Absent))]);

        ProjectionNodeReference[] invoiceRules = [new RuleNodeReference(customerRule.Id), new RuleNodeReference(totalRule.Id), new RuleNodeReference(tierRule.Id), new RuleNodeReference(flagRule.Id)];
        Control(0, ControlKind.Iteration, "Repeat invoice rules for each order row.", invoiceRules, [new NodeDependency(new BoundaryNodeReference(orders.Id))], [new ReferenceDetail(new BoundaryNodeReference(orders.Id))]);
        Control(1, ControlKind.Filter, "Keep rows above the parameter threshold.", invoiceRules, [new FieldDependency(amount.Id, DependencyRole.Condition), new ParameterDependency(threshold.Id, DependencyRole.Condition)], [new ExpressionDetail("greaterThan", [new ReferenceDetail(new FieldNodeReference(amount.Id)), new ReferenceDetail(new ParameterNodeReference(threshold.Id))])]);
        Control(2, ControlKind.Join, "Join order customerId to the named XML customer id attribute.", [new RuleNodeReference(customerRule.Id), new RuleNodeReference(tierRule.Id)], [new FieldDependency(customerId.Id), new FieldDependency(customerKey.Id)], [new ExpressionDetail("equals", [new ReferenceDetail(new FieldNodeReference(customerId.Id)), new ReferenceDetail(new FieldNodeReference(customerKey.Id))])]);
        Control(3, ControlKind.Grouping, "Group invoice amounts by customer ID.", [new RuleNodeReference(totalRule.Id)], [new FieldDependency(customerId.Id)], [new GroupDetail("Grouping key", [new ReferenceDetail(new FieldNodeReference(customerId.Id))])]);
        Control(4, ControlKind.Sorting, "Sort invoice rows by customer ID in ascending order.", [new BoundaryNodeReference(invoices.Id)], [new FieldDependency(customerId.Id)], [new ExpressionDetail("ascending", [new ReferenceDetail(new FieldNodeReference(customerId.Id))])]);
        Control(5, ControlKind.Window, "Describe a three-row running amount window.", [new RuleNodeReference(totalRule.Id)], [new FieldDependency(amount.Id)], [new ExpressionDetail("windowSum", [new ReferenceDetail(new FieldNodeReference(amount.Id)), new LiteralDetail(new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "3"))])]);
        Control(6, ControlKind.Failure, "Report a failure when a total cannot be produced.", [new RuleNodeReference(totalRule.Id)], [new NodeDependency(new RuleNodeReference(totalRule.Id), DependencyRole.Condition)], [new ReferenceDetail(new RuleNodeReference(totalRule.Id))]);
        Control(7, ControlKind.Ordering, "Keep output publication order: invoices, audit, rejected.", [new BoundaryNodeReference(invoices.Id), new BoundaryNodeReference(audit.Id), new BoundaryNodeReference(rejected.Id)], [], [new GroupDetail("Output order", [new ReferenceDetail(new BoundaryNodeReference(invoices.Id)), new ReferenceDetail(new BoundaryNodeReference(audit.Id)), new ReferenceDetail(new BoundaryNodeReference(rejected.Id))])]);
    }

    private ProjectionDocumentV1 Partial()
    {
        var locator = Pointer("/rules/0/unknown");
        var reference = new RuleNodeReference(_rules[0].Id);
        _rules[0] = _rules[0] with
        {
            Summary = "The visible direct dependency has an unsupported native extension.",
            Details = [new UnsupportedDetail("futureTransform", "The synthetic extension has no viewer explanation.", locator)]
        };
        return Document(new ProjectionCoverage(CoverageStatus.Partial, [new CoverageGap("projection.unsupportedConstruct", "The native extension remains unexplained.", locator, reference)]),
            [new ProjectionDiagnostic("projection.unsupportedConstruct", DiagnosticSeverity.Warning, "The native extension remains unexplained.", locator, reference), new ProjectionDiagnostic("projection.incompleteLineage", DiagnosticSeverity.Warning, "Additional native dependencies may exist in the extension.", locator, reference)]);
    }

    private ProjectionDocumentV1 Unavailable()
    {
        var locator = Pointer("/opaquePayload");
        return Document(new ProjectionCoverage(CoverageStatus.Unavailable, [new CoverageGap("projection.unsupportedEnvelope", "The synthetic envelope has no supported graph.", locator)]),
            [new ProjectionDiagnostic("projection.unsupportedEnvelope", DiagnosticSeverity.Warning, "The original artifact is retained; an explanation is unavailable.", locator)]);
    }

    private ProjectionDocumentV1 Complete() => Document(new ProjectionCoverage(CoverageStatus.Complete, []), []);

    private ProjectionDocumentV1 Document(ProjectionCoverage coverage, IReadOnlyList<ProjectionDiagnostic> diagnostics) =>
        new(_provenance, coverage, _boundaries.ToArray(), _fields.ToArray(), _parameters.ToArray(), _rules.ToArray(), _controls.ToArray(), Relationships(), diagnostics, Capabilities(_boundaries));

    private ProjectionBoundary Boundary(int index, string name, BoundaryRole role, string format, BoundaryShape shape, string rootPointer)
    {
        var locator = Pointer("/boundaries/" + index.ToString(CultureInfo.InvariantCulture));
        var id = StableId("boundary", "", locator);
        var root = Field(id, name, [], new FieldDataType(shape == BoundaryShape.Rows ? FieldDataTypeKind.Array : FieldDataTypeKind.Object, []), Pointer(rootPointer));
        var boundary = new ProjectionBoundary(id, name, role, format, shape, root.Id, locator);
        _boundaries.Add(boundary);
        return boundary;
    }

    private ProjectionField Field(ProjectionBoundary boundary, string name, IReadOnlyList<PathSegment> path, FieldDataType type, NativeLocator locator, CardinalityKind cardinality = CardinalityKind.ExactlyOne, FieldRequirement requirement = FieldRequirement.Required, FieldNullability nullability = FieldNullability.NonNullable) =>
        Field(boundary.Id, name, path, type, locator, cardinality, requirement, nullability);

    private ProjectionField Field(string boundaryId, string name, IReadOnlyList<PathSegment> path, FieldDataType type, NativeLocator locator, CardinalityKind cardinality = CardinalityKind.ExactlyOne, FieldRequirement requirement = FieldRequirement.Required, FieldNullability nullability = FieldNullability.NonNullable)
    {
        var field = new ProjectionField(StableId("field", boundaryId, locator), boundaryId, path, name, type, new FieldCardinality(cardinality), requirement, nullability, locator);
        _fields.Add(field);
        return field;
    }

    private ProjectionParameter Parameter(int index, string name, FieldDataType type, LiteralValue defaultValue)
    {
        var locator = Pointer("/parameters/" + index.ToString(CultureInfo.InvariantCulture));
        var parameter = new ProjectionParameter(StableId("parameter", "", locator), name, type, FieldRequirement.Optional, defaultValue, locator);
        _parameters.Add(parameter);
        return parameter;
    }

    private ProjectionRule Rule(int index, ProjectionField output, string summary, IReadOnlyList<RuleDependency> dependencies, IReadOnlyList<DetailNode> details)
    {
        var locator = Pointer("/rules/" + index.ToString(CultureInfo.InvariantCulture));
        var rule = new ProjectionRule(StableId("rule", output.BoundaryId, locator), output.Id, summary, dependencies, details, locator);
        _rules.Add(rule);
        return rule;
    }

    private void Control(int index, ControlKind kind, string summary, IReadOnlyList<ProjectionNodeReference> appliesTo, IReadOnlyList<RuleDependency> dependencies, IReadOnlyList<DetailNode> details)
    {
        var locator = Pointer("/controls/" + index.ToString(CultureInfo.InvariantCulture));
        _controls.Add(new ProjectionControl(StableId("control", "", locator), kind, summary, appliesTo, dependencies, details, locator));
    }

    private IReadOnlyList<ProjectionRelationship> Relationships()
    {
        var relationships = new List<ProjectionRelationship>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(RelationshipKind kind, ProjectionNodeReference from, ProjectionNodeReference to)
        {
            var id = StableId("relationship", ReferenceId(from), new OpaqueNativeLocator(DocumentId, kind.ToString(), ReferenceId(to)));
            if (seen.Add(id))
                relationships.Add(new ProjectionRelationship(id, kind, from, to));
        }

        void Dependencies(ProjectionNodeReference from, IReadOnlyList<RuleDependency> dependencies)
        {
            foreach (var dependency in dependencies)
            {
                switch (dependency)
                {
                    case FieldDependency field: Add(RelationshipKind.ReadsFrom, from, new FieldNodeReference(field.FieldId)); break;
                    case ParameterDependency parameter: Add(RelationshipKind.DependsOn, from, new ParameterNodeReference(parameter.ParameterId)); break;
                    case NodeDependency node: Add(RelationshipKind.DependsOn, from, node.Node); break;
                    case ConstantDependency: break;
                    default: throw new InvalidOperationException("Unknown synthetic dependency kind.");
                }
            }
        }

        foreach (var field in _fields)
            Add(RelationshipKind.BoundaryMember, new FieldNodeReference(field.Id), new BoundaryNodeReference(field.BoundaryId));
        foreach (var rule in _rules)
        {
            var reference = new RuleNodeReference(rule.Id);
            Add(RelationshipKind.WritesTo, reference, new FieldNodeReference(rule.OutputFieldId));
            Dependencies(reference, rule.Dependencies);
        }
        foreach (var control in _controls)
        {
            var reference = new ControlNodeReference(control.Id);
            foreach (var target in control.AppliesTo)
                Add(RelationshipKind.GovernedBy, target, reference);
            Dependencies(reference, control.Dependencies);
        }
        return relationships.ToArray();
    }

    private static IReadOnlyList<OperationCapability> Capabilities(IReadOnlyList<ProjectionBoundary> boundaries)
    {
        var outputs = boundaries.Where(boundary => boundary.Role is BoundaryRole.PrimaryOutput or BoundaryRole.NamedOutput).Select(boundary => boundary.Id).ToArray();
        CapabilityCondition[] fixtureOnly = [new("fixtureOnly", "Self-authored explanation data; no production mapping runtime is qualified.")];
        return
        [
            new(MappingOperation.Inspect, CapabilityStatus.Supported, "braid.synthetic.fixture.v1", [], fixtureOnly),
            new(MappingOperation.Project, CapabilityStatus.Supported, "braid.synthetic.fixture.v1", [], fixtureOnly),
            new(MappingOperation.Validate, CapabilityStatus.NotQualified, "braid.synthetic.fixture.v1", [], fixtureOnly),
            new(MappingOperation.Preview, CapabilityStatus.NotQualified, "braid.synthetic.fixture.v1", outputs, fixtureOnly),
            new(MappingOperation.Build, CapabilityStatus.NotQualified, "braid.synthetic.fixture.v1", outputs, fixtureOnly)
        ];
    }

    private static FieldDataType Scalar(ScalarDataType type) => new(FieldDataTypeKind.Scalar, [type]);
    private static JsonPointerNativeLocator Pointer(string pointer) => new(DocumentId, pointer);

    private static string ReferenceId(ProjectionNodeReference reference) => reference switch
    {
        BoundaryNodeReference boundary => boundary.BoundaryId,
        FieldNodeReference field => field.FieldId,
        ParameterNodeReference parameter => parameter.ParameterId,
        RuleNodeReference rule => rule.RuleId,
        ControlNodeReference control => control.ControlId,
        _ => throw new InvalidOperationException("Unknown synthetic reference kind.")
    };

    private static string StableId(string kind, string owner, NativeLocator locator)
    {
        string[] parts = locator switch
        {
            DocumentNativeLocator document => ["document", document.DocumentId],
            JsonPointerNativeLocator pointer => ["jsonPointer", pointer.DocumentId, pointer.Pointer],
            GraphNodeNativeLocator node => ["graphNode", node.DocumentId, node.FunctionId ?? "", node.NodeId],
            OpaqueNativeLocator opaque => ["opaque", opaque.DocumentId, opaque.Scheme, opaque.Value],
            _ => throw new InvalidOperationException("Unknown synthetic locator kind.")
        };
        var identity = string.Join("|", new[] { kind, owner }.Concat(parts).Select(part => part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part));
        return kind + ":" + Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(identity)).Value;
    }
}
