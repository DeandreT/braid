using System.Net;
using System.Runtime.Versioning;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
internal static class ServerHostBrowserSecurity
{
    // Exercise the actual published host, not a test-only middleware pipeline.
    internal static async Task CheckAsync(HttpClient client)
    {
        var assembly = Environment.GetEnvironmentVariable("WEFT_HOST_TEST_SERVER_PATH");
        ServerHostCheck.That(assembly is not null, "The published browser host was not supplied.");
        var root = Path.Combine(Path.GetDirectoryName(assembly!)!, "wwwroot");
        ServerHostCheck.That(File.Exists(Path.Combine(root, "index.html")), "The published app shell was missing.");
        var origin = client.BaseAddress?.GetLeftPart(UriPartial.Authority);
        ServerHostCheck.That(origin is not null, "The published host origin was missing.");
        var editorPolicy = $"default-src 'none'; script-src {origin}/edit/ 'wasm-unsafe-eval'; worker-src 'none'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => "/" + string.Join("/", Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
                .Select(Uri.EscapeDataString)))
            .Concat(["/", "/catalog/browser-security-deep-link"]);
        foreach (var path in paths)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            ServerHostCheck.That(response.StatusCode == HttpStatusCode.OK, "A published browser resource was not served.");
            foreach (var (name, value) in new[] {
                ("Content-Security-Policy", path.StartsWith("/edit/", StringComparison.Ordinal) ? editorPolicy : Weft.Api.BrowserSecurityHeaders.ContentSecurityPolicy),
                ("X-Content-Type-Options", "nosniff"), ("Referrer-Policy", "no-referrer"),
                ("Cross-Origin-Opener-Policy", "same-origin"), ("Cross-Origin-Resource-Policy", "same-origin") })
                ServerHostCheck.That(response.Headers.TryGetValues(name, out var values) && values.SequenceEqual([value]),
                    "A published browser resource was missing its security policy.");
            // Static bundles are larger than the auth fixture's small private-JSON
            // limit. Drain them without retaining or printing their contents.
            await response.Content.CopyToAsync(Stream.Null, deadline.Token);
        }
        foreach (var path in new[] { "/edit/", "/edit/not-shipped.js" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            ServerHostCheck.That(response.StatusCode == HttpStatusCode.NotFound, "An absent editor asset was not refused.");
            ServerHostCheck.That(response.Headers.TryGetValues("Content-Security-Policy", out var policies)
                && policies.SequenceEqual([Weft.Api.BrowserSecurityHeaders.ContentSecurityPolicy]), "An absent editor asset received a worker policy.");
            ServerHostCheck.That((await response.Content.ReadAsByteArrayAsync(deadline.Token)).Length == 0, "An absent editor asset returned a body.");
        }
    }
}
