using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Braid.Api;
using Braid.Server;

namespace Braid.Tests;

public sealed class ShellTests
{
    [Fact]
    public async Task ConfigurationCannotAddPublicListenersToTheDevelopmentHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Unexpected:Url"] = "http://0.0.0.0:0",
            ["urls"] = "http://0.0.0.0:0"
        });
        builder.WebHost.ConfigureKestrel(server =>
            DevelopmentListenAddress.ConfigureKestrel(server, DevelopmentListenAddress.Parse("127.0.0.1:0")));
        await using var app = builder.Build();
        app.MapBraidHealth();
        await app.StartAsync();
        var bound = new Uri(Assert.Single(app.Urls));
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(bound.Host)));
    }

    [Fact]
    public async Task HealthIsVersionedJsonAndUnknownRoutesAreNotHealthy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapBraidHealth();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("braid-api", body.GetProperty("service").GetString());
        Assert.Equal("v1", body.GetProperty("apiVersion").GetString());
        using var missing = await client.GetAsync("/api/v1/missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("0.0.0.0:4100")]
    [InlineData("[::]:4100")]
    [InlineData("192.0.2.1:4100")]
    [InlineData("localhost:4100")]
    [InlineData("bad")]
    public void UnauthenticatedShellRejectsNonLoopbackListeners(string value) =>
        Assert.Throws<ArgumentException>(() => DevelopmentListenAddress.Parse(value));

    [Theory]
    [InlineData(null, "127.0.0.1:4100")]
    [InlineData("127.0.0.1:0", "127.0.0.1:0")]
    [InlineData("[::1]:4100", "[::1]:4100")]
    public void DevelopmentListenerAcceptsOnlyLocalAddresses(string? value, string expected) =>
        Assert.Equal(expected, DevelopmentListenAddress.Parse(value).ToString());
}
