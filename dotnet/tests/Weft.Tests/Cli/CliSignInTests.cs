using System.Net.Http;
using System.Text.Json.Nodes;
using Weft.Cli;
using Weft.Contracts.Catalog;

namespace Weft.Tests.Cli;

public sealed class CliSignInTests
{
    [Fact]
    public async Task DeviceCodeSignInCachesTokensAndRequestsCarryOnlyTheBearerToken()
    {
        await using var issuer = await CliTestIssuer.StartAsync();
        await using var stub = await CliApiStub.StartAsync(issuer);
        using var cli = new CliHarness(issuer, stub.Handler);
        await cli.ConfigureAsync();

        var login = await cli.LoginAsync();
        Assert.Contains("enter the code " + issuer.LastUserCode, login.Error, StringComparison.Ordinal);
        Assert.Contains("Signed in to test as cli.user@example.test.", login.Out, StringComparison.Ordinal);
        Assert.Equal(1, issuer.DeviceGrants);
        Assert.True(cli.Caches.Holds("test"));

        var whoami = await cli.RunAsync("whoami");
        Assert.True(whoami.Exit == 0, whoami.ToString());
        Assert.Contains("cli.user@example.test", whoami.Out, StringComparison.Ordinal);
        Assert.Contains(stub.OrganizationId, whoami.Out, StringComparison.Ordinal);
        var json = await cli.RunAsync("whoami", "--json");
        Assert.True(json.Exit == 0, json.ToString());
        Assert.DoesNotContain("requestToken", json.Out, StringComparison.Ordinal);
        Assert.Equal("bearer", JsonNode.Parse(json.Out)!["context"]!["mode"]!.GetValue<string>());
        Assert.Equal(stub.OrganizationId, JsonNode.Parse(json.Out)!["context"]!["allowedOrganizationId"]!.GetValue<string>());
        // The cached token served both commands without another sign-in.
        Assert.Equal(1, issuer.DeviceGrants);
        Assert.Equal(0, issuer.RefreshGrants);
        stub.AssertClean();
    }

    [Fact]
    public async Task BrowserSignInUsesAuthorizationCodeWithPkceOnALoopbackRedirect()
    {
        await using var issuer = await CliTestIssuer.StartAsync();
        using var cli = new CliHarness(issuer, null);
        var redirects = new List<Uri>();
        Task? browsing = null;
        cli.OpenBrowser = address =>
        {
            // The "browser" runs on its own, as a real one would: MSAL starts its loopback listener after opening it.
            Assert.Equal(CliTestIssuer.Host, address.Host);
            browsing = Task.Run(async () => redirects.Add(await issuer.BrowseAsync(address)));
            return Task.CompletedTask;
        };
        await cli.ConfigureAsync();

        var login = await cli.RunAsync("login", "--browser");
        Assert.True(login.Exit == 0, login.ToString());
        await browsing!;
        Assert.Equal(1, issuer.CodeGrants);
        Assert.Equal(0, issuer.DeviceGrants);
        var redirect = Assert.Single(redirects);
        Assert.Equal("http", redirect.Scheme);
        Assert.Equal("localhost", redirect.Host);
        Assert.True(cli.Caches.Holds("test"));
    }

    [Fact]
    public async Task AnExpiredAccessTokenIsRefreshedSilentlyAndARefusedRefreshRequiresSignIn()
    {
        await using var issuer = await CliTestIssuer.StartAsync();
        await using var stub = await CliApiStub.StartAsync(issuer);
        using var cli = new CliHarness(issuer, stub.Handler);
        await cli.ConfigureAsync();
        issuer.AccessTokenLifetime = TimeSpan.FromSeconds(1);
        // MSAL refreshes a token that expires within its five-minute margin.
        await cli.LoginAsync();
        issuer.AccessTokenLifetime = TimeSpan.FromHours(1);

        var context = await cli.RunAsync("context");
        Assert.True(context.Exit == 0, context.ToString());
        Assert.Equal(1, issuer.RefreshGrants);

        issuer.AccessTokenLifetime = TimeSpan.FromSeconds(1);
        var again = await cli.RunAsync("logout");
        Assert.True(again.Exit == 0, again.ToString());
        await cli.LoginAsync();
        issuer.RefuseRefresh = true;
        var refused = await cli.RunAsync("context");
        Assert.Equal(CliExitCode.SignInRequired, refused.Code);
        Assert.Contains("weft login", refused.Error, StringComparison.Ordinal);
        stub.AssertClean();
    }

    [Fact]
    public async Task LogoutRemovesTheAccountAndLaterCommandsRequireSignIn()
    {
        await using var issuer = await CliTestIssuer.StartAsync();
        await using var stub = await CliApiStub.StartAsync(issuer);
        using var cli = new CliHarness(issuer, stub.Handler);
        await cli.ConfigureAsync();
        await cli.LoginAsync();

        var logout = await cli.RunAsync("logout");
        Assert.True(logout.Exit == 0, logout.ToString());
        Assert.Contains("Signed out of test.", logout.Error, StringComparison.Ordinal);
        Assert.False(cli.Caches.Holds("test"));
        var projects = await cli.RunAsync("projects", "list");
        Assert.Equal(CliExitCode.SignInRequired, projects.Code);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task WithoutAProtectedStoreSignInIsRefusedAndNothingIsRequested()
    {
        await using var issuer = await CliTestIssuer.StartAsync();
        using var cli = new CliHarness(issuer, null);
        await cli.ConfigureAsync();
        cli.Caches.Unavailable = true;

        var login = await cli.RunAsync("login");
        Assert.Equal(CliExitCode.ProtectedStoreUnavailable, login.Code);
        Assert.Contains("does not store sign-in tokens unprotected", login.Error, StringComparison.Ordinal);
        Assert.Equal(0, issuer.DeviceGrants);
        Assert.Null(issuer.LastUserCode);
        Assert.Equal(["config.json"], Directory.GetFiles(cli.ConfigurationDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task TheServerRefusingTheIdentityAfterARefreshMapsToSignInFailure()
    {
        await using var issuer = await CliTestIssuer.StartAsync();
        await using var stub = await CliApiStub.StartAsync(issuer);
        using var cli = new CliHarness(issuer, stub.Handler);
        await cli.ConfigureAsync();
        await cli.LoginAsync();
        stub.Enqueue("GetCatalogContext", CliStubReply.Error(401, CatalogErrorCodeV1.IdentityRejected),
            CliStubReply.Error(401, CatalogErrorCodeV1.IdentityRejected));

        var refused = await cli.RunAsync("context");
        Assert.Equal(CliExitCode.SignInFailed, refused.Code);
        Assert.Contains("identityRejected", refused.Error, StringComparison.Ordinal);
        // One forced refresh between the two refusals, then no further attempt.
        Assert.Equal(1, issuer.RefreshGrants);
        Assert.Equal(2, stub.Calls("GetCatalogContext").Count);

        // A refresh is forced once: later retries of the same request use the refreshed token.
        stub.Enqueue("GetCatalogContext", CliStubReply.Error(401, CatalogErrorCodeV1.IdentityRejected),
            CliStubReply.Error(429, CatalogErrorCodeV1.Unavailable, ("Retry-After", "1")), CliStubReply.Error(429, CatalogErrorCodeV1.Unavailable));
        var retried = await cli.RunAsync("context");
        Assert.True(retried.Exit == 0, retried.ToString());
        Assert.Equal(2, issuer.RefreshGrants);

        stub.Enqueue("GetCatalogContext", CliStubReply.Error(403, CatalogErrorCodeV1.IdentityRejected));
        var unbound = await cli.RunAsync("context");
        Assert.Equal(CliExitCode.SignInFailed, unbound.Code);
        stub.Enqueue("GetCatalogContext", CliStubReply.Error(403, CatalogErrorCodeV1.PermissionDenied));
        var denied = await cli.RunAsync("context");
        Assert.Equal(CliExitCode.PermissionDenied, denied.Code);
        stub.AssertClean();
    }
}
