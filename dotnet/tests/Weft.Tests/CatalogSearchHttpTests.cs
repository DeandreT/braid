using System.Net;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogSearchHttpTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task SearchRouteBindsQueryCursorAndOrganizationAndReturnsOnlyPlainText()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        await test.CommitCreateAsync("<script>Orders</script>", "Orders metadata", filename: "orders.json");
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        var path = $"/api/v1/organizations/{test.Project.OrganizationId:D}/search";
        using var response = await api.Client.GetAsync(path + "?q=orders&limit=1");
        var page = await CatalogHttpTestSupport.JsonAsync(response);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var hit = Assert.Single(page.GetProperty("hits").EnumerateArray());
        Assert.Equal("mapping", hit.GetProperty("kind").GetString());
        Assert.Equal("<script>Orders</script>", hit.GetProperty("title").GetProperty("text").GetString());
        Assert.Equal(8, Assert.Single(hit.GetProperty("title").GetProperty("highlights").EnumerateArray()).GetProperty("start").GetInt32());
        var cursor = page.GetProperty("nextCursor").GetString();
        using var next = await api.Client.GetAsync(path + "?q=orders&cursor=" + cursor);
        Assert.Equal("revision", Assert.Single((await CatalogHttpTestSupport.JsonAsync(next)).GetProperty("hits").EnumerateArray()).GetProperty("kind").GetString());
        foreach (var query in new[] { "", "?q=", "?q=orders&limit=0", "?q=orders&limit=101", "?q=orders&q=other", "?q=orders&unexpected=1", "?q=changed&cursor=" + cursor })
        {
            using var invalid = await api.Client.GetAsync(path + query);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var crossOrganization = await api.Client.GetAsync($"/api/v1/organizations/{test.Seed.OrganizationB:D}/search?q=orders");
        Assert.Equal(HttpStatusCode.NotFound, crossOrganization.StatusCode);
    }

    [PostgresFact]
    public async Task MissingReportIndexesHaveAnExplicitRetryableResponseInsteadOfPartialHits()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case, search: new PendingSearch());
        using var response = await api.Client.GetAsync($"/api/v1/organizations/{test.Project.OrganizationId:D}/search?q=orders");
        var body = await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.ServiceUnavailable);
        Assert.Equal("unavailable", body.GetProperty("code").GetString());
        Assert.Equal(TimeSpan.FromSeconds(2), response.Headers.RetryAfter?.Delta);
    }

    private sealed class PendingSearch : ICatalogSearch
    {
        public Task<CatalogSearchPage?> SearchAsync(ActorContext actor, Guid organizationId, CatalogSearchQuery query, int limit,
            CatalogSearchCursor? cursor = null, CancellationToken cancellationToken = default) => throw new CatalogSearchIndexPendingException();
    }
}

public sealed class CatalogSearchCursorTests
{
    [Fact]
    public void CursorsAreCanonicalBoundedAndBindExactRetainedReportResource()
    {
        var query = new CatalogSearchQuery("Orders 🧵");
        var key = new CatalogSearchKey(CatalogSearchKind.Field, new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(),
            Guid.NewGuid(), "field/🧵", Guid.NewGuid());
        var cursor = new CatalogSearchCursor(query, key);
        var encoded = CatalogSearchCursorCodec.Encode(cursor);
        Assert.Equal(cursor, CatalogSearchCursorCodec.Decode(encoded, key.Project.OrganizationId, query));
        Assert.Throws<ArgumentException>(() => CatalogSearchCursorCodec.Decode(encoded, Guid.NewGuid(), query));
        Assert.Throws<ArgumentException>(() => CatalogSearchCursorCodec.Decode(encoded, key.Project.OrganizationId, new("other")));
        foreach (var invalid in new[] { "", encoded + "=", "!!!!", new string('a', 4097) })
            Assert.Throws<ArgumentException>(() => CatalogSearchCursorCodec.Decode(invalid, key.Project.OrganizationId, query));
        var longKey = new CatalogSearchKey(CatalogSearchKind.Rule, key.Project, key.MappingId, key.RevisionId,
            new string('é', 1024), key.ProjectionDerivationId);
        var longCursor = new CatalogSearchCursor(query, longKey);
        var longEncoded = CatalogSearchCursorCodec.Encode(longCursor);
        Assert.True(longEncoded.Length <= 4096);
        Assert.Equal(longCursor, CatalogSearchCursorCodec.Decode(longEncoded, key.Project.OrganizationId, query));
    }

    [Fact]
    public void SnippetsKeepMatchesNearTheEndAndNeverSplitSupplementaryUnicode()
    {
        var snippet = CatalogSearchSnippets.Create(new string('é', 3000) + "🧵Order ID", new("🧵order"));
        var span = Assert.Single(snippet.Highlights);
        Assert.Equal("🧵Order", snippet.Text.Substring(span.Start, span.Length));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(snippet.Text) <= 4096);
        Assert.StartsWith("…", snippet.Text, StringComparison.Ordinal);
    }
}
