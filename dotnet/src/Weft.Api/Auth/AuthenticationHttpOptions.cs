using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Weft.Api.Catalog;
using Weft.Identity;

namespace Weft.Api.Auth;

public enum AuthenticationHttpMode { Disabled = 1, Development = 2, Session = 3 }

public sealed class AuthenticationHttpOptions
{
    private AuthenticationStatusOrigin? admittedOrigin;

    public AuthenticationHttpOptions(AuthenticationHttpMode mode, string expectedOrigin)
    {
        if (!Enum.IsDefined(mode) || string.IsNullOrEmpty(expectedOrigin))
            throw new ArgumentException("The authentication HTTP profile is invalid.");
        if (mode == AuthenticationHttpMode.Disabled && TryDisabledListener(expectedOrigin, out var listener))
        {
            DisabledListenerAddress = listener;
            Scheme = "http";
            Authority = expectedOrigin["http://".Length..];
        }
        else if (mode == AuthenticationHttpMode.Session || expectedOrigin.StartsWith("https://", StringComparison.Ordinal))
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
        if (DisabledListenerAddress is null)
            admittedOrigin = new(expectedOrigin, Scheme, Authority);
    }

    [JsonIgnore] public AuthenticationHttpMode Mode { get; }
    [JsonIgnore] public string ExpectedOrigin { get; }
    /// <summary>
    /// The configured origin, or the validated bound origin after Disabled port-zero startup.
    /// Null until that listener has been admitted; never derived from request headers.
    /// </summary>
    [JsonIgnore] public string? AdmittedBrowserOrigin => AdmittedOrigin?.Value;
    internal string Scheme { get; }
    internal string Authority { get; }
    internal IPAddress? DisabledListenerAddress { get; }
    internal AuthenticationStatusOrigin? AdmittedOrigin => Volatile.Read(ref admittedOrigin);
    internal void PublishDisabledOrigin(string origin)
    {
        if (Mode != AuthenticationHttpMode.Disabled || DisabledListenerAddress is null ||
            Interlocked.CompareExchange(ref admittedOrigin,
                new(origin, "http", origin["http://".Length..]), null) is not null)
            throw new InvalidOperationException("The disabled host binding is invalid.");
    }
    internal static bool ValidTransport(HttpContext context, AuthenticationStatusOrigin origin) =>
        context.Request.Scheme == origin.Scheme && context.Request.Host.Value == origin.Authority &&
        !context.Request.PathBase.HasValue && (origin.Scheme == "https" ||
            context.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(peer));

    private static bool TryDisabledListener(string value, out IPAddress? listener)
    {
        listener = null;
        if (value.Length > 128 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || uri.Port != 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
            return false;
        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "[" + address + "]" : address.ToString();
        if (value != "http://" + host + ":0") return false;
        listener = address;
        return true;
    }
    public override string ToString() => nameof(AuthenticationHttpOptions);
}

internal sealed class AuthenticationStatusOrigin(string value, string scheme, string authority)
{
    internal string Value { get; } = value;
    internal string Scheme { get; } = scheme;
    internal string Authority { get; } = authority;
    public override string ToString() => nameof(AuthenticationStatusOrigin);
}
