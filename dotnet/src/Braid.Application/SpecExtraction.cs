using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

/// <summary>Hard ceilings remain enforced even when a caller supplies a smaller extraction profile.</summary>
public sealed class SpecExtractionLimits
{
    public SpecExtractionLimits(int maximumSourceBytes = SpecCapturedSource.MaximumBytes, int maximumChunks = 10_000,
        int maximumEvidenceBytes = 2 * 1024 * 1024, int maximumDepth = 64, int maximumTokens = 100_000)
    {
        if (maximumSourceBytes is < 1 or > SpecCapturedSource.MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumSourceBytes));
        if (maximumChunks is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maximumChunks));
        if (maximumEvidenceBytes is < 1 or > 2 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumEvidenceBytes));
        if (maximumDepth is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (maximumTokens is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        MaximumSourceBytes = maximumSourceBytes;
        MaximumChunks = maximumChunks;
        MaximumEvidenceBytes = maximumEvidenceBytes;
        MaximumDepth = maximumDepth;
        MaximumTokens = maximumTokens;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("braid.spec-extraction-limits.v1");
            writer.WriteNumberValue(maximumSourceBytes);
            writer.WriteNumberValue(maximumChunks);
            writer.WriteNumberValue(maximumEvidenceBytes);
            writer.WriteNumberValue(maximumDepth);
            writer.WriteNumberValue(maximumTokens);
            writer.WriteEndArray();
        }
        Digest = Sha256Digest.FromBytes(stream.ToArray());
    }

    public int MaximumSourceBytes { get; }
    public int MaximumChunks { get; }
    public int MaximumEvidenceBytes { get; }
    public int MaximumDepth { get; }
    public int MaximumTokens { get; }
    public Sha256Digest Digest { get; }
}

/// <summary>
/// Untrusted evidence. Byte offsets are zero-based in the original captured bytes; lines are one-based.
/// JSON pointers use RFC 6901 (empty means root). A container chunk cites its opening token only.
/// Text chunks exclude their line terminator. No page, schema validity or resolved reference is implied.
/// </summary>
public sealed class SpecEvidenceChunk
{
    internal SpecEvidenceChunk(int ordinal, string text, string? jsonPointer, int byteOffset, int byteLength, int startLine, int endLine)
    {
        Ordinal = ordinal;
        Text = text;
        JsonPointer = jsonPointer;
        ByteOffset = byteOffset;
        ByteLength = byteLength;
        StartLine = startLine;
        EndLine = endLine;
    }

    public int Ordinal { get; }
    public string Text { get; }
    public string? JsonPointer { get; }
    public int ByteOffset { get; }
    public int ByteLength { get; }
    public int StartLine { get; }
    public int EndLine { get; }
    public SpecEvidenceLocator.ByteRange Locator => new(ByteOffset, ByteLength, StartLine, EndLine, JsonPointer);
    public override string ToString() => nameof(SpecEvidenceChunk);
}

/// <summary>Failure is atomic: no partial evidence. Diagnostics are fixed codes, never document content.</summary>
public sealed class SpecExtractionResult
{
    private readonly byte[] canonicalEvidence;
    // Database restoration only; never a model or HTTP admission path.
    internal SpecExtractionResult(SpecExtractionIdentity identity, byte[] evidence)
    {
        if (evidence.Length > SpecCapturedSource.MaximumBytes || Sha256Digest.FromBytes(evidence) != identity.EvidenceDigest ||
            identity.Outcome != SpecExtractionOutcome.Complete) throw new ArgumentException("Invalid retained extraction.");
        using var document = JsonDocument.Parse(evidence, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 2 or > 10002 || root[0].GetString() != "braid.spec-evidence.v1")
            throw new ArgumentException("Invalid retained extraction.");
        Code = root[1].GetString() ?? throw new ArgumentException("Missing extraction code.");
        var chunks = new List<SpecEvidenceChunk>();
        foreach (var item in root.EnumerateArray().Skip(2))
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 7 || item[0].GetInt32() != chunks.Count)
                throw new ArgumentException("Invalid retained chunk.");
            var chunk = new SpecEvidenceChunk(item[0].GetInt32(), item[1].GetString()!, item[2].GetString(), item[3].GetInt32(), item[4].GetInt32(), item[5].GetInt32(), item[6].GetInt32());
            if (chunk.Text is null || chunk.ByteOffset < 0 || chunk.ByteLength < 0 || chunk.StartLine < 1 || chunk.EndLine < chunk.StartLine)
                throw new ArgumentException("Invalid retained locator.");
            chunks.Add(chunk);
        }
        if (!Serialize(Code, chunks).AsSpan().SequenceEqual(evidence)) throw new ArgumentException("Noncanonical extraction.");
        Identity = identity; Chunks = chunks.AsReadOnly(); canonicalEvidence = evidence.ToArray();
    }
    internal SpecExtractionResult(SpecCapturedSource source, SpecExtractionLimits limits, SpecExtractionOutcome outcome,
        string code, IReadOnlyList<SpecEvidenceChunk> chunks)
    {
        try { canonicalEvidence = Serialize(code, chunks); }
        catch (EvidenceLimitException)
        {
            outcome = SpecExtractionOutcome.LimitExceeded;
            code = "serialized-evidence-bytes";
            chunks = [];
            canonicalEvidence = Serialize(code, chunks);
        }
        Code = code;
        Chunks = Array.AsReadOnly(chunks.ToArray());
        Identity = new(source.Identity, source.Digest, source.Format, SpecSourceExtractor.Version, limits.Digest, outcome,
            Sha256Digest.FromBytes(canonicalEvidence));
    }

    private static byte[] Serialize(string code, IReadOnlyList<SpecEvidenceChunk> chunks)
    {
        using var stream = new EvidenceStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("braid.spec-evidence.v1");
            writer.WriteStringValue(code);
            foreach (var chunk in chunks)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(chunk.Ordinal);
                writer.WriteStringValue(chunk.Text);
                writer.WriteStringValue(chunk.JsonPointer);
                writer.WriteNumberValue(chunk.ByteOffset);
                writer.WriteNumberValue(chunk.ByteLength);
                writer.WriteNumberValue(chunk.StartLine);
                writer.WriteNumberValue(chunk.EndLine);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return stream.ToArray();
    }

    private sealed class EvidenceLimitException : Exception;

    // Enforce the physical ceiling on actual encoded bytes, including escaping and JSON framing.
    // A refusal discards every chunk; it never publishes truncated evidence as a complete extraction.
    private sealed class EvidenceStream : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > SpecCapturedSource.MaximumBytes) throw new EvidenceLimitException();
            base.Write(buffer.ToArray(), 0, buffer.Length);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > SpecCapturedSource.MaximumBytes) throw new EvidenceLimitException();
            base.Write(buffer, offset, count);
        }
    }

    public SpecExtractionIdentity Identity { get; }
    public SpecExtractionOutcome Outcome => Identity.Outcome;
    public string Code { get; }
    public IReadOnlyList<SpecEvidenceChunk> Chunks { get; }
    /// <summary>Exact immutable evidence serialization whose digest is pinned by Identity; not a prompt or authority.</summary>
    public byte[] CopyEvidenceBytes() => (byte[])canonicalEvidence.Clone();

    /// <summary>Constructs a citation only to an existing chunk of this exact extraction; conveys no read authority.</summary>
    public SpecEvidenceCitation Cite(int ordinal)
    {
        if (ordinal < 0 || ordinal >= Chunks.Count) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return new(Identity, Chunks[ordinal]);
    }

    public override string ToString() => nameof(SpecExtractionResult);
}

/// <summary>Exact extraction pin plus a verified local locator. Consumers must check current project authority before disclosure.</summary>
public sealed class SpecEvidenceCitation
{
    internal SpecEvidenceCitation(SpecExtractionIdentity extraction, SpecEvidenceChunk chunk)
    {
        Extraction = extraction;
        Ordinal = chunk.Ordinal;
        JsonPointer = chunk.JsonPointer;
        ByteOffset = chunk.ByteOffset;
        ByteLength = chunk.ByteLength;
        StartLine = chunk.StartLine;
        EndLine = chunk.EndLine;
    }

    public SpecExtractionIdentity Extraction { get; }
    public SpecEvidenceLocator.ByteRange Locator => new(ByteOffset, ByteLength, StartLine, EndLine, JsonPointer);
    public int Ordinal { get; }
    public string? JsonPointer { get; }
    public int ByteOffset { get; }
    public int ByteLength { get; }
    public int StartLine { get; }
    public int EndLine { get; }
    public override string ToString() => nameof(SpecEvidenceCitation);
}
