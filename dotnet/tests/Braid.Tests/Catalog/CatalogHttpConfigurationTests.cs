using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;

namespace Braid.Tests;

/// <summary>No database fixture: disabled actual transport/OpenAPI and explicitly synthetic configuration/peer tests.</summary>
public sealed class CatalogHttpConfigurationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task DisabledCatalogRetainsTypedOpenApiButNeverResolvesAReaderOrCreatesDefaultIdentity(string environment)
    {
        await using var api = await CatalogHttpTestSupport.StartAsync(environment: environment);
        var project = "/api/v1/organizations/11111111-1111-1111-1111-111111111111/projects/00000001-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var mapping = project + "/mappings/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var receipt = project + "/revision-ingestions/cccccccc-cccc-cccc-cccc-cccccccccccc";
        foreach (var path in new[]
        {
            "/api/v1/catalog/context", project[..project.LastIndexOf('/')], project,
            project + "/mappings", mapping, mapping + "/metadata", mapping + "/revisions",
            mapping + "/revisions/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", receipt
        })
        {
            using var response = await api.Client.GetAsync(path);
            var error = await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.NotFound);
            Assert.Equal("unavailable", error.GetProperty("code").GetString());
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        foreach (var path in new[] { project + "/mappings", mapping + "/revisions", receipt + "/resume" })
        {
            using var body = new ByteArrayContent([1, 2, 3]);
            using var response = await api.Client.PostAsync(path, body);
            var error = await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.NotFound);
            Assert.Equal("unavailable", error.GetProperty("code").GetString());
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        using var mutation = await api.UnsafeAsync(null, null);
        Assert.Equal(HttpStatusCode.NotFound, mutation.StatusCode);
        Assert.Equal(0, api.UnsafeBodyReadCount);
        Assert.Equal(0, api.UnsafeMutationCount);
        using var openApiResponse = await api.Client.GetAsync("/openapi/v1.json");
        var document = await CatalogHttpTestSupport.JsonAsync(openApiResponse);
        var paths = document.GetProperty("paths");
        var organizationRoute = "/api/v1/organizations/{organizationId}";
        var projectRoute = organizationRoute + "/projects/{projectId}";
        var mappingRoute = projectRoute + "/mappings/{mappingId}";
        var receiptRoute = projectRoute + "/revision-ingestions/{operationId}";
        foreach (var (route, schemaName) in new[]
        {
            ("/api/v1/catalog/context", "CatalogContextV1"),
            (organizationRoute + "/projects", "CatalogProjectPageV1"),
            (projectRoute, "CatalogProjectV1"),
            (projectRoute + "/mappings", "CatalogMappingPageV1"),
            (mappingRoute, "CatalogMappingV1"),
            (mappingRoute + "/metadata", "CatalogMappingMetadataV1"),
            (mappingRoute + "/revisions", "CatalogRevisionPageV1"),
            (mappingRoute + "/revisions/{revisionId}", "CatalogRevisionV1"),
            (receiptRoute, "CatalogRevisionIngestionReceiptV1")
        })
        {
            var operation = paths.GetProperty(route).GetProperty("get");
            var responseSchema = operation.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema");
            Assert.Equal("#/components/schemas/" + schemaName, responseSchema.GetProperty("$ref").GetString());
            string[] expectedMethods = route == mappingRoute + "/metadata" ? ["get", "patch"] :
                route == organizationRoute + "/projects" || route == projectRoute + "/mappings" ||
                route == mappingRoute + "/revisions" ? ["get", "post"] : ["get"];
            Assert.Equal(expectedMethods, paths.GetProperty(route).EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal));
        }
        foreach (var route in new[] { projectRoute + "/mappings", mappingRoute + "/revisions", receiptRoute + "/resume" })
        {
            var operation = paths.GetProperty(route).GetProperty("post");
            var responseSchema = operation.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema");
            Assert.Equal("#/components/schemas/CatalogRevisionIngestionResultV1", responseSchema.GetProperty("$ref").GetString());
        }
        Assert.Equal(new[] { "post" }, paths.GetProperty(receiptRoute + "/resume").EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.False(paths.TryGetProperty(CatalogHttpTestSupport.UnsafePath, out _));
        var schemas = document.GetProperty("components").GetProperty("schemas");
        Assert.Equal("string", schemas.GetProperty("CatalogMappingV1").GetProperty("properties").GetProperty("version").GetProperty("type").GetString());
        Assert.Equal("string", schemas.GetProperty("CatalogRevisionV1").GetProperty("properties").GetProperty("sequence").GetProperty("type").GetString());
        Assert.Equal("string", schemas.GetProperty("CatalogHeadV1").GetProperty("properties").GetProperty("sequence").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void SyntheticHostedConfigurationRejectsDevelopmentIdentity(string environment)
    {
        var builder = Builder(environment);
        var options = new CatalogApiOptions(true, true, Guid.NewGuid(), Guid.NewGuid(), "http://127.0.0.1:4100");
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddBraidCatalog(options, builder.Environment));
    }

    [Fact]
    public void SyntheticEnabledConfigurationRequiresEveryExplicitIdentityOption()
    {
        var builder = Builder("Development");
        var principal = Guid.NewGuid();
        var organization = Guid.NewGuid();
        foreach (var options in new[]
        {
            new CatalogApiOptions(true),
            new CatalogApiOptions(true, false, principal, organization, "http://127.0.0.1:4100"),
            new CatalogApiOptions(true, true, null, organization, "http://127.0.0.1:4100"),
            new CatalogApiOptions(true, true, principal, null, "http://127.0.0.1:4100"),
            new CatalogApiOptions(true, true, principal, organization)
        })
            Assert.Throws<InvalidOperationException>(() => builder.Services.AddBraidCatalog(options, builder.Environment));
        Assert.Throws<ArgumentException>(() => new CatalogApiOptions(true, true, Guid.Empty, organization, "http://127.0.0.1:4100"));
        Assert.Throws<ArgumentException>(() => new CatalogApiOptions(true, true, principal, Guid.Empty, "http://127.0.0.1:4100"));
    }

    [Theory]
    [InlineData("http://localhost:4100")]
    [InlineData("http://0.0.0.0:4100")]
    [InlineData("http://192.0.2.1:4100")]
    [InlineData("https://127.0.0.1:4100")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("http://[::1]:80")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.1:4100/")]
    [InlineData("http://127.0.0.1:4100/path")]
    [InlineData("http://127.0.0.1:4100?query=true")]
    [InlineData("http://127.0.0.1:4100#fragment")]
    [InlineData("http://user@127.0.0.1:4100")]
    [InlineData("HTTP://127.0.0.1:4100")]
    [InlineData("http://127.1:4100")]
    [InlineData("http://[::]:4100")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:4100")]
    public void SyntheticOriginConfigurationRejectsNoncanonicalOrNonloopbackOrigins(string origin) =>
        Assert.Throws<ArgumentException>(() => new CatalogApiOptions(true, true, Guid.NewGuid(), Guid.NewGuid(), origin));

    [Theory]
    [InlineData("http://127.0.0.1:4100")]
    [InlineData("http://[::1]:4100")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://[::1]")]
    public void SyntheticOriginConfigurationAcceptsCanonicalConcreteIpLoopback(string origin)
    {
        var builder = Builder("Development");
        var options = new CatalogApiOptions(true, true, Guid.NewGuid(), Guid.NewGuid(), origin);
        Assert.Same(builder.Services, builder.Services.AddBraidCatalog(options, builder.Environment));
    }

    [Theory]
    [InlineData("http://127.0.0.1", "127.0.0.1", "127.0.0.1")]
    [InlineData("http://[::1]", "[::1]", "::1")]
    public async Task SyntheticDefaultPortCanonicalHostAndOriginAcceptTheSameFrameworkCookieTokenAndClaims(
        string origin, string host, string peer)
    {
        // DefaultHttpContext exercises transport admission and the actual framework token
        // primitive only. No listener is bound to port 80 and no database admission is claimed.
        var builder = Builder("Development");
        builder.Services.AddBraidApi();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        var principal = Guid.NewGuid();
        var organization = Guid.NewGuid();
        builder.Services.AddBraidCatalog(new(true, true, principal, organization, origin), builder.Environment);
        await using var app = builder.Build();
        var boundary = app.Services.GetRequiredService<CatalogRequestBoundary>();
        DefaultHttpContext Context(string requestHost)
        {
            var context = new DefaultHttpContext { RequestServices = app.Services };
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString(requestHost);
            return context;
        }
        var issue = Context(host);
        var trusted = await boundary.AuthenticateAsync(issue, unsafeRequest: false);
        Assert.True(trusted.IsAdmitted);
        Assert.Equal(principal, trusted.Actor!.PrincipalId.Value);
        Assert.Equal(organization, trusted.AllowedOrganizationId);
        var token = boundary.IssueRequestToken(issue, trusted.Actor!);
        var cookie = Assert.Single(issue.Response.Headers.SetCookie)!.Split(';')[0];
        var claim = organization.ToString("D") + ":" + principal.ToString("D");
        Assert.Equal(claim, issue.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        using var body = new MemoryStream([1, 2, 3]);
        var validate = Context(host);
        validate.Request.Method = HttpMethods.Post;
        validate.Request.Headers.Origin = origin;
        validate.Request.Headers.Cookie = cookie;
        validate.Request.Headers[CatalogRequestBoundary.RequestTokenHeader] = token;
        validate.Request.Body = body;
        var admitted = await boundary.AuthenticateAsync(validate, unsafeRequest: true);
        Assert.True(admitted.IsAdmitted);
        Assert.Equal(principal, admitted.Actor!.PrincipalId.Value);
        Assert.Equal(organization, admitted.AllowedOrganizationId);
        Assert.Equal(claim, validate.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal(0, body.Position);

        var redundantOrigin = Context(host);
        redundantOrigin.Request.Method = HttpMethods.Post;
        redundantOrigin.Request.Headers.Origin = origin + ":80";
        redundantOrigin.Request.Headers.Cookie = cookie;
        redundantOrigin.Request.Headers[CatalogRequestBoundary.RequestTokenHeader] = token;
        Assert.False((await boundary.AuthenticateAsync(redundantOrigin, unsafeRequest: true)).IsAdmitted);
        Assert.False((await boundary.AuthenticateAsync(Context(host + ":81"), unsafeRequest: false)).IsAdmitted);
    }

    [Theory]
    [InlineData(null, "http")]
    [InlineData("192.0.2.1", "http")]
    [InlineData("203.0.113.8", "http")]
    [InlineData("::", "http")]
    [InlineData("127.0.0.1", "https")]
    public async Task SyntheticNonloopbackPeerAndSchemeCannotBeReplacedByForwardedHeaders(string? address, string scheme)
    {
        var builder = Builder("Development");
        builder.Services.AddBraidApi();
        builder.Services.AddBraidCatalog(new(true, true, Guid.NewGuid(), Guid.NewGuid(), "http://127.0.0.1:4100"), builder.Environment);
        await using var app = builder.Build();
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString("127.0.0.1", 4100);
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        context.Request.Headers["X-Forwarded-Proto"] = "http";
        context.Request.Headers["Forwarded"] = "for=127.0.0.1;host=127.0.0.1:4100;proto=http";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D"))], "forged"));
        var result = await app.Services.GetRequiredService<CatalogRequestBoundary>().AuthenticateAsync(context, unsafeRequest: false);
        Assert.False(result.IsAdmitted);
        Assert.Null(result.Actor);
        Assert.Null(result.AllowedOrganizationId);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    private static WebApplicationBuilder Builder(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        return builder;
    }
}
