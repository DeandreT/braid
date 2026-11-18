using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Domain;

namespace Braid.Api.Auth;

/// <summary>
/// Explicit bearer access for signed-in users on business routes, beside the session catalog. The organization is
/// the session catalog's; each bearer principal may send a bounded burst and then a fixed sustained rate.
/// </summary>
public sealed class BearerAccessOptions
{
    public const int Burst = 60;
    public const int RequestsPerSecond = 1;

    public BearerAccessOptions(Guid organizationId) : this(organizationId, Burst, RequestsPerSecond, TimeSpan.FromSeconds(1)) { }

    internal BearerAccessOptions(Guid organizationId, int burst, int tokensPerPeriod, TimeSpan period)
    {
        if (organizationId == Guid.Empty || burst < 1 || tokensPerPeriod < 1 || period <= TimeSpan.Zero)
            throw new ArgumentException("Bearer access requires an organization and a positive rate.");
        OrganizationId = organizationId; LimitBurst = burst; LimitTokensPerPeriod = tokensPerPeriod; LimitPeriod = period;
    }

    public Guid OrganizationId { get; }
    internal int LimitBurst { get; }
    internal int LimitTokensPerPeriod { get; }
    internal TimeSpan LimitPeriod { get; }
    public override string ToString() => nameof(BearerAccessOptions);
}

public static class BearerAccessRegistration
{
    /// <summary>
    /// Adds bearer admission to the session catalog registered on the same collection. The caller owns the validator,
    /// identity reader and audit writer and disposes them after the host settles; nothing here contacts a provider.
    /// </summary>
    public static IServiceCollection AddBraidBearerAccess(this IServiceCollection services, BearerAccessOptions options,
        IUserTokenValidator validator, IExternalIdentityReader identities, IApiRequestAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(audit);
        var catalogs = services.Where(item => item.ServiceType == typeof(CatalogBrowserSessionOptions)).ToArray();
        if (catalogs.Length != 1 || catalogs[0].ImplementationInstance is not CatalogBrowserSessionOptions catalog ||
            catalog.OrganizationId != options.OrganizationId || services.Any(item => item.ServiceType == typeof(CatalogBearerAdmission)))
            throw new InvalidOperationException("Bearer access requires one prior session catalog for the same organization.");
        // The container owns only the admission and its rate limiter; the borrowed ports stay with the caller.
        services.AddSingleton(_ => new CatalogBearerAdmission(options, validator, identities, audit));
        return services;
    }
}

/// <summary>
/// One bearer request: validate the delegated token, resolve the current enabled binding and principal, apply the
/// per-principal limit and, for a mutation, record the audit before it runs. No session, cookie or antiforgery state.
/// </summary>
internal sealed class CatalogBearerAdmission : IDisposable
{
    internal const string InvalidRequest = "Bearer error=\"invalid_request\"";
    internal const string InvalidToken = "Bearer error=\"invalid_token\"";
    private readonly BearerAccessOptions options;
    private readonly IUserTokenValidator validator;
    private readonly IExternalIdentityReader identities;
    private readonly IApiRequestAuditWriter audit;
    private readonly PartitionedRateLimiter<Guid> limiter;

    internal CatalogBearerAdmission(BearerAccessOptions options, IUserTokenValidator validator,
        IExternalIdentityReader identities, IApiRequestAuditWriter audit)
    {
        this.options = options; this.validator = validator; this.identities = identities; this.audit = audit;
        limiter = PartitionedRateLimiter.Create<Guid, Guid>(principal => RateLimitPartition.GetTokenBucketLimiter(principal,
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = options.LimitBurst,
                TokensPerPeriod = options.LimitTokensPerPeriod,
                ReplenishmentPeriod = options.LimitPeriod,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }

    internal async Task<CatalogRequestAdmission> AdmitAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        // Bearer is a business-route credential only, and never shares a request with cookies.
        if (context.GetEndpoint() is not { } endpoint || !ApiSecurity.IsBusiness(endpoint.Metadata))
            return CatalogRequestAdmission.Closed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status401Unauthorized);
        if (!TryReadToken(request, out var token) || request.Headers.Cookie.Count != 0)
            return Refuse(context, CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest, InvalidRequest);
        UserCaller caller;
        try { caller = await validator.ValidateAsync(token, cancellationToken); }
        catch (UserTokenException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return error.Failure == UserTokenFailure.Unavailable ? Unavailable()
                : Refuse(context, CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status401Unauthorized, InvalidToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var resolution = await identities.ResolveAsync(caller.Identity, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (resolution.Outcome != ExternalIdentityResolutionOutcome.Resolved)
            return CatalogRequestAdmission.Closed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status403Forbidden);
        if (resolution.Binding is not { Enabled: true } binding || binding.Key != caller.Identity ||
            binding.BindingId == Guid.Empty || binding.PrincipalId.Value == Guid.Empty)
            return Unavailable();
        var principal = new PrincipalId(binding.PrincipalId.Value);
        using (var lease = limiter.AttemptAcquire(principal.Value))
        {
            if (!lease.IsAcquired)
            {
                var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? Math.Ceiling(after.TotalSeconds) : 1;
                context.Response.Headers.RetryAfter = Math.Clamp(seconds, 1, 86_400).ToString(CultureInfo.InvariantCulture);
                return CatalogRequestAdmission.Closed(CatalogErrorCodeV1.RateLimited, StatusCodes.Status429TooManyRequests);
            }
        }
        Guid? selected;
        if (context.Request.RouteValues.TryGetValue("organizationId", out var raw))
        {
            if (raw is not string text || !Guid.TryParseExact(text, "D", out var parsed) || parsed == Guid.Empty || text != parsed.ToString("D"))
                return Refuse(context, CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest, InvalidRequest);
            selected = parsed;
        }
        else selected = options.OrganizationId;
        var reader = context.RequestServices.GetService<ICatalogReader>();
        var scopeAdmission = reader is null ? null
            : await reader.GetOrganizationAdmissionAsync(new ActorContext(principal), selected.Value, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (scopeAdmission is null)
            return CatalogRequestAdmission.Closed(CatalogErrorCodeV1.NotFound, StatusCodes.Status404NotFound);
        if (scopeAdmission.OrganizationId != selected || scopeAdmission.PrincipalId != principal) return Unavailable();
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            // A mutation runs only after its audit is durable; a failed write refuses rather than runs unaudited.
            if (endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is not { } operation ||
                !ApiRequestAudit.IsOperation(operation) || request.Method is not ("POST" or "PUT" or "PATCH" or "DELETE"))
                return Unavailable();
            var resources = request.RouteValues
                .Where(pair => pair.Value is string value && ApiRequestAudit.IsResourceName(pair.Key) && ApiRequestAudit.IsResourceValue(value))
                .Select(pair => new KeyValuePair<string, string>(pair.Key, (string)pair.Value!))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(ApiRequestAudit.MaximumResources).ToArray();
            var entry = new ApiRequestAudit(Guid.NewGuid(), selected.Value, principal, binding.BindingId,
                caller.ClientApplicationId, operation, request.Method, resources);
            if (await audit.RecordAsync(entry, cancellationToken) != ApiRequestAuditOutcome.Recorded)
                return CatalogRequestAdmission.Closed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status403Forbidden);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return CatalogRequestAdmission.AcceptedBearer(new ActorContext(principal), selected.Value,
            scopeAdmission.AdmissionVersion, binding.BindingId, binding.Version);
    }

    /// <summary>Identity-only bearer admission: valid token, enabled binding and the shared rate limit.</summary>
    internal async Task<CatalogRequestAdmission> AdmitIdentityAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) ||
            context.GetEndpoint() is not { } endpoint || !ApiSecurity.IsIdentityRead(endpoint.Metadata))
            return CatalogRequestAdmission.Closed(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
        if (!TryReadToken(request, out var token) || request.Headers.Cookie.Count != 0)
            return Refuse(context, CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest, InvalidRequest);
        UserCaller caller;
        try { caller = await validator.ValidateAsync(token, cancellationToken); }
        catch (UserTokenException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return error.Failure == UserTokenFailure.Unavailable ? Unavailable()
                : Refuse(context, CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status401Unauthorized, InvalidToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var resolution = await identities.ResolveAsync(caller.Identity, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (resolution.Outcome != ExternalIdentityResolutionOutcome.Resolved)
            return CatalogRequestAdmission.Closed(CatalogErrorCodeV1.IdentityRejected, StatusCodes.Status403Forbidden);
        if (resolution.Binding is not { Enabled: true } binding || binding.Key != caller.Identity ||
            binding.BindingId == Guid.Empty || binding.PrincipalId.Value == Guid.Empty)
            return Unavailable();
        var principal = new PrincipalId(binding.PrincipalId.Value);
        using (var lease = limiter.AttemptAcquire(principal.Value))
        {
            if (!lease.IsAcquired)
            {
                var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? Math.Ceiling(after.TotalSeconds) : 1;
                context.Response.Headers.RetryAfter = Math.Clamp(seconds, 1, 86_400).ToString(CultureInfo.InvariantCulture);
                return CatalogRequestAdmission.Closed(CatalogErrorCodeV1.RateLimited, StatusCodes.Status429TooManyRequests);
            }
        }
        return CatalogRequestAdmission.AcceptedBearer(new ActorContext(principal), null, 0, binding.BindingId, binding.Version);
    }

    // Exactly one "Bearer <token>" value. The validator admits only compact JWS characters.
    private static bool TryReadToken(HttpRequest request, out string token)
    {
        token = "";
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { Length: > 7 } value || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;
        token = value[7..];
        return token.Length != 0;
    }

    private static CatalogRequestAdmission Refuse(HttpContext context, CatalogErrorCodeV1 error, int status, string challenge)
    {
        context.Response.Headers.WWWAuthenticate = challenge;
        return CatalogRequestAdmission.Closed(error, status);
    }

    private static CatalogRequestAdmission Unavailable() =>
        CatalogRequestAdmission.Closed(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);

    public void Dispose() => limiter.Dispose();
    public override string ToString() => nameof(CatalogBearerAdmission);
}
