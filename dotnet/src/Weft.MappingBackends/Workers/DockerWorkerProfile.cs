using Weft.Application;

namespace Weft.MappingBackends.Workers;

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

    /// <summary>Bounded sample preview with explicit input and output documents.</summary>
    public static DockerWorkerProfile Preview { get; } = new("preview", 536_870_912, 30,
        TimeSpan.FromSeconds(30), PreviewPolicy.MaximumRequestBytes, PreviewPolicy.MaximumResponseBytes);

    public string Name { get; }
    public long MemoryBytes { get; }
    public int CpuSeconds { get; }
    public TimeSpan MaximumWallTimeout { get; }
    public int MaximumRequestBytes { get; }
    public int MaximumOutputBytes { get; }
}
