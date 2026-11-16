using System.Globalization;

namespace Braid.MappingBackends.Workers;

/// <summary>Trusted host configuration, never supplied by a mapping or browser.</summary>
public sealed class DockerInspectionWorkerOptions
{
    public const int MaximumFrameBytes = (8 * 1024 * 1024) + 4;
    public static readonly TimeSpan MaximumWallTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan KillTimeout = TimeSpan.FromMilliseconds(250);

    public DockerInspectionWorkerOptions(
        string dockerExecutable,
        string imageId,
        TimeSpan? wallTimeout = null,
        string dockerSocketPath = "/var/run/docker.sock",
        DockerWorkerProfile? profile = null,
        DockerRunnerBudgets? budgets = null)
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
        Profile = profile ?? DockerWorkerProfile.Inspection;
        WallTimeout = wallTimeout ?? Profile.MaximumWallTimeout;
        if (WallTimeout <= TimeSpan.Zero || WallTimeout > Profile.MaximumWallTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(wallTimeout), "The wall deadline must be positive and within the selected profile bound.");
        }

        Budgets = budgets ?? new();
        if (Budgets.Create is { } create && (create <= TimeSpan.Zero || create > DockerRunnerBudgets.Maximum) ||
            Budgets.Cleanup is { } cleanup && (cleanup <= TimeSpan.Zero || cleanup > DockerRunnerBudgets.Maximum))
        {
            throw new ArgumentOutOfRangeException(nameof(budgets), "Docker CLI budgets must be positive and within their fixed bound.");
        }
    }

    public string DockerExecutable { get; }
    public string DockerSocketPath { get; }
    public string ImageId { get; }
    public TimeSpan WallTimeout { get; }
    public DockerWorkerProfile Profile { get; }
    public DockerRunnerBudgets Budgets { get; }

    /// <summary>Null: creation settles within the operation's wall budget.</summary>
    public TimeSpan? CreateTimeout => Budgets.Create;

    /// <summary>One bound shared by ownership inspection and removal, per cleanup or reaper attempt.</summary>
    public TimeSpan CleanupTimeout => Budgets.Cleanup ?? DefaultCleanupTimeout;

    internal TimeSpan ReaperInitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan ReaperMaximumDelay { get; init; } = TimeSpan.FromSeconds(30);

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

/// <summary>Host-configured Docker CLI budgets for busy local daemons; unset values keep the defaults.</summary>
public sealed record DockerRunnerBudgets(TimeSpan? Create = null, TimeSpan? Cleanup = null)
{
    public const string CreateSetting = "BRAID_FERRULE_DOCKER_CREATE_TIMEOUT_SECONDS";
    public const string CleanupSetting = "BRAID_FERRULE_DOCKER_CLEANUP_TIMEOUT_SECONDS";
    public static readonly TimeSpan Maximum = TimeSpan.FromSeconds(120);

    public static DockerRunnerBudgets Read(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new(Seconds(read(CreateSetting)), Seconds(read(CleanupSetting)));
    }

    private static TimeSpan? Seconds(string? value) => value is null ? null
        : value.Length is >= 1 and <= 3 && value[0] != '0' &&
          int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= Maximum.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidOperationException("The Docker runner budget settings are invalid.");
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
