using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Auth;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Domain;
using Braid.Identity;

namespace Braid.Api.Catalog;

public sealed partial class CatalogRequestBoundary
{
    private readonly CatalogBrowserSessionOptions? _browserOptions;
    private readonly ServerSessionBrowser? _browser;
    private readonly CatalogBearerAdmission? _bearer;

    internal CatalogRequestBoundary(CatalogBrowserSessionOptions options, ServerSessionBrowser browser,
        CatalogBearerAdmission? bearer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(browser);
        _browserOptions = options;
        _browser = browser;
        _bearer = bearer;
    }

    /// <summary>Identity-only admission for discovery: who is calling, with no business scope.</summary>
    public async Task<CatalogRequestAdmission> AuthenticateIdentityAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (_browser is null)
            {
                if (_options is not { Enabled: true } options)
                    return SessionClosed(CatalogErrorCodeV1.Unavailable, StatusCodes.Status404NotFound);
                if (!ValidTransport(context) ||
                    context.Request.Headers.Authorization.Count != 0)
                    return SessionClosed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status403Forbidden);
                return CatalogRequestAdmission.Accepted(new ActorContext(new PrincipalId(options.PrincipalId!.Value)),
                    options.OrganizationId!.Value);
            }
            if (context.Request.Headers.Authorization.Count != 0)
                return _bearer is { } bearer ? await bearer.AdmitIdentityAsync(context, cancellationToken)
                    : SessionClosed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status401Unauthorized);
            var observed = await _browser.ObserveAsync(context, cancellationToken);
            CheckSessionCancellation(context, cancellationToken);
            if (observed.Kind != ServerSessionRequestKind.Authenticated)
                return observed.Kind switch
                {
                    ServerSessionRequestKind.Anonymous or ServerSessionRequestKind.AuthenticationRequired =>
                        SessionClosed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status401Unauthorized),
                    ServerSessionRequestKind.CsrfRejected =>
                        SessionClosed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status403Forbidden),
                    _ => SessionClosed(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable)
                };
            if (!ExpectedSessionIdentity(observed))
                return SessionClosed(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
            var binding = await _browser.ReadCurrentBindingAsync(context, observed.Identity!, cancellationToken);
            CheckSessionCancellation(context, cancellationToken);
            return CatalogRequestAdmission.AcceptedSession(new ActorContext(new PrincipalId(observed.Identity!.PrincipalId.Value)),
                null, observed.Identity, 0, binding.BindingId, binding.BindingVersion);
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
            var scope = context.Features.Get<ServerSessionBusinessScope>();
            if (scope is null || scope.PrincipalId != observed.Identity.PrincipalId.Value ||
                scope.LoginEpoch != observed.Identity.LoginEpoch)
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
            var observed = await _browser!.ObserveAsync(context, cancellationToken);
            CheckSessionCancellation(context, cancellationToken);
            if (observed.Kind != ServerSessionRequestKind.Authenticated)
                return observed.Kind switch
                {
                    ServerSessionRequestKind.Anonymous or ServerSessionRequestKind.AuthenticationRequired =>
                        SessionClosed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status401Unauthorized),
                    ServerSessionRequestKind.CsrfRejected =>
                        SessionClosed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status403Forbidden),
                    _ => SessionClosed(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable)
                };
            if (!ExpectedSessionIdentity(observed))
                return SessionClosed(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
            var scope = ResolveScope(context);
            if (scope is null)
                return SessionClosed(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
            var actor = new ActorContext(new PrincipalId(observed.Identity!.PrincipalId.Value));
            var admission = await ReadAdmissionAsync(context, actor, scope.Value, cancellationToken);
            if (admission is null)
                return SessionClosed(CatalogErrorCodeV1.NotFound, StatusCodes.Status404NotFound);
            ServerSessionBusinessScope.Bind(context, scope.Value, admission.AdmissionVersion, observed.Identity);
            // Actual-method admission, including safe requests. Observe alone cannot admit a mutation.
            var admitted = await _browser.AdmitRequestAsync(context, cancellationToken);
            CheckSessionCancellation(context, cancellationToken);
            if (admitted.Kind == ServerSessionRequestKind.Authenticated && admitted.Identity is { } actual &&
                SameIdentity(observed.Identity, actual))
                return CatalogRequestAdmission.AcceptedSession(actor, scope.Value, actual, admission.AdmissionVersion);
            return admitted.Kind switch
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

    private async Task<CatalogRequestAdmission> AuthenticateBearerAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (_bearer is null)
            return SessionClosed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status401Unauthorized);
        try
        {
            var admission = await _bearer.AdmitAsync(context, cancellationToken);
            CheckSessionCancellation(context, cancellationToken);
            return admission;
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

    /// <summary>The route-selected organization, or the root organization on an explicit root-only mapping.</summary>
    private Guid? ResolveScope(HttpContext context) =>
        RouteScope(context) is { } route
            ? route == Guid.Empty ? null : route
            : _browserOptions!.OrganizationId;

    /// <summary>Fresh business admission for the selected scope; discovery never substitutes for it.</summary>
    private static async Task<CatalogOrganizationAdmission?> ReadAdmissionAsync(HttpContext context, ActorContext actor,
        Guid organizationId, CancellationToken cancellationToken)
    {
        var reader = context.RequestServices.GetService<ICatalogReader>() ??
            throw new CatalogSessionBoundaryUnavailableException();
        var admission = await reader.GetOrganizationAdmissionAsync(actor, organizationId, cancellationToken);
        if (admission is not null && (admission.OrganizationId != organizationId || admission.PrincipalId != actor.PrincipalId))
            throw new CatalogSessionBoundaryUnavailableException();
        return admission;
    }

    private bool ExpectedSessionIdentity(ServerSessionRequestAdmission observed) =>
        observed is { Kind: ServerSessionRequestKind.Authenticated, Identity: { } identity } &&
        identity.OrganizationId == _browserOptions!.OrganizationId && identity.PrincipalId.Value != Guid.Empty &&
        identity.LoginEpoch != Guid.Empty;

    private static bool SameIdentity(ServerSessionRequestIdentity? left, ServerSessionRequestIdentity? right) =>
        left is not null && right is not null && left.OrganizationId == right.OrganizationId &&
        left.PrincipalId == right.PrincipalId && left.LoginEpoch == right.LoginEpoch;

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
