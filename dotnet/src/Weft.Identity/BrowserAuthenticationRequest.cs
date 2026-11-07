using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Weft.Identity;

internal static class BrowserAuthenticationRequest
{
    internal const string ReturnToHeader = "X-Weft-Return-To";

    /// <summary>The validated sign-in return path of a login request, or the application root.</summary>
    internal static string ReturnPath(HttpContext context)
    {
        var value = context.Request.Headers[ReturnToHeader];
        return value.Count == 1 && BrowserReturnPath.IsValid(value[0]) ? value[0]! : BrowserReturnPath.Root;
    }

    internal static void Require(HttpContext context, BrowserAuthenticationOptions options, string action)
    {
        var request = context.Request;
        if (context.Response.HasStarted) throw Error(BrowserAuthenticationFailureKind.Unavailable);
        if (!request.IsHttps || request.Scheme != "https" || request.Host.Value != options.Transport.Authority ||
            request.PathBase.HasValue || context.Features.Get<ITlsTokenBindingFeature>()?.GetProvidedTokenBindingId() is not null)
            throw Error(BrowserAuthenticationFailureKind.RequestRejected);
        var callback = action == "callback";
        if (action != "login" && request.Headers.ContainsKey("X-Weft-Invitation"))
            throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        if (action == "login")
        {
            var invitation = request.Headers["X-Weft-Invitation"];
            if (invitation.Count > 1 || invitation.Count == 1 && !Weft.Application.OrganizationInvitationSecret.IsValid(invitation[0]))
                throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        }
        var path = action switch
        {
            "bootstrap" => "/api/auth/session",
            "login" => "/api/auth/login",
            "logout" => "/api/auth/logout",
            "forget-local" => "/api/auth/forget-local",
            "callback" => EntraBrowserClient.CallbackPath,
            _ => throw Error(BrowserAuthenticationFailureKind.InvalidRequest)
        };
        if (request.Path != path || request.Method != (callback || action == "bootstrap" ? "GET" : "POST"))
            throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        RequireEmptyBody(context);
        if (request.Headers.Authorization.Count != 0) throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        if (!callback && request.QueryString.HasValue) throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        var origin = request.Headers.Origin;
        if (!callback && (action != "bootstrap" || origin.Count != 0) &&
            (origin.Count != 1 || !string.Equals(origin[0], options.ExpectedOrigin, StringComparison.Ordinal)))
            throw Error(BrowserAuthenticationFailureKind.RequestRejected);
        if (callback)
        {
            RequireCallbackQuery(request);
            return;
        }
        var expected = action switch
        {
            "bootstrap" => "X-Weft-Session",
            "login" => "X-Weft-Login",
            "logout" => "X-Weft-Logout",
            _ => "X-Weft-Forget-Local"
        };
        foreach (var name in new[] { "X-Weft-Session", "X-Weft-Login", "X-Weft-Logout", "X-Weft-Forget-Local" })
        {
            var values = request.Headers[name];
            if (name == expected ? values.Count != 1 || values[0] != "1" : values.Count != 0)
                throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        }
        // Only sign-in may name where it returns, and only as one valid application path.
        var returnTo = request.Headers[ReturnToHeader];
        if (action == "login" ? returnTo.Count > 1 || returnTo.Count == 1 && !BrowserReturnPath.IsValid(returnTo[0])
            : returnTo.Count != 0)
            throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        var fetchSite = request.Headers["Sec-Fetch-Site"];
        if (fetchSite.Count > 1 || fetchSite.Count == 1 && fetchSite[0] == "cross-site")
            throw Error(BrowserAuthenticationFailureKind.RequestRejected);
    }

    internal static void RequireEmptyBody(HttpContext context)
    {
        var request = context.Request;
        if (request.Headers.ContentType.Count != 0 || request.Headers.TransferEncoding.Count != 0 ||
            request.ContentLength is > 0 || request.Headers["Content-Length"].Count > 1)
            throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        var body = context.Features.Get<IHttpRequestBodyDetectionFeature>();
        if (body is null) throw Error(BrowserAuthenticationFailureKind.Unavailable);
        if (body.CanHaveBody) throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
    }

    private static void RequireCallbackQuery(HttpRequest request)
    {
        var raw = request.QueryString.Value;
        if (raw is null || raw.Length is < 2 or > 16384 || raw[0] != '?' ||
            raw.Any(character => character < 33 || character > 126))
            throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in raw[1..].Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals < 1 || values.Count == 16) throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
            var name = Decode(pair[..equals]);
            var value = Decode(pair[(equals + 1)..]);
            var bound = name switch
            {
                "code" => 8192,
                "state" => BrowserReturnPath.MaximumProtectedStateLength,
                "error_description" => 4096,
                "error_uri" or "iss" => 2048,
                "session_state" => 1024,
                "client_info" or "error" => 512,
                _ => throw Error(BrowserAuthenticationFailureKind.InvalidRequest)
            };
            if (value.Length > bound || !values.TryAdd(name, value))
                throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        }
        if (!values.TryGetValue("state", out var state) || state.Length == 0 ||
            values.ContainsKey("code") == values.ContainsKey("error") ||
            values.TryGetValue("code", out var code) && code.Length == 0 ||
            values.TryGetValue("error", out var error) && error.Length == 0 ||
            values.ContainsKey("code") && (values.ContainsKey("error_description") || values.ContainsKey("error_uri")))
            throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
        if (request.Query.Count != values.Count || values.Any(pair =>
            request.Query[pair.Key].Count != 1 || request.Query[pair.Key][0] != pair.Value))
            throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
    }

    private static string Decode(string raw)
    {
        var bytes = new byte[raw.Length];
        var length = 0;
        try
        {
            for (var index = 0; index < raw.Length; index++)
            {
                var character = raw[index];
                if (character == '%')
                {
                    if (index + 2 >= raw.Length || !char.IsAsciiHexDigit(raw[index + 1]) ||
                        !char.IsAsciiHexDigit(raw[index + 2])) throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
                    bytes[length++] = (byte)((Hex(raw[++index]) << 4) | Hex(raw[++index]));
                }
                else bytes[length++] = (byte)(character == '+' ? ' ' : character);
            }
            var decoded = new UTF8Encoding(false, true).GetString(bytes, 0, length);
            if (decoded.Any(char.IsControl)) throw Error(BrowserAuthenticationFailureKind.InvalidRequest);
            return decoded;
        }
        catch (DecoderFallbackException) { throw Error(BrowserAuthenticationFailureKind.InvalidRequest); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    private static int Hex(char value) => value <= '9' ? value - '0' : (value | 32) - 'a' + 10;
    internal static BrowserAuthenticationException Error(BrowserAuthenticationFailureKind kind) => new(kind);
}
