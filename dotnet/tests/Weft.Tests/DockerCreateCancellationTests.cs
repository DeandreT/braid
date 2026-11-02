using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class DockerCreateCancellationTests
{
    [Fact]
    public async Task Shutdown_is_observed_at_create_settlement_before_linked_callbacks_are_delivered()
    {
        // Keep the control thread independent of the callback's deliberately
        // blocked ThreadPool worker, including on a two-CPU CI runner.
        await Task.Factory.StartNew(async () =>
        {
            using var shutdown = new CancellationTokenSource();
            using var caller = new CancellationTokenSource();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, shutdown.Token);
            using var wall = new CancellationTokenSource();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, wall.Token);
            using var releaseCallback = new ManualResetEventSlim();
            using var entered = new ManualResetEventSlim();
            // Registrations run last-in-first-out. Hold shutdown's callback worker
            // before it can propagate to the operation and its deadline.
            using var registration = shutdown.Token.Register(() =>
            {
                entered.Set();
                releaseCallback.Wait();
            });
            var cancel = shutdown.CancelAsync();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
                Assert.True(shutdown.IsCancellationRequested);
                Assert.False(lifetime.IsCancellationRequested);
                Assert.False(deadline.IsCancellationRequested);
                // Negative control: the former linked-token-only checkpoint misses stop.
                deadline.Token.ThrowIfCancellationRequested();
                Assert.Throws<OperationCanceledException>(() =>
                    DockerInspectionWorkerRunner.ObserveCreateCancellation(deadline.Token, shutdown.Token));
                Assert.Equal(WorkerProcessFailure.Canceled,
                    DockerInspectionWorkerRunner.CancellationFailure(lifetime.Token, shutdown.Token));
            }
            finally
            {
                releaseCallback.Set();
                await cancel.WaitAsync(TimeSpan.FromSeconds(1));
            }
            Assert.True(deadline.IsCancellationRequested);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    [Theory]
    [InlineData(false, false, WorkerProcessFailure.TimedOut)]
    [InlineData(true, false, WorkerProcessFailure.Canceled)]
    [InlineData(false, true, WorkerProcessFailure.Canceled)]
    [InlineData(true, true, WorkerProcessFailure.Canceled)]
    public void Only_caller_or_shutdown_cancellation_changes_timeout_classification(bool callerCanceled, bool shutdownCanceled,
        WorkerProcessFailure expected)
    {
        using var caller = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        if (callerCanceled) caller.Cancel();
        if (shutdownCanceled) shutdown.Cancel();
        Assert.Equal(expected, DockerInspectionWorkerRunner.CancellationFailure(caller.Token, shutdown.Token));
        if (!shutdownCanceled)
        {
            DockerInspectionWorkerRunner.ObserveCreateCancellation(deadline.Token, shutdown.Token);
            deadline.Cancel();
        }
        Assert.Throws<OperationCanceledException>(() =>
            DockerInspectionWorkerRunner.ObserveCreateCancellation(deadline.Token, shutdown.Token));
    }
}
