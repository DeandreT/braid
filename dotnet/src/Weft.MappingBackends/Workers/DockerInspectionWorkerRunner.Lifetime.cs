namespace Weft.MappingBackends.Workers;

public sealed partial class DockerInspectionWorkerRunner
{
    private readonly object lifetimeGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? stopOperation;
    private int outstandingOperations;
    private int lifetimeDisposed;
    private bool stopping;

    /// <summary>False once admission closed: stopping, or unconfirmed cleanup, which keeps the runner closed until restart.</summary>
    public bool Available { get { lock (lifetimeGate) return !stopping && Volatile.Read(ref unavailable) == 0; } }

    public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
    {
        if (requestFrame.Length > options.Profile.MaximumRequestBytes)
            throw new ArgumentException("The request frame exceeds the fixed transport limit.", nameof(requestFrame));

        lock (lifetimeGate)
        {
            if (stopping) return Failed(WorkerProcessFailure.SandboxUnavailable);
            outstandingOperations++;
        }
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            return await RunOwnedAsync(requestFrame, lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            // An exceptional original operation cannot become successful host cleanup
            // merely because its lease has ended. Preserve its exception for its caller.
            Interlocked.Exchange(ref unavailable, 1);
            throw;
        }
        finally
        {
            lock (lifetimeGate)
            {
                outstandingOperations--;
                if (stopping && outstandingOperations == 0) drained.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Closes admission and joins the original queued/active operations, including their
    /// original create and cleanup budgets. This task owns settlement, not any host waiter.
    /// </summary>
    public Task StopAsync()
    {
        lock (lifetimeGate)
        {
            if (stopOperation is not null) return stopOperation;
            stopping = true;
            if (outstandingOperations == 0) drained.TrySetResult();
            return stopOperation = StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        // CancelAsync does not execute callbacks under lifetimeGate. Regardless of a
        // callback failure, dependencies remain owned until the counted original work ends.
        try { await shutdown.CancelAsync().ConfigureAwait(false); }
        finally { await drained.Task.ConfigureAwait(false); }
        if (Volatile.Read(ref unavailable) != 0)
            throw new InvalidOperationException("Inspection worker shutdown could not confirm safe cleanup.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            if (Interlocked.Exchange(ref lifetimeDisposed, 1) == 0)
            {
                admission.Dispose();
                shutdown.Dispose();
            }
        }
    }
}
