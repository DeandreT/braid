using System.Security.Cryptography;

namespace Weft.Domain;

public sealed record Sha256Digest
{
    private Sha256Digest(string value) => Value = value;

    public string Value { get; }

    public static Sha256Digest Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A SHA-256 digest must contain exactly 64 hexadecimal characters.", nameof(value));

        return new Sha256Digest(value.ToLowerInvariant());
    }

    public static Sha256Digest FromBytes(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    public override string ToString() => Value;
}
