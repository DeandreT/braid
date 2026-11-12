using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Fixed binary stdin request and closed JSON stdout for the installed Office child. No caller-selected paths or executable fields.</summary>
public static class SpecOfficeProcessProtocol
{
    public const string Version = "weft.office-worker-response.v1";
    public const int HeaderBytes = 84;
    public const int MaximumRequestBytes = HeaderBytes + SpecCapturedSource.MaximumBytes;
    public const int MaximumResponseBytes = SpecCapturedSource.MaximumBytes + 2048;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SpecSourceIdentity ChildIdentity = new(new(new ProjectScope(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222")),
        Guid.Parse("33333333-3333-3333-3333-333333333333")), Guid.Parse("44444444-4444-4444-4444-444444444444"), 1);

    public static byte[] EncodeRequest(SpecCapturedSource source, SpecOfficeExtractionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(limits);
        if (source.Format is not (SpecSourceFormat.Docx or SpecSourceFormat.Xlsx)) throw new SpecOfficeProtocolException();
        var result = new byte[HeaderBytes + source.ByteCount];
        "WEFTOFC1"u8.CopyTo(result);
        var fields = new[] { (int)source.Format, source.ByteCount, limits.MaximumSourceBytes, limits.MaximumEntries, limits.MaximumEntryBytes,
            limits.MaximumExpandedBytes, limits.MaximumCompressionRatio, limits.MaximumXmlDepth, limits.MaximumXmlNodes, limits.MaximumChunks, limits.MaximumEvidenceBytes };
        for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8 + i * 4, 4), fields[i]);
        Convert.FromHexString(source.Digest.Value).CopyTo(result, 52);
        source.CopyBytes().CopyTo(result, HeaderBytes);
        return result;
    }
    internal static (SpecCapturedSource Source, SpecOfficeExtractionLimits Limits) DecodeRequest(byte[] frame)
    {
        if (frame.Length < HeaderBytes || frame.Length > MaximumRequestBytes || !frame.AsSpan(0, 8).SequenceEqual("WEFTOFC1"u8)) throw new SpecOfficeProtocolException();
        int Field(int index) => BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(8 + index * 4, 4));
        if (Field(0) is not ((int)SpecSourceFormat.Docx or (int)SpecSourceFormat.Xlsx) || Field(1) != frame.Length - HeaderBytes) throw new SpecOfficeProtocolException();
        SpecOfficeExtractionLimits limits;
        try { limits = new(Field(2), Field(3), Field(4), Field(5), Field(6), Field(7), Field(8), Field(9), Field(10)); }
        catch (ArgumentException) { throw new SpecOfficeProtocolException(); }
        var source = new SpecCapturedSource(ChildIdentity, (SpecSourceFormat)Field(0), frame.AsSpan(HeaderBytes));
        if (!Convert.FromHexString(source.Digest.Value).AsSpan().SequenceEqual(frame.AsSpan(52, 32))) throw new SpecOfficeProtocolException();
        return (source, limits);
    }
    internal static byte[] EncodeResponse(SpecCapturedSource source, SpecOfficeExtractionResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("format", Version); writer.WriteNumber("extractor", SpecOfficeExtractionResult.ExtractorVersion);
            writer.WriteString("sourceSha256", result.Identity.SourceDigest.Value); writer.WriteString("sourceFormat", result.Identity.Format.ToString());
            writer.WriteNumber("sourceByteCount", source.ByteCount);
            writer.WriteString("profileSha256", result.Identity.ProfileDigest.Value); writer.WriteString("outcome", result.Outcome.ToString());
            writer.WriteString("evidenceSha256", result.Identity.EvidenceDigest.Value);
            writer.WritePropertyName("evidence"); writer.WriteRawValue(result.CopyEvidenceBytes(), skipInputValidation: false); writer.WriteEndObject();
        }
        if (stream.Length > MaximumResponseBytes) throw new SpecOfficeProtocolException();
        return stream.ToArray();
    }
    internal static SpecOfficeExtractionResult DecodeResponse(ReadOnlyMemory<byte> frame, SpecCapturedSource source, SpecOfficeExtractionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(limits);
        if (frame.Length is 0 or > MaximumResponseBytes || source.Format is not (SpecSourceFormat.Docx or SpecSourceFormat.Xlsx)) throw new SpecOfficeProtocolException();
        try
        {
            _ = Utf8.GetCharCount(frame.Span);
            using var json = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (property.Name is not ("format" or "extractor" or "sourceSha256" or "sourceByteCount" or "sourceFormat" or "profileSha256" or "outcome" or "evidenceSha256" or "evidence") || !names.Add(property.Name)) throw new SpecOfficeProtocolException();
            if (names.Count != 9 || root.GetProperty("format").GetString() != Version || root.GetProperty("extractor").GetInt32() != SpecOfficeExtractionResult.ExtractorVersion || root.GetProperty("sourceByteCount").GetInt32() != source.ByteCount ||
                root.GetProperty("sourceSha256").GetString() != source.Digest.Value || root.GetProperty("sourceFormat").GetString() != source.Format.ToString() ||
                root.GetProperty("profileSha256").GetString() != limits.Digest.Value) throw new SpecOfficeProtocolException();
            var outcome = root.GetProperty("outcome").GetString() switch
            {
                "Partial" => SpecExtractionOutcome.Partial,
                "Unsupported" => SpecExtractionOutcome.Unsupported,
                "Malformed" => SpecExtractionOutcome.Malformed,
                "LimitExceeded" => SpecExtractionOutcome.LimitExceeded,
                _ => throw new SpecOfficeProtocolException()
            };
            var evidence = root.GetProperty("evidence");
            if (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() < 2 || evidence.GetArrayLength() > limits.MaximumChunks + 2 ||
                evidence[0].GetString() != "weft.spec-evidence.v2") throw new SpecOfficeProtocolException();
            var code = evidence[1].GetString() ?? throw new SpecOfficeProtocolException();
            if (!AdmitsCode(outcome, source.Format, code) || (outcome != SpecExtractionOutcome.Partial && evidence.GetArrayLength() != 2)) throw new SpecOfficeProtocolException();
            if (source.ByteCount > limits.MaximumSourceBytes && (outcome != SpecExtractionOutcome.LimitExceeded || code != "source-bytes")) throw new SpecOfficeProtocolException();
            var chunks = new List<SpecOfficeEvidenceChunk>();
            var locators = new HashSet<SpecEvidenceLocator>();
            var cells = new HashSet<(string Part, string Sheet, string Cell)>();
            var textBytes = 0;
            foreach (var item in evidence.EnumerateArray().Skip(2))
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 4 || item[0].GetInt32() != chunks.Count) throw new SpecOfficeProtocolException();
                var text = item[1].GetString() ?? throw new SpecOfficeProtocolException();
                var formula = item[3].ValueKind == JsonValueKind.Null ? null : item[3].GetString() ?? throw new SpecOfficeProtocolException();
                textBytes += Utf8.GetByteCount(text) + (formula is null ? 0 : Utf8.GetByteCount(formula));
                if (textBytes > limits.MaximumEvidenceBytes) throw new SpecOfficeProtocolException();
                var locator = item[2];
                if (locator.ValueKind != JsonValueKind.Array || locator.GetArrayLength() != 6) throw new SpecOfficeProtocolException();
                SpecEvidenceLocator typed;
                if (source.Format == SpecSourceFormat.Docx && locator[0].GetString() == "docx-paragraph" && formula is null)
                {
                    int? Nullable(int n) => locator[n].ValueKind == JsonValueKind.Null ? null : locator[n].GetInt32();
                    typed = new SpecEvidenceLocator.WordParagraph(locator[1].GetString()!, locator[2].GetInt32(), Nullable(3), Nullable(4), Nullable(5));
                    var word = (SpecEvidenceLocator.WordParagraph)typed;
                    if (word.Paragraph > limits.MaximumXmlNodes || word.Table > limits.MaximumXmlNodes || word.Row > limits.MaximumXmlNodes || word.Cell > limits.MaximumXmlNodes)
                        throw new SpecOfficeProtocolException();
                }
                else if (source.Format == SpecSourceFormat.Xlsx && locator[0].GetString() == "xlsx-cell")
                {
                    var kind = locator[4].GetString();
                    if (!Enum.TryParse<SpecCellValueKind>(kind, out var valueKind) || !Enum.IsDefined(valueKind) || valueKind.ToString() != kind) throw new SpecOfficeProtocolException();
                    var cached = locator[5].GetBoolean();
                    if ((cached && formula is null) || (formula is not null && formula.Length == 0) || (formula is not null && !cached && text.Length != 0)) throw new SpecOfficeProtocolException();
                    if ((valueKind == SpecCellValueKind.Blank && (text.Length != 0 || cached)) ||
                        (valueKind == SpecCellValueKind.InlineString && formula is not null)) throw new SpecOfficeProtocolException();
                    typed = new SpecEvidenceLocator.SheetCell(locator[1].GetString()!, locator[2].GetString()!, locator[3].GetString()!, valueKind, cached);
                    var cell = (SpecEvidenceLocator.SheetCell)typed;
                    if (!cells.Add((cell.Part, cell.Sheet, cell.Cell)) || cell.Sheet.Any(c => c is '[' or ']' or ':' or '*' or '?' or '/' or '\\')) throw new SpecOfficeProtocolException();
                }
                else throw new SpecOfficeProtocolException();
                if (!locators.Add(typed)) throw new SpecOfficeProtocolException();
                chunks.Add(new(chunks.Count, text, typed, formula));
            }
            var result = new SpecOfficeExtractionResult(source, limits, outcome, code, chunks);
            var exact = Utf8.GetBytes(evidence.GetRawText());
            if (exact.Length > limits.MaximumEvidenceBytes || result.Outcome != outcome || !exact.AsSpan().SequenceEqual(result.CopyEvidenceBytes()) ||
                result.Identity.EvidenceDigest.Value != root.GetProperty("evidenceSha256").GetString()) throw new SpecOfficeProtocolException();
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        { throw new SpecOfficeProtocolException(); }
    }
    private static bool AdmitsCode(SpecExtractionOutcome outcome, SpecSourceFormat format, string code) => outcome switch
    {
        SpecExtractionOutcome.Partial => code == (format == SpecSourceFormat.Docx ? "office-body-text-only" : "office-stored-cells-only"),
        SpecExtractionOutcome.LimitExceeded => code is "source-bytes" or "zip-entries" or "zip-entry-bytes" or "zip-expanded-bytes" or "zip-ratio" or "xml-depth" or "xml-nodes" or "office-chunks" or "evidence-bytes" or "serialized-evidence-bytes",
        SpecExtractionOutcome.Unsupported => code is "legacy-or-encrypted-office" or "unsupported-package-part" or "zip64-unsupported" or "multi-disk-zip" or "encrypted-zip" or "zip-compression-unsupported" or "active-office-content" or "external-relationship" or "unsupported-relationship" or "office-format-mismatch" or "unsupported-word-content" or "unsupported-word-table" or "unsupported-word-cell" or "unsupported-word-paragraph" or "unsupported-word-break" or "unsupported-word-run" or "unsupported-shared-strings" or "unsupported-sheet" or "unsupported-worksheet-feature" or "unsupported-sheet-row" or "unsupported-cell-content" or "unsupported-formula-kind" or "unsupported-cell-type" or "unsupported-rich-string",
        SpecExtractionOutcome.Malformed => code is "invalid-office-zip" or "invalid-office-xml" or "invalid-package-name" or "zip-directory-mismatch" or "zip-content-mismatch" or "invalid-zip-directory" or "duplicate-package-part" or "invalid-local-header" or "conflicting-local-header" or "invalid-data-descriptor" or "overlapping-zip-entries" or "unsafe-package-path" or "missing-package-part" or "invalid-relationships" or "duplicate-relationship" or "invalid-content-types" or "missing-office-relationship" or "invalid-office-relationship" or "invalid-word-document" or "invalid-word-body" or "word-cell-without-paragraph" or "invalid-word-text" or "invalid-workbook" or "invalid-workbook-sheets" or "duplicate-shared-strings" or "invalid-shared-strings" or "invalid-sheet-identity" or "invalid-sheet-relationship" or "invalid-worksheet" or "invalid-sheet-data" or "invalid-sheet-row" or "invalid-cell-address" or "cell-row-mismatch" or "invalid-cell-value" or "invalid-shared-string-index" or "invalid-inline-string" or "invalid-numeric-cell" or "invalid-boolean-cell" or "invalid-error-cell" or "invalid-date-cell",
        _ => false
    };
    public static async Task<byte[]> ReadBoundedAsync(Stream input, int maximumBytes, CancellationToken token)
    {
        if (maximumBytes is < 1 or > MaximumResponseBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes - (int)output.Length + 1)), token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximumBytes) throw new SpecOfficeProtocolException();
            output.Write(buffer, 0, count);
        }
    }
}

public sealed class SpecOfficeProtocolException : Exception
{
    public SpecOfficeProtocolException() : base("Office worker protocol could not be admitted.") { }
}
