using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

namespace Braid.Identity;

internal sealed class ServerSessionCarrierFormat(IDataProtector protector) : ISecureDataFormat<AuthenticationTicket>
{
    internal const string SessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";
    internal const int MaximumCharacters = 2048;

    public string Protect(AuthenticationTicket data) => Protect(data, purpose: null);

    public string Protect(AuthenticationTicket data, string? purpose)
    {
        RequirePurpose(purpose);
        var key = RequireCarrier(data);
        var plaintext = new byte[51];
        byte[]? ciphertext = null;
        try
        {
            "BSC1"u8.CopyTo(plaintext);
            Encoding.ASCII.GetBytes(key, plaintext.AsSpan(4));
            ciphertext = protector.Protect(plaintext);
            var encoded = Encode(ciphertext);
            if (encoded.Length is < 1 or > MaximumCharacters)
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            return encoded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null)
                CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public AuthenticationTicket? Unprotect(string? protectedText) => Unprotect(protectedText, purpose: null);

    public AuthenticationTicket? Unprotect(string? protectedText, string? purpose)
    {
        RequirePurpose(purpose);
        if (!IsToken(protectedText, MaximumCharacters))
            return null;
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            ciphertext = Decode(protectedText!);
            if (!string.Equals(Encode(ciphertext), protectedText, StringComparison.Ordinal))
                return null;
            try { plaintext = protector.Unprotect(ciphertext); }
            catch (ServerSessionIdentityException error) when (error.Kind == ServerSessionIdentityFailureKind.IntegrityFailure)
            {
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Denied);
            }
            if (plaintext.Length != 51 || !plaintext.AsSpan(0, 4).SequenceEqual("BSC1"u8) ||
                !IsAscii(plaintext.AsSpan(4)))
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Denied);
            var key = Encoding.ASCII.GetString(plaintext, 4, 47);
            if (!ServerSessionHandle.TryParse(key, out _))
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Denied);
            var claim = new Claim(SessionIdClaim, key, ClaimValueTypes.String,
                ServerSessionSchemes.Session, ServerSessionSchemes.Session);
            return new(new ClaimsPrincipal(new ClaimsIdentity([claim], ServerSessionSchemes.Session)),
                new AuthenticationProperties(), ServerSessionSchemes.Session);
        }
        catch (FormatException) { return null; }
        finally
        {
            if (ciphertext is not null)
                CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static string RequireCarrier(AuthenticationTicket ticket)
    {
        if (ticket is null || ticket.Principal.GetType() != typeof(ClaimsPrincipal) ||
            ticket.AuthenticationScheme is not (ServerSessionSchemes.Session or ServerSessionSchemes.Issue) ||
            ticket.Properties.Items.Count != 0 || ticket.Properties.Parameters.Count != 0)
            throw Unsupported();
        var identities = ticket.Principal.Identities.Take(2).ToArray();
        if (identities.Length != 1 || identities[0].GetType() != typeof(ClaimsIdentity))
            throw Unsupported();
        var identity = identities[0];
        var claims = identity.Claims.Take(2).ToArray();
        if (identity.AuthenticationType != ServerSessionSchemes.Session || identity.NameClaimType != ClaimTypes.Name ||
            identity.RoleClaimType != ClaimTypes.Role || identity.Actor is not null ||
            identity.BootstrapContext is not null || identity.Label is not null || claims.Length != 1)
            throw Unsupported();
        var claim = claims[0];
        if (claim.GetType() != typeof(Claim) || claim.Type != SessionIdClaim ||
            claim.ValueType != ClaimValueTypes.String || claim.Issuer != ServerSessionSchemes.Session ||
            claim.OriginalIssuer != ServerSessionSchemes.Session || claim.Properties.Count != 0 ||
            !ReferenceEquals(claim.Subject, identity) || !ServerSessionHandle.TryParse(claim.Value, out _))
            throw Unsupported();
        return claim.Value;
    }

    internal static bool IsToken(string? value, int maximum) => value is { Length: > 0 } &&
        value.Length <= maximum && value.Length % 4 != 1 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    internal static bool IsCanonicalToken(string? value, int maximum)
    {
        if (!IsToken(value, maximum))
            return false;
        byte[]? decoded = null;
        try
        {
            decoded = Decode(value!);
            return string.Equals(Encode(decoded), value, StringComparison.Ordinal);
        }
        catch (FormatException) { return false; }
        finally
        {
            if (decoded is not null)
                CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static bool IsAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            if (value > 127)
                return false;
        return true;
    }

    private static byte[] Decode(string text) => Convert.FromBase64String(
        text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void RequirePurpose(string? purpose)
    {
        if (purpose is not null)
            throw Unsupported();
    }

    private static ServerSessionIdentityException Unsupported() =>
        ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
}
