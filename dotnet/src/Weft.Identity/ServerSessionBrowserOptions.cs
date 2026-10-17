using System.Text.Json.Serialization;

namespace Weft.Identity;

public sealed class ServerSessionBrowserOptions
{
    public ServerSessionBrowserOptions(string expectedOrigin)
    {
        if (string.IsNullOrEmpty(expectedOrigin) || expectedOrigin.Length > 2048 ||
            !Uri.TryCreate(expectedOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || origin.AbsolutePath != "/" || origin.Port is < 1 or > 65535 ||
            origin.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        string host;
        try
        {
            host = origin.HostNameType == UriHostNameType.IPv6 ? "[" + origin.Host.Trim('[', ']').ToLowerInvariant() + "]"
                : origin.IdnHost.ToLowerInvariant();
        }
        catch (UriFormatException)
        {
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        }
        var authority = host + (origin.IsDefaultPort ? "" : ":" +
            origin.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.Equals(expectedOrigin, "https://" + authority, StringComparison.Ordinal))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        ExpectedOrigin = expectedOrigin;
        Authority = authority;
    }

    [JsonIgnore]
    public string ExpectedOrigin { get; }
    internal string Authority { get; }
    public override string ToString() => nameof(ServerSessionBrowserOptions);
}

public enum ServerSessionRequestKind
{
    Anonymous = 1,
    Authenticated = 2,
    AuthenticationRequired = 3,
    CsrfRejected = 4,
    Unavailable = 5
}

public sealed class ServerSessionRequestIdentity
{
    internal ServerSessionRequestIdentity(Weft.Domain.PrincipalId principalId, Guid organizationId, Guid loginEpoch)
    {
        PrincipalId = new(principalId.Value);
        OrganizationId = organizationId;
        LoginEpoch = loginEpoch;
    }

    [JsonIgnore]
    public Weft.Domain.PrincipalId PrincipalId { get; }
    [JsonIgnore]
    public Guid OrganizationId { get; }
    [JsonIgnore]
    public Guid LoginEpoch { get; }
    public override string ToString() => nameof(ServerSessionRequestIdentity);
}

public sealed class ServerSessionRequestAdmission
{
    internal ServerSessionRequestAdmission(ServerSessionRequestKind kind, ServerSessionRequestIdentity? identity = null)
    {
        if ((kind == ServerSessionRequestKind.Authenticated) != (identity is not null))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.IntegrityFailure);
        Kind = kind;
        Identity = identity;
    }

    public ServerSessionRequestKind Kind { get; }
    [JsonIgnore]
    public ServerSessionRequestIdentity? Identity { get; }
    public override string ToString() => nameof(ServerSessionRequestAdmission);
}
