using System.Net;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Actual Kestrel requests and PostgreSQL admission; unsafe endpoint is explicitly test-only.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogHttpSecurityTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task RealHostHeaderAndProxySpoofingCannotBypassTheConfiguredTransportBoundary()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        foreach (var host in new[] { "attacker.invalid", $"localhost:{api.Origin.Port}", $"127.0.0.1:{api.Origin.Port + (api.Origin.Port == 65535 ? -1 : 1)}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog/context");
            request.Headers.Host = host;
            request.Headers.TryAddWithoutValidation("Forwarded", $"for=127.0.0.1;host={api.Origin.Authority};proto=http");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", api.Origin.Authority);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
            using var response = await api.Client.SendAsync(request);
            var body = await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.Forbidden);
            Assert.Equal("identityRejected", body.GetProperty("code").GetString());
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        using var injected = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog/context");
        injected.Headers.TryAddWithoutValidation("X-Weft-Principal", test.Seed.OtherProjectAdmin.ToString("D"));
        injected.Headers.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL-ID", test.Seed.OtherProjectAdmin.ToString("D"));
        injected.Headers.TryAddWithoutValidation("X-Role", "administrator");
        injected.Headers.TryAddWithoutValidation("Authorization", "Bearer forged-development-identity");
        using var admitted = await api.Client.SendAsync(injected);
        Assert.Equal(test.Seed.Alice.ToString("D"),
            (await CatalogHttpTestSupport.JsonAsync(admitted)).GetProperty("actorPrincipalId").GetString());
    }

    [PostgresFact]
    public async Task IssuedFrameworkCookieAndRequestTokenPermitTheTestOnlyUnsafeRouteAfterFreshAdmission()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        using var contextResponse = await api.Client.GetAsync("/api/v1/catalog/context");
        var context = await CatalogHttpTestSupport.JsonAsync(contextResponse);
        var cookie = Assert.Single(contextResponse.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(".Weft.Catalog.Antiforgery=", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/api/v1", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; secure", cookie, StringComparison.OrdinalIgnoreCase); // Explicit HTTP-only Development profile.
        var token = context.GetProperty("requestToken").GetString()!;
        using var success = await api.UnsafeAsync(token, api.Origin.GetLeftPart(UriPartial.Authority));
        var accepted = await CatalogHttpTestSupport.JsonAsync(success);
        Assert.True(accepted.GetProperty("acceptedByteCount").GetInt64() > 0);
        Assert.Equal(1, api.UnsafeBodyReadCount);
        Assert.Equal(1, api.UnsafeMutationCount);
    }

    [PostgresFact]
    public async Task MissingNullForeignNoncanonicalOrMultipleOriginsAreRejectedBeforeUnsafeApplicationBodyReads()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        using var contextResponse = await api.Client.GetAsync("/api/v1/catalog/context");
        var token = (await CatalogHttpTestSupport.JsonAsync(contextResponse)).GetProperty("requestToken").GetString()!;
        var canonical = api.Origin.GetLeftPart(UriPartial.Authority);
        foreach (var origin in new string?[]
        {
            null, "null", "", "https://attacker.invalid", $"http://localhost:{api.Origin.Port}",
            canonical + "/", canonical.ToUpperInvariant(), canonical + "/path", canonical + "?query=true",
            canonical + ", " + canonical, $"http://127.0.0.1:{api.Origin.Port + (api.Origin.Port == 65535 ? -1 : 1)}"
        })
        {
            using var denied = await api.UnsafeAsync(token, origin);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(0, api.UnsafeBodyReadCount);
            Assert.Equal(0, api.UnsafeMutationCount);
        }
        using var duplicate = await api.UnsafeAsync(token, canonical, duplicateOrigin: true);
        Assert.Equal(HttpStatusCode.Forbidden, duplicate.StatusCode);
        Assert.Equal(0, api.UnsafeBodyReadCount);
        Assert.Equal(0, api.UnsafeMutationCount);
    }

    [PostgresFact]
    public async Task MissingInvalidDuplicateAndOtherSessionTokensAreRejectedBeforeUnsafeApplicationBodyReads()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        using var contextResponse = await api.Client.GetAsync("/api/v1/catalog/context");
        var token = (await CatalogHttpTestSupport.JsonAsync(contextResponse)).GetProperty("requestToken").GetString()!;
        var origin = api.Origin.GetLeftPart(UriPartial.Authority);
        foreach (var invalid in new string?[] { null, "", "invalid", new('a', 4097) })
        {
            using var denied = await api.UnsafeAsync(invalid, origin);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var noCookieClient = api.NewClient();
        using var noCookie = await api.UnsafeAsync(token, origin, noCookieClient);
        Assert.Equal(HttpStatusCode.Forbidden, noCookie.StatusCode);

        var otherCookies = new CookieContainer();
        using var otherSession = api.NewClient(otherCookies);
        using var otherContext = await otherSession.GetAsync("/api/v1/catalog/context");
        Assert.Equal(HttpStatusCode.OK, otherContext.StatusCode);
        using var wrongSession = await api.UnsafeAsync(token, origin, otherSession);
        Assert.Equal(HttpStatusCode.Forbidden, wrongSession.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, CatalogHttpTestSupport.UnsafePath)
        {
            Content = new StringContent("must not be read")
        };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", new[] { token, token });
        using var duplicate = await api.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, duplicate.StatusCode);
        Assert.Equal(0, api.UnsafeBodyReadCount);
        Assert.Equal(0, api.UnsafeMutationCount);
        using var correct = await api.UnsafeAsync(token, origin);
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
    }

    [PostgresFact]
    public async Task ValidTransportAndTokenStillRequireFreshActualDatabaseAdmissionBeforeUnsafeApplicationIo()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        using var contextResponse = await api.Client.GetAsync("/api/v1/catalog/context");
        var token = (await CatalogHttpTestSupport.JsonAsync(contextResponse)).GetProperty("requestToken").GetString()!;
        await test.Case.ExecuteAsync("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
            test.Project.OrganizationId, test.Seed.Alice);
        using var denied = await api.UnsafeAsync(token, api.Origin.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(0, api.UnsafeBodyReadCount);
        Assert.Equal(0, api.UnsafeMutationCount);
        await test.Case.ExecuteAsync("UPDATE weft.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2",
            test.Project.OrganizationId, test.Seed.Alice);
        using var restored = await api.UnsafeAsync(token, api.Origin.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(1, api.UnsafeMutationCount);
    }

    [PostgresFact]
    public async Task TokenIssuedForOneConfiguredActorCannotAuthorizeAnotherConfiguredActor()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var first = await CatalogHttpTestSupport.StartAsync(test.Case);
        using var contextResponse = await first.Client.GetAsync("/api/v1/catalog/context");
        var token = (await CatalogHttpTestSupport.JsonAsync(contextResponse)).GetProperty("requestToken").GetString()!;
        await using var second = await CatalogHttpTestSupport.StartAsync(test.Case, test.Seed.Admitted);
        using var copiedCookieClient = second.NewClient(first.Cookies);
        using var denied = await second.UnsafeAsync(token, second.Origin.GetLeftPart(UriPartial.Authority), copiedCookieClient);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, second.UnsafeBodyReadCount);
        Assert.Equal(0, second.UnsafeMutationCount);
    }

    [PostgresFact]
    public async Task UnadmittedConfiguredIdentityDoesNotIssueCookieOrFallBackToAnotherActor()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case, Guid.NewGuid());
        using var context = await api.Client.GetAsync("/api/v1/catalog/context");
        var error = await CatalogHttpTestSupport.JsonAsync(context, HttpStatusCode.NotFound);
        Assert.Equal("notFound", error.GetProperty("code").GetString());
        Assert.False(context.Headers.Contains("Set-Cookie"));
        using var project = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(new ProjectScope(test.Project.OrganizationId, test.Project.ProjectId)));
        Assert.Equal(HttpStatusCode.NotFound, project.StatusCode);
    }
}
