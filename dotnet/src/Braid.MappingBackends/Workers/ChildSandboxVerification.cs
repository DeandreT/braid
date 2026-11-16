using System.Globalization;

namespace Braid.MappingBackends.Workers;

/// <summary>Procfs checks of a self-hardening service child, made before the child receives anything.</summary>
internal static class ChildSandboxVerification
{
    /// <summary>
    /// Confirms no-new-privileges, filter mode, the exact filter count, every fixed limit (soft and hard) and
    /// that the child is non-dumpable, which hides its environment even from its own user.
    /// </summary>
    internal static ChildAdmissionFailure Verify(int processId, int expectedFilters, IEnumerable<(string Name, long Value)> limits,
        bool requireUnprivilegedIdentity = false) =>
        Verify(processId, expectedFilters, limits, File.ReadAllLines, File.ReadAllBytes, requireUnprivilegedIdentity);

    // Per-call read-only seam for deterministic procfs qualification; production always uses the file readers above.
    internal static ChildAdmissionFailure Verify(int processId, int expectedFilters, IEnumerable<(string Name, long Value)> limits,
        Func<string, string[]> readLines, Func<string, byte[]> readBytes, bool requireUnprivilegedIdentity = false)
    {
        var readFailure = ChildAdmissionFailure.StatusRead;
        try
        {
            var status = readLines($"/proc/{processId}/status");
            if (requireUnprivilegedIdentity && !UnprivilegedIdentity(status)) return ChildAdmissionFailure.IdentityVerification;
            var statusFailure = VerifyStatus(status, expectedFilters);
            if (statusFailure != ChildAdmissionFailure.None) return statusFailure;
            readFailure = ChildAdmissionFailure.LimitsRead;
            var lines = readLines($"/proc/{processId}/limits");
            var limitFailure = VerifyLimits(lines, limits);
            if (limitFailure != ChildAdmissionFailure.None) return limitFailure;

            readFailure = ChildAdmissionFailure.EnvironmentRead;
            try
            {
                _ = readBytes($"/proc/{processId}/environ");
                return ChildAdmissionFailure.EnvironmentReadable;
            }
            catch (UnauthorizedAccessException)
            {
                return ChildAdmissionFailure.None;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return readFailure;
        }
    }

    internal static ChildAdmissionFailure VerifyStatus(IEnumerable<string> status, int expectedFilters)
    {
        var lines = status.ToArray();
        if (!lines.Contains("NoNewPrivs:\t1", StringComparer.Ordinal)) return ChildAdmissionFailure.NoNewPrivilegesVerification;
        if (!lines.Contains("Seccomp:\t2", StringComparer.Ordinal)) return ChildAdmissionFailure.SeccompModeVerification;
        return SeccompFilters(lines) == expectedFilters ? ChildAdmissionFailure.None : ChildAdmissionFailure.FilterCountVerification;
    }

    // Stable non-root credentials, no capabilities and no tracer.
    internal static bool UnprivilegedIdentity(IEnumerable<string> status)
    {
        var lines = status.ToArray();
        string? Field(string name) => lines.Where(line => line.StartsWith(name, StringComparison.Ordinal)).ToArray() is [var line] ? line[name.Length..] : null;
        foreach (var name in new[] { "CapInh:", "CapPrm:", "CapEff:", "CapAmb:", "TracerPid:" })
            if (Field(name) is not { } value || !ulong.TryParse(value.AsSpan().Trim(), name == "TracerPid:" ? NumberStyles.None : NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var number) || number != 0)
                return false;
        foreach (var name in new[] { "Uid:", "Gid:" })
            if (Field(name)?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is not { Length: 4 } values ||
                values[0] == "0" || values.Any(value => value != values[0]))
                return false;
        return true;
    }

    internal static ChildAdmissionFailure VerifyLimits(IEnumerable<string> lines, IEnumerable<(string Name, long Value)> limits)
    {
        var entries = lines.ToArray();
        foreach (var (name, value) in limits)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            var matching = entries.Where(candidate => candidate.StartsWith(name + "  ", StringComparison.Ordinal)).ToArray();
            var fields = matching.Length == 1 ? matching[0][name.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries) : null;
            if (fields is not { Length: >= 2 } || fields[0] != text || fields[1] != text) return ChildAdmissionFailure.LimitVerification;
        }
        return ChildAdmissionFailure.None;
    }

    internal static int? SeccompFilters(IEnumerable<string> status)
    {
        const string prefix = "Seccomp_filters:\t";
        var line = status.FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        return line is not null && int.TryParse(line.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count : null;
    }

    /// <summary>The service process's own filters, which every child inherits before adding its own.</summary>
    internal static int InheritedFilters() => SeccompFilters(File.ReadAllLines("/proc/self/status")) ??
        throw new PlatformNotSupportedException("The kernel does not report seccomp filters.");
}
