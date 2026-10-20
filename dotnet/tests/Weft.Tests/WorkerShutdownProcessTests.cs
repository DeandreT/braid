using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Weft.Tests;

/// <summary>
/// Attributable generic-host shutdown controls using the real daemon behind the creation gate.
/// They qualify host lifetime ownership only, not browser teardown.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class WorkerShutdownProcessTests
{
    // Bounds owned teardown settlement only; operation budgets and oracles are unchanged.
    // A saturated daemon can answer a released create minutes later, and an unobserved
    // publication would leave this control's own container behind.
    internal static readonly TimeSpan SettlementTimeout = TimeSpan.FromMinutes(3);

    [FerruleWorkerIntegrationTests.WorkerFact]
    public Task UnownedHostControlExitsBeforeItsCapturedCreatePublishes() => RunAsync("unowned-control", groupSignal: false);

    [FerruleWorkerIntegrationTests.WorkerFact]
    public Task OwnedHostOnlyTerminationDrainsOriginalCreateBeforeProviderDisposal() => RunAsync("owned-drain", groupSignal: false);

    [FerruleWorkerIntegrationTests.WorkerFact]
    public Task ProcessGroupInterruptionIsAnAttributedUncertainCleanupFailureNotAFalseAbsence() => RunAsync("owned-drain", groupSignal: true);

    private static async Task RunAsync(string mode, bool groupSignal)
    {
        // The private case directory holds the child's TMPDIR and trace; it is retained only on failure.
        var resultRoot = Directory.CreateTempSubdirectory("weft-worker-shutdown-").FullName;
        File.SetUnixFileMode(resultRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var events = await WorkerShutdownEvents.OpenAsync(deadline.Token);
        await using var proxy = new DockerCreationGateProxy();
        await using var child = await ShutdownProbeProcess.StartAsync(mode, proxy.SocketPath, resultRoot, deadline.Token);
        DockerCreationGateProxy.PublishedCreation? publication = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            var ready = await child.WaitAsync(row => row.Kind == "host-ready", deadline.Token);
            var name = await proxy.CapturedCreate.Task.WaitAsync(deadline.Token);
            var create = await child.WaitAsync(row => row.Kind == "command-start" && row.Operation == "create", deadline.Token);
            await child.WaitAsync(row => row.Kind == "queue-owned", deadline.Token);
            Assert.Equal(name, create.ContainerName);
            Assert.Equal(ready.HostPid, ready.ProcessGroup);
            Assert.Equal(ready.ProcessGroup, create.ProcessGroup);
            Assert.True(create.CliPid > 1 && create.StartTicks > 0);
            Assert.False(proxy.PublishedCreate.Task.IsCompleted);
            Assert.False(child.Exit.IsCompleted);
            var signalBoundary = child.SignalDuringCreate(ready, create,
                groupSignal ? -ready.ProcessGroup!.Value : ready.HostPid!.Value);
            await child.WaitAsync(row => row.Kind == "stopping-entered", deadline.Token);

            if (mode == "owned-drain" && !groupSignal)
            {
                var queued = await child.WaitAsync(row => row.Kind == "queued-terminal", deadline.Token);
                Assert.Equal("Canceled", queued.Failure);
                // A lifecycle checkpoint, not a sleep, proves the original drain is held.
                Assert.False(child.Exit.IsCompleted);
                Assert.DoesNotContain(child.Rows, row => row.Kind == "command-start" && row.Operation is "rm" or "start" or "inspect");
                Assert.DoesNotContain(child.Rows, row => row.Kind == "host-stop-returned" || row.Kind == "provider-disposed");
                proxy.ReleaseCreate();
                publication = await proxy.PublishedCreate.Task.WaitAsync(deadline.Token);
                var created = await events.WaitAsync(publication.ContainerId, "create", deadline.Token);
                var destroyed = await events.WaitAsync(publication.ContainerId, "destroy", deadline.Token);
                Assert.Equal(name, created.Name);
                Assert.Equal(Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE"), created.Image);
                Assert.Equal(name, destroyed.Name);
                Assert.Equal(0, await child.Exit.WaitAsync(deadline.Token));
                await child.JoinOutputAsync(deadline.Token);
                Assert.Contains(child.Rows, row => row.Kind == "run-terminal" && row.Failure == "Canceled");
                Assert.Contains(child.Rows, row => row.Kind == "provider-disposed");
                Assert.DoesNotContain(child.Rows, row => row.Kind == "host-failed" || row.Kind == "provider-disposal-failed");
                var remove = Assert.Single(child.Rows, row => row.Kind == "command-exit" && row.Operation == "rm");
                var disposed = Assert.Single(child.Rows, row => row.Kind == "provider-disposed");
                Assert.True(remove.Sequence < disposed.Sequence);
                Assert.Equal(0, remove.ExitCode);
                Assert.DoesNotContain(child.Rows, row => row.Kind == "command-start" && row.Operation == "start");
                // The trace tool calls both inspect formats "inspect". In this
                // canceled branch only the ownership-only exact-ID read may run.
                var ownership = Assert.Single(child.Rows, row => row.Kind == "command-start" && row.Operation == "inspect");
                Assert.Equal(publication.ContainerId, ownership.ContainerName);
                Assert.Equal(publication.ContainerId, remove.ContainerName);
                Assert.DoesNotContain(events.Rows(publication.ContainerId), row => row.Action == "start");
                await AssertAbsentAsync(publication.ContainerId, deadline.Token);
            }
            else
            {
                // The original request is published only AFTER the owned host is terminal.
                // A late Created resource here is causally attributed, not timestamp inferred.
                Assert.Equal(groupSignal ? 6 : 3, await child.Exit.WaitAsync(deadline.Token));
                await child.JoinOutputAsync(deadline.Token);
                Assert.False(proxy.PublishedCreate.Task.IsCompleted);
                Assert.DoesNotContain(child.Rows, row => row.Kind == "command-start" && row.Operation == "start");
                if (groupSignal)
                {
                    WorkerShutdownProbeGuards.RequireSigtermCreateExit(create, signalBoundary, child.Rows);
                    Assert.Contains(child.Rows, row => row.Kind == "command-exit" && row.Operation == "inspect");
                    Assert.DoesNotContain(child.Rows, row => row.Kind == "command-start" && row.Operation == "rm");
                    Assert.Contains(child.Rows, row => row.Kind == "host-failed");
                    Assert.Contains(child.Rows, row => row.Kind == "provider-disposal-failed");
                }
                else
                    Assert.Contains(child.Rows, row => row.Kind == "host-stop-returned" && row.Unsettled == true);
                proxy.ReleaseCreate();
                publication = await proxy.PublishedCreate.Task.WaitAsync(deadline.Token);
                var created = await events.WaitAsync(publication.ContainerId, "create", deadline.Token);
                Assert.Equal(name, created.Name);
                Assert.Equal(Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE"), created.Image);
                var state = await DockerAsync(["container", "inspect", "--format", "{{.Id}} {{.Name}} {{.State.Status}} {{.State.Pid}}", publication.ContainerId], deadline.Token);
                Assert.Equal(0, state.Code);
                Assert.Equal(publication.ContainerId + " /" + name + " created 0", state.Output.Trim());
                Assert.DoesNotContain(events.Rows(publication.ContainerId), row => row.Action == "start");
            }
            Assert.Single(child.Rows, row => row.Kind == "command-start" && row.Operation == "create");
            Assert.All(child.Rows.Where(row => row.Kind == "command-exit"), row => Assert.Equal(0L, row.InputBytes ?? -1));
        }
        catch (Exception error) { primary = error; }
        finally
        {
            // Preserve the primary observation even if independent cleanup also fails.
            await Clean(async () => await child.DisposeAsync());
            proxy.ReleaseCreate();
            if (proxy.CapturedCreate.Task.IsCompletedSuccessfully)
            {
                // A loaded daemon can publish the released create long after its CLI is
                // gone. Settle that exact publication before any absence is attributed.
                await Clean(async () => { publication ??= await proxy.PublishedCreate.Task.WaitAsync(SettlementTimeout); });
                await Clean(async () => await proxy.CreateForwardCompleted.Task.WaitAsync(SettlementTimeout));
            }
            await Clean(async () => await child.JoinExactCliProcessesAsync());
            if (publication is not null)
            {
                await Clean(async () =>
                {
                    using var bound = new CancellationTokenSource(SettlementTimeout);
                    Assert.True(proxy.CapturedOwner.Task.IsCompletedSuccessfully);
                    var owner = await proxy.CapturedOwner.Task;
                    var inspected = await DockerAsync(["container", "inspect", "--format", DockerWorkerOwnershipTestSupport.InspectFormat, publication.ContainerId], bound.Token);
                    var removedByParent = false;
                    if (inspected.Code == 0)
                    {
                        Assert.Equal(publication.ContainerId,
                            DockerWorkerOwnershipTestSupport.VerifiedId(inspected.Output, publication.Name, owner, publication.ContainerId));
                        var removal = await DockerAsync(["rm", "--force", "--volumes", publication.ContainerId], bound.Token);
                        Assert.Equal(0, removal.Code);
                        Assert.Equal(publication.ContainerId, removal.Output.Trim());
                        removedByParent = true;
                    }
                    else
                        AssertMissing(inspected, publication.ContainerId);
                    await AssertAbsentAsync(publication.ContainerId, bound.Token);
                    if (removedByParent) await events.WaitAsync(publication.ContainerId, "destroy", bound.Token);
                    await File.WriteAllTextAsync(Path.Combine(resultRoot, "events.json"), JsonSerializer.Serialize(events.Rows(publication.ContainerId)));
                });
            }
            // Forwarding may retain an error without throwing from disposal. Inspect
            // that original failure task only after ALL observer/work cleanup joins.
            await Clean(async () => await WorkerShutdownProbeGuards.SettleAndObserveProxyAsync(async () =>
            {
                await Clean(async () => await proxy.DisposeAsync());
                await Clean(async () => await events.DisposeAsync());
            }, proxy.Failure.Task));
        }
        if (primary is not null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0)
            throw new AggregateException($"Owned worker-shutdown control or settlement failed; evidence retained in {resultRoot}.", cleanup);
        Directory.Delete(resultRoot, recursive: true);

        async Task Clean(Func<Task> action)
        { try { await action(); } catch (Exception error) { cleanup.Add(error); } }
    }

    private static async Task AssertAbsentAsync(string id, CancellationToken cancellationToken) =>
        AssertMissing(await DockerAsync(["container", "inspect", "--format", "{{.Id}}", id], cancellationToken), id);

    private static void AssertMissing(DockerResult result, string id)
    {
        Assert.NotEqual(0, result.Code);
        Assert.Equal("", result.Output.Trim());
        Assert.Contains(result.Error.Trim(), new[] { "Error: No such object: " + id, "Error: No such container: " + id,
            "Error response from daemon: No such container: " + id });
    }

    private static async Task<DockerResult> DockerAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("weft-shutdown-control-").FullName;
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo("/usr/bin/docker")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        start.Environment.Clear();
        start.Environment["LC_ALL"] = "C.UTF-8";
        start.Environment["HOME"] = directory;
        start.Environment["DOCKER_CONFIG"] = directory;
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add("--host=unix:///var/run/docker.sock");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Owned Docker observation did not start.");
            var stdout = ReadBoundedAsync(process.StandardOutput, 65536, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError, 65536, cancellationToken);
            try
            {
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken));
                return new(process.ExitCode, await stdout, await stderr);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[1024];
        while (await reader.ReadAsync(buffer, token) is var count && count != 0)
        {
            if (result.Length + count > maximum) throw new InvalidOperationException("Owned observation exceeded its bound.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private sealed record DockerResult(int Code, string Output, string Error);
}

[SupportedOSPlatform("linux")]
internal sealed class ShutdownProbeProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task stdout, stderr;
    private readonly List<ShutdownTrace> rows = [];
    private readonly object gate = new();
    private TaskCompletionSource changed = NewSignal();
    private Exception? traceFailure;
    private bool traceEnded;
    private int disposed;
    private ShutdownProbeProcess(Process process, string resultRoot, CancellationToken token)
    {
        this.process = process;
        Exit = ObserveExitAsync();
        stdout = ReadTraceAsync(resultRoot, token);
        stderr = CheckErrorAsync(token);
    }
    internal Task<int> Exit { get; }
    internal ShutdownTrace[] Rows { get { lock (gate) return rows.ToArray(); } }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task<ShutdownProbeProcess> StartAsync(string mode, string socket, string resultRoot, CancellationToken token)
    {
        var executable = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Assert.True(executable is not null && Path.IsPathFullyQualified(executable));
        var probe = Path.Combine(AppContext.BaseDirectory, "WorkerShutdownProbe", "Weft.WorkerShutdownProbe.dll");
        Assert.True(File.Exists(probe));
        var start = new ProcessStartInfo("/usr/bin/setsid")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = resultRoot
        };
        start.Environment.Clear();
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(executable)!;
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["LC_ALL"] = "C.UTF-8";
        var scratch = Path.Combine(resultRoot, "scratch");
        Directory.CreateDirectory(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        start.Environment["TMPDIR"] = scratch;
        if (Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT") is { } processors)
            start.Environment["DOTNET_PROCESSOR_COUNT"] = processors;
        foreach (var argument in new[] { "--fork", "--wait", executable!, probe }) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Owned shutdown probe did not start.");
        var result = new ShutdownProbeProcess(process, resultRoot, token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = "weft.worker-shutdown-probe.v1",
                mode,
                imageId = Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE"),
                socketPath = socket
            }));
            process.StandardInput.Close();
            return result;
        }
        catch (Exception primary)
        {
            try { await result.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
            throw;
        }
    }

    internal async Task<ShutdownTrace> WaitAsync(Func<ShutdownTrace, bool> predicate, CancellationToken token)
    {
        while (true)
        {
            Task next;
            lock (gate)
            {
                if (traceFailure is not null) throw new InvalidOperationException("Owned trace failed.", traceFailure);
                if (rows.FirstOrDefault(predicate) is { } match) return match;
                if (traceEnded) throw new InvalidOperationException("Owned trace ended before its required checkpoint.");
                next = changed.Task;
            }
            await next.WaitAsync(token);
        }
    }

    internal void Signal(ShutdownTrace ready, int target, int signal)
    {
        Assert.True(ready.HostPid > 1 && ready.ProcessGroup == ready.HostPid && ready.StartTicks > 0);
        using var host = Process.GetProcessById(ready.HostPid!.Value);
        Assert.False(host.HasExited);
        Assert.Equal(ready.StartTicks, ReadStartTicks(host.Id));
        Assert.Equal(ready.ProcessGroup, GetProcessGroup(ready.HostPid.Value));
        Assert.True(target == ready.HostPid || target == -ready.ProcessGroup);
        Assert.Equal(0, SendSignal(target, signal));
    }

    internal ShutdownSignalBoundary SignalDuringCreate(ShutdownTrace ready, ShutdownTrace create, int target)
    {
        Assert.True(ready.HostPid > 1 && ready.ProcessGroup == ready.HostPid && ready.StartTicks > 0);
        using var host = Process.GetProcessById(ready.HostPid!.Value);
        Assert.False(host.HasExited);
        Assert.Equal(ready.StartTicks, ReadStartTicks(host.Id));
        Assert.Equal(ready.ProcessGroup, GetProcessGroup(ready.HostPid.Value));
        Assert.True(target == ready.HostPid || target == -ready.ProcessGroup);
        Assert.True(create.CliPid > 1 && create.StartTicks > 0);
        using var cli = Process.GetProcessById(create.CliPid!.Value);
        cli.Refresh();
        var rowsBeforeSignal = Rows;
        // This final live PID/start/group check precedes the syscall directly: no
        // await, file write, publication release or operation-budget extension.
        var identity = new ShutdownCliIdentity(cli.Id, ReadStartTicks(cli.Id) ?? -1,
            GetProcessGroup(cli.Id), cli.HasExited);
        var boundary = WorkerShutdownProbeGuards.RequireLiveCreateBeforeSignal(ready, create, rowsBeforeSignal, identity);
        Assert.Equal(0, SendSignal(target, signal: 15));
        return boundary;
    }

    private async Task<int> ObserveExitAsync() { await process.WaitForExitAsync(); return process.ExitCode; }
    internal Task JoinOutputAsync(CancellationToken token) => Task.WhenAll(stdout, stderr).WaitAsync(token);
    private async Task CheckErrorAsync(CancellationToken token)
    {
        var text = await WorkerShutdownProcessTests.ReadBoundedAsync(process.StandardError, 65536, token);
        Assert.Equal("", text);
    }
    private async Task ReadTraceAsync(string resultRoot, CancellationToken token)
    {
        try
        {
            var total = 0; var line = new StringBuilder(); var buffer = new char[1024];
            using var output = new StreamWriter(Path.Combine(resultRoot, "trace.jsonl"), append: false);
            while (await process.StandardOutput.ReadAsync(buffer, token) is var count && count != 0)
            {
                total += count;
                if (total > 131072) throw new InvalidOperationException("Owned trace exceeded its bound.");
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] != '\n')
                    {
                        if (line.Length >= 16384) throw new InvalidOperationException("Owned trace line exceeded its bound.");
                        line.Append(buffer[index]); continue;
                    }
                    var text = line.ToString(); line.Clear();
                    var row = JsonSerializer.Deserialize<ShutdownTrace>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                        throw new InvalidOperationException("Owned trace was empty.");
                    Assert.Equal("weft.worker-shutdown-trace.v1", row.SchemaVersion);
                    lock (gate)
                    {
                        Assert.Equal(rows.Count + 1, row.Sequence);
                        rows.Add(row); var previous = changed; changed = NewSignal(); previous.TrySetResult();
                    }
                    await output.WriteLineAsync(text); await output.FlushAsync(token);
                }
            }
            if (line.Length != 0) throw new InvalidOperationException("Owned trace ended with a partial record.");
        }
        catch (Exception error) { lock (gate) { traceFailure = error; changed.TrySetResult(); } throw; }
        finally { lock (gate) { traceEnded = true; changed.TrySetResult(); } }
    }

    internal Task JoinExactCliProcessesAsync() =>
        WorkerShutdownProbeGuards.JoinAllExactCliProcessesAsync(Rows, JoinExactCliProcessAsync);

    private static async Task JoinExactCliProcessAsync(ShutdownTrace row)
    {
        Process cli;
        try { cli = Process.GetProcessById(row.CliPid!.Value); }
        catch (ArgumentException) { return; }
        using (cli)
        {
            if (cli.HasExited || ReadStartTicks(cli.Id) != row.StartTicks) return;
            await WorkerShutdownProbeGuards.WaitKillAndReapAsync(
                () => cli.WaitForExitAsync().WaitAsync(WorkerShutdownProcessTests.SettlementTimeout),
                () =>
                {
                    cli.Refresh();
                    if (!cli.HasExited && ReadStartTicks(cli.Id) == row.StartTicks) cli.Kill();
                },
                () => cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            Exception? signalFailure = null;
            if (!process.HasExited)
            {
                var ready = Rows.SingleOrDefault(row => row.Kind == "host-ready");
                if (ready is not null)
                {
                    try { Signal(ready, -ready.ProcessGroup!.Value, signal: 9); }
                    catch (Exception error) { signalFailure = error; }
                }
                // An unverified group is never signaled; the exact owned tree still terminates.
                if (ready is null || signalFailure is not null) process.Kill(entireProcessTree: true);
            }
            await Exit.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3));
            if (signalFailure is not null) ExceptionDispatchInfo.Throw(signalFailure);
        }
        finally { process.Dispose(); }
    }

    // Matches the probe's kernel start-time identity; Process.StartTime differs per observer.
    // A reaped process has no identity, never a reusable default.
    private static long? ReadStartTicks(int pid)
    {
        string stat;
        try { stat = File.ReadAllText($"/proc/{pid}/stat"); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        return long.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19], CultureInfo.InvariantCulture);
    }

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)] private static extern int GetProcessGroup(int pid);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int SendSignal(int pid, int signal);
}

internal sealed record ShutdownTrace(string SchemaVersion, int Sequence, string Kind, string? Operation,
    string? ContainerName, int? CliPid, int? ProcessGroup, long? StartTicks, int? HostPid, int? ExitCode,
    bool? Unsettled, string? Failure, long? InputBytes);
