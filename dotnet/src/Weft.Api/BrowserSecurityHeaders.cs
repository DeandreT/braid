using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Weft.Api;

/// <summary>The browser boundary shared by the Development and Session hosts.</summary>
public static class BrowserSecurityHeaders
{
    public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'self'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    // Register before transport gates, static files and endpoint dispatch. A dedicated
    // endpoint (notably the inert auth failure document) may select a stricter CSP.
    public static IApplicationBuilder UseWeftBrowserSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            Apply(context.Response.Headers);
            // OnStarting runs in reverse order. Restore headers even after an inner
            // exception handler clears the response, without relaxing endpoint CSPs.
            context.Response.OnStarting(() =>
            {
                Apply(context.Response.Headers);
                return Task.CompletedTask;
            });
            await next(context);
        });

    private static void Apply(IHeaderDictionary headers)
    {
        if (!headers.ContainsKey("Content-Security-Policy"))
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }
}
