using Braid.Application;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.MappingBackends;

namespace Braid.Tests;

public sealed class FerruleRevisionReportFormatValidatorTests
{
    [Theory]
    [InlineData("7ff0000000000000")]
    [InlineData("fff0000000000000")]
    [InlineData("7ff8000000000001")]
    [InlineData("7ff0000000000001")]
    public void NeutralBinary64AcceptanceDoesNotWidenTheFerruleFiniteOnlyRule(string bits)
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        var report = new ProjectionReportV1(OperationStatusV1.Succeeded, FerruleReportNormalizer.Provenance(FerruleReportTestData.Provenance(artifact), artifact),
            FerruleReportTestData.Document(key, bits), []);
        Assert.Equal(RevisionReportCoverage.Complete, RevisionReportProjectionValidation.Validate(key, report, key.Policy).Projection!.Coverage);
        Assert.Equal(RevisionReportValidationFailure.Integrity,
            Assert.Throws<RevisionReportValidationException>(() => new FerruleRevisionReportFormatValidator().Validate(key, report)).Failure);
    }

    [Theory]
    [InlineData("8000000000000000")]
    [InlineData("4008000000000000")]
    public void FiniteAndNegativeZeroBitsAreRetainedExactly(string bits)
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        var report = new ProjectionReportV1(OperationStatusV1.Succeeded, FerruleReportNormalizer.Provenance(FerruleReportTestData.Provenance(artifact), artifact),
            FerruleReportTestData.Document(key, bits), []);
        new FerruleRevisionReportFormatValidator().Validate(key, report);
        Assert.Equal(bits, Assert.IsType<ExactNumberLiteralValue>(report.Document!.Parameters[0].DefaultValue).Value);
    }

    [Fact]
    public void SupportedHistoricalProvenanceIsComparedToItsOwnKeyWithoutCurrentRunnerResolution()
    {
        var resolutions = 0;
        var registry = FerruleRevisionReportBackendFactory.CreateRegistry(FerruleReportTestData.Profile, new(),
            () => { resolutions++; throw new InvalidOperationException("Stored reads resolve no current runner."); });
        var artifact = FerruleReportTestData.Artifact();
        var historical = new RevisionReportBackendProfile("ferrule", "historical.engine", "sha256:" + new string('b', 64), "historical.adapter");
        var key = FerruleReportTestData.ProjectionKey(artifact, profile: historical);
        var actual = new MappingOperationProvenance(artifact.Scope, artifact.Digest, historical.BackendId, historical.BackendRevision,
            historical.BackendBuild, historical.AdapterBuild, true);
        var report = new ProjectionReportV1(OperationStatusV1.Succeeded, FerruleReportNormalizer.Provenance(actual, artifact), FerruleReportTestData.Document(key), []);
        registry.FindStored(RevisionReportFormat.FromKey(key))!.Validate(key, report);
        Assert.Equal(0, resolutions);
        Assert.Equal(historical.BackendBuild, report.Document!.Provenance.Backend.BuildId);
        var wrong = report with { Provenance = report.Provenance! with { Backend = report.Provenance.Backend with { BuildId = FerruleReportTestData.Image } } };
        Assert.Throws<RevisionReportValidationException>(() => registry.FindStored(RevisionReportFormat.FromKey(key))!.Validate(key, wrong));
    }

    [Fact]
    public void StoredFormatRefusesUnsupportedIdentityAndCancellationBeforeProviderWork()
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        var wrong = new ProjectionCacheKey(key.Source, key.Profile, key.SchemaVersion, key.AlgorithmVersion, "other.codec", key.Policy);
        var report = new ProjectionReportV1(OperationStatusV1.Succeeded, FerruleReportNormalizer.Provenance(FerruleReportTestData.Provenance(artifact), artifact), FerruleReportTestData.Document(key), []);
        Assert.Throws<RevisionReportValidationException>(() => new FerruleRevisionReportFormatValidator().Validate(wrong, report));
        Assert.Throws<OperationCanceledException>(() => new FerruleRevisionReportFormatValidator().Validate(key, report, new(true)));
    }
}
