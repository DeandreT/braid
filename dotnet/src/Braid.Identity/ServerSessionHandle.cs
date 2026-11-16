using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Braid.Domain;

namespace Braid.Identity;

internal sealed class ServerSessionHandle
{
    private const string Prefix = "bs1.";
    private readonly string key;

    private ServerSessionHandle(string key, Sha256Digest digest)
    {
        this.key = key;
        Digest = digest;
    }

    [JsonIgnore]
    internal Sha256Digest Digest { get; }

    internal static ServerSessionHandle Create()
    {
        var random = RandomNumberGenerator.GetBytes(32);
        try { return new(Prefix + Encode(random), Sha256Digest.FromBytes(random)); }
        finally { CryptographicOperations.ZeroMemory(random); }
    }

    internal static bool TryParse(string? key, [NotNullWhen(true)] out ServerSessionHandle? handle)
    {
        handle = null;
        if (key is null || key.Length != 47 || !key.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        foreach (var value in key.AsSpan(Prefix.Length))
            if (!(char.IsAsciiLetterOrDigit(value) || value is '-' or '_'))
                return false;

        byte[]? random = null;
        try
        {
            random = Convert.FromBase64String(key[Prefix.Length..].Replace('-', '+').Replace('_', '/') + "=");
            if (random.Length != 32 || !string.Equals(Prefix + Encode(random), key, StringComparison.Ordinal))
                return false;
            handle = new(key, Sha256Digest.FromBytes(random));
            return true;
        }
        catch (FormatException) { return false; }
        finally
        {
            if (random is not null)
                CryptographicOperations.ZeroMemory(random);
        }
    }

    internal string ExportKey() => key;
    public override string ToString() => nameof(ServerSessionHandle);

    private static string Encode(ReadOnlySpan<byte> random) =>
        Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
