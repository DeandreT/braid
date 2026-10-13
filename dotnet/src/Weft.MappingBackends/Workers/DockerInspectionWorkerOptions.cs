namespace Weft.MappingBackends.Workers;

/// <summary>Trusted host configuration, never supplied by a mapping or browser.</summary>
public sealed class DockerInspectionWorkerOptions
{
    public const int MaximumFrameBytes = (8 * 1024 * 1024) + 4;
    public static readonly TimeSpan MaximumWallTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan KillTimeout = TimeSpan.FromMilliseconds(250);

    public DockerInspectionWorkerOptions(
        string dockerExecutable,
        string imageId,
        TimeSpan? wallTimeout = null,
        string dockerSocketPath = "/var/run/docker.sock")
    {
        DockerExecutable = AbsoluteUnixPath(dockerExecutable, nameof(dockerExecutable));
        DockerSocketPath = AbsoluteUnixPath(dockerSocketPath, nameof(dockerSocketPath));
        ArgumentNullException.ThrowIfNull(imageId);
        if (imageId.Length != 71 || !imageId.StartsWith("sha256:", StringComparison.Ordinal)
            || !imageId.AsSpan(7).ContainsOnlyHexadecimal())
        {
            throw new ArgumentException("The worker image must be an immutable sha256 image ID.", nameof(imageId));
        }

        ImageId = imageId.ToLowerInvariant();
        WallTimeout = wallTimeout ?? MaximumWallTimeout;
        if (WallTimeout <= TimeSpan.Zero || WallTimeout > MaximumWallTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(wallTimeout), "The wall deadline must be positive and at most ten seconds.");
        }
    }

    public string DockerExecutable { get; }
    public string DockerSocketPath { get; }
    public string ImageId { get; }
    public TimeSpan WallTimeout { get; }

    private static string AbsoluteUnixPath(string path, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        if (!path.StartsWith('/') || path.Contains('\0') || path.Contains('\n') || path.Contains('\r')
            || path.Contains('\\') || path.Contains("..", StringComparison.Ordinal)
            || !string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal))
        {
            throw new ArgumentException("A normalized absolute Unix path is required.", parameterName);
        }

        return path;
    }
}

internal static class HexadecimalValidation
{
    internal static bool ContainsOnlyHexadecimal(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
