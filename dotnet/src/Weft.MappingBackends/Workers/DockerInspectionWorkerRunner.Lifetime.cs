using Weft.Application;

namespace Weft.MappingBackends.Workers;

public sealed partial class DockerInspectionWorkerRunner : IExecutionAdmission
{
    private readonly object lifetimeGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<OwnedContainer> unconfirmed = [];
    private Task? stopOperation;
    private Task? reaper;
    private int outstandingOperations;
    private int lifetimeDisposed;
    private bool stopping;

    /// <summary>
    /// False once admission closed: stopping, a container whose removal the reaper has not yet confirmed, or an
    /// unconfirmed CLI exit or private directory removal, which keeps the runner closed until restart.
    /// </summary>
    public bool Available { get { lock (lifetimeGate) return !stopping && !Closed; } }
    public bool CanExecute => Available;

    private bool Closed { get { lock (lifetimeGate) return Volatile.Read(ref unavailable) != 0 || unconfirmed.Count != 0; } }

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

    /// <summary>Holds admission closed until the exact owned container is confirmed removed or absent.</summary>
    private void Reap(OwnedContainer container)
    {
        lock (lifetimeGate)
        {
            unconfirmed.Add(container);
            if (stopping || reaper is not null) return;
            // Recovery belongs to the host, not the failed request's identity or execution context.
            if (ExecutionContext.IsFlowSuppressed()) reaper = Task.Run(ReapAsync);
            else using (ExecutionContext.SuppressFlow()) reaper = Task.Run(ReapAsync);
        }
    }

    private async Task ReapAsync()
    {
        var delay = options.ReaperInitialDelay;
        try
        {
            while (true)
            {
                await Task.Delay(delay, shutdown.Token).ConfigureAwait(false);
                OwnedContainer[] attempt;
                lock (lifetimeGate) attempt = [.. unconfirmed];
                foreach (var container in attempt)
                {
                    var (confirmed, next) = await ReapOnceAsync(container).ConfigureAwait(false);
                    lock (lifetimeGate)
                    {
                        var index = unconfirmed.IndexOf(container);
                        if (confirmed) unconfirmed.RemoveAt(index);
                        else unconfirmed[index] = next;
                    }
                }

                lock (lifetimeGate)
                {
                    if (unconfirmed.Count == 0)
                    {
                        reaper = null;
                        return;
                    }
                }

                delay = delay * 2 < options.ReaperMaximumDelay ? delay * 2 : options.ReaperMaximumDelay;
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception)
        {
            // An unexpected reaper fault proves nothing about the container: stay closed until restart.
            Interlocked.Exchange(ref unavailable, 1);
        }
    }

    private async Task<(bool Confirmed, OwnedContainer Next)> ReapOnceAsync(OwnedContainer container)
    {
        string? configDirectory = null;
        try
        {
            configDirectory = Directory.CreateTempSubdirectory("weft-docker-").FullName;
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(configDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // Never create a replacement to settle a lost acknowledgement. A second create can reserve and
            // release the name before the original daemon request publishes its container. Only discovering
            // the original marked container and confirming its exact-ID removal settles that uncertainty.
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            cleanup.CancelAfter(options.CleanupTimeout);
            return (await RemoveOwnedContainerAsync(configDirectory, container.Name, container.Owner, container.Id, cleanup.Token)
                .ConfigureAwait(false), container);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, container);
        }
        finally
        {
            if (configDirectory is not null)
            {
                try { Directory.Delete(configDirectory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Exchange(ref unavailable, 1);
                }
            }
        }
    }

    /// <summary>
    /// Closes admission and joins the original queued/active operations, including their
    /// original create and cleanup budgets, and the reaper. This task owns settlement, not any host waiter.
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
        finally
        {
            await drained.Task.ConfigureAwait(false);
            Task? reaping;
            lock (lifetimeGate) reaping = reaper;
            if (reaping is not null) await reaping.ConfigureAwait(false);
        }
        if (Closed)
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

    /// <summary>The exact identity one operation created: its generated name, its marker and, once acknowledged, its ID.</summary>
    private sealed record OwnedContainer(string Name, string Owner, string? Id);
}
