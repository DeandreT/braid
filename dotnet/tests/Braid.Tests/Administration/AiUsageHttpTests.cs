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

namespace Braid.Tests;

public sealed class AiUsageHttpTests
{
    [Fact]
    public async Task Read_keeps_explicit_filters_exact_counts_and_coverage_metadata()
    {
        await using var host = await Host.Start();
        using var response = await host.Client.GetAsync(host.Path + "?groupBy=modelProfile&profileId=bounded-model&profileVersion=2&since=2026-10-01T00:00:00Z&until=2026-11-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(AiUsageGrouping.ModelProfile, host.Reader.Last!.Grouping);
        Assert.Equal("bounded-model", host.Reader.Last.ProfileId); Assert.Equal(2, host.Reader.Last.ProfileVersion);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("9007199254740993", body.RootElement.GetProperty("groups")[0].GetProperty("usage").GetProperty("turns").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("groups")[0].GetProperty("usage").GetProperty("totalTokens").ValueKind);
        Assert.True(body.RootElement.GetProperty("includesPartialHistory").GetBoolean());
    }

    [Theory]
    [InlineData("?groupBy=other")]
    [InlineData("?groupBy=project&groupBy=principal")]
    [InlineData("?principalId=bad")]
    [InlineData("?profileVersion=2")]
    [InlineData("?profileId=model&profileVersion=01")]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?cursor=e30")]
    [InlineData("?extra=ignored")]
    public async Task Invalid_filters_do_not_reach_the_reader(string query)
    {
        await using var host = await Host.Start();
        using var response = await host.Client.GetAsync(host.Path + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Reader.Calls);
    }

    [Fact]
    public async Task Foreign_scope_and_request_bodies_are_refused()
    {
        await using var host = await Host.Start();
        using var foreign = await host.Client.GetAsync($"/api/v1/organizations/{Guid.NewGuid():D}/usage");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, host.Path) { Content = new StringContent("not a query") };
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Reader.Calls);
    }

    [Fact]
    public async Task A_refusal_or_storage_failure_never_returns_old_usage_or_exception_text()
    {
        await using var host = await Host.Start();
        host.Reader.Allowed = false;
        using var hidden = await host.Client.GetAsync(host.Path);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        host.Reader.Allowed = true; host.Reader.Failing = true;
        using var failed = await host.Client.GetAsync(host.Path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain("PRIVATE", await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(failed.Headers.CacheControl?.NoStore);
    }

    private sealed class Reader : IAiUsageReader
    {
        internal int Calls;
        internal bool Allowed = true, Failing;
        internal AiUsageQuery? Last;
        public Task<AiUsagePageV1?> ReadUsageAsync(ActorContext actor, AiUsageQuery query, CancellationToken token)
        {
            Calls++; Last = query;
            if (Failing) throw new InvalidOperationException("PRIVATE storage detail");
            var coverage = query.Since.AddDays(1);
            AiUsagePageV1? result = Allowed ? new(query.Since, query.Until,
                [new(query.OrganizationId, query.ProjectId, query.PrincipalId, query.ProfileId, query.ProfileVersion,
                    new("9007199254740993", "1", "0", "0", null, null, null, "0", "0", "0", "1", "1", "1", []))],
                null, true, query.Until, coverage, true) : null;
            return Task.FromResult(result);
        }
    }

    private sealed class Host(WebApplication app, Uri origin, Reader reader) : IAsyncDisposable
    {
        internal Reader Reader { get; } = reader;
        internal string Path => $"/api/v1/organizations/{WorkspaceToolFixture.Organization:D}/usage";
        internal HttpClient Client { get; } = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = origin };
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
            var reader = new Reader(); builder.Services.AddSingleton<IAiUsageReader>(reader);
            var app = builder.Build(); app.MapBraidCatalog(); port.Stop(); await app.StartAsync();
            return new(app, origin, reader);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); }
    }
}
