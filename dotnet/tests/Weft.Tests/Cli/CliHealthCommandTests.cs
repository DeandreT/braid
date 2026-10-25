using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using Weft.Cli;
using Weft.Cli.Http;
using Weft.Contracts;

namespace Weft.Tests.Cli;

public sealed class CliHealthCommandTests
{
    [Theory]
    [InlineData(false, 200, "ok", (int)CliExitCode.Success)]
    [InlineData(true, 200, "ok", (int)CliExitCode.Success)]
    [InlineData(true, 503, "unavailable", (int)CliExitCode.Unavailable)]
    public async Task PublicHealthDoesNotUseIdentityOrTheProtectedStore(bool ready, int status, string state, int expected)
    {
        var identity = new UnusedIdentity();
        using var handler = new HealthHandler(ready ? "/api/v1/health/ready" : "/api/v1/health", status, state);
        using var cli = new CliHarness(identity, handler, new Uri("http://127.0.0.1:4200"), () => [],
            Guid.NewGuid(), Guid.NewGuid(), "api://weft/weft.api");
        cli.Caches.Unavailable = true;
        await cli.ConfigureAsync();
        var result = await cli.RunAsync(ready ? ["health", "--ready", "--json"] : ["health", "--json"]);
        Assert.Equal((CliExitCode)expected, result.Code);
        Assert.Equal(state, JsonDocument.Parse(result.Out).RootElement.GetProperty("status").GetString());
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, cli.Caches.Attached);
        Assert.Equal(0, identity.Calls);
    }

    [Fact]
    public void OnlyFixedPublicProbesCanOmitBearerAuthentication() =>
        Assert.Equal(["GetHealth", "GetReadiness"], ApiOperations.All.Where(operation => !operation.RequiresAuthentication)
            .Select(operation => operation.OperationId).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(401, "unavailable", (int)CliExitCode.SignInFailed)]
    [InlineData(200, "unexpected", (int)CliExitCode.Transport)]
    public async Task RefusedOrMalformedHealthNeverTriggersSignIn(int status, string state, int expected)
    {
        var identity = new UnusedIdentity();
        using var handler = new HealthHandler("/api/v1/health", status, state);
        using var cli = new CliHarness(identity, handler, new Uri("http://127.0.0.1:4200"), () => [],
            Guid.NewGuid(), Guid.NewGuid(), "api://weft/weft.api");
        cli.Caches.Unavailable = true;
        await cli.ConfigureAsync();
        var result = await cli.RunAsync("health");
        Assert.Equal((CliExitCode)expected, result.Code);
        Assert.Empty(result.Out);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, identity.Calls);
        Assert.Equal(0, cli.Caches.Attached);
    }

    private sealed class UnusedIdentity : IMsalHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient GetHttpClient()
        {
            Calls++;
            throw new InvalidOperationException("Public health must not contact the identity provider.");
        }
    }

    private sealed class HealthHandler(string path, int status, string state) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("X-XSRF-TOKEN"));
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new HealthResponseV1(state, "weft-api", "0.1.0", "v1"),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
            });
        }
    }
}
