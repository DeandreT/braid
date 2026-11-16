using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

/// <summary>Independent host transcripts qualify projection admission; live workers are tested separately.</summary>
public sealed class FerruleProjectionBackendTests
{
    private const string WorkerBuild = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly JsonSerializerOptions Options = ProjectionJson.CreateOptions();
    private static readonly JsonSerializerOptions WireOptions = new() { MaxDepth = 256 };
    private static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [Fact]
    public async Task ProjectionUsesTrustedProvenanceAndPreservesTheNativeRequestBytes()
    {
        var native = Encoding.UTF8.GetBytes("\uFEFF{\n  \"unknown\": \"a/b~c\", \"number\": 9007199254740993, \"bits\": \"8000000000000000\"\n}\n");
        var artifact = Artifact(native);
        var runner = ReplyRunner();
        IMappingProjectionBackend backend = new FerruleInspectionBackend(runner);
        using var cancellation = new CancellationTokenSource();

        var result = await backend.ProjectAsync(artifact, cancellation.Token);

        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        AssertProvenance(result.Provenance, artifact, true);
        Assert.Empty(result.Diagnostics);
        var projection = Assert.IsType<ProjectionDocumentV1>(result.Projection);
        Assert.Equal("braid.mapping-projection.v1", projection.SchemaVersion);
        Assert.Equal(new ScopedRevisionId(Scope.OrganizationId.ToString("D"), Scope.ProjectId.ToString("D"),
            Scope.MappingId.ToString("D"), Scope.RevisionId.ToString("D")), projection.Provenance.Revision);
        Assert.Equal(new ArtifactDigest("sha256", artifact.Digest.Value), projection.Provenance.NativeArtifactDigest);
        Assert.Equal(new BackendIdentity("ferrule", FerruleInspectionBackend.EngineRevision, WorkerBuild), projection.Provenance.Backend);
        Assert.Equal(FerruleInspectionBackend.AdapterBuild, projection.Provenance.AdapterVersion);
        Assert.Equal(FerruleInspectionBackend.ProjectionVersion, projection.Provenance.ProjectionVersion);
        Assert.DoesNotContain("untrusted", JsonSerializer.Serialize(projection.Provenance, Options), StringComparison.Ordinal);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(cancellation.Token, runner.LastCancellationToken);
        var frame = Assert.IsType<byte[]>(runner.LastRequest);
        var headerLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame));
        Assert.InRange(headerLength, 1, InspectionPolicy.MaximumHeaderBytes);
        var header = RequestHeader(frame);
        Assert.Equal("project", header["operation"]!.GetValue<string>());
        Assert.Equal(FerruleInspectionBackend.ProtocolVersion, header["protocolVersion"]!.GetValue<string>());
        Assert.Equal(InspectionPolicy.DocumentId, header["documentId"]!.GetValue<string>());
        Assert.Equal(artifact.Digest.Value, header["nativeDigest"]!.GetValue<string>());
        Assert.True(Guid.TryParseExact(header["requestId"]!.GetValue<string>(), "D", out var requestId));
        Assert.NotEqual(Guid.Empty, requestId);
        Assert.Equal(Scope.RevisionId.ToString("D"), header["revision"]!["revisionId"]!.GetValue<string>());
        Assert.Equal(InspectionPolicy.MaximumDiagnostics, header["limits"]!["maximumDiagnostics"]!.GetValue<int>());
        Assert.Equal(InspectionPolicy.MaximumItems, header["limits"]!["maximumItems"]!.GetValue<int>());
        Assert.Equal(InspectionPolicy.MaximumTextBytes, header["limits"]!["maximumTextBytes"]!.GetValue<int>());
        Assert.Equal((uint)native.Length, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4 + headerLength)));
        Assert.Equal(8 + headerLength + native.Length, frame.Length);
        Assert.Equal(native, frame[(8 + headerLength)..]);
        Assert.Equal(native, artifact.CopyBytes());
    }

    [Fact]
    public async Task ProjectionRetainsTypedPathsExactNumbersAndRecursiveExplanationReferences()
    {
        var result = await new FerruleInspectionBackend(ReplyRunner()).ProjectAsync(Artifact());
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        var projection = Assert.IsType<ProjectionDocumentV1>(result.Projection);
        var literalPath = projection.Fields.Single(field => field.Id == "field:dotted");
        var nestedPath = projection.Fields.Single(field => field.Id == "field:nested");
        Assert.Equal("customer.name", Assert.IsType<FieldPathSegment>(Assert.Single(literalPath.Path)).Name);
        Assert.Collection(nestedPath.Path,
            segment => Assert.Equal("customer", Assert.IsType<FieldPathSegment>(segment).Name),
            segment => Assert.Equal("name", Assert.IsType<FieldPathSegment>(segment).Name));
        Assert.NotEqual(literalPath.Id, nestedPath.Id);
        var attribute = projection.Fields.Single(field => field.Id == "field:attribute");
        Assert.Equal("urn:orders:/~", Assert.IsType<FieldPathSegment>(attribute.Path[0]).NamespaceUri);
        Assert.IsType<AnyItemPathSegment>(attribute.Path[1]);
        Assert.Equal("id/a~b.name", Assert.IsType<AttributePathSegment>(attribute.Path[2]).Name);
        Assert.Equal("urn:identity", Assert.IsType<AttributePathSegment>(attribute.Path[2]).NamespaceUri);
        Assert.Equal("9007199254740993", Assert.IsType<IndexPathSegment>(projection.Fields.Single(field => field.Id == "field:index").Path[1]).Index);
        Assert.Equal(new FieldCardinality(CardinalityKind.Bounded, "0", "9007199254740993"), attribute.Cardinality);
        Assert.Equal(FieldRequirement.Unknown, attribute.Requirement);
        Assert.Equal(FieldNullability.Unknown, attribute.Nullability);
        Assert.Equal("/schema/a~1b~0c.name", Assert.IsType<JsonPointerNativeLocator>(attribute.NativeLocator).Pointer);
        var native = Assert.IsType<GraphNodeNativeLocator>(nestedPath.NativeLocator);
        Assert.Equal("4294967295", native.NodeId);
        Assert.Equal("9007199254740993", native.FunctionId);
        Assert.Equal(new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "0.1000000000000000000000000001"), projection.Parameters[0].DefaultValue);
        Assert.Equal(new[] { ExactNumberFlavor.Decimal, ExactNumberFlavor.Integer, ExactNumberFlavor.BinaryFloat },
            projection.Rules.SelectMany(rule => rule.Dependencies).OfType<ConstantDependency>()
                .Select(dependency => Assert.IsType<ExactNumberLiteralValue>(dependency.Value).Flavor));
        Assert.Equal(new[] { "0.1000000000000000000000000001", "9007199254740993", "8000000000000000" },
            projection.Rules.SelectMany(rule => rule.Dependencies).OfType<ConstantDependency>()
                .Select(dependency => Assert.IsType<ExactNumberLiteralValue>(dependency.Value).Value));
        Assert.Contains(projection.Rules[0].Dependencies, dependency => dependency is ParameterDependency { Role: DependencyRole.Condition });
        Assert.Contains(projection.Rules[0].Dependencies, dependency => dependency is FieldDependency { Role: DependencyRole.Default });
        Assert.Contains(projection.Rules[0].Dependencies, dependency => dependency is NodeDependency { Node: ControlNodeReference });
        var group = Assert.IsType<GroupDetail>(Assert.Single(projection.Rules[0].Details));
        var expression = Assert.IsType<ExpressionDetail>(Assert.Single(group.Children));
        Assert.Contains(expression.Arguments, detail => detail is ReferenceDetail { Reference: FieldNodeReference { FieldId: "field:nested" } });
        Assert.Equal(new FieldNodeReference("field:nested"), Assert.Single(projection.Diagnostics).ProjectionLocator);
        Assert.Equal(native, Assert.Single(projection.Diagnostics).NativeLocator);
        Assert.Equal(new[] { "boundary:input", "boundary:output" }, projection.Boundaries.Select(boundary => boundary.Id));
        Assert.Equal(new[] { ControlKind.Iteration, ControlKind.Filter }, projection.Controls.Select(control => control.Kind));
        var json = JsonSerializer.Serialize(projection, Options);
        var restored = JsonSerializer.Deserialize<ProjectionDocumentV1>(json, Options);
        Assert.NotNull(restored);
        Assert.Equal(json, JsonSerializer.Serialize(restored, Options));
    }

    [Theory]
    [InlineData("complete", CoverageStatus.Complete)]
    [InlineData("partial", CoverageStatus.Partial)]
    [InlineData("unavailable", CoverageStatus.Unavailable)]
    public async Task ReadableCoverageDoesNotQualifyPreviewOrBuild(string coverage, CoverageStatus expected)
    {
        var result = await new FerruleInspectionBackend(ReplyRunner(response => response["result"] = Payload(coverage))).ProjectAsync(Artifact());
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        var projection = Assert.IsType<ProjectionDocumentV1>(result.Projection);
        Assert.Equal(expected, projection.Coverage.Status);
        Assert.Equal(5, projection.Capabilities.Count);
        foreach (var capability in projection.Capabilities)
        {
            Assert.Equal(capability.Operation is MappingOperation.Inspect or MappingOperation.Validate or MappingOperation.Project
                ? CapabilityStatus.Supported : CapabilityStatus.NotQualified, capability.Status);
            Assert.NotEmpty(capability.Conditions);
        }
        if (expected == CoverageStatus.Partial)
        {
            Assert.NotEmpty(projection.Fields);
            Assert.NotEmpty(projection.Rules);
            var gap = Assert.Single(projection.Coverage.Gaps);
            Assert.Equal(new ControlNodeReference("control:filter"), gap.ProjectionLocator);
            Assert.Equal(new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/controls/1"), gap.NativeLocator);
            Assert.IsType<UnsupportedDetail>(Assert.Single(projection.Controls[1].Details));
        }
        else if (expected == CoverageStatus.Unavailable)
        {
            Assert.Empty(projection.Boundaries);
            Assert.Empty(projection.Fields);
            Assert.Empty(projection.Parameters);
            Assert.Empty(projection.Rules);
            Assert.Empty(projection.Controls);
            Assert.Empty(projection.Relationships);
            Assert.NotEmpty(projection.Coverage.Gaps);
            Assert.NotEmpty(projection.Diagnostics);
        }
    }

    [Fact]
    public async Task SharedNativeNodeDiagnosticsRetainNativeEvidenceWithoutInventingAProjectedLink()
    {
        var runner = ReplyRunner(response =>
        {
            Set(response, "result.fields.1.nativeLocator", At(response, "result.fields.2.nativeLocator").DeepClone());
            Set(response, "result.diagnostics.0.projectionLocator", null);
        });
        var result = await new FerruleInspectionBackend(runner).ProjectAsync(Artifact());
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        var diagnostic = Assert.Single(result.Projection!.Diagnostics);
        Assert.Null(diagnostic.ProjectionLocator);
        Assert.Equal(new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "4294967295", "9007199254740993"), diagnostic.NativeLocator);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeOnlyGraphEvidenceIsAdmittedForUnregisteredAndSharedOrigins(bool gap, bool sharedOrigin)
    {
        var native = sharedOrigin
            ? new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "4294967295", "9007199254740993")
            : new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "1");
        var runner = ReplyRunner(response =>
        {
            if (sharedOrigin)
                Set(response, "result.fields.1.nativeLocator", Serialize<NativeLocator>(native));
            Set(response, "result.diagnostics.0.projectionLocator", null);
            if (gap)
            {
                Set(response, "result.coverage.status", "partial");
                Set(response, "result.coverage.gaps", new JsonArray(Serialize(new CoverageGap("ferrule.native", "Native evidence retained.", native))));
            }
            else
                Set(response, "result.diagnostics.0.nativeLocator", Serialize<NativeLocator>(native));
        });

        var result = await new FerruleInspectionBackend(runner).ProjectAsync(Artifact());

        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        if (gap)
        {
            var evidence = Assert.Single(result.Projection!.Coverage.Gaps);
            Assert.Null(evidence.ProjectionLocator);
            Assert.Equal(native, evidence.NativeLocator);
        }
        else
        {
            var evidence = Assert.Single(result.Projection!.Diagnostics);
            Assert.Null(evidence.ProjectionLocator);
            Assert.Equal(native, evidence.NativeLocator);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraphLinksRequireTheUniqueEmittedEntityOrUnsupportedDetailOwner(bool unsupportedDetailOrigin)
    {
        var native = unsupportedDetailOrigin
            ? new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "1")
            : new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "4294967295", "9007199254740993");
        ProjectionNodeReference projected = unsupportedDetailOrigin ? new ControlNodeReference("control:filter") : new FieldNodeReference("field:nested");
        var runner = ReplyRunner(response =>
        {
            Set(response, "result.coverage.status", "partial");
            Set(response, "result.coverage.gaps", new JsonArray(Serialize(new CoverageGap("ferrule.native", "Native graph coverage gap.", native, projected))));
            Set(response, "result.diagnostics.0.nativeLocator", Serialize<NativeLocator>(native));
            Set(response, "result.diagnostics.0.projectionLocator", Serialize(projected));
            if (unsupportedDetailOrigin)
                At(response, "result.controls.1.details").AsArray().Add(Serialize<DetailNode>(new UnsupportedDetail("ferrule.native", "Native construct is not projected.", native)));
        });

        var result = await new FerruleInspectionBackend(runner).ProjectAsync(Artifact());

        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        var projection = Assert.IsType<ProjectionDocumentV1>(result.Projection);
        Assert.Equal(projected, Assert.Single(projection.Coverage.Gaps).ProjectionLocator);
        Assert.Equal(projected, Assert.Single(projection.Diagnostics).ProjectionLocator);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ArrayItemDomainsRetainUniqueScalarMembers(int scalarMembers)
    {
        var members = new[] { ScalarDataType.String, ScalarDataType.Integer }.Take(scalarMembers).ToArray();
        var runner = ReplyRunner(response => Set(response, "result.fields.3.dataType", Serialize(new FieldDataType(FieldDataTypeKind.Array, members))));

        var result = await new FerruleInspectionBackend(runner).ProjectAsync(Artifact());

        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        var field = result.Projection!.Fields.Single(field => field.Id == "field:attribute");
        Assert.Equal(FieldDataTypeKind.Array, field.DataType.Kind);
        Assert.Equal(members, field.DataType.ScalarTypes);
    }

    [Theory]
    [InlineData("2", null)]
    [InlineData(null, "9007199254740993")]
    [InlineData("2", "9007199254740993")]
    public async Task BoundedCardinalityRetainsOneSidedAndExactIntervals(string? minimum, string? maximum)
    {
        var expected = new FieldCardinality(CardinalityKind.Bounded, minimum, maximum);
        var runner = ReplyRunner(response => Set(response, "result.fields.3.cardinality", Serialize(expected)));

        var result = await new FerruleInspectionBackend(runner).ProjectAsync(Artifact());

        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        Assert.Equal(expected, result.Projection!.Fields.Single(field => field.Id == "field:attribute").Cardinality);
    }

    [Theory]
    [InlineData("nativeDigest", "NativeDigest")]
    [InlineData("result.coverage.status", "Status")]
    [InlineData("result.capabilities.0.status", "Status")]
    public async Task WorkerPropertyCaseAliasesAreRejected(string path, string alias)
    {
        var runner = new RecordingRunner((request, _) =>
        {
            var response = JsonNode.Parse(Response(RequestHeader(request.Span)).ToJsonString(WireOptions),
                new JsonNodeOptions { PropertyNameCaseInsensitive = false })!.AsObject();
            var separator = path.LastIndexOf('.');
            var owner = separator < 0 ? response : At(response, path[..separator]).AsObject();
            var property = path[(separator + 1)..];
            owner[alias] = At(response, path).DeepClone();
            Assert.Equal(2, owner.Count(entry => entry.Key == property || entry.Key == alias));
            var json = response.ToJsonString(WireOptions);
            Assert.Contains("\"" + property + "\":", json, StringComparison.Ordinal);
            Assert.Contains("\"" + alias + "\":", json, StringComparison.Ordinal);
            return Task.FromResult(new WorkerProcessResult(Frame(Encoding.UTF8.GetBytes(json)), WorkerProcessFailure.None));
        });

        AssertLocalFailure(await new FerruleInspectionBackend(runner).ProjectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("provenance")]
    [InlineData("schemaVersion")]
    [InlineData("wrongOperation")]
    [InlineData("wrongRequest")]
    [InlineData("wrongRevision")]
    [InlineData("wrongDigest")]
    [InlineData("wrongDocument")]
    [InlineData("wrongEngine")]
    [InlineData("duplicateEntityId")]
    [InlineData("crossKindEntityId")]
    [InlineData("duplicateRelationshipId")]
    [InlineData("duplicateRelationshipTriple")]
    [InlineData("missingRelationship")]
    [InlineData("extraRelationship")]
    [InlineData("wrongRelationshipDirection")]
    [InlineData("unknownRelationshipKind")]
    [InlineData("unresolvedRelationship")]
    [InlineData("wrongFieldBoundary")]
    [InlineData("wrongRootField")]
    [InlineData("inputRuleOutput")]
    [InlineData("unknownRuleOutput")]
    [InlineData("unresolvedFieldDependency")]
    [InlineData("unresolvedParameterDependency")]
    [InlineData("unresolvedNodeDependency")]
    [InlineData("unresolvedNestedDetail")]
    [InlineData("unresolvedAppliesTo")]
    [InlineData("unresolvedDiagnostic")]
    [InlineData("diagnosticWrongNativeLink")]
    [InlineData("diagnosticUnregisteredNative")]
    [InlineData("ambiguousDiagnosticLink")]
    [InlineData("unresolvedGap")]
    [InlineData("gapUnregisteredNative")]
    [InlineData("gapWrongNativeLink")]
    [InlineData("ambiguousGapLink")]
    [InlineData("unsupportedComplete")]
    [InlineData("unsupportedWithoutGap")]
    [InlineData("completeWithGap")]
    [InlineData("partialWithoutGap")]
    [InlineData("unavailableWithGraph")]
    [InlineData("unknownCoverage")]
    [InlineData("unknownFieldType")]
    [InlineData("duplicateScalarType")]
    [InlineData("scalarWithoutType")]
    [InlineData("duplicateArrayScalarType")]
    [InlineData("unknownArrayScalarType")]
    [InlineData("objectWithScalarType")]
    [InlineData("unknownCardinality")]
    [InlineData("invalidCardinalityRange")]
    [InlineData("boundedWithoutBounds")]
    [InlineData("invalidLowerOnlyBound")]
    [InlineData("invalidUpperOnlyBound")]
    [InlineData("cardinalityMetadataMismatch")]
    [InlineData("unknownRequirement")]
    [InlineData("nullNullability")]
    [InlineData("numericRole")]
    [InlineData("unknownPathKind")]
    [InlineData("numericPathIndex")]
    [InlineData("invalidPathIndex")]
    [InlineData("unknownDetailKind")]
    [InlineData("unknownControlKind")]
    [InlineData("unknownDependencyRole")]
    [InlineData("unknownLiteralKind")]
    [InlineData("unknownLiteralFlavor")]
    [InlineData("numericExactLiteral")]
    [InlineData("invalidInteger")]
    [InlineData("invalidDecimal")]
    [InlineData("invalidFloatBits")]
    [InlineData("wrongLocatorDocument")]
    [InlineData("invalidPointerEscape")]
    [InlineData("invalidGraphNodeId")]
    [InlineData("graphNodeOverflow")]
    [InlineData("invalidFunctionId")]
    [InlineData("functionIdOverflow")]
    [InlineData("unknownLocatorKind")]
    [InlineData("missingFields")]
    [InlineData("nullFields")]
    [InlineData("nullFieldEntry")]
    [InlineData("missingFieldRequirement")]
    [InlineData("qualifiedPreview")]
    [InlineData("unqualifiedProject")]
    [InlineData("duplicateOperation")]
    [InlineData("emptyConditions")]
    [InlineData("unresolvedCapabilityBoundary")]
    public async Task MalformedGraphsAndUntrustedMetadataNeverProduceAProjection(string corruption)
    {
        var runner = ReplyRunner(response => Corrupt(response, corruption));
        AssertLocalFailure(await new FerruleInspectionBackend(runner).ProjectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("parameterName")]
    [InlineData("fieldId")]
    [InlineData("pathName")]
    [InlineData("namespace")]
    [InlineData("integer")]
    [InlineData("summary")]
    [InlineData("diagnostic")]
    public async Task OversizedProjectionTextIsRejectedInsteadOfClipped(string value)
    {
        var runner = ReplyRunner(response =>
        {
            var text = new string('é', InspectionPolicy.MaximumTextBytes / 2 + 1);
            switch (value)
            {
                case "parameterName": Set(response, "result.parameters.0.name", text); break;
                case "fieldId": Set(response, "result.fields.0.id", text); break;
                case "pathName": Set(response, "result.fields.1.path.0.name", text); break;
                case "namespace": Set(response, "result.fields.3.path.0.namespaceUri", text); break;
                case "integer": Set(response, "result.rules.1.dependencies.0.value.value", new string('9', InspectionPolicy.MaximumTextBytes + 1)); break;
                case "summary": Set(response, "result.rules.0.summary", text); break;
                case "diagnostic": Set(response, "result.diagnostics.0.message", text); break;
            }
        });
        AssertLocalFailure(await new FerruleInspectionBackend(runner).ProjectAsync(Artifact()), MappingOperationStatus.ResourceLimit);
    }

    [Fact]
    public async Task Utf8TextAtTheExactCeilingRemainsUnchanged()
    {
        var name = new string('é', InspectionPolicy.MaximumTextBytes / 2);
        var runner = ReplyRunner(response => Set(response, "result.parameters.0.name", name));
        var result = await new FerruleInspectionBackend(runner).ProjectAsync(Artifact());
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        Assert.Equal(name, result.Projection!.Parameters[0].Name);
    }

    [Theory]
    [InlineData("details")]
    [InlineData("path")]
    [InlineData("dependencies")]
    [InlineData("appliesTo")]
    [InlineData("rawCollection")]
    [InlineData("diagnostics")]
    [InlineData("gaps")]
    [InlineData("depth")]
    [InlineData("detailDepth")]
    public async Task ExpandedStructuresAndRawCollectionsObeyResourceCeilings(string expansion)
    {
        var runner = ReplyRunner(response => Expand(response, expansion));
        AssertLocalFailure(await new FerruleInspectionBackend(runner).ProjectAsync(Artifact()), MappingOperationStatus.ResourceLimit);
    }

    [Fact]
    public async Task CombinedStructuralCeilingIncludesExpandedCollectionsAndIsInclusive()
    {
        var acceptedRunner = ReplyRunner(response => PadStructuralEntries(response, InspectionPolicy.MaximumItems));
        var accepted = await new FerruleInspectionBackend(acceptedRunner).ProjectAsync(Artifact());
        Assert.Equal(MappingOperationStatus.Succeeded, accepted.Status);
        Assert.NotNull(accepted.Projection);
        Assert.True(accepted.Projection.Rules[0].Dependencies.Count > InspectionPolicy.MaximumItems / 2);

        var rejectedRunner = ReplyRunner(response => PadStructuralEntries(response, InspectionPolicy.MaximumItems + 1));
        AssertLocalFailure(await new FerruleInspectionBackend(rejectedRunner).ProjectAsync(Artifact()), MappingOperationStatus.ResourceLimit);
    }

    [Fact]
    public async Task ExactDiagnosticCeilingRetainsAllReportedDiagnostics()
    {
        var runner = ReplyRunner(response =>
        {
            var diagnostics = At(response, "result.diagnostics").AsArray();
            var template = diagnostics[0]!.DeepClone();
            while (diagnostics.Count < InspectionPolicy.MaximumDiagnostics) diagnostics.Add(template.DeepClone());
        });
        var result = await new FerruleInspectionBackend(runner).ProjectAsync(Artifact());
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(InspectionPolicy.MaximumDiagnostics, result.Projection!.Diagnostics.Count);
    }

    [Fact]
    public async Task ExactDetailDepthCeilingRetainsItsExplanationTree()
    {
        var runner = ReplyRunner(response => Set(response, "result.rules.0.details", new JsonArray(NestedDetails(InspectionPolicy.MaximumProjectionDetailDepth))));
        var result = await new FerruleInspectionBackend(runner).ProjectAsync(Artifact());
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        DetailNode detail = Assert.Single(result.Projection!.Rules[0].Details);
        for (var depth = 0; depth < InspectionPolicy.MaximumProjectionDetailDepth; depth++)
            detail = Assert.Single(Assert.IsType<GroupDetail>(detail).Children);
        Assert.Equal(new LiteralDetail(new BooleanLiteralValue(true)), detail);
    }

    [Fact]
    public async Task StructuralBudgetCoversTheWholeGraphAcrossIndividuallyBoundedLists()
    {
        var runner = ReplyRunner(response =>
        {
            var fields = At(response, "result.fields").AsArray();
            var relationships = At(response, "result.relationships").AsArray();
            var template = fields[1]!.DeepClone();
            for (var index = 0; index < 5000; index++)
            {
                var id = "field:extra:" + index.ToString(CultureInfo.InvariantCulture);
                var field = template.DeepClone();
                field["id"] = id;
                fields.Add(field);
                relationships.Add(Serialize(new ProjectionRelationship("relationship:extra:" + index.ToString(CultureInfo.InvariantCulture),
                    RelationshipKind.BoundaryMember, new FieldNodeReference(id), new BoundaryNodeReference("boundary:input"))));
            }
            Assert.True(fields.Count < InspectionPolicy.MaximumItems);
            Assert.True(relationships.Count < InspectionPolicy.MaximumItems);
        });
        AssertLocalFailure(await new FerruleInspectionBackend(runner).ProjectAsync(Artifact()), MappingOperationStatus.ResourceLimit);
    }

    [Fact]
    public async Task ResponseByteCeilingIsInclusiveAndTheNextByteIsAResourceFailure()
    {
        var acceptedRunner = PaddedReplyRunner(InspectionPolicy.MaximumResponseBytes);
        Assert.Equal(MappingOperationStatus.Succeeded, (await new FerruleInspectionBackend(acceptedRunner).ProjectAsync(Artifact())).Status);
        var rejectedRunner = PaddedReplyRunner(InspectionPolicy.MaximumResponseBytes + 1);
        AssertLocalFailure(await new FerruleInspectionBackend(rejectedRunner).ProjectAsync(Artifact()), MappingOperationStatus.ResourceLimit);
    }

    [Fact]
    public async Task NativeAdmissionAndPreCancellationDoNotStartAWorker()
    {
        var runner = ReplyRunner();
        var backend = new FerruleInspectionBackend(runner);
        var oversized = Artifact(new byte[InspectionPolicy.MaximumNativeBytes + 1]);
        AssertLocalFailure(await backend.ProjectAsync(oversized), MappingOperationStatus.ResourceLimit, oversized);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertLocalFailure(await backend.ProjectAsync(Artifact(), cancellation.Token), MappingOperationStatus.Canceled);
        Assert.Equal(0, runner.Calls);
        await Assert.ThrowsAsync<ArgumentNullException>(() => backend.ProjectAsync(null!));
    }

    [Theory]
    [InlineData(WorkerProcessFailure.Canceled, MappingOperationStatus.Canceled)]
    [InlineData(WorkerProcessFailure.TimedOut, MappingOperationStatus.TimedOut)]
    [InlineData(WorkerProcessFailure.StartFailed, MappingOperationStatus.BackendUnavailable)]
    [InlineData(WorkerProcessFailure.WorkerFailed, MappingOperationStatus.BackendFailure)]
    [InlineData(WorkerProcessFailure.OutputLimitExceeded, MappingOperationStatus.ResourceLimit)]
    [InlineData(WorkerProcessFailure.CleanupFailed, MappingOperationStatus.IsolationFailure)]
    [InlineData(WorkerProcessFailure.SandboxUnavailable, MappingOperationStatus.BackendUnavailable)]
    public async Task WorkerFailuresProduceSafeDiagnosticsAndNoProjection(WorkerProcessFailure failure, MappingOperationStatus status)
    {
        var runner = new RecordingRunner((_, _) => Task.FromResult(new WorkerProcessResult("secret stderr /host/private/path"u8.ToArray(), failure)));
        AssertLocalFailure(await new FerruleInspectionBackend(runner).ProjectAsync(Artifact()), status);
    }

    private static void Corrupt(JsonObject response, string corruption)
    {
        switch (corruption)
        {
            case "provenance": Set(response, "result.provenance", new JsonObject { ["backend"] = "untrusted" }); break;
            case "schemaVersion": Set(response, "result.schemaVersion", "untrusted.v999"); break;
            case "wrongOperation": response["operation"] = "inspect"; break;
            case "wrongRequest": response["requestId"] = Guid.NewGuid().ToString("D"); break;
            case "wrongRevision": Set(response, "revision.revisionId", Guid.NewGuid().ToString("D")); break;
            case "wrongDigest": response["nativeDigest"] = new string('0', 64); break;
            case "wrongDocument": response["documentId"] = "other.json"; break;
            case "wrongEngine": response["engineRevision"] = new string('0', 40); break;
            case "duplicateEntityId": At(response, "result.fields").AsArray().Add(At(response, "result.fields.1").DeepClone()); break;
            case "crossKindEntityId": ReplaceStringValues(response, "parameter:threshold", "field:dotted"); break;
            case "duplicateRelationshipId": Set(response, "result.relationships.1.id", At(response, "result.relationships.0.id").DeepClone()); break;
            case "duplicateRelationshipTriple":
                var duplicate = At(response, "result.relationships.0").DeepClone();
                duplicate["id"] = "relationship:duplicate";
                At(response, "result.relationships").AsArray().Add(duplicate);
                break;
            case "missingRelationship": At(response, "result.relationships").AsArray().RemoveAt(0); break;
            case "extraRelationship": At(response, "result.relationships").AsArray().Add(Serialize(new ProjectionRelationship("extra", RelationshipKind.Produces, new BoundaryNodeReference("boundary:input"), new BoundaryNodeReference("boundary:output")))); break;
            case "wrongRelationshipDirection":
                var relationship = At(response, "result.relationships.0");
                var from = relationship["from"]!.DeepClone();
                relationship["from"] = relationship["to"]!.DeepClone();
                relationship["to"] = from;
                break;
            case "unknownRelationshipKind": Set(response, "result.relationships.0.kind", "future"); break;
            case "unresolvedRelationship": Set(response, "result.relationships.0.to.boundaryId", "missing"); break;
            case "wrongFieldBoundary": Set(response, "result.fields.1.boundaryId", "missing"); break;
            case "wrongRootField": Set(response, "result.boundaries.0.rootFieldId", "field:output-root"); break;
            case "inputRuleOutput":
                Set(response, "result.rules.0.outputFieldId", "field:dotted");
                var writesTo = At(response, "result.relationships").AsArray().Single(node => node!["kind"]!.GetValue<string>() == "writesTo" && node["from"]!["ruleId"]!.GetValue<string>() == "rule:total")!;
                writesTo["to"]!["fieldId"] = "field:dotted";
                break;
            case "unknownRuleOutput": Set(response, "result.rules.0.outputFieldId", "missing"); break;
            case "unresolvedFieldDependency": Set(response, "result.rules.0.dependencies.0.fieldId", "missing"); break;
            case "unresolvedParameterDependency": Set(response, "result.rules.0.dependencies.2.parameterId", "missing"); break;
            case "unresolvedNodeDependency": Set(response, "result.rules.0.dependencies.4.node.controlId", "missing"); break;
            case "unresolvedNestedDetail": Set(response, "result.rules.0.details.0.children.0.arguments.0.reference.fieldId", "missing"); break;
            case "unresolvedAppliesTo": Set(response, "result.controls.0.appliesTo.0.ruleId", "missing"); break;
            case "unresolvedDiagnostic": Set(response, "result.diagnostics.0.projectionLocator.fieldId", "missing"); break;
            case "diagnosticWrongNativeLink": Set(response, "result.diagnostics.0.projectionLocator.fieldId", "field:dotted"); break;
            case "diagnosticUnregisteredNative": Set(response, "result.diagnostics.0.nativeLocator", Serialize<NativeLocator>(new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "1"))); break;
            case "ambiguousDiagnosticLink": Set(response, "result.fields.1.nativeLocator", At(response, "result.fields.2.nativeLocator").DeepClone()); break;
            case "unresolvedGap": response["result"] = Payload("partial"); Set(response, "result.coverage.gaps.0.projectionLocator.controlId", "missing"); break;
            case "gapUnregisteredNative":
            case "gapWrongNativeLink":
            case "ambiguousGapLink":
                Set(response, "result.coverage.status", "partial");
                var native = corruption == "gapUnregisteredNative"
                    ? new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "1")
                    : new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "4294967295", "9007199254740993");
                var projected = new FieldNodeReference(corruption == "gapWrongNativeLink" ? "field:dotted" : "field:nested");
                Set(response, "result.coverage.gaps", new JsonArray(Serialize(new CoverageGap("ferrule.native", "Native graph coverage gap.", native, projected))));
                if (corruption == "ambiguousGapLink")
                {
                    Set(response, "result.fields.1.nativeLocator", Serialize<NativeLocator>(native));
                    Set(response, "result.diagnostics.0.projectionLocator", null);
                }
                break;
            case "unsupportedComplete": response["result"] = Payload("partial"); Set(response, "result.coverage.status", "complete"); Set(response, "result.coverage.gaps", new JsonArray()); break;
            case "unsupportedWithoutGap": response["result"] = Payload("partial"); Set(response, "result.coverage.gaps", new JsonArray()); break;
            case "completeWithGap": Set(response, "result.coverage.gaps", At(Payload("partial"), "coverage.gaps").DeepClone()); break;
            case "partialWithoutGap": Set(response, "result.coverage.status", "partial"); break;
            case "unavailableWithGraph": Set(response, "result.coverage.status", "unavailable"); break;
            case "unknownCoverage": Set(response, "result.coverage.status", "future"); break;
            case "unknownFieldType": Set(response, "result.fields.1.dataType.kind", "future"); break;
            case "duplicateScalarType": Set(response, "result.fields.1.dataType.scalarTypes", new JsonArray("string", "string")); break;
            case "scalarWithoutType": Set(response, "result.fields.1.dataType.scalarTypes", new JsonArray()); break;
            case "duplicateArrayScalarType": Set(response, "result.fields.3.dataType", Serialize(new FieldDataType(FieldDataTypeKind.Array, [ScalarDataType.String, ScalarDataType.String]))); break;
            case "unknownArrayScalarType": Set(response, "result.fields.3.dataType.kind", "array"); Set(response, "result.fields.3.dataType.scalarTypes", new JsonArray("future")); break;
            case "objectWithScalarType": Set(response, "result.fields.0.dataType.scalarTypes", new JsonArray("string")); break;
            case "unknownCardinality": Set(response, "result.fields.1.cardinality.kind", "future"); break;
            case "invalidCardinalityRange": Set(response, "result.fields.3.cardinality.minimum", "9007199254740994"); break;
            case "boundedWithoutBounds": Set(response, "result.fields.3.cardinality", Serialize(new FieldCardinality(CardinalityKind.Bounded))); break;
            case "invalidLowerOnlyBound": Set(response, "result.fields.3.cardinality", Serialize(new FieldCardinality(CardinalityKind.Bounded, "-2"))); break;
            case "invalidUpperOnlyBound": Set(response, "result.fields.3.cardinality", Serialize(new FieldCardinality(CardinalityKind.Bounded, null, "01"))); break;
            case "cardinalityMetadataMismatch": Set(response, "result.fields.1.cardinality.maximum", "2"); break;
            case "unknownRequirement": Set(response, "result.fields.1.requirement", "future"); break;
            case "nullNullability": Set(response, "result.fields.1.nullability", null); break;
            case "numericRole": Set(response, "result.boundaries.0.role", 0); break;
            case "unknownPathKind": Set(response, "result.fields.1.path.0.kind", "future"); break;
            case "numericPathIndex": Set(response, "result.fields.4.path.1.index", 9007199254740993L); break;
            case "invalidPathIndex": Set(response, "result.fields.4.path.1.index", "01"); break;
            case "unknownDetailKind": Set(response, "result.rules.0.details.0.kind", "future"); break;
            case "unknownControlKind": Set(response, "result.controls.0.kind", "future"); break;
            case "unknownDependencyRole": Set(response, "result.rules.0.dependencies.0.role", "future"); break;
            case "unknownLiteralKind": Set(response, "result.rules.1.dependencies.0.value.kind", "future"); break;
            case "unknownLiteralFlavor": Set(response, "result.rules.1.dependencies.0.value.flavor", "future"); break;
            case "numericExactLiteral": Set(response, "result.rules.1.dependencies.0.value.value", 9007199254740993L); break;
            case "invalidInteger": Set(response, "result.rules.1.dependencies.0.value.value", "1.0"); break;
            case "invalidDecimal": Set(response, "result.rules.0.dependencies.3.value.value", "NaN"); break;
            case "invalidFloatBits": Set(response, "result.rules.2.dependencies.0.value.value", "0x8000000000000000"); break;
            case "wrongLocatorDocument": Set(response, "result.fields.1.nativeLocator.documentId", "other.json"); break;
            case "invalidPointerEscape": Set(response, "result.fields.3.nativeLocator.pointer", "/schema/~2"); break;
            case "invalidGraphNodeId": Set(response, "result.fields.2.nativeLocator.nodeId", "01"); break;
            case "graphNodeOverflow": Set(response, "result.fields.2.nativeLocator.nodeId", "4294967296"); break;
            case "invalidFunctionId": Set(response, "result.fields.2.nativeLocator.functionId", "-1"); break;
            case "functionIdOverflow": Set(response, "result.fields.2.nativeLocator.functionId", "18446744073709551616"); break;
            case "unknownLocatorKind": Set(response, "result.fields.2.nativeLocator.kind", "future"); break;
            case "missingFields": response["result"]!.AsObject().Remove("fields"); break;
            case "nullFields": Set(response, "result.fields", null); break;
            case "nullFieldEntry": Set(response, "result.fields.0", null); break;
            case "missingFieldRequirement": At(response, "result.fields.1").AsObject().Remove("requirement"); break;
            case "qualifiedPreview": Set(response, "result.capabilities.3.status", "supported"); break;
            case "unqualifiedProject": Set(response, "result.capabilities.2.status", "notQualified"); break;
            case "duplicateOperation": Set(response, "result.capabilities.1.operation", "inspect"); break;
            case "emptyConditions": Set(response, "result.capabilities.2.conditions", new JsonArray()); break;
            case "unresolvedCapabilityBoundary": Set(response, "result.capabilities.3.boundaryIds.0", "missing"); break;
            default: throw new ArgumentException("Unknown test corruption.", nameof(corruption));
        }
    }

    private static void Expand(JsonObject response, string expansion)
    {
        var entries = new JsonArray();
        switch (expansion)
        {
            case "details":
                for (var index = 0; index <= InspectionPolicy.MaximumItems; index++) entries.Add(Serialize<DetailNode>(new LiteralDetail(new BooleanLiteralValue(true))));
                Set(response, "result.rules.0.details.0.children", entries);
                break;
            case "path":
                for (var index = 0; index <= InspectionPolicy.MaximumItems; index++) entries.Add(Serialize<PathSegment>(new FieldPathSegment("nested")));
                Set(response, "result.fields.1.path", entries);
                break;
            case "dependencies":
                for (var index = 0; index <= InspectionPolicy.MaximumItems; index++) At(response, "result.rules.0.dependencies").AsArray().Add(Serialize<RuleDependency>(new ConstantDependency(new BooleanLiteralValue(true))));
                break;
            case "appliesTo":
                for (var index = 0; index <= InspectionPolicy.MaximumItems; index++) entries.Add(Serialize<ProjectionNodeReference>(new RuleNodeReference("rule:total")));
                Set(response, "result.controls.0.appliesTo", entries);
                break;
            case "rawCollection":
                for (var index = 0; index <= InspectionPolicy.MaximumResponseItems; index++) entries.Add("boundary:input");
                Set(response, "result.capabilities.0.boundaryIds", entries);
                break;
            case "diagnostics":
                for (var index = 0; index <= InspectionPolicy.MaximumDiagnostics; index++) entries.Add(At(response, "result.diagnostics.0").DeepClone());
                Set(response, "result.diagnostics", entries);
                break;
            case "gaps":
                response["result"] = Payload("partial");
                for (var index = 0; index <= InspectionPolicy.MaximumDiagnostics; index++) entries.Add(At(response, "result.coverage.gaps.0").DeepClone());
                Set(response, "result.coverage.gaps", entries);
                break;
            case "depth":
                Set(response, "result.rules.0.details", new JsonArray(NestedDetails(InspectionPolicy.MaximumJsonDepth)));
                break;
            case "detailDepth":
                Set(response, "result.rules.0.details", new JsonArray(NestedDetails(InspectionPolicy.MaximumProjectionDetailDepth + 1)));
                break;
            default: throw new ArgumentException("Unknown test expansion.", nameof(expansion));
        }
    }

    private static ProjectionDocumentV1 Template(string coverage)
    {
        var source = "boundary:input";
        var target = "boundary:output";
        ProjectionField[] fields =
        [
            Field("field:input-root", source, "Source", [], new FieldDataType(FieldDataTypeKind.Object, []), new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/source")),
            Field("field:dotted", source, "customer.name", [new FieldPathSegment("customer.name")], Scalar(ScalarDataType.String), new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "12")),
            Field("field:nested", source, "name", [new FieldPathSegment("customer"), new FieldPathSegment("name")], Scalar(ScalarDataType.String), new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "4294967295", "9007199254740993")),
            Field("field:attribute", source, "id/a~b.name", [new FieldPathSegment("customer", "urn:orders:/~"), new AnyItemPathSegment(), new AttributePathSegment("id/a~b.name", "urn:identity")], Scalar(ScalarDataType.String), new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/schema/a~1b~0c.name")) with
                { Cardinality = new FieldCardinality(CardinalityKind.Bounded, "0", "9007199254740993"), Requirement = FieldRequirement.Unknown, Nullability = FieldNullability.Unknown },
            Field("field:index", source, "value", [new FieldPathSegment("rates"), new IndexPathSegment("9007199254740993"), new FieldPathSegment("value")], Scalar(ScalarDataType.Decimal), new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/rates/0")),
            Field("field:output-root", target, "Target", [], new FieldDataType(FieldDataTypeKind.Object, []), new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/target")),
            Field("field:total", target, "total", [new FieldPathSegment("total")], Scalar(ScalarDataType.Decimal), new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/target/total")),
            Field("field:constant", target, "constant", [new FieldPathSegment("constant")], Scalar(ScalarDataType.Integer), new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/target/constant")),
            Field("field:bits", target, "bits", [new FieldPathSegment("bits")], Scalar(ScalarDataType.BinaryFloat), new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/target/bits"))
        ];
        var decimalValue = new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "0.1000000000000000000000000001");
        ProjectionRule[] rules =
        [
            new("rule:total", "field:total", "Choose and explain an exact total.",
                [new FieldDependency("field:dotted"), new FieldDependency("field:nested", DependencyRole.Default), new ParameterDependency("parameter:threshold", DependencyRole.Condition), new ConstantDependency(decimalValue, DependencyRole.Default), new NodeDependency(new ControlNodeReference("control:filter"), DependencyRole.Condition)],
                [new GroupDetail("Arguments", [new ExpressionDetail("choose", [new ReferenceDetail(new FieldNodeReference("field:dotted")), new ReferenceDetail(new FieldNodeReference("field:nested")), new ReferenceDetail(new ParameterNodeReference("parameter:threshold")), new LiteralDetail(decimalValue)])])],
                new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/rules/0")),
            new("rule:integer", "field:constant", "Emit the integer without numeric conversion.", [new ConstantDependency(new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "9007199254740993"))], [new LiteralDetail(new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "9007199254740993"))], new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/rules/1")),
            new("rule:bits", "field:bits", "Preserve negative-zero bits.", [new ConstantDependency(new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "8000000000000000"))], [new LiteralDetail(new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "8000000000000000"))], new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/rules/2"))
        ];
        ProjectionControl[] controls =
        [
            new("control:iteration", ControlKind.Iteration, "Iterate source items.", [new RuleNodeReference("rule:total")], [new NodeDependency(new BoundaryNodeReference(source))], [new ReferenceDetail(new BoundaryNodeReference(source))], new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/controls/0")),
            new("control:filter", ControlKind.Filter, "Filter with a runtime parameter.", [new RuleNodeReference("rule:total")], [new FieldDependency("field:index", DependencyRole.Condition), new ParameterDependency("parameter:threshold", DependencyRole.Condition)], [new ExpressionDetail("greaterThan", [new ReferenceDetail(new FieldNodeReference("field:index")), new ReferenceDetail(new ParameterNodeReference("parameter:threshold"))])], new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/controls/1"))
        ];
        MappingOperation[] operations = [MappingOperation.Inspect, MappingOperation.Validate, MappingOperation.Project, MappingOperation.Preview, MappingOperation.Build];
        var capabilities = operations.Select(operation => new OperationCapability(operation,
            operation is MappingOperation.Inspect or MappingOperation.Validate or MappingOperation.Project ? CapabilityStatus.Supported : CapabilityStatus.NotQualified,
            operation == MappingOperation.Project ? "ferrule.projection.v1" : "ferrule.transcript.v1",
            operation is MappingOperation.Preview or MappingOperation.Build && coverage != "unavailable" ? [target] : [],
            [new CapabilityCondition(operation == MappingOperation.Project ? "boundedReadOnlyProjection" : "readOnly",
                "Resources remain unresolved and mapping execution is unavailable.")])).ToArray();
        var provenance = new ProjectionProvenance(new ScopedRevisionId("untrusted", "untrusted", "untrusted", "untrusted"),
            new ArtifactDigest("untrusted", "untrusted"), new BackendIdentity("untrusted", "untrusted", "untrusted"), "untrusted", "untrusted");
        if (coverage == "unavailable")
            return new ProjectionDocumentV1(provenance, new ProjectionCoverage(CoverageStatus.Unavailable,
                [new CoverageGap("ferrule.projection.unavailable", "The artifact cannot be explained.", new DocumentNativeLocator(InspectionPolicy.DocumentId))]),
                [], [], [], [], [], [], [new ProjectionDiagnostic("ferrule.projection.unavailable", DiagnosticSeverity.Warning, "No supported graph is available.", new DocumentNativeLocator(InspectionPolicy.DocumentId))], capabilities);
        CoverageGap[] gaps = [];
        if (coverage == "partial")
        {
            var locator = controls[1].NativeLocator;
            controls[1] = controls[1] with { Kind = ControlKind.Unknown, Details = [new UnsupportedDetail("futureFilter", "An understandable graph retains an unexplained filter.", locator)] };
            gaps = [new CoverageGap("ferrule.projection.unsupported", "The filter is only partially understood.", locator, new ControlNodeReference("control:filter"))];
        }
        return new ProjectionDocumentV1(provenance, new ProjectionCoverage(coverage == "partial" ? CoverageStatus.Partial : CoverageStatus.Complete, gaps),
            [new ProjectionBoundary(source, "Source", BoundaryRole.PrimaryInput, "native-json", BoundaryShape.Object, "field:input-root", new DocumentNativeLocator(InspectionPolicy.DocumentId)),
                new ProjectionBoundary(target, "Target", BoundaryRole.PrimaryOutput, "native-json", BoundaryShape.Object, "field:output-root", new DocumentNativeLocator(InspectionPolicy.DocumentId))],
            fields, [new ProjectionParameter("parameter:threshold", "threshold", Scalar(ScalarDataType.Decimal), FieldRequirement.Optional, decimalValue, new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/parameters/0"))],
            rules, controls, Relationships(fields, rules, controls),
            [new ProjectionDiagnostic("ferrule.projection.information", DiagnosticSeverity.Information, "A native node is linked precisely.", fields[2].NativeLocator, new FieldNodeReference(fields[2].Id))], capabilities);
    }

    private static IReadOnlyList<ProjectionRelationship> Relationships(IReadOnlyList<ProjectionField> fields, IReadOnlyList<ProjectionRule> rules, IReadOnlyList<ProjectionControl> controls)
    {
        var result = new List<ProjectionRelationship>();
        foreach (var field in fields) Add(RelationshipKind.BoundaryMember, new FieldNodeReference(field.Id), new BoundaryNodeReference(field.BoundaryId));
        foreach (var rule in rules)
        {
            var reference = new RuleNodeReference(rule.Id);
            Add(RelationshipKind.WritesTo, reference, new FieldNodeReference(rule.OutputFieldId));
            Dependencies(reference, rule.Dependencies);
        }
        foreach (var control in controls)
        {
            var reference = new ControlNodeReference(control.Id);
            foreach (var appliesTo in control.AppliesTo) Add(RelationshipKind.GovernedBy, appliesTo, reference);
            Dependencies(reference, control.Dependencies);
        }
        return result;

        void Add(RelationshipKind kind, ProjectionNodeReference from, ProjectionNodeReference to) =>
            result.Add(new ProjectionRelationship("relationship:" + result.Count.ToString(CultureInfo.InvariantCulture), kind, from, to));
        void Dependencies(ProjectionNodeReference from, IReadOnlyList<RuleDependency> dependencies)
        {
            foreach (var dependency in dependencies)
            {
                switch (dependency)
                {
                    case FieldDependency field: Add(RelationshipKind.ReadsFrom, from, new FieldNodeReference(field.FieldId)); break;
                    case ParameterDependency parameter: Add(RelationshipKind.DependsOn, from, new ParameterNodeReference(parameter.ParameterId)); break;
                    case NodeDependency node: Add(RelationshipKind.DependsOn, from, node.Node); break;
                }
            }
        }
    }

    private static ProjectionField Field(string id, string boundaryId, string name, IReadOnlyList<PathSegment> path, FieldDataType type, NativeLocator locator) =>
        new(id, boundaryId, path, name, type, new FieldCardinality(CardinalityKind.ExactlyOne), FieldRequirement.Required, FieldNullability.NonNullable, locator);

    private static FieldDataType Scalar(ScalarDataType type) => new(FieldDataTypeKind.Scalar, [type]);

    private static JsonObject Payload(string coverage = "complete")
    {
        var source = Serialize(Template(coverage)).AsObject();
        var payload = new JsonObject { ["kind"] = "project" };
        foreach (var property in source)
        {
            if (property.Key is not ("provenance" or "schemaVersion")) payload[property.Key] = property.Value?.DeepClone();
        }
        return payload;
    }

    private static JsonNode Serialize<T>(T value) => JsonSerializer.SerializeToNode(value, Options)!;

    private static JsonNode NestedDetails(int depth)
    {
        JsonNode nested = Serialize<DetailNode>(new LiteralDetail(new BooleanLiteralValue(true)));
        for (var index = 0; index < depth; index++)
            nested = new JsonObject { ["kind"] = "group", ["label"] = "Nested", ["children"] = new JsonArray(nested) };
        return nested;
    }

    private static void PadStructuralEntries(JsonObject response, int total)
    {
        var current = CountArrayEntries(response["result"]!);
        Assert.InRange(current, 1, total);
        var dependencies = At(response, "result.rules.0.dependencies").AsArray();
        while (current++ < total) dependencies.Add(Serialize<RuleDependency>(new ConstantDependency(new BooleanLiteralValue(true))));
        Assert.Equal(total, CountArrayEntries(response["result"]!));
    }

    private static int CountArrayEntries(JsonNode node) => node switch
    {
        JsonArray array => array.Count + array.Where(child => child is not null).Sum(child => CountArrayEntries(child!)),
        JsonObject obj => obj.Sum(property => property.Value is null ? 0 : CountArrayEntries(property.Value)),
        _ => 0
    };

    private static void ReplaceStringValues(JsonNode node, string original, string replacement)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text) && text == original) obj[property.Key] = replacement;
                else if (property.Value is { } child) ReplaceStringValues(child, original, replacement);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text) && text == original) array[index] = replacement;
                else if (array[index] is { } child) ReplaceStringValues(child, original, replacement);
            }
        }
    }

    private static NativeArtifact Artifact(byte[]? bytes = null)
    {
        bytes ??= "{}"u8.ToArray();
        return NativeArtifact.Admit(Scope, bytes, bytes.Length);
    }

    private static RecordingRunner ReplyRunner(Action<JsonObject>? alter = null) => new((request, _) =>
    {
        var response = Response(RequestHeader(request.Span));
        alter?.Invoke(response);
        return Task.FromResult(new WorkerProcessResult(Frame(response), WorkerProcessFailure.None));
    });

    private static RecordingRunner PaddedReplyRunner(int length) => new((request, _) =>
    {
        var response = Encoding.UTF8.GetBytes(Response(RequestHeader(request.Span)).ToJsonString());
        var payload = new byte[length];
        response.CopyTo(payload, 0);
        Array.Fill(payload, (byte)' ', response.Length, length - response.Length);
        return Task.FromResult(new WorkerProcessResult(Frame(payload), WorkerProcessFailure.None));
    });

    private static JsonObject RequestHeader(ReadOnlySpan<byte> frame)
    {
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame));
        return JsonNode.Parse(frame.Slice(4, length))!.AsObject();
    }

    private static JsonObject Response(JsonObject request) => new()
    {
        ["protocolVersion"] = FerruleInspectionBackend.ProtocolVersion,
        ["requestId"] = request["requestId"]!.DeepClone(),
        ["operation"] = request["operation"]!.DeepClone(),
        ["revision"] = request["revision"]!.DeepClone(),
        ["nativeDigest"] = request["nativeDigest"]!.DeepClone(),
        ["documentId"] = InspectionPolicy.DocumentId,
        ["engineRevision"] = FerruleInspectionBackend.EngineRevision,
        ["result"] = Payload(),
        ["failure"] = null
    };

    private static JsonNode At(JsonObject root, string path)
    {
        JsonNode current = root;
        foreach (var part in path.Split('.')) current = current is JsonArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)]! : current[part]!;
        return current;
    }

    private static void Set(JsonObject root, string path, JsonNode? value)
    {
        var separator = path.LastIndexOf('.');
        var parent = separator < 0 ? root : At(root, path[..separator]);
        var key = path[(separator + 1)..];
        if (parent is JsonArray array) array[int.Parse(key, CultureInfo.InvariantCulture)] = value;
        else parent[key] = value;
    }

    private static byte[] Frame(JsonObject response) => Frame(Encoding.UTF8.GetBytes(response.ToJsonString(WireOptions)));

    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static void AssertProvenance(MappingOperationProvenance provenance, NativeArtifact artifact, bool confirmed)
    {
        Assert.Equal(artifact.Scope, provenance.Scope);
        Assert.Equal(artifact.Digest, provenance.NativeDigest);
        Assert.Equal("ferrule", provenance.BackendId);
        Assert.Equal(FerruleInspectionBackend.EngineRevision, provenance.EngineRevision);
        Assert.Equal(WorkerBuild, provenance.BackendBuild);
        Assert.Equal(FerruleInspectionBackend.AdapterBuild, provenance.AdapterBuild);
        Assert.Equal(confirmed, provenance.Confirmed);
    }

    private static void AssertLocalFailure(MappingProjectionResult result, MappingOperationStatus status, NativeArtifact? artifact = null)
    {
        Assert.Equal(status, result.Status);
        Assert.Null(result.Projection);
        AssertProvenance(result.Provenance, artifact ?? Artifact(), false);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.StartsWith("mapping.", diagnostic.Code, StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.False(diagnostic.MessageTruncated);
        Assert.NotEmpty(diagnostic.Message);
        Assert.DoesNotContain("secret", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/host/private/path", diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(diagnostic.NativeLocator);
        Assert.Null(diagnostic.Owner);
        Assert.Null(diagnostic.Location);
    }

    private sealed class RecordingRunner(Func<ReadOnlyMemory<byte>, CancellationToken, Task<WorkerProcessResult>> run) : IInspectionWorkerRunner
    {
        public string BuildIdentity => WorkerBuild;
        public int Calls { get; private set; }
        public byte[]? LastRequest { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = requestFrame.ToArray();
            LastCancellationToken = cancellationToken;
            return run(requestFrame, cancellationToken);
        }
    }
}
