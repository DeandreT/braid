namespace Weft.Tests;

/// <summary>Source-owned oracles/cleanup sequencing; these helpers start no process.</summary>
internal static class WorkerShutdownProbeGuards
{
    internal static ShutdownSignalBoundary RequireLiveCreateBeforeSignal(ShutdownTrace ready, ShutdownTrace create,
        IReadOnlyList<ShutdownTrace> rows, ShutdownCliIdentity identity)
    {
        if (ready.Kind != "host-ready" || ready.HostPid is not > 1 || ready.StartTicks is not > 0 ||
            ready.ProcessGroup != ready.HostPid || create.Kind != "command-start" || create.Operation != "create" ||
            create.CliPid is not > 1 || create.StartTicks is not > 0 || create.ProcessGroup != ready.ProcessGroup ||
            create.ContainerName is not { Length: 45 } name || !name.StartsWith("weft-inspect-", StringComparison.Ordinal) ||
            !name["weft-inspect-".Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidOperationException("The experiment has no complete owned host/create identity.");
        if (identity.HasExited || identity.Pid != create.CliPid || identity.StartTicks != create.StartTicks ||
            identity.ProcessGroup != ready.ProcessGroup)
            throw new InvalidOperationException("The exact captured create CLI is no longer live before the experiment signal.");
        var starts = rows.Where(row => row.Kind == "command-start" && row.Operation == "create").ToArray();
        if (starts.Length != 1 || starts[0] != create || rows.Any(IsPreSignalTerminal))
            throw new InvalidOperationException("Create termination, removal or host shutdown preceded the experiment signal.");
        return new(ready.HostPid.Value, create.CliPid.Value, create.StartTicks.Value,
            ready.ProcessGroup!.Value, rows.Max(row => row.Sequence));
    }

    private static bool IsPreSignalTerminal(ShutdownTrace row) =>
        (row.Operation == "create" && (row.Kind is "command-exit" or "command-kill")) ||
        ((row.Operation is "rm" or "inspect" or "start") && (row.Kind is "command-start" or "command-exit" or "command-kill")) ||
        row.Kind is "stopping-entered" or "hosted-stop" or "hosted-stopped" or "host-stop-returned" or
            "run-terminal" or "host-failed" or "provider-disposed" or "provider-disposal-failed" or "queued-terminal" or "queued-failed";

    internal static void RequireSigtermCreateExit(ShutdownTrace create, ShutdownSignalBoundary boundary,
        IReadOnlyList<ShutdownTrace> rows)
    {
        var exits = rows.Where(row => row.Kind == "command-exit" && row.Operation == "create").ToArray();
        // Linux Process.ExitCode represents SIGTERM as 128+15. A wall-deadline
        // SIGKILL/nonzero exit is not evidence of the experiment's SIGTERM.
        if (exits.Length != 1 || exits[0].CliPid != boundary.CreatePid || exits[0].ContainerName != create.ContainerName ||
            exits[0].Sequence <= boundary.LastObservedSequence || exits[0].ExitCode != 143 ||
            rows.Any(row => row.Kind == "command-kill" && row.Operation == "create"))
            throw new InvalidOperationException("Create termination is not attributable to the experiment SIGTERM.");
    }

    internal static async Task SettleAndObserveProxyAsync(Func<Task> settleAll, Task<Exception> retainedFailure)
    {
        var failures = new List<Exception>();
        try { await settleAll(); }
        catch (Exception error) { failures.Add(error); }
        // The proxy reports many forwarding failures through this retained task,
        // not through DisposeAsync. Pending is its healthy terminal observation.
        if (retainedFailure.IsCompletedSuccessfully)
            failures.Add(new InvalidOperationException("The owned proxy retained a failure after settlement.", retainedFailure.Result));
        else if (retainedFailure.IsCompleted)
            failures.Add(new InvalidOperationException("The owned proxy failure observer did not retain its normal task state.", retainedFailure.Exception));
        if (failures.Count != 0) throw new AggregateException("Owned proxy settlement or retained observation failed.", failures);
    }

    internal static async Task JoinAllExactCliProcessesAsync(IReadOnlyList<ShutdownTrace> rows,
        Func<ShutdownTrace, Task> joinExactIdentity)
    {
        var failures = new List<Exception>();
        foreach (var row in rows.Where(row => row.Kind == "command-start" && row.CliPid > 1 && row.StartTicks > 0)
            .DistinctBy(row => (row.CliPid, row.StartTicks)))
        {
            try { await joinExactIdentity(row); }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException($"Exact CLI cleanup failed for PID {row.CliPid}, start {row.StartTicks}.", error));
            }
        }
        if (failures.Count != 0) throw new AggregateException("One or more exact attributed CLIs did not settle.", failures);
    }

    internal static async Task WaitKillAndReapAsync(Func<Task> initialWait, Action killIfSameIdentity, Func<Task> finalReap)
    {
        try { await initialWait(); }
        catch (TimeoutException original)
        {
            // Cleanup is still failure even if the kill/reap succeeds. Independent
            // cleanup failures do not skip the next step or the next CLI identity.
            var failures = new List<Exception> { original };
            try { killIfSameIdentity(); } catch (Exception error) { failures.Add(error); }
            try { await finalReap(); } catch (Exception error) { failures.Add(error); }
            throw new AggregateException("The exact attributed CLI exceeded its original reap wait.", failures);
        }
    }
}

internal sealed record ShutdownCliIdentity(int Pid, long StartTicks, int ProcessGroup, bool HasExited);
internal sealed record ShutdownSignalBoundary(int HostPid, int CreatePid, long CreateStartTicks,
    int ProcessGroup, int LastObservedSequence);
