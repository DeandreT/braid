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
        var files = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;
        app.Use(async (context, next) =>
        {
            // API and authentication endpoints have their own response policies.
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                var path = context.Request.Path;
                var editPath = path.StartsWithSegments("/edit");
                // Only shipped worker assets get the wasm policy; the shell fallback must never serve HTML under it.
                var asset = editPath && files.GetFileInfo(path.Value!) is { Exists: true, IsDirectory: false };
                context.Response.Headers.ContentSecurityPolicy = asset ? editor : Shell;
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                if (editPath && !asset)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }
            await next(context);
        });
    }
}
