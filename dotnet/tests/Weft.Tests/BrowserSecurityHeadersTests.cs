using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Weft.Api;
using Weft.Api.Auth;

namespace Weft.Tests;

public sealed class BrowserSecurityHeadersTests
{
    // Independent expected value: changing the implementation alone must fail.
    private const string ExpectedPolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'self'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    [Fact]
    public async Task ShellDeepLinksAndEveryStaticFileCarryThePolicyIncludingHeadAndNotModified()
    {
        var root = Path.Combine(Path.GetTempPath(), "weft-browser-headers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var assets = new Dictionary<string, string>
            {
                ["index.html"] = "<!doctype html><title>Owned shell</title>",
                ["main.js"] = "export const owned = true;",
                ["styles.css"] = "body { color: black; }",
                ["icon.svg"] = "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>",
                ["font.woff2"] = "owned font bytes"
            };
            foreach (var (file, body) in assets)
                await File.WriteAllTextAsync(Path.Combine(root, file), body);
            using var files = new PhysicalFileProvider(root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            app.UseWeftBrowserSecurityHeaders();
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            app.Run(async context =>
            {
                context.Response.Headers["X-Weft-Test-Fallback"] = "1";
                context.Response.ContentType = "text/html";
                await context.Response.SendFileAsync(Path.Combine(root, "index.html"));
            });
            await app.StartAsync();
            using var client = app.GetTestClient();
            foreach (var path in new[] { "/", "/index.html", "/catalog/deep/link", "/main.js", "/styles.css", "/icon.svg", "/font.woff2" })
            {
                using var get = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, get.StatusCode);
                AssertHeaders(get);
                bool fallback = path == "/catalog/deep/link";
                string file = path == "/" || fallback ? "index.html" : path[1..];
                Assert.Equal(assets[file], await get.Content.ReadAsStringAsync());
                Assert.Equal(fallback, get.Headers.Contains("X-Weft-Test-Fallback"));
                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
                Assert.Equal(fallback, head.Headers.Contains("X-Weft-Test-Fallback"));
                AssertHeaders(head);
                if (!fallback)
                {
                    var tag = get.Headers.ETag;
                    Assert.NotNull(tag);
                    Assert.Equal(tag, head.Headers.ETag);
                    Assert.NotNull(get.Content.Headers.LastModified);
                    Assert.Empty(await head.Content.ReadAsByteArrayAsync());
                    using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
                    conditional.Headers.IfNoneMatch.Add(tag);
                    using var notModified = await client.SendAsync(conditional);
                    Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
                    Assert.False(notModified.Headers.Contains("X-Weft-Test-Fallback"));
                    Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());
                    AssertHeaders(notModified);
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ActualUnavailableAuthenticationCallbackKeepsItsStrictCspAndNoStoreWithSharedHeaders()
    {
        // Deliberately unavailable dependency: execute the real callback mapper
        // and failure handler without a provider, credentials, database or listener.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton(new AuthenticationHttpOptions(AuthenticationHttpMode.Session, "https://weft.test"));
        await using var app = builder.Build();
        app.UseWeftBrowserSecurityHeaders();
        app.MapWeftAuthentication();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/auth/callback/entra?code=private-test-code&state=private-test-state");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertHeaders(response, "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'");
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains(response.Headers.Pragma, item => item.Name == "no-cache");
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
        Assert.Null(response.Headers.Location);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sign-in is temporarily unavailable", body);
        Assert.DoesNotContain("private-test-", body);
    }

    [Fact]
    public async Task RefusalsAndClearedErrorsKeepHeadersWithoutRelaxingAuthDocumentPolicy()
    {
        const string authPolicy = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseWeftBrowserSecurityHeaders();
        app.Run(context =>
        {
            context.Response.Clear();
            context.Response.StatusCode = 403;
            if (context.Request.Path == "/auth")
                context.Response.Headers.ContentSecurityPolicy = authPolicy;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var refusal = await client.GetAsync("/refused");
        AssertHeaders(refusal);
        using var auth = await client.GetAsync("/auth");
        AssertHeaders(auth, authPolicy);
    }

    private static void AssertHeaders(HttpResponseMessage response, string csp = ExpectedPolicy)
    {
        foreach (var (name, value) in new[] {
            ("Content-Security-Policy", csp), ("X-Content-Type-Options", "nosniff"),
            ("Referrer-Policy", "no-referrer"), ("Cross-Origin-Opener-Policy", "same-origin"),
            ("Cross-Origin-Resource-Policy", "same-origin") })
            Assert.Equal(value, Assert.Single(response.Headers.GetValues(name)));
    }
}
