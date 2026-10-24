using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Api.Help;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>The help routes over actual loopback Kestrel with the Development catalog admission.</summary>
public sealed class HelpHttpTests
{
    private static readonly Guid Organization = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Principal = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly JsonSerializerOptions Json = Options();

    [Fact]
    public async Task SectionsListTheEmbeddedGuideInOrderWithoutCaching()
    {
        await using var host = await HelpHost.StartAsync();
        using var response = await host.Client.GetAsync("/api/v1/help/sections");
        var body = await BodyAsync(response, HttpStatusCode.OK);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(Canonical(JsonSerializer.SerializeToElement(UserGuide.Embedded.Contents, Json)), Canonical(body));
        var ids = body.GetProperty("sections").EnumerateArray().Select(section => section.GetProperty("id").GetString()).ToArray();
        Assert.Equal("overview", ids[0]);
        Assert.Equal(1, host.Reader.AdmissionReads);
    }

    [Fact]
    public async Task EverySectionIsServedAsStructuredText()
    {
        await using var host = await HelpHost.StartAsync();
        foreach (var summary in UserGuide.Embedded.Contents.Sections)
        {
            using var response = await host.Client.GetAsync($"/api/v1/help/sections/{summary.Id}");
            var body = await BodyAsync(response, HttpStatusCode.OK);
            Assert.True(UserGuide.Embedded.TryGetSection(summary.Id, out var section));
            Assert.Equal(Canonical(JsonSerializer.SerializeToElement(section, Json)), Canonical(body));
            Assert.Equal(summary.Title, body.GetProperty("title").GetString());
            Assert.Contains(body.GetProperty("blocks").EnumerateArray(), block => block.GetProperty("kind").GetString() == "paragraph");
        }
        using var reports = await host.Client.GetAsync("/api/v1/help/sections/reports");
        var blocks = (await BodyAsync(reports, HttpStatusCode.OK)).GetProperty("blocks");
        Assert.Contains(blocks.EnumerateArray(), block => block.GetProperty("kind").GetString() == "heading" &&
            block.GetProperty("anchor").GetString() == "coverage" && block.GetProperty("level").GetInt32() == 2);
    }

    [Theory]
    [InlineData("missing-section")]
    [InlineData("a")]
    [InlineData("overview-2")]
    public async Task UnknownSectionsAreAFixedNotFound(string sectionId)
    {
        await using var host = await HelpHost.StartAsync();
        using var response = await host.Client.GetAsync($"/api/v1/help/sections/{sectionId}");
        await ErrorAsync(response, HttpStatusCode.NotFound, "notFound");
    }

    [Theory]
    [InlineData("Overview")]
    [InlineData("sign_in")]
    [InlineData("sign--in")]
    [InlineData("-overview")]
    [InlineData("overview-")]
    [InlineData("1overview")]
    [InlineData("overview.md")]
    [InlineData("%20overview")]
    [InlineData("ov%C3%A9rview")]
    [InlineData("overview%0A")]
    public async Task MalformedSectionIdsAreRefusedBeforeAnyRead(string sectionId)
    {
        await using var host = await HelpHost.StartAsync();
        using var response = await host.Client.GetAsync($"/api/v1/help/sections/{sectionId}");
        await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
        Assert.Equal(0, host.Reader.AdmissionReads);
    }

    [Fact]
    public async Task OverlongSectionIdsAndQueriesAreRefused()
    {
        await using var host = await HelpHost.StartAsync();
        using var overlong = await host.Client.GetAsync($"/api/v1/help/sections/{new string('a', UserGuide.MaximumSectionIdCharacters + 1)}");
        await ErrorAsync(overlong, HttpStatusCode.BadRequest, "invalidRequest");
        using var longest = await host.Client.GetAsync($"/api/v1/help/sections/{new string('a', UserGuide.MaximumSectionIdCharacters)}");
        await ErrorAsync(longest, HttpStatusCode.NotFound, "notFound");
        using var listQuery = await host.Client.GetAsync("/api/v1/help/sections?limit=1");
        await ErrorAsync(listQuery, HttpStatusCode.BadRequest, "invalidRequest");
        using var sectionQuery = await host.Client.GetAsync("/api/v1/help/sections/overview?format=html");
        await ErrorAsync(sectionQuery, HttpStatusCode.BadRequest, "invalidRequest");
    }

    [Fact]
    public async Task OrganizationAuthorityIsReadFreshlyForEveryRequest()
    {
        await using var host = await HelpHost.StartAsync();
        host.Reader.Admitted = false;
        using var list = await host.Client.GetAsync("/api/v1/help/sections");
        await ErrorAsync(list, HttpStatusCode.NotFound, "notFound");
        using var section = await host.Client.GetAsync("/api/v1/help/sections/overview");
        await ErrorAsync(section, HttpStatusCode.NotFound, "notFound");
        host.Reader.Admitted = true;
        using var restored = await host.Client.GetAsync("/api/v1/help/sections/overview");
        await BodyAsync(restored, HttpStatusCode.OK);
        Assert.Equal(3, host.Reader.AdmissionReads);
        host.Reader.Failing = true;
        using var failing = await host.Client.GetAsync("/api/v1/help/sections");
        await ErrorAsync(failing, HttpStatusCode.InternalServerError, "persistenceFailure");
    }

    [Fact]
    public async Task DisabledCatalogAndForeignTransportAreClosedBeforeAnyRead()
    {
        await using (var disabled = await HelpHost.StartAsync(enabled: false))
        {
            using var response = await disabled.Client.GetAsync("/api/v1/help/sections");
            await ErrorAsync(response, HttpStatusCode.NotFound, "unavailable");
            using var section = await disabled.Client.GetAsync("/api/v1/help/sections/overview");
            await ErrorAsync(section, HttpStatusCode.NotFound, "unavailable");
            Assert.Equal(0, disabled.Reader.AdmissionReads);
        }
        await using var host = await HelpHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/help/sections");
        request.Headers.Host = "localhost:" + host.Origin.Port;
        using var foreign = await host.Client.SendAsync(request);
        await ErrorAsync(foreign, HttpStatusCode.Forbidden, "identityRejected");
        Assert.Equal(0, host.Reader.AdmissionReads);
    }

    [Fact]
    public async Task OnlyGetIsMapped()
    {
        await using var host = await HelpHost.StartAsync();
        using var post = await host.Client.PostAsync("/api/v1/help/sections/overview", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(0, host.Reader.AdmissionReads);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await BodyAsync(response, status);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Single(body.EnumerateObject());
    }

    private static string Canonical(JsonElement value) => JsonSerializer.Serialize(value);

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ProjectionJson.Configure(options);
        return options;
    }

    private sealed class HelpHost : IAsyncDisposable
    {
        private static readonly IDataProtectionProvider TestKeys = new EphemeralDataProtectionProvider();
        private readonly WebApplication _app;

        private HelpHost(WebApplication app, Uri origin, CountingReader reader)
        {
            _app = app;
            Origin = origin;
            Reader = reader;
            Client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(20) };
        }

        internal Uri Origin { get; }
        internal HttpClient Client { get; }
        internal CountingReader Reader { get; }

        internal static async Task<HelpHost> StartAsync(bool enabled = true)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            var origin = new Uri($"http://127.0.0.1:{port}");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
            builder.Services.AddWeftApi();
            builder.Services.AddSingleton(TestKeys);
            builder.Services.AddWeftCatalog(enabled
                ? new CatalogApiOptions(true, true, Principal, Organization, origin.GetLeftPart(UriPartial.Authority))
                : new CatalogApiOptions(), builder.Environment);
            var reader = new CountingReader();
            builder.Services.AddSingleton<ICatalogReader>(reader);
            var app = builder.Build();
            app.MapWeftHelp();
            reservation.Stop();
            var host = new HelpHost(app, origin, reader);
            try
            {
                await app.StartAsync();
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    /// <summary>Answers only organization admission; any other catalog read would fail the test.</summary>
    private sealed class CountingReader : ICatalogReader
    {
        private int _admissionReads;
        internal int AdmissionReads => Volatile.Read(ref _admissionReads);
        internal volatile bool Admitted = true;
        internal volatile bool Failing;

        public Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionAsync(ActorContext actor, Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _admissionReads);
            Assert.Equal(Principal, actor.PrincipalId.Value);
            Assert.Equal(Organization, organizationId);
            if (Failing)
                throw new InvalidOperationException("Synthetic catalog persistence failure.");
            return Task.FromResult(Admitted ? new CatalogOrganizationAdmission(Organization, actor.PrincipalId, "Help organization") : null);
        }

        public Task<CatalogProjectPage?> ListProjectsAsync(ActorContext actor, Guid organizationId, int limit,
            CatalogProjectCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogProject?> GetProjectAsync(ActorContext actor, ProjectScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogMappingPage?> ListMappingsAsync(ActorContext actor, ProjectScope scope, int limit,
            CatalogMappingCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogMapping?> GetMappingAsync(ActorContext actor, MappingScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogMappingMetadata?> GetMappingMetadataAsync(ActorContext actor, MappingScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogRevisionPage?> ListRevisionsAsync(ActorContext actor, MappingScope scope, int limit,
            CatalogRevisionCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogRevision?> GetRevisionAsync(ActorContext actor, RevisionScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
