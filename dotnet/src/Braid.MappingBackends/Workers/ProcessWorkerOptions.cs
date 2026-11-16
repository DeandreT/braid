using System.Globalization;

namespace Braid.MappingBackends.Workers;

/// <summary>
/// Trusted Ferrule service configuration for fresh worker child processes, never supplied by a request. The
/// profile supplies the same CPU, memory, descriptor, deadline and frame bounds as the restricted container.
/// </summary>
public sealed class ProcessWorkerOptions
{
    /// <summary>The worker writes these bytes once every sandbox control is in force and before it reads input.</summary>
    public static ReadOnlySpan<byte> ReadyMarker => "BFS1"u8;

    /// <summary>Bound for a killed child to be reaped; an unreaped child is unconfirmed cleanup.</summary>
    public static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(2);

    public const int StandardErrorLimit = 64 * 1024;

    public ProcessWorkerOptions(string workerExecutable, DockerWorkerProfile profile, string temporaryRoot, TimeSpan? wallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // The build profile runs the native generation stage of a compile operation; compilation itself runs in
        // the separate compile sandbox (ProcessCompileRunner).
        if (profile != DockerWorkerProfile.Inspection && profile != DockerWorkerProfile.Edit && profile != DockerWorkerProfile.Authoring && profile != DockerWorkerProfile.AuthoredPreview && profile != DockerWorkerProfile.Preview && profile != DockerWorkerProfile.Build && profile != DockerWorkerProfile.Interchange && profile != DockerWorkerProfile.Pdf)
            throw new ArgumentException("Service child processes require a supported fixed worker profile.", nameof(profile));
        WorkerExecutable = AbsolutePath(workerExecutable, nameof(workerExecutable));
        TemporaryRoot = AbsolutePath(temporaryRoot, nameof(temporaryRoot));
        Profile = profile;
        WallTimeout = wallTimeout ?? profile.MaximumWallTimeout;
        if (WallTimeout <= TimeSpan.Zero || WallTimeout > profile.MaximumWallTimeout)
            throw new ArgumentOutOfRangeException(nameof(wallTimeout), "The wall deadline must be positive and within the selected profile bound.");
    }

    public string WorkerExecutable { get; }
    /// <summary>An existing directory; each operation gets a fresh private empty subdirectory as its working directory.</summary>
    public string TemporaryRoot { get; }
    public DockerWorkerProfile Profile { get; }
    public TimeSpan WallTimeout { get; }

    /// <summary>Test seam: keeps standard input open so a real worker stays alive until the deadline.</summary>
    internal bool WithholdEndOfInput { get; init; }

    /// <summary>The exact arguments that select the worker's self-hardening mode and its fixed limits.</summary>
    public IReadOnlyList<string> Arguments =>
    [
        "--service-sandbox",
        "--cpu-seconds=" + Profile.CpuSeconds.ToString(CultureInfo.InvariantCulture),
        "--memory-bytes=" + Profile.MemoryBytes.ToString(CultureInfo.InvariantCulture),
        "--open-files=" + Profile.FileLimit.ToString(CultureInfo.InvariantCulture)
    ];

    internal static string AbsolutePath(string path, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        if (!path.StartsWith('/') || path.Contains('\0') || path.Contains('\n') || path.Contains('\r') || path.Contains('\\') ||
            path.Contains("..", StringComparison.Ordinal) || !string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal) ||
            (path.Length > 1 && path.EndsWith('/')))
            throw new ArgumentException("A normalized absolute Unix path is required.", parameterName);
        return path;
    }
}
