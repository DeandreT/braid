using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weft.Application;

namespace Weft.DatabaseTool;

/// <summary>One exact, bounded binary stdin key. No command line/environment key or JSON retention.</summary>
internal static class ExternalIdentityOwnerInput
{
    internal const int MaximumInputBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<ExternalIdentityKey> ReadAsync(Stream input, CancellationToken cancellationToken)
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
            return Parse(buffer.AsSpan(0, count));
        }
        catch (IOException)
        { throw InvalidInput(); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static ExternalIdentityKey Parse(ReadOnlySpan<byte> bytes)
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
            string? provider = null, authority = null, subject = null;
            var seen = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw InvalidInput();
                var name = String(ref reader);
                var bit = name switch { "provider" => 1, "authority" => 2, "subject" => 4, _ => throw InvalidInput() };
                if ((seen & bit) != 0 || !reader.Read() || reader.TokenType != JsonTokenType.String) throw InvalidInput();
                var value = String(ref reader);
                seen |= bit;
                switch (bit)
                {
                    case 1: provider = value; break;
                    case 2: authority = value; break;
                    case 4: subject = value; break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seen != 7 || reader.Read()) throw InvalidInput();
            return new ExternalIdentityKey(provider!, authority!, subject!);
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
