using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Api.Creation;
using Weft.Application;
using Weft.Contracts.Creation;
using Weft.Domain;
using Weft.Identity;

namespace Weft.Tests;

public sealed class CreationCapabilitiesTests
{
    private const string Route = "/api/v1/organizations/{organizationId}/projects/{projectId}/creation-capabilities";

    [Fact]
    public async Task OpenApi_declares_authenticated_closed_metadata_capability_response()
    {
        var b = WebApplication.CreateBuilder(); b.WebHost.UseTestServer(); b.Logging.ClearProviders(); b.Services.AddWeftApi();
        await using var app = b.Build(); app.MapWeftCreation(); app.MapOpenApi(); await app.StartAsync(); using var client = app.GetTestClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var operation = json.RootElement.GetProperty("paths").GetProperty(Route).GetProperty("get");
        Assert.Equal("GetCreationCapabilities", operation.GetProperty("operationId").GetString());
        Assert.Equal(2, operation.GetProperty("security").GetArrayLength());
        var schema = json.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(nameof(CreationCapabilitiesV1));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("available", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal("boolean", schema.GetProperty("properties").GetProperty("available").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Development_identity_cannot_observe_capabilities()
    {
        var f = new CreationRuntimeFixture(); var b = WebApplication.CreateBuilder(); b.WebHost.UseTestServer(); b.Logging.ClearProviders();
        b.Services.AddWeftApi(); b.Services.AddAntiforgery(); f.Register(b.Services);
        b.Services.AddSingleton(new CatalogApiOptions(true, true, WorkspaceToolFixture.Actor.PrincipalId.Value,
            WorkspaceToolFixture.Organization, "http://127.0.0.1"));
        b.Services.AddSingleton<CatalogRequestBoundary>();
        await using var app = b.Build(); app.Use((c, next) => { c.Connection.RemoteIpAddress = IPAddress.Loopback; return next(c); });
        app.MapWeftCreation(); await app.StartAsync(); using var client = app.GetTestClient(); client.BaseAddress = new("http://127.0.0.1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path())).StatusCode);
        Assert.Empty(f.Catalog.CatalogReads);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Authenticated_metadata_capability_reflects_runtime_without_payload_or_source_reads()
    {
        await using var h = await Host.Start();
        h.Fixture.Catalog.Payloads!.Grants.Clear(); h.Fixture.Stale = true;
        var response = await h.Client.GetAsync(Path());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True((await response.Content.ReadFromJsonAsync<CreationCapabilitiesV1>())!.Available);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        h.Admission.Ready = false;
        Assert.False((await h.Client.GetFromJsonAsync<CreationCapabilitiesV1>(Path()))!.Available);
        Assert.Equal(0, h.Fixture.Reads); Assert.Equal(0, h.Fixture.Compiles); Assert.Equal(0, h.Fixture.Creates);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(Path(organization: Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(Path(project: WorkspaceToolFixture.Hidden.ProjectId))).StatusCode);
        h.Admission.Ready = true;
        h.Admission.AfterVerify = () => h.Fixture.Catalog.RevokedProjects.Add(WorkspaceToolFixture.Project);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(Path())).StatusCode);
        h.Admission.AfterVerify = null; h.Fixture.Catalog.RevokedProjects.Clear();
        h.Identity.Actor = new(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(Path())).StatusCode);
        h.Identity.Actor = WorkspaceToolFixture.Actor.PrincipalId;
        h.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.GetAsync(Path())).StatusCode);
        var issue = await h.Client.GetAsync("/__issue");
        h.Client.DefaultRequestHeaders.Add("Cookie", issue.Headers.GetValues("Set-Cookie").Single(s => s.StartsWith("__Host-Weft.Session=", StringComparison.Ordinal)).Split(';')[0]);
        Assert.True((await h.Client.GetFromJsonAsync<CreationCapabilitiesV1>(Path()))!.Available);
        h.Fixture.Catalog.Active = false;
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(Path())).StatusCode);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Missing_runtime_returns_false_to_current_metadata_reader()
    {
        await using var h = await Host.Start(missingRuntime: true);
        var response = await h.Client.GetAsync(Path());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<CreationCapabilitiesV1>())!.Available);
        Assert.Equal(0, h.Admission.Calls);
        h.Fixture.Catalog.RevokedProjects.Add(WorkspaceToolFixture.Project);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(Path())).StatusCode);
    }

    private static string Path(Guid? organization = null, Guid? project = null) =>
        $"/api/v1/organizations/{organization ?? WorkspaceToolFixture.Organization:D}/projects/{project ?? WorkspaceToolFixture.Project.ProjectId:D}/creation-capabilities";

    private sealed class Admission : ICreationRuntimeAdmission
    {
        internal bool Ready = true;
        internal int Calls;
        internal Action? AfterVerify;
        public Task<bool> VerifyAsync(CancellationToken ct) { Calls++; AfterVerify?.Invoke(); return Task.FromResult(Ready); }
    }

    [SupportedOSPlatform("linux")]
    private sealed class Host : IAsyncDisposable
    {
        private readonly SessionTicketQualificationRoot files = new();
        private DurableSessionKeyProfile profile = null!;
        private WebApplication app = null!;
        internal readonly CreationRuntimeFixture Fixture = new();
        internal readonly IdentityPorts Identity = new();
        internal readonly Admission Admission = new();
        internal HttpClient Client = null!;
        internal static async Task<Host> Start(bool missingRuntime = false)
        {
            var h = new Host(); var org = WorkspaceToolFixture.Organization;
            using (DurableSessionKeyProfile.BootstrapEmpty(h.files.Options(org), h.files.Certificate, [], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2))) { }
            h.profile = DurableSessionKeyProfile.Open(h.files.Options(org), h.files.Certificate, []);
            var b = WebApplication.CreateBuilder(); b.WebHost.UseTestServer(); b.Logging.ClearProviders(); b.Services.AddWeftApi(); h.Fixture.Register(b.Services);
            b.Services.AddSingleton<ICreationRuntimeAdmission>(h.Admission);
            if (missingRuntime) b.Services.RemoveAll<IMappingCreationCompiler>();
            // Capability must never query grants or open the original source store.
            b.Services.AddSingleton<IProjectPayloadPermissionReader>(_ => throw new InvalidOperationException("Unexpected payload query."));
            b.Services.AddSingleton<IProjectPayloadGrantPresence>(_ => throw new InvalidOperationException("Unexpected payload query."));
            b.Services.AddSingleton<ISpecBundleServiceFactory>(new UnavailableSources());
            var sessions = new SessionStoreTestHarness.CountingSessionPorts();
            sessions.Create = (intent, _) =>
            {
                sessions.Row = new(intent.Key, intent.BindingId, intent.BindingVersion, intent.LoginEpoch,
                    ServerSessionPolicy.InitialLifetime(DateTimeOffset.UtcNow, intent.VerifiedLoginDeadlineUtc)!, 1, intent.CopyProtectedTicket());
                return Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Created, sessions.Row));
            };
            sessions.Observe = (key, _) => Task.FromResult(ServerSessionPolicy.ObserveCurrent(key, DateTimeOffset.UtcNow, sessions.Row,
                sessions.Row is { } row ? new(row.BindingId, row.BindingVersion, h.Identity.Actor, true, true) : null));
            b.Services.AddWeftBrowserSessions(new("https://localhost"), h.profile, sessions, sessions, TimeProvider.System);
            b.Services.AddWeftSessionCatalog(new(org), new(), new(), new(), new());
            b.Services.AddWeftBearerAccess(new(org), h.Identity, h.Identity, h.Identity);
            h.app = b.Build(); h.app.MapWeftCreation();
            h.app.MapGet("/__issue", async (HttpContext c, ServerSessionBrowser browser) =>
            { await browser.IssueFreshAsync(c, Guid.NewGuid(), 1, h.Identity.Actor, DateTimeOffset.UtcNow.AddMinutes(30), c.RequestAborted); return Results.NoContent(); });
            await h.app.StartAsync(); h.Client = h.app.GetTestClient(); h.Client.BaseAddress = new("https://localhost");
            h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-token"); return h;
        }
        public async ValueTask DisposeAsync() { Client?.Dispose(); if (app is not null) await app.DisposeAsync(); profile?.Dispose(); files.Dispose(); }
    }
    private sealed class IdentityPorts : IUserTokenValidator, IExternalIdentityReader, IApiRequestAuditWriter
    {
        private readonly ExternalIdentityKey identity = new("fixture", "https://issuer.invalid", "subject");
        internal PrincipalId Actor = WorkspaceToolFixture.Actor.PrincipalId;
        public Task<UserCaller> ValidateAsync(string token, CancellationToken ct = default) => Task.FromResult(new UserCaller(identity, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));
        public Task<ExternalIdentityResolution> ResolveAsync(ExternalIdentityKey key, CancellationToken ct = default) =>
            Task.FromResult(ExternalIdentityResolution.FromCurrent(new(Guid.NewGuid(), key, Actor, 1, true, DateTimeOffset.UtcNow), true));
        public Task<ExternalIdentityResolution> ResolveBindingAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiRequestAuditOutcome> RecordAsync(ApiRequestAudit audit, CancellationToken ct) => Task.FromResult(ApiRequestAuditOutcome.Recorded);
    }
    private sealed class UnavailableSources : ISpecBundleServiceFactory
    {
        public Task<SpecBundleService> OpenAsync(ActorContext actor, bool write, CancellationToken ct) =>
            throw new InvalidOperationException("Capabilities must not open the source store.");
    }
}
