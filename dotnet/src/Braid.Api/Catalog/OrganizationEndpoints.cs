using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Auth;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Contracts.Organizations;

namespace Braid.Api.Catalog;

/// <summary>Identity, admission discovery and qualified context for request-local organization selection.</summary>
public static class OrganizationEndpoints
{
    private const int MaximumCursorCharacters = 512;
    private const int MaximumPageBytes = 128 * 1024;

    public static IEndpointRouteBuilder MapBraidOrganizations(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var identity = endpoints.IdentityRoutes();
        Describe<IdentityV1>(identity.MapGet("/api/v1/identity", GetIdentity), "GetIdentity",
            "Read the authenticated caller's identity without any business scope.");
        Describe<OrganizationPageV1>(identity.MapGet("/api/v1/organizations", ListOrganizations), "ListOrganizations",
            "Read a bounded canonical-order page of the caller's currently active organization admissions.");
        var business = endpoints.BusinessRoutes();
        Describe<OrganizationContextV1>(business.MapGet("/api/v1/organizations/{organizationId}/context", GetContext),
            "GetOrganizationContext", "Read the qualified business context and request token for one admitted organization.");
        return endpoints;
    }

    private static async Task<IResult> GetIdentity(HttpContext context, CatalogRequestBoundary boundary)
    {
        context.Response.Headers.CacheControl = "no-store";
        var admission = await boundary.AuthenticateIdentityAsync(context, context.RequestAborted);
        if (!admission.IsAdmitted)
            return Error(admission.Error!.Value, admission.StatusCode);
        if (context.Request.QueryString.HasValue)
            return Error(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
        return Results.Ok(new IdentityV1(admission.ContextMode!, admission.Actor!.PrincipalId.Value.ToString("D"),
            admission.IdentityContext));
    }

    private static async Task<IResult> ListOrganizations(HttpContext context, CatalogRequestBoundary boundary,
        string? limit = null, string? cursor = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        var admission = await boundary.AuthenticateIdentityAsync(context, context.RequestAborted);
        if (!admission.IsAdmitted)
            return Error(admission.Error!.Value, admission.StatusCode);
        foreach (var query in context.Request.Query)
            if (query.Key is not ("limit" or "cursor") || query.Value.Count != 1)
                return Error(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
        var size = 50;
        if (limit is not null && (limit.Length > 3 || !int.TryParse(limit, NumberStyles.None,
            CultureInfo.InvariantCulture, out size) || size is < 1 or > 100 ||
            limit != size.ToString(CultureInfo.InvariantCulture)))
            return Error(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
        var principal = admission.Actor!.PrincipalId.Value;
        Guid? after = null;
        if (cursor is not null && !DecodeCursor(cursor, principal, out after))
            return Error(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
        try
        {
            OrganizationDiscoveryPage page;
            if (admission.ContextMode == "development")
            {
                // Development exposes at most the freshly admitted configured organization.
                var reader = context.RequestServices.GetService<ICatalogReader>();
                if (reader is null || admission.AllowedOrganizationId is not { } configured)
                    return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
                var organization = await reader.GetOrganizationAdmissionAsync(admission.Actor, configured,
                    context.RequestAborted);
                page = new(organization is null ? [] : [new OrganizationDiscoveryItem(organization.OrganizationId, organization.Name)], null);
            }
            else
            {
                var discovery = context.RequestServices.GetService<IOrganizationDiscovery>();
                if (discovery is null || admission.BindingId == Guid.Empty || admission.BindingVersion < 1)
                    return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
                var found = await discovery.DiscoverAsync(
                    new(admission.Actor.PrincipalId, admission.BindingId, admission.BindingVersion),
                    size, after, context.RequestAborted);
                if (found is null)
                    return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
                page = found;
            }
            var organizations = page.Items.Select(item => new OrganizationV1(item.OrganizationId.ToString("D"), item.Name)).ToArray();
            var result = new OrganizationPageV1(organizations, page.After is { } next ? EncodeCursor(principal, next) : null);
            var serialized = System.Text.Json.JsonSerializer.Serialize(result,
                new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            if (Encoding.UTF8.GetByteCount(serialized) > MaximumPageBytes)
                return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
            return Results.Json(result, statusCode: StatusCodes.Status200OK);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or OperationCanceledException))
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Task<IResult> GetContext(string organizationId, HttpContext context,
        CatalogRequestBoundary boundary, CatalogMetadataEditOptions metadataEditOptions,
        CatalogRevisionIngestionOptions ingestionOptions, CatalogProjectCreationOptions creationOptions,
        CatalogRevisionReportOptions revisionReportOptions) =>
        CatalogEndpoints.WithSelectedContext(context, boundary, metadataEditOptions, ingestionOptions,
            creationOptions, revisionReportOptions);

    private static bool DecodeCursor(string cursor, Guid principal, out Guid? after)
    {
        after = null;
        if (cursor.Length is 0 or > MaximumCursorCharacters)
            return false;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(
                cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - cursor.Length % 4) % 4)));
        }
        catch (FormatException) { return false; }
        var parts = decoded.Split(':');
        if (parts.Length != 3 || parts[0] != "braid-organizations-v1" ||
            !Guid.TryParseExact(parts[1], "D", out var owner) || owner != principal ||
            !Guid.TryParseExact(parts[2], "D", out var last) || last == Guid.Empty)
            return false;
        after = last;
        return true;
    }

    private static string EncodeCursor(Guid principal, Guid after) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"braid-organizations-v1:{principal:D}:{after:D}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IResult Error(CatalogErrorCodeV1 code, int status) =>
        Results.Json(new CatalogErrorV1(code), statusCode: status);

    private static void Describe<T>(RouteHandlerBuilder route, string name, string summary) =>
        route.WithName(name).WithTags("Organizations").WithSummary(summary)
            .Produces<T>(StatusCodes.Status200OK)
            .Produces<CatalogErrorV1>(StatusCodes.Status400BadRequest)
            .Produces<CatalogErrorV1>(StatusCodes.Status401Unauthorized)
            .Produces<CatalogErrorV1>(StatusCodes.Status403Forbidden)
            .Produces<CatalogErrorV1>(StatusCodes.Status404NotFound)
            .Produces<CatalogErrorV1>(StatusCodes.Status429TooManyRequests)
            .Produces<CatalogErrorV1>(StatusCodes.Status503ServiceUnavailable);
}
