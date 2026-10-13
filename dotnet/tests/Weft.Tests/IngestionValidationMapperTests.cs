using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Ingestion;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Synthetic mapper models exercise representation/failure policy, not actual native admission.</summary>
public sealed class IngestionValidationMapperTests
{
    private static readonly RevisionScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Sha256Digest Digest = Sha256Digest.FromBytes("synthetic-native"u8);
    private static MappingOperationProvenance Provenance => new(Scope, Digest, "synthetic.validator",
        "synthetic.engine", "synthetic.worker", "synthetic.adapter", true);

    [Theory]
    [InlineData(MappingOperationStatus.Succeeded, IngestionBackendStatus.Succeeded)]
    [InlineData(MappingOperationStatus.InvalidMapping, IngestionBackendStatus.InvalidMapping)]
    [InlineData(MappingOperationStatus.UnsupportedArtifact, IngestionBackendStatus.UnsupportedArtifact)]
    [InlineData(MappingOperationStatus.ResourceLimit, IngestionBackendStatus.ResourceLimit)]
    [InlineData(MappingOperationStatus.ProtocolFailure, IngestionBackendStatus.ProtocolFailure)]
    [InlineData(MappingOperationStatus.BackendUnavailable, IngestionBackendStatus.BackendUnavailable)]
    [InlineData(MappingOperationStatus.BackendFailure, IngestionBackendStatus.BackendFailure)]
    [InlineData(MappingOperationStatus.TimedOut, IngestionBackendStatus.TimedOut)]
    [InlineData(MappingOperationStatus.Canceled, IngestionBackendStatus.Canceled)]
    [InlineData(MappingOperationStatus.IsolationFailure, IngestionBackendStatus.IsolationFailure)]
    public void SyntheticKnownStatusesPreserveActualNullableFacts(MappingOperationStatus status, IngestionBackendStatus expected)
    {
        var actualProvenance = Provenance with { Confirmed = false };
        var result = new MappingValidationResult(status, actualProvenance, null, 0, 0, false, [Diagnostic()]);

        var evidence = Evidence(result);

        Assert.Equal(expected, evidence.Facts.Status);
        Assert.Null(evidence.Facts.IsValid);
        Assert.Equal(0, evidence.Facts.TotalIssueCount);
        Assert.Equal(0, evidence.Facts.EmittedIssueCount);
        Assert.False(evidence.Facts.Truncated);
        Assert.Equal(Scope, evidence.Facts.Provenance.Scope);
        Assert.Equal(Digest, evidence.Facts.Provenance.NativeDigest);
        Assert.Equal(new IngestionBackendProfile("synthetic.validator", "synthetic.engine", "synthetic.worker", "synthetic.adapter"),
            evidence.Facts.Provenance.BackendProfile);
        Assert.False(evidence.Facts.Provenance.Confirmed);
        Assert.Single(evidence.CopyDocument().GetProperty("diagnostics").EnumerateArray());
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void SyntheticValidityIsNotInferredFromIssueCount(bool validity, int issues)
    {
        var result = new MappingValidationResult(validity ? MappingOperationStatus.Succeeded : MappingOperationStatus.InvalidMapping,
            Provenance, validity, issues, issues, false, issues == 0 ? [] : [Diagnostic()]);

        var evidence = Evidence(result);

        Assert.Equal(validity, evidence.Facts.IsValid);
        Assert.Equal(issues, evidence.Facts.TotalIssueCount);
        Assert.Equal(issues, evidence.Facts.EmittedIssueCount);
    }

    [Fact]
    public void SyntheticEvidenceRetainsEveryDiagnosticFieldAndLargeDecimalIdentity()
    {
        const string node = "18446744073709551615";
        const string function = "9007199254740993";
        var source = new MappingDiagnostic("synthetic.validation", DiagnosticSeverity.Warning, "Exact message é 🙂", true,
            new GraphNodeNativeLocator(IngestionIntent.EntryDocument, node, function),
            new GraphNodeDiagnosticOwner(function, node), "function/path", true);
        var result = new MappingValidationResult(MappingOperationStatus.InvalidMapping, Provenance, false, 3, 1, true, [source]);

        var evidence = Evidence(result);
        var diagnostic = evidence.CopyDocument().GetProperty("diagnostics")[0];

        Assert.Equal("synthetic.validation", diagnostic.GetProperty("code").GetString());
        Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
        Assert.Equal(source.Message, diagnostic.GetProperty("message").GetString());
        Assert.True(diagnostic.GetProperty("messageTruncated").GetBoolean());
        Assert.Equal("function/path", diagnostic.GetProperty("location").GetString());
        Assert.True(diagnostic.GetProperty("locationTruncated").GetBoolean());
        Assert.Equal("graphNode", diagnostic.GetProperty("owner").GetProperty("kind").GetString());
        Assert.Equal(node, diagnostic.GetProperty("owner").GetProperty("nodeId").GetString());
        Assert.Equal(function, diagnostic.GetProperty("owner").GetProperty("functionId").GetString());
        Assert.Equal(node, diagnostic.GetProperty("nativeLocator").GetProperty("nodeId").GetString());
        Assert.Equal(function, diagnostic.GetProperty("nativeLocator").GetProperty("functionId").GetString());
        Assert.Equal(3, evidence.Facts.TotalIssueCount);
        Assert.Equal(1, evidence.Facts.EmittedIssueCount);
        Assert.True(evidence.Facts.Truncated);
        var json = Encoding.UTF8.GetString(evidence.CopyUtf8());
        Assert.DoesNotContain("projection", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyntheticAllOwnerEndpointAndPathShapesRemainTyped()
    {
        MappingDiagnosticOwner[] owners =
        [
            new GraphNodeDiagnosticOwner(null, "9"), new UserFunctionDiagnosticOwner("9007199254740993"),
            new EndpointDiagnosticOwner(new SourceMappingEndpoint()),
            new EndpointDiagnosticOwner(new TargetMappingEndpoint()),
            new EndpointDiagnosticOwner(new NamedSourceMappingEndpoint("12", "named source")),
            new EndpointDiagnosticOwner(new NamedTargetMappingEndpoint("13", "named target")),
            new SchemaNodeDiagnosticOwner(new SourceMappingEndpoint(),
                [new ChildSchemaOwnerStep("0"), new DynamicFieldSchemaOwnerStep(), new ContainsSchemaOwnerStep("2"),
                    new DependentSchemaOwnerStep("3")]),
            new ScopeDiagnosticOwner(new NamedTargetMappingEndpoint("14", "target"),
                [new ChildScopeOwnerStep("0"), new DynamicChildScopeOwnerStep("1"), new SegmentScopeOwnerStep("2")]),
            new FailureRuleDiagnosticOwner("18446744073709551615")
        ];
        var evidence = Evidence(Result(owners.Select(owner => Diagnostic() with { Owner = owner }).ToArray()));
        var diagnostics = evidence.CopyDocument().GetProperty("diagnostics");

        Assert.Equal("source", diagnostics[2].GetProperty("owner").GetProperty("endpoint").GetProperty("kind").GetString());
        Assert.Equal("target", diagnostics[3].GetProperty("owner").GetProperty("endpoint").GetProperty("kind").GetString());
        Assert.Equal("namedSource", diagnostics[4].GetProperty("owner").GetProperty("endpoint").GetProperty("kind").GetString());
        Assert.Equal("named target", diagnostics[5].GetProperty("owner").GetProperty("endpoint").GetProperty("name").GetString());
        Assert.Equal(new[] { "child", "dynamicField", "contains", "dependentSchema" }, diagnostics[6].GetProperty("owner")
            .GetProperty("path").EnumerateArray().Select(step => step.GetProperty("kind").GetString()));
        Assert.Equal(new[] { "child", "dynamicChild", "segment" }, diagnostics[7].GetProperty("owner")
            .GetProperty("path").EnumerateArray().Select(step => step.GetProperty("kind").GetString()));
        Assert.Equal("18446744073709551615", diagnostics[8].GetProperty("owner").GetProperty("index").GetString());
    }

    [Fact]
    public void SyntheticAllNativeLocatorShapesRemainExact()
    {
        NativeLocator[] locators =
        [
            new DocumentNativeLocator(IngestionIntent.EntryDocument),
            new JsonPointerNativeLocator(IngestionIntent.EntryDocument, "/a~1b/~0c"),
            new GraphNodeNativeLocator(IngestionIntent.EntryDocument, "9007199254740993"),
            new TextSpanNativeLocator(IngestionIntent.EntryDocument, "9007199254740993", "20", TextSpanUnit.Utf16CodeUnits),
            new OpaqueNativeLocator(IngestionIntent.EntryDocument, "synthetic", "opaque native location")
        ];
        var diagnostics = Evidence(Result(locators.Select(locator => Diagnostic() with { NativeLocator = locator }).ToArray()))
            .CopyDocument().GetProperty("diagnostics");

        Assert.Equal(new[] { "document", "jsonPointer", "graphNode", "textSpan", "opaque" }, diagnostics.EnumerateArray()
            .Select(diagnostic => diagnostic.GetProperty("nativeLocator").GetProperty("kind").GetString()));
        Assert.Equal("/a~1b/~0c", diagnostics[1].GetProperty("nativeLocator").GetProperty("pointer").GetString());
        Assert.Equal("9007199254740993", diagnostics[3].GetProperty("nativeLocator").GetProperty("startOffset").GetString());
        Assert.Equal("utf16CodeUnits", diagnostics[3].GetProperty("nativeLocator").GetProperty("unit").GetString());
        Assert.Equal("opaque native location", diagnostics[4].GetProperty("nativeLocator").GetProperty("value").GetString());
    }

    [Fact]
    public void SyntheticEvidenceOwnsItsSourceAndRetrievedBuffers()
    {
        SchemaOwnerStep[] path = [new ChildSchemaOwnerStep("1")];
        MappingDiagnostic[] diagnostics = [Diagnostic() with { Owner = new SchemaNodeDiagnosticOwner(new SourceMappingEndpoint(), path) }];
        var evidence = Evidence(Result(diagnostics));
        path[0] = new ChildSchemaOwnerStep("2");
        diagnostics[0] = Diagnostic() with { Code = "changed" };
        var copy = evidence.CopyUtf8();
        Array.Fill(copy, (byte)'x');

        var retained = evidence.CopyDocument().GetProperty("diagnostics")[0];
        Assert.Equal("synthetic.validation", retained.GetProperty("code").GetString());
        Assert.Equal("1", retained.GetProperty("owner").GetProperty("path")[0].GetProperty("index").GetString());
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("digest")]
    [InlineData("backend")]
    [InlineData("engine")]
    [InlineData("worker")]
    [InlineData("adapter")]
    public void SyntheticObservedIdentityMismatchesArePreservedRatherThanReplaced(string changed)
    {
        var provenance = Provenance;
        provenance = changed switch
        {
            "scope" => provenance with { Scope = new RevisionScope(Guid.NewGuid(), Scope.ProjectId, Scope.MappingId, Scope.RevisionId) },
            "digest" => provenance with { NativeDigest = Sha256Digest.FromBytes("different"u8) },
            "backend" => provenance with { BackendId = "different.backend" },
            "engine" => provenance with { EngineRevision = "different.engine" },
            "worker" => provenance with { BackendBuild = "different.worker" },
            "adapter" => provenance with { AdapterBuild = "different.adapter" },
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };
        var evidence = Evidence(Result([]) with { Provenance = provenance });

        Assert.Equal(provenance.Scope, evidence.Facts.Provenance.Scope);
        Assert.Equal(provenance.NativeDigest, evidence.Facts.Provenance.NativeDigest);
        Assert.Equal(provenance.BackendId, evidence.Facts.Provenance.BackendProfile.BackendId);
        Assert.Equal(provenance.EngineRevision, evidence.Facts.Provenance.BackendProfile.EngineRevision);
        Assert.Equal(provenance.BackendBuild, evidence.Facts.Provenance.BackendProfile.BackendBuild);
        Assert.Equal(provenance.AdapterBuild, evidence.Facts.Provenance.BackendProfile.AdapterBuild);
    }

    [Theory]
    [InlineData("nullResult")]
    [InlineData("nullProvenance")]
    [InlineData("nullScope")]
    [InlineData("nullDigest")]
    [InlineData("unknownStatus")]
    [InlineData("emptyIdentity")]
    [InlineData("identityLimit")]
    [InlineData("identityUnicode")]
    [InlineData("nullDiagnostics")]
    [InlineData("nullDiagnostic")]
    [InlineData("negativeTotal")]
    [InlineData("negativeEmitted")]
    [InlineData("tooManyTotal")]
    [InlineData("countMismatch")]
    [InlineData("emittedExceedsTotal")]
    [InlineData("nullCode")]
    [InlineData("emptyCode")]
    [InlineData("unknownSeverity")]
    [InlineData("nullMessage")]
    [InlineData("nulText")]
    [InlineData("invalidUnicode")]
    [InlineData("unknownOwner")]
    [InlineData("nullEndpoint")]
    [InlineData("unknownEndpoint")]
    [InlineData("nullSchemaPath")]
    [InlineData("unknownSchemaStep")]
    [InlineData("nullScopePath")]
    [InlineData("unknownScopeStep")]
    [InlineData("malformedDecimal")]
    [InlineData("unknownLocator")]
    [InlineData("nullDocument")]
    [InlineData("badPointer")]
    [InlineData("badPointerEscape")]
    [InlineData("unknownSpanUnit")]
    public void SyntheticMalformedModelsAreProtocolFailuresRatherThanEvidenceLimits(string corruption)
    {
        var result = Corrupt(corruption);

        var completion = IngestionValidationMapper.Map(result);

        Assert.Equal(IngestionAttemptCompletionKind.OperationalFailure, completion.Kind);
        Assert.Equal(IngestionFailureCode.ProtocolFailure, completion.Failure);
        Assert.Null(completion.Evidence);
        Assert.Null(completion.ObservedProvenance);
    }

    [Fact]
    public void SyntheticDiagnosticLimitIsAnHonestEvidenceLimitWithoutValidatorFacts()
    {
        var result = Result(Enumerable.Repeat(Diagnostic(), 129).ToArray());

        AssertEvidenceLimit(IngestionValidationMapper.Map(result));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("message")]
    [InlineData("location")]
    [InlineData("endpointName")]
    [InlineData("decimal")]
    [InlineData("opaqueValue")]
    public void SyntheticStrictUtf8TextBudgetRejectsWithoutSilentTruncation(string field)
    {
        var over = new string('é', 1025); // 2050 UTF8 bytes despite 1025 UTF16 characters.
        var diagnostic = Diagnostic();
        diagnostic = field switch
        {
            "code" => diagnostic with { Code = over },
            "message" => diagnostic with { Message = over },
            "location" => diagnostic with { Location = over },
            "endpointName" => diagnostic with { Owner = new EndpointDiagnosticOwner(new NamedSourceMappingEndpoint("0", over)) },
            "decimal" => diagnostic with { Owner = new FailureRuleDiagnosticOwner(new string('1', 2049)) },
            "opaqueValue" => diagnostic with { NativeLocator = new OpaqueNativeLocator(IngestionIntent.EntryDocument, "synthetic", over) },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        AssertEvidenceLimit(IngestionValidationMapper.Map(Result([diagnostic])));
    }

    [Fact]
    public void SyntheticTextAtExactUtf8BudgetIsRetained()
    {
        var message = new string('é', 1024);
        var evidence = Evidence(Result([Diagnostic() with { Message = message }]));

        Assert.Equal(message, evidence.CopyDocument().GetProperty("diagnostics")[0].GetProperty("message").GetString());
    }

    [Fact]
    public void SyntheticOverallNodeBudgetIncludesEnvelopeAndTypedPathNodes()
    {
        var diagnostic = Diagnostic() with
        {
            Owner = new SchemaNodeDiagnosticOwner(new SourceMappingEndpoint(),
                Enumerable.Repeat<SchemaOwnerStep>(new ChildSchemaOwnerStep("0"), 10930).ToArray())
        };
        AssertEvidenceLimit(IngestionValidationMapper.Map(Result([diagnostic])));
    }

    [Fact]
    public void SyntheticDocumentByteBudgetUsesCompleteSerializationSize()
    {
        var diagnostic = Diagnostic() with
        {
            // Each exact decimal string fits the text budget, but serialized path bytes exceed eight MiB.
            Owner = new SchemaNodeDiagnosticOwner(new SourceMappingEndpoint(),
                Enumerable.Repeat<SchemaOwnerStep>(new ChildSchemaOwnerStep(new string('1', 2048)), 4200).ToArray())
        };
        AssertEvidenceLimit(IngestionValidationMapper.Map(Result([diagnostic])));
    }

    [Fact]
    public async Task SyntheticRunnerRegistrationUsesActualConfiguredBuildAndValidatesExactlyOnce()
    {
        var runner = new SyntheticFailureRunner();
        var registration = FerruleIngestionAdmissionBackend.CreateRegistration(runner);
        var bytes = Encoding.UTF8.GetBytes(" \n{\"number\":9007199254740993,\"unknown\":true}\n");
        var artifact = NativeArtifact.Admit(Scope, bytes, bytes.Length);

        var completion = await registration.Backend.ValidateAsync(artifact);

        Assert.Equal(new IngestionBackendProfile("ferrule", FerruleInspectionBackend.EngineRevision,
            runner.BuildIdentity, FerruleInspectionBackend.AdapterBuild), registration.Profile);
        Assert.Equal(InspectionPolicy.MaximumNativeBytes, registration.NativeByteLimit);
        Assert.Equal(1, runner.Calls);
        var frame = Assert.IsType<byte[]>(runner.Request);
        var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(frame);
        using var header = JsonDocument.Parse(frame.AsMemory(4, headerLength));
        Assert.Equal("validate", header.RootElement.GetProperty("operation").GetString());
        Assert.Equal(bytes, frame[(8 + headerLength)..]);
        Assert.Equal(bytes, artifact.CopyBytes());
        Assert.Equal(IngestionBackendStatus.TimedOut, completion.Evidence!.Facts.Status);
        Assert.Null(completion.Evidence.Facts.IsValid);
        Assert.False(completion.Evidence.Facts.Provenance.Confirmed);
    }

    [Fact]
    public async Task SyntheticRunnerCallerCancellationBeforeAndDuringInvocationIsRethrown()
    {
        var runner = new SyntheticFailureRunner();
        var backend = new FerruleIngestionAdmissionBackend(runner);
        var artifact = NativeArtifact.Admit(Scope, "native"u8, 6);
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ValidateAsync(artifact, before.Token));
        Assert.Equal(0, runner.Calls);
        using var during = new CancellationTokenSource();
        runner.DuringInvocation = during.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ValidateAsync(artifact, during.Token));
        Assert.Equal(1, runner.Calls);
    }

    private static IngestionEvidenceDocument Evidence(MappingValidationResult result)
    {
        var completion = IngestionValidationMapper.Map(result);
        Assert.Equal(IngestionAttemptCompletionKind.Validation, completion.Kind);
        Assert.Null(completion.Failure);
        return Assert.IsType<IngestionEvidenceDocument>(completion.Evidence);
    }

    private static void AssertEvidenceLimit(IngestionAttemptCompletion completion)
    {
        Assert.Equal(IngestionAttemptCompletionKind.OperationalFailure, completion.Kind);
        Assert.Equal(IngestionFailureCode.EvidenceLimit, completion.Failure);
        Assert.Null(completion.Evidence);
        var observed = Assert.IsType<IngestionProvenance>(completion.ObservedProvenance);
        Assert.Equal(Scope, observed.Scope);
        Assert.Equal(Digest, observed.NativeDigest);
        Assert.True(observed.Confirmed);
    }

    private static MappingDiagnostic Diagnostic() => new("synthetic.validation", DiagnosticSeverity.Error,
        "Synthetic diagnostic", false, new DocumentNativeLocator(IngestionIntent.EntryDocument));
    private static MappingValidationResult Result(IReadOnlyList<MappingDiagnostic> diagnostics) =>
        new(MappingOperationStatus.InvalidMapping, Provenance, false, diagnostics.Count, diagnostics.Count, false, diagnostics);

    private static MappingValidationResult Corrupt(string corruption)
    {
        var result = Result([Diagnostic()]);
        var diagnostic = result.Diagnostics[0];
        switch (corruption)
        {
            case "nullResult": return null!;
            case "nullProvenance": return result with { Provenance = null! };
            case "nullScope": return result with { Provenance = Provenance with { Scope = null! } };
            case "nullDigest": return result with { Provenance = Provenance with { NativeDigest = null! } };
            case "unknownStatus": return result with { Status = (MappingOperationStatus)999 };
            case "emptyIdentity": return result with { Provenance = Provenance with { BackendId = " " } };
            case "identityLimit": return result with { Provenance = Provenance with { BackendId = new string('a', 129) } };
            case "identityUnicode": return result with { Provenance = Provenance with { AdapterBuild = "\ud800" } };
            case "nullDiagnostics": return result with { Diagnostics = null! };
            case "nullDiagnostic": return result with { Diagnostics = [null!] };
            case "negativeTotal": return result with { TotalIssueCount = -1 };
            case "negativeEmitted": return result with { EmittedIssueCount = -1 };
            case "tooManyTotal": return result with { TotalIssueCount = 32769 };
            case "countMismatch": return result with { EmittedIssueCount = 0 };
            case "emittedExceedsTotal": return result with { TotalIssueCount = 0 };
            case "nullCode": diagnostic = diagnostic with { Code = null! }; break;
            case "emptyCode": diagnostic = diagnostic with { Code = " " }; break;
            case "unknownSeverity": diagnostic = diagnostic with { Severity = (DiagnosticSeverity)999 }; break;
            case "nullMessage": diagnostic = diagnostic with { Message = null! }; break;
            case "nulText": diagnostic = diagnostic with { Message = "NUL\0" }; break;
            case "invalidUnicode": diagnostic = diagnostic with { Message = "\ud800" }; break;
            case "unknownOwner": diagnostic = diagnostic with { Owner = new UnknownOwner() }; break;
            case "nullEndpoint": diagnostic = diagnostic with { Owner = new EndpointDiagnosticOwner(null!) }; break;
            case "unknownEndpoint": diagnostic = diagnostic with { Owner = new EndpointDiagnosticOwner(new UnknownEndpoint()) }; break;
            case "nullSchemaPath": diagnostic = diagnostic with { Owner = new SchemaNodeDiagnosticOwner(new SourceMappingEndpoint(), null!) }; break;
            case "unknownSchemaStep": diagnostic = diagnostic with { Owner = new SchemaNodeDiagnosticOwner(new SourceMappingEndpoint(), [new UnknownSchemaStep()]) }; break;
            case "nullScopePath": diagnostic = diagnostic with { Owner = new ScopeDiagnosticOwner(new TargetMappingEndpoint(), null!) }; break;
            case "unknownScopeStep": diagnostic = diagnostic with { Owner = new ScopeDiagnosticOwner(new TargetMappingEndpoint(), [new UnknownScopeStep()]) }; break;
            case "malformedDecimal": diagnostic = diagnostic with { Owner = new FailureRuleDiagnosticOwner("01") }; break;
            case "unknownLocator": diagnostic = diagnostic with { NativeLocator = new UnknownLocator(IngestionIntent.EntryDocument) }; break;
            case "nullDocument": diagnostic = diagnostic with { NativeLocator = new DocumentNativeLocator(null!) }; break;
            case "badPointer": diagnostic = diagnostic with { NativeLocator = new JsonPointerNativeLocator(IngestionIntent.EntryDocument, "path") }; break;
            case "badPointerEscape": diagnostic = diagnostic with { NativeLocator = new JsonPointerNativeLocator(IngestionIntent.EntryDocument, "/~2") }; break;
            case "unknownSpanUnit": diagnostic = diagnostic with { NativeLocator = new TextSpanNativeLocator(IngestionIntent.EntryDocument, "0", "1", (TextSpanUnit)999) }; break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        return result with { Diagnostics = [diagnostic] };
    }

    private sealed record UnknownOwner : MappingDiagnosticOwner;
    private sealed record UnknownEndpoint : MappingEndpoint;
    private sealed record UnknownSchemaStep : SchemaOwnerStep;
    private sealed record UnknownScopeStep : ScopeOwnerStep;
    private sealed record UnknownLocator(string DocumentId) : NativeLocator(DocumentId);

    private sealed class SyntheticFailureRunner : IInspectionWorkerRunner
    {
        public string BuildIdentity => "synthetic.worker.transport";
        public int Calls { get; private set; }
        public byte[]? Request { get; private set; }
        public Action? DuringInvocation { get; set; }
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            Calls++;
            Request = requestFrame.ToArray();
            DuringInvocation?.Invoke();
            return Task.FromResult(new WorkerProcessResult([], WorkerProcessFailure.TimedOut));
        }
    }
}
