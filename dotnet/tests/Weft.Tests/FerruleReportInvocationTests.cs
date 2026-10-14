using System.Buffers.Binary;
using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Authored synthetic transcripts and pure provider checks; no worker, store or database qualification.</summary>
public sealed class FerruleReportInvocationTests
{
    private const string Build = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly RevisionScope Scope = new(
        Guid.Parse("d4111111-1111-1111-1111-111111111111"),
        Guid.Parse("d4222222-2222-2222-2222-222222222222"),
        Guid.Parse("d4333333-3333-3333-3333-333333333333"),
        Guid.Parse("d4444444-4444-4444-4444-444444444444"));
    private static readonly JsonSerializerOptions WireOptions = new() { MaxDepth = 256 };
    private static readonly DocumentNativeLocator Document = new("entry.native.json");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReducedHostLimitsAndExactNativeBytesTravelOnlyInTheSelectedReportInvocation(bool validation)
    {
        var bytes = Encoding.UTF8.GetBytes("\uFEFF{ \"opaque\":9007199254740993 }\r\n");
        var artifact = Artifact(bytes);
        var runner = ReplyRunner();
        var backend = new FerruleInspectionBackend(runner);
        var policy = new RevisionReportPolicy(nativeByteLimit: bytes.Length, itemLimit: 40,
            diagnosticLimit: 3, textByteLimit: 64);
        using var cancellation = new CancellationTokenSource();

        Assert.Equal(MappingOperationStatus.Succeeded, await ReportStatus(backend, artifact, policy, validation, cancellation.Token));

        var reduced = Header(runner.Requests[0]);
        Assert.Equal(validation ? "validate" : "project", reduced["operation"]!.GetValue<string>());
        Assert.Equal(new[] { "maximumDiagnostics", "maximumItems", "maximumTextBytes" },
            reduced["limits"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(3, reduced["limits"]!["maximumDiagnostics"]!.GetValue<int>());
        Assert.Equal(40, reduced["limits"]!["maximumItems"]!.GetValue<int>());
        Assert.Equal(64, reduced["limits"]!["maximumTextBytes"]!.GetValue<int>());
        Assert.Equal(artifact.Digest.Value, reduced["nativeDigest"]!.GetValue<string>());
        Assert.Equal(bytes, NativeBytes(runner.Requests[0]));
        Assert.Equal(cancellation.Token, runner.Tokens[0]);
        Assert.Equal(MappingOperationStatus.Succeeded, (await backend.ValidateAsync(artifact)).Status);
        var defaults = Header(runner.Requests[1]);
        Assert.Equal(128, defaults["limits"]!["maximumDiagnostics"]!.GetValue<int>());
        Assert.Equal(8192, defaults["limits"]!["maximumItems"]!.GetValue<int>());
        Assert.Equal(2048, defaults["limits"]!["maximumTextBytes"]!.GetValue<int>());
        Assert.Equal(bytes, artifact.CopyBytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAndSerializedHeaderLimitsRefuseBeforeDispatchAndAdmitTheirExactBoundary(bool validation)
    {
        var artifact = Artifact([0xef, 0xbb, 0xbf, 0x7b, 0x7d]);
        var runner = ReplyRunner();
        var backend = new FerruleInspectionBackend(runner);
        Assert.Equal(MappingOperationStatus.ResourceLimit,
            await ReportStatus(backend, artifact, new RevisionReportPolicy(nativeByteLimit: 4), validation));
        Assert.Equal(MappingOperationStatus.ResourceLimit,
            await ReportStatus(backend, artifact, new RevisionReportPolicy(headerByteLimit: 1), validation));
        Assert.Empty(runner.Requests);

        Assert.Equal(MappingOperationStatus.Succeeded,
            await ReportStatus(backend, artifact, new RevisionReportPolicy(nativeByteLimit: 5), validation));
        var headerBytes = checked((int)BinaryPrimitives.ReadUInt32BigEndian(runner.Requests[0]));
        Assert.Equal(MappingOperationStatus.Succeeded,
            await ReportStatus(backend, artifact, new RevisionReportPolicy(headerByteLimit: headerBytes), validation));
        Assert.Equal(MappingOperationStatus.ResourceLimit,
            await ReportStatus(backend, artifact, new RevisionReportPolicy(headerByteLimit: headerBytes - 1), validation));
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(artifact.CopyBytes(), NativeBytes(runner.Requests[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteRawResponsePayloadAtTheReducedByteLimitIsAcceptedAndTheNextByteIsRefused(bool validation)
    {
        const int limit = 4096;
        foreach (var padding in new[] { 0, 1 })
        {
            var runner = ReplyRunner(payload: json =>
            {
                var encoded = Encoding.UTF8.GetBytes(json.ToJsonString(WireOptions));
                Assert.True(encoded.Length < limit);
                var padded = new byte[limit + padding];
                padded.AsSpan().Fill((byte)' ');
                encoded.CopyTo(padded.AsSpan());
                return padded;
            });
            var result = await ReportStatus(new FerruleInspectionBackend(runner), Artifact(),
                new RevisionReportPolicy(workerResponseByteLimit: limit), validation);
            Assert.Equal(padding == 0 ? MappingOperationStatus.Succeeded : MappingOperationStatus.ResourceLimit, result);
            Assert.Single(runner.Requests);
        }
    }

    [Theory]
    [InlineData(false, "depth")]
    [InlineData(true, "depth")]
    [InlineData(false, "arrayItems")]
    [InlineData(true, "arrayItems")]
    public async Task RawLimitsRunBeforeUnknownMemberTypedDecode(bool validation, string dimension)
    {
        SyntheticRunner MakeRunner() => ReplyRunner(response => response["notInTheProtocol"] = dimension == "depth"
            ? JsonNode.Parse("[[[[[[[[0]]]]]]]]")
            : new JsonArray(Enumerable.Range(0, 100).Select(index => (JsonNode?)JsonValue.Create(index)).ToArray()));
        var reduced = dimension == "depth"
            ? new RevisionReportPolicy(jsonDepthLimit: 7)
            : new RevisionReportPolicy(responseItemLimit: 30);

        Assert.Equal(MappingOperationStatus.ResourceLimit,
            await ReportStatus(new FerruleInspectionBackend(MakeRunner()), Artifact(), reduced, validation));
        Assert.Equal(MappingOperationStatus.ProtocolFailure,
            await ReportStatus(new FerruleInspectionBackend(MakeRunner()), Artifact(), new RevisionReportPolicy(), validation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawReportValueAndContainerDepthBoundariesAreExactAndLegacyDefaultsRemainUsable(bool validation)
    {
        var sample = Envelope(HeaderForCount(validation), validation ? RawValidation() : RawProjection());
        using var json = JsonDocument.Parse(sample.ToJsonString(WireOptions));
        var values = CountValues(json.RootElement);
        var containers = ContainerDepth(json.RootElement);
        // Report preflight counts all values; unchanged legacy invocation counts only array elements.
        Assert.True(values > CountArrayMembers(json.RootElement));
        if (!validation) Assert.Equal(14, CountArrayMembers(json.RootElement));
        var runner = ReplyRunner();
        var backend = new FerruleInspectionBackend(runner);
        Assert.Equal(MappingOperationStatus.Succeeded,
            await ReportStatus(backend, Artifact(), new RevisionReportPolicy(responseItemLimit: values, jsonDepthLimit: containers), validation));
        Assert.Equal(MappingOperationStatus.ResourceLimit,
            await ReportStatus(backend, Artifact(), new RevisionReportPolicy(responseItemLimit: values - 1), validation));
        Assert.Equal(MappingOperationStatus.ResourceLimit,
            await ReportStatus(backend, Artifact(), new RevisionReportPolicy(jsonDepthLimit: containers - 1), validation));
        Assert.Equal(MappingOperationStatus.Succeeded,
            validation ? (await backend.ValidateAsync(Artifact())).Status : (await backend.ProjectAsync(Artifact())).Status);
    }

    [Fact]
    public async Task ReducedTypedValidationAtItsExactCapsRetainsActualFalseValidityAndCounts()
    {
        var runner = ReplyRunner(response => response["result"] = RawValidation(2));
        var result = await new FerruleInspectionBackend(runner).ValidateReportAsync(Artifact(),
            new RevisionReportPolicy(itemLimit: 4, diagnosticLimit: 2, validationIssueLimit: 2, textByteLimit: 64));

        Assert.Equal(MappingOperationStatus.InvalidMapping, result.Status);
        Assert.False(result.IsValid);
        Assert.Equal(2, result.TotalIssueCount);
        Assert.Equal(2, result.EmittedIssueCount);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.False(result.Truncated);
        Assert.True(result.Provenance.Confirmed);
    }

    [Fact]
    public async Task ReducedProjectionDetailDepthAcceptsTheBoundaryAndRefusesTheNextNestedDetail()
    {
        SyntheticRunner MakeRunner() => ReplyRunner(response => response["result"]!["controls"] = JsonNode.Parse("""
            [{"id":"c","kind":"filter","summary":"filter","appliesTo":[],"dependencies":[],
              "details":[{"kind":"group","label":"a","children":[{"kind":"group","label":"b","children":[{"kind":"literal","value":{"kind":"boolean","value":true}}]}]}],
              "nativeLocator":{"kind":"document","documentId":"entry.native.json"}}]
            """));
        Assert.Equal(MappingOperationStatus.Succeeded,
            await ReportStatus(new FerruleInspectionBackend(MakeRunner()), Artifact(), new RevisionReportPolicy(detailDepthLimit: 2), false));
        Assert.Equal(MappingOperationStatus.ResourceLimit,
            await ReportStatus(new FerruleInspectionBackend(MakeRunner()), Artifact(), new RevisionReportPolicy(detailDepthLimit: 1), false));
    }

    [Theory]
    [InlineData(false, "items")]
    [InlineData(false, "diagnostics")]
    [InlineData(false, "text")]
    [InlineData(true, "items")]
    [InlineData(true, "diagnostics")]
    [InlineData(true, "text")]
    [InlineData(true, "issues")]
    public async Task TypedProviderReductionsAreEnforcedWithoutClipping(bool validation, string dimension)
    {
        var policy = dimension switch
        {
            "items" => new RevisionReportPolicy(itemLimit: validation ? 2 : 13),
            "diagnostics" => new RevisionReportPolicy(diagnosticLimit: 1),
            "text" => new RevisionReportPolicy(textByteLimit: 64),
            "issues" => new RevisionReportPolicy(validationIssueLimit: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        };
        var runner = ReplyRunner(response =>
        {
            if (validation) response["result"] = RawValidation(2, dimension == "text" ? new string('é', 33) : "problem");
            else if (dimension == "diagnostics")
                response["result"]!["diagnostics"] = JsonNode.Parse("""
                    [{"code":"a","severity":"warning","message":"one","nativeLocator":null,"projectionLocator":null},
                     {"code":"b","severity":"warning","message":"two","nativeLocator":null,"projectionLocator":null}]
                    """);
            else if (dimension == "text") response["result"]!["boundaries"]![0]!["name"] = new string('é', 33);
        });

        var backend = new FerruleInspectionBackend(runner);
        if (validation)
        {
            var result = await backend.ValidateReportAsync(Artifact(), policy);
            Assert.Equal(MappingOperationStatus.ResourceLimit, result.Status);
            Assert.Null(result.IsValid);
            Assert.False(result.Provenance.Confirmed);
            Assert.Equal("mapping.inspection.limit", Assert.Single(result.Diagnostics).Code);
        }
        else
        {
            var result = await backend.ProjectReportAsync(Artifact(), policy);
            Assert.Equal(MappingOperationStatus.ResourceLimit, result.Status);
            Assert.Null(result.Projection);
            Assert.False(result.Provenance.Confirmed);
            Assert.Equal("mapping.inspection.limit", Assert.Single(result.Diagnostics).Code);
        }
    }

    [Theory]
    [InlineData(false, WorkerProcessFailure.Canceled, MappingOperationStatus.Canceled)]
    [InlineData(true, WorkerProcessFailure.TimedOut, MappingOperationStatus.TimedOut)]
    [InlineData(false, WorkerProcessFailure.StartFailed, MappingOperationStatus.BackendUnavailable)]
    [InlineData(true, WorkerProcessFailure.SandboxUnavailable, MappingOperationStatus.BackendUnavailable)]
    [InlineData(false, WorkerProcessFailure.WorkerFailed, MappingOperationStatus.BackendFailure)]
    [InlineData(true, WorkerProcessFailure.OutputLimitExceeded, MappingOperationStatus.ResourceLimit)]
    [InlineData(false, WorkerProcessFailure.CleanupFailed, MappingOperationStatus.IsolationFailure)]
    public async Task ReportInvocationRetainsDefaultTransportFailureClassifications(bool validation,
        WorkerProcessFailure failure, MappingOperationStatus expected)
    {
        var runner = new SyntheticRunner((_, _) => new WorkerProcessResult([], failure));
        var backend = new FerruleInspectionBackend(runner);
        Assert.Equal(expected, await ReportStatus(backend, Artifact(), new RevisionReportPolicy(), validation));
        Assert.Equal(expected, validation ? (await backend.ValidateAsync(Artifact())).Status : (await backend.ProjectAsync(Artifact())).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureOutranksCancellationFromTheSyntheticRunner(bool validation)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new SyntheticRunner((_, _) =>
        {
            cancellation.Cancel();
            return new WorkerProcessResult([], WorkerProcessFailure.CleanupFailed);
        });
        Assert.Equal(MappingOperationStatus.IsolationFailure,
            await ReportStatus(new FerruleInspectionBackend(runner), Artifact(), new RevisionReportPolicy(), validation, cancellation.Token));
        Assert.Single(runner.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeAdmissionAndAfterRunnerReturnDoesNotExposeAReport(bool validation)
    {
        using var before = new CancellationTokenSource();
        before.Cancel();
        var untouched = ReplyRunner();
        Assert.Equal(MappingOperationStatus.Canceled,
            await ReportStatus(new FerruleInspectionBackend(untouched), Artifact(), new RevisionReportPolicy(), validation, before.Token));
        Assert.Empty(untouched.Requests);

        using var after = new CancellationTokenSource();
        var completed = ReplyRunner(_ => after.Cancel());
        Assert.Equal(MappingOperationStatus.Canceled,
            await ReportStatus(new FerruleInspectionBackend(completed), Artifact(), new RevisionReportPolicy(), validation, after.Token));
        Assert.Single(completed.Requests);
    }

    [Fact]
    public void StoredProjectionHookAcceptsHistoricalProvenanceAndFiniteBoundaryBitsWithoutARunner()
    {
        var document = TypedProjection() with
        {
            Parameters = [new("p", "p", new(FieldDataTypeKind.Scalar, [ScalarDataType.BinaryFloat]),
                FieldRequirement.Optional, new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "8000000000000000"),
                new GraphNodeNativeLocator("entry.native.json", "4294967295", "18446744073709551615"))]
        };

        FerruleInspectionBackend.ValidateStoredProjectionDocument(document, new RevisionReportPolicy());

        Assert.Equal("historical.engine", document.Provenance.Backend.Revision);
        Assert.Equal("historical.adapter", document.Provenance.AdapterVersion);
        Assert.Equal("8000000000000000", Assert.IsType<ExactNumberLiteralValue>(Assert.Single(document.Parameters).DefaultValue).Value);
    }

    [Theory]
    [InlineData("7ff0000000000000")]
    [InlineData("fff0000000000000")]
    [InlineData("7ff8000000000042")]
    [InlineData("000000000000000A")]
    public void StoredProjectionHookRetainsFerruleFiniteOnlyLowercaseBinary64Policy(string bits)
    {
        var document = TypedProjection() with
        {
            Parameters = [new("p", "p", new(FieldDataTypeKind.Scalar, [ScalarDataType.BinaryFloat]),
                FieldRequirement.Optional, new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, bits), Document)]
        };
        Refuses(RevisionReportValidationFailure.Integrity,
            () => FerruleInspectionBackend.ValidateStoredProjectionDocument(document, new RevisionReportPolicy()));
    }

    [Theory]
    [InlineData("4294967296", null)]
    [InlineData("1", "18446744073709551616")]
    [InlineData("01", null)]
    public void StoredProjectionHookRetainsNativeNodeWidthsAndCanonicalDecimalIds(string node, string? function)
    {
        var document = TypedProjection() with
        {
            Boundaries = [TypedProjection().Boundaries[0] with { NativeLocator = new GraphNodeNativeLocator("entry.native.json", node, function) },
                TypedProjection().Boundaries[1]]
        };
        Refuses(RevisionReportValidationFailure.Integrity,
            () => FerruleInspectionBackend.ValidateStoredProjectionDocument(document, new RevisionReportPolicy()));
    }

    [Theory]
    [InlineData("boundaryOrder")]
    [InlineData("unavailableGraph")]
    [InlineData("completeGap")]
    [InlineData("partialNoGap")]
    [InlineData("executingCapability")]
    [InlineData("wrongProjectProfile")]
    [InlineData("wrongOutputCapability")]
    public void StoredProjectionHookRetainsProviderCoverageOrderingAndCapabilityRestrictions(string corruption)
    {
        var document = TypedProjection();
        document = corruption switch
        {
            "boundaryOrder" => document with { Boundaries = document.Boundaries.Reverse().ToArray() },
            "unavailableGraph" => document with { Coverage = new(CoverageStatus.Unavailable, [new("gap", "unknown", Document)]) },
            "completeGap" => document with { Coverage = new(CoverageStatus.Complete, [new("gap", "unknown", Document)]) },
            "partialNoGap" => document with { Coverage = new(CoverageStatus.Partial, []) },
            "executingCapability" => document with
            {
                Capabilities = document.Capabilities.Select(capability =>
                capability.Operation == MappingOperation.Build ? capability with { Status = CapabilityStatus.Supported } : capability).ToArray()
            },
            "wrongProjectProfile" => document with
            {
                Capabilities = document.Capabilities.Select(capability =>
                capability.Operation == MappingOperation.Project ? capability with { Profile = "other" } : capability).ToArray()
            },
            "wrongOutputCapability" => document with
            {
                Capabilities = document.Capabilities.Select(capability =>
                capability.Operation == MappingOperation.Preview ? capability with { BoundaryIds = ["in"] } : capability).ToArray()
            },
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        Refuses(RevisionReportValidationFailure.Integrity,
            () => FerruleInspectionBackend.ValidateStoredProjectionDocument(document, new RevisionReportPolicy()));
    }

    [Fact]
    public void StoredUnavailableProjectionNeedsAnActualGapAndNoGraph()
    {
        var document = TypedProjection() with
        {
            Coverage = new(CoverageStatus.Unavailable, [new("gap", "unknown", Document)]),
            Boundaries = [],
            Capabilities = TypedCapabilities([])
        };
        FerruleInspectionBackend.ValidateStoredProjectionDocument(document, new RevisionReportPolicy());
        Assert.Equal(CoverageStatus.Unavailable, document.Coverage.Status);
        Assert.Single(document.Coverage.Gaps);
    }

    [Fact]
    public void StoredNestedUnsupportedDetailNeedsItsActualNativeGapAndOwnerLink()
    {
        var native = new GraphNodeNativeLocator("entry.native.json", "2");
        var owner = new ControlNodeReference("c");
        var document = TypedProjection() with
        {
            Coverage = new(CoverageStatus.Partial, [new("gap", "unsupported", native, owner)]),
            Controls = [new("c", ControlKind.Filter, "filter", [], [],
                [new GroupDetail("nested", [new UnsupportedDetail("construct", "unsupported", native)])],
                new GraphNodeNativeLocator("entry.native.json", "1"))]
        };
        FerruleInspectionBackend.ValidateStoredProjectionDocument(document, new RevisionReportPolicy());
        var unproven = document with
        {
            Coverage = new(CoverageStatus.Partial,
            [new("gap", "unsupported", new GraphNodeNativeLocator("entry.native.json", "1"), owner)])
        };
        Refuses(RevisionReportValidationFailure.Integrity,
            () => FerruleInspectionBackend.ValidateStoredProjectionDocument(unproven, new RevisionReportPolicy()));
    }

    [Fact]
    public void StoredProjectionAggregateTextDiagnosticAndDetailBoundariesAreEffective()
    {
        var basic = TypedProjection();
        FerruleInspectionBackend.ValidateStoredProjectionDocument(basic, new RevisionReportPolicy(itemLimit: 14));
        Refuses(RevisionReportValidationFailure.ResourceLimit,
            () => FerruleInspectionBackend.ValidateStoredProjectionDocument(basic, new RevisionReportPolicy(itemLimit: 13)));
        var text = basic with { Boundaries = [basic.Boundaries[0] with { Name = new string('é', 32) }, basic.Boundaries[1]] };
        FerruleInspectionBackend.ValidateStoredProjectionDocument(text, new RevisionReportPolicy(textByteLimit: 64));
        Refuses(RevisionReportValidationFailure.ResourceLimit,
            () => FerruleInspectionBackend.ValidateStoredProjectionDocument(text, new RevisionReportPolicy(textByteLimit: 63)));
        var diagnostics = basic with { Diagnostics = [new("a", DiagnosticSeverity.Warning, "one"), new("b", DiagnosticSeverity.Warning, "two")] };
        Refuses(RevisionReportValidationFailure.ResourceLimit,
            () => FerruleInspectionBackend.ValidateStoredProjectionDocument(diagnostics, new RevisionReportPolicy(diagnosticLimit: 1)));
        var detail = new GroupDetail("a", [new GroupDetail("b", [new LiteralDetail(new BooleanLiteralValue(true))])]);
        var explained = basic with { Controls = [new("c", ControlKind.Filter, "filter", [], [], [detail], Document)] };
        FerruleInspectionBackend.ValidateStoredProjectionDocument(explained, new RevisionReportPolicy(detailDepthLimit: 2));
        Refuses(RevisionReportValidationFailure.ResourceLimit,
            () => FerruleInspectionBackend.ValidateStoredProjectionDocument(explained, new RevisionReportPolicy(detailDepthLimit: 1)));
    }

    [Fact]
    public void StoredValidationHookPreservesActualFalseAndNullValidityWithHistoricalProvenance()
    {
        var invalid = TypedValidation(1);
        FerruleInspectionBackend.ValidateStoredValidationReport(invalid, new RevisionReportPolicy());
        var operational = invalid with
        {
            Status = OperationStatusV1.InvalidMapping,
            IsValid = null,
            TotalIssueCount = 0,
            EmittedIssueCount = 0,
            Diagnostics = [new("ferrule.nativeDecodeFailed", DiagnosticSeverity.Error, "native refused", false, Document)]
        };
        FerruleInspectionBackend.ValidateStoredValidationReport(operational, new RevisionReportPolicy());
        Assert.False(invalid.IsValid);
        Assert.Null(operational.IsValid);
        Assert.Equal("historical.engine", operational.Provenance!.Backend.Revision);
    }

    [Theory]
    [InlineData("diagnostics")]
    [InlineData("issues")]
    [InlineData("ownerItems")]
    [InlineData("schemaPathItems")]
    [InlineData("scopePathItems")]
    [InlineData("text")]
    public void StoredValidationHookUsesReducedDiagnosticIssueAggregateAndUtf8Caps(string dimension)
    {
        var report = TypedValidation(2);
        var policy = dimension switch
        {
            "diagnostics" => new RevisionReportPolicy(diagnosticLimit: 1),
            "issues" => new RevisionReportPolicy(validationIssueLimit: 1),
            "ownerItems" => new RevisionReportPolicy(itemLimit: 3),
            "schemaPathItems" or "scopePathItems" => new RevisionReportPolicy(itemLimit: 3),
            "text" => new RevisionReportPolicy(textByteLimit: 64),
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        };
        if (dimension == "text") report = report with { Diagnostics = report.Diagnostics.Select(diagnostic => diagnostic with { Message = new string('é', 33) }).ToArray() };
        if (dimension is "schemaPathItems" or "scopePathItems")
        {
            report = TypedValidation(1);
            NativeDiagnosticOwnerV1 owner = dimension == "schemaPathItems"
                ? new SchemaNodeNativeOwnerV1(new SourceNativeEndpointV1(), [new ChildSchemaNativeOwnerStepV1("0"), new ChildSchemaNativeOwnerStepV1("1")])
                : new ScopeNativeOwnerV1(new TargetNativeEndpointV1(), [new ChildScopeNativeOwnerStepV1("0"), new SegmentScopeNativeOwnerStepV1("1")]);
            report = report with { Diagnostics = [report.Diagnostics[0] with { Owner = owner }] };
        }
        Refuses(RevisionReportValidationFailure.ResourceLimit,
            () => FerruleInspectionBackend.ValidateStoredValidationReport(report, policy));
    }

    [Theory]
    [InlineData("nullSucceeded")]
    [InlineData("falseZeroIssues")]
    [InlineData("nonErrorSeverity")]
    [InlineData("wrongOwnerNode")]
    public void StoredValidationHookDoesNotAdmitContradictoryActualValidationFacts(string corruption)
    {
        var report = TypedValidation(1);
        report = corruption switch
        {
            "nullSucceeded" => report with { IsValid = null, Status = OperationStatusV1.Succeeded },
            "falseZeroIssues" => report with { TotalIssueCount = 0, EmittedIssueCount = 0, Diagnostics = [] },
            "nonErrorSeverity" => report with { Diagnostics = [report.Diagnostics[0] with { Severity = DiagnosticSeverity.Warning }] },
            "wrongOwnerNode" => report with { Diagnostics = [report.Diagnostics[0] with { Owner = new GraphNodeNativeOwnerV1(null, "2") }] },
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        Refuses(RevisionReportValidationFailure.Integrity,
            () => FerruleInspectionBackend.ValidateStoredValidationReport(report, new RevisionReportPolicy()));
    }

    [Theory]
    [InlineData("boundaries")]
    [InlineData("path")]
    [InlineData("scalarTypes")]
    [InlineData("details")]
    [InlineData("conditions")]
    [InlineData("diagnostics")]
    public void StoredProjectionLoopsObserveCancellationDuringCallerCollectionTraversal(string dimension)
    {
        using var cancellation = new CancellationTokenSource();
        var document = TypedProjection();
        switch (dimension)
        {
            case "boundaries": document = document with { Boundaries = new CancelOnRead<ProjectionBoundary>(document.Boundaries, cancellation) }; break;
            case "path": document = WithOutputField(document, new CancelOnRead<PathSegment>([new FieldPathSegment("a"), new FieldPathSegment("b")], cancellation)); break;
            case "scalarTypes":
                document = document with
                {
                    Parameters = [new("p", "p", new(FieldDataTypeKind.ScalarUnion,
                new CancelOnRead<ScalarDataType>([ScalarDataType.String, ScalarDataType.Integer], cancellation)), FieldRequirement.Optional, null, Document)]
                }; break;
            case "details":
                document = document with
                {
                    Controls = [new("c", ControlKind.Filter, "filter", [], [],
                new CancelOnRead<DetailNode>([new LiteralDetail(new BooleanLiteralValue(true)), new LiteralDetail(new BooleanLiteralValue(false))], cancellation), Document)]
                }; break;
            case "conditions":
                document = document with
                {
                    Capabilities = document.Capabilities.Select(capability =>
                capability.Operation == MappingOperation.Inspect ? capability with { Conditions = new CancelOnRead<CapabilityCondition>([new("a", "one"), new("b", "two")], cancellation) } : capability).ToArray()
                }; break;
            case "diagnostics":
                document = document with
                {
                    Diagnostics = new CancelOnRead<ProjectionDiagnostic>(
                [new("a", DiagnosticSeverity.Warning, "one"), new("b", DiagnosticSeverity.Warning, "two")], cancellation)
                }; break;
            default: throw new ArgumentOutOfRangeException(nameof(dimension));
        }
        Assert.ThrowsAny<OperationCanceledException>(() =>
            FerruleInspectionBackend.ValidateStoredProjectionDocument(document, new RevisionReportPolicy(), cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoredValidationLoopsObserveCancellationDuringIssueAndOwnerPathTraversal(bool ownerPath)
    {
        using var cancellation = new CancellationTokenSource();
        var report = TypedValidation(2);
        report = ownerPath
            ? report with
            {
                Diagnostics = [report.Diagnostics[0] with
                { Owner = new SchemaNodeNativeOwnerV1(new SourceNativeEndpointV1(),
                    new CancelOnRead<SchemaNativeOwnerStepV1>([new ChildSchemaNativeOwnerStepV1("0"), new ChildSchemaNativeOwnerStepV1("1")], cancellation)) }, report.Diagnostics[1]]
            }
            : report with { Diagnostics = new CancelOnRead<ValidationDiagnosticV1>(report.Diagnostics, cancellation) };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            FerruleInspectionBackend.ValidateStoredValidationReport(report, new RevisionReportPolicy(), cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void BothStoredHooksObservePreCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            FerruleInspectionBackend.ValidateStoredProjectionDocument(TypedProjection(), new RevisionReportPolicy(), cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            FerruleInspectionBackend.ValidateStoredValidationReport(TypedValidation(1), new RevisionReportPolicy(), cancellation.Token));
    }

    private static async Task<MappingOperationStatus> ReportStatus(FerruleInspectionBackend backend, NativeArtifact artifact,
        RevisionReportPolicy policy, bool validation, CancellationToken cancellationToken = default) => validation
        ? (await backend.ValidateReportAsync(artifact, policy, cancellationToken)).Status
        : (await backend.ProjectReportAsync(artifact, policy, cancellationToken)).Status;

    private static NativeArtifact Artifact(byte[]? bytes = null) => NativeArtifact.Admit(Scope, bytes ?? "{}"u8.ToArray(), 4194304);

    private static JsonObject Header(byte[] frame)
    {
        var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame));
        return JsonNode.Parse(frame.AsSpan(4, size))!.AsObject();
    }

    private static byte[] NativeBytes(byte[] frame)
    {
        var headerBytes = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame));
        var nativeBytes = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4 + headerBytes)));
        Assert.Equal(8 + headerBytes + nativeBytes, frame.Length);
        return frame[(8 + headerBytes)..];
    }

    private static byte[] Frame(byte[] payload)
    {
        var result = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, checked((uint)payload.Length));
        payload.CopyTo(result.AsSpan(4));
        return result;
    }

    private static SyntheticRunner ReplyRunner(Action<JsonObject>? mutate = null, Func<JsonObject, byte[]>? payload = null) => new((frame, _) =>
    {
        var request = Header(frame);
        var response = Envelope(request, request["operation"]!.GetValue<string>() == "validate" ? RawValidation() : RawProjection());
        mutate?.Invoke(response);
        return new WorkerProcessResult(Frame(payload?.Invoke(response) ?? Encoding.UTF8.GetBytes(response.ToJsonString(WireOptions))), WorkerProcessFailure.None);
    });

    private static JsonObject Envelope(JsonObject header, JsonNode result) => new()
    {
        ["protocolVersion"] = "weft.ferrule-host.v1",
        ["requestId"] = header["requestId"]!.DeepClone(),
        ["operation"] = header["operation"]!.DeepClone(),
        ["revision"] = header["revision"]!.DeepClone(),
        ["nativeDigest"] = header["nativeDigest"]!.DeepClone(),
        ["documentId"] = "entry.native.json",
        ["engineRevision"] = "0495822e102bba3f97d9bffd3c665adfb6138bec",
        ["result"] = result,
        ["failure"] = null
    };

    private static JsonObject HeaderForCount(bool validation) => new()
    {
        ["requestId"] = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
        ["operation"] = validation ? "validate" : "project",
        ["revision"] = new JsonObject
        {
            ["organizationId"] = Scope.OrganizationId.ToString("D"),
            ["projectId"] = Scope.ProjectId.ToString("D"),
            ["mappingId"] = Scope.MappingId.ToString("D"),
            ["revisionId"] = Scope.RevisionId.ToString("D")
        },
        ["nativeDigest"] = Artifact().Digest.Value
    };

    private static JsonObject RawProjection() => JsonNode.Parse("""
        {"kind":"project","coverage":{"status":"complete","gaps":[]},
         "boundaries":[
          {"id":"in","name":"input","role":"primaryInput","format":"json","shape":"object","rootFieldId":null,"nativeLocator":{"kind":"document","documentId":"entry.native.json"}},
          {"id":"out","name":"output","role":"primaryOutput","format":"json","shape":"object","rootFieldId":null,"nativeLocator":{"kind":"document","documentId":"entry.native.json"}}],
         "fields":[],"parameters":[],"rules":[],"controls":[],"relationships":[],"diagnostics":[],
         "capabilities":[
          {"operation":"inspect","status":"supported","profile":"p","boundaryIds":[],"conditions":[{"code":"c","description":"d"}]},
          {"operation":"project","status":"supported","profile":"ferrule.projection.v1","boundaryIds":[],"conditions":[{"code":"boundedReadOnlyProjection","description":"d"}]},
          {"operation":"validate","status":"supported","profile":"p","boundaryIds":[],"conditions":[{"code":"c","description":"d"}]},
          {"operation":"preview","status":"notQualified","profile":"p","boundaryIds":["out"],"conditions":[{"code":"c","description":"d"}]},
          {"operation":"build","status":"notQualified","profile":"p","boundaryIds":["out"],"conditions":[{"code":"c","description":"d"}]}]}
        """)!.AsObject();

    private static JsonObject RawValidation(int count = 0, string message = "problem")
    {
        var issues = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            var issue = JsonNode.Parse("""
                {"code":"ferrule.validation","location":"at node","locationTruncated":false,"message":"problem","messageTruncated":false,
                 "owner":{"kind":"graphNode","functionId":null,"nodeId":"1"},"nativeLocator":{"kind":"graphNode","documentId":"entry.native.json","nodeId":"1","functionId":null}}
                """)!.AsObject();
            issue["message"] = message;
            issues.Add(issue);
        }
        return new JsonObject
        {
            ["kind"] = "validate",
            ["isValid"] = count == 0,
            ["totalIssueCount"] = count,
            ["emittedIssueCount"] = count,
            ["truncated"] = false,
            ["issues"] = issues
        };
    }

    private static int CountArrayMembers(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.GetArrayLength() + value.EnumerateArray().Sum(CountArrayMembers),
        JsonValueKind.Object => value.EnumerateObject().Sum(property => CountArrayMembers(property.Value)),
        _ => 0
    };

    private static int CountValues(JsonElement value) => 1 + (value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().Sum(CountValues),
        JsonValueKind.Object => value.EnumerateObject().Sum(property => CountValues(property.Value)),
        _ => 0
    });

    private static int ContainerDepth(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => 1 + value.EnumerateArray().Select(ContainerDepth).DefaultIfEmpty(0).Max(),
        JsonValueKind.Object => 1 + value.EnumerateObject().Select(property => ContainerDepth(property.Value)).DefaultIfEmpty(0).Max(),
        _ => 0
    };

    private static ProjectionProvenance HistoricalProjectionProvenance() => new(
        new(Scope.OrganizationId.ToString("D"), Scope.ProjectId.ToString("D"), Scope.MappingId.ToString("D"), Scope.RevisionId.ToString("D")),
        new("sha256", Artifact().Digest.Value), new("ferrule", "historical.engine", "historical.build"),
        "historical.adapter", "weft.ferrule-projection.v1");

    private static ProjectionDocumentV1 TypedProjection() => new(HistoricalProjectionProvenance(), new(CoverageStatus.Complete, []),
        [new("in", "input", BoundaryRole.PrimaryInput, "json", BoundaryShape.Object, null, Document),
         new("out", "output", BoundaryRole.PrimaryOutput, "json", BoundaryShape.Object, null, Document)],
        [], [], [], [], [], [], TypedCapabilities(["out"]));

    private static OperationCapability[] TypedCapabilities(string[] outputs) =>
    [
        new(MappingOperation.Inspect, CapabilityStatus.Supported, "p", [], [new("c", "d")]),
        new(MappingOperation.Project, CapabilityStatus.Supported, "ferrule.projection.v1", [], [new("boundedReadOnlyProjection", "d")]),
        new(MappingOperation.Validate, CapabilityStatus.Supported, "p", [], [new("c", "d")]),
        new(MappingOperation.Preview, CapabilityStatus.NotQualified, "p", outputs, [new("c", "d")]),
        new(MappingOperation.Build, CapabilityStatus.NotQualified, "p", outputs, [new("c", "d")])
    ];

    private static ProjectionDocumentV1 WithOutputField(ProjectionDocumentV1 document, IReadOnlyList<PathSegment> path) => document with
    {
        Fields = [new("f", "out", path, "f", new(FieldDataTypeKind.Scalar, [ScalarDataType.String]),
            new(CardinalityKind.ExactlyOne), FieldRequirement.Required, FieldNullability.NonNullable, Document)],
        Relationships = [new("member", RelationshipKind.BoundaryMember, new FieldNodeReference("f"), new BoundaryNodeReference("out"))]
    };

    private static ValidationReportV1 TypedValidation(int count)
    {
        var projection = HistoricalProjectionProvenance();
        return new(OperationStatusV1.InvalidMapping,
            new(projection.Revision, projection.NativeArtifactDigest, projection.Backend, projection.AdapterVersion, true),
            false, count, count, false,
            Enumerable.Range(0, count).Select(_ => new ValidationDiagnosticV1("ferrule.validation", DiagnosticSeverity.Error,
                "problem", false, new GraphNodeNativeLocator("entry.native.json", "1"), new GraphNodeNativeOwnerV1(null, "1"), "at node")).ToArray());
    }

    private static void Refuses(RevisionReportValidationFailure failure, Action action) =>
        Assert.Equal(failure, Assert.Throws<RevisionReportValidationException>(action).Failure);

    private sealed class SyntheticRunner(Func<byte[], CancellationToken, WorkerProcessResult> respond) : IInspectionWorkerRunner
    {
        public string BuildIdentity => Build;
        public List<byte[]> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            var frame = requestFrame.ToArray();
            Requests.Add(frame);
            Tokens.Add(cancellationToken);
            return Task.FromResult(respond(frame, cancellationToken));
        }
    }

    private sealed class CancelOnRead<T>(IReadOnlyList<T> values, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => values.Count;
        public T this[int index]
        {
            get
            {
                if (index == 0) cancellation.Cancel();
                return values[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < Count; index++) yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
