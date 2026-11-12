using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Weft.Deployment;
using Weft.Application;

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
    private readonly List<OwnedOperation> owned = [];
    private Task? stop;
    private Task<bool>? stopWork;
    private long stopDeadline, stopCompletedAt, stopDecisionAt;
    private int active, unavailable, disposed;
    private bool stopping;

    public ProcessCompiledRuntimeRunner(ProcessCompiledRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Operation)) throw new ArgumentOutOfRangeException(nameof(options));
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Compiled service execution requires Linux.");
        if (!File.Exists(options.RuntimeAssembly) || !File.Exists(options.LauncherExecutable) ||
            !File.Exists(BuildCompilation.Host(options.DotnetRoot)) || !File.Exists(BuildCompilation.HostFxr(options.DotnetRoot)) || !Directory.Exists(options.TemporaryRoot))
            throw new ArgumentException("The fixed runtime, launcher and temporary root must be installed.");
        this.options = options;
        grants = ProcessCompileOptions.ToolchainGrants.ResolveRoot(options.DotnetRoot, options.LauncherExecutable);
        inheritedFilters = ChildSandboxVerification.InheritedFilters();
        // Every granted directory beside the .NET root holds system libraries; /dev/urandom is a file.
        RuntimeIdentity = ComputeIdentity(options, grants.Interpreters,
            [.. grants.Read.Where(path => path != options.DotnetRoot && Directory.Exists(path))]);
        options.ObserveShutdown?.Invoke(shutdown.Token);
    }

    /// <summary>The runtime host's fixed line for a frame it cannot decode.</summary>
    private ReadOnlySpan<byte> ProbeRefusal => options.Operation switch
    {
        CompiledRuntimeOperation.XmlCanonicalComparison => "WeftXmlComparisonFailed:decode\n"u8,
        CompiledRuntimeOperation.OfficeExtraction => "WeftDocumentWorkerFailed:decode\n"u8,
        _ => "WeftCompiledRuntimeFailed:decode\n"u8
    };

    /// <summary>The native identity shared by the service's transport profiles.</summary>
    public string BuildIdentity => options.NativeIdentity;
    public string RuntimeIdentity { get; }
    public bool Available { get { lock (gate) return !stopping && Volatile.Read(ref unavailable) == 0; } }

    /// <summary>Proves the launcher's controls and that the installed runtime host starts and runs under them.</summary>
    public async Task<bool> ProbeAsync(CancellationToken token) =>
        (await ProbeResultAsync(token).ConfigureAwait(false)).Failure == WorkerProcessFailure.None;

    internal Task<WorkerProcessResult> ProbeResultAsync(CancellationToken token) =>
        ExecuteAsync(ReadOnlyMemory<byte>.Empty, probe: true, token);

    public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken token)
    {
        var maximum = options.Operation switch
        {
            CompiledRuntimeOperation.XmlCanonicalComparison => XmlComparisonProtocol.MaximumRequestBytes,
            CompiledRuntimeOperation.OfficeExtraction => SpecOfficeProcessProtocol.MaximumRequestBytes,
            _ => ProcessCompiledRuntimeOptions.Profile.MaximumRequestBytes
        };
        if (requestFrame.Length is < 1 || requestFrame.Length > maximum)
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
        try
        {
            if (!OperatingSystem.IsLinux()) return Failed(WorkerProcessFailure.SandboxUnavailable);
            var operation = new OwnedOperation(Path.Combine(options.TemporaryRoot, "weft-execute-" + Guid.NewGuid().ToString("N")),
                options, token, shutdown.Token);
            lock (gate) owned.Add(operation);
            var result = await RunOwnedAsync(frame, probe, operation, operation.WallDeadline.Token).ConfigureAwait(false);
            if (Volatile.Read(ref unavailable) != 0) return Failed(WorkerProcessFailure.CleanupFailed);
            return result.Failure == WorkerProcessFailure.Canceled && !operation.Lifetime.IsCancellationRequested
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
    private async Task<WorkerProcessResult> RunOwnedAsync(ReadOnlyMemory<byte> frame, bool probe, OwnedOperation operation, CancellationToken token)
    {
        var directory = operation.DirectoryPath;
        var markerMissing = false;
        var startupFailure = ChildAdmissionFailure.MarkerMissing;
        try
        {
            options.ObserveOperation?.Invoke(operation);
            if (options.BeforeLaunch is { } beforeLaunch) await beforeLaunch(operation).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(directory)) return Failed(WorkerProcessFailure.SandboxUnavailable);
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            operation.Created = true;
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
            var child = operation.Child = Process.Start(start) ?? throw new InvalidOperationException("The runtime launcher did not start.");
            operation.ChildPid = child.Id;
            operation.StartReapObservation(child);
            operation.ChildStarted.TrySetResult(child.Id);
            var io = operation.Io;
            var operations = operation.IoTasks;
            var error = operation.CreateOriginal<byte[]>(CompiledRuntimeIoKind.StandardError,
                () => DrainAsync(child.StandardError.BaseStream, ProcessWorkerOptions.StandardErrorLimit, io.Token));
            _ = operation.Track(CompiledRuntimeIoKind.StandardError, error); // retained in the operation, not detached
            var marker = new byte[ProcessCompileOptions.ReadyMarker.Length];
            var handshake = operation.CreateOriginal(CompiledRuntimeIoKind.Handshake,
                () => child.StandardOutput.BaseStream.ReadExactlyAsync(marker, io.Token).AsTask());
            // Qualification may hold ownership of the actual task, but cannot replace or omit that original task.
            await operation.Track(CompiledRuntimeIoKind.Handshake, handshake).WaitAsync(token).ConfigureAwait(false);
            if (!marker.AsSpan().SequenceEqual(ProcessCompileOptions.ReadyMarker))
                return ChildAdmissionDiagnostics.Refused(ChildAdmissionFailure.MarkerMismatch);
            var admission = Verify(child.Id);
            if (admission != ChildAdmissionFailure.None) return ChildAdmissionDiagnostics.Refused(admission);
            token.ThrowIfCancellationRequested();
            var maximumOutput = options.Operation switch
            {
                CompiledRuntimeOperation.XmlCanonicalComparison => XmlComparisonProtocol.MaximumResponseBytes,
                CompiledRuntimeOperation.OfficeExtraction => SpecOfficeProcessProtocol.MaximumResponseBytes,
                _ => ProcessCompiledRuntimeOptions.Profile.MaximumOutputBytes
            };
            var output = operation.CreateOriginal<byte[]>(CompiledRuntimeIoKind.StandardOutput,
                () => DrainAsync(child.StandardOutput.BaseStream, maximumOutput, io.Token));
            _ = operation.Track(CompiledRuntimeIoKind.StandardOutput, output);
            // The writer's synchronous close is owned too; it cannot hold this method before its task is tracked.
            var write = operation.CreateOriginal(CompiledRuntimeIoKind.Writer, async () =>
            {
                await operation.InitialIoRegistered.Task.ConfigureAwait(false);
                await WriteAsync(operation, frame, io.Token).ConfigureAwait(false);
            });
            _ = operation.Track(CompiledRuntimeIoKind.Writer, write);
            _ = operation.Track(CompiledRuntimeIoKind.ExitObserver,
                operation.CreateOriginal(CompiledRuntimeIoKind.ExitObserver, () => child.WaitForExitAsync(io.Token)));
            operation.InitialIoRegistered.TrySetResult();
            // Observe the first failure without leaving detached fault-observer tasks behind.
            var waiting = new List<Task>(operations);
            while (waiting.Count != 0)
            {
                var completed = await Task.WhenAny(waiting).WaitAsync(token).ConfigureAwait(false);
                waiting.Remove(completed);
                await completed.ConfigureAwait(false);
            }
            // The probe's empty frame proves the runtime itself starts under these controls: it fails only at decoding.
            if (probe)
                return child.ExitCode == 1 && (await output.ConfigureAwait(false)).Length == 0 &&
                    (await error.ConfigureAwait(false)).AsSpan().SequenceEqual(ProbeRefusal)
                    ? new([], WorkerProcessFailure.None) : ChildAdmissionDiagnostics.Refused(ChildAdmissionFailure.RuntimeProbeRefusal);
            return child.ExitCode == 0 ? new(await output.ConfigureAwait(false), WorkerProcessFailure.None) : Failed(WorkerProcessFailure.WorkerFailed);
        }
        catch (OperationCanceledException) { return Failed(WorkerProcessFailure.Canceled); }
        catch (OutputLimitException) { return Failed(WorkerProcessFailure.OutputLimitExceeded); }
        catch (EndOfStreamException)
        {
            // The exit category is read only after the existing cleanup has reaped this child.
            markerMissing = true;
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { return Failed(WorkerProcessFailure.WorkerFailed); }
        finally
        {
            // The cleanup budget starts at entry, before notification, registration release, gate contention or work.
            var cleanupStartedAt = Stopwatch.GetTimestamp();
            operation.CleanupStartedAt = cleanupStartedAt;
            options.ObserveCleanupStart?.Invoke(operation, cleanupStartedAt);
            operation.InitialIoRegistered.TrySetResult();
            long cleanupDeadline;
            lock (gate)
            {
                cleanupDeadline = CleanupDeadlineFrom(cleanupStartedAt);
                if (stopping) cleanupDeadline = Math.Min(cleanupDeadline, stopDeadline);
                operation.BeginCleanup(cleanupDeadline);
            }
            var settled = await WithinDeadlineAsync(operation.Work!, cleanupDeadline).ConfigureAwait(false);
            operation.CleanupDecisionAt = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (settled) owned.Remove(operation);
                else Interlocked.Exchange(ref unavailable, 1);
                if (markerMissing && operation.ExitCode is { } code) startupFailure = ChildAdmissionDiagnostics.StartupExit(code);
            }
        }
        return ChildAdmissionDiagnostics.Refused(startupFailure);
    }

    private ChildAdmissionFailure Verify(int pid) => ChildSandboxVerification.Verify(pid, inheritedFilters + ProcessCompileOptions.LauncherFilters,
    [
        ("Max cpu time", ProcessCompiledRuntimeOptions.Profile.CpuSeconds),
        ("Max file size", ProcessCompiledRuntimeOptions.FileBytes), ("Max core file size", 0),
        ("Max open files", ProcessCompiledRuntimeOptions.Profile.FileLimit),
        ("Max address space", ProcessCompiledRuntimeOptions.AddressSpaceBytes),
        ("Max locked memory", 0), ("Max file locks", 0), ("Max msgqueue size", 0), ("Max realtime priority", 0)
    ], requireUnprivilegedIdentity: true);

    private async Task WriteAsync(OwnedOperation operation, ReadOnlyMemory<byte> frame, CancellationToken token)
    {
        var child = operation.Child!;
        try
        {
            await child.StandardInput.BaseStream.WriteAsync(new[] { ProcessCompileOptions.Go }, token).ConfigureAwait(false);
            await child.StandardInput.BaseStream.WriteAsync(frame, token).ConfigureAwait(false);
            await child.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
            operation.FrameFlushed.TrySetResult(child.Id);
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

    private static long CleanupDeadlineFrom(long startedAt) => startedAt +
        (long)(ProcessWorkerOptions.KillTimeout.TotalSeconds * Stopwatch.Frequency);

    private static long NewCleanupDeadline() => CleanupDeadlineFrom(Stopwatch.GetTimestamp());

    private static TimeSpan Remaining(long deadline) =>
        TimeSpan.FromSeconds(Math.Max(0, deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);

    internal static async Task<bool> WithinDeadlineAsync(Task<bool> work, long deadline,
        Func<Task<bool>, TimeSpan, Task<bool>>? wait = null)
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var remaining = Remaining(deadline);
            try
            {
                var settled = await (wait is null ? work.WaitAsync(remaining) : wait(work, remaining)).ConfigureAwait(false);
                return settled && Stopwatch.GetTimestamp() < deadline;
            }
            catch (TimeoutException)
            {
                // WaitAsync's timer can round an interval down. Retry only the remainder of this same absolute
                // deadline, including a sub-tick TimeSpan that rounds to zero. Never issue a new cleanup budget.
            }
        }
        return false;
    }

    private static bool Ordinary(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    /// <summary>Retains every resource, including an overdue cleanup task. Late settlement never reopens admission.</summary>
    internal sealed class OwnedOperation
    {
        private readonly ProcessCompiledRuntimeOptions options;
        internal OwnedOperation(string directory, ProcessCompiledRuntimeOptions options, CancellationToken caller, CancellationToken shutdown)
        {
            DirectoryPath = directory;
            this.options = options;
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown);
            WallDeadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            WallStartedAt = Stopwatch.GetTimestamp();
            WallTargetAt = WallStartedAt + (long)(options.WallTimeout.TotalSeconds * Stopwatch.Frequency);
            WallDeadline.CancelAfter(options.WallTimeout);
        }
        internal string DirectoryPath { get; }
        internal CancellationTokenSource Lifetime { get; }
        internal CancellationTokenSource WallDeadline { get; }
        internal CancellationTokenSource Io { get; } = new();
        internal List<Task> IoTasks { get; } = [];
        internal Process? Child { get; set; }
        internal int ChildPid { get; set; }
        internal bool Created { get; set; }
        internal TaskCompletionSource<int> FrameFlushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> ChildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource InitialIoRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<long> CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<bool>? Work { get; private set; }
        internal Task? Cancellation { get; private set; }
        internal Task? Reaped { get; private set; }
        internal Task? NativeExit { get; private set; }
        internal bool HandlesDisposed { get; private set; }
        internal bool DirectoryRemoved { get; private set; }
        internal Exception? Failure { get; private set; }
        internal long Deadline { get; private set; }
        internal int? ExitCode { get; private set; }
        internal long CompletedAt { get; private set; }
        internal long WallTargetAt { get; }
        internal long WallStartedAt { get; }
        internal long CleanupStartedAt { get; set; }
        internal long CleanupDecisionAt { get; set; }
        private readonly List<CompiledRuntimeCleanupStep> steps = [];
        private readonly List<CompiledRuntimeTaskOwnership> tasks = [];
        private readonly Dictionary<CompiledRuntimeIoKind, TaskCompletionSource<CompiledRuntimeTaskOwnership>> taskRegistered =
            Enum.GetValues<CompiledRuntimeIoKind>().ToDictionary(kind => kind,
                _ => new TaskCompletionSource<CompiledRuntimeTaskOwnership>(TaskCreationOptions.RunContinuationsAsynchronously));
        internal CompiledRuntimeCleanupStep[] Steps { get { lock (steps) return [.. steps]; } }
        internal CompiledRuntimeTaskOwnership[] Tasks { get { lock (tasks) return [.. tasks]; } }
        internal Task<CompiledRuntimeTaskOwnership> TaskRegistered(CompiledRuntimeIoKind kind) => taskRegistered[kind].Task;

        // The gate is inside the original operation task, before its real I/O delegate is launched. With null
        // qualification hooks it simply runs the real delegate. Both this task and any ownership wrapper are joined.
        internal Task CreateOriginal(CompiledRuntimeIoKind kind, Func<Task> action) => Task.Run(async () =>
        {
            if (options.BeforeIoLaunch is { } before) await before(this, kind).ConfigureAwait(false);
            await action().ConfigureAwait(false);
        });

        internal Task<T> CreateOriginal<T>(CompiledRuntimeIoKind kind, Func<Task<T>> action) => Task.Run(async () =>
        {
            if (options.BeforeIoLaunch is { } before) await before(this, kind).ConfigureAwait(false);
            return await action().ConfigureAwait(false);
        });

        internal void StartReapObservation(Process child)
        {
            // Start and retain the actual noncancelable process-exit wait while the child is still live, not only
            // after Kill. A launch-held confirmation task does not mean the kernel child is still unreaped.
            var nativeExit = child.WaitForExitAsync();
            NativeExit = nativeExit;
            Reaped = Track(CompiledRuntimeIoKind.ReapConfirmation,
                CreateOriginal(CompiledRuntimeIoKind.ReapConfirmation, () => nativeExit));
        }

        internal Task Track(CompiledRuntimeIoKind kind, Task original)
        {
            Task held;
            try { held = options.WrapOwnedTask?.Invoke(this, kind, original) ?? original; }
            catch (Exception error) when (Ordinary(error)) { held = Task.FromException(error); }
            var ownership = new CompiledRuntimeTaskOwnership(kind, original, held);
            lock (tasks) tasks.Add(ownership);
            if (kind != CompiledRuntimeIoKind.ReapConfirmation) IoTasks.Add(held);
            taskRegistered[kind].TrySetResult(ownership);
            return held;
        }

        internal void BeginCleanup(long deadline)
        {
            Deadline = deadline;
            // Cancellation callbacks and synchronous OS disposal/deletion cannot block the caller's bounded wait.
            // This task is owned by the runner, not an unretained continuation on an expired wait.
            Work = Task.Run(CleanupAsync);
            CleanupStarted.TrySetResult(deadline);
        }

        private async Task StageAsync(CompiledRuntimeCleanupStage stage)
        {
            lock (steps) steps.Add(new(stage, Deadline, Remaining(Deadline)));
            if (options.BeforeCleanupStage is { } before) await before(stage).ConfigureAwait(false);
        }

        private async Task<bool> CleanupAsync()
        {
            Task? joined = null;
            try
            {
                try { Cancellation = Io.CancelAsync(); }
                catch (Exception error) when (Ordinary(error)) { Cancellation = Task.FromException(error); }
                // Start termination before awaiting cancellation callbacks, including a deliberately held callback.
                if (Child is { } child)
                {
                    try { if (!child.HasExited) child.Kill(); }
                    catch (Exception error) when (Ordinary(error)) { Failure = error; }
                    try { if (Reaped is null) StartReapObservation(child); }
                    catch (Exception error) when (Ordinary(error)) { Reaped = Task.FromException(error); }
                }
                else Reaped = Task.CompletedTask;
                // Construct the complete join before any test barrier or fallible stage. Even an exceptional stage
                // cannot make Work complete while its cancellation, exit or I/O tasks are still outstanding.
                joined = Task.WhenAll(Tasks.SelectMany(task => new[] { task.Original, task.Owned })
                    .Append(NativeExit ?? Task.CompletedTask).Append(Reaped!).Append(Cancellation!).Distinct());
                await StageAsync(CompiledRuntimeCleanupStage.Process).ConfigureAwait(false);
                await StageAsync(CompiledRuntimeCleanupStage.Callbacks).ConfigureAwait(false);
                await StageAsync(CompiledRuntimeCleanupStage.Pipes).ConfigureAwait(false);
                try { await joined.ConfigureAwait(false); }
                catch (Exception error) when (joined.IsCompleted && Ordinary(error))
                {
                    // Canceled/failed I/O is expected when terminating an invocation, but a callback failure is not.
                    if (Cancellation!.IsFaulted) Failure = Cancellation.Exception;
                    if (Reaped!.IsFaulted) Failure = Reaped.Exception;
                }
                if (Child is not null && !Child.HasExited) return false;
                ExitCode = Child?.ExitCode;
                await StageAsync(CompiledRuntimeCleanupStage.Handles).ConfigureAwait(false);
                Child?.Dispose();
                Io.Dispose();
                WallDeadline.Dispose();
                Lifetime.Dispose();
                HandlesDisposed = true;
                await StageAsync(CompiledRuntimeCleanupStage.Directory).ConfigureAwait(false);
                if (Created) Directory.Delete(DirectoryPath, recursive: true);
                DirectoryRemoved = true;
                return Failure is null;
            }
            catch (Exception error) when (Ordinary(error)) { Failure = error; return false; }
            finally
            {
                if (joined is not null)
                    try { await joined.ConfigureAwait(false); }
                    catch (Exception error) when (joined.IsCompleted && Ordinary(error)) { /* All failures observed. */ }
                CompletedAt = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// <c>sha256:</c> over the entry filename, runtime host, .NET host and pinned runtime, launcher and runner, the ELF
    /// interpreter and every system library the host, runtime and launcher can load (each file's label, length and
    /// SHA-256 in a fixed order).
    /// </summary>
    /// <param name="interpreters">The surviving native launcher's ELF interpreter, mapped by its initial exec.</param>
    /// <param name="libraryDirectories">The granted system library directories, which the image fills from unpinned packages.</param>
    internal static string ComputeIdentity(ProcessCompiledRuntimeOptions options, IReadOnlyList<string> interpreters,
        IReadOnlyList<string> libraryDirectories)
    {
        var manifest = new StringBuilder("weft.compiled-runtime-identity.v1\nentry=")
            .Append(Path.GetFileName(options.RuntimeAssembly)).Append('\n');
        // Mapping identity bytes are deliberately unchanged. XML gets an explicitly distinct operation identity.
        if (options.Operation == CompiledRuntimeOperation.XmlCanonicalComparison)
            manifest.Append("operation=").Append(XmlComparisonProtocol.ModeArgument).Append('\n')
                .Append("protocol=").Append(XmlComparisonProtocol.Version).Append('\n')
                .Append("comparer=").Append(XmlComparisonProtocol.Comparer).Append('\n');
        if (options.Operation == CompiledRuntimeOperation.OfficeExtraction)
            manifest.Append("operation=office-extraction\nprotocol=").Append(SpecOfficeProcessProtocol.Version).Append('\n')
                .Append("extractor=").Append(SpecOfficeExtractionResult.ExtractorVersion).Append('\n');
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
        foreach (var library in ElfLibraryClosure.Resolve(objects.Concat(interpreters), ElfLibraryClosure.RuntimeOpenedLibraries, libraryDirectories)
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
            stopDeadline = NewCleanupDeadline();
            if (active == 0) drained.TrySetResult();
            stopWork = Task.Run(StopOwnedAsync);
            return stop = StopCoreAsync(stopDeadline);
        }
    }
    private async Task<bool> StopOwnedAsync()
    {
        try
        {
            var cancellation = shutdown.CancelAsync();
            var all = Task.WhenAll(cancellation, drained.Task);
            try { await all.ConfigureAwait(false); }
            catch (Exception error) when (all.IsCompleted && Ordinary(error)) { Interlocked.Exchange(ref unavailable, 1); }
            OwnedOperation[] retained;
            lock (gate) retained = [.. owned];
            foreach (var operation in retained)
                if (operation.Work is { } work && !await work.ConfigureAwait(false)) Interlocked.Exchange(ref unavailable, 1);
            if (Volatile.Read(ref unavailable) != 0) return false;
            if (Interlocked.Exchange(ref disposed, 1) == 0) shutdown.Dispose();
            return true;
        }
        finally { Interlocked.Exchange(ref stopCompletedAt, Stopwatch.GetTimestamp()); }
    }

    private async Task StopCoreAsync(long deadline)
    {
        try
        {
            if (Volatile.Read(ref unavailable) != 0 || !await WithinDeadlineAsync(stopWork!, deadline).ConfigureAwait(false) || Volatile.Read(ref unavailable) != 0)
            {
                Interlocked.Exchange(ref unavailable, 1);
                throw new InvalidOperationException("Compiled runtime cleanup was not confirmed within its aggregate budget.");
            }
        }
        finally { Interlocked.Exchange(ref stopDecisionAt, Stopwatch.GetTimestamp()); }
    }

    // Qualification can join the very same retained owners after releasing its barriers. This never clears failure.
    internal async Task JoinRetainedCleanupAsync()
    {
        List<Task<bool>> work;
        lock (gate)
        {
            work = [.. owned.Select(operation => operation.Work).OfType<Task<bool>>()];
            if (stopWork is { } pending) work.Add(pending);
        }
        await Task.WhenAll(work).ConfigureAwait(false);
    }

    internal long StopDeadline { get { lock (gate) return stopDeadline; } }
    internal long StopCompletedAt => Interlocked.Read(ref stopCompletedAt);
    internal long StopDecisionAt => Interlocked.Read(ref stopDecisionAt);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
    private static WorkerProcessResult Failed(WorkerProcessFailure failure) => new([], failure);
    private sealed class OutputLimitException : Exception;
}
