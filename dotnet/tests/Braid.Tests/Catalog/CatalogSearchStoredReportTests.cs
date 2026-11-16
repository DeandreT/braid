using System.Runtime.Versioning;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CatalogSearchStoredReportTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task SearchIndexesVerifiedRetainedBytesWithoutReadingNativeOrInvokingTheBackend()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var report = Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(
            await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)));
        var field = report.Report.Document!.Fields.First(item => !string.IsNullOrEmpty(item.Name));
        var source = await test.SourceSnapshotAsync();
        var nativeReads = test.StoragePort.NativeReads;
        var backendCalls = test.BackendPort.Calls;
        var readOnly = test.NewService(registry: test.NewRegistry(includeCurrent: false,
            backendFactory: () => throw new InvalidOperationException("Search must not resolve a mapping backend.")));
        await using var database = new PostgresCatalogDatabase(test.Case.CatalogConnectionString);
        var search = new PostgresCatalogSearch(new(database), () => readOnly, () => test.DerivationDatabase);
        var page = await search.SearchAsync(test.Actor, test.Project.OrganizationId, new(field.Name!), 100);
        Assert.Contains(page!.Hits, hit => hit.Key.Kind == CatalogSearchKind.Field && hit.Key.ResourceId == field.Id &&
            hit.Key.RevisionId == test.Revision.RevisionId && hit.Key.ProjectionDerivationId == report.Metadata.DerivationId);
        Assert.Equal(nativeReads, test.StoragePort.NativeReads);
        Assert.Equal(backendCalls, test.BackendPort.Calls);
        Assert.Equal(source, await test.SourceSnapshotAsync());

        await test.Case.ExecuteAsync("DELETE FROM braid.catalog_search_report_index");
        test.StoragePort.ReadSubstitution = (_, bytes) => bytes[..^1];
        await Assert.ThrowsAsync<CatalogSearchIndexPendingException>(() => search.SearchAsync(test.Actor,
            test.Project.OrganizationId, new(field.Name!), 100));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.catalog_search_report_index"));
        Assert.Equal(nativeReads, test.StoragePort.NativeReads);
        Assert.Equal(backendCalls, test.BackendPort.Calls);
        Assert.Equal(source, await test.SourceSnapshotAsync());
    }
}
