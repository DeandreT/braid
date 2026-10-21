using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Deployment;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual scoped source/environment/evidence reads with explicitly synthetic verified projection facts.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PersistedBuildIntentFactoryTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData("complete", true)]
    [InlineData("missing", false)]
    [InlineData("unknown-format", false)]
    [InlineData("document-set", false)]
    [InlineData("wrong-profile", false)]
    public async Task New_intent_requires_stored_exact_verified_physical_boundary_facts(string condition, bool admitted)
    {
        await using var test = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using var metadata = new PostgresBuildMetadataReader(test.Runs.Cases.Case.RegressionConnectionString("weft_build_metadata_reader"));
        await using var database = new PostgresRevisionReportDatabase(test.Runs.Cases.Case.ReportConnectionString);
        var sources = new PostgresRevisionReportMetadataReader(database);
        var source = await sources.GetSourceAsync(test.Actor, test.Intent.Revision);
        Assert.NotNull(source);
        var profile = new RevisionReportBackendProfile(test.Intent.Backend.BackendId, test.Intent.Backend.EngineRevision,
            condition == "wrong-profile" ? "different-report-image" : test.Intent.Backend.EngineBuild, "synthetic-report-adapter");
        var key = new ProjectionCacheKey(source.Source, profile, "weft.mapping-projection.v1", "weft.test-projection.v1", "weft.revision-report-codec.v1", new());
        ProjectionBoundary[] boundaries =
        [
            new("source", "Source", BoundaryRole.PrimaryInput, condition == "unknown-format" ? "unknown" : "json",
                condition == "document-set" ? BoundaryShape.DocumentSet : BoundaryShape.Object, null, new JsonPointerNativeLocator(source.Source.LogicalDocument, "/source")),
            new("target", "Target", BoundaryRole.PrimaryOutput, "json", BoundaryShape.Object, null, new JsonPointerNativeLocator(source.Source.LogicalDocument, "/target"))
        ];
        var body = RevisionReportCodecTestSupport.ProjectionJson(key,
            boundaries: JsonSerializer.Serialize(boundaries, ProjectionJson.CreateOptions()));
        var report = JsonSerializer.Deserialize<ProjectionReportV1>(body, ProjectionJson.CreateOptions())!;
        var stored = RevisionReportCodecTestSupport.Metadata(RevisionReportCodecTestSupport.Claim(key), Encoding.UTF8.GetBytes(body),
            RevisionReportCodecTestSupport.ProjectionObservation(key, 2));
        var read = condition == "missing" ? RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.ProfileUnavailable) :
            RevisionReportReadResult.ForAvailable(source, VerifiedProjectionReport.Create(stored, report, new()));
        var reports = new StoredReportReader(read);
        var factory = new PersistedBuildIntentFactory(metadata, sources, reports, new RegressionRunReadService(test.Runs.Reader, test.Runs.Cases.Reader),
            test.Intent.Backend, test.Intent.Profile);
        var selection = new BuildApprovalSelection(test.Intent.BuildId, test.Intent.Revision.RevisionId,
            test.Intent.Environment.EnvironmentId, test.Intent.Environment.Version, test.Intent.RegressionEvidenceId);
        var result = await factory.CreateAsync(test.Actor, test.Runs.Cases.Mapping, test.Intent.ApprovalId, selection);
        Assert.Equal(admitted, result is not null);
        Assert.Equal(1, reports.Reads);
        if (result is not null)
        {
            Assert.Equal(test.Intent.NativeDigest, result.NativeDigest);
            Assert.Equal(test.Intent.Backend, result.Backend); Assert.Equal(test.Intent.Environment, result.Environment);
            Assert.True(selection.Matches(result));
            Assert.Equal(new[] { new BuildBoundary(BuildBoundaryKind.PrimaryInput, null, "json"), new BuildBoundary(BuildBoundaryKind.PrimaryOutput, null, "json") }, result.Boundaries);
        }
        Assert.Equal(0L, await test.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_approvals"));
        Assert.Equal(0L, await test.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_derivations"));
    }

    private sealed class StoredReportReader(RevisionReportReadResult result) : IRevisionReportReader
    {
        internal int Reads;
        public Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
            Guid? projectionDerivationId = null, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(result); }
        public Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
            Guid derivationId, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
