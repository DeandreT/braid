using System.Net;
using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using H = Weft.Tests.CatalogRevisionReportHttpTestSupport;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CatalogRevisionReportBoundaryTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task ReportsOffClosesAllSixRoutesBeforeResolutionAndLeavesOrdinaryMetadataAvailable()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        await using var api = await H.StartAsync(test, enabled: false, derivationEnabled: false);
        var token = await api.TokenAsync();
        foreach (var operation in new[] { RevisionReportOperation.Projection, RevisionReportOperation.Validation })
        {
            foreach (var (method, path) in new[]
            {
                (HttpMethod.Get, H.CurrentPath(test.Revision, operation)),
                (HttpMethod.Get, H.RetainedPath(test.Revision, operation, Guid.NewGuid())),
                (HttpMethod.Post, H.EnsurePath(test.Revision, operation))
            })
            {
                using var response = await api.SendAsync(method, path, token);
                await H.ErrorAsync(response, HttpStatusCode.NotFound, "unavailable");
            }
        }
        Assert.Equal(0, api.RevisionReads);
        Assert.Equal(0, api.ReaderResolutions);
        Assert.Equal(0, api.DeriverResolutions);
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
        using var metadata = await api.Client.GetAsync(H.RevisionPath(test.Revision));
        await CatalogHttpTestSupport.JsonAsync(metadata);
        using var context = await api.Client.GetAsync("/api/v1/catalog/context");
        var flags = (await CatalogHttpTestSupport.JsonAsync(context)).GetProperty("features");
        Assert.False(flags.GetProperty("readReports").GetBoolean());
        Assert.False(flags.GetProperty("deriveReports").GetBoolean());
    }

    [RevisionIngestionFact]
    public async Task MalformedSelectorsMediaConditionsAndNonemptyFramingNeverResolveReportPortsOrReadBodies()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        await using var api = await H.StartAsync(test);
        var token = await api.TokenAsync();
        var current = H.CurrentPath(test.Revision, RevisionReportOperation.Projection);
        var validation = H.CurrentPath(test.Revision, RevisionReportOperation.Validation);
        var ensure = H.EnsurePath(test.Revision, RevisionReportOperation.Projection);
        var id = Guid.NewGuid().ToString("D");
        (HttpMethod Method, string Path, string? Header, string? Value, byte[]? Body, HttpStatusCode Status)[] cases =
        [
            (HttpMethod.Get, current + "?unknown=1", null, null, null, HttpStatusCode.BadRequest),
            (HttpMethod.Get, current + "?projectionDerivationId=" + id, null, null, null, HttpStatusCode.BadRequest),
            (HttpMethod.Get, validation + "?projectionDerivationId=" + id + "&projection%44erivationId=" + id, null, null, null, HttpStatusCode.BadRequest),
            (HttpMethod.Get, validation + "?projectionDerivationId=" + Guid.Empty.ToString("D"), null, null, null, HttpStatusCode.BadRequest),
            (HttpMethod.Post, ensure + "?repairDerivationId=", null, null, null, HttpStatusCode.BadRequest),
            (HttpMethod.Post, ensure + "?projectionDerivationId=" + id, null, null, null, HttpStatusCode.BadRequest),
            (HttpMethod.Post, ensure + "?repairDerivationId=" + id + "&repairDerivationId=" + id, null, null, null, HttpStatusCode.BadRequest),
            (HttpMethod.Get, current + "?" + new string('x', 257), null, null, null, HttpStatusCode.BadRequest),
            (HttpMethod.Get, current, "If-None-Match", "*", null, HttpStatusCode.BadRequest),
            (HttpMethod.Post, ensure, "If-Match", "*", null, HttpStatusCode.BadRequest),
            (HttpMethod.Post, ensure, "Idempotency-Key", id, null, HttpStatusCode.BadRequest),
            (HttpMethod.Get, current, "Content-Type", "application/json", [], HttpStatusCode.UnsupportedMediaType),
            (HttpMethod.Post, ensure, "Content-Encoding", "gzip", null, HttpStatusCode.UnsupportedMediaType),
            (HttpMethod.Post, ensure, null, null, "x"u8.ToArray(), HttpStatusCode.BadRequest),
            (HttpMethod.Get, current.Replace(test.Revision.ProjectId.ToString("D"), test.Revision.ProjectId.ToString("D").ToUpperInvariant(), StringComparison.Ordinal), null, null, null, HttpStatusCode.BadRequest)
        ];
        foreach (var item in cases)
        {
            api.ResetObservations();
            using var response = await api.SendAsync(item.Method, item.Path, token,
                headers: item.Header is null ? null : [new(item.Header, item.Value!)], body: item.Body);
            await H.ErrorAsync(response, item.Status, item.Status == HttpStatusCode.UnsupportedMediaType ? "unsupportedMediaType" : "invalidRequest");
            Assert.Equal(0, api.RevisionReads);
            Assert.Equal(0, api.CurrentReads);
            Assert.Equal(0, api.Ensures);
            Assert.Equal(0, api.BodyReads);
        }
        // Real header-only requests leave their declared bodies unsent: refusal cannot depend on a body read.
        foreach (var framing in new[] { new KeyValuePair<string, string>("Content-Length", "128"), new("Transfer-Encoding", "chunked") })
        {
            api.ResetObservations();
            using var request = await api.StartRawAsync("POST", ensure, token, [framing]);
            Assert.Equal(400, await request.ReadStatusAsync());
            Assert.Equal(0, api.RevisionReads);
            Assert.Equal(0, api.Ensures);
            Assert.Equal(0, api.BodyReads);
        }
        Assert.Equal(0, api.ReaderResolutions);
        Assert.Equal(0, api.DeriverResolutions);
        Assert.Equal(0, test.StoragePort.NativeReads);
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
    }

    [RevisionIngestionFact]
    public async Task HostOriginTokenOrganizationAndProjectAuthorityDenyBeforeReportResolution()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        await using var api = await H.StartAsync(test);
        var token = await api.TokenAsync();
        var ensure = H.EnsurePath(test.Revision, RevisionReportOperation.Projection);
        foreach (var (badToken, badOrigin, headers) in new[]
        {
            ((string?)null, (string?)null, (KeyValuePair<string,string>[]?)null),
            ("not-a-framework-token", (string?)null, null),
            (token, "http://127.0.0.1:1", null),
            (token, (string?)null, new KeyValuePair<string,string>[] { new("Host", "127.0.0.1:1") })
        })
        {
            api.ResetObservations();
            using var response = await api.SendAsync(HttpMethod.Post, ensure, badToken, badOrigin, headers);
            await H.ErrorAsync(response, HttpStatusCode.Forbidden, "identityRejected");
            Assert.Equal(0, api.RevisionReads);
        }
        var foreign = new RevisionScope(test.Seed.OrganizationB, test.Revision.ProjectId, test.Revision.MappingId, test.Revision.RevisionId);
        using (var response = await api.SendAsync(HttpMethod.Get, H.CurrentPath(foreign, RevisionReportOperation.Projection)))
            await H.ErrorAsync(response, HttpStatusCode.NotFound, "notFound");
        await using (var unrelated = await H.StartAsync(test, principal: test.Seed.OtherProjectAdmin))
        {
            var unrelatedToken = await unrelated.TokenAsync();
            using var response = await unrelated.SendAsync(HttpMethod.Post, ensure, unrelatedToken);
            await H.ErrorAsync(response, HttpStatusCode.NotFound, "notFound");
            Assert.Equal(0, unrelated.DeriverResolutions);
        }
        foreach (var scope in new[]
        {
            new RevisionScope(test.Revision.OrganizationId, test.Seed.HiddenProjectA, test.Revision.MappingId, test.Revision.RevisionId),
            new RevisionScope(test.Revision.OrganizationId, test.Revision.ProjectId, Guid.NewGuid(), test.Revision.RevisionId),
            new RevisionScope(test.Revision.OrganizationId, test.Revision.ProjectId, test.Revision.MappingId, Guid.NewGuid())
        })
        {
            using var response = await api.SendAsync(HttpMethod.Get, H.CurrentPath(scope, RevisionReportOperation.Validation));
            await H.ErrorAsync(response, HttpStatusCode.NotFound, "notFound");
        }
        Assert.Equal(0, api.ReaderResolutions);
        Assert.Equal(0, api.DeriverResolutions);
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
    }

    [RevisionIngestionFact]
    public async Task FreshCatalogAuthorityAfterCompletedPreparationDiscardsTheRealReportBeforeDelivery()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var selected = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        var row = await test.AttemptSnapshotAsync(selected.Metadata.DerivationId);
        await using var api = await H.StartAsync(test);
        var gate = new RevisionReportBoundaryGate();
        api.BeforeRevisionAsync = (_, _, call, token) => call == 2 ? gate.HoldAsync(token) : Task.CompletedTask;
        var observation = api.ObserveNextRequest();
        var pending = api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection));
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, api.CompletedCalls);
            Assert.Equal(2, api.RevisionReads);
            await api.AssertCatalogPoolUnboundAsync();
            await test.AssertPoolsUnboundAsync();
            await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
        }
        finally { gate.Release(); }
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await H.ErrorAsync(response, HttpStatusCode.NotFound, "notFound");
        await observation.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(404, observation.StatusAtFinish);
        Assert.Equal(row, await test.AttemptSnapshotAsync(selected.Metadata.DerivationId));
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
    }
}
