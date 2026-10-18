using System.Net;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weft.Api.Auth;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
public sealed class DisabledStatusBindingTests
{
    [Fact]
    public async Task DisabledActualBindingIsClosedBeforeStartedAndOneImmutableSnapshotGovernsHostAndOrigin()
    {
        var options = new AuthenticationHttpOptions(AuthenticationHttpMode.Disabled, "http://127.0.0.1:0");
        var server = new DisabledBindingServer(["http://127.0.0.1:58413"]);
        using var host = Build(server, options);
        ServerHostCheck.That(host.Services.GetServices<IHostedService>().OfType<IHostedLifecycleService>().Count() == 1
            && host.Services.GetService<Weft.Identity.ServerSessionBrowser>() is null
            && host.Services.GetService<Weft.Identity.EntraBrowserClient>() is null,
            "Disabled registration did not retain one normal binder without sensitive composition.");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task start = host.StartAsync(budget.Token);
        try
        {
            await server.Entered.Task.WaitAsync(budget.Token);
            var closed = await server.RequestAsync("127.0.0.1:58413", "http://127.0.0.1:58413");
            ServerHostCheck.That(closed.Status == 503, "A Disabled status succeeded before actual-origin publication.");
            ServerHostHttp.PrivateHeaders(closed);
            server.Release.TrySetResult(); await start.WaitAsync(budget.Token);
            ServerHostHttp.Mode(await server.RequestAsync("127.0.0.1:58413", "http://127.0.0.1:58413"), "disabled");
            ServerHostCheck.That((await server.RequestAsync("127.0.0.1:58413", "http://127.0.0.1:0")).Status is 400 or 403,
                "Configured port zero became admitted request origin.");
            ServerHostCheck.That((await server.RequestAsync("127.0.0.1:58414", "http://127.0.0.1:58414")).Status is 400 or 403,
                "A request header selected the Disabled origin.");
            server.Addresses.Addresses.Clear(); server.Addresses.Addresses.Add("http://127.0.0.1:58414");
            ServerHostHttp.Mode(await server.RequestAsync("127.0.0.1:58413", "http://127.0.0.1:58413"), "disabled");
            ServerHostCheck.That(options.ExpectedOrigin == "http://127.0.0.1:0",
                "Actual binding rewrote public configured options.");
        }
        finally
        {
            server.Release.TrySetResult();
            try { await start.WaitAsync(budget.Token); } catch (Exception) { }
            await host.StopAsync(budget.Token);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("multiple")]
    [InlineData("zero")]
    [InlineData("different-loopback")]
    [InlineData("public")]
    [InlineData("wildcard")]
    [InlineData("dns")]
    [InlineData("path")]
    public async Task InvalidActualFeatureFailsStartupWithoutPublishingStatus(string kind)
    {
        string[] addresses = kind switch
        {
            "multiple" => ["http://127.0.0.1:58413", "http://127.0.0.1:58414"],
            "zero" => ["http://127.0.0.1:0"],
            "different-loopback" => ["http://127.0.0.2:58413"],
            "public" => ["http://192.0.2.1:58413"],
            "wildcard" => ["http://0.0.0.0:58413"],
            "dns" => ["http://localhost:58413"],
            "path" => ["http://127.0.0.1:58413/path"],
            _ => []
        };
        var server = new DisabledBindingServer(addresses, kind == "missing");
        using var host = Build(server, new(AuthenticationHttpMode.Disabled, "http://127.0.0.1:0"));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task start = host.StartAsync(budget.Token); bool refused = false;
        try
        {
            await server.Entered.Task.WaitAsync(budget.Token); server.Release.TrySetResult();
            try { await start.WaitAsync(budget.Token); } catch (Exception) { refused = true; }
            ServerHostCheck.That(refused, "Invalid actual binding did not fail normal host startup.");
            ServerHostCheck.That((await server.RequestAsync("127.0.0.1:58413", null)).Status == 503,
                "Invalid actual binding published a successful status.");
        }
        finally
        {
            server.Release.TrySetResult();
            try { await start.WaitAsync(budget.Token); } catch (Exception) { }
            await host.StopAsync(budget.Token);
        }
        ServerHostCheck.That(server.StopCalls == 1, "Normal partial-start cleanup did not stop the actual registered server.");
    }

    [Theory]
    [InlineData(AuthenticationHttpMode.Development)]
    [InlineData(AuthenticationHttpMode.Session)]
    public void EnabledModesDoNotAcquireDisabledPortZeroGrammar(AuthenticationHttpMode mode)
    {
        bool refused = false;
        try { _ = new AuthenticationHttpOptions(mode, "http://127.0.0.1:0"); }
        catch (Exception) { refused = true; }
        ServerHostCheck.That(refused, "An enabled authentication mode admitted port-zero options.");
    }

    [Fact]
    public async Task PublishedDisabledOriginRetainsOrdinaryShapeAndPeerRefusals()
    {
        var server = new DisabledBindingServer(["http://127.0.0.1:58413"]); server.Release.TrySetResult();
        using var host = Build(server, new(AuthenticationHttpMode.Disabled, "http://127.0.0.1:0"));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.StartAsync(budget.Token);
        try
        {
            foreach (var request in new Func<Task<ServerHostReply>>[] {
                () => server.RequestAsync("127.0.0.1:58413", null, query: "?owned=probe"),
                () => server.RequestAsync("127.0.0.1:58413", null, body: [1]),
                () => server.RequestAsync("127.0.0.1:58413", null, statusHeader: false),
                () => server.RequestAsync("127.0.0.1:58413", null, extra: [new("Authorization", "Bearer owned-probe")]),
                () => server.RequestAsync("127.0.0.1:58413", null, extra: [new("Sec-Fetch-Site", "cross-site")]),
                () => server.RequestAsync("127.0.0.1:58413", null, nonloopback: true),
                () => server.RequestAsync("127.0.0.1:58413", "http://127.0.0.1:58414") })
            {
                var reply = await request();
                ServerHostCheck.That(reply.Status is 400 or 403, "Actual-origin publication bypassed an ordinary status shape/peer refusal.");
                ServerHostHttp.PrivateHeaders(reply);
            }
            ServerHostCheck.That((await server.RequestAsync("127.0.0.1:58413", null, method: "POST")).Status == 405,
                "Actual-origin publication changed the fixed status method refusal.");
            var missingBodyDetection = await server.RequestAsync("127.0.0.1:58413", null, canHaveBody: null);
            ServerHostCheck.That(missingBodyDetection.Status == 503,
                "Actual-origin publication admitted a request without the server body-detection feature.");
            ServerHostHttp.PrivateHeaders(missingBodyDetection);
            var bodyCapable = await server.RequestAsync("127.0.0.1:58413", null, canHaveBody: true);
            ServerHostCheck.That(bodyCapable.Status == 400,
                "Actual-origin publication admitted a body-capable request without body headers.");
            ServerHostHttp.PrivateHeaders(bodyCapable);
            ServerHostHttp.Mode(await server.RequestAsync("127.0.0.1:58413", null, canHaveBody: false), "disabled");
        }
        finally { await host.StopAsync(budget.Token); }
    }

    private static IHost Build(DisabledBindingServer server, AuthenticationHttpOptions options) =>
        Host.CreateDefaultBuilder([]).ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(web =>
            {
                web.ConfigureServices((context, services) =>
                {
                    services.AddSingleton<IServer>(server);
                    services.AddRouting();
                    services.AddWeftAuthenticationHttp(options, context.HostingEnvironment);
                });
                web.Configure(app => { app.UseRouting(); app.UseEndpoints(routes => routes.MapWeftAuthentication()); });
            }).Build();
}

/// <summary>Standard server/feature boundary only; no production binder access or listener.</summary>
internal sealed class DisabledBindingServer : IServer
{
    internal ServerAddressesFeature Addresses { get; } = new();
    public IFeatureCollection Features { get; } = new FeatureCollection();
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<IFeatureCollection, Task>? dispatch;
    internal int StopCalls;

    internal DisabledBindingServer(IEnumerable<string> addresses, bool missing = false)
    {
        foreach (string address in addresses) Addresses.Addresses.Add(address);
        if (!missing) Features.Set<IServerAddressesFeature>(Addresses);
    }

    public async Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
    {
        dispatch = async features =>
        {
            TContext context = application.CreateContext(features); Exception? failure = null;
            try { await application.ProcessRequestAsync(context); }
            catch (Exception error) { failure = error; throw; }
            finally { application.DisposeContext(context, failure); }
        };
        Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
    }

    internal async Task<ServerHostReply> RequestAsync(string authority, string? origin, string method = "GET", string query = "",
        IReadOnlyList<KeyValuePair<string, string>>? extra = null, byte[]? body = null, bool statusHeader = true, bool nonloopback = false,
        bool? canHaveBody = false)
    {
        ServerHostCheck.That(dispatch is not null, "The normal fake server application was not initialized.");
        using var input = new MemoryStream(body ?? []);
        var request = new HttpRequestFeature
        {
            Method = method,
            Scheme = "http",
            Protocol = "HTTP/1.1",
            Path = "/api/auth/session",
            RawTarget = "/api/auth/session" + query,
            QueryString = query,
            Body = input
        };
        request.Headers.Host = authority;
        if (statusHeader) request.Headers["X-Weft-Session"] = "1";
        if (body is not null) request.Headers.ContentLength = body.Length;
        if (extra is not null) foreach (var field in extra) request.Headers[field.Key] = field.Value;
        if (origin is not null) request.Headers.Origin = origin;
        var response = new HttpResponseFeature(); using var output = new MemoryStream();
        var features = new FeatureCollection(); features.Set<IHttpRequestFeature>(request); features.Set<IHttpResponseFeature>(response);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(output));
        if (canHaveBody is bool detected) features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(detected));
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            LocalIpAddress = IPAddress.Loopback,
            LocalPort = 58413,
            RemoteIpAddress = nonloopback ? IPAddress.Parse("192.0.2.1") : IPAddress.Loopback,
            RemotePort = 15001
        });
        await dispatch!(features).WaitAsync(TimeSpan.FromSeconds(5));
        return new(response.StatusCode, response.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.Select(value => value ?? "").ToArray(), StringComparer.OrdinalIgnoreCase), output.ToArray());
    }

    public Task StopAsync(CancellationToken cancellationToken) { Interlocked.Increment(ref StopCalls); return Task.CompletedTask; }
    public void Dispose() { }

    private sealed record BodyDetection(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;
}
