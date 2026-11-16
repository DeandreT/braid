using System.Globalization;
using System.Text;
using System.Text.Json;
using Braid.Api.Fixtures;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Tests;

public sealed class ProjectionFixtureTests
{
    private static readonly JsonSerializerOptions Options = ProjectionJson.CreateOptions();

    [Fact]
    public void FixtureInventoryIsExplicitAndUnknownFixturesAreRejected()
    {
        string[] expectedIds = ["direct", "rich", "partial", "unavailable"];
        Assert.Equal(expectedIds, ProjectionFixtures.Ids);
        Assert.False(ProjectionFixtures.TryGet("missing-fixture", out var document));
        Assert.Null(document);
        Assert.Throws<ArgumentException>(() => ProjectionFixtures.GetNativeArtifact("missing-fixture"));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("rich")]
    [InlineData("partial")]
    [InlineData("unavailable")]
    public void RepeatedProjectionIsDeterministicAndProducesFreshGraphs(string fixtureId)
    {
        var first = Get(fixtureId);
        var second = Get(fixtureId);
        var json = JsonSerializer.Serialize(first, Options);

        Assert.NotSame(first, second);
        Assert.Equal(json, JsonSerializer.Serialize(second, Options));
        var restored = JsonSerializer.Deserialize<ProjectionDocumentV1>(json, Options);
        Assert.NotNull(restored);
        Assert.Equal(json, JsonSerializer.Serialize(restored, Options));
        if (first.Fields.Count > 0)
        {
            Assert.NotSame(first.Fields, second.Fields);
            Assert.NotSame(first.Fields[0], second.Fields[0]);
            var nonRoot = first.Fields.FirstOrDefault(field => field.Path.Count > 0);
            if (nonRoot is not null)
                Assert.NotSame(nonRoot.Path, second.Fields.Single(field => field.Id == nonRoot.Id).Path);
        }
        if (first.Rules.Count > 0)
        {
            Assert.NotSame(first.Rules, second.Rules);
            Assert.NotSame(first.Rules[0], second.Rules[0]);
        }
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("rich")]
    [InlineData("partial")]
    [InlineData("unavailable")]
    public void ProjectionPreservesOwnedNativeBytesAndDigestProvenance(string fixtureId)
    {
        var artifact = ProjectionFixtures.GetNativeArtifact(fixtureId);
        var original = artifact.CopyBytes();
        var callerBuffer = artifact.CopyBytes();
        Assert.NotEmpty(original);
        Assert.NotSame(original, callerBuffer);
        callerBuffer[0] ^= 0xff;

        var projection = Get(fixtureId);

        Assert.Equal(original, artifact.CopyBytes());
        Assert.Equal(original, ProjectionFixtures.GetNativeArtifact(fixtureId).CopyBytes());
        Assert.Equal(original.Length, artifact.ByteCount);
        Assert.Equal(Sha256Digest.FromBytes(original), artifact.Digest);
        Assert.Equal("sha256", projection.Provenance.NativeArtifactDigest.Algorithm);
        Assert.Equal(artifact.Digest.Value, projection.Provenance.NativeArtifactDigest.Value);
        Assert.Equal(artifact.Scope.OrganizationId.ToString("D"), projection.Provenance.Revision.OrganizationId);
        Assert.Equal(artifact.Scope.ProjectId.ToString("D"), projection.Provenance.Revision.ProjectId);
        Assert.Equal(artifact.Scope.MappingId.ToString("D"), projection.Provenance.Revision.MappingId);
        Assert.Equal(artifact.Scope.RevisionId.ToString("D"), projection.Provenance.Revision.RevisionId);
        Assert.Equal("braid.synthetic", projection.Provenance.Backend.Id);
        Assert.False(string.IsNullOrWhiteSpace(projection.Provenance.Backend.BuildId));
        Assert.False(string.IsNullOrWhiteSpace(projection.Provenance.AdapterVersion));
        Assert.False(string.IsNullOrWhiteSpace(projection.Provenance.ProjectionVersion));
        Assert.Contains('\n', Encoding.UTF8.GetString(original));
    }

    [Fact]
    public void DirectFixtureKeepsDottedAndNestedFieldsDistinct()
    {
        var projection = Get("direct");
        var dotted = projection.Fields.Where(field => field.Path is [FieldPathSegment { Name: "customer.name" }]).ToArray();
        var nested = projection.Fields.Where(field => field.Path is
            [FieldPathSegment { Name: "customer" }, FieldPathSegment { Name: "name" }]).ToArray();

        Assert.NotEmpty(dotted);
        Assert.NotEmpty(nested);
        Assert.Contains(dotted, dot => nested.Any(path => path.BoundaryId == dot.BoundaryId));
        foreach (var dot in dotted)
        {
            foreach (var path in nested.Where(path => path.BoundaryId == dot.BoundaryId))
            {
                Assert.NotEqual(dot.Id, path.Id);
                Assert.NotEqual(JsonSerializer.Serialize(dot.Path, Options), JsonSerializer.Serialize(path.Path, Options));
            }
        }
        Assert.Contains(projection.Rules.SelectMany(rule => rule.Dependencies).OfType<FieldDependency>(),
            dependency => dotted.Any(field => field.Id == dependency.FieldId));
        Assert.Contains(projection.Rules.SelectMany(rule => rule.Dependencies).OfType<FieldDependency>(),
            dependency => nested.Any(field => field.Id == dependency.FieldId));
    }

    [Fact]
    public void RichFixtureRetainsTypedPathsScopedIdentitiesAndBoundaryOrder()
    {
        var projection = Get("rich");
        var segments = projection.Fields.SelectMany(field => field.Path).ToArray();
        Assert.Contains(segments, segment => segment is FieldPathSegment { NamespaceUri: not null });
        Assert.Contains(segments, segment => segment is AttributePathSegment { NamespaceUri: not null });
        Assert.Contains(segments, segment => segment is IndexPathSegment);
        Assert.Contains(segments, segment => segment is AnyItemPathSegment);
        Assert.Contains(projection.Fields, field => field.Path.Count == 0);

        var repeatedNativeIds = projection.Fields
            .Where(field => field.NativeLocator is GraphNodeNativeLocator)
            .GroupBy(field => ((GraphNodeNativeLocator)field.NativeLocator).NodeId)
            .Where(group => group.Select(field => field.BoundaryId).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToArray();
        Assert.NotEmpty(repeatedNativeIds);
        Assert.All(repeatedNativeIds, group =>
            Assert.Equal(group.Count(), group.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count()));

        Assert.Contains(projection.Boundaries, boundary => boundary.Role == BoundaryRole.PrimaryInput);
        Assert.Contains(projection.Boundaries, boundary => boundary.Role == BoundaryRole.NamedInput);
        var outputs = projection.Boundaries.Where(boundary => boundary.Role is BoundaryRole.PrimaryOutput or BoundaryRole.NamedOutput).ToArray();
        Assert.Equal(3, outputs.Length);
        Assert.Equal(BoundaryRole.PrimaryOutput, outputs[0].Role);
        Assert.All(outputs.Skip(1), boundary => Assert.Equal(BoundaryRole.NamedOutput, boundary.Role));
        Assert.Equal(outputs.Select(boundary => boundary.Id), Get("rich").Boundaries
            .Where(boundary => boundary.Role is BoundaryRole.PrimaryOutput or BoundaryRole.NamedOutput)
            .Select(boundary => boundary.Id));
    }

    [Theory]
    [InlineData(ExactNumberFlavor.Integer, "9007199254740993")]
    [InlineData(ExactNumberFlavor.Decimal, "0.1000000000000000000000000001")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "8000000000000000")]
    public void RichFixtureRetainsExactLiteralRepresentations(ExactNumberFlavor flavor, string expected)
    {
        var projection = Get("rich");
        var dependencies = projection.Rules.SelectMany(rule => rule.Dependencies)
            .Concat(projection.Controls.SelectMany(control => control.Dependencies));
        Assert.Contains(dependencies.OfType<ConstantDependency>(), dependency =>
            dependency.Value is ExactNumberLiteralValue number && number.Flavor == flavor && number.Value == expected);
        Assert.Contains(AllDetails(projection).OfType<LiteralDetail>(), detail =>
            detail.Value is ExactNumberLiteralValue number && number.Flavor == flavor && number.Value == expected);

        var json = JsonSerializer.Serialize(projection, Options);
        using var serialized = JsonDocument.Parse(json);
        Assert.Contains(JsonValues(serialized.RootElement), value =>
            value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("kind", out var kind) && kind.GetString() == "exactNumber" &&
            value.TryGetProperty("value", out var number) && number.ValueKind == JsonValueKind.String && number.GetString() == expected);
        var restored = JsonSerializer.Deserialize<ProjectionDocumentV1>(json, Options);
        Assert.NotNull(restored);
        Assert.Contains(AllDetails(restored).OfType<LiteralDetail>(), detail =>
            detail.Value is ExactNumberLiteralValue number && number.Flavor == flavor && number.Value == expected);
    }

    [Fact]
    public void RichFixtureExplainsParametersDependencyRolesAndAllControlKinds()
    {
        var projection = Get("rich");
        Assert.NotEmpty(projection.Parameters);
        Assert.Contains(projection.Parameters, parameter => parameter.DefaultValue is ExactNumberLiteralValue
        { Flavor: ExactNumberFlavor.Decimal, Value: "0.1000000000000000000000000001" });
        var dependencies = projection.Rules.SelectMany(rule => rule.Dependencies)
            .Concat(projection.Controls.SelectMany(control => control.Dependencies)).ToArray();
        Assert.Contains(dependencies, dependency => dependency is ParameterDependency);
        Assert.Contains(dependencies, dependency => dependency.Role == DependencyRole.Default);
        Assert.Contains(dependencies, dependency => dependency.Role == DependencyRole.Condition);
        Assert.Contains(projection.Rules, rule => rule.Dependencies.Count > 0 && rule.Dependencies.All(dependency => dependency is ConstantDependency));
        Assert.Contains(AllDetails(projection), detail => detail is ExpressionDetail);
        ControlKind[] expectedControls = [ControlKind.Iteration, ControlKind.Filter, ControlKind.Join, ControlKind.Grouping,
            ControlKind.Sorting, ControlKind.Window, ControlKind.Failure, ControlKind.Ordering];
        Assert.Equal(expectedControls, projection.Controls.Select(control => control.Kind));
    }

    [Fact]
    public void NativeLocatorsRetainLargeNodeIdsAndEscapedJsonPointers()
    {
        var projection = Get("direct");
        var nativeLocators = projection.Fields.Select(field => field.NativeLocator)
            .Concat(projection.Rules.Select(rule => rule.NativeLocator));
        Assert.Contains(nativeLocators, locator => locator is GraphNodeNativeLocator { NodeId: "9007199254740993" });
        Assert.Contains(nativeLocators, locator => locator is JsonPointerNativeLocator { Pointer: "/outputNodes/a~0b~1c" });

        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(projection, Options));
        Assert.Contains(JsonValues(serialized.RootElement), value =>
            value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("nodeId", out var nodeId) &&
            nodeId.ValueKind == JsonValueKind.String && nodeId.GetString() == "9007199254740993");
    }

    [Theory]
    [InlineData("direct", 256, 32)]
    [InlineData("rich", 256, 32)]
    [InlineData("partial", 256, 32)]
    [InlineData("unavailable", 256, 32)]
    [InlineData("rich", 1, 32)]
    [InlineData("partial", 256, 1)]
    public void EveryNativeLocatorResolvesIntoTheRetainedArtifact(string fixtureId, int maximumNodes, int maximumDiagnostics)
    {
        var projection = Get(fixtureId, maximumNodes, maximumDiagnostics);
        using var native = JsonDocument.Parse(ProjectionFixtures.GetNativeArtifact(fixtureId).CopyBytes());
        var locators = AllNativeLocators(projection).ToArray();
        Assert.NotEmpty(locators);
        foreach (var locator in locators)
        {
            Assert.Equal("entry.synthetic.json", locator.DocumentId);
            switch (locator)
            {
                case DocumentNativeLocator:
                    Assert.Equal(JsonValueKind.Object, native.RootElement.ValueKind);
                    break;
                case JsonPointerNativeLocator pointer:
                    Assert.Equal(JsonValueKind.Object, ResolveJsonPointer(native.RootElement, pointer.Pointer).ValueKind);
                    break;
                case GraphNodeNativeLocator node:
                    var matches = JsonValues(native.RootElement).Where(value =>
                        value.ValueKind == JsonValueKind.Object &&
                        (HasExactNodeId(value, "id", node.NodeId) || HasExactNodeId(value, "nativeId", node.NodeId)) &&
                        (node.FunctionId is null || value.TryGetProperty("function", out var function) &&
                            function.ValueKind == JsonValueKind.String && function.GetString() == node.FunctionId));
                    Assert.Single(matches);
                    break;
                default:
                    Assert.Fail($"No native evidence check exists for locator {locator}.");
                    break;
            }
        }
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("rich")]
    [InlineData("partial")]
    [InlineData("unavailable")]
    public void AllProjectedReferencesResolveAndRelationshipsMatchCanonicalReferences(string fixtureId)
    {
        var projection = Get(fixtureId);
        var ids = projection.Boundaries.Select(boundary => boundary.Id)
            .Concat(projection.Fields.Select(field => field.Id))
            .Concat(projection.Parameters.Select(parameter => parameter.Id))
            .Concat(projection.Rules.Select(rule => rule.Id))
            .Concat(projection.Controls.Select(control => control.Id)).ToArray();
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(projection.Relationships.Count, projection.Relationships.Select(relationship => relationship.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var boundary in projection.Boundaries)
        {
            if (boundary.RootFieldId is { } rootFieldId)
                Assert.Contains(projection.Fields, field => field.Id == rootFieldId && field.BoundaryId == boundary.Id);
        }
        foreach (var field in projection.Fields)
            AssertReference(projection, new BoundaryNodeReference(field.BoundaryId));
        foreach (var rule in projection.Rules)
        {
            AssertReference(projection, new FieldNodeReference(rule.OutputFieldId));
            var output = projection.Fields.Single(field => field.Id == rule.OutputFieldId);
            Assert.Contains(projection.Boundaries, boundary => boundary.Id == output.BoundaryId &&
                boundary.Role is BoundaryRole.PrimaryOutput or BoundaryRole.NamedOutput);
            AssertDependencies(projection, rule.Dependencies);
        }
        foreach (var control in projection.Controls)
        {
            Assert.All(control.AppliesTo, reference => AssertReference(projection, reference));
            AssertDependencies(projection, control.Dependencies);
        }
        Assert.All(AllDetails(projection).OfType<ReferenceDetail>(), detail => AssertReference(projection, detail.Reference));
        foreach (var gap in projection.Coverage.Gaps)
        {
            if (gap.ProjectionLocator is { } reference)
                AssertReference(projection, reference);
        }
        foreach (var diagnostic in projection.Diagnostics)
        {
            if (diagnostic.ProjectionLocator is { } reference)
                AssertReference(projection, reference);
        }
        foreach (var capability in projection.Capabilities)
            Assert.All(capability.BoundaryIds, boundaryId => AssertReference(projection, new BoundaryNodeReference(boundaryId)));
        foreach (var relationship in projection.Relationships)
        {
            AssertReference(projection, relationship.From);
            AssertReference(projection, relationship.To);
        }

        var actual = projection.Relationships.Select(relationship =>
            (relationship.Kind, From: ReferenceKey(relationship.From), To: ReferenceKey(relationship.To))).ToHashSet();
        Assert.True(ExpectedRelationships(projection).SetEquals(actual),
            "Relationships must agree with boundary ownership, outputs, dependencies, and control applicability.");
    }

    [Fact]
    public void PartialCoverageKeepsUnderstoodGraphAndLocatesItsGap()
    {
        var projection = Get("partial");
        Assert.Equal(CoverageStatus.Partial, projection.Coverage.Status);
        Assert.NotEmpty(projection.Fields);
        Assert.NotEmpty(projection.Rules);
        Assert.NotEmpty(projection.Coverage.Gaps);
        foreach (var gap in projection.Coverage.Gaps)
        {
            var reference = Assert.IsType<RuleNodeReference>(gap.ProjectionLocator);
            Assert.Contains(projection.Rules, rule => rule.Id == reference.RuleId);
            Assert.False(string.IsNullOrWhiteSpace(gap.Code));
            Assert.False(string.IsNullOrWhiteSpace(gap.Message));
            Assert.False(string.IsNullOrWhiteSpace(gap.NativeLocator.DocumentId));
            Assert.Contains(projection.Diagnostics, diagnostic =>
                diagnostic.NativeLocator == gap.NativeLocator && diagnostic.ProjectionLocator == gap.ProjectionLocator);
            Assert.Contains(AllDetails(projection).OfType<UnsupportedDetail>(), detail => detail.NativeLocator == gap.NativeLocator);
        }
    }

    [Theory]
    [InlineData("direct", CoverageStatus.Complete)]
    [InlineData("rich", CoverageStatus.Complete)]
    [InlineData("partial", CoverageStatus.Partial)]
    [InlineData("unavailable", CoverageStatus.Unavailable)]
    public void CoverageNeverQualifiesSyntheticExecution(string fixtureId, CoverageStatus expectedCoverage)
    {
        var projection = Get(fixtureId);
        Assert.Equal(expectedCoverage, projection.Coverage.Status);
        Assert.Equal(Enum.GetValues<MappingOperation>().Length, projection.Capabilities.Count);
        Assert.Equal(projection.Capabilities.Count, projection.Capabilities.Select(capability => capability.Operation).Distinct().Count());
        foreach (var operation in Enum.GetValues<MappingOperation>())
        {
            var capability = Assert.Single(projection.Capabilities, candidate => candidate.Operation == operation);
            Assert.False(string.IsNullOrWhiteSpace(capability.Profile));
            Assert.Equal(operation is MappingOperation.Inspect or MappingOperation.Project
                ? CapabilityStatus.Supported : CapabilityStatus.NotQualified, capability.Status);
            if (capability.Status == CapabilityStatus.NotQualified)
                Assert.NotEmpty(capability.Conditions);
        }
        if (expectedCoverage == CoverageStatus.Complete)
            Assert.Empty(projection.Coverage.Gaps);
        if (expectedCoverage == CoverageStatus.Unavailable)
        {
            AssertEmptyGraph(projection);
            Assert.NotEmpty(projection.Coverage.Gaps);
            Assert.NotEmpty(projection.Diagnostics);
        }
    }

    [Fact]
    public void NodeBudgetIncludesRelationshipsAndRejectsOnlyOverflow()
    {
        var complete = Get("rich");
        var required = complete.Boundaries.Count + complete.Fields.Count + complete.Parameters.Count +
            complete.Rules.Count + complete.Controls.Count + complete.Relationships.Count;
        Assert.True(required > 1);
        var exactBudget = Get("rich", maximumNodes: required);
        Assert.Equal(JsonSerializer.Serialize(complete, Options), JsonSerializer.Serialize(exactBudget, Options));

        var overflow = Get("rich", maximumNodes: required - 1);
        AssertLimitFailure(overflow, "projection.nodeLimit");
        AssertLimitFailure(Get("rich", maximumNodes: 1), "projection.nodeLimit");
    }

    [Fact]
    public void DiagnosticBudgetDoesNotSilentlyTruncateRequiredExplanations()
    {
        var partial = Get("partial");
        Assert.Equal(2, partial.Diagnostics.Count);
        var exactBudget = Get("partial", maximumDiagnostics: partial.Diagnostics.Count);
        Assert.Equal(JsonSerializer.Serialize(partial, Options), JsonSerializer.Serialize(exactBudget, Options));

        var overflow = Get("partial", maximumDiagnostics: 1);
        AssertLimitFailure(overflow, "projection.diagnosticLimit");
        Assert.Single(overflow.Diagnostics);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResourceBudgetsMustBePositive(int invalidLimit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectionFixtures.TryGet("direct", out _, maximumNodes: invalidLimit));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectionFixtures.TryGet("direct", out _, maximumDiagnostics: invalidLimit));
    }

    private static ProjectionDocumentV1 Get(string fixtureId, int maximumNodes = 256, int maximumDiagnostics = 32)
    {
        Assert.True(ProjectionFixtures.TryGet(fixtureId, out var document, maximumNodes, maximumDiagnostics));
        Assert.NotNull(document);
        return document;
    }

    private static void AssertLimitFailure(ProjectionDocumentV1 projection, string code)
    {
        Assert.Equal(CoverageStatus.Unavailable, projection.Coverage.Status);
        AssertEmptyGraph(projection);
        Assert.Contains(projection.Coverage.Gaps, gap => gap.Code == code);
        Assert.Contains(projection.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.All(projection.Coverage.Gaps, gap => Assert.Null(gap.ProjectionLocator));
        Assert.All(projection.Diagnostics, diagnostic => Assert.Null(diagnostic.ProjectionLocator));
        Assert.All(projection.Capabilities, capability => Assert.Empty(capability.BoundaryIds));
        Assert.All(projection.Capabilities.Where(capability => capability.Operation is MappingOperation.Validate or MappingOperation.Preview or MappingOperation.Build),
            capability => Assert.Equal(CapabilityStatus.NotQualified, capability.Status));
    }

    private static void AssertEmptyGraph(ProjectionDocumentV1 projection)
    {
        Assert.Empty(projection.Boundaries);
        Assert.Empty(projection.Fields);
        Assert.Empty(projection.Parameters);
        Assert.Empty(projection.Rules);
        Assert.Empty(projection.Controls);
        Assert.Empty(projection.Relationships);
    }

    private static void AssertReference(ProjectionDocumentV1 projection, ProjectionNodeReference reference)
    {
        var resolves = reference switch
        {
            BoundaryNodeReference boundary => projection.Boundaries.Any(node => node.Id == boundary.BoundaryId),
            FieldNodeReference field => projection.Fields.Any(node => node.Id == field.FieldId),
            ParameterNodeReference parameter => projection.Parameters.Any(node => node.Id == parameter.ParameterId),
            RuleNodeReference rule => projection.Rules.Any(node => node.Id == rule.RuleId),
            ControlNodeReference control => projection.Controls.Any(node => node.Id == control.ControlId),
            _ => false
        };
        Assert.True(resolves, $"Unresolved reference: {reference}");
    }

    private static void AssertDependencies(ProjectionDocumentV1 projection, IEnumerable<RuleDependency> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            switch (dependency)
            {
                case FieldDependency field:
                    AssertReference(projection, new FieldNodeReference(field.FieldId));
                    break;
                case ParameterDependency parameter:
                    AssertReference(projection, new ParameterNodeReference(parameter.ParameterId));
                    break;
                case NodeDependency node:
                    AssertReference(projection, node.Node);
                    break;
                case ConstantDependency:
                    break;
                default:
                    Assert.Fail($"Unknown dependency: {dependency}");
                    break;
            }
        }
    }

    private static string ReferenceKey(ProjectionNodeReference reference) => reference switch
    {
        BoundaryNodeReference boundary => $"boundary:{boundary.BoundaryId}",
        FieldNodeReference field => $"field:{field.FieldId}",
        ParameterNodeReference parameter => $"parameter:{parameter.ParameterId}",
        RuleNodeReference rule => $"rule:{rule.RuleId}",
        ControlNodeReference control => $"control:{control.ControlId}",
        _ => throw new ArgumentException("Unknown projection reference.", nameof(reference))
    };

    private static HashSet<(RelationshipKind Kind, string From, string To)> ExpectedRelationships(ProjectionDocumentV1 projection)
    {
        var expected = new HashSet<(RelationshipKind Kind, string From, string To)>();
        foreach (var field in projection.Fields)
            expected.Add((RelationshipKind.BoundaryMember, ReferenceKey(new FieldNodeReference(field.Id)), ReferenceKey(new BoundaryNodeReference(field.BoundaryId))));
        foreach (var rule in projection.Rules)
        {
            var from = new RuleNodeReference(rule.Id);
            expected.Add((RelationshipKind.WritesTo, ReferenceKey(from), ReferenceKey(new FieldNodeReference(rule.OutputFieldId))));
            AddDependencies(from, rule.Dependencies);
        }
        foreach (var control in projection.Controls)
        {
            var reference = new ControlNodeReference(control.Id);
            foreach (var target in control.AppliesTo)
                expected.Add((RelationshipKind.GovernedBy, ReferenceKey(target), ReferenceKey(reference)));
            AddDependencies(reference, control.Dependencies);
        }
        return expected;

        void AddDependencies(ProjectionNodeReference from, IEnumerable<RuleDependency> dependencies)
        {
            foreach (var dependency in dependencies)
            {
                switch (dependency)
                {
                    case FieldDependency field:
                        expected.Add((RelationshipKind.ReadsFrom, ReferenceKey(from), ReferenceKey(new FieldNodeReference(field.FieldId))));
                        break;
                    case ParameterDependency parameter:
                        expected.Add((RelationshipKind.DependsOn, ReferenceKey(from), ReferenceKey(new ParameterNodeReference(parameter.ParameterId))));
                        break;
                    case NodeDependency node:
                        expected.Add((RelationshipKind.DependsOn, ReferenceKey(from), ReferenceKey(node.Node)));
                        break;
                }
            }
        }
    }

    private static IEnumerable<DetailNode> AllDetails(ProjectionDocumentV1 projection) =>
        projection.Rules.SelectMany(rule => rule.Details)
            .Concat(projection.Controls.SelectMany(control => control.Details)).SelectMany(Descendants);

    private static IEnumerable<NativeLocator> AllNativeLocators(ProjectionDocumentV1 projection) =>
        projection.Boundaries.Select(boundary => boundary.NativeLocator)
            .Concat(projection.Fields.Select(field => field.NativeLocator))
            .Concat(projection.Parameters.Select(parameter => parameter.NativeLocator))
            .Concat(projection.Rules.Select(rule => rule.NativeLocator))
            .Concat(projection.Controls.Select(control => control.NativeLocator))
            .Concat(AllDetails(projection).OfType<UnsupportedDetail>().Select(detail => detail.NativeLocator))
            .Concat(projection.Coverage.Gaps.Select(gap => gap.NativeLocator))
            .Concat(projection.Diagnostics.Select(diagnostic => diagnostic.NativeLocator).OfType<NativeLocator>());

    private static bool HasExactNodeId(JsonElement value, string propertyName, string expected) =>
        value.TryGetProperty(propertyName, out var id) && (id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() == expected,
            JsonValueKind.Number => id.GetRawText() == expected,
            _ => false
        });

    private static JsonElement ResolveJsonPointer(JsonElement root, string pointer)
    {
        if (pointer.Length == 0)
            return root;
        Assert.True(pointer.StartsWith('/'), $"JSON Pointer must start with '/': {pointer}");
        var current = root;
        foreach (var encodedToken in pointer.Split('/').Skip(1))
        {
            var token = new StringBuilder();
            for (var index = 0; index < encodedToken.Length; index++)
            {
                var character = encodedToken[index];
                if (character == '~')
                {
                    Assert.True(index + 1 < encodedToken.Length && encodedToken[index + 1] is '0' or '1',
                        $"Invalid JSON Pointer escape: {pointer}");
                    character = encodedToken[++index] == '0' ? '~' : '/';
                }
                token.Append(character);
            }
            var decodedToken = token.ToString();
            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    Assert.True(current.TryGetProperty(decodedToken, out var target), $"Missing native JSON Pointer target: {pointer}");
                    current = target;
                    break;
                case JsonValueKind.Array:
                    Assert.True(decodedToken.Length > 0 && (decodedToken == "0" || decodedToken[0] != '0'),
                        $"Invalid JSON Pointer array index: {pointer}");
                    Assert.True(int.TryParse(decodedToken, NumberStyles.None, CultureInfo.InvariantCulture, out var arrayIndex),
                        $"Invalid JSON Pointer array index: {pointer}");
                    Assert.True(arrayIndex >= 0 && arrayIndex < current.GetArrayLength(), $"Missing native JSON Pointer array item: {pointer}");
                    current = current[arrayIndex];
                    break;
                default:
                    Assert.Fail($"JSON Pointer traverses a primitive native value: {pointer}");
                    break;
            }
        }
        return current;
    }

    private static IEnumerable<DetailNode> Descendants(DetailNode detail)
    {
        yield return detail;
        var children = detail switch
        {
            GroupDetail group => group.Children,
            ExpressionDetail expression => expression.Arguments,
            _ => []
        };
        foreach (var child in children)
        {
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static IEnumerable<JsonElement> JsonValues(JsonElement value)
    {
        yield return value;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                foreach (var child in JsonValues(property.Value))
                    yield return child;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var child in JsonValues(item))
                    yield return child;
            }
        }
    }
}
