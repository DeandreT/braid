using Weft.Contracts.Operations;

namespace Weft.Application.Regression;

/// <summary>
/// Dormant worker-side core only. No existing comparer dispatch, API, capability, persistence or build-proof path
/// calls this class. A host must qualify termination of synchronous transform calls before exposing this policy.
/// </summary>
internal static class XmlCanonicalComparison
{
    public const string Policy = "weft.regression.xml-c14n10-comments.v1";
    private const int ChunkSize = 4096;

    public static XmlCanonicalComparisonResult Compare(XmlComparisonOutputSet expected, XmlComparisonOutputSet actual,
        OperationStatusV1 executionStatus, RegressionComparisonLimits comparisonLimits,
        XmlCanonicalizationLimits canonicalLimits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(comparisonLimits);
        ArgumentNullException.ThrowIfNull(canonicalLimits);
        if (!Enum.IsDefined(executionStatus)) throw new ArgumentOutOfRangeException(nameof(executionStatus));
        cancellationToken.ThrowIfCancellationRequested();
        if (executionStatus != OperationStatusV1.Succeeded) return NotEvaluated(XmlCanonicalFailure.ExecutionUnsuccessful);
        if (expected.Outputs.Count == 0) return NotEvaluated(XmlCanonicalFailure.MissingBaseline);

        // Charge both complete original sets before parsing anything, including unmatched outputs and tails.
        if (expected.ByteCount > canonicalLimits.OriginalBytesPerSide)
            return AdmissionLimit(XmlComparisonSide.Expected, expected.ByteCount, canonicalLimits.OriginalBytesPerSide);
        if (actual.ByteCount > canonicalLimits.OriginalBytesPerSide)
            return AdmissionLimit(XmlComparisonSide.Actual, actual.ByteCount, canonicalLimits.OriginalBytesPerSide);
        var originalTotal = checked((long)expected.ByteCount + actual.ByteCount);
        if (originalTotal > comparisonLimits.ComparedBytes)
            return AdmissionLimit(null, originalTotal, comparisonLimits.ComparedBytes);

        var differences = new List<XmlCanonicalDifference>();
        var totalDifferences = 0;
        var leftIndex = 0;
        var rightIndex = 0;
        var expectedDocuments = 0;
        var actualDocuments = 0;
        long expectedCanonicalBytes = 0;
        long actualCanonicalBytes = 0;
        long remainingCanonicalBytes = canonicalLimits.CanonicalBytesTotal;
        var leftBuffer = new byte[ChunkSize];
        var rightBuffer = new byte[ChunkSize];
        while (leftIndex < expected.Outputs.Count || rightIndex < actual.Outputs.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = leftIndex < expected.Outputs.Count ? expected.Outputs[leftIndex] : null;
            var right = rightIndex < actual.Outputs.Count ? actual.Outputs[rightIndex] : null;
            var order = left is null ? 1 : right is null ? -1 : StringComparer.Ordinal.Compare(left.Key.Name, right.Key.Name);
            if (order < 0) right = null;
            if (order > 0) left = null;
            var key = (left ?? right)!.Key;
            var side = XmlComparisonSide.Expected;
            try
            {
                // This is an explicitly selected XML comparer, not format inference. A different observed media
                // label is a difference against an XML peer, never permission to pass a foreign-format pair.
                if (left?.MediaType != XmlDocumentPolicy.MediaType && right?.MediaType != XmlDocumentPolicy.MediaType)
                {
                    side = left is null ? XmlComparisonSide.Actual : XmlComparisonSide.Expected;
                    throw new XmlCanonicalizationException(XmlCanonicalFailure.InvalidMediaType, XmlCanonicalPhase.Admission);
                }
                // Every present document is parsed and canonicalized, regardless of raw equality or header mismatch.
                using var leftCanonical = Open(left, XmlComparisonSide.Expected);
                using var rightCanonical = Open(right, XmlComparisonSide.Actual);
                long? firstDifference;
                if (leftCanonical is not null && rightCanonical is not null)
                    firstDifference = CompareBytes(leftCanonical, rightCanonical, leftBuffer, rightBuffer, cancellationToken, ref side);
                else
                {
                    if (leftCanonical is not null)
                    {
                        side = XmlComparisonSide.Expected;
                        Drain(leftCanonical, leftBuffer, cancellationToken);
                    }
                    if (rightCanonical is not null)
                    {
                        side = XmlComparisonSide.Actual;
                        Drain(rightCanonical, rightBuffer, cancellationToken);
                    }
                    firstDifference = null;
                }
                var kind = left is null ? XmlCanonicalDifferenceKind.UnexpectedOutput :
                    right is null ? XmlCanonicalDifferenceKind.MissingOutput :
                    left.MediaType != right.MediaType ? XmlCanonicalDifferenceKind.MediaTypeDiffer :
                    left.Metadata != right.Metadata ? XmlCanonicalDifferenceKind.XmlMetadataDiffer :
                    firstDifference is not null ? XmlCanonicalDifferenceKind.CanonicalBytesDiffer : (XmlCanonicalDifferenceKind?)null;
                if (kind is not null)
                {
                    totalDifferences = checked(totalDifferences + 1);
                    if (differences.Count < comparisonLimits.Differences)
                        differences.Add(new(key, kind.Value, left?.ByteCount, right?.ByteCount, left?.Digest, right?.Digest,
                            leftCanonical?.Length, rightCanonical?.Length,
                            kind == XmlCanonicalDifferenceKind.CanonicalBytesDiffer ? firstDifference : null));
                }
            }
            catch (XmlCanonicalizationException error)
            {
                return NotEvaluated(error.Reason, new(key, side, error.Phase, error.Reason, error.Observed, error.Limit));
            }
            catch (OutOfMemoryException)
            {
                // An enclosing worker still owns hard memory enforcement and unexplained termination handling.
                return NotEvaluated(XmlCanonicalFailure.ResourceLimit,
                    new(key, side, XmlCanonicalPhase.Canonicalization, XmlCanonicalFailure.ResourceLimit));
            }
            if (order <= 0) leftIndex++;
            if (order >= 0) rightIndex++;

            XmlCanonicalDocument? Open(XmlComparisonOutput? output, XmlComparisonSide currentSide)
            {
                if (output is null) return null;
                side = currentSide;
                if (!XmlDocumentPolicy.Admits(output.Metadata, XmlDocumentRole.Output))
                    throw new XmlCanonicalizationException(XmlCanonicalFailure.InvalidMetadata, XmlCanonicalPhase.Admission);
                var document = XmlCanonicalDocument.Open(output.Span, canonicalLimits.OriginalBytesPerSide,
                    Math.Min(canonicalLimits.CanonicalBytesPerDocument, remainingCanonicalBytes), cancellationToken);
                remainingCanonicalBytes = checked(remainingCanonicalBytes - document.Length);
                if (currentSide == XmlComparisonSide.Expected)
                {
                    expectedDocuments++;
                    expectedCanonicalBytes = checked(expectedCanonicalBytes + document.Length);
                }
                else
                {
                    actualDocuments++;
                    actualCanonicalBytes = checked(actualCanonicalBytes + document.Length);
                }
                return document;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(totalDifferences == 0 ? RegressionComparisonOutcome.Passed : RegressionComparisonOutcome.Failed,
            XmlCanonicalFailure.None, expected, actual, totalDifferences, Array.AsReadOnly(differences.ToArray()),
            new(expectedDocuments, expectedCanonicalBytes), new(actualDocuments, actualCanonicalBytes), null);

        XmlCanonicalComparisonResult NotEvaluated(XmlCanonicalFailure reason, XmlCanonicalDiagnostic? diagnostic = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(RegressionComparisonOutcome.NotEvaluated, reason, expected, actual, 0,
                Array.Empty<XmlCanonicalDifference>(), null, null, diagnostic);
        }

        XmlCanonicalComparisonResult AdmissionLimit(XmlComparisonSide? side, long observed, long limit) =>
            NotEvaluated(XmlCanonicalFailure.ResourceLimit,
                new(null, side, XmlCanonicalPhase.Admission, XmlCanonicalFailure.ResourceLimit, observed, limit));
    }

    private static long? CompareBytes(XmlCanonicalDocument left, XmlCanonicalDocument right, byte[] leftBuffer,
        byte[] rightBuffer, CancellationToken token, ref XmlComparisonSide side)
    {
        long offset = 0;
        long? first = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            side = XmlComparisonSide.Expected;
            var leftCount = left.Read(leftBuffer, token);
            side = XmlComparisonSide.Actual;
            var rightCount = right.Read(rightBuffer, token);
            if (leftCount == 0 && rightCount == 0) return first;
            if (first is null)
            {
                var shared = Math.Min(leftCount, rightCount);
                for (var index = 0; index < shared; index++)
                {
                    if ((index & 255) == 0) token.ThrowIfCancellationRequested();
                    if (leftBuffer[index] == rightBuffer[index]) continue;
                    first = checked(offset + index);
                    break;
                }
                if (first is null && leftCount != rightCount) first = checked(offset + shared);
            }
            // Continue to EOF even after the first mismatch: the complete canonical material is accounted/read.
            offset = checked(offset + Math.Max(leftCount, rightCount));
        }
    }

    private static void Drain(XmlCanonicalDocument document, byte[] buffer, CancellationToken token)
    {
        while (document.Read(buffer, token) != 0) token.ThrowIfCancellationRequested();
    }
}
