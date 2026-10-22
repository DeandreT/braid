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

/// <summary>The packaged Ferrule service host as a separate process with only its explicit environment.</summary>
internal sealed class PackagedFerruleService : IAsyncDisposable
{
    private readonly System.Diagnostics.Process process;

    private PackagedFerruleService(System.Diagnostics.Process process, int port, int healthPort)
    {
        this.process = process;
        Port = port;
        HealthPort = healthPort;
    }

    internal static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "FerruleService", "weft-ferrule-service.dll");
    internal int Port { get; }
    internal int HealthPort { get; }
    internal System.Diagnostics.Process Process => process;

    /// <summary>The build identity the service reports for the configured worker executable.</summary>
    internal static string Identity => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(FerruleServiceTestSupport.Executable!)));

    internal static System.Diagnostics.Process Launch(IReadOnlyDictionary<string, string> environment)
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "/usr/share/dotnet";
        var start = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(dotnetRoot, "dotnet"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path);
        start.Environment.Clear();
        start.Environment["DOTNET_ROOT"] = dotnetRoot;
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        start.Environment["HOME"] = System.IO.Path.GetTempPath();
        foreach (var (name, value) in environment) start.Environment[name] = value;
        return System.Diagnostics.Process.Start(start)!;
    }

    /// <summary>Starts the service with a PEM bundle, the admitted client pins and a private temporary root.</summary>
    internal static async Task<PackagedFerruleService> StartAsync(string serverPem, IEnumerable<string> clientPins, string temporaryRoot, int concurrency = 2)
    {
        var port = FreePort();
        var healthPort = FreePort();
        var process = Launch(new Dictionary<string, string>
        {
            ["WEFT_FERRULE_SERVICE_LISTEN_ADDR"] = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
            ["WEFT_FERRULE_SERVICE_HEALTH_ADDR"] = "127.0.0.1:" + healthPort.ToString(CultureInfo.InvariantCulture),
            ["WEFT_FERRULE_SERVICE_PEM"] = serverPem,
            ["WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256"] = string.Join(',', clientPins),
            ["WEFT_FERRULE_SERVICE_WORKER_EXECUTABLE"] = FerruleServiceTestSupport.Executable!,
            ["WEFT_FERRULE_SERVICE_TEMPORARY_ROOT"] = temporaryRoot,
            ["WEFT_FERRULE_SERVICE_CONCURRENCY"] = concurrency.ToString(CultureInfo.InvariantCulture)
        });
        var service = new PackagedFerruleService(process, port, healthPort);
        try
        {
            await service.WaitForReadinessAsync();
            return service;
        }
        catch
        {
            await service.DisposeAsync();
            throw;
        }
    }

    internal async Task WaitForReadinessAsync()
    {
        using var probe = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + HealthPort.ToString(CultureInfo.InvariantCulture)), Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (process.HasExited)
                throw new InvalidOperationException("The packaged Ferrule service exited: " + await process.StandardError.ReadToEndAsync());
            try
            {
                using var ready = await probe.GetAsync(Weft.MappingBackends.Workers.FerruleServiceProtocol.ReadinessPath);
                if (ready.StatusCode == System.Net.HttpStatusCode.NoContent) return;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(100);
        }

        throw new TimeoutException("The packaged Ferrule service did not become ready.");
    }

    /// <summary>Graceful stop through SIGTERM; returns the exit code.</summary>
    internal async Task<int> StopAsync()
    {
        if (!process.HasExited)
        {
            using var terminate = System.Diagnostics.Process.Start("/usr/bin/kill", ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)]);
            await terminate.WaitForExitAsync();
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return process.ExitCode;
    }

    internal static string Bundle(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        using var key = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPrivateKey(certificate)!;
        return certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n";
    }

    internal static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill();
            await process.WaitForExitAsync();
        }

        process.Dispose();
    }
}
