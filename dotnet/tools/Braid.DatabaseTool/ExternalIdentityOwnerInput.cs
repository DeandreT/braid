using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Braid.Application;

namespace Braid.DatabaseTool;

/// <summary>One exact, bounded binary stdin key. No command line/environment key or JSON retention.</summary>
internal static class ExternalIdentityOwnerInput
{
    internal const int MaximumInputBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<ExternalIdentityKey> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var members = await ReadMembersAsync(input, ["provider", "authority", "subject"], cancellationToken);
        try { return new ExternalIdentityKey(members[0], members[1], members[2]); }
        catch (ArgumentException) { throw InvalidInput(); }
    }

    /// <summary>
    /// The tenant-stable Entra user key from exactly <c>tenantId</c> and <c>objectId</c>, each a canonical
    /// nonzero lowercase <c>D</c>-format UUID. The provider and key layout are fixed, never supplied.
    /// </summary>
    internal static async Task<ExternalIdentityKey> ReadEntraUserAsync(Stream input, CancellationToken cancellationToken)
    {
        var members = await ReadMembersAsync(input, ["tenantId", "objectId"], cancellationToken);
        try { return EntraUserIdentity.Key(Identifier(members[0]), Identifier(members[1])); }
        catch (ArgumentException) { throw InvalidInput(); }
    }

    private static Guid Identifier(string value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && string.Equals(value, id.ToString("D"), StringComparison.Ordinal)
            ? id : throw InvalidInput();

    private static async Task<string[]> ReadMembersAsync(Stream input, string[] names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var buffer = new byte[MaximumInputBytes + 1];
        try
        {
            var count = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken);
                if (read == 0) break;
                count += read;
                if (count > MaximumInputBytes) throw InvalidInput();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Parse(buffer.AsSpan(0, count), names);
        }
        catch (IOException)
        { throw InvalidInput(); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static string[] Parse(ReadOnlySpan<byte> bytes, string[] names)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2
            });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw InvalidInput();
            var values = new string?[names.Length];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw InvalidInput();
                var index = Array.IndexOf(names, String(ref reader));
                if (index < 0 || values[index] is not null || !reader.Read() || reader.TokenType != JsonTokenType.String)
                    throw InvalidInput();
                values[index] = String(ref reader);
            }
            if (reader.TokenType != JsonTokenType.EndObject || values.Any(value => value is null) || reader.Read()) throw InvalidInput();
            return values!;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            // Do not preserve parser/decoder diagnostics, rejected text, byte positions or inner exceptions.
            throw InvalidInput();
        }
    }

    private static string String(ref Utf8JsonReader reader)
    {
        // Valid raw UTF8 does not prove the legality of JSON's escaped UTF16 code units.
        // Validate them before GetString can choose any replacement behavior.
        var raw = reader.ValueSpan;
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] != (byte)'\\') continue;
            if (++index >= raw.Length) throw InvalidInput();
            if (raw[index] != (byte)'u') continue; // The JSON reader validates the other escape spellings.
            var unit = Hex(raw, index + 1);
            index += 4;
            if (unit is >= 0xdc00 and <= 0xdfff) throw InvalidInput();
            if (unit is not (>= 0xd800 and <= 0xdbff)) continue;
            if (index + 6 >= raw.Length || raw[index + 1] != (byte)'\\' || raw[index + 2] != (byte)'u')
                throw InvalidInput();
            var low = Hex(raw, index + 3);
            if (low is not (>= 0xdc00 and <= 0xdfff)) throw InvalidInput();
            index += 6;
        }
        return reader.GetString() ?? throw InvalidInput();
    }

    private static int Hex(ReadOnlySpan<byte> bytes, int start)
    {
        if (start < 0 || start + 4 > bytes.Length) throw InvalidInput();
        var value = 0;
        for (var index = 0; index < 4; index++)
        {
            var digit = bytes[start + index];
            value = (value << 4) | (digit switch
            {
                >= (byte)'0' and <= (byte)'9' => digit - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => digit - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => digit - (byte)'A' + 10,
                _ => throw InvalidInput()
            });
        }
        return value;
    }

    private static ArgumentException InvalidInput()
        => new("The external identity stdin input is invalid.");
}
