using System.Text;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

public sealed partial class CreationEvidenceDocument
{
    /// <summary>Trusted retention boundary only. The caller supplies the stored identity pin,
    /// not an identity reconstructed from current defaults. Does not execute source parsers.</summary>
    internal static CreationEvidenceDocument RestoreRetained(SpecCapturedSource source, SpecExtractionIdentity identity, byte[] canonicalEvidence)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(canonicalEvidence);
        if (source.Identity != identity.Source || source.Digest != identity.SourceDigest || source.Format != identity.Format ||
            canonicalEvidence.Length > SpecCapturedSource.MaximumBytes || Sha256Digest.FromBytes(canonicalEvidence) != identity.EvidenceDigest)
            throw new ArgumentException("Substituted retained evidence identity.");
        if (identity.Format is SpecSourceFormat.Text or SpecSourceFormat.Json or SpecSourceFormat.OpenApiJson)
        {
            if (identity.ExtractorVersion != SpecSourceExtractor.Version)
                throw new ArgumentException("Unsupported retained byte extraction.");
            var result = new SpecExtractionResult(identity, canonicalEvidence);
            // Historical v2 extraction charges unescaped source bytes, UTF-8 pointer
            // bytes and 64 bytes per chunk. Its separate canonical serializer allows
            // 10 MiB because JSON escaping can expand the decoded evidence budget.
            var decodedCost = result.Chunks.Sum(c => (long)c.ByteLength +
                (c.JsonPointer is null ? 0 : Encoding.UTF8.GetByteCount(c.JsonPointer)) + 64);
            if (decodedCost > 2 * 1024 * 1024)
                throw new ArgumentException("Retained byte evidence exceeds its decoded budget.");
            if (result.Code != (identity.Format == SpecSourceFormat.OpenApiJson ? "openapi-json-structural-only" : "complete") ||
                result.Chunks.Any(c => (long)c.ByteOffset + c.ByteLength > source.ByteCount ||
                    (identity.Format == SpecSourceFormat.Text ? c.JsonPointer is not null : c.JsonPointer is null)))
                throw new ArgumentException("Invalid retained byte evidence.");
            return From(result);
        }
        var pdf = identity.Format == SpecSourceFormat.Pdf;
        if (identity.Outcome != SpecExtractionOutcome.Partial ||
            (pdf ? identity.ExtractorVersion != SpecPdfExtractionResult.ExtractorVersion || source.ByteCount > SpecPdfWorkerResponse.MaximumSourceBytes || canonicalEvidence.Length > 8 * 1024 * 1024
                 : identity.Format is not (SpecSourceFormat.Docx or SpecSourceFormat.Xlsx) || identity.ExtractorVersion != SpecOfficeExtractionResult.ExtractorVersion))
            throw new ArgumentException("Unsupported retained structural extraction.");
        using var document = JsonDocument.Parse(canonicalEvidence, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        var code = pdf ? "pdf-page-text-only" : identity.Format == SpecSourceFormat.Docx ? "office-body-text-only" : "office-stored-cells-only";
        var maximumChunks = pdf ? SpecPdfWorkerResponse.MaximumPages : 10_000;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2 || root.GetArrayLength() > maximumChunks + 2 ||
            root[0].GetString() != "braid.spec-evidence.v2" || root[1].GetString() != code)
            throw new ArgumentException("Invalid retained structural envelope.");
        var chunks = new List<CreationEvidenceChunk>();
        var locators = new HashSet<SpecEvidenceLocator>();
        var cells = new HashSet<(string Part, string Sheet, string Cell)>();
        var textBytes = 0;
        foreach (var item in root.EnumerateArray().Skip(2))
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != (pdf ? 5 : 4) || item[0].GetInt32() != chunks.Count)
                throw new ArgumentException("Invalid retained chunk ordinal or shape.");
            var text = item[1].GetString() ?? throw new ArgumentException("Missing retained text.");
            var formula = item[3].GetString();
            textBytes += Encoding.UTF8.GetByteCount(text) + (formula is null ? 0 : Encoding.UTF8.GetByteCount(formula));
            var locator = item[2];
            if (locator.ValueKind != JsonValueKind.Array || locator.GetArrayLength() != (pdf ? 2 : 6))
                throw new ArgumentException("Invalid retained locator shape.");
            if (pdf)
            {
                var missing = item[4].GetBoolean();
                if (locator[0].GetString() != "pdf-page" || locator[1].GetInt32() != chunks.Count + 1 || formula is not null ||
                    missing != string.IsNullOrWhiteSpace(text) || textBytes > SpecPdfWorkerResponse.MaximumTextBytes)
                    throw new ArgumentException("Invalid retained PDF page facts.");
                chunks.Add(CreationEvidenceChunk.From(new SpecPdfEvidenceChunk(chunks.Count, new(chunks.Count + 1, text, missing))));
                continue;
            }
            SpecEvidenceLocator typed;
            if (identity.Format == SpecSourceFormat.Docx && locator[0].GetString() == "docx-paragraph" && formula is null)
            {
                int? Nullable(int n) => locator[n].ValueKind == JsonValueKind.Null ? null : locator[n].GetInt32();
                var word = new SpecEvidenceLocator.WordParagraph(locator[1].GetString()!, locator[2].GetInt32(), Nullable(3), Nullable(4), Nullable(5));
                if (word.Paragraph > 100_000 || word.Table > 100_000 || word.Row > 100_000 || word.Cell > 100_000)
                    throw new ArgumentException("Invalid retained paragraph bounds.");
                typed = word;
            }
            else if (identity.Format == SpecSourceFormat.Xlsx && locator[0].GetString() == "xlsx-cell")
            {
                var kind = locator[4].GetString();
                if (!Enum.TryParse<SpecCellValueKind>(kind, out var valueKind) || !Enum.IsDefined(valueKind) || valueKind.ToString() != kind)
                    throw new ArgumentException("Invalid retained cell kind.");
                var cached = locator[5].GetBoolean();
                if ((cached && formula is null) || (formula is not null && formula.Length == 0) || (formula is not null && !cached && text.Length != 0) ||
                    (valueKind == SpecCellValueKind.Blank && (text.Length != 0 || cached)) || (valueKind == SpecCellValueKind.InlineString && formula is not null))
                    throw new ArgumentException("Invalid retained formula/cache facts.");
                var cell = new SpecEvidenceLocator.SheetCell(locator[1].GetString()!, locator[2].GetString()!, locator[3].GetString()!, valueKind, cached);
                if (!cells.Add((cell.Part, cell.Sheet, cell.Cell)) || cell.Sheet.Any(c => c is '[' or ']' or ':' or '*' or '?' or '/' or '\\'))
                    throw new ArgumentException("Invalid retained cell identity.");
                typed = cell;
            }
            else throw new ArgumentException("Invalid retained locator family.");
            if (!locators.Add(typed)) throw new ArgumentException("Duplicate retained locator.");
            chunks.Add(CreationEvidenceChunk.From(new SpecOfficeEvidenceChunk(chunks.Count, text, typed, formula)));
        }
        if (pdf && !chunks.Any(c => !c.MissingText)) throw new ArgumentException("Unreadable PDF cannot be retained as Partial.");
        // Exact serialization rejects whitespace, alternate escaping, extra fields and noncanonical numbers.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray(); writer.WriteStringValue("braid.spec-evidence.v2"); writer.WriteStringValue(code);
            foreach (var chunk in chunks)
            {
                writer.WriteStartArray(); writer.WriteNumberValue(chunk.Ordinal); writer.WriteStringValue(chunk.Text);
                SpecEvidenceLocatorJson.Write(writer, chunk.Locator); writer.WriteStringValue(chunk.Formula);
                if (pdf) writer.WriteBooleanValue(chunk.MissingText);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        if (!stream.ToArray().AsSpan().SequenceEqual(canonicalEvidence)) throw new ArgumentException("Noncanonical retained evidence.");
        return new(identity, code, canonicalEvidence.ToArray(), chunks, true,
            pdf ? PdfCoverage : identity.Format == SpecSourceFormat.Docx ? WordCoverage : WorkbookCoverage);
    }
}
