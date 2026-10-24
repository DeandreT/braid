using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Weft.Cli.Configuration;

/// <summary>
/// One named profile: only non-secret settings. The tenant is fixed per profile and so is its authority,
/// <c>https://login.microsoftonline.com/{tenant}</c>; nothing in a request or the environment overrides it.
/// </summary>
internal sealed partial record CliProfile
{
    internal const int MaximumScopeLength = 512;

    public CliProfile(string name, Uri server, Guid tenantId, Guid clientId, string scope)
    {
        Name = RequireName(name);
        Server = RequireServer(server);
        TenantId = tenantId != Guid.Empty ? tenantId : throw CliException.Usage("The tenant must be a nonzero tenant ID.");
        ClientId = clientId != Guid.Empty ? clientId : throw CliException.Usage("The client ID must be a nonzero application ID.");
        Scope = RequireScope(scope);
    }

    public string Name { get; }

    /// <summary>The Weft server origin: HTTPS, or HTTP only for a loopback address.</summary>
    public Uri Server { get; }
    public Guid TenantId { get; }
    public Guid ClientId { get; }

    /// <summary>The one delegated API scope the CLI requests, for example <c>api://{id}/weft.api</c>.</summary>
    public string Scope { get; }

    public Uri Authority => new("https://login.microsoftonline.com/" + TenantId.ToString("D", CultureInfo.InvariantCulture));

    internal static string RequireName(string name)
    {
        if (name is null || !NamePattern().IsMatch(name))
            throw CliException.Usage("Profile names use 1-63 lowercase letters, digits and hyphens, starting with a letter or digit.");
        return name;
    }

    internal static Uri ParseServer(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var server))
            throw CliException.Usage("The server must be an absolute https:// URL.");
        return RequireServer(server);
    }

    internal static Guid ParseId(string text, string what)
    {
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty)
            throw CliException.Usage($"The {what} must be a nonzero GUID in the form 00000000-0000-0000-0000-000000000000.");
        return id;
    }

    private static Uri RequireServer(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var loopbackHttp = server.Scheme == Uri.UriSchemeHttp && (server.IsLoopback || IPAddress.TryParse(server.Host, out var address) && IPAddress.IsLoopback(address));
        if (!server.IsAbsoluteUri || !(server.Scheme == Uri.UriSchemeHttps || loopbackHttp) || server.UserInfo.Length != 0 ||
            server.AbsolutePath != "/" || server.Query.Length != 0 || server.Fragment.Length != 0)
            throw CliException.Usage("The server must be an https:// origin with no path, query or credentials (http:// only for loopback).");
        return new Uri(server.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static string RequireScope(string scope)
    {
        if (string.IsNullOrEmpty(scope) || scope.Length > MaximumScopeLength || scope.Any(character => character <= ' ' || character > '~'))
            throw CliException.Usage("The scope must be one printable ASCII scope without spaces, for example api://<application ID>/weft.api.");
        if (scope.EndsWith("/.default", StringComparison.Ordinal))
            throw CliException.Usage("The scope must be the delegated Weft API scope, not a .default scope.");
        return scope;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
