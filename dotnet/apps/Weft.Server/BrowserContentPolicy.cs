namespace Weft.Server;

/// <summary>The shell cannot compile wasm. Only the path-confined dedicated editor worker can.</summary>
internal static class BrowserContentPolicy
{
    internal const string Shell = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'self'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    internal static string Editor(string publicOrigin)
    {
        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || publicOrigin != uri.GetLeftPart(UriPartial.Authority))
            throw new ArgumentException("The editor policy requires a canonical public origin.", nameof(publicOrigin));
        return $"default-src 'none'; script-src {publicOrigin}/edit/ 'wasm-unsafe-eval'; worker-src 'none'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    }

    internal static void Use(IApplicationBuilder app, string publicOrigin)
    {
        var editor = Editor(publicOrigin);
        app.Use(async (context, next) =>
        {
            // API and authentication endpoints have their own response policies.
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Headers.ContentSecurityPolicy = context.Request.Path.StartsWithSegments("/edit") ? editor : Shell;
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
            }
            await next(context);
        });
    }
}
