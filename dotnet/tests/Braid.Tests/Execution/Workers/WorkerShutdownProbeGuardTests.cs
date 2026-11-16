namespace Braid.Tests;

/// <summary>Pure sequencing/identity regressions; no OS process, provider or daemon.</summary>
public sealed class WorkerShutdownProbeGuardTests
{
    private const string Name = "braid-inspect-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static ShutdownTrace Ready => Row(1, "host-ready") with { HostPid = 301, ProcessGroup = 301, StartTicks = 1234 };
    private static ShutdownTrace Create => Row(2, "command-start", "create") with
    { ContainerName = Name, CliPid = 302, ProcessGroup = 301, StartTicks = 5678 };
    private static ShutdownCliIdentity Live => new(302, 5678, 301, false);
    private static ShutdownTrace Row(int sequence, string kind, string? operation = null) =>
        new("braid.worker-shutdown-trace.v1", sequence, kind, operation, null, null, null, null, null, null, null, null, null);

    [Fact]
    public void ExactLiveCreateIdentityIsRequiredImmediatelyBeforeSignal()
    {
        ShutdownTrace[] rows = [Ready, Create];
        var boundary = WorkerShutdownProbeGuards.RequireLiveCreateBeforeSignal(Ready, Create, rows, Live);
        Assert.Equal(new ShutdownSignalBoundary(301, 302, 5678, 301, 2), boundary);
        foreach (var invalid in new[] { Live with { HasExited = true }, Live with { Pid = 303 },
            Live with { StartTicks = 5679 }, Live with { ProcessGroup = 304 } })
            Assert.Throws<InvalidOperationException>(() =>
                WorkerShutdownProbeGuards.RequireLiveCreateBeforeSignal(Ready, Create, rows, invalid));
    }

    [Fact]
    public void PreSignalCreateExitKillRemovalAndHostTerminalRecordsRefuseTheExperiment()
    {
        foreach (var terminal in new[] { Row(3, "command-exit", "create"), Row(3, "command-kill", "create"),
            Row(3, "command-start", "rm"), Row(3, "command-exit", "rm"), Row(3, "host-failed"),
            Row(3, "stopping-entered"), Row(3, "run-terminal") })
            Assert.Throws<InvalidOperationException>(() => WorkerShutdownProbeGuards.RequireLiveCreateBeforeSignal(
                Ready, Create, [Ready, Create, terminal], Live));
    }

    [Fact]
    public void GroupExitCannotSubstituteTimeoutKillOrAnyNonzeroStatusForSigterm()
    {
        var boundary = WorkerShutdownProbeGuards.RequireLiveCreateBeforeSignal(Ready, Create, [Ready, Create], Live);
        var exit = Row(3, "command-exit", "create") with { CliPid = 302, ContainerName = Name, ExitCode = 143 };
        WorkerShutdownProbeGuards.RequireSigtermCreateExit(Create, boundary, [Ready, Create, exit]);
        foreach (var invalid in new[] { exit with { ExitCode = 137 }, exit with { ExitCode = 1 },
            exit with { ExitCode = 0 }, exit with { CliPid = 303 }, exit with { Sequence = 2 } })
            Assert.Throws<InvalidOperationException>(() =>
                WorkerShutdownProbeGuards.RequireSigtermCreateExit(Create, boundary, [Ready, Create, invalid]));
        Assert.Throws<InvalidOperationException>(() => WorkerShutdownProbeGuards.RequireSigtermCreateExit(
            Create, boundary, [Ready, Create, exit, Row(4, "command-kill", "create")]));
    }

    [Fact]
    public async Task LateStoredProxyFailureIsObservedAfterTheLastSettlementAction()
    {
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new IOException("Synthetic late forwarding failure.");
        var settled = false;
        var observed = await Assert.ThrowsAsync<AggregateException>(() => WorkerShutdownProbeGuards.SettleAndObserveProxyAsync(
            () => { settled = true; failure.SetResult(original); return Task.CompletedTask; }, failure.Task));
        Assert.True(settled);
        Assert.Same(original, Assert.Single(observed.InnerExceptions).InnerException);
    }

    [Fact]
    public async Task HealthyPendingProxyFailureTaskIsInspectedOnlyAfterSettlement()
    {
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settlement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = WorkerShutdownProbeGuards.SettleAndObserveProxyAsync(() => settlement.Task, failure.Task);
        Assert.False(observation.IsCompleted);
        settlement.SetResult();
        await observation;
        Assert.False(failure.Task.IsCompleted);
    }

    [Fact]
    public async Task SettlementFailureDoesNotHideTheIndependentlyRetainedProxyFailure()
    {
        var disposal = new IOException("Synthetic observer disposal failure.");
        var stored = new IOException("Synthetic stored forwarding failure.");
        var observed = await Assert.ThrowsAsync<AggregateException>(() => WorkerShutdownProbeGuards.SettleAndObserveProxyAsync(
            () => Task.FromException(disposal), Task.FromResult<Exception>(stored)));
        Assert.Equal(2, observed.InnerExceptions.Count);
        Assert.Same(disposal, observed.InnerExceptions[0]);
        Assert.Same(stored, observed.InnerExceptions[1].InnerException);
    }

    [Fact]
    public async Task FailedReapStillAttemptsEveryRemainingDistinctAttributedIdentity()
    {
        var first = Create;
        var second = Create with { Sequence = 3, CliPid = 303, StartTicks = 5679 };
        var third = Create with { Sequence = 4, CliPid = 304, StartTicks = 5680 };
        var attempted = new List<int>();
        var timeout = new TimeoutException("Synthetic first identity timeout.");
        var later = new IOException("Synthetic second identity failure.");
        var observed = await Assert.ThrowsAsync<AggregateException>(() => WorkerShutdownProbeGuards.JoinAllExactCliProcessesAsync(
            [first, second, third, second], row =>
            {
                attempted.Add(row.CliPid!.Value);
                return row.CliPid == 302 ? Task.FromException(timeout) :
                    row.CliPid == 303 ? Task.FromException(later) : Task.CompletedTask;
            }));
        Assert.Equal([302, 303, 304], attempted);
        Assert.Equal(2, observed.InnerExceptions.Count);
        Assert.Same(timeout, observed.InnerExceptions[0].InnerException);
        Assert.Same(later, observed.InnerExceptions[1].InnerException);
    }

    [Fact]
    public async Task TimedOutIdentityAttemptsBothKillAndReapEvenWhenEachIndependentlyFails()
    {
        var calls = new List<string>();
        var timeout = new TimeoutException("Synthetic initial wait timeout.");
        var kill = new IOException("Synthetic exact kill failure.");
        var reap = new TimeoutException("Synthetic final reap timeout.");
        var observed = await Assert.ThrowsAsync<AggregateException>(() => WorkerShutdownProbeGuards.WaitKillAndReapAsync(
            () => { calls.Add("wait"); return Task.FromException(timeout); },
            () => { calls.Add("kill"); throw kill; },
            () => { calls.Add("reap"); return Task.FromException(reap); }));
        Assert.Equal(["wait", "kill", "reap"], calls);
        Assert.Equal(3, observed.InnerExceptions.Count);
        Assert.Same(timeout, observed.InnerExceptions[0]);
        Assert.Same(kill, observed.InnerExceptions[1]);
        Assert.Same(reap, observed.InnerExceptions[2]);
    }
}
