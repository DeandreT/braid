using System.Globalization;

namespace Weft.MappingBackends.Workers;

/// <summary>Procfs checks of a self-hardening service child, made before the child receives anything.</summary>
internal static class ChildSandboxVerification
{
    /// <summary>
    /// Confirms no-new-privileges, filter mode, the exact filter count, every fixed limit (soft and hard) and
    /// that the child is non-dumpable, which hides its environment even from its own user.
    /// </summary>
    internal static bool Verified(int processId, int expectedFilters, IEnumerable<(string Name, long Value)> limits)
    {
        try
        {
            var status = File.ReadAllLines($"/proc/{processId}/status");
            if (!status.Contains("NoNewPrivs:\t1", StringComparer.Ordinal) || !status.Contains("Seccomp:\t2", StringComparer.Ordinal) ||
                SeccompFilters(status) != expectedFilters)
                return false;
            var lines = File.ReadAllLines($"/proc/{processId}/limits");
            foreach (var (name, value) in limits)
            {
                var text = value.ToString(CultureInfo.InvariantCulture);
                var line = lines.SingleOrDefault(candidate => candidate.StartsWith(name + "  ", StringComparison.Ordinal));
                var fields = line?[name.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields is not { Length: >= 2 } || fields[0] != text || fields[1] != text) return false;
            }

            try
            {
                _ = File.ReadAllBytes($"/proc/{processId}/environ");
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
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
