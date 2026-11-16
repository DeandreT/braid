using Braid.Contracts.Operations;

namespace Braid.Application.Regression;

/// <summary>Dormant internal port. Only an isolated, fixed-operation worker may implement this contract.</summary>
internal interface IXmlCanonicalComparisonWorker
{
    Task<XmlComparisonWorkerResult> CompareAsync(XmlComparisonOutputSet expected, XmlComparisonOutputSet actual,
        OperationStatusV1 executionStatus, CancellationToken cancellationToken = default);
}

internal enum XmlComparisonWorkerFailure { None, Canceled, TimedOut, Unavailable, InvalidResponse }

internal sealed record XmlComparisonWorkerResult(XmlCanonicalComparisonResult? Comparison, XmlComparisonWorkerFailure Failure);

/// <summary>Trusted composition must explicitly select every acceptance limit. These are not allocation guarantees.</summary>
internal sealed class XmlComparisonWorkerOptions
{
    public XmlComparisonWorkerOptions(RegressionComparisonLimits comparison, XmlCanonicalizationLimits canonical)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(canonical);
        Comparison = comparison;
        Canonical = canonical;
    }

    public RegressionComparisonLimits Comparison { get; }
    public XmlCanonicalizationLimits Canonical { get; }
}
