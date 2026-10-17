using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Identity;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
public sealed class AuthHttpDisabledModeTests
{
    [Fact]
    public Task DisabledStatusNeedsNoIdentityProviderProfilePoolOrModeGuessing() => AuthHttpModeFixture.RunAsync(null, async test =>
    {
        var reply = await test.StatusAsync();
        AuthHttpFixture.Check(reply.Json().GetProperty("mode").GetString() == "disabled" && reply.Json().EnumerateObject().Count() == 2, "Disabled mode inferred a session state or exposed extra fields.");
        AuthHttpFixture.Check(test.Application.Services.GetService<EntraBrowserAuthentication>() is null && test.Application.Services.GetService<ServerSessionBrowser>() is null
            && test.Application.Services.GetService<EntraBrowserClient>() is null && test.Application.Services.GetService<DurableSessionKeyProfile>() is null && test.Application.Services.GetService<IExternalIdentityReader>() is null, "Disabled status constructed a sensitive identity dependency.");
        using var login = await test.Client.PostAsync(AuthHttpFixture.LoginPath, null);
        AuthHttpFixture.Check(login.StatusCode == HttpStatusCode.NotFound, "A disabled mode mapped session action routes.");
        using var head = new HttpRequestMessage(HttpMethod.Head, AuthHttpFixture.StatusPath); head.Headers.Add("X-Weft-Session", "1"); using var refused = await test.Client.SendAsync(head);
        AuthHttpFixture.Check(refused.StatusCode == HttpStatusCode.MethodNotAllowed && refused.Content.Headers.Allow.SequenceEqual(new[] { "GET" }), "Disabled status admitted HEAD rather than its fixed method.");
    });
}

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class AuthHttpDevelopmentModeTests(PostgresFixture postgres)
{
    [PostgresFact]
    public async Task ExplicitDevelopmentStatusPreservesExistingCatalogAndUnsafeTokenFlow()
    {
        PostgresMappingMetadataTestSupport? data = null; Exception? primary = null; Exception? cleanup = null; Task? dataRelease = null; bool retainDependencies = false;
        try
        {
            data = await PostgresMappingMetadataTestSupport.CreateAsync(postgres);
            await AuthHttpModeFixture.RunAsync(data, async test =>
            {
                var status = await test.StatusAsync(); AuthHttpFixture.Check(status.Json().GetProperty("mode").GetString() == "development" && status.Json().EnumerateObject().Count() == 2, "Development status acquired session fields.");
                using var response = await test.Client.GetAsync(AuthHttpFixture.ContextPath); using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                AuthHttpFixture.Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("mode").GetString() == "development" && !json.RootElement.TryGetProperty("identityContext", out _), "Development context serialization changed.");
                string token = json.RootElement.GetProperty("requestToken").GetString()!;
                using var request = new HttpRequestMessage(HttpMethod.Post, AuthHttpModeFixture.UnsafePath) { Content = new ByteArrayContent([1]) };
                request.Headers.Add("Origin", test.Origin.GetLeftPart(UriPartial.Authority)); request.Headers.Add("X-XSRF-TOKEN", token);
                using var accepted = await test.Client.SendAsync(request);
                AuthHttpFixture.Check(accepted.StatusCode == HttpStatusCode.OK && test.BodyReads == 1 && test.Business == 1, "Development's actual catalog/epoch token path stopped admitting its positive unsafe control.");
                AuthHttpFixture.Check(test.Application.Services.GetService<EntraBrowserAuthentication>() is null && test.Application.Services.GetService<ServerSessionBrowser>() is null && test.Application.Services.GetService<EntraBrowserClient>() is null, "Development status constructed session-mode identity services.");
            }, retainDependencies: () => retainDependencies = true);
        }
        catch (Exception error) { primary = AuthHttpFixture.Safe(error, "The Development auth compatibility case failed."); }
        finally
        {
            if (retainDependencies) cleanup = new AuthHttpFailure("Development restricted dependencies retained because original host settlement is unproven.");
            else if (data is not null) try { dataRelease ??= data.DisposeAsync().AsTask(); await dataRelease.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { cleanup = new AuthHttpFailure("The Development restricted case did not settle."); }
        }
        if (primary is not null && cleanup is not null) throw new AggregateException("Development auth and cleanup failed.", primary, cleanup);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanup is not null) throw cleanup;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class AuthHttpModeFixture
{
    internal const string UnsafePath = "/api/v1/__weft118/development-unsafe";
    internal WebApplication Application = null!;
    internal HttpClient Client = null!;
    internal Uri Origin = null!;
    internal int BodyReads, Business;
    internal static async Task RunAsync(PostgresMappingMetadataTestSupport? data, Func<AuthHttpModeFixture, Task> body, Action? retainDependencies = null)
    {
        var test = new AuthHttpModeFixture(); Exception? primary = null; var errors = new List<Exception>(); Task? stop = null, dispose = null;
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(1);
        try
        {
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port; test.Origin = new("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = data is null ? Environments.Production : Environments.Development, ApplicationName = typeof(AuthHttpModeFixture).Assembly.FullName });
            builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders(); builder.Services.AddWeftApi();
            if (data is not null)
            {
                builder.Services.AddWeftCatalog(new CatalogApiOptions(true, true, data.Seed.Alice, data.Seed.OrganizationA, test.Origin.GetLeftPart(UriPartial.Authority)), builder.Environment);
                builder.Services.AddSingleton<ICatalogReader>(data.Reader);
            }
            builder.Services.AddWeftAuthenticationHttp(new(data is null ? AuthenticationHttpMode.Disabled : AuthenticationHttpMode.Development, test.Origin.GetLeftPart(UriPartial.Authority)), builder.Environment);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            test.Application = builder.Build(); test.Application.MapWeftAuthentication();
            if (data is not null)
            {
                test.Application.MapWeftCatalog();
                test.Application.MapPost(UnsafePath, async (HttpContext context, CatalogRequestBoundary boundary) =>
                {
                    var admission = await boundary.AuthenticateAsync(context, unsafeRequest: true, context.RequestAborted);
                    if (!admission.IsAdmitted) return (IResult)Results.StatusCode(admission.StatusCode);
                    var organization = await data.Reader.GetOrganizationAdmissionAsync(admission.Actor!, admission.AllowedOrganizationId!.Value, context.RequestAborted);
                    if (organization is null) return (IResult)Results.NotFound();
                    test.BodyReads++; using var bytes = new MemoryStream(); await context.Request.Body.CopyToAsync(bytes, context.RequestAborted); test.Business++;
                    return (IResult)Results.Ok();
                }).ExcludeFromDescription();
            }
            test.Client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = true, CookieContainer = new(), ActivityHeadersPropagator = null }) { BaseAddress = test.Origin, Timeout = TimeSpan.FromSeconds(15) };
            reservation.Stop(); using var start = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await test.Application.StartAsync(start.Token); await body(test);
        }
        catch (Exception error) { primary = AuthHttpFixture.Safe(error, "The explicit auth-mode HTTP case failed."); }
        finally
        {
            reservation.Stop(); try { test.Client?.Dispose(); } catch (Exception) { errors.Add(new AuthHttpFailure("The mode client cleanup failed.")); }
            if (test.Application is not null)
            {
                try { stop = test.Application.StopAsync(); await stop.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The original mode host stop failed.")); }
                try { dispose = test.Application.DisposeAsync().AsTask(); await dispose.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The original mode host disposal failed.")); }
                if (stop is not { IsCompletedSuccessfully: true } || dispose is not { IsCompletedSuccessfully: true }) { retainDependencies?.Invoke(); errors.Add(new AuthHttpFailure("Original mode host settlement is unproven.")); }
            }
        }
        if (primary is not null && errors.Count > 0) throw new AggregateException("Auth mode and original cleanup failed.", new[] { primary }.Concat(errors));
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (errors.Count > 0) throw new AggregateException("Original auth mode cleanup failed.", errors);
    }
    internal async Task<AuthHttpReply> StatusAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthHttpFixture.StatusPath); request.Headers.Add("X-Weft-Session", "1"); using var response = await Client.SendAsync(request);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(); AuthHttpFixture.Check(bytes.Length <= 1024, "Mode status exceeded its bounded union.");
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new AuthHttpReply(response.StatusCode, headers, bytes, new()); AuthHttpFixture.SharedHeaders(result); AuthHttpFixture.Check(result.Status == HttpStatusCode.OK, "Explicit mode status was unavailable."); return result;
    }
}
