using System.Text.Json;
using Weft.Api;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;
using Weft.MappingBackends;

namespace Weft.Tests;

public sealed class NativeExampleServiceTests
{
    private const string FunctionId = "18446744073709551615";
    private static readonly JsonSerializerOptions Options = ProjectionJson.CreateOptions();
    private static readonly NativeExampleManifest Manifest = new();
    private static readonly GraphNodeNativeLocator Graph = new(InspectionPolicy.DocumentId, "12", FunctionId);

    [Fact]
    public void ManifestRetainsExactPackagedBytesAndContentDerivedRevisionIdentity()
    {
        var files = new Dictionary<string, (string File, string MappingId)>(StringComparer.Ordinal)
        {
            ["basic"] = ("valid-basic", "63333333-3333-4333-8333-000000000001"),
            ["exact"] = ("versioned-exact", "63333333-3333-4333-8333-000000000002"),
            ["conditions"] = ("projection-basic", "63333333-3333-4333-8333-000000000003"),
            ["controls"] = ("projection-controls", "63333333-3333-4333-8333-000000000004"),
            ["functions"] = ("projection-functions", "63333333-3333-4333-8333-000000000005"),
            ["partial"] = ("projection-partial", "63333333-3333-4333-8333-000000000006"),
            ["invalid"] = ("validation-cycle", "63333333-3333-4333-8333-000000000007"),
            ["generated"] = ("projection-generated-source-context", "63333333-3333-4333-8333-000000000008"),
            ["ambiguous"] = ("projection-join-context", "63333333-3333-4333-8333-000000000009")
        };
        Assert.Equal(files.Count, Manifest.Examples.Count);
        foreach (var descriptor in Manifest.Examples)
        {
            Assert.True(Manifest.TryGet(descriptor.Id, out var selected, out var artifact));
            Assert.NotNull(artifact);
            var original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", files[descriptor.Id].File + ".json"));
            Assert.Equal(descriptor, selected);
            Assert.Equal(original, artifact.CopyBytes());
            Assert.Equal(Sha256Digest.FromBytes(original).Value, descriptor.Digest.Value);
            Assert.Equal(original.Length, descriptor.ByteCount);
            Assert.Equal(files[descriptor.Id].MappingId, descriptor.Revision.MappingId);
            Assert.Equal(Guid.ParseExact(descriptor.Digest.Value[..32], "N"), artifact.Scope.RevisionId);
            var copy = artifact.CopyBytes();
            copy[0] ^= 0xff;
            Assert.Equal(original, artifact.CopyBytes());
        }
        Assert.False(Manifest.TryGet("../valid-basic.json", out var unknown, out var native));
        Assert.Null(unknown);
        Assert.Null(native);
    }

    [Fact]
    public void UsedCycleExamplePreservesItsPinnedNativeGraphAndRevision()
    {
        Assert.True(Manifest.TryGet("invalid", out var descriptor, out var artifact));
        Assert.NotNull(descriptor);
        Assert.NotNull(artifact);
        Assert.Equal(526, descriptor.ByteCount);
        Assert.Equal("54514537939302ceda435e4403da8051935f55c9e684dc2f9dadfb05145f52c2", descriptor.Digest.Value);
        Assert.Equal("63333333-3333-4333-8333-000000000007", descriptor.Revision.MappingId);
        Assert.Equal("54514537-9393-02ce-da43-5e4403da8051", descriptor.Revision.RevisionId);
        using var native = JsonDocument.Parse(artifact.CopyBytes());
        var node = Assert.Single(native.RootElement.GetProperty("graph").GetProperty("nodes").EnumerateObject());
        Assert.Equal("1", node.Name);
        Assert.Equal("call", node.Value.GetProperty("kind").GetString());
        Assert.Equal("concat", node.Value.GetProperty("function").GetString());
        Assert.Equal(1, Assert.Single(node.Value.GetProperty("args").EnumerateArray()).GetInt32());
        var binding = Assert.Single(native.RootElement.GetProperty("root").GetProperty("bindings").EnumerateArray());
        Assert.Equal("code", binding.GetProperty("target_field").GetString());
        Assert.Equal(1, binding.GetProperty("node").GetInt32());
    }

    [Fact]
    public async Task BothOperationsReceiveTheSameImmutableArtifactAndCancellationToken()
    {
        var backend = new Backend();
        using var cancellation = new CancellationTokenSource();
        var service = new NativeExampleService(Manifest, backend, backend);

        var result = Assert.IsType<NativeExampleResultV1>(await service.GetAsync("exact", cancellation.Token));

        Assert.Equal(OperationStatusV1.Succeeded, result.Projection.Status);
        Assert.True(result.Validation.IsValid);
        Assert.Same(backend.ProjectionArtifact, backend.ValidationArtifact);
        Assert.Equal(cancellation.Token, backend.ProjectionToken);
        Assert.Equal(cancellation.Token, backend.ValidationToken);
        Assert.Equal(1, backend.ProjectionCalls);
        Assert.Equal(1, backend.ValidationCalls);
        Assert.Equal(0, backend.InspectionCalls);
        Assert.True(Manifest.TryGet("exact", out _, out var expected));
        Assert.NotNull(expected);
        Assert.Equal(expected.CopyBytes(), backend.ProjectionArtifact!.CopyBytes());
        Assert.Equal(result.Example.Digest, result.Projection.Document!.Provenance.NativeArtifactDigest);
    }

    [Fact]
    public async Task UnknownExamplesDoNotInvokeEitherBackend()
    {
        var backend = new Backend();
        Assert.Null(await new NativeExampleService(Manifest, backend, backend).GetAsync("missing"));
        Assert.Equal(0, backend.ProjectionCalls);
        Assert.Equal(0, backend.ValidationCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentlyMissingBackendInterfacesDoNotHideTheAvailableOperation(bool projectionOnly)
    {
        var backend = new Backend();
        var service = new NativeExampleService(Manifest, projectionOnly ? null : backend, projectionOnly ? backend : null);
        var result = Assert.IsType<NativeExampleResultV1>(await service.GetAsync("basic"));
        Assert.Equal(projectionOnly ? OperationStatusV1.Succeeded : OperationStatusV1.BackendUnavailable, result.Projection.Status);
        Assert.Equal(projectionOnly ? OperationStatusV1.BackendUnavailable : OperationStatusV1.Succeeded, result.Validation.Status);
        Assert.Equal(projectionOnly ? 1 : 0, backend.ProjectionCalls);
        Assert.Equal(projectionOnly ? 0 : 1, backend.ValidationCalls);
        if (projectionOnly) Assert.Null(result.Validation.IsValid);
        else Assert.True(result.Validation.IsValid);
    }

    [Theory]
    [InlineData(MappingOperationStatus.InvalidMapping, OperationStatusV1.InvalidMapping)]
    [InlineData(MappingOperationStatus.UnsupportedArtifact, OperationStatusV1.UnsupportedArtifact)]
    [InlineData(MappingOperationStatus.ResourceLimit, OperationStatusV1.ResourceLimit)]
    [InlineData(MappingOperationStatus.ProtocolFailure, OperationStatusV1.ProtocolFailure)]
    [InlineData(MappingOperationStatus.BackendUnavailable, OperationStatusV1.BackendUnavailable)]
    [InlineData(MappingOperationStatus.BackendFailure, OperationStatusV1.BackendFailure)]
    [InlineData(MappingOperationStatus.TimedOut, OperationStatusV1.TimedOut)]
    [InlineData(MappingOperationStatus.Canceled, OperationStatusV1.Canceled)]
    [InlineData(MappingOperationStatus.IsolationFailure, OperationStatusV1.IsolationFailure)]
    public async Task NormalizedFailuresRetainTheirStatusAndNeverManufactureAValidatorResult(MappingOperationStatus status, OperationStatusV1 expected)
    {
        var backend = new Backend
        {
            Projection = (artifact, _) => Task.FromResult(new MappingProjectionResult(status, Provenance(artifact, false), null, [Diagnostic()])),
            Validation = (artifact, _) => Task.FromResult(new MappingValidationResult(status, Provenance(artifact, false), null, 0, 0, false, [Diagnostic()]))
        };
        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));
        Assert.Equal(expected, result.Projection.Status);
        Assert.Equal(expected, result.Validation.Status);
        Assert.NotNull(result.Projection.Provenance);
        Assert.False(result.Projection.Provenance.Confirmed);
        Assert.Null(result.Projection.Document);
        Assert.Null(result.Validation.IsValid);
        Assert.Null(Assert.Single(result.Validation.Diagnostics).ProjectionLocator);
    }

    [Fact]
    public async Task ProjectionFailureDoesNotDiscardSuccessfulNativeValidation()
    {
        var backend = new Backend
        {
            Projection = (artifact, _) => Task.FromResult(new MappingProjectionResult(MappingOperationStatus.ResourceLimit, Provenance(artifact, true), null, [Diagnostic()]))
        };
        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));
        Assert.Equal(OperationStatusV1.ResourceLimit, result.Projection.Status);
        Assert.Null(result.Projection.Document);
        Assert.Equal(OperationStatusV1.Succeeded, result.Validation.Status);
        Assert.True(result.Validation.IsValid);
        Assert.True(result.Validation.Provenance!.Confirmed);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionStartsNoBackendAndHasNoFabricatedProvenance()
    {
        var backend = new Backend();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic", cancellation.Token));
        Assert.Equal(OperationStatusV1.Canceled, result.Projection.Status);
        Assert.Equal(OperationStatusV1.Canceled, result.Validation.Status);
        Assert.Null(result.Projection.Provenance);
        Assert.Null(result.Validation.Provenance);
        Assert.Equal(0, backend.ProjectionCalls);
        Assert.Equal(0, backend.ValidationCalls);
    }

    [Fact]
    public async Task CancellationDuringProjectionReachesTheBackendAndSkipsFurtherWorkerAdmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend
        {
            Projection = async (_, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Cancellation was not propagated.");
            }
        };
        using var cancellation = new CancellationTokenSource();
        var read = new NativeExampleService(Manifest, backend, backend).GetAsync("basic", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = Assert.IsType<NativeExampleResultV1>(await read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(OperationStatusV1.Canceled, result.Projection.Status);
        Assert.Equal(OperationStatusV1.Canceled, result.Validation.Status);
        Assert.Null(result.Projection.Document);
        Assert.Null(result.Validation.IsValid);
        Assert.Equal(1, backend.ProjectionCalls);
        Assert.Equal(0, backend.ValidationCalls);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("pointer")]
    [InlineData("textSpan")]
    [InlineData("opaque")]
    [InlineData("unsupportedDetail")]
    public async Task UniqueEmittedNativeEvidenceLinksToItsTypedProjectedOwner(string evidence)
    {
        NativeLocator native = evidence switch
        {
            "pointer" => new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/source/a~1b~0c"),
            "textSpan" => new TextSpanNativeLocator(InspectionPolicy.DocumentId, "9007199254740993", "2"),
            "opaque" => new OpaqueNativeLocator(InspectionPolicy.DocumentId, "weft.fixture", "exact/native:identity"),
            _ => Graph
        };
        var backend = new Backend
        {
            Projection = (artifact, _) =>
            {
                var document = Document(artifact);
                document = evidence == "unsupportedDetail"
                    ? document with
                    {
                        Coverage = new ProjectionCoverage(CoverageStatus.Partial, [new CoverageGap("native.unknown", "Native construct is not explained.", native, new RuleNodeReference("rule:one"))]),
                        Rules = [document.Rules[0] with { Details = [new GroupDetail("Native explanation", [new ExpressionDetail("weft.fixture", [new UnsupportedDetail("weft.fixture.unknown", "Native construct is not explained.", native)])])] }]
                    }
                    : document with { Rules = [document.Rules[0] with { NativeLocator = native }] };
                return Task.FromResult(SuccessfulProjection(artifact, document));
            },
            Validation = (artifact, _) => Task.FromResult(InvalidValidation(artifact, Diagnostic(native)))
        };

        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));

        Assert.Equal(OperationStatusV1.InvalidMapping, result.Validation.Status);
        var diagnostic = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal(native, diagnostic.NativeLocator);
        Assert.Equal(new RuleNodeReference("rule:one"), diagnostic.ProjectionLocator);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("shared")]
    [InlineData("sharedDetail")]
    [InlineData("differentFunction")]
    [InlineData("document")]
    [InlineData("emptyPointer")]
    [InlineData("displayText")]
    [InlineData("backendBuild")]
    public async Task AbsentSharedOrForeignEvidenceRetainsNativeLocationWithoutAnArbitraryRowLink(string evidence)
    {
        NativeLocator native = evidence switch
        {
            "document" => new DocumentNativeLocator(InspectionPolicy.DocumentId),
            "emptyPointer" => new JsonPointerNativeLocator(InspectionPolicy.DocumentId, ""),
            _ => Graph
        };
        var backend = new Backend
        {
            Projection = (artifact, _) =>
            {
                var document = Document(artifact);
                if (evidence is "shared" or "backendBuild")
                    document = document with { Rules = [document.Rules[0] with { NativeLocator = native }] };
                if (evidence == "shared")
                    document = document with { Fields = [document.Fields[0] with { NativeLocator = native }, document.Fields[1]] };
                if (evidence == "sharedDetail")
                    document = document with
                    {
                        Coverage = new ProjectionCoverage(CoverageStatus.Partial, [new CoverageGap("unknown", "Shared native construct.", native)]),
                        Rules = [document.Rules[0] with { Details = [new UnsupportedDetail("unknown", "Shared native construct.", native)] }],
                        Controls = [new ProjectionControl("control:one", ControlKind.Filter, "Native filter.", [], [], [new UnsupportedDetail("unknown", "Shared native construct.", native)], new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/controls/0"))]
                    };
                if (evidence == "differentFunction")
                    document = document with { Rules = [document.Rules[0] with { NativeLocator = new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "12") }] };
                if (evidence == "displayText")
                    document = document with { Rules = [document.Rules[0] with { Details = [new ExpressionDetail("graphNode:12:" + FunctionId, [])] }] };
                return Task.FromResult(SuccessfulProjection(artifact, document));
            },
            Validation = (artifact, _) =>
            {
                var result = InvalidValidation(artifact, Diagnostic(native, new GraphNodeDiagnosticOwner(FunctionId, "12")));
                if (evidence == "backendBuild") result = result with { Provenance = result.Provenance with { BackendBuild = "other.worker.build" } };
                return Task.FromResult(result);
            }
        };

        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));

        Assert.Equal(OperationStatusV1.InvalidMapping, result.Validation.Status);
        var diagnostic = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal(native, diagnostic.NativeLocator);
        Assert.Equal(new GraphNodeNativeOwnerV1(FunctionId, "12"), diagnostic.Owner);
        Assert.Null(diagnostic.ProjectionLocator);
    }

    [Fact]
    public async Task EveryNativeOwnerVariantPreservesTypedStepsAndExactIdentities()
    {
        var owners = new (MappingDiagnosticOwner Native, NativeDiagnosticOwnerV1 Public)[]
        {
            (new GraphNodeDiagnosticOwner(FunctionId, "4294967295"), new GraphNodeNativeOwnerV1(FunctionId, "4294967295")),
            (new UserFunctionDiagnosticOwner(FunctionId), new UserFunctionNativeOwnerV1(FunctionId)),
            (new EndpointDiagnosticOwner(new SourceMappingEndpoint()), new EndpointNativeOwnerV1(new SourceNativeEndpointV1())),
            (new EndpointDiagnosticOwner(new TargetMappingEndpoint()), new EndpointNativeOwnerV1(new TargetNativeEndpointV1())),
            (new EndpointDiagnosticOwner(new NamedSourceMappingEndpoint("9007199254740993", "source.a/b~c")), new EndpointNativeOwnerV1(new NamedSourceNativeEndpointV1("9007199254740993", "source.a/b~c"))),
            (new EndpointDiagnosticOwner(new NamedTargetMappingEndpoint("9007199254740993", "target.a/b~c")), new EndpointNativeOwnerV1(new NamedTargetNativeEndpointV1("9007199254740993", "target.a/b~c"))),
            (new SchemaNodeDiagnosticOwner(new NamedSourceMappingEndpoint("2", "named"), [new ChildSchemaOwnerStep("9007199254740993"), new DynamicFieldSchemaOwnerStep(), new ContainsSchemaOwnerStep("2"), new DependentSchemaOwnerStep("3")]),
                new SchemaNodeNativeOwnerV1(new NamedSourceNativeEndpointV1("2", "named"), [new ChildSchemaNativeOwnerStepV1("9007199254740993"), new DynamicFieldSchemaNativeOwnerStepV1(), new ContainsSchemaNativeOwnerStepV1("2"), new DependentSchemaNativeOwnerStepV1("3")])),
            (new ScopeDiagnosticOwner(new NamedTargetMappingEndpoint("3", "named"), [new ChildScopeOwnerStep("9007199254740993"), new DynamicChildScopeOwnerStep("2"), new SegmentScopeOwnerStep("3")]),
                new ScopeNativeOwnerV1(new NamedTargetNativeEndpointV1("3", "named"), [new ChildScopeNativeOwnerStepV1("9007199254740993"), new DynamicChildScopeNativeOwnerStepV1("2"), new SegmentScopeNativeOwnerStepV1("3")])),
            (new FailureRuleDiagnosticOwner("9007199254740993"), new FailureRuleNativeOwnerV1("9007199254740993"))
        };
        var issues = owners.Select(owner => Diagnostic(owner.Native is GraphNodeDiagnosticOwner graph
            ? new GraphNodeNativeLocator(InspectionPolicy.DocumentId, graph.NodeId, graph.FunctionId)
            : new DocumentNativeLocator(InspectionPolicy.DocumentId), owner.Native) with
        { MessageTruncated = true, Location = "Native display location", LocationTruncated = true }).ToArray();
        var backend = new Backend { Validation = (artifact, _) => Task.FromResult(InvalidValidation(artifact, issues) with { Truncated = true }) };

        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));

        Assert.Equal(owners.Length, result.Validation.Diagnostics.Count);
        for (var index = 0; index < owners.Length; index++)
        {
            var issue = result.Validation.Diagnostics[index];
            Assert.Equal(JsonSerializer.Serialize(owners[index].Public, Options), JsonSerializer.Serialize(issue.Owner, Options));
            Assert.Equal("Native display location", issue.Location);
            Assert.True(issue.MessageTruncated);
            Assert.True(issue.LocationTruncated);
            Assert.Null(issue.ProjectionLocator);
        }
        var json = JsonSerializer.Serialize(result, Options);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<NativeExampleResultV1>(json, Options), Options));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("digest")]
    [InlineData("document")]
    [InlineData("unconfirmed")]
    public async Task ContradictoryProjectionIdentityNeverPublishesAGraph(string contradiction)
    {
        var backend = new Backend
        {
            Projection = (artifact, _) =>
            {
                var result = SuccessfulProjection(artifact, Document(artifact));
                result = contradiction switch
                {
                    "scope" => result with { Provenance = result.Provenance with { Scope = new RevisionScope(artifact.Scope.OrganizationId, artifact.Scope.ProjectId, artifact.Scope.MappingId, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")) } },
                    "digest" => result with { Provenance = result.Provenance with { NativeDigest = Sha256Digest.FromBytes("other"u8) } },
                    "document" => result with { Projection = result.Projection! with { Provenance = result.Projection!.Provenance with { Backend = new BackendIdentity("other", "revision", "build") } } },
                    _ => result with { Provenance = result.Provenance with { Confirmed = false } }
                };
                return Task.FromResult(result);
            }
        };
        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));
        Assert.Equal(OperationStatusV1.ProtocolFailure, result.Projection.Status);
        Assert.Null(result.Projection.Document);
        Assert.True(result.Validation.IsValid);
        Assert.Null(Assert.Single(result.Projection.Diagnostics).NativeLocator);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticEmissionLimitIsInclusiveAndRefusesOversizedReports(bool exceed)
    {
        var count = InspectionPolicy.MaximumDiagnostics + (exceed ? 1 : 0);
        var issues = Enumerable.Range(0, count).Select(_ => Diagnostic()).ToArray();
        var backend = new Backend { Validation = (artifact, _) => Task.FromResult(InvalidValidation(artifact, issues)) };
        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));
        Assert.Equal(exceed ? OperationStatusV1.ResourceLimit : OperationStatusV1.InvalidMapping, result.Validation.Status);
        Assert.Equal(OperationStatusV1.Succeeded, result.Projection.Status);
        if (exceed) Assert.Null(result.Validation.IsValid);
        else Assert.Equal(count, result.Validation.Diagnostics.Count);
    }

    [Fact]
    public async Task CombinedReportByteLimitIsInclusiveAndOverflowPublishesNoGraph()
    {
        var message = "A";
        var backend = new Backend
        {
            Projection = (artifact, _) => Task.FromResult(new MappingProjectionResult(MappingOperationStatus.ResourceLimit, Provenance(artifact, true), null,
                [Diagnostic() with { Code = "fixture.output", Message = message }]))
        };
        var service = new NativeExampleService(Manifest, projectionBackend: backend);
        var initial = Assert.IsType<NativeExampleResultV1>(await service.GetAsync("basic"));
        var initialLength = JsonSerializer.SerializeToUtf8Bytes(initial, Options).Length;
        message = new string('A', NativeExampleService.MaximumResponseBytes - initialLength + 1);
        var atLimit = Assert.IsType<NativeExampleResultV1>(await service.GetAsync("basic"));
        Assert.Equal("fixture.output", Assert.Single(atLimit.Projection.Diagnostics).Code);
        Assert.Equal(NativeExampleService.MaximumResponseBytes, JsonSerializer.SerializeToUtf8Bytes(atLimit, Options).Length);
        message += "A";
        var exceeded = Assert.IsType<NativeExampleResultV1>(await service.GetAsync("basic"));
        Assert.Equal(OperationStatusV1.ResourceLimit, exceeded.Projection.Status);
        Assert.Equal(OperationStatusV1.ResourceLimit, exceeded.Validation.Status);
        Assert.Null(exceeded.Projection.Document);
        Assert.Null(exceeded.Validation.IsValid);
        Assert.Equal("mapping.example.limit", Assert.Single(exceeded.Projection.Diagnostics).Code);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(exceeded, Options).Length, 1, NativeExampleService.MaximumResponseBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackendExceptionsRemainIndependentSafeFailures(bool projectionFails)
    {
        var backend = new Backend();
        if (projectionFails) backend.Projection = (_, _) => throw new IOException("secret stderr /host/private/path");
        else backend.Validation = (_, _) => throw new InvalidOperationException("secret stderr /host/private/path");
        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));
        Assert.Equal(projectionFails ? OperationStatusV1.BackendFailure : OperationStatusV1.Succeeded, result.Projection.Status);
        Assert.Equal(projectionFails ? OperationStatusV1.Succeeded : OperationStatusV1.BackendFailure, result.Validation.Status);
        var json = JsonSerializer.Serialize(result, Options);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/host/private/path", json, StringComparison.Ordinal);
    }

    private static MappingDiagnostic Diagnostic(NativeLocator? native = null, MappingDiagnosticOwner? owner = null)
        => new("fixture.validation", DiagnosticSeverity.Error, "Native validation finding.", false, native, owner);

    private static MappingOperationProvenance Provenance(NativeArtifact artifact, bool confirmed)
        => new(artifact.Scope, artifact.Digest, "fixture.backend", "fixture.engine.pin", "fixture.worker.build", "fixture.adapter.v1", confirmed);

    private static MappingProjectionResult SuccessfulProjection(NativeArtifact artifact, ProjectionDocumentV1 document)
        => new(MappingOperationStatus.Succeeded, Provenance(artifact, true), document, []);

    private static MappingValidationResult InvalidValidation(NativeArtifact artifact, params MappingDiagnostic[] issues)
        => new(MappingOperationStatus.InvalidMapping, Provenance(artifact, true), false, issues.Length, issues.Length, false, issues);

    private static ProjectionDocumentV1 Document(NativeArtifact artifact)
    {
        var provenance = Provenance(artifact, true);
        var revision = new ScopedRevisionId(artifact.Scope.OrganizationId.ToString("D"), artifact.Scope.ProjectId.ToString("D"),
            artifact.Scope.MappingId.ToString("D"), artifact.Scope.RevisionId.ToString("D"));
        var scalar = new FieldDataType(FieldDataTypeKind.Scalar, [ScalarDataType.String]);
        var cardinality = new FieldCardinality(CardinalityKind.ExactlyOne);
        return new ProjectionDocumentV1(new ProjectionProvenance(revision, new ArtifactDigest("sha256", artifact.Digest.Value),
            new BackendIdentity(provenance.BackendId, provenance.EngineRevision, provenance.BackendBuild), provenance.AdapterBuild, "fixture.projection.v1"),
            new ProjectionCoverage(CoverageStatus.Complete, []),
            [new ProjectionBoundary("input", "Input", BoundaryRole.PrimaryInput, "json", BoundaryShape.Scalar, "field:in", new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/source")),
                new ProjectionBoundary("output", "Output", BoundaryRole.PrimaryOutput, "json", BoundaryShape.Scalar, "field:out", new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/target"))],
            [new ProjectionField("field:in", "input", [], "Input", scalar, cardinality, FieldRequirement.Required, FieldNullability.NonNullable, new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/source/schema")),
                new ProjectionField("field:out", "output", [], "Output", scalar, cardinality, FieldRequirement.Required, FieldNullability.NonNullable, new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/target/schema"))],
            [], [new ProjectionRule("rule:one", "field:out", "Copy input.", [new FieldDependency("field:in")], [], new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/root/bindings/0"))], [],
            [new ProjectionRelationship("in-member", RelationshipKind.BoundaryMember, new FieldNodeReference("field:in"), new BoundaryNodeReference("input")),
                new ProjectionRelationship("out-member", RelationshipKind.BoundaryMember, new FieldNodeReference("field:out"), new BoundaryNodeReference("output")),
                new ProjectionRelationship("read", RelationshipKind.ReadsFrom, new RuleNodeReference("rule:one"), new FieldNodeReference("field:in")),
                new ProjectionRelationship("write", RelationshipKind.WritesTo, new RuleNodeReference("rule:one"), new FieldNodeReference("field:out"))], [], []);
    }

    private sealed class Backend : IMappingInspectionBackend, IMappingProjectionBackend
    {
        public Func<NativeArtifact, CancellationToken, Task<MappingProjectionResult>> Projection { get; set; }
            = (artifact, _) => Task.FromResult(SuccessfulProjection(artifact, Document(artifact)));
        public Func<NativeArtifact, CancellationToken, Task<MappingValidationResult>> Validation { get; set; }
            = (artifact, _) => Task.FromResult(new MappingValidationResult(MappingOperationStatus.Succeeded, Provenance(artifact, true), true, 0, 0, false, []));
        public NativeArtifact? ProjectionArtifact { get; private set; }
        public NativeArtifact? ValidationArtifact { get; private set; }
        public CancellationToken ProjectionToken { get; private set; }
        public CancellationToken ValidationToken { get; private set; }
        public int ProjectionCalls { get; private set; }
        public int ValidationCalls { get; private set; }
        public int InspectionCalls { get; private set; }

        public Task<MappingProjectionResult> ProjectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            ProjectionCalls++;
            ProjectionArtifact = artifact;
            ProjectionToken = cancellationToken;
            return Projection(artifact, cancellationToken);
        }
        public Task<MappingValidationResult> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            ValidationCalls++;
            ValidationArtifact = artifact;
            ValidationToken = cancellationToken;
            return Validation(artifact, cancellationToken);
        }
        public Task<MappingInspectionResult> InspectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            InspectionCalls++;
            throw new InvalidOperationException("Example reads do not request a third inspection operation.");
        }
    }
}
