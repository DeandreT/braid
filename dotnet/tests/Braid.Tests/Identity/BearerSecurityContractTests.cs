using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Braid.Ai;
using Braid.Api;
using Braid.Api.Auth;
using Braid.Api.Catalog;
using Braid.Api.Chat;
using Braid.Application;
using Braid.Application.Automation;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Identity;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>
/// Every route of a Session host with every business feature and bearer access enabled is exercised with a refused
/// bearer token and without credentials; the schemes it actually admits must be exactly those the shipped
/// contracts/braid-v1.json declares for that operation.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class BearerSecurityContractTests
{
    private const string Unreachable = "Host=127.0.0.1;Port=1;Database=braid_unopened;Username=braid_unopened;Password=braid-unopened;Pooling=false;Timeout=1";

    [Fact]
    public async Task Each_route_admits_exactly_the_schemes_its_OpenAPI_operation_declares()
    {
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "contracts", "braid-v1.json")));
        var declared = new Dictionary<(string, string), string>();
        foreach (var path in contract.RootElement.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
                declared[(path.Name, operation.Name.ToUpperInvariant())] = Schemes(operation.Value);
        await using var host = await Host.StartAsync();
        var observed = new Dictionary<(string, string), string>();
        foreach (var (pattern, method) in host.Routes(declared.Keys))
        {
            var path = RouteParameter().Replace(pattern, match => match.Groups[1].Value.EndsWith("Id", StringComparison.Ordinal)
                ? Guid.NewGuid().ToString("D") : "1");
            using var refused = await host.SendAsync(method, path, "Bearer not-a-jwt");
            using var anonymous = await host.SendAsync(method, path, null);
            var challenge = refused.Headers.WwwAuthenticate.ToString();
            string actual;
            if (refused.StatusCode == HttpStatusCode.Unauthorized && challenge == "Bearer error=\"invalid_token\"")
            {
                // The bearer path ran; without credentials the session path refuses exactly like a missing cookie.
                Assert.True(anonymous.StatusCode == HttpStatusCode.Unauthorized && anonymous.Headers.WwwAuthenticate.Count == 0,
                    $"{method} {pattern} admitted a request without credentials.");
                actual = HttpMethods.IsGet(method) ? "braidSession | entraBearer" : "braidSession+braidAntiforgery | entraBearer";
            }
            else if (pattern.StartsWith("/api/auth/", StringComparison.Ordinal))
            {
                Assert.True(challenge.Length == 0 && (int)refused.StatusCode >= 400, $"{method} {pattern} admitted a bearer token.");
                actual = HttpMethods.IsGet(method) ? "anonymous | braidSession" : "anonymous | braidSession+braidAntiforgery";
            }
            else
            {
                Assert.True(challenge.Length == 0 && anonymous.IsSuccessStatusCode, $"{method} {pattern} is neither public nor admitted.");
                actual = "none";
            }
            observed[(pattern, method)] = actual;
        }
        Assert.NotEmpty(observed);
        foreach (var (operation, schemes) in observed)
        {
            Assert.True(declared.TryGetValue(operation, out var expected), $"{operation} is mapped but absent from the contract.");
            Assert.True(expected == schemes, $"{operation.Item2} {operation.Item1} admits {schemes} but declares {expected}.");
        }
        // Sandbox execution is Development-only, and hosted startup refuses its enabled switch.
        // Keep the exact excluded operations rather than exempting a route prefix.
        const string sandbox = "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/sandbox";
        string[] developmentOnly =
        [
            "GET /api/v1/fixtures/projections/{fixtureId}",
            "GET " + sandbox,
            "POST " + sandbox + "/deployments/{deploymentId}",
            "GET " + sandbox + "/{targetId}/messages",
            "GET " + sandbox + "/{targetId}/messages/{messageId}",
            "POST " + sandbox + "/{targetId}/messages/{messageId}"
        ];
        Assert.Equal(developmentOnly.Order(StringComparer.Ordinal),
            declared.Keys.Where(key => !observed.ContainsKey(key)).Select(key => key.Item2 + " " + key.Item1).Order(StringComparer.Ordinal));
    }

    private static string Schemes(JsonElement operation)
    {
        if (!operation.TryGetProperty("security", out var security)) return "none";
        return string.Join(" | ", security.EnumerateArray().Select(requirement =>
            requirement.EnumerateObject().Any() ? string.Join('+', requirement.EnumerateObject().Select(scheme => scheme.Name)) : "anonymous"));
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Path.GetDirectoryName(sourceFile)! })
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "global.json")) && File.Exists(Path.Combine(directory.FullName, "contracts", "braid-v1.json")))
                    return directory.FullName;
        throw new InvalidOperationException("The repository contract is required for the security parity check.");
    }

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex RouteParameter();

    /// <summary>
    /// The Session host's composition with unreachable stores: a refused token is decided before any provider or database
    /// work, and a request without credentials stops at session admission without a cookie to read.
    /// </summary>
    private sealed class Host : IAsyncDisposable
    {
        private WebApplication app = null!;
        private HttpClient client = null!;
        private SessionTicketQualificationRoot files = null!;
        private DurableSessionKeyProfile profile = null!;
        private EntraProtocolCrypto crypto = null!;
        private EntraUserTokenValidator validator = null!;
        private readonly List<IAsyncDisposable> pools = [];

        internal static async Task<Host> StartAsync()
        {
            var host = new Host();
            try { await host.InitializeAsync(); return host; }
            catch { await host.DisposeAsync(); throw; }
        }

        private async Task InitializeAsync()
        {
            var organization = Guid.NewGuid();
            files = new();
            using (DurableSessionKeyProfile.BootstrapEmpty(files.Options(organization), files.Certificate, [],
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2))) { }
            profile = DurableSessionKeyProfile.Open(files.Options(organization), files.Certificate, []);
            crypto = new();
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(1);
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var origin = "https://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sessionReader = Own(new PostgresServerSessionDatabase(Unreachable, ServerSessionDatabaseProfile.Reader));
            var sessionWriter = Own(new PostgresServerSessionDatabase(Unreachable, ServerSessionDatabaseProfile.Writer));
            var identityPool = Own(new PostgresExternalIdentityDatabase(Unreachable));
            var identities = new PostgresExternalIdentityReader(identityPool);
            var audit = Own(new PostgresApiRequestAuditWriter(Unreachable));
            validator = new(new(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client, UserTokenValidationTests.Scope,
                [UserTokenValidationTests.Cli]));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
            builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen =>
            { listen.Protocols = HttpProtocols.Http1; listen.UseHttps(crypto.ApplicationTls); }));
            builder.Services.AddBraidApi();
            builder.Services.AddSingleton(new Braid.Api.Specs.SpecApiOptions(true));
            builder.Services.AddSingleton(new ReleaseOperationsOptions(true));
            builder.Services.AddSingleton<IExternalIdentityReader>(identities);
            builder.Services.AddBraidBrowserSessions(new(origin), profile, new PostgresServerSessionReader(sessionReader),
                new PostgresServerSessionWriter(sessionWriter), TimeProvider.System);
            builder.Services.AddBraidEntraClient(new(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client, origin), profile, crypto.ClientCertificate);
            builder.Services.AddBraidEntraBrowserAuthentication(new(organization, origin), profile, identities);
            builder.Services.AddBraidAuthenticationHttp(new(AuthenticationHttpMode.Session, origin), builder.Environment);
            builder.Services.AddBraidSessionCatalog(new(organization), new CatalogMetadataEditOptions(true), new CatalogRevisionIngestionOptions(true),
                new CatalogProjectCreationOptions(true), new CatalogRevisionReportOptions(true, true));
            builder.Services.AddBraidBearerAccess(new(organization), validator, identities, audit);
            builder.Services.AddSingleton(new CatalogInterchangeOptions(true));
            builder.Services.AddSingleton<IInterchangeBackendRegistry>(_ => throw new InvalidOperationException("Refused credentials must not resolve interchange workers."));
            builder.Services.AddSingleton<InterchangeImportService>(_ => throw new InvalidOperationException("Refused credentials must not resolve import storage."));
            builder.Services.AddSingleton<InterchangeExportService>(_ => throw new InvalidOperationException("Refused credentials must not resolve export storage."));
            // Every business feature is on, so each route reaches its admission.
            var image = "sha256:" + new string('a', 64);
            builder.Services.AddBraidRegressionCases(new(true, new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, image,
                FerrulePreviewBackend.AdapterBuild, PreviewPolicy.Profile)));
            builder.Services.AddBraidRegressionRuns(new(true));
            builder.Services.AddSingleton(new CatalogAutomationOptions(true));
            builder.Services.AddSingleton<IAutomationStore>(_ => throw new InvalidOperationException("Refused credentials must not resolve automation stores."));
            builder.Services.AddBraidBuildApi(new(true, new BuildBackendIdentity("ferrule", FerruleInspectionBackend.EngineRevision, image,
                FerrulePreviewBackend.AdapterBuild), FerruleBuildCompiler.Profile));
            builder.Services.AddBraidPreviews(new CatalogPreviewOptions(true));
            builder.Services.AddBraidChat(new([new ChatModelProfile("Unreachable model", "unreachable", AiProfileSpec.Create(AiProfileKey.Create("unreachable", 1),
                AiDialect.ChatCompletionsSseV1, "unreachable-model", new Uri("http://127.0.0.1:1/v1/chat/completions"), true, AiAuthentication.Bearer, "unreachable-credential",
                AiCapability.Text, "mapping-metadata", 1, true, false))], TimeSpan.FromDays(1), TimeSpan.FromSeconds(60), 128));
            app = builder.Build();
            app.UseRouting();
            app.MapBraidHealth();
            app.MapBraidAuthentication();
            app.MapBraidCatalog();
            Braid.Api.Specs.SpecEndpoints.MapBraidSpecs(app);
            Braid.Api.Creation.CreationEndpoints.MapBraidCreation(app);
            app.MapBraidHelp();
            app.MapBraidRegressionCases();
            app.MapBraidRegressionRuns();
            app.MapBraidAutomation();
            app.MapBraidBuilds();
            app.MapBraidReleaseOperations();
            app.MapBraidChat();
            using var start = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await app.StartAsync(start.Token);
            client = AuthHttpFixture.NewClient(crypto.Root, "127.0.0.1", port, port);
            client.BaseAddress = new Uri(origin);
        }

        private T Own<T>(T resource) where T : IAsyncDisposable { pools.Add(resource); return resource; }

        // The authentication routes answer every method themselves (405 with Allow); their operations come from the contract.
        internal IEnumerable<(string Pattern, string Method)> Routes(IEnumerable<(string Path, string Method)> declared) =>
            app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods
                ? methods.Select(method => (endpoint.RoutePattern.RawText!, method))
                : declared.Where(item => item.Path == endpoint.RoutePattern.RawText).Select(item => (item.Path, item.Method)))
            .Distinct().OrderBy(route => route.Item1, StringComparer.Ordinal).ThenBy(route => route.Item2, StringComparer.Ordinal).ToArray();

        internal async Task<HttpResponseMessage> SendAsync(string method, string path, string? authorization)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
            if (path == "/api/auth/session") request.Headers.TryAddWithoutValidation("X-Braid-Session", "1");
            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            client?.Dispose();
            if (app is not null) await app.DisposeAsync();
            if (validator is not null) await validator.DisposeAsync();
            foreach (var pool in pools) await pool.DisposeAsync();
            profile?.Dispose();
            crypto?.Dispose();
            files?.Dispose();
        }
    }
}
