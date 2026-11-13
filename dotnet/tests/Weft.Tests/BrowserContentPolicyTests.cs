using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Server;

namespace Weft.Tests;

public sealed class BrowserContentPolicyTests
{
    [Fact]
    public async Task CombinedHeadersKeepTheWorkerExceptionAndRefuseMissingEditorAssets()
    {
        var root = Directory.CreateTempSubdirectory("weft-combined-csp-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "edit"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "edit", "worker.js"), "export {};");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "index.html"), "<!doctype html><title>shell</title>");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", WebRootPath = root.FullName });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            await using var app = builder.Build();
            app.UseWeftBrowserSecurityHeaders();
            BrowserContentPolicy.Use(app, "https://weft.example");
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapFallbackToFile("index.html");
            await app.StartAsync();
            using var client = app.GetTestClient();
            const string workerPolicy = "default-src 'none'; script-src https://weft.example/edit/ 'wasm-unsafe-eval'; worker-src 'none'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            foreach (var path in new[] { "/", "/catalog/deep-link", "/edit/worker.js", "/edit/", "/edit/missing.js" })
            {
                using var response = await client.GetAsync(path);
                var missing = path is "/edit/" or "/edit/missing.js";
                Assert.Equal(missing ? HttpStatusCode.NotFound : HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(path == "/edit/worker.js" ? workerPolicy : BrowserSecurityHeaders.ContentSecurityPolicy,
                    Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
                foreach (var (name, value) in new[] { ("X-Content-Type-Options", "nosniff"), ("Referrer-Policy", "no-referrer"),
                    ("Cross-Origin-Opener-Policy", "same-origin"), ("Cross-Origin-Resource-Policy", "same-origin") })
                    Assert.Equal(value, Assert.Single(response.Headers.GetValues(name)));
                if (missing) Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            }
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task OnlyExistingEditorAssetsReceiveTheWorkerPolicyAndUnknownEditorPathsAreClosed()
    {
        var root = Directory.CreateTempSubdirectory("weft-csp-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "edit"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "edit", "worker.js"), "export {};");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "index.html"), "<!doctype html><title>shell</title>");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", WebRootPath = root.FullName });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            await using var app = builder.Build();
            BrowserContentPolicy.Use(app, "https://weft.example");
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapFallbackToFile("index.html");
            await app.StartAsync();
            using var client = app.GetTestClient();
            var editor = BrowserContentPolicy.Editor("https://weft.example");

            using var asset = await client.GetAsync("/edit/worker.js");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal(editor, asset.Headers.GetValues("Content-Security-Policy").Single());

            foreach (var path in new[] { "/edit/missing", "/edit", "/edit/", "/edit/nested/index.html" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal(BrowserContentPolicy.Shell, response.Headers.GetValues("Content-Security-Policy").Single());
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            }

            using var shell = await client.GetAsync("/projects/deep-link");
            Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
            Assert.Equal(BrowserContentPolicy.Shell, shell.Headers.GetValues("Content-Security-Policy").Single());
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void ShellDoesNotPermitWasmOrInlineScriptsAndWorkerIsPathConfined()
    {
        Assert.DoesNotContain("wasm-unsafe-eval", BrowserContentPolicy.Shell);
        Assert.Contains("script-src 'self';", BrowserContentPolicy.Shell);
        Assert.Contains("worker-src 'self';", BrowserContentPolicy.Shell);
        var worker = BrowserContentPolicy.Editor("https://weft.example");
        Assert.Contains("script-src https://weft.example/edit/ 'wasm-unsafe-eval';", worker);
        Assert.Contains("worker-src 'none';", worker);
        Assert.DoesNotContain("connect-src", worker); // default-src 'none' blocks every network API.
    }

    [Theory]
    [InlineData("https://weft.example/path")]
    [InlineData("https://weft.example/")]
    [InlineData("https://weft.example/?script-src=*")]
    [InlineData("https://someone@weft.example")]
    [InlineData("data:text/plain,unsafe")]
    public void EditorRefusesNonOriginPolicyInput(string origin) => Assert.Throws<ArgumentException>(() => BrowserContentPolicy.Editor(origin));
}
