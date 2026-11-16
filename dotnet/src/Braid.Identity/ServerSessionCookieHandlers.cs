using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Braid.Identity;

internal sealed class ServerSessionCookieHandler : CookieAuthenticationHandler
{
    private readonly ServerSessionBrowser browser;

    public ServerSessionCookieHandler(IOptionsMonitor<CookieAuthenticationOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, ServerSessionBrowser browser) : base(options, logger, encoder) =>
        this.browser = browser;

    protected override Task InitializeHandlerAsync()
    {
        browser.ValidateCookieConfiguration(Options, issue: false);
        return base.InitializeHandlerAsync();
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        browser.ValidateCookieConfiguration(Options, issue: false);
        return browser.AuthenticateForHandlerAsync(Context);
    }

    protected override Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties) =>
        throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);

    protected override Task HandleSignOutAsync(AuthenticationProperties? properties) =>
        throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

internal sealed class ServerSessionIssueCookieHandler : CookieAuthenticationHandler
{
    private readonly ServerSessionBrowser browser;

    public ServerSessionIssueCookieHandler(IOptionsMonitor<CookieAuthenticationOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, ServerSessionBrowser browser) : base(options, logger, encoder) =>
        this.browser = browser;

    protected override Task InitializeHandlerAsync()
    {
        browser.ValidateCookieConfiguration(Options, issue: true);
        return base.InitializeHandlerAsync();
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        browser.ValidateCookieConfiguration(Options, issue: true);
        return Task.FromResult(AuthenticateResult.NoResult());
    }

    protected override Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties)
    {
        browser.ValidateCookieConfiguration(Options, issue: true);
        browser.ConsumeIssueTransition(Context);
        return base.HandleSignInAsync(user, properties);
    }

    protected override Task HandleSignOutAsync(AuthenticationProperties? properties) =>
        throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

internal sealed class ServerSessionFixedCookieEvents : CookieAuthenticationEvents
{
    public override Task SigningIn(CookieSigningInContext context) => Task.CompletedTask;
    public override Task SignedIn(CookieSignedInContext context) => Task.CompletedTask;
    public override Task RedirectToReturnUrl(RedirectContext<CookieAuthenticationOptions> context) => Task.CompletedTask;
}
