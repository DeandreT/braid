using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Braid.Application.Regression;

namespace Braid.Jobs;

public sealed record RegressionDispatchBatch(int Ready, int Settled, int Deferred, int Failed);

/// <summary>Independent sequential case boundaries; discovery failure never acknowledges a run.</summary>
public sealed class RegressionRunPump(IRegressionRunQueue queue, IRegressionRunDispatcher dispatcher)
{
    private readonly Guid worker = Guid.NewGuid();

    public async Task<RegressionDispatchBatch> DrainOnceAsync(CancellationToken token = default)
    {
        var items = await queue.ListReadyAsync(16, token).ConfigureAwait(false);
        if (items is null || items.Count > 16) throw new InvalidOperationException("Regression dispatch page refused.");
        var settled = 0;
        var deferred = 0;
        var failed = 0;
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (item is null || item.OrganizationId == Guid.Empty || item.RunId == Guid.Empty)
                    throw new InvalidOperationException("Regression dispatch identity refused.");
                var outcome = await dispatcher.ExecuteAsync(item.OrganizationId, item.RunId, worker, token).ConfigureAwait(false);
                switch (outcome)
                {
                    case RegressionRunDispatchOutcome.Completed or RegressionRunDispatchOutcome.Finished: settled++; break;
                    case RegressionRunDispatchOutcome.Deferred or RegressionRunDispatchOutcome.ClaimLost: deferred++; break;
                    case RegressionRunDispatchOutcome.Missing: failed++; break;
                    default: throw new InvalidOperationException("Regression dispatch outcome refused.");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                // No payload or dependency text is retained. The unchanged row remains retryable.
                failed++;
            }
        }
        return new(items.Count, settled, deferred, failed);
    }
}

/// <summary>Default-off host component; a committed row remains discoverable across process restarts.</summary>
public sealed class RegressionWorkerService(RegressionRunPump pump, ILogger<RegressionWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await pump.DrainOnceAsync(stoppingToken).ConfigureAwait(false);
                if (batch.Failed > 0) logger.LogWarning("Regression dispatch left {Count} runs retryable.", batch.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                logger.LogWarning("Regression dispatch could not read the durable queue.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
