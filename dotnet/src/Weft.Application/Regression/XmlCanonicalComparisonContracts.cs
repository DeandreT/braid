using Weft.Domain;

namespace Weft.Application.Regression;

/// <summary>
/// Internal comparison material, not an admitted execution/persistence contract. Capture retains original bytes;
/// parsing is deliberately deferred so even identical and unmatched documents receive the complete safety check.
/// </summary>
internal sealed class XmlComparisonOutput
{
    private readonly byte[] bytes;

    private XmlComparisonOutput(RegressionOutputKey key, byte[] bytes, string mediaType, XmlDocumentMetadata? metadata)
    {
        Key = key;
        this.bytes = bytes;
        MediaType = mediaType;
        Metadata = metadata;
        Digest = Sha256Digest.FromBytes(bytes);
    }

    public RegressionOutputKey Key { get; }
    public string MediaType { get; }
    public XmlDocumentMetadata? Metadata { get; }
    public int ByteCount => bytes.Length;
    public Sha256Digest Digest { get; }
    internal ReadOnlySpan<byte> Span => bytes;

    public static XmlComparisonOutput Capture(RegressionOutputKey key, ReadOnlySpan<byte> bytes,
        XmlDocumentMetadata? metadata, string mediaType = XmlDocumentPolicy.MediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(mediaType);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > XmlDocumentPolicy.MaximumBytes)
            throw new RegressionComparisonException(RegressionComparisonFailure.ResourceLimit);
        var result = new XmlComparisonOutput(key, bytes.ToArray(), mediaType, metadata);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public byte[] CopyBytes() => (byte[])bytes.Clone();
    public override string ToString() => nameof(XmlComparisonOutput);
}

/// <summary>An immutable XML output snapshot, with the output limit of 64 rather than the input limit of 16.</summary>
internal sealed class XmlComparisonOutputSet
{
    private XmlComparisonOutputSet(XmlComparisonOutput[] outputs, int byteCount)
    {
        Outputs = Array.AsReadOnly(outputs);
        ByteCount = byteCount;
    }

    public IReadOnlyList<XmlComparisonOutput> Outputs { get; }
    public int ByteCount { get; }

    public static XmlComparisonOutputSet Capture(IReadOnlyList<XmlComparisonOutput> outputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        cancellationToken.ThrowIfCancellationRequested();
        if (outputs.Count > XmlDocumentPolicy.MaximumOutputs)
            throw new RegressionComparisonException(RegressionComparisonFailure.ResourceLimit);
        var owned = new XmlComparisonOutput[outputs.Count];
        var keys = new HashSet<RegressionOutputKey>();
        var total = 0;
        for (var index = 0; index < outputs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = outputs[index];
            if (output is null || !keys.Add(output.Key))
                throw new RegressionComparisonException(RegressionComparisonFailure.InvalidInput);
            if (output.ByteCount > XmlDocumentPolicy.MaximumBytes - total)
                throw new RegressionComparisonException(RegressionComparisonFailure.ResourceLimit);
            total = checked(total + output.ByteCount);
            owned[index] = output;
        }
        Array.Sort(owned, (left, right) => StringComparer.Ordinal.Compare(left.Key.Name, right.Key.Name));
        cancellationToken.ThrowIfCancellationRequested();
        return new(owned, total);
    }

    public override string ToString() => nameof(XmlComparisonOutputSet);
}

/// <summary>
/// All limits must be supplied explicitly by a future qualified host. These are acceptance/read bounds, not
/// bounds on the transform's eager intermediate allocations. This type selects no worker or memory/CPU limits.
/// </summary>
internal sealed class XmlCanonicalizationLimits
{
    public XmlCanonicalizationLimits(int originalBytesPerSide, long canonicalBytesPerDocument, long canonicalBytesTotal)
    {
        if (originalBytesPerSide is <= 0 or > XmlDocumentPolicy.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(originalBytesPerSide));
        if (canonicalBytesPerDocument <= 0 || canonicalBytesPerDocument == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(canonicalBytesPerDocument));
        if (canonicalBytesTotal <= 0 || canonicalBytesTotal == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(canonicalBytesTotal));
        OriginalBytesPerSide = originalBytesPerSide;
        CanonicalBytesPerDocument = canonicalBytesPerDocument;
        CanonicalBytesTotal = canonicalBytesTotal;
    }

    public int OriginalBytesPerSide { get; }
    public long CanonicalBytesPerDocument { get; }
    public long CanonicalBytesTotal { get; }
    public override string ToString() => nameof(XmlCanonicalizationLimits);
}

internal enum XmlCanonicalFailure
{
    None, ExecutionUnsuccessful, MissingBaseline, InvalidMediaType, InvalidMetadata,
    InvalidDocument, UnsupportedDomain, ResourceLimit, CanonicalizerFailure
}

internal enum XmlCanonicalPhase { Admission, Safety, NamespaceDomain, Canonicalization, CanonicalRead }
internal enum XmlComparisonSide { Expected, Actual }
internal enum XmlCanonicalDifferenceKind { MissingOutput, UnexpectedOutput, MediaTypeDiffer, XmlMetadataDiffer, CanonicalBytesDiffer }

/// <summary>No XML values, names, URIs, parser messages or exception objects cross this boundary.</summary>
internal sealed record XmlCanonicalDiagnostic(RegressionOutputKey? Key, XmlComparisonSide? Side,
    XmlCanonicalPhase Phase, XmlCanonicalFailure Reason, long? Observed = null, long? Limit = null);

internal sealed record XmlCanonicalTotals(int Documents, long Bytes);

/// <summary>Canonical offsets are zero-based offsets in canonical UTF-8, never source offsets or XML locations.</summary>
internal sealed record XmlCanonicalDifference(RegressionOutputKey Key, XmlCanonicalDifferenceKind Kind,
    int? ExpectedByteCount, int? ActualByteCount, Sha256Digest? ExpectedDigest, Sha256Digest? ActualDigest,
    long? ExpectedCanonicalByteCount, long? ActualCanonicalByteCount, long? CanonicalByteOffset)
{
    public int? FirstDifferentByte => null;
    public override string ToString() => nameof(XmlCanonicalDifference);
}

internal sealed class XmlCanonicalComparisonResult
{
    internal XmlCanonicalComparisonResult(RegressionComparisonOutcome outcome, XmlCanonicalFailure reason,
        XmlComparisonOutputSet expected, XmlComparisonOutputSet actual, int totalDifferenceCount,
        IReadOnlyList<XmlCanonicalDifference> differences, XmlCanonicalTotals? expectedCanonical,
        XmlCanonicalTotals? actualCanonical, XmlCanonicalDiagnostic? diagnostic)
    {
        Outcome = outcome;
        NotEvaluatedReason = reason;
        ExpectedOutputCount = expected.Outputs.Count;
        ActualOutputCount = actual.Outputs.Count;
        ExpectedOriginalBytes = expected.ByteCount;
        ActualOriginalBytes = actual.ByteCount;
        TotalDifferenceCount = totalDifferenceCount;
        Differences = differences;
        ExpectedCanonical = expectedCanonical;
        ActualCanonical = actualCanonical;
        Diagnostic = diagnostic;
    }

    public string Policy => XmlCanonicalComparison.Policy;
    public RegressionComparisonOutcome Outcome { get; }
    public XmlCanonicalFailure NotEvaluatedReason { get; }
    public bool ComparisonComplete => Outcome != RegressionComparisonOutcome.NotEvaluated;
    public int ExpectedOutputCount { get; }
    public int ActualOutputCount { get; }
    public long ExpectedOriginalBytes { get; }
    public long ActualOriginalBytes { get; }
    public XmlCanonicalTotals? ExpectedCanonical { get; }
    public XmlCanonicalTotals? ActualCanonical { get; }
    public int TotalDifferenceCount { get; }
    public IReadOnlyList<XmlCanonicalDifference> Differences { get; }
    public bool Truncated => Differences.Count < TotalDifferenceCount;
    public XmlCanonicalDiagnostic? Diagnostic { get; }
    public override string ToString() => $"{nameof(XmlCanonicalComparisonResult)}:{Outcome}";
}

internal sealed class XmlCanonicalizationException(XmlCanonicalFailure reason, XmlCanonicalPhase phase,
    long? observed = null, long? limit = null) : Exception("XML canonical comparison refused.")
{
    public XmlCanonicalFailure Reason { get; } = reason;
    public XmlCanonicalPhase Phase { get; } = phase;
    public long? Observed { get; } = observed;
    public long? Limit { get; } = limit;
}
