using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Weft.Deployment;

namespace Weft.MappingBackends.Workers;

/// <summary>Executes only the fixed installed runtime in a fresh verified launcher; mapping assemblies never load in this process.</summary>
public sealed class ProcessCompiledRuntimeRunner : IInspectionWorkerRunner, IAsyncDisposable
{
    private readonly ProcessCompiledRuntimeOptions options;
    private readonly ProcessCompileOptions.ToolchainGrants grants;
    private readonly int inheritedFilters;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? stop;
    private int active, unavailable, disposed;
    private bool stopping;

    public ProcessCompiledRuntimeRunner(ProcessCompiledRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Compiled service execution requires Linux.");
        if (!File.Exists(options.RuntimeAssembly) || !File.Exists(options.LauncherExecutable) ||
            !File.Exists(BuildCompilation.Host(options.DotnetRoot)) || !Directory.Exists(options.TemporaryRoot))
            throw new ArgumentException("The fixed runtime, launcher and temporary root must be installed.");
        this.options = options;
        grants = ProcessCompileOptions.ToolchainGrants.ResolveRoot(options.DotnetRoot);
        inheritedFilters = ChildSandboxVerification.InheritedFilters();
        // Every granted directory beside the .NET root holds system libraries; /dev/urandom is a file.
        RuntimeIdentity = ComputeIdentity(options, grants.Execute,
            [.. grants.Read.Where(path => path != options.DotnetRoot && Directory.Exists(path))]);
    }

    /// <summary>The runtime host's fixed line for a frame it cannot decode.</summary>
    private static ReadOnlySpan<byte> ProbeRefusal => "WeftCompiledRuntimeFailed:decode\n"u8;

    /// <summary>The native identity shared by the service's transport profiles.</summary>
    public string BuildIdentity => options.NativeIdentity;
    public string RuntimeIdentity { get; }
    public bool Available { get { lock (gate) return !stopping && Volatile.Read(ref unavailable) == 0; } }

    /// <summary>Proves the launcher's controls and that the installed runtime host starts and runs under them.</summary>
    public async Task<bool> ProbeAsync(CancellationToken token) =>
        (await ExecuteAsync(ReadOnlyMemory<byte>.Empty, probe: true, token).ConfigureAwait(false)).Failure == WorkerProcessFailure.None;

    public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken token)
    {
        if (requestFrame.Length is < 1 || requestFrame.Length > ProcessCompiledRuntimeOptions.Profile.MaximumRequestBytes)
            throw new ArgumentException("The compiled request exceeds its fixed transport bound.", nameof(requestFrame));
        return ExecuteAsync(requestFrame, probe: false, token);
    }

    private async Task<WorkerProcessResult> ExecuteAsync(ReadOnlyMemory<byte> frame, bool probe, CancellationToken token)
    {
        lock (gate)
        {
            if (stopping || Volatile.Read(ref unavailable) != 0) return Failed(WorkerProcessFailure.SandboxUnavailable);
            active++;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(options.WallTimeout);
        try
        {
            if (!OperatingSystem.IsLinux()) return Failed(WorkerProcessFailure.SandboxUnavailable);
            var result = await RunOwnedAsync(frame, probe, deadline.Token).ConfigureAwait(false);
            if (Volatile.Read(ref unavailable) != 0) return Failed(WorkerProcessFailure.CleanupFailed);
            return result.Failure == WorkerProcessFailure.Canceled && !lifetime.IsCancellationRequested
                ? Failed(WorkerProcessFailure.TimedOut) : result;
        }
        catch
        {
            Interlocked.Exchange(ref unavailable, 1);
            throw;
        }
        finally
        {
            lock (gate) { active--; if (stopping && active == 0) drained.TrySetResult(); }
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task<WorkerProcessResult> RunOwnedAsync(ReadOnlyMemory<byte> frame, bool probe, CancellationToken token)
    {
        var directory = Path.Combine(options.TemporaryRoot, "weft-execute-" + Guid.NewGuid().ToString("N"));
        Process? child = null;
        var created = false;
        using var io = CancellationTokenSource.CreateLinkedTokenSource(token);
        var operations = new List<Task>();
        try
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(directory)) return Failed(WorkerProcessFailure.SandboxUnavailable);
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            created = true;
            var start = new ProcessStartInfo(options.LauncherExecutable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory,
                CreateNoWindow = true
            };
            start.Environment.Clear();
            foreach (var (key, value) in options.Environment(directory)) start.Environment[key] = value;
            foreach (var argument in options.Arguments(grants)) start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new InvalidOperationException("The runtime launcher did not start.");
            var error = DrainAsync(child.StandardError.BaseStream, ProcessWorkerOptions.StandardErrorLimit, io.Token);
            operations.Add(error);
            var marker = new byte[ProcessCompileOptions.ReadyMarker.Length];
            await child.StandardOutput.BaseStream.ReadExactlyAsync(marker, token).ConfigureAwait(false);
            if (!marker.AsSpan().SequenceEqual(ProcessCompileOptions.ReadyMarker) || !Verified(child.Id))
                return Failed(WorkerProcessFailure.SandboxUnavailable);
            var output = DrainAsync(child.StandardOutput.BaseStream, ProcessCompiledRuntimeOptions.Profile.MaximumOutputBytes, io.Token);
            operations.Add(output);
            operations.Add(WriteAsync(child, frame, io.Token));
            operations.Add(child.WaitForExitAsync(io.Token));
            var all = Task.WhenAll(operations);
            var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var operation in operations) _ = ObserveFailureAsync(operation, fault);
            if (await Task.WhenAny(all, fault.Task).WaitAsync(token).ConfigureAwait(false) == fault.Task)
                throw await fault.Task.ConfigureAwait(false);
            await all.ConfigureAwait(false);
            // The probe's empty frame proves the runtime itself starts under these controls: it fails only at decoding.
            if (probe)
                return child.ExitCode == 1 && (await output.ConfigureAwait(false)).Length == 0 &&
                    (await error.ConfigureAwait(false)).AsSpan().SequenceEqual(ProbeRefusal)
                    ? new([], WorkerProcessFailure.None) : Failed(WorkerProcessFailure.SandboxUnavailable);
            return child.ExitCode == 0 ? new(await output.ConfigureAwait(false), WorkerProcessFailure.None) : Failed(WorkerProcessFailure.WorkerFailed);
        }
        catch (OperationCanceledException) { return Failed(WorkerProcessFailure.Canceled); }
        catch (OutputLimitException) { return Failed(WorkerProcessFailure.OutputLimitExceeded); }
        catch (EndOfStreamException) { return Failed(WorkerProcessFailure.SandboxUnavailable); }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { return Failed(WorkerProcessFailure.WorkerFailed); }
        finally
        {
            await io.CancelAsync().ConfigureAwait(false);
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited) child.Kill();
                    await child.WaitForExitAsync().WaitAsync(ProcessWorkerOptions.KillTimeout).ConfigureAwait(false);
                    var all = Task.WhenAll(operations);
                    try { await all.WaitAsync(ProcessWorkerOptions.KillTimeout).ConfigureAwait(false); }
                    catch (Exception error) when (all.IsCompleted && error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                    { /* Every pipe settled; its failure was observed. */ }
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or TimeoutException)
                { Interlocked.Exchange(ref unavailable, 1); }
                finally { child.Dispose(); }
            }
            if (created)
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Interlocked.Exchange(ref unavailable, 1); }
        }
    }

    private bool Verified(int pid) => ChildSandboxVerification.Verified(pid, inheritedFilters + ProcessCompileOptions.LauncherFilters,
    [
        ("Max cpu time", ProcessCompiledRuntimeOptions.Profile.CpuSeconds),
        ("Max file size", ProcessCompiledRuntimeOptions.FileBytes), ("Max core file size", 0),
        ("Max open files", ProcessCompiledRuntimeOptions.Profile.FileLimit),
        ("Max address space", ProcessCompiledRuntimeOptions.AddressSpaceBytes),
        ("Max locked memory", 0), ("Max file locks", 0), ("Max msgqueue size", 0), ("Max realtime priority", 0)
    ]);

    private async Task WriteAsync(Process child, ReadOnlyMemory<byte> frame, CancellationToken token)
    {
        try
        {
            await child.StandardInput.BaseStream.WriteAsync(new[] { ProcessCompileOptions.Go }, token).ConfigureAwait(false);
            await child.StandardInput.BaseStream.WriteAsync(frame, token).ConfigureAwait(false);
            await child.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
        }
        finally { if (!options.WithholdEndOfInput) child.StandardInput.Close(); }
    }

    private static async Task<byte[]> DrainAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (count > maximum - result.Length) throw new OutputLimitException();
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static async Task ObserveFailureAsync(Task task, TaskCompletionSource<Exception> failed)
    { try { await task.ConfigureAwait(false); } catch (Exception error) { failed.TrySetResult(error); } }

    /// <summary>
    /// <c>sha256:</c> over the entry filename, runtime host, .NET host and pinned runtime, launcher and runner, the ELF
    /// interpreter and every system library the host, runtime and launcher can load (each file's label, length and
    /// SHA-256 in a fixed order).
    /// </summary>
    /// <param name="interpreters">The granted ELF interpreters, which <c>execve</c> maps for the host.</param>
    /// <param name="libraryDirectories">The granted system library directories, which the image fills from unpinned packages.</param>
    internal static string ComputeIdentity(ProcessCompiledRuntimeOptions options, IReadOnlyList<string> interpreters,
        IReadOnlyList<string> libraryDirectories)
    {
        var manifest = new StringBuilder("weft.compiled-runtime-identity.v1\nentry=")
            .Append(Path.GetFileName(options.RuntimeAssembly)).Append('\n');
        void Add(string name, string path)
        {
            using var stream = File.OpenRead(path);
            manifest.Append(name).Append('\t').Append(stream.Length).Append('\t')
                .Append(Convert.ToHexStringLower(SHA256.HashData(stream))).Append('\n');
        }
        var roots = new[]
        {
            ("runtime-host", Path.GetDirectoryName(options.RuntimeAssembly)!),
            ("dotnet-host", Path.Combine(options.DotnetRoot, "host")),
            ("dotnet-runtime", Path.Combine(options.DotnetRoot, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion))
        };
        foreach (var (label, root) in roots)
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                Add(label + "/" + Path.GetRelativePath(root, path), path);
        Add("dotnet", BuildCompilation.Host(options.DotnetRoot));
        Add("launcher", options.LauncherExecutable);
        Add("runner", typeof(ProcessCompiledRuntimeRunner).Assembly.Location);
        foreach (var interpreter in interpreters.Order(StringComparer.Ordinal)) Add("interpreter" + interpreter, interpreter);
        // The system libraries the runtime child can load; the runtime host, .NET host and runtime are measured above.
        var objects = roots.SelectMany(root => Directory.EnumerateFiles(root.Item2, "*", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal).Prepend(BuildCompilation.Host(options.DotnetRoot)).Prepend(options.LauncherExecutable);
        foreach (var library in ElfLibraryClosure.Resolve(objects, ElfLibraryClosure.RuntimeOpenedLibraries, libraryDirectories)
            .Where(library => !roots.Any(root => library.StartsWith(root.Item2 + "/", StringComparison.Ordinal))))
            Add("library" + library, library);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())));
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            if (stop is not null) return stop;
            stopping = true;
            if (active == 0) drained.TrySetResult();
            return stop = StopCoreAsync();
        }
    }
    private async Task StopCoreAsync()
    {
        try { await shutdown.CancelAsync().ConfigureAwait(false); }
        finally { await drained.Task.ConfigureAwait(false); }
        if (Volatile.Read(ref unavailable) != 0) throw new InvalidOperationException("Compiled runtime cleanup was not confirmed.");
    }
    public async ValueTask DisposeAsync()
    { try { await StopAsync().ConfigureAwait(false); } finally { if (Interlocked.Exchange(ref disposed, 1) == 0) shutdown.Dispose(); } }
    private static WorkerProcessResult Failed(WorkerProcessFailure failure) => new([], failure);
    private sealed class OutputLimitException : Exception;
}
