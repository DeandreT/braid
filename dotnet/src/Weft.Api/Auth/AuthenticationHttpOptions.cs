using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Weft.Api.Catalog;
using Weft.Identity;

namespace Weft.Api.Auth;

public enum AuthenticationHttpMode { Disabled = 1, Development = 2, Session = 3 }

public sealed class AuthenticationHttpOptions
{
    public AuthenticationHttpOptions(AuthenticationHttpMode mode, string expectedOrigin)
    {
        if (!Enum.IsDefined(mode) || string.IsNullOrEmpty(expectedOrigin))
            throw new ArgumentException("The authentication HTTP profile is invalid.");
        if (mode == AuthenticationHttpMode.Session || expectedOrigin.StartsWith("https://", StringComparison.Ordinal))
        {
            if (mode == AuthenticationHttpMode.Development)
                throw new ArgumentException("Development authentication retains its loopback HTTP profile.");
            _ = new ServerSessionBrowserOptions(expectedOrigin);
            Scheme = "https";
            Authority = expectedOrigin["https://".Length..];
        }
        else
        {
            _ = new CatalogApiOptions(browserOrigin: expectedOrigin);
            Scheme = "http";
            Authority = expectedOrigin["http://".Length..];
        }
        Mode = mode;
        ExpectedOrigin = expectedOrigin;
    }

    [JsonIgnore] public AuthenticationHttpMode Mode { get; }
    [JsonIgnore] public string ExpectedOrigin { get; }
    internal string Scheme { get; }
    internal string Authority { get; }
    internal bool ValidTransport(HttpContext context) => context.Request.Scheme == Scheme &&
        context.Request.Host.Value == Authority && !context.Request.PathBase.HasValue &&
        (Scheme == "https" || context.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(peer));
    public override string ToString() => nameof(AuthenticationHttpOptions);
}
