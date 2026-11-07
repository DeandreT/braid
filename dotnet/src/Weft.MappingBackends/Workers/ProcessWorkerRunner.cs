using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Weft.MappingBackends.Workers;

/// <summary>
/// Runs each operation as a fresh Ferrule worker process in its self-hardening mode, for the portable Ferrule
/// service. The worker applies the profile's fixed CPU, address-space and descriptor limits, forbids process
/// creation and file writes, becomes non-dumpable, sets no-new-privileges and filters syscalls (no network,
/// exec, tracing, foreign signals or file opening). This runner verifies every control in procfs before it
/// sends any frame byte, gives the child an empty environment and a fresh private empty working directory,
/// and kills it at the wall deadline. Concurrency is bounded by the service, not by this runner.
/// </summary>
public sealed class ProcessWorkerRunner : IInspectionWorkerRunner, IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ProcessWorkerOptions options;
    private readonly int inheritedFilters;
    private readonly object lifetimeGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? stopOperation;
    private int outstandingOperations;
    private int unavailable;
    private int lifetimeDisposed;
    private bool stopping;

    public ProcessWorkerRunner(ProcessWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Service worker processes require Linux.");
        this.options = options;
        // The identity is the exact worker executable the service runs; a read-only image cannot change it.
        using (var executable = File.OpenRead(options.WorkerExecutable))
            BuildIdentity = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(executable));
        if (!Directory.Exists(options.TemporaryRoot))
            throw new ArgumentException("The temporary root must be an existing directory.", nameof(options));
        // A read-only root without a writable temporary volume fails at startup, not on every operation.
        var probe = Path.Combine(options.TemporaryRoot, "weft-ferrule-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(probe);
        // Each child inherits the service's own filters and must add exactly its three.
        inheritedFilters = ChildSandboxVerification.InheritedFilters();
    }

    public string BuildIdentity { get; }
    public DockerWorkerProfile Profile => options.Profile;

    /// <summary>False once a cleanup could not be confirmed or the runner is stopping.</summary>
    public bool Available
    {
        get
        {
            lock (lifetimeGate) return !stopping && Volatile.Read(ref unavailable) == 0;
        }
    }

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
            if (!OperatingSystem.IsLinux() || Volatile.Read(ref unavailable) != 0) return Failed(WorkerProcessFailure.SandboxUnavailable);
            if (cancellationToken.IsCancellationRequested) return Failed(WorkerProcessFailure.Canceled);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            return await RunOwnedAsync(requestFrame, lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            // An exceptional operation cannot report confirmed cleanup.
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

    [SupportedOSPlatform("linux")]
    private async Task<WorkerProcessResult> RunOwnedAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        using var wallDeadline = new CancellationTokenSource(options.WallTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wallDeadline.Token);
        var directory = Path.Combine(options.TemporaryRoot, "weft-ferrule-" + Guid.NewGuid().ToString("N"));
        var directoryCreated = false;
        Process? process = null;
        var result = Failed(WorkerProcessFailure.SandboxUnavailable);
        try
        {
            if (Directory.Exists(directory)) return result;
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            directoryCreated = true;
            try
            {
                process = Process.Start(StartInfo(directory)) ?? throw new InvalidOperationException("The worker did not start.");
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
            {
                return result = Failed(WorkerProcessFailure.StartFailed);
            }

            result = await ExchangeAsync(process, frame, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = Failed(WorkerProcessFailure.Canceled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result = Failed(WorkerProcessFailure.SandboxUnavailable);
        }
        finally
        {
            if (process is not null)
            {
                if (!await SettleAsync(process).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref unavailable, 1);
                    result = Failed(WorkerProcessFailure.CleanupFailed);
                }
                else if (result.AdmissionFailure == ChildAdmissionFailure.MarkerMissing)
                {
                    // Inspect only after the existing reap, never wait longer or trust an unverified child.
                    result = ChildAdmissionDiagnostics.Refused(ChildAdmissionDiagnostics.StartupExit(process.ExitCode));
                }

                process.Dispose();
            }

            if (directoryCreated)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Exchange(ref unavailable, 1);
                    result = Failed(WorkerProcessFailure.CleanupFailed);
                }
            }
        }

        if (Volatile.Read(ref unavailable) != 0) return Failed(WorkerProcessFailure.CleanupFailed);
        return result.Failure == WorkerProcessFailure.Canceled && !cancellationToken.IsCancellationRequested
            ? Failed(WorkerProcessFailure.TimedOut) : result;
    }

    private ProcessStartInfo StartInfo(string directory)
    {
        var startInfo = new ProcessStartInfo(options.WorkerExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
        // No service setting, credential or path reaches the worker.
        startInfo.Environment.Clear();
        foreach (var argument in options.Arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private async Task<WorkerProcessResult> ExchangeAsync(Process process, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        using var ioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var standardOutput = process.StandardOutput.BaseStream;
        var error = DrainAsync(process.StandardError.BaseStream, ProcessWorkerOptions.StandardErrorLimit, ioCancellation.Token);
        _ = ReportFailureAsync(error, new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            // Nothing of the request is sent before the worker proves its sandbox and procfs confirms it.
            var marker = new byte[ProcessWorkerOptions.ReadyMarker.Length];
            try
            {
                await standardOutput.ReadExactlyAsync(marker, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return ChildAdmissionDiagnostics.Refused(ChildAdmissionFailure.MarkerMissing);
            }

            if (!marker.AsSpan().SequenceEqual(ProcessWorkerOptions.ReadyMarker))
                return ChildAdmissionDiagnostics.Refused(ChildAdmissionFailure.MarkerMismatch);
            var admission = Verify(process.Id);
            if (admission != ChildAdmissionFailure.None) return ChildAdmissionDiagnostics.Refused(admission);

            var output = DrainAsync(standardOutput, options.Profile.MaximumOutputBytes, ioCancellation.Token);
            Task[] operations = [output, error, WriteInputAsync(process, frame, ioCancellation.Token), process.WaitForExitAsync(ioCancellation.Token)];
            var all = Task.WhenAll(operations);
            var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var operation in operations) _ = ReportFailureAsync(operation, fault);
            var completed = await Task.WhenAny(all, fault.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (completed == fault.Task) throw await fault.Task.ConfigureAwait(false);
            await all.ConfigureAwait(false);
            _ = StrictUtf8.GetString(await error.ConfigureAwait(false));
            var bytes = await output.ConfigureAwait(false);
            return CompletedExit(bytes, process.ExitCode);
        }
        catch (OutputLimitException)
        {
            return Failed(WorkerProcessFailure.OutputLimitExceeded);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or DecoderFallbackException or Win32Exception)
        {
            return Failed(WorkerProcessFailure.WorkerFailed);
        }
        finally
        {
            await ioCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    internal static WorkerProcessResult CompletedExit(byte[] output, int exitCode) => exitCode == 0
        ? new WorkerProcessResult(output, WorkerProcessFailure.None)
        : Failed(WorkerProcessFailure.WorkerFailed);

    /// <summary>Confirms in procfs that the running child applied every control before it reads input.</summary>
    private ChildAdmissionFailure Verify(int processId) => ChildSandboxVerification.Verify(processId, inheritedFilters + 3,
    [
        ("Max cpu time", options.Profile.CpuSeconds),
        ("Max file size", 0),
        ("Max core file size", 0),
        ("Max processes", 1),
        ("Max open files", options.Profile.FileLimit),
        ("Max address space", options.Profile.MemoryBytes),
        ("Max locked memory", 0),
        ("Max file locks", 0),
        ("Max msgqueue size", 0),
        ("Max realtime priority", 0)
    ]);

    /// <summary>Kills a child that is still running and waits, within a bound, until it is reaped.</summary>
    private static async Task<bool> SettleAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(ProcessWorkerOptions.KillTimeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or TimeoutException or IOException)
        {
            return false;
        }
    }

    private async Task WriteInputAsync(Process process, ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        var writer = process.StandardInput;
        try
        {
            await writer.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            await writer.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!options.WithholdEndOfInput) writer.Dispose();
        }
    }

    private static async Task<byte[]> DrainAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) return buffer.ToArray();
            if (count > limit - buffer.Length) throw new OutputLimitException();
            buffer.Write(chunk, 0, count);
        }
    }

    private static async Task ReportFailureAsync(Task operation, TaskCompletionSource<Exception> fault)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            fault.TrySetResult(exception);
        }
    }

    /// <summary>
    /// Closes admission, cancels running children through the normal path and joins them. Unconfirmed cleanup
    /// faults this task instead of reporting a clean stop.
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
        try { await shutdown.CancelAsync().ConfigureAwait(false); }
        finally { await drained.Task.ConfigureAwait(false); }
        if (Volatile.Read(ref unavailable) != 0)
            throw new InvalidOperationException("Service worker shutdown could not confirm cleanup.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            if (Interlocked.Exchange(ref lifetimeDisposed, 1) == 0) shutdown.Dispose();
        }
    }

    private static WorkerProcessResult Failed(WorkerProcessFailure failure) => new([], failure);

    private sealed class OutputLimitException : Exception;
}
