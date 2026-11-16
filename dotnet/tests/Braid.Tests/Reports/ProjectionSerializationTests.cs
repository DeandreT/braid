using System.Text.Json;
using Braid.Contracts.Projections;

namespace Braid.Tests;

public sealed class ProjectionSerializationTests
{
    private static readonly JsonSerializerOptions Options = ProjectionJson.CreateOptions();

    [Fact]
    public void PolymorphicContractsRetainTheirKindsAndTypedContents()
    {
        (Type Contract, object Value, string Kind)[] cases =
        [
            (typeof(PathSegment), new FieldPathSegment("customer.name"), "field"),
            (typeof(PathSegment), new AttributePathSegment("id", "urn:orders"), "attribute"),
            (typeof(PathSegment), new IndexPathSegment("9007199254740993"), "index"),
            (typeof(PathSegment), new AnyItemPathSegment(), "anyItem"),
            (typeof(LiteralValue), new StringLiteralValue("00123"), "string"),
            (typeof(LiteralValue), new BooleanLiteralValue(false), "boolean"),
            (typeof(LiteralValue), new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "0.30"), "exactNumber"),
            (typeof(LiteralValue), new NullLiteralValue(NullLiteralKind.Absent), "null"),
            (typeof(LiteralValue), new NullLiteralValue(NullLiteralKind.ExplicitNull), "null"),
            (typeof(LiteralValue), new NullLiteralValue(NullLiteralKind.XmlNil), "null"),
            (typeof(ProjectionNodeReference), new BoundaryNodeReference("source"), "boundary"),
            (typeof(ProjectionNodeReference), new FieldNodeReference("source.name"), "field"),
            (typeof(ProjectionNodeReference), new ParameterNodeReference("fallback"), "parameter"),
            (typeof(ProjectionNodeReference), new RuleNodeReference("rule.name"), "rule"),
            (typeof(ProjectionNodeReference), new ControlNodeReference("filter"), "control"),
            (typeof(RuleDependency), new FieldDependency("source.name"), "field"),
            (typeof(RuleDependency), new ParameterDependency("fallback", DependencyRole.Default), "parameter"),
            (typeof(RuleDependency), new ConstantDependency(new StringLiteralValue("default")), "constant"),
            (typeof(RuleDependency), new NodeDependency(new ControlNodeReference("filter"), DependencyRole.Condition), "node"),
            (typeof(NativeLocator), new DocumentNativeLocator("project"), "document"),
            (typeof(NativeLocator), new JsonPointerNativeLocator("project", "/schema/customer.name"), "jsonPointer"),
            (typeof(NativeLocator), new GraphNodeNativeLocator("project", "9007199254740993", "18446744073709551615"), "graphNode"),
            (typeof(NativeLocator), new TextSpanNativeLocator("project", "12", "3", TextSpanUnit.UnicodeScalars), "textSpan"),
            (typeof(NativeLocator), new OpaqueNativeLocator("project", "example:scope", "scope/1"), "opaque"),
            (typeof(DetailNode), new GroupDetail("Arguments", [new LiteralDetail(new StringLiteralValue("a"))]), "group"),
            (typeof(DetailNode), new ExpressionDetail("choose", [new ReferenceDetail(new ParameterNodeReference("fallback"))]), "expression"),
            (typeof(DetailNode), new LiteralDetail(new BooleanLiteralValue(true)), "literal"),
            (typeof(DetailNode), new ReferenceDetail(new FieldNodeReference("source.name")), "reference"),
            (typeof(DetailNode), new UnsupportedDetail("external-rule", "Explanation unavailable", new DocumentNativeLocator("project")), "unsupported")
        ];

        foreach (var (contract, value, kind) in cases)
        {
            var json = JsonSerializer.Serialize(value, contract, Options);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(kind, document.RootElement.GetProperty("kind").GetString());
            var restored = JsonSerializer.Deserialize(json, contract, Options);
            Assert.NotNull(restored);
            Assert.Equal(value.GetType(), restored.GetType());
            Assert.Equal(json, JsonSerializer.Serialize(restored, contract, Options));
        }
    }

    [Fact]
    public void ExactNumbersAndNativeIdentitiesRemainStrings()
    {
        const string integer = "900719925474099312345678901234567890";
        const string decimalValue = "0.3000000000000000000000000000000000000001";
        const string negativeZeroBits = "8000000000000000";
        const string nativeId = "18446744073709551615";
        var projection = CreatePartialDocument() with
        {
            Rules =
            [
                new ProjectionRule(
                    nativeId,
                    "target.amount",
                    "Exact constants",
                    [
                        new ConstantDependency(new ExactNumberLiteralValue(ExactNumberFlavor.Integer, integer)),
                        new ConstantDependency(new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, decimalValue)),
                        new ConstantDependency(new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, negativeZeroBits))
                    ],
                    [],
                    new GraphNodeNativeLocator("project", nativeId, nativeId))
            ]
        };

        var json = JsonSerializer.Serialize(projection, Options);
        using var document = JsonDocument.Parse(json);
        var rule = document.RootElement.GetProperty("rules")[0];
        Assert.Equal(JsonValueKind.String, rule.GetProperty("id").ValueKind);
        Assert.Equal(nativeId, rule.GetProperty("id").GetString());
        Assert.Equal(nativeId, rule.GetProperty("nativeLocator").GetProperty("nodeId").GetString());
        Assert.Equal(nativeId, rule.GetProperty("nativeLocator").GetProperty("functionId").GetString());
        var dependencies = rule.GetProperty("dependencies");
        string[] expectedValues = [integer, decimalValue, negativeZeroBits];
        string[] expectedFlavors = ["integer", "decimal", "binaryFloat"];
        for (var index = 0; index < expectedValues.Length; index++)
        {
            var literal = dependencies[index].GetProperty("value");
            Assert.Equal(JsonValueKind.String, literal.GetProperty("value").ValueKind);
            Assert.Equal(expectedValues[index], literal.GetProperty("value").GetString());
            Assert.Equal(expectedFlavors[index], literal.GetProperty("flavor").GetString());
        }

        var restored = JsonSerializer.Deserialize<ProjectionDocumentV1>(json, Options);
        Assert.NotNull(restored);
        Assert.Equal(nativeId, restored.Rules[0].Id);
        Assert.Equal(nativeId, Assert.IsType<GraphNodeNativeLocator>(restored.Rules[0].NativeLocator).FunctionId);
        Assert.Equal(expectedValues, restored.Rules[0].Dependencies.Select(dependency =>
            Assert.IsType<ExactNumberLiteralValue>(Assert.IsType<ConstantDependency>(dependency).Value).Value));
    }

    [Fact]
    public void DottedFieldsAndNestedFieldsHaveDistinctTypedPaths()
    {
        IReadOnlyList<PathSegment> dotted = [new FieldPathSegment("customer.name")];
        IReadOnlyList<PathSegment> nested = [new FieldPathSegment("customer"), new FieldPathSegment("name")];
        IReadOnlyList<PathSegment> repeatedAttribute =
        [
            new FieldPathSegment("orders"),
            new AnyItemPathSegment(),
            new IndexPathSegment("2"),
            new AttributePathSegment("id", "urn:orders")
        ];

        var dottedJson = JsonSerializer.Serialize(dotted, Options);
        var nestedJson = JsonSerializer.Serialize(nested, Options);
        Assert.NotEqual(dottedJson, nestedJson);
        var restoredDotted = JsonSerializer.Deserialize<IReadOnlyList<PathSegment>>(dottedJson, Options);
        var restoredNested = JsonSerializer.Deserialize<IReadOnlyList<PathSegment>>(nestedJson, Options);
        Assert.NotNull(restoredDotted);
        Assert.NotNull(restoredNested);
        Assert.Equal("customer.name", Assert.IsType<FieldPathSegment>(Assert.Single(restoredDotted)).Name);
        Assert.Collection(restoredNested,
            segment => Assert.Equal("customer", Assert.IsType<FieldPathSegment>(segment).Name),
            segment => Assert.Equal("name", Assert.IsType<FieldPathSegment>(segment).Name));

        var repeatedJson = JsonSerializer.Serialize(repeatedAttribute, Options);
        var restoredRepeated = JsonSerializer.Deserialize<IReadOnlyList<PathSegment>>(repeatedJson, Options);
        Assert.NotNull(restoredRepeated);
        Assert.IsType<AnyItemPathSegment>(restoredRepeated[1]);
        Assert.Equal("2", Assert.IsType<IndexPathSegment>(restoredRepeated[2]).Index);
        Assert.Equal("urn:orders", Assert.IsType<AttributePathSegment>(restoredRepeated[3]).NamespaceUri);
    }

    [Fact]
    public void PartialCoverageRetainsGapsControlsDependencyRolesAndQualification()
    {
        var projection = CreatePartialDocument();
        var json = JsonSerializer.Serialize(projection, Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("braid.mapping-projection.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("partial", root.GetProperty("coverage").GetProperty("status").GetString());
        Assert.Equal("graphNode", root.GetProperty("coverage").GetProperty("gaps")[0].GetProperty("nativeLocator").GetProperty("kind").GetString());
        Assert.Equal("unknown", root.GetProperty("fields")[0].GetProperty("requirement").GetString());
        Assert.Equal("unknown", root.GetProperty("fields")[0].GetProperty("nullability").GetString());
        Assert.Equal("unknown", root.GetProperty("fields")[0].GetProperty("cardinality").GetProperty("kind").GetString());
        Assert.Equal("default", root.GetProperty("rules")[0].GetProperty("dependencies")[1].GetProperty("role").GetString());
        Assert.Equal("condition", root.GetProperty("rules")[0].GetProperty("dependencies")[2].GetProperty("role").GetString());
        Assert.Equal("notQualified", root.GetProperty("capabilities")[0].GetProperty("status").GetString());

        var restored = JsonSerializer.Deserialize<ProjectionDocumentV1>(json, Options);
        Assert.NotNull(restored);
        Assert.Equal(CoverageStatus.Partial, restored.Coverage.Status);
        Assert.Equal("unexplained-control", Assert.Single(restored.Coverage.Gaps).Code);
        Assert.Equal(projection.Provenance, restored.Provenance);
        Assert.Equal(DependencyRole.Default, Assert.IsType<ParameterDependency>(restored.Rules[0].Dependencies[1]).Role);
        Assert.Equal(DependencyRole.Condition, Assert.IsType<NodeDependency>(restored.Rules[0].Dependencies[2]).Role);
        Assert.IsType<UnsupportedDetail>(Assert.Single(restored.Controls[0].Details));
        Assert.IsType<ControlNodeReference>(Assert.Single(restored.Diagnostics).ProjectionLocator);
        Assert.Equal(CapabilityStatus.NotQualified, Assert.Single(restored.Capabilities).Status);
    }

    [Fact]
    public void MissingRequiredDataUnknownKindsAndNumericEnumsAreRejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuleDependency>("""{"kind":"field"}""", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuleDependency>("""{"kind":"field","fieldId":null}""", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NativeLocator>("""{"kind":"future","documentId":"project"}""", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CoverageStatus>("1", Options));
    }

    private static ProjectionDocumentV1 CreatePartialDocument()
    {
        var native = new GraphNodeNativeLocator("project", "18446744073709551615");
        var sourceLocator = new JsonPointerNativeLocator("project", "/source/fields/0");
        var targetLocator = new JsonPointerNativeLocator("project", "/target/fields/0");
        return new ProjectionDocumentV1(
            new ProjectionProvenance(
                new ScopedRevisionId("organization", "project", "mapping", "revision"),
                new ArtifactDigest("sha256", new string('a', 64)),
                new BackendIdentity("fixture", "fixture-revision", "fixture-build"),
                "adapter-v1",
                "projection-v1"),
            new ProjectionCoverage(CoverageStatus.Partial,
            [
                new CoverageGap("unexplained-control", "This control is retained without a complete explanation.", native, new ControlNodeReference("filter"))
            ]),
            [
                new ProjectionBoundary("source", "Input", BoundaryRole.PrimaryInput, "application/json", BoundaryShape.Object, null, new DocumentNativeLocator("project")),
                new ProjectionBoundary("target", "Output", BoundaryRole.PrimaryOutput, "application/json", BoundaryShape.Object, null, new DocumentNativeLocator("project"))
            ],
            [
                new ProjectionField("source.name", "source", [new FieldPathSegment("customer.name")], "customer.name",
                    new FieldDataType(FieldDataTypeKind.Scalar, [ScalarDataType.String]),
                    new FieldCardinality(CardinalityKind.Unknown), FieldRequirement.Unknown, FieldNullability.Unknown, sourceLocator),
                new ProjectionField("target.amount", "target", [new FieldPathSegment("amount")], "amount",
                    new FieldDataType(FieldDataTypeKind.Scalar, [ScalarDataType.Integer]),
                    new FieldCardinality(CardinalityKind.ExactlyOne), FieldRequirement.Required, FieldNullability.NonNullable, targetLocator)
            ],
            [
                new ProjectionParameter("fallback", "fallback", new FieldDataType(FieldDataTypeKind.Scalar, [ScalarDataType.Integer]),
                    FieldRequirement.Optional, new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "0"), native)
            ],
            [
                new ProjectionRule("rule.amount", "target.amount", "Choose a value or the runtime fallback.",
                [
                    new FieldDependency("source.name"),
                    new ParameterDependency("fallback", DependencyRole.Default),
                    new NodeDependency(new ControlNodeReference("filter"), DependencyRole.Condition)
                ],
                [
                    new ExpressionDetail("choose", [new ReferenceDetail(new FieldNodeReference("source.name")), new ReferenceDetail(new ParameterNodeReference("fallback"))])
                ], native)
            ],
            [
                new ProjectionControl("filter", ControlKind.Unknown, "Unexplained control", [new RuleNodeReference("rule.amount")], [],
                    [new UnsupportedDetail("fixture-control", "The fixture preserves the control's native location.", native)], native)
            ],
            [
                new ProjectionRelationship("reads-name", RelationshipKind.ReadsFrom, new RuleNodeReference("rule.amount"), new FieldNodeReference("source.name")),
                new ProjectionRelationship("writes-amount", RelationshipKind.WritesTo, new RuleNodeReference("rule.amount"), new FieldNodeReference("target.amount"))
            ],
            [
                new ProjectionDiagnostic("partial-explanation", DiagnosticSeverity.Warning, "A native construct needs an explanation.", native, new ControlNodeReference("filter"))
            ],
            [
                new OperationCapability(MappingOperation.Preview, CapabilityStatus.NotQualified, "fixture-json-v1", ["source", "target"],
                    [new CapabilityCondition("fixture-only", "This read model does not execute mappings.")])
            ]);
    }
}
