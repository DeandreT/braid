using Braid.Application;
using Braid.Deployment;

namespace Braid.MappingBackends.Workers;

/// <summary>Fixed container resources selected by trusted host composition, never by a request.</summary>
public sealed class DockerWorkerProfile
{
    private DockerWorkerProfile(string name, long memoryBytes, int cpuSeconds, TimeSpan maximumWallTimeout, int maximumRequestBytes, int maximumOutputBytes)
    {
        Name = name;
        MemoryBytes = memoryBytes;
        CpuSeconds = cpuSeconds;
        MaximumWallTimeout = maximumWallTimeout;
        MaximumRequestBytes = maximumRequestBytes;
        MaximumOutputBytes = maximumOutputBytes;
    }

    /// <summary>Read-only inspection, validation and projection.</summary>
    public static DockerWorkerProfile Inspection { get; } = new("inspection", 268_435_456, 10,
        TimeSpan.FromSeconds(10), DockerInspectionWorkerOptions.MaximumFrameBytes, DockerInspectionWorkerOptions.MaximumFrameBytes);

    /// <summary>Exact-base edit replay; isolation is identical to inspection, with bounded checkpoint frames.</summary>
    public static DockerWorkerProfile Edit { get; } = new("edit", 268_435_456, 10,
        TimeSpan.FromSeconds(10), MappingEditPolicy.MaximumRequestBytes, MappingEditPolicy.MaximumResponseBytes);

    /// <summary>Fresh native construction from a bounded authored plan; no revision or preview.</summary>
    public static DockerWorkerProfile Authoring { get; } = new("authoring", 268_435_456, 10,
        TimeSpan.FromSeconds(10), MappingAuthoringPolicy.MaximumRequestBytes, MappingAuthoringPolicy.MaximumResponseBytes);

    public static DockerWorkerProfile AuthoredPreview { get; } = new("authored-preview", 536_870_912, 30,
        TimeSpan.FromSeconds(30), MappingAuthoredPreviewPolicy.MaximumRequestBytes, MappingAuthoredPreviewPolicy.MaximumResponseBytes);

    /// <summary>Bounded sample preview with explicit input and output documents.</summary>
    public static DockerWorkerProfile Preview { get; } = new("preview", 536_870_912, 30,
        TimeSpan.FromSeconds(30), PreviewPolicy.MaximumRequestBytes, PreviewPolicy.MaximumResponseBytes);

    /// <summary>In-memory MFD packages; inspection retains its smaller unchanged limits.</summary>
    public static DockerWorkerProfile Interchange { get; } = new("interchange", 536_870_912, 30,
        TimeSpan.FromSeconds(30), FerruleInterchangeBackend.MaximumRequestBytes, FerruleInterchangeBackend.MaximumResponseBytes);

    /// <summary>Package-free compilation from trusted native source emission; only a bounded private tmpfs is writable.</summary>
    public static DockerWorkerProfile Build { get; } = new("build", 2_147_483_648, 120,
        TimeSpan.FromSeconds(120), BuildWorkerProtocol.MaximumNativeBytes + 16388, BuildWorkerProtocol.MaximumReplyBytes);
    /// <summary>One in-memory invocation of a verified compiled mapping, without a writable filesystem or network.</summary>
    public static DockerWorkerProfile Compiled { get; } = new("compiled", 536_870_912, 30,
        TimeSpan.FromSeconds(30), CompiledRuntimeProtocol.MaximumRequestBytes, CompiledRuntimeProtocol.MaximumResponseBytes);
    internal static DockerWorkerProfile Pdf { get; } = new("pdf", SpecPdfExtractionProfile.MemoryBytes, SpecPdfExtractionProfile.CpuSeconds,
        TimeSpan.FromSeconds(SpecPdfExtractionProfile.WallSeconds), SpecPdfWorkerResponse.MaximumSourceBytes, SpecPdfWorkerResponse.MaximumResponseBytes);
    public string Entrypoint => this == Build ? "/braid-build-worker" : this == Compiled ? "/braid-compiled-runtime" : "/ferrule-host";
    public int ProcessLimit => this == Build ? 128 : this == Compiled ? 64 : 16;
    public int FileLimit => this == Build ? 256 : this == Compiled ? 128 : 64;
    public const string BuildTemporaryMount = "rw,noexec,nosuid,nodev,size=536870912,uid=65532,gid=65532,mode=0700";

    public string Name { get; }
    public long MemoryBytes { get; }
    public int CpuSeconds { get; }
    public TimeSpan MaximumWallTimeout { get; }
    public int MaximumRequestBytes { get; }
    public int MaximumOutputBytes { get; }
}
