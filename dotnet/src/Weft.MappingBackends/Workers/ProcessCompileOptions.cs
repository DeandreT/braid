using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Weft.Deployment;

namespace Weft.MappingBackends.Workers;

/// <summary>
/// Trusted Ferrule service configuration for compile operations, never supplied by a request. Generation runs in
/// the worker's own service sandbox with the build profile's limits; compilation runs the pinned compiler in a
/// fresh self-hardening launcher (<c>weft-compile-sandbox</c>) with the limits below. One wall deadline, the build
/// profile's, covers both stages.
/// </summary>
public sealed class ProcessCompileOptions
{
    /// <summary>The launcher writes these bytes once every control is in force and before the compiler starts.</summary>
    public static ReadOnlySpan<byte> ReadyMarker => "WFC1"u8;

    /// <summary>The one byte that starts the compiler after the controls are verified.</summary>
    public const byte Go = (byte)'G';

    /// <summary>Syscall filters the launcher adds to the service's own.</summary>
    public const int LauncherFilters = 2;

    /// <summary>The compiler's managed heap bound: the build container profile's memory.</summary>
    public static long HeapBytes => DockerWorkerProfile.Build.MemoryBytes;

    /// <summary>The compiler host's address space: the heap reservation, its code reservation, the mapped toolchain and its threads.</summary>
    public const long AddressSpaceBytes = 6L << 30;

    /// <summary>The largest file the compiler can write; the only writable file is the pre-created output assembly.</summary>
    public const long FileBytes = 64L << 20;

    /// <summary>Compiler diagnostics on standard output are drained and discarded up to this bound.</summary>
    public const int DiagnosticsLimit = 1024 * 1024;

    public ProcessCompileOptions(string workerExecutable, string launcherExecutable, string temporaryRoot,
        string toolchainRoot = BuildCompilation.DotnetRoot, TimeSpan? wallTimeout = null)
    {
        generation = new ProcessWorkerOptions(workerExecutable, DockerWorkerProfile.Build, temporaryRoot, wallTimeout);
        LauncherExecutable = ProcessWorkerOptions.AbsolutePath(launcherExecutable, nameof(launcherExecutable));
        ToolchainRoot = ProcessWorkerOptions.AbsolutePath(toolchainRoot, nameof(toolchainRoot));
    }

    private readonly ProcessWorkerOptions generation;

    /// <summary>The generation stage: the worker in its service sandbox with the build profile's limits.</summary>
    public ProcessWorkerOptions Generation
    {
        get => generation;
        // Test seam: the same stage with its own observation settings.
        internal init => generation = value?.Profile == DockerWorkerProfile.Build
            ? value : throw new ArgumentException("Generation runs with the build profile.", nameof(value));
    }
    public string LauncherExecutable { get; }
    /// <summary>The installed .NET root holding the host, the pinned compiler and the reference assemblies.</summary>
    public string ToolchainRoot { get; }
    public string TemporaryRoot => Generation.TemporaryRoot;
    public TimeSpan WallTimeout => Generation.WallTimeout;
    public static DockerWorkerProfile Profile => DockerWorkerProfile.Build;

    /// <summary>Test seam: replaces the compiler command (absolute program and arguments) to observe termination.</summary>
    internal IReadOnlyList<string>? CompilerCommand { get; init; }

    /// <summary>The environment of the compiler host: nothing of the service, a bounded heap and no diagnostics channel.</summary>
    internal IReadOnlyDictionary<string, string> Environment(string directory) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PATH"] = ToolchainRoot + ":/usr/bin:/bin",
        ["HOME"] = directory,
        ["TMPDIR"] = directory,
        ["DOTNET_ROOT"] = ToolchainRoot,
        ["DOTNET_CLI_HOME"] = directory,
        ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "true",
        ["DOTNET_NOLOGO"] = "true",
        ["DOTNET_PROCESSOR_COUNT"] = "1",
        ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1",
        ["DOTNET_ROLL_FORWARD"] = "Disable",
        ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_EnableDiagnostics"] = "0",
        // A fixed heap and an equal reservation keep the host's address space deterministic under its limit.
        ["DOTNET_GCHeapHardLimit"] = "0x" + HeapBytes.ToString("x", CultureInfo.InvariantCulture),
        ["DOTNET_GCRegionRange"] = "0x" + HeapBytes.ToString("x", CultureInfo.InvariantCulture),
        ["DOTNET_GCNoAffinitize"] = "1",
        ["MALLOC_ARENA_MAX"] = "2"
    };

    /// <summary>
    /// The exact launcher arguments: fixed limits, the one writable output (absent for a probe), read access to the
    /// toolchain, the system libraries and <c>/dev/urandom</c>, execute access to the host's ELF interpreter, then
    /// the compiler command.
    /// </summary>
    internal IReadOnlyList<string> LauncherArguments(string directory, ToolchainGrants grants, bool output)
    {
        string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "--cpu-seconds=" + Text(Profile.CpuSeconds),
            "--memory-bytes=" + Text(AddressSpaceBytes),
            "--open-files=" + Text(Profile.FileLimit),
            "--file-bytes=" + Text(FileBytes)
        };
        if (output) arguments.Add("--output=" + BuildCompilation.Output);
        arguments.AddRange(grants.Read.Select(path => "--read=" + path));
        arguments.AddRange(grants.Execute.Select(path => "--execute=" + path));
        arguments.Add("--");
        if (CompilerCommand is { } command) arguments.AddRange(command);
        else
        {
            arguments.Add(BuildCompilation.Host(ToolchainRoot));
            arguments.AddRange(BuildCompilation.CompilerArguments(ToolchainRoot, directory));
        }

        return arguments;
    }

    /// <summary>Read and execute grants derived once from the installed toolchain and system layout.</summary>
    internal sealed record ToolchainGrants(IReadOnlyList<string> Read, IReadOnlyList<string> Execute)
    {
        private static readonly string[] SystemLibraries = ["/lib", "/lib64", "/usr/lib", "/usr/lib64"];

        internal static ToolchainGrants Resolve(ProcessCompileOptions options)
            => ResolveRoot(options.ToolchainRoot);

        internal static ToolchainGrants ResolveRoot(string root)
        {
            var host = BuildCompilation.Host(root);
            var interpreter = Interpreter(host) ?? throw new ArgumentException("The .NET host has no admissible ELF interpreter.");
            var read = new List<string> { root };
            read.AddRange(SystemLibraries.Where(Directory.Exists));
            if (File.Exists("/dev/urandom")) read.Add("/dev/urandom");
            return new(read, [ProcessWorkerOptions.AbsolutePath(interpreter, nameof(interpreter))]);
        }

        /// <summary>The PT_INTERP path of a 64-bit little-endian ELF executable, which <c>execve</c> opens for execution.</summary>
        private static string? Interpreter(string executable)
        {
            using var file = File.OpenRead(executable);
            Span<byte> header = stackalloc byte[64];
            file.ReadExactly(header);
            if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F' || header[4] != 2 || header[5] != 1) return null;
            var table = BinaryPrimitives.ReadInt64LittleEndian(header[0x20..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(header[0x36..]);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(header[0x38..]);
            if (size < 56 || table < 64) return null;
            Span<byte> entry = stackalloc byte[56];
            for (var index = 0; index < count; index++)
            {
                file.Position = table + (long)index * size;
                file.ReadExactly(entry);
                if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != 3) continue;
                var offset = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
                var length = BinaryPrimitives.ReadInt64LittleEndian(entry[32..]);
                if (length is < 2 or > 4096) return null;
                var path = new byte[length];
                file.Position = offset;
                file.ReadExactly(path);
                return path[^1] == 0 ? Encoding.ASCII.GetString(path, 0, path.Length - 1) : null;
            }

            return null;
        }
    }
}
