using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Administration;
using Braid.Domain;
using Braid.Tests.AuditTestSupport;

namespace Braid.Tests;

public sealed class AuditLogHttpTests
{
    [Fact]
    public async Task Reads_and_CSV_preserve_the_explicit_window_without_mutations()
    {
        await using var host = await Host.Start();
        var project = WorkspaceToolFixture.Project.ProjectId;
        using var response = await host.Client.GetAsync(host.Path + $"?projectId={project:D}&kind=build&limit=20&since=2026-10-01T00:00:00Z&until=2026-11-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("10000000-0000-4000-8000-000000000001", body.RootElement.GetProperty("entries")[0].GetProperty("eventId").GetString());
        Assert.Equal(project, host.Reader.Last!.ProjectId);
        Assert.Equal("build", host.Reader.Last.Kind); Assert.Equal(20, host.Reader.Last.Limit);
        using var csv = await host.Client.GetAsync(host.Path + "/export?since=2026-10-01T00:00:00Z&until=2026-11-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Contains("braid-audit.csv", csv.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        Assert.StartsWith(AuditLogCsv.Header, await csv.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(csv.Headers.Contains("X-Braid-Audit-Since"));
        Assert.True(csv.Headers.Contains("X-Braid-Audit-Until"));
    }

    [Theory]
    [InlineData("?extra=1")]
    [InlineData("?kind=build&kind=approval")]
    [InlineData("?kind=unknown")]
    [InlineData("?projectId=not-a-guid")]
    [InlineData("?actorId=00000000-0000-0000-0000-000000000000")]
    [InlineData("?limit=01")]
    [InlineData("?limit=101")]
    [InlineData("?cursor=e30")]
    [InlineData("?since=2026-11-01T00:00:00Z&until=2026-10-01T00:00:00Z")]
    public async Task Invalid_query_never_reaches_the_reader(string query)
    {
        await using var host = await Host.Start();
        using var response = await host.Client.GetAsync(host.Path + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Reader.Calls);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Foreign_scope_and_body_are_refused_before_reads()
    {
        await using var host = await Host.Start();
        using var foreign = await host.Client.GetAsync($"/api/v1/organizations/{Guid.NewGuid():D}/audit");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, host.Path) { Content = new StringContent("not an audit query") };
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Reader.Calls);
    }

    [Fact]
    public async Task Hidden_and_unavailable_audits_never_return_cached_event_data()
    {
        await using var host = await Host.Start();
        host.Reader.Allowed = false;
        using var denied = await host.Client.GetAsync(host.Path);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        host.Reader.Allowed = true; host.Reader.Failing = true;
        using var unavailable = await host.Client.GetAsync(host.Path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.DoesNotContain("PRIVATE", await unavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(unavailable.Headers.CacheControl?.NoStore);
    }

    private sealed class Host(WebApplication app, HttpClient client, Reader reader) : IAsyncDisposable
    {
        internal Reader Reader { get; } = reader;
        internal string Path => $"/api/v1/organizations/{WorkspaceToolFixture.Organization:D}/audit";
        internal HttpClient Client { get; } = client;
        internal static async Task<Host> Start()
        {
            using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
            var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
            builder.Logging.ClearProviders(); builder.Configuration.Sources.Clear();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, origin.Port));
            builder.Services.AddBraidApi(); builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddBraidCatalog(new(true, true, WorkspaceToolFixture.Actor.PrincipalId.Value, WorkspaceToolFixture.Organization,
                origin.GetLeftPart(UriPartial.Authority)), builder.Environment);
            builder.Services.AddSingleton<ICatalogReader>(new WorkspaceToolFixture());
            var reader = new Reader(); builder.Services.AddSingleton<IAuditLogReader>(reader);
            var app = builder.Build();
            HttpClient? client = null;
            try
            {
                client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = origin };
                app.MapBraidCatalog(); port.Stop();
                await app.StartAsync(); return new(app, client, reader);
            }
            catch (Exception startup)
            {
                try { await CleanupAsync(app, client); }
                catch (Exception cleanup) { throw new AggregateException(startup, cleanup); }
                throw;
            }
        }
        public async ValueTask DisposeAsync() => await CleanupAsync(app, Client);
        private static Task CleanupAsync(WebApplication app, HttpClient? client) => AuditFixtureCleanup.RunAsync(
            () => { client?.Dispose(); return Task.CompletedTask; },
            async () => await app.DisposeAsync());
    }

    private sealed class Reader : IAuditLogReader
    {
        internal int Calls;
        internal bool Allowed = true, Failing;
        internal AuditLogQuery? Last;
        public Task<AuditLogPageV1?> ReadAuditAsync(ActorContext actor, Guid organizationId, AuditLogQuery query, CancellationToken token)
        {
            Calls++; Last = query;
            if (Failing) throw new InvalidOperationException("PRIVATE reader details");
            AuditLogPageV1? value = Allowed ? new([new(Guid.Parse("10000000-0000-4000-8000-000000000001"), query.Since.AddMinutes(1), "build", "created", "user",
                actor.PrincipalId.Value, query.ProjectId, null, Guid.NewGuid(), "queued", false)], null, query.Since, query.Until, true) : null;
            return Task.FromResult(value);
        }
    }
}
