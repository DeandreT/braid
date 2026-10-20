using System.Net;
using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using H = Weft.Tests.CatalogRevisionReportHttpTestSupport;

namespace Weft.Tests;

/// <summary>Actual catalog, PostgreSQL, worker-derived retained reports and HTTP refusals.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CatalogRevisionComparisonTests(PostgresFixture fixture)
{
    private static string Path(RevisionScope scope, string query) =>
        $"/api/v1/organizations/{scope.OrganizationId:D}/projects/{scope.ProjectId:D}/mappings/{scope.MappingId:D}/comparisons" + query;

    private static string Query(RevisionScope left, Guid leftProjection, RevisionScope right, Guid rightProjection,
        Guid? leftValidation = null, Guid? rightValidation = null) =>
        $"?leftRevision={left.RevisionId:D}&leftProjection={leftProjection:D}" +
        (leftValidation is { } lv ? $"&leftValidation={lv:D}" : "") +
        $"&rightRevision={right.RevisionId:D}&rightProjection={rightProjection:D}" +
        (rightValidation is { } rv ? $"&rightValidation={rv:D}" : "");

    [RevisionIngestionFact]
    public async Task StrictQueriesMissingReportsForeignScopesAndDisabledReadsAreRefusedWithoutDerivation()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var projection = RevisionReportTestSupport.Available(
            await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)).Metadata.DerivationId;
        var calls = test.BackendPort.Calls;
        await using (var api = await H.StartAsync(test))
        {
            var valid = Query(test.Revision, projection, test.Revision, projection);
            var id = Guid.NewGuid().ToString("D");
            foreach (var query in new[]
            {
                "",
                $"?leftRevision={test.Revision.RevisionId:D}&leftProjection={projection:D}",
                valid + "&unknown=" + id,
                valid + "&leftProjection=" + id,
                valid + "&left%50rojection=" + id,
                valid.Replace(projection.ToString("D"), projection.ToString("D").ToUpperInvariant(), StringComparison.Ordinal),
                valid + "&&",
                valid.Replace("leftRevision=", "leftRevision=00000000-0000-0000-0000-000000000000&ignored=", StringComparison.Ordinal)
            })
            {
                using var response = await api.SendAsync(HttpMethod.Get, Path(test.Revision, query));
                await H.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
            }

            foreach (var query in new[]
            {
                Query(test.Revision, Guid.NewGuid(), test.Revision, projection),
                Query(test.Revision, projection, test.Revision, projection, rightValidation: Guid.NewGuid()),
                Query(new(test.Revision.OrganizationId, test.Revision.ProjectId, test.Revision.MappingId, Guid.NewGuid()), projection, test.Revision, projection)
            })
            {
                using var response = await api.SendAsync(HttpMethod.Get, Path(test.Revision, query));
                await H.ErrorAsync(response, HttpStatusCode.NotFound, "notFound");
            }

            var foreign = new RevisionScope(Guid.NewGuid(), test.Revision.ProjectId, test.Revision.MappingId, test.Revision.RevisionId);
            using (var response = await api.SendAsync(HttpMethod.Get, Path(foreign, Query(foreign, projection, foreign, projection))))
                await H.ErrorAsync(response, HttpStatusCode.NotFound, "notFound");

            using (var response = await api.SendAsync(HttpMethod.Get, Path(test.Revision, valid), body: [1]))
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await using (var disabled = await H.StartAsync(test, enabled: false, derivationEnabled: false))
        {
            using var response = await disabled.SendAsync(HttpMethod.Get,
                Path(test.Revision, Query(test.Revision, projection, test.Revision, projection)));
            await H.ErrorAsync(response, HttpStatusCode.NotFound, "unavailable");
            Assert.Equal(0, disabled.ReaderResolutions);
        }
        Assert.Equal(calls, test.BackendPort.Calls);
    }
}
