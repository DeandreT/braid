using Braid.Contracts.Operations;

namespace Braid.Application.Regression;

/// <summary>The comparison is derived from original observations, never stored as a verdict.</summary>
public sealed class SavedXmlComparison
{
    public const string Policy = XmlCanonicalComparison.Policy;
    private readonly IXmlCanonicalComparisonWorker worker;
    internal SavedXmlComparison(IXmlCanonicalComparisonWorker worker) => this.worker = worker;

    internal async Task<RegressionComparisonResult> CompareAsync(RegressionCaseVersion saved, MappingPreviewStatus status,
        RegressionOutputSet outputs, CancellationToken token)
    {
        if (saved.Runtime.Profile != XmlPreviewAdmission.Profile || saved.ComparisonPolicy != Policy)
            throw new InvalidOperationException("Not a canonical XML case.");
        var execution = status == MappingPreviewStatus.Succeeded ? OperationStatusV1.Succeeded : OperationStatusV1.BackendFailure;
        XmlComparisonOutputSet Capture(RegressionOutputSet set) => XmlComparisonOutputSet.Capture(set.Outputs.Select(o =>
            XmlComparisonOutput.Capture(o.Key, o.Span, o.XmlMetadata, o.MediaType, token)).ToArray(), token);
        var reply = await worker.CompareAsync(Capture(saved.Expected), Capture(outputs), execution, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (reply.Failure != XmlComparisonWorkerFailure.None || reply.Comparison is not { } result)
            throw new InvalidOperationException("Isolated XML comparison unavailable.");
        var reason = result.NotEvaluatedReason switch
        {
            XmlCanonicalFailure.None => RegressionNotEvaluatedReason.None,
            XmlCanonicalFailure.ExecutionUnsuccessful => RegressionNotEvaluatedReason.ExecutionUnsuccessful,
            XmlCanonicalFailure.MissingBaseline => RegressionNotEvaluatedReason.MissingBaseline,
            XmlCanonicalFailure.ResourceLimit => RegressionNotEvaluatedReason.ResourceLimit,
            XmlCanonicalFailure.UnsupportedDomain => RegressionNotEvaluatedReason.UnsupportedDomain,
            XmlCanonicalFailure.CanonicalizerFailure => RegressionNotEvaluatedReason.CanonicalizerFailure,
            _ => RegressionNotEvaluatedReason.InvalidDocument
        };
        return new(Policy, result.Outcome, reason, execution, result.ExpectedOutputCount, result.ActualOutputCount,
            result.TotalDifferenceCount, result.Differences.Select(d => new RegressionOutputDifference(d.Key, d.Kind switch
            {
                XmlCanonicalDifferenceKind.CanonicalBytesDiffer => RegressionDifferenceKind.CanonicalBytesDiffer,
                XmlCanonicalDifferenceKind.XmlMetadataDiffer => RegressionDifferenceKind.XmlMetadataDiffer,
                XmlCanonicalDifferenceKind.MissingOutput => RegressionDifferenceKind.MissingOutput,
                XmlCanonicalDifferenceKind.UnexpectedOutput => RegressionDifferenceKind.UnexpectedOutput,
                XmlCanonicalDifferenceKind.MediaTypeDiffer => RegressionDifferenceKind.MediaTypeDiffer,
                _ => throw new InvalidOperationException("Unknown XML difference.")
            }, d.ExpectedByteCount, d.ActualByteCount, d.ExpectedDigest, d.ActualDigest, null)
            { CanonicalByteOffset = d.CanonicalByteOffset }).ToArray());
    }
}
