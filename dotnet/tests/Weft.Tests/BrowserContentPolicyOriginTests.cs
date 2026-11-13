using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Auth;
using Weft.Server;

namespace Weft.Tests;

public sealed class BrowserContentPolicyOriginTests
{
    [Fact]
    public async Task DisabledPortZeroUsesThePublishedListenerOriginAndIgnoresRequestOriginHeaders()
    {
        var root = CreateWebRoot();
        try
        {
            var options = new AuthenticationHttpOptions(AuthenticationHttpMode.Disabled, "http://127.0.0.1:0");
            var builder = CreateBuilder(root.FullName);
            builder.Services.AddWeftAuthenticationHttp(options, builder.Environment);
            await using var app = builder.Build();
            ConfigurePipeline(app, options);
            app.MapWeftAuthentication();
            Assert.Null(options.AdmittedBrowserOrigin);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await app.StartAsync(deadline.Token);
            var origin = new Uri(Assert.Single(app.Urls)).GetLeftPart(UriPartial.Authority);
            Assert.NotEqual(0, new Uri(origin).Port);
            Assert.Equal(origin, options.AdmittedBrowserOrigin);
            Assert.Equal("http://127.0.0.1:0", options.ExpectedOrigin);
            using var client = CreateClient(origin);
            var workerPolicy = ExpectedEditorPolicy(origin);

            // Make the forged request first: headers cannot establish the origin,
            // and a later ordinary request must retain the same published snapshot.
            using (var forged = new HttpRequestMessage(HttpMethod.Get, "/edit/worker.js"))
            {
                forged.Headers.Host = "attacker.invalid:4312";
                forged.Headers.Add("Origin", "https://attacker.invalid:4312");
                forged.Headers.Add("Forwarded", "host=attacker.invalid:4312;proto=https");
                forged.Headers.Add("X-Forwarded-Host", "attacker.invalid:4312");
                forged.Headers.Add("X-Forwarded-Proto", "https");
                forged.Headers.Add("X-Forwarded-Port", "4312");
                using var response = await client.SendAsync(forged, deadline.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                AssertHeaders(response, workerPolicy);
                Assert.Equal("export {};", await response.Content.ReadAsStringAsync(deadline.Token));
            }

            foreach (var path in new[] { "/", "/catalog/deep-link", "/edit/worker.js", "/edit/core.js", "/edit/core.wasm", "/edit/", "/edit/missing.js" })
            {
                using var response = await client.GetAsync(path, deadline.Token);
                var missing = path is "/edit/" or "/edit/missing.js";
                Assert.Equal(missing ? HttpStatusCode.NotFound : HttpStatusCode.OK, response.StatusCode);
                AssertHeaders(response, !missing && path.StartsWith("/edit/", StringComparison.Ordinal)
                    ? workerPolicy : BrowserSecurityHeaders.ContentSecurityPolicy);
                if (missing) Assert.Empty(await response.Content.ReadAsByteArrayAsync(deadline.Token));
            }

            using (var head = new HttpRequestMessage(HttpMethod.Head, "/edit/worker.js"))
            using (var response = await client.SendAsync(head, deadline.Token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                AssertHeaders(response, workerPolicy);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync(deadline.Token));
            }
            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
            statusRequest.Headers.Add("X-Weft-Session", "1");
            using var status = await client.SendAsync(statusRequest, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.Equal(origin, options.AdmittedBrowserOrigin);
            await app.StopAsync(deadline.Token);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(AuthenticationHttpMode.Development, "http://127.0.0.1:4100")]
    [InlineData(AuthenticationHttpMode.Session, "https://weft.example")]
    [InlineData(AuthenticationHttpMode.Disabled, "http://127.0.0.1:4100")]
    public async Task ConfiguredOriginsRemainUnchangedWhenTheListenerAndRequestUseAnotherAuthority(
        AuthenticationHttpMode mode, string configuredOrigin)
    {
        var root = CreateWebRoot();
        try
        {
            var options = new AuthenticationHttpOptions(mode, configuredOrigin);
            var builder = CreateBuilder(root.FullName);
            await using var app = builder.Build();
            ConfigurePipeline(app, options);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await app.StartAsync(deadline.Token);
            var origin = new Uri(Assert.Single(app.Urls)).GetLeftPart(UriPartial.Authority);
            using var client = CreateClient(origin);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/edit/worker.js");
            request.Headers.Host = "attacker.invalid:4312";
            request.Headers.Add("Origin", "http://attacker.invalid:4312");
            using var response = await client.SendAsync(request, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertHeaders(response, ExpectedEditorPolicy(configuredOrigin));
            Assert.Equal(configuredOrigin, options.ExpectedOrigin);
            Assert.Equal(configuredOrigin, options.AdmittedBrowserOrigin);
            await app.StopAsync(deadline.Token);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task AnUnpublishedDisabledListenerCannotServeEditorBytesUnderPortZeroPolicy()
    {
        var root = CreateWebRoot();
        try
        {
            var builder = CreateBuilder(root.FullName);
            await using var app = builder.Build();
            var pipeline = ((IApplicationBuilder)app).New();
            pipeline.UseWeftBrowserSecurityHeaders();
            BrowserContentPolicy.Use(pipeline, new AuthenticationHttpOptions(AuthenticationHttpMode.Disabled, "http://127.0.0.1:0"));
            pipeline.Run(_ => throw new InvalidOperationException("Unpublished editor request reached static serving."));
            var request = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            request.Request.Path = "/edit/worker.js";
            using var output = new MemoryStream();
            request.Response.Body = output;
            await pipeline.Build()(request);
            Assert.Equal(503, request.Response.StatusCode);
            Assert.Equal(BrowserSecurityHeaders.ContentSecurityPolicy, request.Response.Headers.ContentSecurityPolicy.ToString());
            Assert.Equal(0, request.Response.Body.Length);
        }
        finally { root.Delete(true); }
    }

    private static WebApplicationBuilder CreateBuilder(string webRoot)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", WebRootPath = webRoot });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => DevelopmentListenAddress.ConfigureKestrel(server, new IPEndPoint(IPAddress.Loopback, 0)));
        return builder;
    }

    private static void ConfigurePipeline(WebApplication app, AuthenticationHttpOptions options)
    {
        app.UseWeftBrowserSecurityHeaders();
        BrowserContentPolicy.Use(app, options);
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallbackToFile("index.html");
    }

    private static DirectoryInfo CreateWebRoot()
    {
        var root = Directory.CreateTempSubdirectory("weft-bound-origin-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "edit"));
        File.WriteAllText(Path.Combine(root.FullName, "index.html"), "<!doctype html><title>shell</title>");
        File.WriteAllText(Path.Combine(root.FullName, "edit", "worker.js"), "export {};");
        File.WriteAllText(Path.Combine(root.FullName, "edit", "core.js"), "export {};");
        File.WriteAllBytes(Path.Combine(root.FullName, "edit", "core.wasm"), [0, 97, 115, 109, 1, 0, 0, 0]);
        return root;
    }

    private static HttpClient CreateClient(string origin) => new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };

    private static string ExpectedEditorPolicy(string origin) =>
        $"default-src 'none'; script-src {origin}/edit/ 'wasm-unsafe-eval'; worker-src 'none'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static void AssertHeaders(HttpResponseMessage response, string policy)
    {
        foreach (var (name, value) in new[] {
            ("Content-Security-Policy", policy), ("X-Content-Type-Options", "nosniff"),
            ("Referrer-Policy", "no-referrer"), ("Cross-Origin-Opener-Policy", "same-origin"),
            ("Cross-Origin-Resource-Policy", "same-origin") })
            Assert.Equal(value, Assert.Single(response.Headers.GetValues(name)));
    }
}
