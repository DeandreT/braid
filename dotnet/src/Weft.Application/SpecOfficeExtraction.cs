using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Host boundary only. Implementations must launch a qualified, killable child with no network, credentials or
/// filesystem authority, bounded memory/CPU/wall time and bounded IPC. No in-process production implementation is supplied.</summary>
public interface ISpecOfficeExtractionWorker
{
    Task<SpecOfficeExtractionResult> ExtractAsync(SpecCapturedSource source, SpecOfficeExtractionLimits limits, CancellationToken cancellationToken);
}

/// <summary>Fail-closed default; configuration flags do not qualify a parser sandbox.</summary>
public sealed class UnavailableSpecOfficeExtractionWorker : ISpecOfficeExtractionWorker
{
    public Task<SpecOfficeExtractionResult> ExtractAsync(SpecCapturedSource source, SpecOfficeExtractionLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SpecOfficeExtractionResult(source, limits, SpecExtractionOutcome.Unsupported, "office-worker-unavailable", []));
    }
}

public sealed class SpecOfficeExtractionLimits
{
    public SpecOfficeExtractionLimits(int maximumSourceBytes = SpecCapturedSource.MaximumBytes, int maximumEntries = 256,
        int maximumEntryBytes = 4 * 1024 * 1024, int maximumExpandedBytes = 16 * 1024 * 1024, int maximumCompressionRatio = 100,
        int maximumXmlDepth = 64, int maximumXmlNodes = 100_000, int maximumChunks = 10_000, int maximumEvidenceBytes = 2 * 1024 * 1024)
    {
        if (maximumSourceBytes is < 1 or > SpecCapturedSource.MaximumBytes || maximumEntries is < 1 or > 256 ||
            maximumEntryBytes is < 1 or > 4 * 1024 * 1024 || maximumExpandedBytes is < 1 or > 16 * 1024 * 1024 ||
            maximumCompressionRatio is < 1 or > 100 || maximumXmlDepth is < 1 or > 64 || maximumXmlNodes is < 1 or > 100_000 ||
            maximumChunks is < 1 or > 10_000 || maximumEvidenceBytes is < 256 or > SpecCapturedSource.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumSourceBytes));
        MaximumSourceBytes = maximumSourceBytes; MaximumEntries = maximumEntries; MaximumEntryBytes = maximumEntryBytes;
        MaximumExpandedBytes = maximumExpandedBytes; MaximumCompressionRatio = maximumCompressionRatio;
        MaximumXmlDepth = maximumXmlDepth; MaximumXmlNodes = maximumXmlNodes; MaximumChunks = maximumChunks; MaximumEvidenceBytes = maximumEvidenceBytes;
        Digest = Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new object[] { "weft.office-limits.v1", maximumSourceBytes,
            maximumEntries, maximumEntryBytes, maximumExpandedBytes, maximumCompressionRatio, maximumXmlDepth, maximumXmlNodes, maximumChunks, maximumEvidenceBytes }));
    }
    public int MaximumSourceBytes { get; }
    public int MaximumEntries { get; }
    public int MaximumEntryBytes { get; }
    public int MaximumExpandedBytes { get; }
    public int MaximumCompressionRatio { get; }
    public int MaximumXmlDepth { get; }
    public int MaximumXmlNodes { get; }
    public int MaximumChunks { get; }
    public int MaximumEvidenceBytes { get; }
    public Sha256Digest Digest { get; }
}

/// <summary>Formula is literal untrusted source, never evaluated. Text is the stored value, not a calculated/rendered value.</summary>
public sealed class SpecOfficeEvidenceChunk
{
    internal SpecOfficeEvidenceChunk(int ordinal, string text, SpecEvidenceLocator locator, string? formula = null)
    { Ordinal = ordinal; Text = text; Locator = locator; Formula = formula; }
    public int Ordinal { get; }
    public string Text { get; }
    public SpecEvidenceLocator Locator { get; }
    public string? Formula { get; }
    public override string ToString() => nameof(SpecOfficeEvidenceChunk);
}

public sealed class SpecOfficeEvidenceCitation
{
    internal SpecOfficeEvidenceCitation(SpecExtractionIdentity extraction, SpecOfficeEvidenceChunk chunk)
    { Extraction = extraction; Ordinal = chunk.Ordinal; Locator = chunk.Locator; }
    public SpecExtractionIdentity Extraction { get; }
    public int Ordinal { get; }
    public SpecEvidenceLocator Locator { get; }
    public override string ToString() => nameof(SpecOfficeEvidenceCitation);
}

/// <summary>V2 is a discriminated structural envelope, separate from legacy v1 byte-span evidence. Refusals are atomic.</summary>
public sealed class SpecOfficeExtractionResult
{
    public const int ExtractorVersion = 3;
    private readonly byte[] bytes;
    internal SpecOfficeExtractionResult(SpecCapturedSource source, SpecOfficeExtractionLimits limits, SpecExtractionOutcome outcome,
        string code, IReadOnlyList<SpecOfficeEvidenceChunk> chunks)
    {
        try { bytes = Serialize(code, chunks, limits.MaximumEvidenceBytes); }
        catch (EvidenceLimitException)
        {
            outcome = SpecExtractionOutcome.LimitExceeded; code = "serialized-evidence-bytes"; chunks = [];
            bytes = Serialize(code, chunks, limits.MaximumEvidenceBytes);
        }
        Code = code; Chunks = Array.AsReadOnly(chunks.ToArray());
        Identity = new(source.Identity, source.Digest, source.Format, ExtractorVersion, limits.Digest, outcome, Sha256Digest.FromBytes(bytes));
    }
    public SpecExtractionIdentity Identity { get; }
    public SpecExtractionOutcome Outcome => Identity.Outcome;
    public string Code { get; }
    public IReadOnlyList<SpecOfficeEvidenceChunk> Chunks { get; }
    public byte[] CopyEvidenceBytes() => (byte[])bytes.Clone();
    public SpecOfficeEvidenceCitation Cite(int ordinal) => ordinal >= 0 && ordinal < Chunks.Count
        ? new(Identity, Chunks[ordinal]) : throw new ArgumentOutOfRangeException(nameof(ordinal));
    public override string ToString() => nameof(SpecOfficeExtractionResult);

    private static byte[] Serialize(string code, IReadOnlyList<SpecOfficeEvidenceChunk> chunks, int limit)
    {
        using var stream = new EvidenceStream(limit);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray(); writer.WriteStringValue("weft.spec-evidence.v2"); writer.WriteStringValue(code);
            foreach (var chunk in chunks)
            {
                writer.WriteStartArray(); writer.WriteNumberValue(chunk.Ordinal); writer.WriteStringValue(chunk.Text);
                SpecEvidenceLocatorJson.Write(writer, chunk.Locator);
                writer.WriteStringValue(chunk.Formula); writer.WriteEndArray();
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
