using System.Net;
using System.Runtime.Versioning;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
internal static class ServerHostBrowserSecurity
{
    // Exercise the actual published host, not a test-only middleware pipeline.
    internal static async Task CheckAsync(HttpClient client, string? publicOrigin = null)
    {
        var assembly = Environment.GetEnvironmentVariable("WEFT_HOST_TEST_SERVER_PATH");
        ServerHostCheck.That(assembly is not null, "The published browser host was not supplied.");
        var root = Path.Combine(Path.GetDirectoryName(assembly!)!, "wwwroot");
        ServerHostCheck.That(File.Exists(Path.Combine(root, "index.html")), "The published app shell was missing.");
        var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => "/" + string.Join("/", Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
                .Select(Uri.EscapeDataString)))
            .Concat(["/", "/catalog/browser-security-deep-link"]);
        foreach (var path in paths)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            ServerHostCheck.That(response.StatusCode == HttpStatusCode.OK, "A published browser resource was not served.");
            var policy = path.StartsWith("/edit/", StringComparison.Ordinal)
                ? Weft.Server.BrowserContentPolicy.Editor(publicOrigin ?? client.BaseAddress!.GetLeftPart(UriPartial.Authority))
                : Weft.Api.BrowserSecurityHeaders.ContentSecurityPolicy;
            foreach (var (name, value) in new[] {
                ("Content-Security-Policy", policy),
                ("X-Content-Type-Options", "nosniff"), ("Referrer-Policy", "no-referrer"),
                ("Cross-Origin-Opener-Policy", "same-origin"), ("Cross-Origin-Resource-Policy", "same-origin") })
                ServerHostCheck.That(response.Headers.TryGetValues(name, out var values) && values.SequenceEqual([value]),
                    "A published browser resource was missing its security policy.");
            // Static bundles are larger than the auth fixture's small private-JSON
            // limit. Drain them without retaining or printing their contents.
            await response.Content.CopyToAsync(Stream.Null, deadline.Token);
        }
    }
}
