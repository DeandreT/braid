using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Weft.MappingBackends.Workers;

/// <summary>Runs a locally provisioned inspection image under a fixed Linux Docker policy.</summary>
public sealed partial class DockerInspectionWorkerRunner : IInspectionWorkerRunner, IAsyncDisposable
{
    private const int ControlOutputLimit = 64 * 1024;
    private const string OperationLabel = "io.weft.inspection.operation.v1";
    private const string OwnershipInspectFormat = """{"Id":{{json .Id}},"Name":{{json .Name}},"Config":{"Labels":{{json .Config.Labels}}}}""";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly DockerInspectionWorkerOptions options;
    private readonly IWorkerProcessLauncher launcher;
    private readonly SemaphoreSlim admission = new(1, 1);
    private int unavailable;

    public DockerInspectionWorkerRunner(DockerInspectionWorkerOptions options)
        : this(options, new WorkerProcessLauncher())
    {
    }

    public DockerInspectionWorkerRunner(DockerInspectionWorkerOptions options, IWorkerProcessLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launcher);
        this.options = options;
        this.launcher = launcher;
    }

    public string BuildIdentity => options.ImageId;
    public DockerWorkerProfile Profile => options.Profile;

    private async Task<WorkerProcessResult> RunOwnedAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
    {
        if (requestFrame.Length > options.Profile.MaximumRequestBytes)
        {
            throw new ArgumentException("The request frame exceeds the fixed transport limit.", nameof(requestFrame));
        }

        if (!OperatingSystem.IsLinux() || Volatile.Read(ref unavailable) != 0)
        {
            return Failed(WorkerProcessFailure.SandboxUnavailable);
        }

        try
        {
            await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failed(WorkerProcessFailure.Canceled);
        }

        try
        {
            if (Volatile.Read(ref unavailable) != 0)
            {
                return Failed(WorkerProcessFailure.SandboxUnavailable);
            }

            return await RunAdmittedAsync(requestFrame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            admission.Release();
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task<WorkerProcessResult> RunAdmittedAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
    {
        using var wallDeadline = new CancellationTokenSource(options.WallTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wallDeadline.Token);
        var containerName = $"weft-inspect-{Guid.NewGuid():N}";
        // Independent of the discoverable name; this is an attribution marker, not a credential.
        var operationOwner = Guid.NewGuid().ToString("N");
        string? configDirectory = null;
        var cleanupRequired = false;
        string? createdContainerId = null;
        var result = Failed(WorkerProcessFailure.SandboxUnavailable);
        try
        {
            configDirectory = Directory.CreateTempSubdirectory("weft-docker-").FullName;
            File.SetUnixFileMode(configDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var daemon = await CommandAsync(configDirectory, ["info", "--format", "{{json .}}"], ReadOnlyMemory<byte>.Empty, ControlOutputLimit, deadline.Token).ConfigureAwait(false);
            if (daemon.Failure != WorkerProcessFailure.None)
            {
                result = Failed(daemon.Failure);
            }
            else if (daemon.ExitCode == 0 && DockerSandboxMetadata.IsSupportedDaemon(daemon.Output))
            {
                var image = await CommandAsync(configDirectory, ["image", "inspect", "--format", "{{json .}}", options.ImageId], ReadOnlyMemory<byte>.Empty, ControlOutputLimit, deadline.Token).ConfigureAwait(false);
                if (image.Failure != WorkerProcessFailure.None)
                {
                    result = Failed(image.Failure);
                }
                else if (image.ExitCode == 0 && DockerSandboxMetadata.IsSafeImage(image.Output, options.ImageId))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    // The daemon may create the named container even when the CLI loses its response.
                    // Settle this side effect within the original wall budget even if the caller leaves.
                    cleanupRequired = true;
                    var create = await CommandAsync(configDirectory, CreateArguments(containerName, operationOwner), ReadOnlyMemory<byte>.Empty, ControlOutputLimit, wallDeadline.Token).ConfigureAwait(false);
                    cleanupRequired = create.Started;
                    result = Failed(create.Failure == WorkerProcessFailure.None ? WorkerProcessFailure.WorkerFailed : create.Failure);
                    createdContainerId = create.Failure == WorkerProcessFailure.None && create.ExitCode == 0
                        ? ReadContainerId(create.Output) : null;
                    deadline.Token.ThrowIfCancellationRequested();
                    if (createdContainerId is not null)
                    {
                        var inspect = await CommandAsync(configDirectory, ["container", "inspect", "--format", "{{json .}}", createdContainerId], ReadOnlyMemory<byte>.Empty, ControlOutputLimit, deadline.Token).ConfigureAwait(false);
                        result = Failed(inspect.Failure == WorkerProcessFailure.None ? WorkerProcessFailure.SandboxUnavailable : inspect.Failure);
                        if (inspect.Failure == WorkerProcessFailure.None && inspect.ExitCode == 0
                            && ReadOwnedContainerId(inspect.Output, containerName, operationOwner, createdContainerId) is not null
                            && DockerSandboxMetadata.IsIsolatedContainer(inspect.Output, options.ImageId, options.Profile))
                        {
                            var worker = await CommandAsync(configDirectory, ["container", "start", "--attach", "--interactive", createdContainerId], requestFrame, options.Profile.MaximumOutputBytes, deadline.Token, binaryOutput: true).ConfigureAwait(false);
                            result = worker.Failure != WorkerProcessFailure.None
                                ? Failed(worker.Failure)
                                : worker.ExitCode != 0
                                    ? Failed(WorkerProcessFailure.WorkerFailed)
                                    : new WorkerProcessResult(worker.Output, WorkerProcessFailure.None);
                        }
                    }
                }
            }
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
            if (cleanupRequired && configDirectory is not null)
            {
                using var cleanupDeadline = new CancellationTokenSource(DockerInspectionWorkerOptions.CleanupTimeout);
                if (!await RemoveOwnedContainerAsync(configDirectory, containerName, operationOwner, createdContainerId, cleanupDeadline.Token).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref unavailable, 1);
                    result = Failed(WorkerProcessFailure.CleanupFailed);
                }
            }

            if (configDirectory is not null)
            {
                try
                {
                    Directory.Delete(configDirectory, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Exchange(ref unavailable, 1);
                    result = Failed(WorkerProcessFailure.CleanupFailed);
                }
            }
        }

        if (Volatile.Read(ref unavailable) != 0)
        {
            return Failed(WorkerProcessFailure.CleanupFailed);
        }

        return result.Failure == WorkerProcessFailure.Canceled && !cancellationToken.IsCancellationRequested
            ? Failed(WorkerProcessFailure.TimedOut)
            : result;
    }

    private string Memory => options.Profile.MemoryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string CpuLimit => FormattableString.Invariant($"cpu={options.Profile.CpuSeconds}:{options.Profile.CpuSeconds}");

    private string[] CreateArguments(string containerName, string operationOwner) =>
    [
        "container", "create", "--name", containerName, "--pull", "never", "--interactive",
        "--label", $"{OperationLabel}={operationOwner}",
        "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true",
        "--user", "65532:65532", "--cpus", "1", "--memory", Memory, "--memory-swap", Memory,
        "--pids-limit", options.Profile.ProcessLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--ulimit", $"nofile={options.Profile.FileLimit}:{options.Profile.FileLimit}", "--ulimit", CpuLimit, "--ulimit", "core=0:0",
        "--ipc", "none", "--cgroupns", "private", "--no-healthcheck", "--restart", "no", "--log-driver", "none",
        .. (options.Profile == DockerWorkerProfile.Build ? new[] { "--tmpfs", "/tmp:" + DockerWorkerProfile.BuildTemporaryMount } : []),
        "--workdir", "/", "--entrypoint", options.Profile.Entrypoint, options.ImageId
    ];

    private async Task<bool> RemoveOwnedContainerAsync(
        string configDirectory, string containerName, string operationOwner, string? createdContainerId, CancellationToken cancellationToken)
    {
        // A name is only a read-only discovery key when the create response was lost.
        // A failed/conflicting create never authorizes adopting or removing that name.
        var target = createdContainerId ?? containerName;
        var inspect = await CommandAsync(configDirectory, ["container", "inspect", "--format", OwnershipInspectFormat, target], ReadOnlyMemory<byte>.Empty, ControlOutputLimit, cancellationToken).ConfigureAwait(false);
        if (inspect.Failure != WorkerProcessFailure.None)
        {
            return false;
        }

        if (inspect.ExitCode != 0)
        {
            // Only an acknowledged create has settled publication. Missing by name after
            // an interrupted create cannot rule out a daemon request still in flight.
            return createdContainerId is not null && inspect.Output.Length == 0 && IsMissingContainer(inspect.Error, createdContainerId);
        }

        var ownedId = ReadOwnedContainerId(inspect.Output, containerName, operationOwner, createdContainerId);
        if (ownedId is null)
        {
            return false;
        }

        // Never re-resolve the mutable name for a destructive operation. Name reuse or
        // reassignment after verification cannot redirect removal to a different ID.
        var cleanup = await CommandAsync(configDirectory, ["container", "rm", "--force", "--volumes", ownedId], ReadOnlyMemory<byte>.Empty, ControlOutputLimit, cancellationToken).ConfigureAwait(false);
        var removed = cleanup.ExitCode == 0 && cleanup.Error.Length == 0 && IsRemovedContainer(cleanup.Output, ownedId);
        var absent = createdContainerId is not null && cleanup.Output.Length == 0
            && ((cleanup.ExitCode == 0 && cleanup.Error.Length == 0) || IsMissingContainer(cleanup.Error, ownedId));
        // Finding a marked object permits exact-ID cleanup, but does not fabricate the
        // lost create acknowledgement: uncertain creation still requires removal's ID.
        return cleanup.Failure == WorkerProcessFailure.None && (removed || absent);
    }

    private async Task<CommandResult> CommandAsync(
        string configDirectory,
        string[] arguments,
        ReadOnlyMemory<byte> input,
        int outputLimit,
        CancellationToken cancellationToken,
        bool binaryOutput = false)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Failed(WorkerProcessFailure.Canceled);
        }

        IWorkerProcess process;
        try
        {
            process = launcher.Start(StartInfo(configDirectory, arguments));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return CommandResult.Failed(WorkerProcessFailure.StartFailed);
        }

        using (process)
        using (var ioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var output = DrainAsync(process.StandardOutput, outputLimit, ioCancellation.Token);
            var error = DrainAsync(process.StandardError, ControlOutputLimit, ioCancellation.Token);
            Task[] operations = [output, error, WriteInputAsync(process, input, ioCancellation.Token), process.WaitForExitAsync(ioCancellation.Token)];
            var all = Task.WhenAll(operations);
            var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var operation in operations)
            {
                _ = ReportFailureAsync(operation, fault);
            }

            WorkerProcessFailure failure;
            try
            {
                var completed = await Task.WhenAny(all, fault.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (completed == fault.Task)
                {
                    throw await fault.Task.ConfigureAwait(false);
                }

                await all.ConfigureAwait(false);
                var errorBytes = await error.ConfigureAwait(false);
                _ = StrictUtf8.GetString(errorBytes);
                var outputBytes = await output.ConfigureAwait(false);
                if (!binaryOutput)
                {
                    _ = StrictUtf8.GetString(outputBytes);
                }

                return new CommandResult(outputBytes, errorBytes, process.ExitCode, WorkerProcessFailure.None, true);
            }
            catch (OperationCanceledException)
            {
                failure = WorkerProcessFailure.Canceled;
            }
            catch (OutputLimitException)
            {
                failure = WorkerProcessFailure.OutputLimitExceeded;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or DecoderFallbackException or System.ComponentModel.Win32Exception)
            {
                failure = WorkerProcessFailure.WorkerFailed;
            }

            ioCancellation.Cancel();
            try
            {
                process.Kill();
                // WaitAsync enforces the bound even if a process-seam implementation ignores cancellation.
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(DockerInspectionWorkerOptions.KillTimeout).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException or IOException)
            {
                Interlocked.Exchange(ref unavailable, 1);
                failure = WorkerProcessFailure.CleanupFailed;
            }

            // Observe late pipe failures without waiting for a stream or daemon to cooperate.
            _ = ReportFailureAsync(all, new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously));
            return CommandResult.Failed(failure, started: true);
        }
    }

    private ProcessStartInfo StartInfo(string configDirectory, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(options.DockerExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            StandardInputEncoding = StrictUtf8,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = configDirectory
        };
        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = configDirectory;
        startInfo.Environment["DOCKER_CONFIG"] = configDirectory;
        startInfo.Environment["LC_ALL"] = "C.UTF-8";
        foreach (var argument in new[] { "--config", configDirectory, "--host", $"unix://{options.DockerSocketPath}" }.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<byte[]> DrainAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return buffer.ToArray();
            }

            if (count > limit - buffer.Length)
            {
                throw new OutputLimitException();
            }

            buffer.Write(chunk, 0, count);
        }
    }

    private static async Task WriteInputAsync(IWorkerProcess process, ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        try
        {
            var stream = process.StandardInput;
            await stream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            process.CloseStandardInput();
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

    private static string? ReadContainerId(byte[] bytes)
    {
        var id = StrictUtf8.GetString(bytes).TrimEnd('\n', '\r');
        return id.Length == 64 && id.AsSpan().ContainsOnlyHexadecimal() ? id : null;
    }

    private static string? ReadOwnedContainerId(byte[] bytes, string containerName, string operationOwner, string? expectedId)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!UniqueProperty(root, "Id", out var id) || id.ValueKind != JsonValueKind.String
                || !UniqueProperty(root, "Name", out var name) || name.ValueKind != JsonValueKind.String
                || name.GetString() != "/" + containerName
                || !UniqueProperty(root, "Config", out var config)
                || !UniqueProperty(config, "Labels", out var labels)
                || !UniqueProperty(labels, OperationLabel, out var owner) || owner.ValueKind != JsonValueKind.String
                || owner.GetString() != operationOwner)
            {
                return null;
            }

            var value = id.GetString()!;
            return value.Length == 64 && value.AsSpan().ContainsOnlyHexadecimal()
                && (expectedId is null || value == expectedId) ? value : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool UniqueProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (property.NameEquals(name))
            {
                if (found) return false;
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    private static bool IsMissingContainer(byte[] bytes, string containerId)
    {
        var error = StrictUtf8.GetString(bytes).Trim();
        return error == $"Error response from daemon: No such container: {containerId}"
            || error == $"Error: No such container: {containerId}"
            || error == $"Error: No such object: {containerId}";
    }

    private static bool IsRemovedContainer(byte[] bytes, string containerId) =>
        string.Equals(StrictUtf8.GetString(bytes).TrimEnd('\n', '\r'), containerId, StringComparison.Ordinal);

    private static WorkerProcessResult Failed(WorkerProcessFailure failure) => new([], failure);
    private sealed class OutputLimitException : Exception { }

    private sealed record CommandResult(byte[] Output, byte[] Error, int ExitCode, WorkerProcessFailure Failure, bool Started)
    {
        public static CommandResult Failed(WorkerProcessFailure failure, bool started = false) => new([], [], -1, failure, started);
    }
}
