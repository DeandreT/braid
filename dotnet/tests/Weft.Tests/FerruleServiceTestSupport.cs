using System.Globalization;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Tests that start real Ferrule child processes run one at a time so child observations stay attributable.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FerruleServiceProcessCollection
{
    public const string Name = "Ferrule service processes";
}

/// <summary>Runs with a ferrule-host executable built from this source (<c>WEFT_FERRULE_HOST_EXECUTABLE</c>).</summary>
public sealed class ServiceWorkerFactAttribute : FactAttribute
{
    public ServiceWorkerFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(FerruleServiceTestSupport.Executable))
            Skip = "Build engines/ferrule-host and set WEFT_FERRULE_HOST_EXECUTABLE for live service-child qualification.";
    }
}

/// <summary>Compares the service child with the Docker runner; both settings must name the same worker build.</summary>
public sealed class ServiceEquivalenceFactAttribute : FactAttribute
{
    public ServiceEquivalenceFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(FerruleServiceTestSupport.Executable) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")))
            Skip = "Set WEFT_FERRULE_HOST_EXECUTABLE and WEFT_FERRULE_WORKER_IMAGE to the same worker build for equivalence qualification.";
    }
}

internal static class FerruleServiceTestSupport
{
    internal static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));

    internal static readonly byte[] Order =
        "{\"id\":\"SO-1\",\"lines\":[{\"sku\":\"A\",\"qty\":2},{\"sku\":\"B\",\"qty\":9007199254740993}]}"u8.ToArray();

    internal static readonly byte[] Rates = "{\"currency\":\"EUR\"}"u8.ToArray();

    internal static string? Executable => Environment.GetEnvironmentVariable("WEFT_FERRULE_HOST_EXECUTABLE");

    internal static IEnumerable<string> Fixtures() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures"), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)!;

    internal static NativeArtifact Artifact(string fixture) => NativeArtifact.Admit(Scope,
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", fixture + ".json")), InspectionPolicy.MaximumNativeBytes);

    internal static MappingPreviewRequest Preview(string fixture = "preview-orders", byte[]? primary = null) => new(
        Artifact(fixture),
        PreviewTargetSelection.All,
        new PreviewInputDocument(null, primary ?? Order),
        [new PreviewInputDocument("rates", Rates)],
        [new("region", PreviewParameterKind.String, "west"), new("express", PreviewParameterKind.Boolean, "true")],
        "2026-10-20T01:02:03.5+02:00",
        PreviewLimits.Default);

    /// <summary>A private temporary root that the runner must leave empty.</summary>
    internal static string TemporaryRoot()
    {
        var root = Directory.CreateTempSubdirectory("weft-service-test-").FullName;
        return Path.TrimEndingDirectorySeparator(root);
    }

    internal static ProcessWorkerOptions Options(string root, DockerWorkerProfile? profile = null, TimeSpan? deadline = null, bool withhold = false) =>
        new(Executable!, profile ?? DockerWorkerProfile.Inspection, root, deadline) { WithholdEndOfInput = withhold };

    /// <summary>Live worker children of this test process, identified by parent and the worker's command name.</summary>
    internal static int[] WorkerChildren()
    {
        var self = Environment.ProcessId;
        // The kernel keeps the first 15 bytes of the executable name as the command name.
        var name = Path.GetFileName(Executable ?? "weft-ferrule-host");
        var expected = name[..Math.Min(15, name.Length)];
        var children = new List<int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            string stat;
            try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            // pid (comm) state ppid ...; comm may contain spaces, so parse after its closing parenthesis.
            var open = stat.IndexOf('(', StringComparison.Ordinal);
            var close = stat.LastIndexOf(')');
            if (open < 0 || close < open) continue;
            var command = stat[(open + 1)..close];
            var fields = stat[(close + 2)..].Split(' ');
            if (fields.Length > 1 && fields[1] == self.ToString(CultureInfo.InvariantCulture) && command == expected &&
                fields[0] != "Z")
                children.Add(pid);
        }

        return [.. children];
    }

    internal static async Task<int[]> WaitForWorkerChildren(int count, bool sandboxReady = false)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var children = WorkerChildren();
            if (sandboxReady)
                children = children.Where(pid =>
                {
                    try
                    {
                        var status = File.ReadAllLines($"/proc/{pid}/status");
                        return status.Contains("Seccomp:\t2", StringComparer.Ordinal) &&
                            status.Contains("NoNewPrivs:\t1", StringComparer.Ordinal);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
                }).ToArray();
            if (children.Length >= count) return children;
            await Task.Delay(25);
        }

        throw new TimeoutException("The expected worker children did not start.");
    }
}
