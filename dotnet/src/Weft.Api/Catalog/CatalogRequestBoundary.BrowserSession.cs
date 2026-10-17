using Microsoft.AspNetCore.Http;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Domain;
using Weft.Identity;

namespace Weft.Api.Catalog;

public sealed partial class CatalogRequestBoundary
{
    private readonly CatalogBrowserSessionOptions? _browserOptions;
    private readonly ServerSessionBrowser? _browser;

    internal CatalogRequestBoundary(CatalogBrowserSessionOptions options, ServerSessionBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(browser);
        _browserOptions = options;
        _browser = browser;
    }

    /// <summary>Call only after fresh organization admission; no client identity or token may authorize issuance.</summary>
    public async Task<string> IssueRequestTokenAsync(HttpContext context, ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actor);
        CheckSessionCancellation(context, cancellationToken);
        if (_browser is null)
            return IssueRequestToken(context, actor);
        try
        {
            // Reuse only the browser's private observation for this exact request, never ambient claims.
            var observed = await _browser.ObserveAsync(context, cancellationToken);
            CheckSessionCancellation(context, cancellationToken);
            if (!ExpectedSessionIdentity(observed) || observed.Identity!.PrincipalId != actor.PrincipalId)
                throw new CatalogSessionBoundaryUnavailableException();
            var token = await _browser.IssueRequestTokenAsync(context, cancellationToken);
            CheckSessionCancellation(context, cancellationToken);
            if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumRequestTokenCharacters)
                throw new CatalogSessionBoundaryUnavailableException();
            return token;
        }
        catch (OperationCanceledException)
        {
            CheckSessionCancellation(context, cancellationToken);
            throw;
        }
        catch (Exception error) when (NonfatalSessionFailure(error))
        {
            CheckSessionCancellation(context, cancellationToken);
            throw new CatalogSessionBoundaryUnavailableException();
        }
    }

    private async Task<CatalogRequestAdmission> AuthenticateBrowserSessionAsync(HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            // Always use actual-method admission, including safe requests. Observe alone cannot admit a mutation.
            var observed = await _browser!.AdmitRequestAsync(context, cancellationToken);
            CheckSessionCancellation(context, cancellationToken);
            if (observed.Kind == ServerSessionRequestKind.Authenticated)
            {
                if (!ExpectedSessionIdentity(observed))
                    return SessionClosed(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
                return CatalogRequestAdmission.AcceptedSession(new ActorContext(new PrincipalId(observed.Identity!.PrincipalId.Value)),
                    _browserOptions!.OrganizationId);
            }
            return observed.Kind switch
            {
                ServerSessionRequestKind.Anonymous or ServerSessionRequestKind.AuthenticationRequired =>
                    SessionClosed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status401Unauthorized),
                ServerSessionRequestKind.CsrfRejected =>
                    SessionClosed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status403Forbidden),
                _ => SessionClosed(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable)
            };
        }
        catch (OperationCanceledException)
        {
            CheckSessionCancellation(context, cancellationToken);
            throw;
        }
        catch (Exception error) when (NonfatalSessionFailure(error))
        {
            CheckSessionCancellation(context, cancellationToken);
            return SessionClosed(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
        }
    }

    private bool ExpectedSessionIdentity(ServerSessionRequestAdmission observed) =>
        observed is { Kind: ServerSessionRequestKind.Authenticated, Identity: { } identity } &&
        identity.OrganizationId == _browserOptions!.OrganizationId && identity.PrincipalId.Value != Guid.Empty &&
        identity.LoginEpoch != Guid.Empty;

    private static CatalogRequestAdmission SessionClosed(CatalogErrorCodeV1 error, int statusCode) =>
        CatalogRequestAdmission.Closed(error, statusCode);

    private static void CheckSessionCancellation(HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.RequestAborted.ThrowIfCancellationRequested();
    }

    private static bool NonfatalSessionFailure(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}

internal sealed class CatalogSessionBoundaryUnavailableException : Exception
{
    internal CatalogSessionBoundaryUnavailableException() : base("Catalog session admission is unavailable.") { }
}
