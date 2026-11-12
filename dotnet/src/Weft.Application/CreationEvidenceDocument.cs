using Weft.Domain;

namespace Weft.Application;

/// <summary>Closed adaptation of trusted extractor results, never a model/JSON evidence constructor.
/// Retains the extractor's exact canonical bytes and identity without changing its outcome.</summary>
public sealed class CreationEvidenceDocument
{
    private readonly byte[] evidence;
    private readonly bool admitted;
    private readonly string? coverage;

    private CreationEvidenceDocument(SpecExtractionIdentity identity, string code, byte[] evidence,
        IEnumerable<CreationEvidenceChunk> chunks, bool admitted, string? coverage)
    {
        if (Sha256Digest.FromBytes(evidence) != identity.EvidenceDigest)
            throw new ArgumentException("Extraction bytes must match their exact identity.");
        Identity = identity; Code = code; this.evidence = evidence;
        Chunks = Array.AsReadOnly(chunks.ToArray()); this.admitted = admitted; this.coverage = coverage;
    }

    public static CreationEvidenceDocument From(SpecExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var supported = result.Outcome == SpecExtractionOutcome.Complete &&
            result.Identity.Format is SpecSourceFormat.Text or SpecSourceFormat.Json or SpecSourceFormat.OpenApiJson;
        return new(result.Identity, result.Code, result.CopyEvidenceBytes(), result.Chunks.Select(CreationEvidenceChunk.From), supported, null);
    }

    public static CreationEvidenceDocument From(SpecOfficeExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var coverage = (result.Outcome, result.Identity.Format, result.Code) switch
        {
            (SpecExtractionOutcome.Partial, SpecSourceFormat.Docx, "office-body-text-only") => WordCoverage,
            (SpecExtractionOutcome.Partial, SpecSourceFormat.Xlsx, "office-stored-cells-only") => WorkbookCoverage,
            _ => null
        };
        return new(result.Identity, result.Code, result.CopyEvidenceBytes(), result.Chunks.Select(CreationEvidenceChunk.From), coverage is not null, coverage);
    }

    private const string WordCoverage = "This Word extraction contains only supported body paragraphs and table-cell text. It does not establish complete document coverage, rendered layout, headers, footers, drawings or page locations.";
    private const string WorkbookCoverage = "This workbook extraction contains only stored cells. Formulas are literal source text and are not evaluated; cached values may be missing or stale. It does not establish complete workbook coverage or calculated results.";

    public static CreationEvidenceDocument From(SpecPdfExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var coverage = result.Outcome == SpecExtractionOutcome.Partial &&
            result.Identity.Format == SpecSourceFormat.Pdf && result.Code == "pdf-page-text-only"
            ? PdfCoverage : null;
        return new(result.Identity, result.Code, result.CopyEvidenceBytes(), result.Chunks.Select(CreationEvidenceChunk.From), coverage is not null, coverage);
    }

    private const string PdfCoverage = "This PDF extraction contains only page text. Text or page content may be missing, including pages without usable text. OCR was not performed. It does not establish complete document coverage or rendered layout.";

    public SpecExtractionIdentity Identity { get; }
    public SpecExtractionOutcome Outcome => Identity.Outcome;
    public string Code { get; }
    public IReadOnlyList<CreationEvidenceChunk> Chunks { get; }
    public byte[] CopyEvidenceBytes() => (byte[])evidence.Clone();
    internal int EvidenceByteCount => evidence.Length;
    internal bool SupportsGeneration => admitted;
    internal string? Coverage => coverage;
    public override string ToString() => nameof(CreationEvidenceDocument);
}

/// <summary>Immutable common view; locators and literal formula text are retained, not inferred.</summary>
public sealed class CreationEvidenceChunk
{
    private CreationEvidenceChunk(int ordinal, string text, SpecEvidenceLocator locator, string? formula, bool missingText)
    { Ordinal = ordinal; Text = text; Locator = locator; Formula = formula; MissingText = missingText; }
    internal static CreationEvidenceChunk From(SpecEvidenceChunk chunk) => new(chunk.Ordinal, chunk.Text, chunk.Locator, null, false);
    internal static CreationEvidenceChunk From(SpecOfficeEvidenceChunk chunk) => new(chunk.Ordinal, chunk.Text, chunk.Locator, chunk.Formula, false);
    internal static CreationEvidenceChunk From(SpecPdfEvidenceChunk chunk) => new(chunk.Ordinal, chunk.Text, chunk.Locator, chunk.Formula, chunk.MissingText);
    public int Ordinal { get; }
    public string Text { get; }
    public SpecEvidenceLocator Locator { get; }
    public string? Formula { get; }
    /// <summary>Explicit page missing-text fact; false for byte/Office evidence, which has no such page flag.</summary>
    public bool MissingText { get; }
    public override string ToString() => nameof(CreationEvidenceChunk);
}
