using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.Api.Catalog;
using Weft.Contracts.Auth;
using Weft.Identity;

namespace Weft.Api.Auth;

public static class AuthenticationEndpoints
{
    public static IServiceCollection AddWeftAuthenticationHttp(this IServiceCollection services,
        AuthenticationHttpOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        if (services.Any(item => item.ServiceType == typeof(AuthenticationHttpOptions)))
            throw new InvalidOperationException("Authentication HTTP must be registered exactly once.");
        if (options.Mode == AuthenticationHttpMode.Session)
        {
            var owners = services.Where(item => item.ServiceType == typeof(EntraBrowserAuthentication)).ToArray();
            var profiles = services.Where(item => item.ServiceType == typeof(BrowserAuthenticationOptions)).ToArray();
            if (owners.Length != 1 || owners[0].IsKeyedService || owners[0].Lifetime != ServiceLifetime.Singleton ||
                profiles.Length != 1 || profiles[0].IsKeyedService ||
                profiles[0].ImplementationInstance is not BrowserAuthenticationOptions profile ||
                profile.ExpectedOrigin != options.ExpectedOrigin ||
                services.Any(item => item.ServiceType == typeof(CatalogApiOptions)))
                throw new InvalidOperationException("Session HTTP requires its one explicit session composition.");
        }
        else
        {
            if (services.Any(item => item.ServiceType == typeof(EntraBrowserAuthentication) ||
                item.ServiceType == typeof(EntraBrowserClient) || item.ServiceType == typeof(ServerSessionBrowser) ||
                item.ServiceType == typeof(BrowserAuthenticationOptions) ||
                item.ServiceType == typeof(CatalogBrowserSessionOptions)))
                throw new InvalidOperationException("A non-session HTTP profile cannot mix session services.");
            var catalog = services.Where(item => item.ServiceType == typeof(CatalogApiOptions)).ToArray();
            if (options.Mode == AuthenticationHttpMode.Development)
            {
                if (catalog.Length != 1 || catalog[0].IsKeyedService ||
                    catalog[0].ImplementationInstance is not CatalogApiOptions development || !development.Enabled ||
                    development.BrowserOrigin != options.ExpectedOrigin)
                    throw new InvalidOperationException("Development status requires the existing Development catalog.");
                development.Validate(environment);
            }
            else if (catalog.Length > 1 || catalog.Length == 1 &&
                (catalog[0].IsKeyedService || catalog[0].ImplementationInstance is not CatalogApiOptions { Enabled: false }))
                throw new InvalidOperationException("Disabled status cannot enable a catalog identity.");
        }
        services.AddSingleton(options);
        return services;
    }

    public static IEndpointRouteBuilder MapWeftAuthentication(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<AuthenticationHttpOptions>();
        Map("/api/auth/session", "GET", "bootstrap");
        if (options.Mode == AuthenticationHttpMode.Session)
        {
            Map("/api/auth/login", "POST", "login");
            Map("/api/auth/callback/entra", "GET", "callback");
            Map("/api/auth/logout", "POST", "logout");
            Map("/api/auth/forget-local", "POST", "forget-local");
        }
        return endpoints;

        void Map(string path, string method, string action)
        {
            // One route handles every verb so even method refusal gets the fixed privacy headers.
            // The fixed supported operation is described explicitly by AuthenticationOpenApi.
            endpoints.Map(path, (HttpContext context) => HandleAsync(context, options, method, action))
                .ExcludeFromDescription();
        }
    }

    private static async Task HandleAsync(HttpContext context, AuthenticationHttpOptions options,
        string method, string action)
    {
        Privacy(context);
        try
        {
            if (context.Request.Method != method)
            {
                context.Response.Headers.Allow = method;
                await Results.Json(new AuthErrorV1(1, "invalidRequest"), statusCode: 405).ExecuteAsync(context);
                return;
            }
            if (options.Mode != AuthenticationHttpMode.Session)
            {
                var status = RequireModeRequest(context, options);
                if (status is not null)
                {
                    await Results.Json(new AuthErrorV1(1, status.Value.Code), statusCode: status.Value.Status).ExecuteAsync(context);
                    return;
                }
                await Results.Json(new AuthModeStatusV1(1,
                    options.Mode == AuthenticationHttpMode.Development ? "development" : "disabled")).ExecuteAsync(context);
                return;
            }
            var owner = context.RequestServices.GetRequiredService<EntraBrowserAuthentication>();
            if (action == "bootstrap")
            {
                var value = await owner.ReadSessionAsync(context, context.RequestAborted);
                var state = value.State switch
                {
                    BrowserAuthenticationState.Anonymous => "anonymous",
                    BrowserAuthenticationState.AuthenticationRequired => "authenticationRequired",
                    BrowserAuthenticationState.Authenticated => "authenticated",
                    _ => throw new InvalidOperationException("The authentication status is invalid.")
                };
                await Results.Json(new AuthSessionV1(1, "session", state, value.AuthActionRequestToken,
                    value.RequestToken, value.IdentityContext)).ExecuteAsync(context);
            }
            else if (action == "login")
            {
                var challenge = await owner.BeginLoginAsync(context, context.RequestAborted);
                await Results.Json(new AuthLoginV1(1, challenge.AuthorizationUrl)).ExecuteAsync(context);
            }
            else if (action == "callback")
            {
                await owner.CompleteLoginAsync(context, context.RequestAborted);
                context.RequestAborted.ThrowIfCancellationRequested();
                if (context.Response.HasStarted) throw new InvalidOperationException("Authentication response already started.");
                context.Response.StatusCode = StatusCodes.Status303SeeOther;
                context.Response.Headers.Location = "/";
                context.Response.ContentLength = 0;
            }
            else if (action == "logout")
            {
                await owner.LogoutAsync(context, context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }
            else
            {
                var outcome = await owner.ForgetLocalAsync(context, context.RequestAborted);
                if (outcome != BrowserForgetLocalOutcome.RevocationUnconfirmed)
                    throw new InvalidOperationException("The local forgetting outcome is invalid.");
                await Results.Json(new AuthForgetLocalV1(1, "revocationUnconfirmed")).ExecuteAsync(context);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            if (context.Response.HasStarted) throw;
            context.Response.Headers.Remove("Location");
            var category = error is BrowserAuthenticationException closed ? closed.Kind : BrowserAuthenticationFailureKind.Unavailable;
            var (code, status) = category switch
            {
                BrowserAuthenticationFailureKind.InvalidRequest => ("invalidRequest", 400),
                BrowserAuthenticationFailureKind.RequestRejected => ("requestRejected", 403),
                BrowserAuthenticationFailureKind.AuthenticationRequired => ("authenticationRequired", 401),
                BrowserAuthenticationFailureKind.ForgetNotAvailable => ("forgetNotAvailable", 409),
                _ => ("unavailable", 503)
            };
            if (action == "callback")
            {
                status = category is BrowserAuthenticationFailureKind.InvalidRequest or
                    BrowserAuthenticationFailureKind.RequestRejected or BrowserAuthenticationFailureKind.AuthenticationRequired ? 401 : 503;
                context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
                var heading = status == 503 ? "Sign-in is temporarily unavailable" : "Sign-in could not be completed";
                await Results.Content("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Weft sign-in</title>" +
                    "<h1>" + heading + "</h1><p><a href=\"/\">Return to Weft</a></p></html>",
                    "text/html; charset=utf-8", statusCode: status).ExecuteAsync(context);
            }
            else await Results.Json(new AuthErrorV1(1, code), statusCode: status).ExecuteAsync(context);
        }
    }

    private static (string Code, int Status)? RequireModeRequest(HttpContext context, AuthenticationHttpOptions options)
    {
        if (!options.ValidTransport(context)) return ("requestRejected", 403);
        if (context.Request.QueryString.HasValue || context.Request.Headers.Authorization.Count != 0 ||
            context.Request.Headers["X-Weft-Session"].Count != 1 || context.Request.Headers["X-Weft-Session"][0] != "1")
            return ("invalidRequest", 400);
        if (new[] { "X-Weft-Login", "X-Weft-Logout", "X-Weft-Forget-Local" }
            .Any(name => context.Request.Headers[name].Count != 0)) return ("invalidRequest", 400);
        var site = context.Request.Headers["Sec-Fetch-Site"];
        if (site.Count > 1 || site.Count == 1 && site[0] == "cross-site") return ("requestRejected", 403);
        var origin = context.Request.Headers.Origin;
        if (origin.Count != 0 && (origin.Count != 1 || origin[0] != options.ExpectedOrigin)) return ("requestRejected", 403);
        if (context.Request.Headers.ContentType.Count != 0 || context.Request.Headers.TransferEncoding.Count != 0 ||
            context.Request.ContentLength is > 0 || context.Request.Headers["Content-Length"].Count > 1)
            return ("invalidRequest", 400);
        var body = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>();
        if (body is null) return ("unavailable", 503);
        return body.CanHaveBody ? ("invalidRequest", 400) : null;
    }

    private static void Privacy(HttpContext context)
    {
        Apply();
        context.Response.OnStarting(() => { Apply(); return Task.CompletedTask; });
        void Apply()
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
        }
    }
}
