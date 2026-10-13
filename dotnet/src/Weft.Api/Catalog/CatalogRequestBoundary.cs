using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

/// <summary>Transport admission only. Every resource operation must subsequently read fresh authority.</summary>
public sealed record CatalogRequestAdmission
{
    private CatalogRequestAdmission(ActorContext? actor, Guid? organizationId,
        CatalogErrorCodeV1? error, int statusCode)
    {
        Actor = actor;
        AllowedOrganizationId = organizationId;
        Error = error;
        StatusCode = statusCode;
    }

    public bool IsAdmitted => Actor is not null;
    public ActorContext? Actor { get; }
    public Guid? AllowedOrganizationId { get; }
    public CatalogErrorCodeV1? Error { get; }
    public int StatusCode { get; }
    internal static CatalogRequestAdmission Accepted(ActorContext actor, Guid organizationId) =>
        new(actor, organizationId, null, StatusCodes.Status200OK);
    internal static CatalogRequestAdmission Closed(CatalogErrorCodeV1 error, int statusCode) =>
        new(null, null, error, statusCode);
}

/// <summary>Trusted local identity plus reusable Origin/framework-token protection before unsafe body reads.</summary>
public sealed class CatalogRequestBoundary
{
    public const string RequestTokenHeader = "X-XSRF-TOKEN";
    private const int MaximumRequestTokenCharacters = 4096;
    private readonly CatalogApiOptions _options;
    private readonly IAntiforgery _antiforgery;

    public CatalogRequestBoundary(CatalogApiOptions options, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(antiforgery);
        _options = options;
        _antiforgery = antiforgery;
    }

    public async Task<CatalogRequestAdmission> AuthenticateAsync(HttpContext context,
        bool unsafeRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.Response.Headers.CacheControl = "no-store";
        if (!_options.Enabled)
            return CatalogRequestAdmission.Closed(CatalogErrorCodeV1.Unavailable, StatusCodes.Status404NotFound);
        if (!ValidTransport(context))
            return Rejected();
        var actor = new ActorContext(new PrincipalId(_options.PrincipalId!.Value));
        if (unsafeRequest)
        {
            var origins = context.Request.Headers.Origin;
            var requestTokens = context.Request.Headers[RequestTokenHeader];
            if (origins.Count != 1 || !string.Equals(origins[0], _options.BrowserOrigin, StringComparison.Ordinal) ||
                requestTokens.Count != 1 || string.IsNullOrWhiteSpace(requestTokens[0]) ||
                requestTokens[0]!.Length > MaximumRequestTokenCharacters)
                return Rejected();
            SetTrustedPrincipal(context);
            try
            {
                // The required header prevents framework fallback to reading a form body.
                await _antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Rejected();
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        return CatalogRequestAdmission.Accepted(actor, _options.OrganizationId!.Value);
    }

    /// <summary>Call only after fresh organization admission; no identity or token is accepted from a request.</summary>
    public string IssueRequestToken(HttpContext context, ActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actor);
        if (!_options.Enabled || !ValidTransport(context) || actor.PrincipalId.Value != _options.PrincipalId)
            throw new InvalidOperationException("Catalog request token issuance requires the configured trusted identity.");
        SetTrustedPrincipal(context);
        return _antiforgery.GetAndStoreTokens(context).RequestToken ??
            throw new InvalidOperationException("The catalog request token could not be issued.");
    }

    private bool ValidTransport(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(peer) &&
        string.Equals(context.Request.Scheme, "http", StringComparison.Ordinal) &&
        string.Equals(context.Request.Host.Value, _options.Authority, StringComparison.Ordinal);

    private void SetTrustedPrincipal(HttpContext context)
    {
        // Include organization in the framework's unique-user claim to bind tokens to this configuration.
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, _options.OrganizationId!.Value.ToString("D") + ":" +
                _options.PrincipalId!.Value.ToString("D"))
        ], "Weft.DevelopmentCatalog");
        context.User = new ClaimsPrincipal(identity);
    }

    private static CatalogRequestAdmission Rejected() =>
        CatalogRequestAdmission.Closed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status403Forbidden);
}
