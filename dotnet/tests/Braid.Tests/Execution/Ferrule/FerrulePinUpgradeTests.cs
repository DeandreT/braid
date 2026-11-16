using Braid.Application;
using Braid.Application.Regression;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Deployment;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Ingestion;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

/// <summary>Authored identity/provenance cases; not worker, image, storage or migration qualification.</summary>
public sealed class FerrulePinUpgradeTests
{
    private const string CurrentRevision = "238369bf0071ee8ea097d8c54fa5061f433807fb";
    private const string CurrentAdapter = "braid.ferrule-adapter.v3";

    /// <summary>Historical engine/adapter pairs, with immutable image identities (including a synthetic prior-pin fixture).</summary>
    public static TheoryData<string, string, string> PreviousProfiles => new()
    {
        { "0495822e102bba3f97d9bffd3c665adfb6138bec", "sha256:247f7e82f965ced7f6dcde5970d3dc3c584c6f648c9d4f359a9bb5b6ed3985a3", "braid.ferrule-adapter.v2" },
        { "577359a991a7dd44172a3c4af91fdcab7592457c", "sha256:aecc4b4f444e9ec414fd879282a5881b59ab7297cf1bb500f06dc2563818d184", "braid.ferrule-adapter.v3" },
        { "0169809123b8caebd141b26a625be4c07e750536", "sha256:" + new string('e', 64), "braid.ferrule-adapter.v3" },
        { "576012aa17c403d9c3fcb8659ed4d3a47ba06864", "sha256:" + new string('f', 64), "braid.ferrule-adapter.v3" },
        { "dddc4dce6c93f4137fb2aa2a011360d089e0a51b", "sha256:" + new string('b', 64), "braid.ferrule-adapter.v3" },
        { "cbae074d1dda69ed00b4c7ee6fe24574edcb6d67", "sha256:" + new string('d', 64), "braid.ferrule-adapter.v3" },
        { "8f3292b516457ab01db41f34d7ff71de63fc2813", "sha256:" + new string('c', 64), "braid.ferrule-adapter.v3" },
    };

    private static RevisionReportBackendProfile Previous(string revision, string image, string adapter) => new("ferrule", revision, image, adapter);

    [Fact]
    public void EveryCurrentIdentityNamesTheUpgradedEngine()
    {
        Assert.Equal(CurrentRevision, FerruleInspectionBackend.EngineRevision);
        Assert.Equal(CurrentRevision, BuildWorkerProtocol.Engine);
        Assert.Equal(CurrentRevision, FerruleReportTestData.Profile.BackendRevision);
        Assert.Equal(CurrentAdapter, FerruleReportTestData.Profile.AdapterBuild);
        // The preview and build adapters did not change; their identities still bind the new engine.
        Assert.Equal("braid.ferrule-preview-adapter.v1", FerrulePreviewBackend.AdapterBuild);
        Assert.Equal(FerrulePreviewBackend.AdapterBuild, FerruleBuildCompiler.Adapter);
    }

    [Theory]
    [MemberData(nameof(PreviousProfiles))]
    public void CurrentKeysSeparateTheEngineEvenWhenAllOtherKeyInputsMatch(string revision, string image, string adapter)
    {
        var artifact = FerruleReportTestData.Artifact();
        var current = FerruleReportTestData.Profile;
        var others = new List<RevisionReportBackendProfile>
        {
            new(current.BackendId, revision, current.BackendBuild, current.AdapterBuild),
            Previous(revision, image, adapter),
        };
        if (adapter != current.AdapterBuild) others.Add(new(current.BackendId, current.BackendRevision, current.BackendBuild, adapter));
        foreach (var other in others)
        {
            Assert.NotEqual(FerruleReportTestData.ProjectionKey(artifact).Fingerprint,
                FerruleReportTestData.ProjectionKey(artifact, profile: other).Fingerprint);
            Assert.NotEqual(FerruleReportTestData.ValidationKey(artifact).Fingerprint,
                FerruleReportTestData.ValidationKey(artifact, profile: other).Fingerprint);
        }
    }

    [Theory]
    [MemberData(nameof(PreviousProfiles))]
    public async Task HistoricalLiveKeysRefuseBeforeResolvingTheCurrentRunner(string revision, string image, string adapter)
    {
        var previous = Previous(revision, image, adapter);
        var resolutions = 0;
        var registry = FerruleRevisionReportBackendFactory.CreateRegistry(FerruleReportTestData.Profile, new(),
            () => { resolutions++; throw new InvalidOperationException("Historical keys cannot dispatch a current worker."); });
        var artifact = FerruleReportTestData.Artifact();
        await Assert.ThrowsAsync<RevisionReportValidationException>(() => registry.FindCurrent("ferrule", RevisionReportOperation.Projection)!
            .GetBackend().DeriveAsync(FerruleReportTestData.ProjectionKey(artifact, profile: previous), artifact));
        await Assert.ThrowsAsync<RevisionReportValidationException>(() => registry.FindCurrent("ferrule", RevisionReportOperation.Validation)!
            .GetBackend().DeriveAsync(FerruleReportTestData.ValidationKey(artifact, profile: previous), artifact));
        Assert.Throws<ArgumentException>(() => FerruleRevisionReportBackendFactory.CreateRegistry(previous, new(),
            () => throw new InvalidOperationException("Old profiles cannot register live workers.")));
        Assert.Equal(0, resolutions);
    }

    [Theory]
    [MemberData(nameof(PreviousProfiles))]
    public void RetainedOldProjectionValidationAndIngestionFactsKeepTheirOwnExactProvenanceWithoutAWorker(string revision, string image, string adapter)
    {
        var resolutions = 0;
        var registry = FerruleRevisionReportBackendFactory.CreateRegistry(FerruleReportTestData.Profile, new(),
            () => { resolutions++; throw new InvalidOperationException("Retained reports require no worker."); });
        var bytes = "{\"kept\":9007199254740993,\"bits\":\"8000000000000000\"}\r\n"u8.ToArray();
        var artifact = NativeArtifact.Admit(FerruleReportTestData.Artifact().Scope, bytes, bytes.Length);
        var old = Previous(revision, image, adapter);
        var provenance = new MappingOperationProvenance(artifact.Scope, artifact.Digest, old.BackendId, old.BackendRevision,
            old.BackendBuild, old.AdapterBuild, true);
        var projectionKey = FerruleReportTestData.ProjectionKey(artifact, profile: old);
        var projection = new ProjectionReportV1(OperationStatusV1.Succeeded, FerruleReportNormalizer.Provenance(provenance, artifact),
            FerruleReportTestData.Document(projectionKey, "8000000000000000"), []);
        var validationKey = FerruleReportTestData.ValidationKey(artifact, profile: old);
        var observed = new MappingValidationResult(MappingOperationStatus.InvalidMapping, provenance, false, 1, 1, false,
            [new("ferrule.validation", DiagnosticSeverity.Error, "Retained authored issue", false,
                new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "4294967295", "9007199254740993"),
                new GraphNodeDiagnosticOwner("9007199254740993", "4294967295"), "retained location")]);
        var validation = FerruleReportNormalizer.Validation(observed, artifact, validationKey.Policy);
        var ingestion = IngestionValidationMapper.Map(observed).Evidence!;
        var retainedIngestionBytes = ingestion.CopyUtf8();

        registry.FindStored(RevisionReportFormat.FromKey(projectionKey))!.Validate(projectionKey, projection);
        registry.FindStored(RevisionReportFormat.FromKey(validationKey))!.Validate(validationKey, validation);
        Assert.Equal(0, resolutions);
        Assert.Equal(revision, projection.Document!.Provenance.Backend.Revision);
        Assert.Equal(image, projection.Document.Provenance.Backend.BuildId);
        Assert.Equal(adapter, projection.Document.Provenance.AdapterVersion);
        Assert.Equal("8000000000000000", Assert.IsType<ExactNumberLiteralValue>(projection.Document.Parameters[0].DefaultValue).Value);
        Assert.Equal(revision, validation.Provenance!.Backend.Revision);
        Assert.Equal(image, validation.Provenance.Backend.BuildId);
        Assert.Equal(adapter, validation.Provenance.AdapterVersion);
        Assert.Equal(new IngestionBackendProfile("ferrule", revision, image, adapter), ingestion.Facts.Provenance.BackendProfile);
        Assert.Equal(retainedIngestionBytes, ingestion.CopyUtf8());
        Assert.Equal(bytes, artifact.CopyBytes());
        Assert.Throws<RevisionReportValidationException>(() => registry.FindStored(RevisionReportFormat.FromKey(projectionKey))!
            .Validate(FerruleReportTestData.ProjectionKey(artifact), projection));
        Assert.Throws<RevisionReportValidationException>(() => registry.FindStored(RevisionReportFormat.FromKey(validationKey))!
            .Validate(FerruleReportTestData.ValidationKey(artifact), validation));
    }

    [Theory]
    [InlineData("0495822e102bba3f97d9bffd3c665adfb6138bec")]
    [InlineData("577359a991a7dd44172a3c4af91fdcab7592457c")]
    [InlineData("0169809123b8caebd141b26a625be4c07e750536")]
    [InlineData("576012aa17c403d9c3fcb8659ed4d3a47ba06864")]
    [InlineData("dddc4dce6c93f4137fb2aa2a011360d089e0a51b")]
    [InlineData("cbae074d1dda69ed00b4c7ee6fe24574edcb6d67")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffff")]
    public async Task OldAndForgedLiveRepliesFailDefaultProjectionReportsAndIngestion(string revision)
    {
        var artifact = FerruleReportTestData.Artifact();
        var runner = new FerruleReportTestData.TranscriptRunner { EngineRevision = revision };
        var projection = await new FerruleInspectionBackend(runner).ProjectAsync(artifact);
        Assert.Equal(MappingOperationStatus.ProtocolFailure, projection.Status);
        Assert.False(projection.Provenance.Confirmed);
        Assert.Null(projection.Projection);
        var reports = new FerruleRevisionReportBackend(runner, FerruleReportTestData.Profile);
        var projected = await reports.DeriveAsync(FerruleReportTestData.ProjectionKey(artifact), artifact);
        var validated = await reports.DeriveAsync(FerruleReportTestData.ValidationKey(artifact), artifact);
        foreach (var result in new[] { projected, validated })
        {
            Assert.Equal(RevisionReportFailureCategory.ProtocolFailure, result.Failure);
            Assert.False(result.IsUsable);
            Assert.False(result.Observation!.Provenance!.Confirmed);
        }
        var ingestion = await new FerruleIngestionAdmissionBackend(runner).ValidateAsync(artifact);
        Assert.Equal(IngestionAttemptCompletionKind.Validation, ingestion.Kind);
        Assert.Equal(IngestionBackendStatus.ProtocolFailure, ingestion.Evidence!.Facts.Status);
        Assert.Null(ingestion.Evidence.Facts.IsValid);
        Assert.False(ingestion.Evidence.Facts.Provenance.Confirmed);
        Assert.Equal(4, runner.Calls);
    }

    [Theory]
    [InlineData("0495822e102bba3f97d9bffd3c665adfb6138bec", "sha256:247f7e82f965ced7f6dcde5970d3dc3c584c6f648c9d4f359a9bb5b6ed3985a3")]
    [InlineData("577359a991a7dd44172a3c4af91fdcab7592457c", "sha256:aecc4b4f444e9ec414fd879282a5881b59ab7297cf1bb500f06dc2563818d184")]
    public async Task ApprovedBuildsOfAnEarlierEngineStayBoundToTheirOriginalImages(string revision, string image)
    {
        var runner = new CountingRunner();
        var original = new BuildBackendIdentity("ferrule", revision, image, FerruleBuildCompiler.Adapter);
        Assert.Throws<ArgumentException>(() => new FerruleCompiledBuildRuntime(runner, original));

        var current = new BuildBackendIdentity("ferrule", BuildWorkerProtocol.Engine, "sha256:" + new string('a', 64), FerruleBuildCompiler.Adapter);
        var runtime = new FerruleCompiledBuildRuntime(runner, current);
        var replay = new RegressionReplay(PreviewTargetSelection.All, "2026-10-21T00:00:00Z", PreviewLimits.Default, new(null, "{}"u8), [], []);
        // The original images, and the earlier engine alone under the current image, are both refused.
        foreach (var approved in new[] { original, new BuildBackendIdentity("ferrule", revision, current.EngineBuild, current.AdapterVersion) })
        {
            var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var intent = new BuildIntent(Guid.NewGuid(), scope, Sha256Digest.FromBytes([4]), approved, FerruleBuildCompiler.Profile,
                [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")],
                new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
            var artifact = new BuildBytes([1, 2, 3], intent.Profile.MaximumArtifactBytes);
            var reference = BuildArtifactReference.Restore(intent, artifact.Digest, artifact.Length);
            var result = await runtime.ExecuteAsync(reference, artifact, replay);
            Assert.Equal(CompiledBuildOutcome.Unsupported, result.Outcome);
            Assert.Null(result.Outputs);
            Assert.Equal(revision, reference.Intent.Backend.EngineRevision);
        }
        Assert.Equal(0, runner.Calls);
    }

    private sealed class CountingRunner : IInspectionWorkerRunner
    {
        public string BuildIdentity => "sha256:" + new string('b', 64);
        internal int Calls { get; private set; }
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WorkerProcessResult([], WorkerProcessFailure.None));
        }
    }
}
