using System.Globalization;
using Weft.Deployment;

namespace Weft.MappingBackends.Workers;

/// <summary>Trusted installation for a fresh read-only compiled-mapping child, never selected by a request.</summary>
public sealed class ProcessCompiledRuntimeOptions
{
    public const long AddressSpaceBytes = 3L << 30;
    public const long FileBytes = 1L << 20;
    public static DockerWorkerProfile Profile => DockerWorkerProfile.Compiled;

    public ProcessCompiledRuntimeOptions(string nativeIdentity, string runtimeAssembly, string launcherExecutable,
        string temporaryRoot, string dotnetRoot = BuildCompilation.DotnetRoot, TimeSpan? wallTimeout = null)
    {
        NativeIdentity = InspectionTransport.RequireImage(nativeIdentity);
        RuntimeAssembly = ProcessWorkerOptions.AbsolutePath(runtimeAssembly, nameof(runtimeAssembly));
        LauncherExecutable = ProcessWorkerOptions.AbsolutePath(launcherExecutable, nameof(launcherExecutable));
        TemporaryRoot = ProcessWorkerOptions.AbsolutePath(temporaryRoot, nameof(temporaryRoot));
        DotnetRoot = ProcessWorkerOptions.AbsolutePath(dotnetRoot, nameof(dotnetRoot));
        WallTimeout = wallTimeout ?? Profile.MaximumWallTimeout;
        if (WallTimeout <= TimeSpan.Zero || WallTimeout > Profile.MaximumWallTimeout) throw new ArgumentOutOfRangeException(nameof(wallTimeout));
    }
    public string NativeIdentity { get; }
    public string RuntimeAssembly { get; }
    public string LauncherExecutable { get; }
    public string TemporaryRoot { get; }
    public string DotnetRoot { get; }
    public TimeSpan WallTimeout { get; }
    // Trusted composition only. No request field can select a process operation.
    internal CompiledRuntimeOperation Operation { get; init; }
    internal bool WithholdEndOfInput { get; init; }
    // Parent-side qualification seams only. None is populated from service/request configuration.
    internal Action<ProcessCompiledRuntimeRunner.OwnedOperation>? ObserveOperation { get; init; }
    internal Action<CancellationToken>? ObserveShutdown { get; init; }
    internal Func<CompiledRuntimeCleanupStage, Task>? BeforeCleanupStage { get; init; }
    internal Func<ProcessCompiledRuntimeRunner.OwnedOperation, CompiledRuntimeIoKind, Task, Task>? WrapOwnedTask { get; init; }
    internal Func<ProcessCompiledRuntimeRunner.OwnedOperation, Task>? BeforeLaunch { get; init; }
    internal Func<ProcessCompiledRuntimeRunner.OwnedOperation, CompiledRuntimeIoKind, Task>? BeforeIoLaunch { get; init; }
    internal Action<ProcessCompiledRuntimeRunner.OwnedOperation, long>? ObserveCleanupStart { get; init; }

    internal IReadOnlyDictionary<string, string> Environment(string directory) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PATH"] = DotnetRoot + ":/usr/bin:/bin",
        ["HOME"] = directory,
        ["TMPDIR"] = directory,
        ["DOTNET_ROOT"] = DotnetRoot,
        ["DOTNET_CLI_HOME"] = directory,
        ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true",
        ["DOTNET_NOLOGO"] = "true",
        ["DOTNET_PROCESSOR_COUNT"] = "1",
        ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1",
        ["DOTNET_ROLL_FORWARD"] = "Disable",
        ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
        ["DOTNET_EnableDiagnostics"] = "0",
        // W^X double-maps JIT code through a memory file that grows past the file-size bound; the sandbox is the boundary.
        ["DOTNET_EnableWriteXorExecute"] = "0",
        ["DOTNET_GCHeapHardLimit"] = "0x" + Profile.MemoryBytes.ToString("x", CultureInfo.InvariantCulture),
        ["DOTNET_GCRegionRange"] = "0x" + Profile.MemoryBytes.ToString("x", CultureInfo.InvariantCulture),
        ["DOTNET_GCNoAffinitize"] = "1",
        ["MALLOC_ARENA_MAX"] = "2"
    };

    internal IReadOnlyList<string> Arguments(ProcessCompileOptions.ToolchainGrants grants)
    {
        var result = new List<string>
        {
            "--cpu-seconds=" + Profile.CpuSeconds.ToString(CultureInfo.InvariantCulture),
            "--memory-bytes=" + AddressSpaceBytes.ToString(CultureInfo.InvariantCulture),
            "--open-files=" + Profile.FileLimit.ToString(CultureInfo.InvariantCulture),
            "--file-bytes=" + FileBytes.ToString(CultureInfo.InvariantCulture)
        };
        result.AddRange(["--dotnet-root=" + DotnetRoot, "--profile=runtime", "--runtime-assembly=" + RuntimeAssembly]);
        result.AddRange(grants.Read.Append(Path.GetDirectoryName(RuntimeAssembly)!).Distinct(StringComparer.Ordinal).Select(path => "--read=" + path));
        if (Operation == CompiledRuntimeOperation.XmlCanonicalComparison) result.Add(XmlComparisonProtocol.ModeArgument);
        else if (Operation is not (CompiledRuntimeOperation.Mapping or CompiledRuntimeOperation.OfficeExtraction)) throw new InvalidOperationException("Unknown fixed runtime operation.");
        return result;
    }
}

internal enum CompiledRuntimeOperation { Mapping, XmlCanonicalComparison, OfficeExtraction }

internal enum CompiledRuntimeCleanupStage { Process, Callbacks, Pipes, Handles, Directory }

internal readonly record struct CompiledRuntimeCleanupStep(CompiledRuntimeCleanupStage Stage, long Deadline, TimeSpan Remaining);

internal enum CompiledRuntimeIoKind { StandardError, Handshake, StandardOutput, Writer, ExitObserver, ReapConfirmation }

internal sealed record CompiledRuntimeTaskOwnership(CompiledRuntimeIoKind Kind, Task Original, Task Owned);
