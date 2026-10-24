using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Weft.Tests.Cli;

/// <summary>
/// An owned HTTPS issuer for the fixed Microsoft organizational endpoints that implements the public-client flows the
/// CLI uses through MSAL: device authorization (RFC 8628), authorization code with PKCE (S256) on a loopback redirect,
/// and refresh-token redemption. It issues RS256 delegated access tokens with the Entra v2 user claims. Clients reach it
/// only through <see cref="MsalHttp"/> or <see cref="CreateHandler"/>, which map <c>login.microsoftonline.com:443</c> to
/// its loopback port and trust only its own test authority.
/// </summary>
internal sealed class CliTestIssuer : IAsyncDisposable
{
    internal const string Host = "login.microsoftonline.com";
    private readonly ConcurrentDictionary<string, DeviceAuthorization> devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CodeGrant> codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> refreshTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> issued = new();
    private readonly RSA signing = RSA.Create(2048);
    private readonly string keyId = Guid.NewGuid().ToString("N");
    private WebApplication? app;

    private CliTestIssuer(Guid tenant, Guid publicClient, Guid audience, string scopeName, Guid objectId, string username)
    {
        Tenant = tenant;
        PublicClient = publicClient;
        Audience = audience;
        ScopeName = scopeName;
        ObjectId = objectId;
        Username = username;
        using var authority = RSA.Create(2048);
        var request = new CertificateRequest("CN=Weft CLI test authority", authority, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var leafKey = RSA.Create(2048);
        var leaf = new CertificateRequest("CN=" + Host, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Host);
        leaf.CertificateExtensions.Add(names.Build());
        using var publicLeaf = leaf.Create(Root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = publicLeaf.CopyWithPrivateKey(leafKey);
        Tls = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    public Guid Tenant { get; }
    public Guid PublicClient { get; }
    public Guid Audience { get; }
    public string ScopeName { get; }
    public Guid ObjectId { get; }
    public string Username { get; }
    public string Scope => $"api://{Audience:D}/{ScopeName}";
    public string Issuer => $"https://{Host}/{Tenant:D}/v2.0";
    public X509Certificate2 Root { get; }
    private X509Certificate2 Tls { get; }
    public int Port { get; private set; }
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Approve device codes as soon as they are issued, as a user would after entering the code.</summary>
    public bool ApproveDevices { get; set; } = true;

    /// <summary>Refuse the next refresh-token redemption with <c>invalid_grant</c>.</summary>
    public bool RefuseRefresh { get; set; }

    public int DeviceGrants;
    public int CodeGrants;
    public int RefreshGrants;
    public int Refusals;

    /// <summary>Every access, refresh and ID token issued, so tests can prove none reached output.</summary>
    public IReadOnlyCollection<string> IssuedSecrets => issued.ToArray();

    /// <summary>
    /// A scripted system browser: opens the authorization address, then submits the approved sign-in's form to the
    /// loopback listener, retrying until the listener (which MSAL starts after opening the browser) accepts it.
    /// </summary>
    public async Task<Uri> BrowseAsync(Uri address)
    {
        using var browser = new HttpClient(CreateHandler());
        var page = await browser.GetStringAsync(address);
        var action = new Uri(WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(page, "action=\"([^\"]+)\"").Groups[1].Value));
        var fields = System.Text.RegularExpressions.Regex.Matches(page, "name=\"([a-z]+)\" value=\"([^\"]*)\"")
            .ToDictionary(match => match.Groups[1].Value, match => WebUtility.HtmlDecode(match.Groups[2].Value));
        using var local = new HttpClient();
        for (var attempt = 0; ; attempt++)
            try
            {
                using var landing = await local.PostAsync(action, new FormUrlEncodedContent(fields));
                return action;
            }
            catch (HttpRequestException) when (attempt < 50)
            {
                await Task.Delay(100);
            }
    }

    public SecurityKey PublicKey => new RsaSecurityKey(signing.ExportParameters(false)) { KeyId = keyId };

    public string? LastUserCode { get; private set; }

    public static async Task<CliTestIssuer> StartAsync(Guid? tenant = null, string username = "cli.user@example.test")
    {
        var issuer = new CliTestIssuer(tenant ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "weft.api", Guid.NewGuid(), username);
        await issuer.StartServerAsync();
        return issuer;
    }

    public void Approve(string userCode)
    {
        foreach (var device in devices.Values.Where(device => device.UserCode == userCode))
            device.Approved = true;
    }

    /// <summary>An access token for <paramref name="scope"/> minted directly, for server-side validation tests.</summary>
    public string AccessToken(string? scope = null, Guid? client = null, DateTimeOffset? expires = null) =>
        Mint(new Dictionary<string, object>
        {
            ["aud"] = Audience.ToString("D"),
            ["iss"] = Issuer,
            ["tid"] = Tenant.ToString("D"),
            ["oid"] = ObjectId.ToString("D"),
            ["sub"] = Subject,
            ["azp"] = (client ?? PublicClient).ToString("D"),
            ["azpacr"] = "0",
            ["scp"] = scope ?? ScopeName,
            ["ver"] = "2.0",
            ["name"] = "CLI test user",
            ["preferred_username"] = Username,
            ["uti"] = Base64Url(RandomNumberGenerator.GetBytes(16))
        }, expires ?? DateTimeOffset.UtcNow + AccessTokenLifetime);

    private string Subject { get; } = Base64Url(RandomNumberGenerator.GetBytes(32));

    public IMsalHttpClientFactory MsalHttp => new Factory(this);

    /// <summary>A handler whose only reachable host is this issuer under its Microsoft name.</summary>
    public SocketsHttpHandler CreateHandler(bool redirects = false)
    {
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        chain.CustomTrustStore.Add(Root);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = redirects,
            UseCookies = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (context.DnsEndPoint.Host != Host || context.DnsEndPoint.Port != 443)
                    throw new HttpRequestException("Only the owned issuer is reachable in this test.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = chain }
        };
    }

    private async Task StartServerAsync()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(1);
        Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(Tls);
        }));
        app = builder.Build();
        app.Run(HandleAsync);
        await app.StartAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var path = context.Request.Path.Value ?? "";
        var tenant = "/" + Tenant.ToString("D");
        if (context.Request.Host.Host != Host)
        {
            await Refuse(context, "invalid_request");
            return;
        }
        switch (context.Request.Method, path)
        {
            case ("GET", "/common/discovery/instance"):
                await Json(context, 200, new Dictionary<string, object>
                {
                    ["tenant_discovery_endpoint"] = $"https://{Host}{tenant}/v2.0/.well-known/openid-configuration",
                    ["api-version"] = "1.1",
                    ["metadata"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["preferred_network"] = Host, ["preferred_cache"] = "login.windows.net",
                            ["aliases"] = new[] { Host, "login.windows.net", "login.microsoft.com", "sts.windows.net" }
                        }
                    }
                });
                return;
            case ("GET", _) when path == tenant + "/v2.0/.well-known/openid-configuration":
                await Json(context, 200, new Dictionary<string, object>
                {
                    ["issuer"] = Issuer,
                    ["authorization_endpoint"] = $"https://{Host}{tenant}/oauth2/v2.0/authorize",
                    ["token_endpoint"] = $"https://{Host}{tenant}/oauth2/v2.0/token",
                    ["device_authorization_endpoint"] = $"https://{Host}{tenant}/oauth2/v2.0/devicecode",
                    ["jwks_uri"] = $"https://{Host}{tenant}/discovery/v2.0/keys",
                    ["response_types_supported"] = new[] { "code" },
                    ["subject_types_supported"] = new[] { "pairwise" },
                    ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                    ["code_challenge_methods_supported"] = new[] { "S256" }
                });
                return;
            case ("GET", _) when path == tenant + "/discovery/v2.0/keys":
                var key = signing.ExportParameters(false);
                await Json(context, 200, new Dictionary<string, object>
                {
                    ["keys"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["kty"] = "RSA", ["use"] = "sig", ["kid"] = keyId, ["alg"] = "RS256",
                            ["n"] = Base64Url(key.Modulus!), ["e"] = Base64Url(key.Exponent!), ["issuer"] = Issuer
                        }
                    }
                });
                return;
            case ("POST", _) when path == tenant + "/oauth2/v2.0/devicecode":
                await DeviceCodeAsync(context);
                return;
            case ("GET", _) when path == tenant + "/oauth2/v2.0/authorize":
                await AuthorizeAsync(context);
                return;
            case ("POST", _) when path == tenant + "/oauth2/v2.0/token":
                await TokenAsync(context);
                return;
            default:
                await Refuse(context, "invalid_request", 404);
                return;
        }
    }

    private async Task DeviceCodeAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form["client_id"] != PublicClient.ToString("D") || !Scopes(form["scope"]).Contains(Scope))
        {
            await Refuse(context, "invalid_client");
            return;
        }
        var device = new DeviceAuthorization(Base64Url(RandomNumberGenerator.GetBytes(32)),
            new string(RandomNumberGenerator.GetItems<char>("BCDFGHJKLMNPQRSTVWXZ", 8)), DateTimeOffset.UtcNow.AddMinutes(15))
        { Approved = ApproveDevices };
        devices[device.DeviceCode] = device;
        LastUserCode = device.UserCode;
        await Json(context, 200, new Dictionary<string, object>
        {
            ["device_code"] = device.DeviceCode,
            ["user_code"] = device.UserCode,
            ["verification_uri"] = "https://microsoft.com/devicelogin",
            ["expires_in"] = 900,
            ["interval"] = 1,
            ["message"] = "To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code " + device.UserCode
        });
    }

    private Task AuthorizeAsync(HttpContext context)
    {
        var query = context.Request.Query;
        if (query["client_id"] != PublicClient.ToString("D") || query["response_type"] != "code" || query["code_challenge_method"] != "S256" ||
            query["code_challenge"].ToString().Length != 43 || !Scopes(query["scope"]).Contains(Scope) || query["state"].Count != 1 ||
            !Uri.TryCreate(query["redirect_uri"], UriKind.Absolute, out var redirect) || redirect.Scheme != "http" ||
            redirect.Host != "localhost" || query.ContainsKey("client_secret"))
            return Refuse(context, "invalid_request");
        var code = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirectUri = query["redirect_uri"].ToString();
        codes[code] = new CodeGrant(query["code_challenge"]!, redirectUri, DateTimeOffset.UtcNow.AddMinutes(5));
        if (query["response_mode"] == "form_post")
        {
            // The approved sign-in posts the code to the loopback listener, so it never appears in a URL.
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync($"""
                <!doctype html><html><body onload="document.forms[0].submit()">
                <form method="post" action="{WebUtility.HtmlEncode(redirectUri)}">
                <input type="hidden" name="code" value="{WebUtility.HtmlEncode(code)}">
                <input type="hidden" name="state" value="{WebUtility.HtmlEncode(query["state"])}">
                </form></body></html>
                """, context.RequestAborted);
        }
        var location = redirectUri + (redirect.Query.Length == 0 ? "?" : "&") + "code=" + Uri.EscapeDataString(code) +
            "&state=" + Uri.EscapeDataString(query["state"]!);
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = location;
        return Task.CompletedTask;
    }

    private async Task TokenAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form["client_id"] != PublicClient.ToString("D") || form.ContainsKey("client_secret") || form.ContainsKey("client_assertion"))
        {
            await Refuse(context, "invalid_client");
            return;
        }
        switch (form["grant_type"].ToString())
        {
            // MSAL sends the short form; RFC 8628 defines the URN.
            case "device_code" or "urn:ietf:params:oauth:grant-type:device_code":
                if (!devices.TryGetValue(form["device_code"].ToString(), out var device) || device.Expires <= DateTimeOffset.UtcNow)
                {
                    await Refuse(context, "expired_token");
                    return;
                }
                if (!device.Approved)
                {
                    await Json(context, 400, new Dictionary<string, object>
                    {
                        ["error"] = "authorization_pending",
                        ["error_description"] = "The user has not yet completed sign-in."
                    });
                    return;
                }
                devices.TryRemove(device.DeviceCode, out _);
                Interlocked.Increment(ref DeviceGrants);
                await TokensAsync(context);
                return;
            case "authorization_code":
                if (!codes.TryRemove(form["code"].ToString(), out var grant) || grant.Expires <= DateTimeOffset.UtcNow ||
                    form["redirect_uri"] != grant.RedirectUri ||
                    Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))) != grant.Challenge)
                {
                    await Refuse(context, "invalid_grant");
                    return;
                }
                Interlocked.Increment(ref CodeGrants);
                await TokensAsync(context);
                return;
            case "refresh_token":
                if (RefuseRefresh || !refreshTokens.TryRemove(form["refresh_token"].ToString(), out _))
                {
                    RefuseRefresh = false;
                    await Refuse(context, "invalid_grant");
                    return;
                }
                Interlocked.Increment(ref RefreshGrants);
                await TokensAsync(context);
                return;
            default:
                await Refuse(context, "unsupported_grant_type");
                return;
        }
    }

    private async Task TokensAsync(HttpContext context)
    {
        var access = AccessToken();
        var refresh = Base64Url(RandomNumberGenerator.GetBytes(32));
        refreshTokens[refresh] = true;
        var now = DateTimeOffset.UtcNow;
        var id = Mint(new Dictionary<string, object>
        {
            ["aud"] = PublicClient.ToString("D"),
            ["iss"] = Issuer,
            ["tid"] = Tenant.ToString("D"),
            ["oid"] = ObjectId.ToString("D"),
            ["sub"] = Subject,
            ["ver"] = "2.0",
            ["name"] = "CLI test user",
            ["preferred_username"] = Username
        }, now.AddHours(1));
        issued.Enqueue(access);
        issued.Enqueue(refresh);
        issued.Enqueue(id);
        await Json(context, 200, new Dictionary<string, object>
        {
            ["token_type"] = "Bearer",
            ["scope"] = Scope,
            ["expires_in"] = (int)AccessTokenLifetime.TotalSeconds,
            ["ext_expires_in"] = (int)AccessTokenLifetime.TotalSeconds,
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["id_token"] = id,
            ["client_info"] = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
            {
                ["uid"] = ObjectId.ToString("D"),
                ["utid"] = Tenant.ToString("D")
            }))
        });
    }

    private string Mint(Dictionary<string, object> claims, DateTimeOffset expires)
    {
        var now = DateTimeOffset.UtcNow;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            IssuedAt = now.UtcDateTime.AddSeconds(-1),
            NotBefore = now.UtcDateTime.AddSeconds(-1),
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(PublicSigningKey, SecurityAlgorithms.RsaSha256)
        });
    }

    private RsaSecurityKey PublicSigningKey => new(signing) { KeyId = keyId };

    private static HashSet<string> Scopes(string? text) => new((text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private async Task Refuse(HttpContext context, string error, int status = 400)
    {
        Interlocked.Increment(ref Refusals);
        await Json(context, status, new Dictionary<string, object> { ["error"] = error, ["error_description"] = "Refused by the test issuer." });
    }

    private static async Task Json(HttpContext context, int status, object value)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(value), context.RequestAborted);
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        signing.Dispose();
        Root.Dispose();
        Tls.Dispose();
    }

    private sealed record DeviceAuthorization(string DeviceCode, string UserCode, DateTimeOffset Expires)
    {
        public volatile bool Approved;
    }

    private sealed record CodeGrant(string Challenge, string RedirectUri, DateTimeOffset Expires);

    private sealed class Factory(CliTestIssuer issuer) : IMsalHttpClientFactory
    {
        private readonly HttpClient client = new(issuer.CreateHandler());

        public HttpClient GetHttpClient() => client;
    }
}
