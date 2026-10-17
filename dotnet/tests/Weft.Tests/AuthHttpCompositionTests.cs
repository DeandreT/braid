using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Weft.Api.Auth;
using Weft.Application;
using Weft.Identity;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class AuthHttpCompositionTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task MatchingOrganizationCannotReplaceExactBorrowedProfileOwnership() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var other = DurableSessionKeyProfile.Open(test.Files.Options(test.Data.Seed.OrganizationA), test.Files.Certificate, []); test.OwnProfile(other);
        foreach (string mismatch in new[] { "browser-profile", "entra-profile", "missing-browser", "missing-entra", "keyed-browser", "duplicate-auth", "duplicate-http" })
        {
            ServiceProvider? provider = null; bool refused = false; Exception? primary = null, cleanup = null;
            string operation = "setup";
            string expectedOperation = mismatch switch
            {
                "browser-profile" or "entra-profile" => "resolve-auth",
                "duplicate-auth" => "duplicate-auth",
                "duplicate-http" => "duplicate-http",
                _ => "register-auth"
            };
            var services = new ServiceCollection(); services.AddLogging();
            var identity = new PostgresExternalIdentityReader(test.First.IdentityPool);
            var transport = new EntraHttpsFixtureTransport(test.Issuer.Port, test.Crypto.Root);
            string origin = test.Origin.GetLeftPart(UriPartial.Authority);
            try
            {
                if (mismatch == "keyed-browser") services.AddKeyedSingleton<ServerSessionBrowser>("untrusted", test.First.Browser);
                else if (mismatch != "missing-browser") services.AddWeftBrowserSessions(new(origin), mismatch == "browser-profile" ? other : test.First.Profile,
                    new PostgresServerSessionReader(test.First.ReaderPool), new PostgresServerSessionWriter(test.First.WriterPool), TimeProvider.System);
                if (mismatch != "missing-entra") EntraClientRegistration.AddForHttpsFixture(services, new(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client, origin),
                    mismatch == "entra-profile" ? other : test.First.Profile, test.Crypto.ClientCertificate, transport);
                services.AddSingleton<IExternalIdentityReader>(identity);
                operation = "register-auth";
                services.AddWeftEntraBrowserAuthentication(new(test.Data.Seed.OrganizationA, origin), test.First.Profile, identity);
                if (mismatch == "duplicate-auth")
                {
                    operation = "duplicate-auth";
                    services.AddWeftEntraBrowserAuthentication(new(test.Data.Seed.OrganizationA, origin), test.First.Profile, identity);
                }
                if (mismatch == "duplicate-http")
                {
                    operation = "register-http";
                    var environment = new AuthHttpEnvironment();
                    services.AddWeftAuthenticationHttp(new(AuthenticationHttpMode.Session, origin), environment);
                    operation = "duplicate-http";
                    services.AddWeftAuthenticationHttp(new(AuthenticationHttpMode.Session, origin), environment);
                }
                operation = "build-provider";
                provider = services.BuildServiceProvider();
                operation = "resolve-auth";
                _ = provider.GetRequiredService<EntraBrowserAuthentication>();
            }
            catch (BrowserAuthenticationException error) when (mismatch != "duplicate-http" && operation == expectedOperation &&
                error.Kind == BrowserAuthenticationFailureKind.Unavailable)
            { refused = true; }
            catch (InvalidOperationException error) when (mismatch == "duplicate-http" && operation == expectedOperation &&
                error.GetType() == typeof(InvalidOperationException) && error.Message == "Authentication HTTP must be registered exactly once.")
            { refused = true; }
            catch (Exception error) { primary = AuthHttpFixture.Safe(error, "A rejected composition failed outside its closed configuration boundary."); }
            finally
            {
                if (provider is not null)
                {
                    try { Task original = provider.DisposeAsync().AsTask(); await original.WaitAsync(TimeSpan.FromSeconds(15)); }
                    catch (Exception) { test.RetainForeignRuntime(); cleanup = new AuthHttpFailure("A rejected-composition original provider disposal did not settle."); }
                }
            }
            if (!refused || transport.SnapshotAttempts().Count != 0) primary ??= new AuthHttpFailure("A mixed/keyed/duplicate profile composition was accepted or sent provider HTTP.");
            if (primary is not null && cleanup is not null) throw new AggregateException("Rejected-composition proof and original cleanup failed.", primary, cleanup);
            if (primary is not null) throw primary;
            if (cleanup is not null) throw cleanup;
        }
    });
}

internal sealed class AuthHttpEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; } = "Weft.Tests";
    public string ContentRootPath { get; set; } = "/";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
