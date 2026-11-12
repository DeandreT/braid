using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Trusted installation profile. The approved executable digest must come from deployment, not its own measurement.</summary>
public sealed class SpecPdfExtractionProfile
{
    public const int CpuSeconds = 10;
    public const long MemoryBytes = 536_870_912;
    public const int OpenFiles = 64;
    public const int WallSeconds = 10;
    public SpecPdfExtractionProfile(Sha256Digest executableDigest, int maximumEvidenceBytes = 8 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(executableDigest);
        if (maximumEvidenceBytes is < 256 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumEvidenceBytes));
        ExecutableDigest = executableDigest; MaximumEvidenceBytes = maximumEvidenceBytes;
        Digest = Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new object[] {
            "weft.pdf-native-profile.v1", executableDigest.Value, SpecPdfWorkerResponse.Format, SpecPdfWorkerResponse.Extractor,
            "native-self-sandbox.v1", CpuSeconds, MemoryBytes, OpenFiles, WallSeconds,
            SpecPdfWorkerResponse.MaximumSourceBytes, SpecPdfWorkerResponse.MaximumResponseBytes,
            SpecPdfWorkerResponse.MaximumTextBytes, SpecPdfWorkerResponse.MaximumPages, maximumEvidenceBytes }));
    }
    public Sha256Digest ExecutableDigest { get; }
    public int MaximumEvidenceBytes { get; }
    public Sha256Digest Digest { get; }
}

public interface ISpecPdfExtractionWorker
{
    Task<SpecPdfExtractionResult> ExtractAsync(SpecCapturedSource source, CancellationToken cancellationToken);
}

/// <summary>Canonical v2 PDF page evidence; blank pages and missing-text flags are retained in page order.</summary>
public sealed class SpecPdfExtractionResult
{
    public const int ExtractorVersion = 4;
    private readonly byte[] bytes;
    private SpecPdfExtractionResult(SpecCapturedSource source, SpecPdfExtractionProfile profile, SpecPdfWorkerResponse response)
    {
        var outcome = response.Status switch
        {
            SpecPdfWorkerStatus.Partial => SpecExtractionOutcome.Partial,
            SpecPdfWorkerStatus.NeedsOcr or SpecPdfWorkerStatus.Encrypted => SpecExtractionOutcome.Unsupported,
            SpecPdfWorkerStatus.Malformed => SpecExtractionOutcome.Malformed,
            SpecPdfWorkerStatus.LimitExceeded => SpecExtractionOutcome.LimitExceeded,
            _ => throw new SpecPdfWorkerProtocolException()
        };
        var code = response.Status switch
        {
            SpecPdfWorkerStatus.Partial => "pdf-page-text-only",
            SpecPdfWorkerStatus.NeedsOcr => "pdf-needs-ocr",
            SpecPdfWorkerStatus.Encrypted => "pdf-encrypted",
            SpecPdfWorkerStatus.Malformed => "pdf-malformed",
            _ => "pdf-limit-exceeded"
        };
        IReadOnlyList<SpecPdfWorkerPage> pages = response.Pages;
        try { bytes = Serialize(code, pages, profile.MaximumEvidenceBytes); }
        catch (EvidenceLimitException)
        {
            outcome = SpecExtractionOutcome.LimitExceeded; code = "serialized-evidence-bytes"; pages = [];
            bytes = Serialize(code, pages, profile.MaximumEvidenceBytes);
        }
        Code = code; Chunks = Array.AsReadOnly(pages.Select((page, ordinal) => new SpecPdfEvidenceChunk(ordinal, page)).ToArray());
        Identity = new(source.Identity, source.Digest, SpecSourceFormat.Pdf, ExtractorVersion, profile.Digest, outcome, Sha256Digest.FromBytes(bytes));
    }
    public SpecExtractionIdentity Identity { get; }
    public SpecExtractionOutcome Outcome => Identity.Outcome;
    public string Code { get; }
    public IReadOnlyList<SpecPdfEvidenceChunk> Chunks { get; }
    public byte[] CopyEvidenceBytes() => (byte[])bytes.Clone();
    public SpecPdfEvidenceCitation Cite(int ordinal) => ordinal >= 0 && ordinal < Chunks.Count
        ? new(Identity, ordinal, Chunks[ordinal].Locator) : throw new ArgumentOutOfRangeException(nameof(ordinal));
    public override string ToString() => nameof(SpecPdfExtractionResult);

    internal static SpecPdfExtractionResult Decode(ReadOnlyMemory<byte> stdout, int exitCode, SpecCapturedSource source, SpecPdfExtractionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(profile);
        if (source.Format != SpecSourceFormat.Pdf) throw new SpecPdfWorkerProtocolException();
        var response = SpecPdfWorkerResponse.Decode(stdout, exitCode, source.Identity, source.Digest, source.ByteCount);
        return new(source, profile, response);
    }
    private static byte[] Serialize(string code, IReadOnlyList<SpecPdfWorkerPage> pages, int limit)
    {
        using var stream = new EvidenceStream(limit);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray(); writer.WriteStringValue("weft.spec-evidence.v2"); writer.WriteStringValue(code);
            for (var ordinal = 0; ordinal < pages.Count; ordinal++)
            {
                var page = pages[ordinal];
                writer.WriteStartArray(); writer.WriteNumberValue(ordinal); writer.WriteStringValue(page.Text);
                SpecEvidenceLocatorJson.Write(writer, page.Locator); writer.WriteNullValue(); writer.WriteBooleanValue(page.MissingText); writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return stream.ToArray();
    }
    private sealed class EvidenceLimitException : Exception;
    private sealed class EvidenceStream(int limit) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit) throw new EvidenceLimitException();
            base.Write(buffer.ToArray(), 0, buffer.Length);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > limit) throw new EvidenceLimitException();
            base.Write(buffer, offset, count);
        }
    }
}

public sealed class SpecPdfEvidenceChunk
{
    internal SpecPdfEvidenceChunk(int ordinal, SpecPdfWorkerPage page)
    { Ordinal = ordinal; Text = page.Text; Locator = page.Locator; MissingText = page.MissingText; }
    public int Ordinal { get; }
    public string Text { get; }
    public SpecEvidenceLocator.PdfPage Locator { get; }
    public string? Formula => null;
    public bool MissingText { get; }
    public override string ToString() => nameof(SpecPdfEvidenceChunk);
}

public sealed class SpecPdfEvidenceCitation
{
    internal SpecPdfEvidenceCitation(SpecExtractionIdentity extraction, int ordinal, SpecEvidenceLocator.PdfPage locator)
    { Extraction = extraction; Ordinal = ordinal; Locator = locator; }
    public SpecExtractionIdentity Extraction { get; }
    public int Ordinal { get; }
    public SpecEvidenceLocator.PdfPage Locator { get; }
    public override string ToString() => nameof(SpecPdfEvidenceCitation);
}

public sealed class SpecPdfWorkerUnavailableException : Exception
{
    public SpecPdfWorkerUnavailableException() : base("The qualified PDF worker is unavailable.") { }
}
