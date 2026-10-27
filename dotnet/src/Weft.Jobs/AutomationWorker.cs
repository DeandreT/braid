using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weft.Application;
using Weft.Application.Automation;
using Weft.Application.Regression;

namespace Weft.Jobs;

/// <summary>One independent lease per case/delivery; a bad item never prevents the next item's admission.</summary>
public sealed class AutomationWorker(IAutomationQueue queue, IRegressionEvaluationExecutor execution,
    INotificationTransport notifications, IAutomationComparisonSettler comparisons, ILogger<AutomationWorker> logger) : BackgroundService
{
    public async Task DrainOnceAsync(CancellationToken token)
    {
        if (execution is not IExecutionAdmission { CanExecute: false })
        {
            var claim = await queue.ClaimEvaluationAsync(token).ConfigureAwait(false);
            if (claim is not null)
            {
                RegressionCaseExecution? result = null;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                try
                {
                    result = await execution.EvaluateAsync(new(claim.Actor), claim.Original, claim.Target, deadline.Token).ConfigureAwait(false);
                    if (result.Outcome == RegressionCaseExecutionOutcome.InfrastructureFailure) result = null;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { }
                await queue.FinishEvaluationAsync(claim, result, token).ConfigureAwait(false);
            }
        }
        await comparisons.SettleNextAsync(token).ConfigureAwait(false);
        var delivery = await queue.ClaimDeliveryAsync(token).ConfigureAwait(false);
        if (delivery is null) return;
        var outcome = NotificationDeliveryOutcome.TransportFailure;
        try { outcome = await notifications.SendAsync(delivery, token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { }
        await queue.FinishDeliveryAsync(delivery, outcome, token).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DrainOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { logger.LogWarning("Automation left an item retryable."); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
