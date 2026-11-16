using System.Globalization;
using System.Text;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// Local, deterministic evidence extraction only. Documents are untrusted; no instructions are executed and no
/// references (including local OpenAPI references) are expanded. OpenAPI support is structural extraction of 3.0/3.1 JSON,
/// not specification validation. Cancellation and I/O failures remain caller-visible rather than becoming malformed data.
/// </summary>
public static class SpecSourceExtractor
{
    public const int Version = 2;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static SpecExtractionResult Extract(SpecCapturedSource source, SpecExtractionLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        SpecExtractionResult Failure(SpecExtractionOutcome outcome, string code) => new(source, limits, outcome, code, []);
        if (source.ByteCount > limits.MaximumSourceBytes) return Failure(SpecExtractionOutcome.LimitExceeded, "source-bytes");
        if (source.Format == SpecSourceFormat.Unsupported) return Failure(SpecExtractionOutcome.Unsupported, "unsupported-format");
        // Office requires a separately qualified child worker; never parse ZIP/XML in the API's synchronous path.
        if (source.Format is SpecSourceFormat.Docx or SpecSourceFormat.Xlsx)
            return Failure(SpecExtractionOutcome.Unsupported, "office-worker-required");
        if (source.Format == SpecSourceFormat.Pdf)
            return Failure(SpecExtractionOutcome.Unsupported, "pdf-worker-required");
        var bytes = source.CopyBytes();
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        try
        {
            // Validate UTF-8 without allocating an unbounded decoded document.
            _ = Utf8.GetCharCount(bytes);
            var extraction = new Extraction(bytes, offset, limits, cancellationToken);
            if (source.Format == SpecSourceFormat.Text) extraction.Text();
            else extraction.Json(source.Format == SpecSourceFormat.OpenApiJson);
            cancellationToken.ThrowIfCancellationRequested();
            return new(source, limits, SpecExtractionOutcome.Complete,
                source.Format == SpecSourceFormat.OpenApiJson ? "openapi-json-structural-only" : "complete", extraction.Chunks);
        }
        catch (ExtractionRefusal refusal) { return Failure(refusal.Outcome, refusal.Code); }
        catch (DecoderFallbackException) { return Failure(SpecExtractionOutcome.Malformed, "invalid-utf8"); }
        catch (JsonException) { return Failure(SpecExtractionOutcome.Malformed, "invalid-json"); }
        catch (InvalidOperationException) { return Failure(SpecExtractionOutcome.Malformed, "invalid-json-string"); }
    }

    private sealed class ExtractionRefusal(SpecExtractionOutcome outcome, string code) : Exception
    {
        internal SpecExtractionOutcome Outcome { get; } = outcome;
        internal string Code { get; } = code;
    }

    private sealed class Extraction(byte[] bytes, int offset, SpecExtractionLimits limits, CancellationToken token)
    {
        internal List<SpecEvidenceChunk> Chunks { get; } = [];
        private int evidenceBytes;
        private int tokens;
        private int lineCursor;
        private int line = 1;
        private string? openApiVersion;
        private bool openApiVersionWrongType;

        private static void Limit(string code) => throw new ExtractionRefusal(SpecExtractionOutcome.LimitExceeded, code);
        private static void Malformed(string code) => throw new ExtractionRefusal(SpecExtractionOutcome.Malformed, code);

        internal void Text()
        {
            var start = offset;
            for (var i = offset; i < bytes.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i - start > limits.MaximumEvidenceBytes) Limit("evidence-bytes");
                if (bytes[i] is not (13 or 10)) continue;
                Add(start, i - start, null);
                if (bytes[i] == 13 && i + 1 < bytes.Length && bytes[i + 1] == 10) i++;
                start = i + 1;
            }
            // A terminal line ending does not invent another empty line; an empty document has no chunks.
            if (start < bytes.Length) Add(start, bytes.Length - start, null);
        }

        internal void Json(bool openApi)
        {
            var reader = new Utf8JsonReader(bytes.AsSpan(offset), new JsonReaderOptions { MaxDepth = 65 });
            if (!Read(ref reader)) Malformed("invalid-json");
            var rootObject = reader.TokenType == JsonTokenType.StartObject;
            Value(ref reader, "", 0);
            if (Read(ref reader)) Malformed("invalid-json");
            if (!openApi) return;
            if (!rootObject || openApiVersion is null || openApiVersionWrongType) Malformed("invalid-openapi-header");
            var version = openApiVersion!.Split('.');
            if (version.Length != 3 || version[0] != "3" || version[1] is not ("0" or "1") ||
                version[2].Length == 0 || !version[2].All(char.IsAsciiDigit))
                throw new ExtractionRefusal(SpecExtractionOutcome.Unsupported, "unsupported-openapi-version");
        }

        private bool Read(ref Utf8JsonReader reader)
        {
            token.ThrowIfCancellationRequested();
            if (!reader.Read()) return false;
            if (++tokens > limits.MaximumTokens) Limit("json-tokens");
            if (reader.BytesConsumed - reader.TokenStartIndex > limits.MaximumEvidenceBytes) Limit("evidence-bytes");
            return true;
        }

        private void Value(ref Utf8JsonReader reader, string pointer, int depth)
        {
            var kind = reader.TokenType;
            var container = kind is JsonTokenType.StartObject or JsonTokenType.StartArray;
            if (container && depth >= limits.MaximumDepth) Limit("json-depth");
            // GetString also checks escaped surrogate validity; raw lexical text remains the actual evidence.
            if (kind == JsonTokenType.String) _ = reader.GetString();
            if (pointer == "/openapi")
            {
                if (kind == JsonTokenType.String) openApiVersion = reader.GetString();
                else openApiVersionWrongType = true;
            }
            Add(offset + (int)reader.TokenStartIndex, (int)(reader.BytesConsumed - reader.TokenStartIndex), pointer);
            if (!container) return;
            var names = kind == JsonTokenType.StartObject ? new HashSet<string>(StringComparer.Ordinal) : null;
            var index = 0;
            while (Read(ref reader))
            {
                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) return;
                string part;
                if (names is not null)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) Malformed("invalid-json");
                    var name = reader.GetString()!;
                    if (!names.Add(name)) Malformed("duplicate-json-property");
                    part = name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                    if (!Read(ref reader)) Malformed("invalid-json");
                }
                else part = (index++).ToString(CultureInfo.InvariantCulture);
                // Bound path growth before concatenation, including deeply nested property names.
                if (pointer.Length + part.Length + 1 > limits.MaximumEvidenceBytes) Limit("evidence-bytes");
                Value(ref reader, pointer + "/" + part, depth + 1);
            }
            Malformed("invalid-json");
        }

        private void Add(int start, int length, string? pointer)
        {
            token.ThrowIfCancellationRequested();
            if (Chunks.Count == limits.MaximumChunks) Limit("chunks");
            // Charge fixed citation overhead as well as text and pointer bytes, so tiny/empty values are bounded too.
            var cost = (long)length + (pointer is null ? 0 : Utf8.GetByteCount(pointer)) + 64;
            if (cost > limits.MaximumEvidenceBytes - evidenceBytes) Limit("evidence-bytes");
            evidenceBytes += (int)cost;
            var startLine = LineAt(start);
            var endLine = LineAt(start + Math.Max(0, length - 1));
            Chunks.Add(new(Chunks.Count, Utf8.GetString(bytes.AsSpan(start, length)), pointer, start, length, startLine, endLine));
        }

        private int LineAt(int position)
        {
            while (lineCursor < position)
            {
                if (bytes[lineCursor] == 13 || bytes[lineCursor] == 10 && (lineCursor == 0 || bytes[lineCursor - 1] != 13)) line++;
                lineCursor++;
            }
            return line;
        }
    }
}
