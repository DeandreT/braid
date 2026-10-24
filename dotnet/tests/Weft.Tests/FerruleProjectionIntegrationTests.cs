using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class FerruleProjectionIntegrationTests
{
    private static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualDirectProjectionPreservesDottedAndNestedDependenciesAndIsDeterministic()
    {
        var artifact = Artifact("valid-basic");
        var before = artifact.CopyBytes();
        var backend = Backend();
        var first = await backend.ProjectAsync(artifact);
        var second = await backend.ProjectAsync(artifact);
        var document = Successful(first, artifact);
        Assert.Equal(JsonSerializer.Serialize(document, ProjectionJson.CreateOptions()),
            JsonSerializer.Serialize(Successful(second, artifact), ProjectionJson.CreateOptions()));
        Assert.Equal(before, artifact.CopyBytes());

        var input = Assert.Single(document.Boundaries, boundary => boundary.Role == BoundaryRole.PrimaryInput);
        var output = Assert.Single(document.Boundaries, boundary => boundary.Role == BoundaryRole.PrimaryOutput);
        foreach (var (sourcePath, targetPath) in new[]
        {
            (new[] { "code" }, new[] { "code" }),
            (new[] { "a.b" }, new[] { "dotted" }),
            (new[] { "nested", "b" }, new[] { "nestedOutput", "b" })
        })
        {
            var source = Field(document, input.Id, sourcePath);
            var target = Field(document, output.Id, targetPath);
            var rule = Assert.Single(document.Rules, candidate => candidate.OutputFieldId == target.Id);
            var dependency = Assert.Single(rule.Dependencies.OfType<FieldDependency>());
            Assert.Equal(source.Id, dependency.FieldId);
            Assert.Equal(DependencyRole.Value, dependency.Role);
            Assert.Contains(document.Relationships, link => link.Kind == RelationshipKind.ReadsFrom
                && link.From is RuleNodeReference { RuleId: var from } && from == rule.Id
                && link.To is FieldNodeReference { FieldId: var to } && to == source.Id);
            Assert.Contains(document.Relationships, link => link.Kind == RelationshipKind.WritesTo
                && link.From is RuleNodeReference { RuleId: var from } && from == rule.Id
                && link.To is FieldNodeReference { FieldId: var to } && to == target.Id);
        }
        AssertNativeLocators(document, before);
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualVersionedProjectionRetainsExactIntegerIntegralFloatAndNegativeZero()
    {
        var artifact = Artifact("versioned-exact");
        var document = Successful(await Backend().ProjectAsync(artifact), artifact);
        var output = Assert.Single(document.Boundaries, boundary => boundary.Role == BoundaryRole.PrimaryOutput);
        foreach (var (name, flavor, value) in new[]
        {
            ("exactInteger", ExactNumberFlavor.Integer, "9007199254740993"),
            ("integralFloat", ExactNumberFlavor.BinaryFloat, "3ff0000000000000"),
            ("negativeZero", ExactNumberFlavor.BinaryFloat, "8000000000000000")
        })
        {
            var field = Field(document, output.Id, [name]);
            var rule = Assert.Single(document.Rules, candidate => candidate.OutputFieldId == field.Id);
            var literal = Assert.IsType<ExactNumberLiteralValue>(Assert.Single(rule.Dependencies.OfType<ConstantDependency>()).Value);
            Assert.Equal(flavor, literal.Flavor);
            Assert.Equal(value, literal.Value);
        }
        Assert.All(document.Fields, field =>
        {
            if (field.NativeLocator is JsonPointerNativeLocator pointer)
                Assert.StartsWith("/document/", pointer.Pointer, StringComparison.Ordinal);
        });
        AssertNativeLocators(document, artifact.CopyBytes());
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualProjectionPreservesConditionDefaultAndDistinctNullSemantics()
    {
        var artifact = Artifact("projection-basic");
        var document = Successful(await Backend().ProjectAsync(artifact), artifact);
        var input = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryInput);
        var output = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryOutput);
        ProjectionRule Rule(string name) => Assert.Single(document.Rules, rule => rule.OutputFieldId == Field(document, output.Id, [name]).Id);
        var enabled = Field(document, input.Id, ["enabled"]);
        var code = Field(document, input.Id, ["code"]);
        Assert.Contains(Rule("conditional").Dependencies, dependency => dependency is FieldDependency field
            && field.FieldId == enabled.Id && field.Role == DependencyRole.Condition);
        Assert.Contains(Rule("conditional").Dependencies, dependency => dependency is FieldDependency field
            && field.FieldId == code.Id && field.Role == DependencyRole.Value);
        var parameter = Assert.Single(document.Parameters, value => value.Name == "fallbackCode");
        Assert.Contains(Rule("parameter").Dependencies, dependency => dependency is ParameterDependency value && value.ParameterId == parameter.Id);
        Assert.Contains(Rule("parameter").Dependencies, dependency => dependency is ConstantDependency
        { Role: DependencyRole.Default, Value: StringLiteralValue { Value: "fallback" } });
        foreach (var (name, kind) in new[]
        {
            ("absent", NullLiteralKind.Absent), ("explicitNull", NullLiteralKind.ExplicitNull), ("xmlNil", NullLiteralKind.XmlNil)
        })
            Assert.Equal(kind, Assert.IsType<NullLiteralValue>(Assert.Single(Rule(name).Dependencies.OfType<ConstantDependency>()).Value).NullKind);
        AssertNativeLocators(document, artifact.CopyBytes());
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualProjectionPreservesNamedOutputOrderAndImplicitJoinAndAggregateDependencies()
    {
        var artifact = Artifact("projection-controls");
        var document = Successful(await Backend().ProjectAsync(artifact), artifact);
        Assert.Equal(new[] { "Output", "audit", "summary" },
            document.Boundaries.Where(value => value.Role is BoundaryRole.PrimaryOutput or BoundaryRole.NamedOutput).Select(value => value.Name));
        foreach (var kind in new[] { ControlKind.Iteration, ControlKind.Filter, ControlKind.Join, ControlKind.Grouping,
            ControlKind.Sorting, ControlKind.Window, ControlKind.Ordering, ControlKind.Failure })
            Assert.Contains(document.Controls, value => value.Kind == kind);
        var input = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryInput);
        var catalog = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.NamedInput && value.Name == "catalog");
        var output = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryOutput);
        var join = Assert.Single(document.Controls, value => value.Kind == ControlKind.Join);
        foreach (var field in new[] { Field(document, input.Id, ["orders", "orderId"]), Field(document, input.Id, ["orders", "region"]),
            Field(document, catalog.Id, ["orderId"]), Field(document, catalog.Id, ["region"]) })
            Assert.Contains(join.Dependencies, dependency => dependency is FieldDependency value && value.FieldId == field.Id);
        var amount = Field(document, input.Id, ["orders", "amount"]);
        var total = Assert.Single(document.Rules, rule => rule.OutputFieldId == Field(document, output.Id, ["total"]).Id);
        Assert.Contains(total.Dependencies, dependency => dependency is FieldDependency value && value.FieldId == amount.Id);
        var label = Field(document, catalog.Id, ["label"]);
        var joinedLabel = Assert.Single(document.Rules, rule => rule.OutputFieldId == Field(document, output.Id, ["joined", "label"]).Id);
        Assert.Contains(joinedLabel.Dependencies, dependency => dependency is FieldDependency value && value.FieldId == label.Id);
        AssertNativeLocators(document, artifact.CopyBytes());
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualReusableFunctionKeepsPrivateParametersOutOfHostInputsAndRetainsCallDependencies()
    {
        var artifact = Artifact("projection-functions");
        var document = Successful(await Backend().ProjectAsync(artifact), artifact);
        Assert.Empty(document.Parameters);
        Assert.Contains(document.Controls, value => value.Kind == ControlKind.Construction
            && value.Summary.Contains("prefixCode", StringComparison.Ordinal));
        var input = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryInput);
        var output = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryOutput);
        var code = Field(document, input.Id, ["code"]);
        foreach (var (name, prefix) in new[] { ("formatted", ">"), ("other", "!") })
        {
            var rule = Assert.Single(document.Rules, value => value.OutputFieldId == Field(document, output.Id, [name]).Id);
            Assert.Contains(rule.Dependencies, dependency => dependency is FieldDependency value && value.FieldId == code.Id);
            Assert.Contains(rule.Dependencies, dependency => dependency is ConstantDependency { Value: StringLiteralValue literal } && literal.Value == prefix);
        }
        Assert.Contains("18446744073709551615", JsonSerializer.Serialize(document, ProjectionJson.CreateOptions()), StringComparison.Ordinal);
        AssertNativeLocators(document, artifact.CopyBytes());
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualPartialProjectionKeepsDynamicGapsAndScopeSpecificLineageVisible()
    {
        var artifact = Artifact("projection-partial");
        var document = Successful(await Backend().ProjectAsync(artifact), artifact);
        Assert.Equal(CoverageStatus.Partial, document.Coverage.Status);
        Assert.NotEmpty(document.Coverage.Gaps);
        var input = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryInput);
        var output = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryOutput);
        foreach (var (sourcePath, targetPath) in new[]
        {
            (new[] { "code" }, new[] { "outerCode" }), (new[] { "items", "code" }, new[] { "rows", "code" })
        })
        {
            var source = Field(document, input.Id, sourcePath);
            var rule = Assert.Single(document.Rules, value => value.OutputFieldId == Field(document, output.Id, targetPath).Id);
            Assert.Contains(rule.Dependencies, dependency => dependency is FieldDependency field && field.FieldId == source.Id);
        }
        var dynamicRule = Assert.Single(document.Rules, value => value.OutputFieldId == Field(document, output.Id, ["dynamicValue"]).Id);
        Assert.Contains(dynamicRule.Details.SelectMany(DetailLocators), value => value is GraphNodeNativeLocator { NodeId: "3", FunctionId: null });
        AssertNativeLocators(document, artifact.CopyBytes());
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualJoinPreservesRawSourcePositionAndFramedOwnershipWithoutFalseOuterLineage()
    {
        var artifact = Artifact("projection-join-context");
        var backend = Backend();
        Assert.True((await backend.ValidateAsync(artifact)).IsValid);
        var document = Successful(await backend.ProjectAsync(artifact), artifact);
        var input = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryInput);
        var output = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryOutput);
        ProjectionRule Rule(string name) => Assert.Single(document.Rules,
            value => value.OutputFieldId == Field(document, output.Id, ["rows", name]).Id);
        var join = Assert.Single(document.Controls, value => value.Kind == ControlKind.Join);
        foreach (var name in new[] { "rawRight", "flattened" })
        {
            var rule = Rule(name);
            Assert.Contains(rule.Dependencies, value => value is NodeDependency
            { Node: ControlNodeReference reference } && reference.ControlId == join.Id);
            Assert.DoesNotContain("defaults to one", JsonSerializer.Serialize(rule, ProjectionJson.CreateOptions()), StringComparison.Ordinal);
        }
        var rightCode = Field(document, input.Id, ["right", "code"]);
        Assert.Equal(rightCode.Id, Assert.Single(Rule("framedCode").Dependencies.OfType<FieldDependency>()).FieldId);
        var uncertain = Rule("code");
        Assert.Equal(CoverageStatus.Partial, document.Coverage.Status);
        Assert.Empty(uncertain.Dependencies.OfType<FieldDependency>());
        var gap = Assert.Single(document.Coverage.Gaps,
            value => value.NativeLocator is GraphNodeNativeLocator { NodeId: "3" });
        Assert.Null(gap.ProjectionLocator);
        Assert.Contains("not definite dependencies", JsonSerializer.Serialize(uncertain.Details, ProjectionJson.CreateOptions()), StringComparison.Ordinal);
        AssertNativeLocators(document, artifact.CopyBytes());
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualOwnedFormsReadOnlyTheirExactEndpointsAndKeepClosedReadsPartial()
    {
        var artifact = Artifact("projection-owned-context");
        var backend = Backend();
        Assert.True((await backend.ValidateAsync(artifact)).IsValid);
        var document = Successful(await backend.ProjectAsync(artifact), artifact);
        var input = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryInput);
        var lookup = document.Boundaries.Where(value => value.Role == BoundaryRole.NamedInput).First();
        var output = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryOutput);
        ProjectionRule Rule(params string[] path) => Assert.Single(document.Rules,
            value => value.OutputFieldId == Field(document, output.Id, path).Id);
        var join = Assert.Single(document.Controls, value => value.Kind == ControlKind.Join);
        Assert.Equal("Ordered owned inner join", join.Summary);
        var joinReads = join.Dependencies.OfType<FieldDependency>().Select(value => value.FieldId).ToHashSet();
        Assert.Contains(Field(document, input.Id, ["orders", "customer"]).Id, joinReads);
        Assert.Contains(Field(document, lookup.Id, ["orders", "id"]).Id, joinReads);
        Assert.Equal(Field(document, lookup.Id, ["orders", "name"]).Id,
            Assert.Single(Rule("rows", "name").Dependencies.OfType<FieldDependency>()).FieldId);
        Assert.Contains(Rule("rows", "position").Dependencies, value => value is NodeDependency
        { Node: ControlNodeReference reference } && reference.ControlId == join.Id);
        Assert.Empty(Rule("rows", "plain").Dependencies.OfType<FieldDependency>());
        Assert.Contains(document.Coverage.Gaps, value => value.NativeLocator is GraphNodeNativeLocator { NodeId: "3" });
        var presence = Assert.Single(document.Controls, value => value.Kind == ControlKind.Iteration && value.Summary == "Optional presence");
        Assert.Equal(Field(document, input.Id, ["flag"]).Id, Assert.Single(presence.Dependencies.OfType<FieldDependency>()).FieldId);
        Assert.Equal(Field(document, input.Id, ["code"]).Id,
            Assert.Single(Rule("maybe", "mapped").Dependencies.OfType<FieldDependency>()).FieldId);
        var failure = Assert.Single(document.Controls, value => value.Kind == ControlKind.Failure);
        Assert.Contains(failure.Dependencies, value => value is FieldDependency { FieldId: var id } && id == Field(document, lookup.Id, ["orders", "name"]).Id);
        Assert.DoesNotContain(failure.Dependencies, value => value is FieldDependency { FieldId: var id } && id == Field(document, input.Id, ["orders", "name"]).Id);
        foreach (var code in new[] { "schema.objectItemNullability", "schema.undeclaredProperty" })
            Assert.Contains(document.Coverage.Gaps, value => value.Code == code);
        Assert.Equal(CoverageStatus.Partial, document.Coverage.Status);
        AssertNativeLocators(document, artifact.CopyBytes());
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualInnerScalarSourceShadowsAnOuterGeneratedItem()
    {
        var artifact = Artifact("projection-generated-source-context");
        var backend = Backend();
        Assert.True((await backend.ValidateAsync(artifact)).IsValid);
        var document = Successful(await backend.ProjectAsync(artifact), artifact);
        var input = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryInput);
        var output = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryOutput);
        var numbers = Field(document, input.Id, ["numbers"]);
        var inner = Assert.Single(document.Rules, value => value.OutputFieldId == Field(document, output.Id, ["outer", "inner"]).Id);
        Assert.Equal(numbers.Id, Assert.Single(inner.Dependencies.OfType<FieldDependency>()).FieldId);
        var generated = Assert.Single(document.Controls, value => value.Kind == ControlKind.Iteration && value.Summary == "Generated scalar sequence");
        Assert.DoesNotContain(inner.Dependencies, value => value is NodeDependency
        { Node: ControlNodeReference reference } && reference.ControlId == generated.Id);
        AssertNativeLocators(document, artifact.CopyBytes());
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualRepeatingScalarPreservesItemTypeMinimumOnlyRangeAndBindingAppendOrder()
    {
        var artifact = NativeArtifact.Admit(Scope, """
            {
              "source":{"name":"Source","kind":{"kind":"group","children":[]}},
              "target":{"name":"Target","kind":{"kind":"group","children":[
                {"name":"codes","repeating":true,"item_count_range":{"minimum":2},
                 "kind":{"kind":"scalar","ty":"string"}}
              ]}},
              "source_options":{"json_document":true},
              "target_options":{"json_document":true},
              "graph":{"nodes":{
                "1":{"kind":"const","value":"a"},
                "2":{"kind":"const","value":"b"}
              }},
              "root":{"target_field":"","bindings":[
                {"target_field":"codes","node":1},
                {"target_field":"codes","node":2}
              ],"children":[]}
            }
            """u8, InspectionPolicy.MaximumNativeBytes);
        var backend = Backend();
        Assert.True((await backend.ValidateAsync(artifact)).IsValid);
        var document = Successful(await backend.ProjectAsync(artifact), artifact);
        var output = Assert.Single(document.Boundaries, value => value.Role == BoundaryRole.PrimaryOutput);
        var codes = Field(document, output.Id, ["codes"]);
        Assert.Equal(FieldDataTypeKind.Array, codes.DataType.Kind);
        Assert.Equal(new[] { ScalarDataType.String }, codes.DataType.ScalarTypes);
        Assert.Equal(CardinalityKind.Bounded, codes.Cardinality.Kind);
        Assert.Equal("2", codes.Cardinality.Minimum);
        Assert.Null(codes.Cardinality.Maximum);
        var rules = document.Rules.Where(value => value.OutputFieldId == codes.Id).ToArray();
        Assert.Equal(2, rules.Length);
        Assert.NotEqual(rules[0].Id, rules[1].Id);
        Assert.Equal(new[] { "a", "b" }, rules.Select(rule => Assert.IsType<StringLiteralValue>(
            Assert.Single(rule.Dependencies.OfType<ConstantDependency>()).Value).Value));
        AssertNativeLocators(document, artifact.CopyBytes());
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualProjectionRefusesAnExactLiteralBeyondTheDisplayPolicyWithoutPublishingAPartialGraph()
    {
        // The test creates a new native artifact; application transport never
        // reparses or rewrites an admitted artifact.
        var native = JsonNode.Parse(Artifact("valid-basic").CopyBytes())!;
        native["graph"]!["nodes"]!["1"] = new JsonObject
        {
            ["kind"] = "const",
            ["value"] = new string('x', InspectionPolicy.MaximumTextBytes + 1)
        };
        var artifact = NativeArtifact.Admit(Scope, JsonSerializer.SerializeToUtf8Bytes(native), InspectionPolicy.MaximumNativeBytes);
        var result = await Backend().ProjectAsync(artifact);
        Assert.Equal(MappingOperationStatus.ResourceLimit, result.Status);
        Assert.True(result.Provenance.Confirmed);
        Assert.Null(result.Projection);
        Assert.NotEmpty(result.Diagnostics);
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualProjectionNormalizesNativeDecodeFailureWithMatchingArtifactProvenance()
    {
        var artifact = NativeArtifact.Admit(Scope, "{invalid"u8, InspectionPolicy.MaximumNativeBytes);
        var result = await Backend().ProjectAsync(artifact);
        Assert.Equal(MappingOperationStatus.InvalidMapping, result.Status);
        Assert.True(result.Provenance.Confirmed);
        Assert.Equal(artifact.Scope, result.Provenance.Scope);
        Assert.Equal(artifact.Digest, result.Provenance.NativeDigest);
        Assert.Null(result.Projection);
        Assert.NotEmpty(result.Diagnostics);
    }

    private static ProjectionDocumentV1 Successful(MappingProjectionResult result, NativeArtifact artifact)
    {
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        Assert.True(result.Provenance.Confirmed);
        var document = Assert.IsType<ProjectionDocumentV1>(result.Projection);
        Assert.Equal("weft.mapping-projection.v1", document.SchemaVersion);
        Assert.Equal(artifact.Scope.OrganizationId.ToString("D"), document.Provenance.Revision.OrganizationId);
        Assert.Equal(artifact.Scope.ProjectId.ToString("D"), document.Provenance.Revision.ProjectId);
        Assert.Equal(artifact.Scope.MappingId.ToString("D"), document.Provenance.Revision.MappingId);
        Assert.Equal(artifact.Scope.RevisionId.ToString("D"), document.Provenance.Revision.RevisionId);
        Assert.Equal("sha256", document.Provenance.NativeArtifactDigest.Algorithm);
        Assert.Equal(artifact.Digest.Value, document.Provenance.NativeArtifactDigest.Value);
        Assert.Equal("ferrule", document.Provenance.Backend.Id);
        Assert.Equal(FerruleInspectionBackend.EngineRevision, document.Provenance.Backend.Revision);
        Assert.Equal(Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE"), document.Provenance.Backend.BuildId);
        Assert.Equal(FerruleInspectionBackend.AdapterBuild, document.Provenance.AdapterVersion);
        Assert.False(string.IsNullOrWhiteSpace(document.Provenance.ProjectionVersion));
        Assert.Equal(CapabilityStatus.Supported,
            Assert.Single(document.Capabilities, capability => capability.Operation == MappingOperation.Project).Status);
        Assert.All(document.Capabilities.Where(capability => capability.Operation is MappingOperation.Preview or MappingOperation.Build),
            capability => Assert.Equal(CapabilityStatus.NotQualified, capability.Status));
        return document;
    }

    private static ProjectionField Field(ProjectionDocumentV1 document, string boundaryId, string[] path) =>
        Assert.Single(document.Fields, field => field.BoundaryId == boundaryId
            && field.Path.All(segment => segment is FieldPathSegment or AnyItemPathSegment)
            && field.Path.OfType<FieldPathSegment>().Select(segment => segment.Name).SequenceEqual(path));

    private static NativeArtifact Artifact(string fixture) => NativeArtifact.Admit(Scope,
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", fixture + ".json")), InspectionPolicy.MaximumNativeBytes);

    private static FerruleInspectionBackend Backend() => new(new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions(
        "/usr/bin/docker", Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!)));

    private static void AssertNativeLocators(ProjectionDocumentV1 document, byte[] bytes)
    {
        using var native = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 256 });
        var locators = document.Boundaries.Select(value => value.NativeLocator)
            .Concat(document.Fields.Select(value => value.NativeLocator))
            .Concat(document.Parameters.Select(value => value.NativeLocator))
            .Concat(document.Rules.Select(value => value.NativeLocator))
            .Concat(document.Controls.Select(value => value.NativeLocator))
            .Concat(document.Coverage.Gaps.Select(value => value.NativeLocator))
            .Concat(document.Diagnostics.Where(value => value.NativeLocator is not null).Select(value => value.NativeLocator!))
            .Concat(document.Rules.SelectMany(value => value.Details).Concat(document.Controls.SelectMany(value => value.Details)).SelectMany(DetailLocators));
        foreach (var locator in locators)
        {
            Assert.Equal(InspectionPolicy.DocumentId, locator.DocumentId);
            switch (locator)
            {
                case DocumentNativeLocator: break;
                case JsonPointerNativeLocator pointer:
                    var selected = native.RootElement;
                    if (pointer.Pointer.Length != 0)
                    {
                        Assert.StartsWith("/", pointer.Pointer, StringComparison.Ordinal);
                        foreach (var part in pointer.Pointer[1..].Split('/'))
                        {
                            var key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                            selected = selected.ValueKind == JsonValueKind.Array
                                ? selected[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)]
                                : selected.GetProperty(key);
                        }
                    }
                    Assert.NotEqual(JsonValueKind.Undefined, selected.ValueKind);
                    break;
                case GraphNodeNativeLocator graph:
                    var project = native.RootElement.TryGetProperty("__ferrule_file", out _) ? native.RootElement.GetProperty("document") : native.RootElement;
                    var owner = graph.FunctionId is null ? project.GetProperty("graph")
                        : project.GetProperty("user_functions").GetProperty(graph.FunctionId).GetProperty("body");
                    Assert.NotEqual(JsonValueKind.Undefined, owner.GetProperty("nodes").GetProperty(graph.NodeId).ValueKind);
                    break;
                default: throw new InvalidOperationException("The native Ferrule worker emitted an unexpected locator kind.");
            }
        }
    }

    private static IEnumerable<NativeLocator> DetailLocators(DetailNode node) => node switch
    {
        UnsupportedDetail unsupported => [unsupported.NativeLocator],
        GroupDetail group => group.Children.SelectMany(DetailLocators),
        ExpressionDetail expression => expression.Arguments.SelectMany(DetailLocators),
        _ => []
    };
}
