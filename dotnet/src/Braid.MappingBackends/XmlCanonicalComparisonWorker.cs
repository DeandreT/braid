using Braid.Application.Regression;
using Braid.Contracts.Operations;
using Braid.Deployment;
using Braid.MappingBackends.Workers;

namespace Braid.MappingBackends;

/// <summary>Not registered. Trusted composition must supply a runner fixed to the XML child operation.</summary>
internal sealed class XmlCanonicalComparisonWorker : IXmlCanonicalComparisonWorker
{
    private readonly IInspectionWorkerRunner runner;
    private readonly XmlComparisonWorkerOptions options;

    internal XmlCanonicalComparisonWorker(IInspectionWorkerRunner runner, XmlComparisonWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        this.runner = runner;
        this.options = options;
    }

    public async Task<XmlComparisonWorkerResult> CompareAsync(XmlComparisonOutputSet expected, XmlComparisonOutputSet actual,
        OperationStatusV1 executionStatus, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return new(null, XmlComparisonWorkerFailure.Canceled);
        var request = XmlComparisonProtocol.For(expected, actual, executionStatus, options);
        var frame = XmlComparisonProtocol.EncodeRequest(request, expected, actual);
        WorkerProcessResult result;
        try { result = await runner.RunAsync(frame, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(null, XmlComparisonWorkerFailure.Canceled); }
        if (cancellationToken.IsCancellationRequested) return new(null, XmlComparisonWorkerFailure.Canceled);
        if (result.Failure != WorkerProcessFailure.None) return new(null, result.Failure switch
        {
            WorkerProcessFailure.Canceled => XmlComparisonWorkerFailure.Canceled,
            WorkerProcessFailure.TimedOut => XmlComparisonWorkerFailure.TimedOut,
            _ => XmlComparisonWorkerFailure.Unavailable
        });
        try { return new(XmlComparisonProtocol.DecodeResponse(result.Output, request, expected, actual), XmlComparisonWorkerFailure.None); }
        catch (InvalidOperationException) { return new(null, XmlComparisonWorkerFailure.InvalidResponse); }
    }
}
