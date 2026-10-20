using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Authored protocol transcripts, not native engine or storage qualification.</summary>
public sealed class FerruleRevisionReportBackendTests
{
    [Fact]
    public void PureRegistrationsAndBackendRetrievalDoNotResolveTheRunner()
    {
        var resolutions = 0;
        var registry = FerruleRevisionReportBackendFactory.CreateRegistry(FerruleReportTestData.Profile, new(),
            () => { resolutions++; throw new InvalidOperationException("No derivation was admitted."); });
        foreach (var operation in Enum.GetValues<RevisionReportOperation>())
        {
            var registration = registry.FindCurrent("ferrule", operation)!;
            Assert.Same(registration.GetBackend(), registration.GetBackend());
            Assert.IsType<FerruleRevisionReportFormatValidator>(registry.FindStored(registration.Format));
        }
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public async Task FullContextMismatchAndNativeReductionRefuseBeforeRunnerResolution()
    {
        var artifact = FerruleReportTestData.Artifact();
        var resolutions = 0;
        var registry = FerruleRevisionReportBackendFactory.CreateRegistry(FerruleReportTestData.Profile, new(),
            () => { resolutions++; throw new InvalidOperationException("No worker should be resolved."); });
        var backend = registry.FindCurrent("ferrule", RevisionReportOperation.Validation)!.GetBackend();
        var foreign = NativeArtifact.Admit(new(artifact.Scope.OrganizationId, artifact.Scope.ProjectId, Guid.NewGuid(), artifact.Scope.RevisionId), "{}"u8, 2);
        await Assert.ThrowsAsync<RevisionReportValidationException>(() => backend.DeriveAsync(FerruleReportTestData.ValidationKey(artifact), foreign));
        var limited = await backend.DeriveAsync(FerruleReportTestData.ValidationKey(artifact, new(nativeByteLimit: 1)), artifact);
        Assert.Equal(RevisionReportFailureCategory.ResourceLimit, limited.Failure);
        Assert.Null(limited.Observation);
        await Assert.ThrowsAsync<OperationCanceledException>(() => backend.DeriveAsync(FerruleReportTestData.ValidationKey(artifact), artifact, new(true)));
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public async Task WrongActualRunnerIdentityNeverDispatchesAndCannotRelabelProfile()
    {
        var runner = new FerruleReportTestData.TranscriptRunner { BuildIdentity = "sha256:" + new string('b', 64) };
        var artifact = FerruleReportTestData.Artifact();
        var result = await new FerruleRevisionReportBackend(runner, FerruleReportTestData.Profile).DeriveAsync(FerruleReportTestData.ValidationKey(artifact), artifact);
        Assert.Equal(RevisionReportFailureCategory.ProfileUnavailable, result.Failure);
        Assert.Null(result.Observation); Assert.False(result.IsUsable); Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualTypedValidityAndCountsRemainDistinctInOwnedValidation(bool valid)
    {
        var runner = new FerruleReportTestData.TranscriptRunner { Valid = valid };
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ValidationKey(artifact, dependency: "server.dependency.identity.v2");
        var result = await new FerruleRevisionReportBackend(runner, FerruleReportTestData.Profile).DeriveAsync(key, artifact);
        Assert.True(result.IsUsable); Assert.Equal(valid, result.ValidationReport!.IsValid);
        Assert.Equal(valid ? 0 : 1, result.Observation!.Validation!.TotalIssueCount);
        Assert.Equal(valid ? RevisionReportBackendStatus.Succeeded : RevisionReportBackendStatus.InvalidMapping, result.Observation.Status);
        Assert.Equal("server.dependency.identity.v2", result.Key!.Source.DependencyIdentity);
        Assert.Equal(artifact.CopyBytes(), FerruleReportTestData.NativePart(runner.LastRequest!));
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData("nativeDecodeFailed", RevisionReportFailureCategory.UnsupportedArtifact)]
    [InlineData("digestMismatch", RevisionReportFailureCategory.ProtocolFailure)]
    [InlineData("resultLimitExceeded", RevisionReportFailureCategory.ResourceLimit)]
    public async Task HostFailureClassificationRetainsConfirmedActualNullValidity(string code, RevisionReportFailureCategory category)
    {
        var runner = new FerruleReportTestData.TranscriptRunner { FailureCode = code };
        var artifact = FerruleReportTestData.Artifact();
        var result = await new FerruleRevisionReportBackend(runner, FerruleReportTestData.Profile).DeriveAsync(FerruleReportTestData.ValidationKey(artifact), artifact);
        Assert.Equal(category, result.Failure); Assert.False(result.IsUsable);
        Assert.True(result.Observation!.Provenance!.Confirmed); Assert.Null(result.Observation.Validation!.IsValid);
        Assert.Equal(code == "resultLimitExceeded" ? RevisionReportBackendStatus.ResourceLimit : RevisionReportBackendStatus.InvalidMapping,
            result.Observation.Status);
        Assert.Null(result.ValidationReport); Assert.Null(result.ProjectionReport);
    }

    [Theory]
    [InlineData(WorkerProcessFailure.CleanupFailed, RevisionReportFailureCategory.IsolationFailure)]
    [InlineData(WorkerProcessFailure.Canceled, RevisionReportFailureCategory.Canceled)]
    public async Task ReturnedTransportFailureSurvivesTheAlreadyCanceledCallerToken(WorkerProcessFailure failure, RevisionReportFailureCategory category)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new FerruleReportTestData.TranscriptRunner { ProcessFailure = failure, CancelDuringRun = cancellation };
        var artifact = FerruleReportTestData.Artifact();
        var result = await new FerruleRevisionReportBackend(runner, FerruleReportTestData.Profile).DeriveAsync(FerruleReportTestData.ValidationKey(artifact), artifact, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested); Assert.Equal(category, result.Failure);
        Assert.False(result.Observation!.Provenance!.Confirmed); Assert.Null(result.Observation.Validation!.IsValid);
        Assert.Equal(category == RevisionReportFailureCategory.IsolationFailure ? RevisionReportBackendStatus.IsolationFailure : RevisionReportBackendStatus.Canceled,
            result.Observation.Status);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task RegistrySharesOneActualRunnerAcrossProjectionAndValidation()
    {
        var runner = new FerruleReportTestData.TranscriptRunner();
        var resolutions = 0;
        var registry = FerruleRevisionReportBackendFactory.CreateRegistry(FerruleReportTestData.Profile, new(), () => { resolutions++; return runner; });
        var artifact = FerruleReportTestData.Artifact();
        var projected = await registry.FindCurrent("ferrule", RevisionReportOperation.Projection)!.GetBackend().DeriveAsync(FerruleReportTestData.ProjectionKey(artifact), artifact);
        var validated = await registry.FindCurrent("ferrule", RevisionReportOperation.Validation)!.GetBackend().DeriveAsync(FerruleReportTestData.ValidationKey(artifact), artifact);
        Assert.True(projected.IsUsable); Assert.True(validated.IsUsable);
        Assert.Equal(1, resolutions); Assert.Equal(2, runner.Calls);
        Assert.Equal(FerruleReportTestData.Profile, projected.Observation!.Provenance!.Profile);
        Assert.Equal(FerruleReportTestData.Profile, validated.Observation!.Provenance!.Profile);
    }

    [Fact]
    public void TrustedProfileRequiresExactImmutableImageAndCurrentAdapterIdentity()
    {
        foreach (var image in new[] { "image:latest", "sha256:" + new string('A', 64), "sha256:" + new string('a', 63), " sha256:" + new string('a', 64) })
            Assert.Throws<ArgumentException>(() => FerruleRevisionReportBackendFactory.CreateProfile(image));
        var profile = FerruleRevisionReportBackendFactory.CreateProfile(FerruleReportTestData.Image);
        Assert.Equal(FerruleInspectionBackend.EngineRevision, profile.BackendRevision);
        Assert.Equal(FerruleInspectionBackend.AdapterBuild, profile.AdapterBuild);
        Assert.Equal(FerruleReportTestData.Image, profile.BackendBuild);
        Assert.Throws<ArgumentException>(() => FerruleRevisionReportBackendFactory.CreateRegistry(
            new("ferrule", "other.engine", profile.BackendBuild, profile.AdapterBuild), new(), () => new FerruleReportTestData.TranscriptRunner()));
    }
}

internal static class FerruleReportTestData
{
    internal const string Image = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal static RevisionReportBackendProfile Profile => FerruleRevisionReportBackendFactory.CreateProfile(Image);
    internal static NativeArtifact Artifact() => NativeArtifact.Admit(new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444")), "{}"u8, 2);
    internal static RevisionReportSource Source(NativeArtifact artifact, string dependency) => new(artifact.Scope,
        new(PrivateObjectId.Restore(artifact.Scope, Guid.Parse("55555555-5555-5555-5555-555555555555"), PrivateObjectClass.NativeArtifact), artifact.Digest, artifact.ByteCount),
        InspectionPolicy.DocumentId, dependency, "ferrule");
    internal static ProjectionCacheKey ProjectionKey(NativeArtifact artifact, RevisionReportPolicy? policy = null, RevisionReportBackendProfile? profile = null) =>
        new(Source(artifact, "weft.dependencies.empty.v1"), profile ?? Profile, "weft.mapping-projection.v1", FerruleInspectionBackend.ProjectionVersion,
            "weft.revision-report-codec.v1", policy ?? new());
    internal static ValidationCacheKey ValidationKey(NativeArtifact artifact, RevisionReportPolicy? policy = null, string dependency = "weft.dependencies.empty.v1",
        RevisionReportBackendProfile? profile = null) => new(Source(artifact, dependency), profile ?? Profile, "weft.mapping-validation.v1",
            "weft.ferrule-validation.v1", "weft.revision-report-codec.v1", policy ?? new());
    internal static MappingOperationProvenance Provenance(NativeArtifact artifact, bool confirmed = true) =>
        new(artifact.Scope, artifact.Digest, "ferrule", FerruleInspectionBackend.EngineRevision, Image, FerruleInspectionBackend.AdapterBuild, confirmed);
    internal static ProjectionDocumentV1 Document(RevisionReportKey key, string? floatBits = null)
    {
        var scope = key.Scope;
        var capabilities = new List<OperationCapability>();
        foreach (var operation in Enum.GetValues<MappingOperation>())
            capabilities.Add(new(operation, operation is MappingOperation.Preview or MappingOperation.Build ? CapabilityStatus.NotQualified : CapabilityStatus.Supported,
                operation == MappingOperation.Project ? "ferrule.projection.v1" : "ferrule.bounded.v1",
                operation is MappingOperation.Preview or MappingOperation.Build ? ["primary-output"] : [],
                [new(operation == MappingOperation.Project ? "boundedReadOnlyProjection" : "bounded", "Bounded read-only operation")]));
        return new(new(new(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D")),
                new("sha256", key.NativeDigest.Value), new("ferrule", key.BackendRevision, key.BackendBuild), key.AdapterBuild, key.AlgorithmVersion),
            new(CoverageStatus.Complete, []),
            [new("primary-input", "Input", BoundaryRole.PrimaryInput, "native-json", BoundaryShape.Object, null, new DocumentNativeLocator(InspectionPolicy.DocumentId)),
                new("primary-output", "Output", BoundaryRole.PrimaryOutput, "native-json", BoundaryShape.Object, null, new DocumentNativeLocator(InspectionPolicy.DocumentId))],
            [], floatBits is null ? [] : [new("parameter", "value", new(FieldDataTypeKind.Scalar, [ScalarDataType.BinaryFloat]),
                FieldRequirement.Optional, new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, floatBits), new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/parameters/0"))],
            [], [], [], [], capabilities);
    }
    internal static byte[] NativePart(byte[] frame)
    {
        var header = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame));
        return frame[(header + 8)..];
    }
    internal sealed class TranscriptRunner : IInspectionWorkerRunner
    {
        public string BuildIdentity { get; set; } = Image;
        internal int Calls { get; private set; }
        internal byte[]? LastRequest { get; private set; }
        internal bool Valid { get; set; } = true;
        internal string EngineRevision { get; set; } = FerruleInspectionBackend.EngineRevision;
        internal string? FailureCode { get; set; }
        internal WorkerProcessFailure ProcessFailure { get; set; }
        internal CancellationTokenSource? CancelDuringRun { get; set; }
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            Calls++; LastRequest = requestFrame.ToArray(); CancelDuringRun?.Cancel();
            if (ProcessFailure != WorkerProcessFailure.None) return Task.FromResult(new WorkerProcessResult([], ProcessFailure));
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(requestFrame.Span));
            var header = JsonNode.Parse(requestFrame.Span.Slice(4, length))!.AsObject();
            JsonObject? payload = null;
            if (FailureCode is null && header["operation"]!.GetValue<string>() == "project")
            {
                var doc = JsonSerializer.SerializeToNode(Document(ProjectionKey(Artifact())), ProjectionJson.CreateOptions())!.AsObject();
                payload = new() { ["kind"] = "project" };
                foreach (var property in doc)
                    if (property.Key is not ("schemaVersion" or "provenance")) payload[property.Key] = property.Value?.DeepClone();
            }
            else if (FailureCode is null)
                payload = new()
                {
                    ["kind"] = "validate",
                    ["isValid"] = Valid,
                    ["totalIssueCount"] = Valid ? 0 : 1,
                    ["emittedIssueCount"] = Valid ? 0 : 1,
                    ["truncated"] = false,
                    ["issues"] = Valid ? new JsonArray() :
                        new JsonArray(new JsonObject
                        {
                            ["code"] = "ferrule.validation",
                            ["location"] = "schema",
                            ["locationTruncated"] = false,
                            ["message"] = "Authored issue",
                            ["messageTruncated"] = false,
                            ["owner"] = null,
                            ["nativeLocator"] = new JsonObject { ["kind"] = "document", ["documentId"] = InspectionPolicy.DocumentId }
                        })
                };
            var response = new JsonObject
            {
                ["protocolVersion"] = "weft.ferrule-host.v1",
                ["requestId"] = header["requestId"]!.DeepClone(),
                ["operation"] = header["operation"]!.DeepClone(),
                ["revision"] = header["revision"]!.DeepClone(),
                ["nativeDigest"] = header["nativeDigest"]!.DeepClone(),
                ["documentId"] = header["documentId"]!.DeepClone(),
                ["engineRevision"] = EngineRevision,
                ["result"] = payload,
                ["failure"] = FailureCode is null ? null : new JsonObject
                {
                    ["code"] = FailureCode,
                    ["message"] = "Authored admission refusal",
                    ["messageTruncated"] = false,
                    ["nativeLocator"] = null
                }
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
            var frame = new byte[bytes.Length + 4]; BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)bytes.Length); bytes.CopyTo(frame.AsSpan(4));
            return Task.FromResult(new WorkerProcessResult(frame, WorkerProcessFailure.None));
        }
    }
}
