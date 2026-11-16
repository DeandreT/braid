using System.Security.Cryptography;
using System.Text;

namespace Braid.Identity;

/// <summary>A presentation signal only. It is neither a credential nor a fresh authority observation.</summary>
public static class ServerSessionIdentityContext
{
    public static string Derive(ServerSessionRequestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.OrganizationId == Guid.Empty || identity.PrincipalId is null ||
            identity.PrincipalId.Value == Guid.Empty || identity.LoginEpoch == Guid.Empty)
            throw new BrowserAuthenticationException(BrowserAuthenticationFailureKind.Unavailable);
        var bytes = Encoding.UTF8.GetBytes("braid:identity-context:v1\n" + identity.OrganizationId.ToString("D") +
            "\n" + identity.PrincipalId.Value.ToString("D") + "\n" + identity.LoginEpoch.ToString("D"));
        byte[]? digest = null;
        try
        {
            digest = SHA256.HashData(bytes);
            return "v1." + Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (digest is not null) CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal static bool IsCanonical(string? value) => value is { Length: 46 } &&
        value.StartsWith("v1.", StringComparison.Ordinal) &&
        ServerSessionCarrierFormat.IsCanonicalToken(value[3..], 43);
}
