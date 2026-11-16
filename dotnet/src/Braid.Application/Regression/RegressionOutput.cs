using System.Text;
using Braid.Domain;

namespace Braid.Application.Regression;

/// <summary>A logical output identity, never a filesystem path. Null names identify the primary output.</summary>
public sealed record RegressionOutputKey
{
    private RegressionOutputKey(string? name) => Name = name;

    public static RegressionOutputKey Primary { get; } = new((string?)null);
    public string? Name { get; }
    public bool IsPrimary => Name is null;

    public static RegressionOutputKey Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > 512 || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            throw new RegressionComparisonException(RegressionComparisonFailure.InvalidInput);
        try { _ = new UTF8Encoding(false, true).GetByteCount(name); }
        catch (EncoderFallbackException) { throw new RegressionComparisonException(RegressionComparisonFailure.InvalidInput); }
        return new(name);
    }

    public override string ToString() => nameof(RegressionOutputKey);
}

/// <summary>Owned exact bytes. A digest is evidence, not a substitute for comparing the bytes.</summary>
public sealed class RegressionOutput
{
    public const int MaximumByteCount = 16 * 1024 * 1024;
    private readonly byte[] bytes;

    private RegressionOutput(RegressionOutputKey key, byte[] bytes, string mediaType, CsvDocumentDialect? csvDialect,
        XmlDocumentMetadata? xmlMetadata)
    {
        Key = key;
        MediaType = mediaType;
        CsvDialect = csvDialect;
        XmlMetadata = xmlMetadata;
        this.bytes = bytes;
        Digest = Sha256Digest.FromBytes(bytes);
    }

    public RegressionOutputKey Key { get; }
    public string MediaType { get; }
    public CsvDocumentDialect? CsvDialect { get; }
    public XmlDocumentMetadata? XmlMetadata { get; }
    public int ByteCount => bytes.Length;
    public Sha256Digest Digest { get; }
    internal ReadOnlySpan<byte> Span => bytes;

    public static RegressionOutput Capture(RegressionOutputKey key, ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken = default, string mediaType = PreviewPolicy.MediaType, CsvDocumentDialect? csvDialect = null,
        XmlDocumentMetadata? xmlMetadata = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        if (xmlMetadata is not null ? csvDialect is not null || !XmlDocumentPolicy.Admits(xmlMetadata, XmlDocumentRole.Output, mediaType)
            : CsvDocumentPolicy.AdmitsMediaType(mediaType) ? !CsvDocumentPolicy.Admits(csvDialect, mediaType)
            : !DocumentPreviewPolicy.AdmitsMediaType(mediaType) || csvDialect is not null)
            throw new RegressionComparisonException(RegressionComparisonFailure.InvalidInput);
        if (bytes.Length > MaximumByteCount)
            throw new RegressionComparisonException(RegressionComparisonFailure.ResourceLimit);
        if (xmlMetadata is not null)
        {
            try { XmlDocumentSafety.Validate(bytes, cancellationToken); }
            catch (XmlDocumentException error)
            { throw new RegressionComparisonException(error.IsResourceLimit ? RegressionComparisonFailure.ResourceLimit : RegressionComparisonFailure.InvalidInput); }
        }
        var output = new RegressionOutput(key, bytes.ToArray(), mediaType, csvDialect, xmlMetadata);
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    public byte[] CopyBytes() => (byte[])bytes.Clone();
    public override string ToString() => nameof(RegressionOutput);
}

/// <summary>A bounded, immutable snapshot, ordered primary first and then by exact ordinal name.</summary>
public sealed class RegressionOutputSet
{
    public const int MaximumOutputCount = 128;
    public const int MaximumByteCount = 32 * 1024 * 1024;

    private RegressionOutputSet(RegressionOutput[] outputs, int byteCount)
    {
        Outputs = Array.AsReadOnly(outputs);
        ByteCount = byteCount;
    }

    public IReadOnlyList<RegressionOutput> Outputs { get; }
    public int ByteCount { get; }

    public static RegressionOutputSet Capture(IReadOnlyList<RegressionOutput> outputs,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(outputs);
        var count = outputs.Count;
        if (count is < 0 or > MaximumOutputCount)
            throw new RegressionComparisonException(RegressionComparisonFailure.ResourceLimit);
        var owned = new RegressionOutput[count];
        var keys = new HashSet<RegressionOutputKey>();
        var byteCount = 0;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = outputs[index];
            if (output is null || !keys.Add(output.Key))
                throw new RegressionComparisonException(RegressionComparisonFailure.InvalidInput);
            if (output.ByteCount > MaximumByteCount - byteCount)
                throw new RegressionComparisonException(RegressionComparisonFailure.ResourceLimit);
            byteCount += output.ByteCount;
            owned[index] = output;
        }
        Array.Sort(owned, (left, right) => StringComparer.Ordinal.Compare(left.Key.Name, right.Key.Name));
        cancellationToken.ThrowIfCancellationRequested();
        return new(owned, byteCount);
    }

    public override string ToString() => nameof(RegressionOutputSet);
}
