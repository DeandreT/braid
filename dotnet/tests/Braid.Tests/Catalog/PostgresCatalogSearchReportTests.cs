using Braid.Application;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual publication metadata and indexing with self-authored typed projections, no worker or payload store.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresCatalogSearchReportTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task OldAndNewRetainedPublicationsAreBackfilledAndOpenTheExactReportWithoutDerivation()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture, migrationCount: 28);
        var first = await PublishAsync(test, test.Key, "CustomerOrderId");
        var before = await test.ImmutableSourceSnapshotAsync();
        await test.Case.MigrateAsync();
        Assert.Equal(before, await test.ImmutableSourceSnapshotAsync());
        await using var database = new PostgresCatalogDatabase(test.Case.CatalogConnectionString);
        var catalog = new PostgresCatalogReader(database);
        var reader = new RetainedReader([first]);
        var search = new PostgresCatalogSearch(catalog, () => reader, () => test.DerivationDatabase);
        var noBackfill = new PostgresCatalogSearch(catalog);
        await Assert.ThrowsAsync<CatalogSearchIndexPendingException>(() => noBackfill.SearchAsync(test.Actor, test.Project.OrganizationId, new("CustomerOrderId"), 100));
        var result = await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("CustomerOrderId"), 100);
        Assert.Equal(2, result!.Hits.Count);
        Assert.All(result.Hits, hit =>
        {
            Assert.Equal(first.Metadata.DerivationId, hit.Key.ProjectionDerivationId);
            Assert.Equal(test.Revision.RevisionId, hit.Key.RevisionId);
        });
        Assert.Equal(1, reader.Reads);
        Assert.Equal(2, (await noBackfill.SearchAsync(test.Actor, test.Project.OrganizationId, new("CustomerOrderId"), 100))!.Hits.Count);
        Assert.Equal(2, (await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("#/output"), 100))!.Hits.Count);
        await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("CustomerOrderId"), 100);
        Assert.Equal(1, reader.Reads);
        var second = await PublishAsync(test, test.ProjectionKey(algorithm: "synthetic.report.projection.v2"), "LaterOrderId");
        reader.Reports.Add(second.Metadata.DerivationId, second);
        await Assert.ThrowsAsync<CatalogSearchIndexPendingException>(() => noBackfill.SearchAsync(test.Actor, test.Project.OrganizationId, new("CustomerOrderId"), 100));
        var later = await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("LaterOrderId"), 100);
        Assert.Equal(2, later!.Hits.Count);
        Assert.All(later.Hits, hit => Assert.Equal(second.Metadata.DerivationId, hit.Key.ProjectionDerivationId));
        Assert.Equal(2, reader.Reads);
        // A member without payload-read permission can search metadata, but losing project access hides it freshly.
        var admitted = await search.SearchAsync(test.AdmittedActor, test.Project.OrganizationId, new("CustomerOrderId"), 100);
        Assert.Equal(2, admitted!.Hits.Count);
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Admitted);
        Assert.Empty((await search.SearchAsync(test.AdmittedActor, test.Project.OrganizationId, new("CustomerOrderId"), 100))!.Hits);
    }

    [PostgresFact]
    public async Task ReadThroughBatchesNeverExposeAnIncompleteSuccessfulPage()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var reports = new List<VerifiedProjectionReport>();
        for (var index = 0; index < 6; index++)
            reports.Add(await PublishAsync(test, test.ProjectionKey(algorithm: "synthetic.report.projection.batch" + index), "BatchOrderId"));
        await using var database = new PostgresCatalogDatabase(test.Case.CatalogConnectionString);
        var reader = new RetainedReader(reports);
        var search = new PostgresCatalogSearch(new(database), () => reader, () => test.DerivationDatabase);
        await Assert.ThrowsAsync<CatalogSearchIndexPendingException>(() => search.SearchAsync(test.Actor, test.Project.OrganizationId, new("BatchOrderId"), 100));
        Assert.Equal(4, reader.Reads);
        var page = await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("BatchOrderId"), 100);
        Assert.Equal(12, page!.Hits.Count);
        Assert.Equal(6, reader.Reads);
        Assert.Equal(6, page.Hits.Select(hit => hit.Key.ProjectionDerivationId).Distinct().Count());
        var collected = new List<CatalogSearchKey>();
        CatalogSearchCursor? cursor = null;
        do
        {
            var part = await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("BatchOrderId"), 3, cursor);
            collected.AddRange(part!.Hits.Select(hit => hit.Key));
            cursor = part.NextCursor;
        } while (cursor is not null);
        Assert.Equal(page.Hits.Select(hit => hit.Key), collected);
        Assert.Equal(6, reader.Reads);
    }

    [PostgresFact]
    public async Task FailedReportReadDoesNotMarkAnOldReportIndexedOrReturnPartialMetadata()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        _ = await PublishAsync(test, test.Key, "UnavailableOrderId");
        await using var database = new PostgresCatalogDatabase(test.Case.CatalogConnectionString);
        var reader = new RetainedReader([]);
        var search = new PostgresCatalogSearch(new(database), () => reader, () => test.DerivationDatabase);
        await Assert.ThrowsAsync<CatalogSearchIndexPendingException>(() => search.SearchAsync(test.Actor,
            test.Project.OrganizationId, new("Synthetic report source"), 100));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.catalog_search_report_index"));
    }

    internal static async Task<VerifiedProjectionReport> PublishAsync(PostgresRevisionDerivationTestSupport test,
        ProjectionCacheKey key, string name)
    {
        var claim = await test.ClaimAsync(key);
        var completion = RevisionReportCompletion.ForUsable(key,
            new(key.Operation, RevisionReportBackendStatus.Succeeded, PostgresRevisionDerivationTestSupport.SyntheticProvenance(key),
                new(RevisionReportCoverage.Complete, 5, 0, 0, 0), null),
            PostgresRevisionDerivationTestSupport.SyntheticDerivedReference(claim));
        var finished = await test.Store.FinishAsync(test.Actor, claim, completion);
        var metadata = finished.Snapshot!.SelectedResult!;
        var scope = key.Scope;
        var wireScope = new ScopedRevisionId(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D"));
        var digest = new ArtifactDigest("sha256", key.NativeDigest.Value);
        var backend = new BackendIdentity(key.Profile.BackendId, key.Profile.BackendRevision, key.Profile.BackendBuild);
        var locator = new JsonPointerNativeLocator(key.Source.LogicalDocument, "/output");
        var report = new ProjectionReportV1(OperationStatusV1.Succeeded,
            new(wireScope, digest, backend, key.Profile.AdapterBuild, true),
            new(new(wireScope, digest, backend, key.Profile.AdapterBuild, key.AlgorithmVersion), new(CoverageStatus.Complete, []),
                [new("output", "Orders", BoundaryRole.PrimaryOutput, "json", BoundaryShape.Object, null, locator)],
                [new("field", "output", [new FieldPathSegment(name)], name, new(FieldDataTypeKind.Scalar, [ScalarDataType.String]),
                    new(CardinalityKind.ExactlyOne), FieldRequirement.Required, FieldNullability.NonNullable, locator)], [],
                [new("rule", "field", "Copy " + name, [], [], locator)], [],
                [new("member", RelationshipKind.BoundaryMember, new FieldNodeReference("field"), new BoundaryNodeReference("output")),
                 new("writes", RelationshipKind.WritesTo, new RuleNodeReference("rule"), new FieldNodeReference("field"))], [], []), []);
        return VerifiedProjectionReport.Create(metadata, report, new());
    }

    internal sealed class RetainedReader(IEnumerable<VerifiedProjectionReport> reports) : IRevisionReportReader
    {
        internal Dictionary<Guid, VerifiedProjectionReport> Reports { get; } = reports.ToDictionary(report => report.Metadata.DerivationId);
        internal int Reads { get; private set; }
        public Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
            Guid? projectionDerivationId = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Search must not select a current report.");
        public Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
            Guid derivationId, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Reports.TryGetValue(derivationId, out var report)
                ? RevisionReportReadResult.ForAvailable(new(report.Metadata.Key.Source, actor.PrincipalId, DateTimeOffset.UtcNow), report)
                : RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.AdmissionBusy));
        }
    }
}
