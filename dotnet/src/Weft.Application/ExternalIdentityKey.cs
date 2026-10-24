using System.Text;
using System.Text.Json.Serialization;

namespace Weft.Application;

/// <summary>
/// An exact provider-neutral account key supplied only after provider verification.
/// This value is not a credential, an email lookup, an organization admission, or authority.
/// </summary>
public sealed class ExternalIdentityKey : IEquatable<ExternalIdentityKey>
{
    public const int MaximumProviderBytes = 64;
    public const int MaximumAuthorityBytes = 1024;
    public const int MaximumSubjectBytes = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ExternalIdentityKey(string provider, string authority, string subject)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Length is < 1 or > MaximumProviderBytes || provider[0] is < 'a' or > 'z' ||
            provider.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')
                and not '.' and not '_' and not '-'))
            throw Invalid(nameof(provider));
        ValidateOpaque(authority, MaximumAuthorityBytes, nameof(authority));
        ValidateOpaque(subject, MaximumSubjectBytes, nameof(subject));
        Provider = provider;
        Authority = authority;
        Subject = subject;
    }

    [JsonIgnore]
    public string Provider { get; }
    [JsonIgnore]
    public string Authority { get; }
    [JsonIgnore]
    public string Subject { get; }

    public bool Equals(ExternalIdentityKey? other) => other is not null &&
        StringComparer.Ordinal.Equals(Provider, other.Provider) &&
        StringComparer.Ordinal.Equals(Authority, other.Authority) &&
        StringComparer.Ordinal.Equals(Subject, other.Subject);

    public override bool Equals(object? obj) => obj is ExternalIdentityKey other && Equals(other);

    public static bool operator ==(ExternalIdentityKey? left, ExternalIdentityKey? right) =>
        ReferenceEquals(left, right) || left is not null && left.Equals(right);

    public static bool operator !=(ExternalIdentityKey? left, ExternalIdentityKey? right) => !(left == right);

    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Provider),
        StringComparer.Ordinal.GetHashCode(Authority), StringComparer.Ordinal.GetHashCode(Subject));

    public override string ToString() => "ExternalIdentityKey";

    /// <summary>
    /// Whether a provider's opaque subject meets the subject component rules, for a provider that verifies a
    /// subject without keying on it.
    /// </summary>
    public static bool IsValidSubject(string? subject)
    {
        if (subject is null) return false;
        try { ValidateOpaque(subject, MaximumSubjectBytes, nameof(subject)); return true; }
        catch (ArgumentException) { return false; }
    }

    private static void ValidateOpaque(string value, int maximumBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        // Every admitted UTF-16 code unit consumes at least one UTF-8 byte; this only bounds invalid work.
        if (value.Length is < 1 || value.Length > maximumBytes || value.Any(char.IsControl))
            throw Invalid(parameterName);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
                throw Invalid(parameterName);
        }
        catch (EncoderFallbackException)
        {
            // Never retain the encoder exception: its diagnostic may contain the rejected input.
            throw Invalid(parameterName);
        }
    }

    private static ArgumentException Invalid(string parameterName) =>
        new("The external identity component is invalid.", parameterName);
}

/// <summary>
/// The tenant-stable key of one Microsoft Entra user: the canonical tenant ID (<c>tid</c>) and the user's
/// canonical object ID (<c>oid</c>) in that tenant. Browser sign-in and bearer access derive the same key from
/// their verified tokens, so one person resolves to one binding through both. The distinct provider slug means
/// this key never equals a retired <c>("entra", issuer, sub)</c> browser key, whatever that key's text.
/// </summary>
public static class EntraUserIdentity
{
    public const string Provider = "entra.oid";
    /// <summary>The provider of retired browser keys, keyed on the per-application pairwise <c>sub</c>.</summary>
    public const string RetiredSubjectProvider = "entra";
    private static readonly Guid ConsumerTenant = new("9188040d-6c67-4c5b-b112-36a304b66dad");

    public static ExternalIdentityKey Key(Guid tenantId, Guid objectId)
    {
        if (tenantId == Guid.Empty || tenantId == ConsumerTenant || objectId == Guid.Empty)
            throw new ArgumentException("An Entra user key requires an organizational tenant and an object ID.");
        return new(Provider, tenantId.ToString("D"), objectId.ToString("D"));
    }
}
