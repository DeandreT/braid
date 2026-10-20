using Weft.Application;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Ingestion;

namespace Weft.Tests;

/// <summary>Authored identity/provenance cases; not worker, image, storage or migration qualification.</summary>
public sealed class FerrulePinUpgradeTests
{
    private const string PreviousRevision = "0495822e102bba3f97d9bffd3c665adfb6138bec";
    private const string PreviousAdapter = "weft.ferrule-adapter.v2";
    private const string PreviousImage = "sha256:247f7e82f965ced7f6dcde5970d3dc3c584c6f648c9d4f359a9bb5b6ed3985a3";
    private static RevisionReportBackendProfile PreviousProfile => new("ferrule", PreviousRevision, PreviousImage, PreviousAdapter);

    [Fact]
    public void CurrentKeysSeparateTheEngineEvenWhenAllOtherKeyInputsMatch()
    {
        var artifact = FerruleReportTestData.Artifact();
        var current = FerruleReportTestData.Profile;
        Assert.Equal("577359a991a7dd44172a3c4af91fdcab7592457c", current.BackendRevision);
        Assert.Equal("weft.ferrule-adapter.v3", current.AdapterBuild);
        var oldEngineOnly = new RevisionReportBackendProfile(current.BackendId, PreviousRevision, current.BackendBuild, current.AdapterBuild);
        var oldAdapterOnly = new RevisionReportBackendProfile(current.BackendId, current.BackendRevision, current.BackendBuild, PreviousAdapter);
        foreach (var other in new[] { oldEngineOnly, oldAdapterOnly, PreviousProfile })
        {
            Assert.NotEqual(FerruleReportTestData.ProjectionKey(artifact).Fingerprint,
                FerruleReportTestData.ProjectionKey(artifact, profile: other).Fingerprint);
            Assert.NotEqual(FerruleReportTestData.ValidationKey(artifact).Fingerprint,
                FerruleReportTestData.ValidationKey(artifact, profile: other).Fingerprint);
        }
    }

    [Fact]
    public async Task HistoricalLiveKeysRefuseBeforeResolvingTheCurrentRunner()
    {
        var resolutions = 0;
        var registry = FerruleRevisionReportBackendFactory.CreateRegistry(FerruleReportTestData.Profile, new(),
            () => { resolutions++; throw new InvalidOperationException("Historical keys cannot dispatch a current worker."); });
        var artifact = FerruleReportTestData.Artifact();
        await Assert.ThrowsAsync<RevisionReportValidationException>(() => registry.FindCurrent("ferrule", RevisionReportOperation.Projection)!
            .GetBackend().DeriveAsync(FerruleReportTestData.ProjectionKey(artifact, profile: PreviousProfile), artifact));
        await Assert.ThrowsAsync<RevisionReportValidationException>(() => registry.FindCurrent("ferrule", RevisionReportOperation.Validation)!
            .GetBackend().DeriveAsync(FerruleReportTestData.ValidationKey(artifact, profile: PreviousProfile), artifact));
        Assert.Throws<ArgumentException>(() => FerruleRevisionReportBackendFactory.CreateRegistry(PreviousProfile, new(),
            () => throw new InvalidOperationException("Old profiles cannot register live workers.")));
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public void RetainedOldProjectionValidationAndIngestionFactsKeepTheirOwnExactProvenanceWithoutAWorker()
    {
        var resolutions = 0;
        var registry = FerruleRevisionReportBackendFactory.CreateRegistry(FerruleReportTestData.Profile, new(),
            () => { resolutions++; throw new InvalidOperationException("Retained reports require no worker."); });
        var bytes = "{\"kept\":9007199254740993,\"bits\":\"8000000000000000\"}\r\n"u8.ToArray();
        var artifact = NativeArtifact.Admit(FerruleReportTestData.Artifact().Scope, bytes, bytes.Length);
        var old = PreviousProfile;
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
        Assert.Equal(PreviousRevision, projection.Document!.Provenance.Backend.Revision);
        Assert.Equal(PreviousImage, projection.Document.Provenance.Backend.BuildId);
        Assert.Equal(PreviousAdapter, projection.Document.Provenance.AdapterVersion);
        Assert.Equal("8000000000000000", Assert.IsType<ExactNumberLiteralValue>(projection.Document.Parameters[0].DefaultValue).Value);
        Assert.Equal(PreviousRevision, validation.Provenance!.Backend.Revision);
        Assert.Equal(PreviousImage, validation.Provenance.Backend.BuildId);
        Assert.Equal(PreviousAdapter, validation.Provenance.AdapterVersion);
        Assert.Equal(new IngestionBackendProfile("ferrule", PreviousRevision, PreviousImage, PreviousAdapter), ingestion.Facts.Provenance.BackendProfile);
        Assert.Equal(retainedIngestionBytes, ingestion.CopyUtf8());
        Assert.Equal(bytes, artifact.CopyBytes());
        Assert.Throws<RevisionReportValidationException>(() => registry.FindStored(RevisionReportFormat.FromKey(projectionKey))!
            .Validate(FerruleReportTestData.ProjectionKey(artifact), projection));
        Assert.Throws<RevisionReportValidationException>(() => registry.FindStored(RevisionReportFormat.FromKey(validationKey))!
            .Validate(FerruleReportTestData.ValidationKey(artifact), validation));
    }

    [Theory]
    [InlineData(PreviousRevision)]
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
}
