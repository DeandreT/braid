using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

internal sealed partial class RevisionReportEnvelopeCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static byte[] CopyBytes(ReadOnlyMemory<byte> bytes, int maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > maximumBytes) throw ResourceLimit();
        var owned = new byte[bytes.Length];
        for (var offset = 0; offset < owned.Length; offset += 4096)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(4096, owned.Length - offset);
            bytes.Span.Slice(offset, count).CopyTo(owned.AsSpan(offset, count));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return owned;
    }

    private static Sha256Digest Digest(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < bytes.Length; offset += 4096)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(bytes.Slice(offset, Math.Min(4096, bytes.Length - offset)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Sha256Digest.Parse(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void ValidateUtf8(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) throw Integrity();
        var decoder = StrictUtf8.GetDecoder();
        Span<char> characters = stackalloc char[4096];
        try
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(4096, bytes.Length - offset);
                decoder.Convert(bytes.Slice(offset, count), characters, offset + count == bytes.Length,
                    out var used, out _, out _);
                if (used == 0) throw Integrity();
                offset += used;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (DecoderFallbackException) { throw Integrity(); }
    }

    private static void Preflight(byte[] bytes, RevisionReportPolicy caps, CancellationToken cancellationToken)
    {
        ValidateUtf8(bytes, cancellationToken);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = RevisionReportPolicy.MaximumJsonDepthLimit + 2
        });
        var objects = new Stack<HashSet<string>>();
        var reportPending = false;
        var insideReport = false;
        var reportFound = false;
        long reportStart = 0;
        long reportBytes = 0;
        var reportDepth = 0;
        var reportItems = 0;
        var rootValues = 0;
        try
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.CurrentDepth == 0 && reader.TokenType is not JsonTokenType.EndObject and not JsonTokenType.EndArray)
                    rootValues++;
                if (reportPending)
                {
                    reportPending = false;
                    insideReport = true;
                    reportFound = true;
                    reportStart = reader.TokenStartIndex;
                    reportDepth = reader.CurrentDepth;
                }
                if (insideReport)
                {
                    if (reader.TokenType is not JsonTokenType.PropertyName and not JsonTokenType.EndObject and not JsonTokenType.EndArray)
                    {
                        if (++reportItems > caps.ResponseItemLimit) throw ResourceLimit();
                        var depth = reader.CurrentDepth - reportDepth +
                            (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray ? 1 : 0);
                        if (depth > caps.JsonDepthLimit) throw ResourceLimit();
                    }
                }
                else if (reader.CurrentDepth > RevisionReportPolicy.MaximumJsonDepthLimit)
                    throw ResourceLimit();

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        objects.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        {
                            var name = ReadTokenText(ref reader, 128);
                            if (!objects.Peek().Add(name)) throw Integrity();
                            if (reader.CurrentDepth == 1 && name == "report") reportPending = true;
                            break;
                        }
                    case JsonTokenType.String:
                        _ = ReadTokenText(ref reader, RevisionReportPolicy.MaximumTextByteLimit);
                        break;
                    case JsonTokenType.Number:
                        if (reader.ValueSpan.Length > 20) throw ResourceLimit();
                        break;
                }
                if (insideReport && reader.CurrentDepth == reportDepth &&
                    reader.TokenType is not JsonTokenType.StartObject and not JsonTokenType.StartArray)
                {
                    reportBytes = reader.BytesConsumed - reportStart;
                    insideReport = false;
                }
                var currentlyExcluded = insideReport ? reader.BytesConsumed - reportStart : reportBytes;
                if (reader.BytesConsumed - currentlyExcluded > caps.HeaderByteLimit) throw ResourceLimit();
            }
            if (rootValues != 1 || !reportFound || insideReport || objects.Count != 0) throw Integrity();
            if (bytes.LongLength - reportBytes > caps.HeaderByteLimit) throw ResourceLimit();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (JsonException) { throw Integrity(); }
        catch (InvalidOperationException) { throw Integrity(); }
    }

    private static string ReadTokenText(ref Utf8JsonReader reader, int maximumBytes)
    {
        // Escaping can expand an ASCII character to six raw bytes. Refuse before decoding/allocation.
        if (reader.ValueSpan.Length > maximumBytes * 6) throw ResourceLimit();
        var raw = reader.ValueSpan;
        if (reader.ValueIsEscaped)
        {
            for (var index = 0; index < raw.Length; index++)
            {
                if (raw[index] != '\\') continue;
                if (++index >= raw.Length) throw Integrity();
                if (raw[index] != 'u') continue;
                var scalar = HexQuad(raw, index + 1);
                index += 4;
                if (scalar is >= 0xdc00 and <= 0xdfff) throw Integrity();
                if (scalar is >= 0xd800 and <= 0xdbff)
                {
                    if (index + 6 >= raw.Length || raw[index + 1] != '\\' || raw[index + 2] != 'u') throw Integrity();
                    var low = HexQuad(raw, index + 3);
                    if (low is < 0xdc00 or > 0xdfff) throw Integrity();
                    index += 6;
                }
            }
        }
        var value = reader.GetString() ?? throw Integrity();
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes) throw ResourceLimit();
            return value;
        }
        catch (EncoderFallbackException) { throw Integrity(); }
    }

    private static int HexQuad(ReadOnlySpan<byte> raw, int start)
    {
        if (start + 4 > raw.Length) throw Integrity();
        var value = 0;
        for (var index = start; index < start + 4; index++)
        {
            var digit = raw[index] switch
            {
                >= (byte)'0' and <= (byte)'9' => raw[index] - '0',
                >= (byte)'a' and <= (byte)'f' => raw[index] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => raw[index] - 'A' + 10,
                _ => throw Integrity()
            };
            value = (value << 4) | digit;
        }
        return value;
    }
}
