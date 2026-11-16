using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;

namespace Braid.Api.Auth;

/// <summary>
/// Marks a business route: the catalog boundary admits it with a Braid session cookie (and its epoch-bound
/// antiforgery header for unsafe methods) or, when bearer access is enabled, with a signed-in user's bearer token.
/// </summary>
public sealed class BusinessRouteMetadata
{
    internal static readonly BusinessRouteMetadata Instance = new();
    private BusinessRouteMetadata() { }
    public override string ToString() => nameof(BusinessRouteMetadata);
}

/// <summary>The supported security contract: which schemes each operation admits, generated from route metadata.</summary>
internal static class ApiSecurity
{
    internal const string SessionScheme = "braidSession";
    internal const string AntiforgeryScheme = "braidAntiforgery";
    internal const string BearerScheme = "entraBearer";
    internal const string AuthorizationPath = "https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize";
    internal const string TokenPath = "https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
    internal const string DevicePath = "https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/devicecode";
    internal const string ScopeTemplate = "api://{sessionClientId}/{scope}";

    /// <summary>Business routes carry <see cref="BusinessRouteMetadata"/>; the empty prefix keeps their exact paths.</summary>
    internal static RouteGroupBuilder BusinessRoutes(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGroup("").WithMetadata(BusinessRouteMetadata.Instance);

    internal static bool IsBusiness(EndpointMetadataCollection metadata) => metadata.GetMetadata<BusinessRouteMetadata>() is not null;

    internal static void Transform(OpenApiDocument document, IReadOnlyList<ApiDescriptionGroup> groups)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        var schemes = document.Components.SecuritySchemes;
        schemes[SessionScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = "__Host-Braid.Session",
            Description = "The browser session from Entra sign-in at /api/auth/login. It is HttpOnly and never readable by scripts. " +
                "Unsafe methods also need the braidAntiforgery header issued for that session."
        };
        schemes[AntiforgeryScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-XSRF-TOKEN",
            Description = "The epoch-bound antiforgery request token from the session status or catalog context, with the exact Origin. " +
                "Required with braidSession on POST, PUT, PATCH and DELETE."
        };
        var scope = new Dictionary<string, string>
        { [ScopeTemplate] = "Act as the signed-in user, with that user's current Braid roles. Replace {sessionClientId} with the Session app registration's client ID and {scope} with its exposed scope." };
        var flows = new OpenApiOAuthFlows
        {
            AuthorizationCode = new OpenApiOAuthFlow
            {
                AuthorizationUrl = new Uri(AuthorizationPath),
                TokenUrl = new Uri(TokenPath),
                Scopes = scope
            }
        };
        // OpenAPI 3.1 has no device flow; this follows the 3.2 deviceAuthorization flow object.
        flows.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        flows.Extensions["x-deviceAuthorization"] = new JsonNodeExtension(new JsonObject
        {
            ["deviceAuthorizationUrl"] = DevicePath,
            ["tokenUrl"] = TokenPath,
            ["scopes"] = new JsonObject { [ScopeTemplate] = scope[ScopeTemplate] }
        });
        schemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Flows = flows,
            Description = "A Microsoft Entra v2 delegated access token for the signed-in user, sent as Authorization: Bearer. " +
                "Public client applications that the deployment allowlists obtain it with the authorization-code flow with PKCE (S256) " +
                "and a loopback redirect, or with the device-code flow. {tenantId} is the deployment's fixed organizational tenant. " +
                "Accepted only on business routes, only when the deployment enables bearer access, and never together with Braid cookies. " +
                "A refused token answers 401 with WWW-Authenticate: Bearer error=\"invalid_token\"; a valid token without an enabled Braid binding answers 403."
        };
        var business = new HashSet<(string, HttpMethod)>();
        foreach (var description in groups.SelectMany(group => group.Items))
            if (description.RelativePath is { } path && description.HttpMethod is { } method &&
                IsBusiness(new EndpointMetadataCollection(description.ActionDescriptor.EndpointMetadata)))
                business.Add(("/" + path, new HttpMethod(method)));
        document.Paths ??= new OpenApiPaths();
        foreach (var (path, item) in document.Paths)
        {
            if (item.Operations is null) continue;
            foreach (var (method, operation) in item.Operations)
            {
                if (business.Contains((path, method)))
                {
                    var safe = method == HttpMethod.Get || method == HttpMethod.Head;
                    operation.Security =
                    [
                        Requirement(document, safe ? [SessionScheme] : [SessionScheme, AntiforgeryScheme]),
                        new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, document)] = [ScopeTemplate] }
                    ];
                    SessionHeaders(operation);
                    Refusals(operation);
                }
                else if (path.StartsWith("/api/auth/", StringComparison.Ordinal))
                {
                    // Anonymous or a browser session; bearer tokens are refused on every authentication route.
                    var safe = method == HttpMethod.Get;
                    operation.Security = [new OpenApiSecurityRequirement(), Requirement(document, safe ? [SessionScheme] : [SessionScheme, AntiforgeryScheme])];
                }
            }
        }
    }

    private static OpenApiSecurityRequirement Requirement(OpenApiDocument document, string[] names)
    {
        var requirement = new OpenApiSecurityRequirement();
        foreach (var name in names) requirement[new OpenApiSecuritySchemeReference(name, document)] = [];
        return requirement;
    }

    // Origin and the antiforgery header belong to the session scheme; a bearer request sends neither.
    private static void SessionHeaders(OpenApiOperation operation)
    {
        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
            if (parameter is { In: ParameterLocation.Header, Required: true, Name: "Origin" or "X-XSRF-TOKEN" })
            {
                parameter.Required = false;
                parameter.Description = "Required with braidSession; omitted with entraBearer. " + parameter.Description;
            }
    }

    // Every business operation declares its closed identity refusals and the bearer rate limit.
    private static void Refusals(OpenApiOperation operation)
    {
        operation.Responses ??= new OpenApiResponses();
        var error = operation.Responses.Where(pair => pair.Key is "400" or "401" or "403" or "404" or "503")
            .Select(pair => pair.Value.Content).FirstOrDefault(content => content?.ContainsKey("application/json") == true);
        if (error is null) throw new InvalidOperationException("A business operation has no closed error schema.");
        foreach (var (status, description) in new[] { ("400", "Malformed credentials or a bearer token combined with cookies."),
            ("401", "Identity refused: no session or a refused bearer token."),
            ("403", "Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding."),
            ("429", "This principal's bearer request rate is exceeded; retry after Retry-After seconds."),
            ("503", "Identity validation, identity persistence or audit persistence is unavailable.") })
        {
            if (operation.Responses.ContainsKey(status)) continue;
            operation.Responses[status] = new OpenApiResponse { Description = description, Content = new Dictionary<string, OpenApiMediaType>(error) };
        }
        foreach (var (status, challenge) in new[] { ("400", CatalogBearerAdmission.InvalidRequest), ("401", CatalogBearerAdmission.InvalidToken) })
        {
            if (operation.Responses[status] is not OpenApiResponse response) continue;
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["WWW-Authenticate"] = new OpenApiHeader
            {
                Description = "Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^" + challenge + "$" }
            };
        }
        if (operation.Responses["429"] is OpenApiResponse limited)
        {
            limited.Headers ??= new Dictionary<string, IOpenApiHeader>();
            // Other 429 answers of an operation are nonwaiting admission refusals, which carry no Retry-After.
            limited.Headers["Retry-After"] = new OpenApiHeader
            {
                Description = "Sent with the rateLimited code: whole seconds until this principal may send another bearer request.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" }
            };
        }
    }
}
