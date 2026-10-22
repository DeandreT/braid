using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Weft.Deployment;

namespace Weft.MappingBackends.Workers;

/// <summary>
/// Runs each <c>weft.ferrule-build.v1</c> operation of the portable Ferrule service the way the Docker compiler
/// worker does, in fresh, independently limited children. The worker generates C# sources in its own service
/// sandbox with the build profile's limits. The known generated files are written into a fresh private directory,
/// and the pinned compiler runs there inside <c>weft-compile-sandbox</c>: fixed CPU, address-space, descriptor and
/// file-size limits, a bounded heap, no network, no new processes, and filesystem access only to the read-only
/// toolchain, the private directory and its one output file. This runner verifies the launcher's controls in procfs
/// before the compiler starts. One wall deadline covers both stages; at the deadline, on cancellation or on an
/// output limit the running child is killed and reaped. The reply is the same framed answer, with the same
/// artifact bytes, as the Docker compiler's.
/// </summary>
public sealed class ProcessCompileRunner : IInspectionWorkerRunner, ICompilationIdentitySource, IAsyncDisposable
{
    private readonly ProcessCompileOptions options;
    private readonly ProcessWorkerRunner generation;
    private readonly ProcessCompileOptions.ToolchainGrants grants;
    private readonly int inheritedFilters;
    private readonly object lifetimeGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? stopOperation;
    private int outstandingOperations;
    private int unavailable;
    private int lifetimeDisposed;
    private bool stopping;

    public ProcessCompileRunner(ProcessCompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Service compile processes require Linux.");
        this.options = options;
        var compiler = BuildCompilation.Compiler(options.ToolchainRoot);
        var references = BuildCompilation.References(options.ToolchainRoot);
        if (!File.Exists(options.LauncherExecutable) || !File.Exists(BuildCompilation.Host(options.ToolchainRoot)) ||
            !File.Exists(compiler) || !Directory.Exists(references))
            throw new ArgumentException("The compile launcher and the pinned toolchain must be installed.", nameof(options));
        grants = ProcessCompileOptions.ToolchainGrants.Resolve(options);
        ToolchainIdentity = ComputeIdentity(options.ToolchainRoot, Path.GetDirectoryName(compiler)!, references, options.LauncherExecutable);
        inheritedFilters = ChildSandboxVerification.InheritedFilters();
        // Verifies the worker and the writable temporary root; the identity is the worker's, like every profile's.
        generation = new ProcessWorkerRunner(options.Generation);
        BuildIdentity = generation.BuildIdentity;
        CompilationBuildIdentity = CompileWorkerIdentity.Combine(BuildIdentity, ToolchainIdentity);
    }

    /// <summary>The service's build identity: the exact worker executable, shared with inspection and preview.</summary>
    public string BuildIdentity { get; }
    public string CompilationBuildIdentity { get; }

    /// <summary>
    /// <c>sha256:</c> over the compiler, references, host/runtime, launcher and compilation implementation
    /// (each file's label, length and SHA-256 in ordinal order), reported and admitted by the compile protocol.
    /// </summary>
    public string ToolchainIdentity { get; }

    public static DockerWorkerProfile Profile => DockerWorkerProfile.Build;

    /// <summary>False once a cleanup could not be confirmed or the runner is stopping.</summary>
    public bool Available
    {
        get
        {
            lock (lifetimeGate) return !stopping && Volatile.Read(ref unavailable) == 0 && generation.Available;
        }
    }

    /// <summary>
    /// Starts one launcher with the compile grants and verifies every control in procfs without starting the
    /// compiler, proving that this kernel and platform enforce the sandbox (for example Landlock ABI 3).
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var directory = Path.Combine(options.TemporaryRoot, "weft-compile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process? process = null;
        try
        {
            process = Process.Start(StartInfo(directory, output: false)) ?? throw new InvalidOperationException("The launcher did not start.");
            var marker = new byte[ProcessCompileOptions.ReadyMarker.Length];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(marker, cancellationToken).ConfigureAwait(false);
            return marker.AsSpan().SequenceEqual(ProcessCompileOptions.ReadyMarker) && Verified(process.Id);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            // Without the go byte the launcher exits and the compiler never starts.
            if (process is not null)
            {
                process.StandardInput.Close();
                if (!await SettleAsync(process).ConfigureAwait(false)) Interlocked.Exchange(ref unavailable, 1);
                process.Dispose();
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
    {
        if (requestFrame.Length > Profile.MaximumRequestBytes)
            throw new ArgumentException("The request frame exceeds the fixed transport limit.", nameof(requestFrame));

        lock (lifetimeGate)
        {
            if (stopping) return Failed(WorkerProcessFailure.SandboxUnavailable);
            outstandingOperations++;
        }
        try
        {
            if (!OperatingSystem.IsLinux() || !Available) return Failed(WorkerProcessFailure.SandboxUnavailable);
            if (cancellationToken.IsCancellationRequested) return Failed(WorkerProcessFailure.Canceled);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            using var wallDeadline = new CancellationTokenSource(options.WallTimeout);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, wallDeadline.Token);
            var result = await StagesAsync(requestFrame, deadline.Token).ConfigureAwait(false);
            if (result.Failure == WorkerProcessFailure.CleanupFailed || !generation.Available) Interlocked.Exchange(ref unavailable, 1);
            if (Volatile.Read(ref unavailable) != 0) return Failed(WorkerProcessFailure.CleanupFailed);
            return result.Failure == WorkerProcessFailure.Canceled && !lifetime.IsCancellationRequested
                ? Failed(WorkerProcessFailure.TimedOut) : result;
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

    private async Task<WorkerProcessResult> StagesAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        // The Docker compiler refuses an inadmissible request as a failed worker, before generation.
        BuildWorkerProtocol.Request request;
        try { request = BuildWorkerProtocol.DecodeRequest(frame.ToArray()).Header; }
        catch (Exception exception) when (Ordinary(exception)) { return Failed(WorkerProcessFailure.WorkerFailed); }

        var generated = await generation.RunAsync(frame, cancellationToken).ConfigureAwait(false);
        switch (generated.Failure)
        {
            case WorkerProcessFailure.None:
                break;
            case WorkerProcessFailure.TimedOut:
            case WorkerProcessFailure.Canceled:
                return Failed(WorkerProcessFailure.Canceled);
            case WorkerProcessFailure.CleanupFailed or WorkerProcessFailure.SandboxUnavailable or WorkerProcessFailure.StartFailed:
                return generated;
            default:
                // Like a generator failure inside the Docker compiler: the compile operation failed.
                return Failed(WorkerProcessFailure.WorkerFailed);
        }

        BuildWorkerProtocol.Reply reply;
        try { reply = BuildWorkerProtocol.DecodeReply(generated.Output, request); }
        catch (Exception exception) when (Ordinary(exception)) { return Failed(WorkerProcessFailure.WorkerFailed); }
        if (reply.Header.Status != "supported" || request.Operation == "inspect")
            return new(BuildCompilation.Refusal(reply), WorkerProcessFailure.None);
        return OperatingSystem.IsLinux() ? await CompileAsync(reply, cancellationToken).ConfigureAwait(false) : Failed(WorkerProcessFailure.SandboxUnavailable);
    }

    [SupportedOSPlatform("linux")]
    private async Task<WorkerProcessResult> CompileAsync(BuildWorkerProtocol.Reply reply, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(options.TemporaryRoot, "weft-compile-" + Guid.NewGuid().ToString("N"));
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
                await BuildCompilation.PrepareAsync(directory, options.ToolchainRoot, reply, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The generator returned a file outside the admitted source set.
                return result = Failed(WorkerProcessFailure.WorkerFailed);
            }

            try
            {
                process = Process.Start(StartInfo(directory, output: true)) ?? throw new InvalidOperationException("The launcher did not start.");
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
            {
                return result = Failed(WorkerProcessFailure.StartFailed);
            }

            result = await ExchangeAsync(process, cancellationToken).ConfigureAwait(false);
            if (result.Failure != WorkerProcessFailure.None) return result;
            var output = new FileInfo(Path.Combine(directory, BuildCompilation.Output));
            if (output.Length is < 1 or > BuildWorkerProtocol.MaximumSourceBytes) return result = Failed(WorkerProcessFailure.WorkerFailed);
            var assembly = await File.ReadAllBytesAsync(output.FullName, cancellationToken).ConfigureAwait(false);
            return result = new(BuildCompilation.Artifact(reply, assembly), WorkerProcessFailure.None);
        }
        catch (OperationCanceledException)
        {
            return result = Failed(WorkerProcessFailure.Canceled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return result = Failed(WorkerProcessFailure.SandboxUnavailable);
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
                }
            }
        }
    }

    private ProcessStartInfo StartInfo(string directory, bool output)
    {
        var startInfo = new ProcessStartInfo(options.LauncherExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
        // No service setting, credential or path reaches the compiler beyond its own fixed environment.
        startInfo.Environment.Clear();
        foreach (var (name, value) in options.Environment(directory)) startInfo.Environment[name] = value;
        foreach (var argument in options.LauncherArguments(directory, grants, output)) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    /// <summary>Nothing starts the compiler before the launcher proves its sandbox and procfs confirms it.</summary>
    private async Task<WorkerProcessResult> ExchangeAsync(Process process, CancellationToken cancellationToken)
    {
        using var ioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var error = DrainAsync(process.StandardError.BaseStream, ProcessWorkerOptions.StandardErrorLimit, ioCancellation.Token);
        _ = ReportFailureAsync(error, new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            var marker = new byte[ProcessCompileOptions.ReadyMarker.Length];
            try
            {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(marker, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return Failed(WorkerProcessFailure.SandboxUnavailable);
            }

            if (!marker.AsSpan().SequenceEqual(ProcessCompileOptions.ReadyMarker) || !Verified(process.Id))
                return Failed(WorkerProcessFailure.SandboxUnavailable);
            await process.StandardInput.BaseStream.WriteAsync(new[] { ProcessCompileOptions.Go }, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            var diagnostics = DrainAsync(process.StandardOutput.BaseStream, ProcessCompileOptions.DiagnosticsLimit, ioCancellation.Token);
            Task[] operations = [diagnostics, error, process.WaitForExitAsync(ioCancellation.Token)];
            var all = Task.WhenAll(operations);
            var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var operation in operations) _ = ReportFailureAsync(operation, fault);
            var completed = await Task.WhenAny(all, fault.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (completed == fault.Task) throw await fault.Task.ConfigureAwait(false);
            await all.ConfigureAwait(false);
            return process.ExitCode == 0 ? new([], WorkerProcessFailure.None) : Failed(WorkerProcessFailure.WorkerFailed);
        }
        catch (OutputLimitException)
        {
            // The Docker compiler fails the operation when the compiler's diagnostics exceed its bound.
            return Failed(WorkerProcessFailure.WorkerFailed);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
        {
            return Failed(WorkerProcessFailure.WorkerFailed);
        }
        finally
        {
            await ioCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private bool Verified(int processId) => ChildSandboxVerification.Verified(processId, inheritedFilters + ProcessCompileOptions.LauncherFilters,
    [
        ("Max cpu time", Profile.CpuSeconds),
        ("Max file size", ProcessCompileOptions.FileBytes),
        ("Max core file size", 0),
        ("Max open files", Profile.FileLimit),
        ("Max address space", ProcessCompileOptions.AddressSpaceBytes),
        ("Max locked memory", 0),
        ("Max file locks", 0),
        ("Max msgqueue size", 0),
        ("Max realtime priority", 0)
    ]);

    /// <summary>Kills a launcher or compiler that is still running and waits, within a bound, until it is reaped.</summary>
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

    internal static string ComputeIdentity(string root, string compiler, string references, string launcher)
    {
        var manifest = new StringBuilder();
        void Add(string label, string path)
        {
            using var file = File.OpenRead(path);
            var digest = Convert.ToHexStringLower(SHA256.HashData(file));
            manifest.Append(label).Append('\t').Append(file.Length).Append('\t').Append(digest).Append('\n');
        }

        foreach (var (label, directory) in new[]
        {
            ("compiler", compiler), ("references", references),
            ("host", Path.Combine(root, "host")),
            ("runtime", Path.Combine(root, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion))
        })
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(directory, path)).Order(StringComparer.Ordinal))
                Add(label + "/" + path, Path.Combine(directory, path));
        Add("dotnet", BuildCompilation.Host(root));
        Add("launcher", launcher);
        Add("compilation", typeof(BuildCompilation).Assembly.Location);
        Add("runner", typeof(ProcessCompileRunner).Assembly.Location);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())));
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
        await generation.StopAsync().ConfigureAwait(false);
        if (Volatile.Read(ref unavailable) != 0)
            throw new InvalidOperationException("Service compile shutdown could not confirm cleanup.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            if (Interlocked.Exchange(ref lifetimeDisposed, 1) == 0) shutdown.Dispose();
            await generation.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool Ordinary(Exception exception) => exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static WorkerProcessResult Failed(WorkerProcessFailure failure) => new([], failure);

    private sealed class OutputLimitException : Exception;
}
