using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Weft.Identity;

internal static class ServerSessionBrowserCookies
{
    internal const string SessionName = "__Host-Weft.Session";
    internal const string AntiforgeryName = "__Host-Weft.Antiforgery";
    internal const string RequestTokenHeader = "X-XSRF-TOKEN";
    internal const int MaximumRequestTokenCharacters = 4096;

    internal static CookieRead Read(HttpContext context, string name, int maximumValue)
    {
        var length = 0;
        var pairs = 0;
        string? found = null;
        foreach (var header in context.Request.Headers.Cookie)
        {
            if (header is null || header.Any(character => character > 126 ||
                (character < 32 && character != '\t')))
                return CookieRead.Malformed;
            if (header.Length > 16384 - length)
                return CookieRead.Malformed;
            length += header.Length;
            foreach (var part in header.Split(';'))
            {
                if (++pairs > 128)
                    return CookieRead.Malformed;
                var pair = part.AsSpan().Trim(" \t".AsSpan());
                if (pair.IsEmpty)
                    continue;
                var equals = pair.IndexOf('=');
                if (equals <= 0)
                    return CookieRead.Malformed;
                var key = pair[..equals];
                var trimmedKey = key.Trim(" \t".AsSpan());
                if (trimmedKey.StartsWith(name + "C", StringComparison.OrdinalIgnoreCase))
                    return CookieRead.Malformed;
                if (!trimmedKey.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!key.Equals(name, StringComparison.Ordinal) || found is not null)
                    return CookieRead.Malformed;
                found = pair[(equals + 1)..].ToString();
                if (!ServerSessionCarrierFormat.IsCanonicalToken(found, maximumValue))
                    return CookieRead.Malformed;
            }
        }
        if (found is null)
            return CookieRead.Absent;
        return string.Equals(context.Request.Cookies[name], found, StringComparison.Ordinal)
            ? new(false, found) : CookieRead.Malformed;
    }

    internal static CookieOptions SessionOptions() => new()
    {
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Domain = null,
        Expires = null,
        MaxAge = null,
        IsEssential = true
    };

    internal static CookieOptions AntiforgeryOptions() => new()
    {
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Domain = null,
        Expires = null,
        MaxAge = null,
        IsEssential = true
    };

    internal sealed class CookieRead(bool malformed, string? value)
    {
        internal static CookieRead Malformed => new(true, null);
        internal static CookieRead Absent => new(false, null);
        internal bool IsMalformed { get; } = malformed;
        internal string? Value { get; } = value;
        public override string ToString() => nameof(CookieRead);
    }
}

internal sealed class ServerSessionCookieManager(ServerSessionBrowser browser, bool issue) : ICookieManager
{
    public string? GetRequestCookie(HttpContext context, string key)
    {
        RequireName(key);
        if (issue)
            return null;
        var read = ServerSessionBrowserCookies.Read(context, key, ServerSessionCarrierFormat.MaximumCharacters);
        if (read.IsMalformed)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
        return read.Value;
    }

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options)
    {
        RequireName(key);
        if (!issue || value is null || !ServerSessionCarrierFormat.IsToken(value, ServerSessionCarrierFormat.MaximumCharacters))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
        browser.RequireIssueAppend(context);
        context.Response.Cookies.Append(key, value, ServerSessionBrowserCookies.SessionOptions());
    }

    public void DeleteCookie(HttpContext context, string key, CookieOptions options) =>
        throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);

    private static void RequireName(string key)
    {
        if (!string.Equals(key, ServerSessionBrowserCookies.SessionName, StringComparison.Ordinal))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }
}
