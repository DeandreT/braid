using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace Weft.Tests;

/// <summary>
/// The owned issuer's public-client flows, as MSAL uses them for the weft CLI: Entra instance discovery, device
/// authorization (approved at once, standing in for the user), authorization code with PKCE (S256) answered by
/// form_post to a loopback redirect, and refresh-token redemption. Every grant issues the delegated v2 access token
/// that <see cref="PublicAccessClaims"/> describes for the requesting client, an ID token and client_info.
/// Requests from any other client reach the confidential flows unchanged.
/// </summary>
internal sealed partial class EntraProtocolHttpsIssuer
{
    private readonly ConcurrentDictionary<string, Guid> publicDevices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (Guid Client, string Challenge, string Redirect)> publicCodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Guid> publicRefreshTokens = new(StringComparer.Ordinal);

    /// <summary>Client IDs answered as public clients; none by default.</summary>
    internal IReadOnlyList<Guid> PublicClients { get; set; } = [];

    /// <summary>The delegated scope name the public clients request for the fixture's Weft application.</summary>
    internal string PublicScope { get; set; } = "weft.api";

    /// <summary>The claims of a delegated access token for one public client; set together with <see cref="PublicClients"/>.</summary>
    internal Func<Guid, JsonObject>? PublicAccessClaims { get; set; }
    internal int PublicDeviceGrants, PublicCodeGrants, PublicRefreshGrants, PublicRefusals;
    internal readonly ConcurrentQueue<string> PublicSecrets = new();

    private string PublicBase => EntraProtocolFixturePolicy.Token[..EntraProtocolFixturePolicy.Token.LastIndexOf('/')];

    private async Task<bool> PublicClientAsync(HttpContext context, string path)
    {
        var request = context.Request;
        if (PublicClients.Count == 0)
            return false;
        if (request.Method == "GET" && path == "/common/discovery/instance")
        {
            await context.Response.WriteAsJsonAsync(new Dictionary<string, object>
            {
                ["tenant_discovery_endpoint"] = $"https://login.microsoftonline.com/{EntraProtocolFixturePolicy.Tenant:D}/v2.0/.well-known/openid-configuration",
                ["api-version"] = "1.1",
                ["metadata"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["preferred_network"] = "login.microsoftonline.com",
                        ["preferred_cache"] = "login.windows.net",
                        ["aliases"] = new[] { "login.microsoftonline.com", "login.windows.net", "login.microsoft.com", "sts.windows.net" }
                    }
                }
            }, context.RequestAborted);
            return true;
        }
        if (request.Method == "GET" && path == EntraProtocolFixturePolicy.Authorize)
        {
            var query = QueryHelpers.ParseQuery(request.QueryString.Value);
            if (!PublicClient(query.TryGetValue("client_id", out var id) ? id.ToString() : null, out var client))
                return false;
            if (query["response_type"] != "code" || query["code_challenge_method"] != "S256" || query["code_challenge"].ToString().Length != 43 ||
                query["response_mode"] != "form_post" || query["state"].Count != 1 || query.ContainsKey("client_secret") ||
                !Scopes(query["scope"]) ||
                !Uri.TryCreate(query["redirect_uri"], UriKind.Absolute, out var redirect) || redirect.Scheme != "http" || redirect.Host != "localhost")
            {
                await PublicRefuseAsync(context, "invalid_request");
                return true;
            }
            var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            publicCodes[code] = (client, query["code_challenge"]!, query["redirect_uri"]!);
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync($"""
                <!doctype html><html><body onload="document.forms[0].submit()">
                <form method="post" action="{WebUtility.HtmlEncode(query["redirect_uri"])}">
                <input type="hidden" name="code" value="{code}">
                <input type="hidden" name="state" value="{WebUtility.HtmlEncode(query["state"])}">
                </form></body></html>
                """, context.RequestAborted);
            return true;
        }
        if (request.Method != "POST" || (path != PublicBase + "/devicecode" && path != EntraProtocolFixturePolicy.Token))
            return false;
        request.EnableBuffering();
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, context.RequestAborted);
        request.Body.Position = 0;
        var form = QueryHelpers.ParseQuery("?" + Encoding.UTF8.GetString(body.ToArray()));
        string? One(string key) => form.TryGetValue(key, out var value) && value.Count == 1 ? value[0] : null;
        if (!PublicClient(One("client_id"), out var publicClient))
            return false;
        if (form.ContainsKey("client_secret") || form.ContainsKey("client_assertion"))
        {
            await PublicRefuseAsync(context, "invalid_client");
            return true;
        }
        if (path != EntraProtocolFixturePolicy.Token)
        {
            if (!Scopes(One("scope")))
            {
                await PublicRefuseAsync(context, "invalid_scope");
                return true;
            }
            var device = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            var userCode = new string(RandomNumberGenerator.GetItems<char>("BCDFGHJKLMNPQRSTVWXZ", 8));
            publicDevices[device] = publicClient;
            PublicSecrets.Enqueue(device);
            await context.Response.WriteAsJsonAsync(new Dictionary<string, object>
            {
                ["device_code"] = device,
                ["user_code"] = userCode,
                ["verification_uri"] = "https://microsoft.com/devicelogin",
                ["expires_in"] = 900,
                ["interval"] = 1,
                ["message"] = "Enter the code " + userCode
            }, context.RequestAborted);
            return true;
        }
        var granted = One("grant_type") switch
        {
            "device_code" or "urn:ietf:params:oauth:grant-type:device_code" when One("device_code") is { } device &&
                publicDevices.TryRemove(device, out var owner) && owner == publicClient => Interlocked.Increment(ref PublicDeviceGrants) > 0,
            "authorization_code" when One("code") is { } code && publicCodes.TryRemove(code, out var issued) && issued.Client == publicClient &&
                One("redirect_uri") == issued.Redirect && One("code_verifier") is { } verifier &&
                Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) == issued.Challenge
                => Interlocked.Increment(ref PublicCodeGrants) > 0,
            "refresh_token" when One("refresh_token") is { } refresh && publicRefreshTokens.TryRemove(refresh, out var holder) &&
                holder == publicClient => Interlocked.Increment(ref PublicRefreshGrants) > 0,
            _ => false
        };
        if (!granted)
        {
            await PublicRefuseAsync(context, "invalid_grant");
            return true;
        }
        await PublicTokensAsync(context, publicClient);
        return true;
    }

    private async Task PublicTokensAsync(HttpContext context, Guid client)
    {
        var access = crypto.SignClaims((PublicAccessClaims ?? throw new InvalidOperationException("The public access claims are unset."))(client)
            .ToJsonString());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = crypto.SignClaims(new JsonObject
        {
            ["aud"] = client.ToString("D"),
            ["iss"] = EntraProtocolFixturePolicy.Issuer,
            ["iat"] = now - 60,
            ["nbf"] = now - 60,
            ["exp"] = now + 600,
            ["name"] = "Fixture user",
            ["oid"] = EntraProtocolFixturePolicy.ObjectId.ToString("D"),
            ["preferred_username"] = "fixture.user@invalid.example",
            ["sub"] = "pairwise-cli-subject",
            ["tid"] = EntraProtocolFixturePolicy.Tenant.ToString("D"),
            ["ver"] = "2.0"
        }.ToJsonString());
        var refresh = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        publicRefreshTokens[refresh] = client;
        PublicSecrets.Enqueue(access);
        PublicSecrets.Enqueue(id);
        PublicSecrets.Enqueue(refresh);
        await context.Response.WriteAsJsonAsync(new Dictionary<string, object>
        {
            ["token_type"] = "Bearer",
            ["scope"] = $"api://{EntraProtocolFixturePolicy.Client:D}/{PublicScope}",
            ["expires_in"] = 540,
            ["ext_expires_in"] = 540,
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["id_token"] = id,
            ["client_info"] = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["uid"] = EntraProtocolFixturePolicy.ObjectId.ToString("D"),
                ["utid"] = EntraProtocolFixturePolicy.Tenant.ToString("D")
            }))
        }, context.RequestAborted);
    }

    private bool PublicClient(string? text, out Guid client) =>
        Guid.TryParseExact(text, "D", out client) && PublicClients.Contains(client);

    private bool Scopes(string? scope) =>
        (scope ?? "").Split(' ').Contains($"api://{EntraProtocolFixturePolicy.Client:D}/{PublicScope}");

    private Task PublicRefuseAsync(HttpContext context, string error)
    {
        Interlocked.Increment(ref PublicRefusals);
        context.Response.StatusCode = 400;
        return context.Response.WriteAsJsonAsync(new { error, error_description = "Refused by the owned issuer." }, context.RequestAborted);
    }
}
