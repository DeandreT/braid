using System.Text.Json;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Weft.Identity;

// Managed immutable values only: standard manager validation can outlive its private
// unjoinable refresh task without borrowing profile, credentials or transport resources.
internal sealed class EntraEndpointPolicy
{
    internal const string Host = "login.microsoftonline.com";
    internal const int MaximumDocumentBytes = 1024 * 1024;
    internal const int MaximumProtocolBytes = 16 * 1024;
    internal const string TemplateIssuer = "https://login.microsoftonline.com/{tenantid}/v2.0";
    internal readonly string Tenant;
    internal readonly string Client;
    internal readonly string Issuer;
    internal readonly string Discovery;
    internal readonly string Authorize;
    internal readonly string Token;
    internal readonly string TenantKeys;
    internal const string CommonKeys = "https://login.microsoftonline.com/common/discovery/v2.0/keys";

    internal EntraEndpointPolicy(EntraClientOptions options)
    {
        Tenant = options.TenantId.ToString("D");
        Client = options.ClientId.ToString("D");
        Issuer = "https://" + Host + "/" + Tenant + "/v2.0";
        Discovery = Issuer + "/.well-known/openid-configuration";
        var protocol = "https://" + Host + "/" + Tenant + "/oauth2/v2.0/";
        Authorize = protocol + "authorize";
        Token = protocol + "token";
        TenantKeys = "https://" + Host + "/" + Tenant + "/discovery/v2.0/keys";
    }

    internal bool IsKeys(string? value) => value == TenantKeys || value == CommonKeys;

    internal void RequireSend(HttpMethod method, Uri? address)
    {
        var exact = address?.OriginalString;
        if (address is null || !address.IsAbsoluteUri || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || address.Fragment.Length != 0 || address.Query.Length != 0 ||
            address.Host != Host || address.Port != 443 ||
            !(method == HttpMethod.Get && (exact == Discovery || IsKeys(exact)) ||
              method == HttpMethod.Post && exact == Token))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    internal void RequireDiscovery(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        RequireUniqueObject(root);
        if (ReadRequired(root, "issuer") != Issuer || ReadRequired(root, "authorization_endpoint") != Authorize ||
            ReadRequired(root, "token_endpoint") != Token || !IsKeys(ReadRequired(root, "jwks_uri")))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        if (root.TryGetProperty("pushed_authorization_request_endpoint", out var par) &&
            (par.ValueKind != JsonValueKind.String || par.GetString() != ""))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        if (root.TryGetProperty("require_pushed_authorization_requests", out var required) &&
            required.ValueKind != JsonValueKind.False)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    internal void RequireConfiguration(OpenIdConnectConfiguration configuration)
    {
        if (configuration.Issuer != Issuer || configuration.AuthorizationEndpoint != Authorize ||
            configuration.TokenEndpoint != Token || !IsKeys(configuration.JwksUri) ||
            !string.IsNullOrEmpty(configuration.PushedAuthorizationRequestEndpoint) ||
            configuration.RequirePushedAuthorizationRequests == true || configuration.SigningKeys.Count == 0)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    internal static void RequireKeysJson(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        RequireUniqueObject(document.RootElement);
        if (!document.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array ||
            keys.GetArrayLength() is < 1 or > 128)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        foreach (var key in keys.EnumerateArray()) RequireUniqueObject(key);
    }

    internal static void RequireUniqueObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name))
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    internal static string ReadRequired(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text ? text :
        throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
}
