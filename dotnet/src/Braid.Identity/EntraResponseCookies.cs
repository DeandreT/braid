using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Braid.Identity;

internal sealed class EntraResponseCookies(EntraBrowserClient.Operation operation, IResponseCookies underlying)
    : IResponseCookies, IResponseCookiesFeature
{
    private readonly HashSet<string> issued = new(StringComparer.Ordinal);
    public IResponseCookies Cookies => this;
    public void Append(string key, string value) =>
        throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);

    public void Append(string key, string value, CookieOptions options)
    {
        Require(key, options);
        var now = DateTimeOffset.UtcNow;
        if (value != "N" || issued.Count >= 2 || options.Expires is not { } expires ||
            expires <= now || expires > now.Add(EntraBrowserClient.FlowLifetime).AddSeconds(1))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        if (key.StartsWith(EntraBrowserClient.NoncePrefix, StringComparison.Ordinal))
        {
            if (operation.NonceName is not null || !EntraBrowserClient.IsBoundNonceCookieName(key))
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            operation.NonceName = key;
        }
        issued.Add(key);
        underlying.Append(key, value, options);
        RequireBudget();
    }

    public void Append(ReadOnlySpan<KeyValuePair<string, string>> values, CookieOptions options)
    {
        foreach (var pair in values) Append(pair.Key, pair.Value, options);
    }

    public void Delete(string key) =>
        throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);

    public void Delete(string key, CookieOptions options)
    {
        Require(key, options);
        underlying.Delete(key, options);
        RequireBudget();
    }

    private void Require(string key, CookieOptions options)
    {
        operation.CheckActive();
        if (key.Length > 4096 || !IsFlowCookie(key) || !options.Secure || !options.HttpOnly ||
            options.Domain is not null || options.Path != "/" || options.SameSite != SameSiteMode.None ||
            !options.IsEssential || options.MaxAge is not null)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    private void RequireBudget()
    {
        if (operation.Context.Response.Headers.SetCookie.Sum(value => Encoding.UTF8.GetByteCount(value ?? "")) >
            EntraEndpointPolicy.MaximumProtocolBytes)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
    }

    internal void Cleanup()
    {
        if (operation.Context.Response.HasStarted) return;
        if (operation.CorrelationName is { } correlation) issued.Add(correlation);
        if (operation.NonceName is { } nonce && EntraBrowserClient.IsBoundNonceCookieName(nonce)) issued.Add(nonce);
        foreach (var key in issued)
        {
            if (!IsFlowCookie(key)) continue;
            try
            {
                // Non-authoritative cleanup after cancellation. No application/AF cookie.
                underlying.Delete(key, new CookieOptions
                {
                    Path = "/",
                    Secure = true,
                    HttpOnly = true,
                    SameSite = SameSiteMode.None,
                    IsEssential = true
                });
            }
            catch (Exception error) when (EntraErrors.Recoverable(error))
            { operation.Refuse(ServerSessionIdentityFailureKind.Unavailable); }
        }
    }

    private static bool IsFlowCookie(string key) => key.StartsWith(EntraBrowserClient.NoncePrefix, StringComparison.Ordinal) ||
        key.StartsWith(EntraBrowserClient.CorrelationPrefix, StringComparison.Ordinal);
}
