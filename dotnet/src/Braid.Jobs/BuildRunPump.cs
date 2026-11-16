using Microsoft.Extensions.Hosting;
using Braid.Deployment;

namespace Braid.Jobs;

/// <summary>Serial bounded discovery; each durable build has its own claim, cancellation and failure boundary.</summary>
public sealed class BuildRunPump(IBuildRunQueue queue, BuildRunExecutor executor)
{
    private readonly Guid worker = Guid.NewGuid();
    public const int BatchSize = 4;

    public async Task<int> RunOnceAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<BuildRunKey> keys;
        try { keys = await queue.DiscoverAsync(BatchSize, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (Ordinary(error)) { return 0; }
        if (keys is null || keys.Count > BatchSize) return 0;
        var handled = 0;
        foreach (var key in keys.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (key is null || key.OrganizationId == Guid.Empty || key.BuildId == Guid.Empty) continue;
            try
            {
                if (await executor.ExecuteAsync(key, worker, token).ConfigureAwait(false) == BuildRunDispatchOutcome.Completed) handled++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (Ordinary(error)) { /* A failed build cannot stop later independent work. */ }
        }
        return handled;
    }

    private static bool Ordinary(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}

public sealed class BuildWorkerService(BuildRunPump pump) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do { await pump.RunOnceAsync(stoppingToken).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
