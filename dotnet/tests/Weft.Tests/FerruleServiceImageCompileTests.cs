using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Weft.Deployment;
using Weft.MappingBackends.Workers;
using static Weft.Tests.FerruleServiceTestSupport;

namespace Weft.Tests;

/// <summary>Compares the packaged service image's compile profile with the Docker compiler image built from the same worker.</summary>
public sealed class ServiceImageCompileFactAttribute : FactAttribute
{
    public ServiceImageCompileFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEFT_FERRULE_SERVICE_IMAGE")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEFT_BUILD_WORKER_IMAGE")))
            Skip = "Set WEFT_FERRULE_SERVICE_IMAGE and WEFT_BUILD_WORKER_IMAGE, built from the same worker, for image compile equivalence.";
    }
}

/// <summary>
/// The service image exactly as a platform runs it (read-only root, no capabilities, no-new-privileges, non-root, a
/// memory-backed <c>/tmp</c>) in platform-sandbox mode with compilation enabled. Its startup proves the compile
/// sandbox under the container runtime's own seccomp profile; every compile and inspection reply must equal the
/// Docker compiler's byte for byte.
/// </summary>
[Collection(FerruleServiceProcessCollection.Name)]
public sealed class FerruleServiceImageCompileTests
{
    [ServiceImageCompileFact]
    public async Task ServiceImageRepliesEqualTheDockerCompilerByteForByte()
    {
        var image = Environment.GetEnvironmentVariable("WEFT_FERRULE_SERVICE_IMAGE")!;
        var name = "weft-compile-equivalence-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var identity = "sha256:" + await DockerAsync("image", "inspect", "--format", "{{index .Config.Labels \"io.weft.ferrule.worker-sha256\"}}", image);
            await DockerAsync("run", "--detach", "--name", name, "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
                "--user", "65532:65532", "--tmpfs", "/tmp:size=1g,mode=1777", "--publish", "127.0.0.1::8080",
                "--env", "WEFT_FERRULE_SERVICE_AUTHENTICATION=platformSandbox", "--env", "WEFT_FERRULE_SERVICE_LISTEN_ADDR=0.0.0.0:8080",
                "--env", "WEFT_FERRULE_SERVICE_CONCURRENCY=1", "--env", "WEFT_FERRULE_SERVICE_QUEUE=0",
                "--env", "WEFT_FERRULE_SERVICE_COMPILE_ENABLED=true", image);
            var port = int.Parse((await DockerAsync("port", name, "8080/tcp")).Split('\n')[0].Split(':')[^1], CultureInfo.InvariantCulture);
            var endpoint = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            await WaitForReadinessAsync(endpoint, name);

            using var metadata = new HttpClient { BaseAddress = endpoint };
            using var capabilities = JsonDocument.Parse(await metadata.GetStringAsync(FerruleServiceProtocol.CapabilitiesPath));
            var toolchain = capabilities.RootElement.GetProperty("profiles").EnumerateArray()
                .Single(profile => profile.GetProperty("name").GetString() == FerruleServiceProtocol.CompileProfile)
                .GetProperty("toolchain").GetProperty("identity").GetString()!;
            await using var service = new FerruleServiceRunner(new(new LoopbackConnector(endpoint), FerruleServiceProtocol.CompileProfile, identity, toolchain));
            var compiled = 0;
            foreach (var fixture in Fixtures())
            {
                foreach (var operation in new[] { "compile", "inspect" })
                {
                    var frame = BuildFrame(fixture, operation);
                    var expected = await FerruleServiceCompileTests.DockerCompilerAsync(frame);
                    var actual = await service.RunAsync(frame, CancellationToken.None);
                    Assert.Equal(WorkerProcessFailure.None, expected.Failure);
                    Assert.Equal(expected.Failure, actual.Failure);
                    Assert.Equal(expected.Output, actual.Output);
                    var reply = BuildWorkerProtocol.DecodeReply(actual.Output, BuildWorkerProtocol.DecodeRequest(frame).Header);
                    if (reply.Header.Files is [{ Path: BuildCompilation.Output }]) compiled++;
                }
            }

            Assert.True(compiled >= 2, "At least two fixtures must compile to artifacts.");
            await service.StopAsync();
        }
        finally
        {
            await DockerAsync("rm", "--force", name).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private static async Task WaitForReadinessAsync(Uri endpoint, string name)
    {
        using var probe = new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 300; attempt++)
        {
            try
            {
                using var ready = await probe.GetAsync(FerruleServiceProtocol.ReadinessPath);
                if (ready.StatusCode == HttpStatusCode.NoContent) return;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
            if (await DockerAsync("inspect", "--format", "{{.State.Running}}", name) != "true")
                throw new InvalidOperationException("The service container stopped: " + await DockerAsync("logs", name) + await DockerErrorAsync("logs", name));
            await Task.Delay(200);
        }

        throw new TimeoutException("The service container did not become ready.");
    }

    /// <summary>The standard error of a Docker command, where it reports a container's own standard error.</summary>
    private static async Task<string> DockerErrorAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        return (await error).Trim();
    }

    private static async Task<string> DockerAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("docker " + arguments[0] + " failed: " + await error);
        return (await output).Trim();
    }

    /// <summary>Plain HTTP to one loopback service instance, standing in for a platform sandbox's own front end.</summary>
    private sealed class LoopbackConnector(Uri endpoint) : IFerruleServiceConnector
    {
        public IFerruleServiceChannel Open() => new Channel(endpoint, new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 })
        {
            Timeout = Timeout.InfiniteTimeSpan
        });

        private sealed class Channel(Uri endpoint, HttpClient client) : IFerruleServiceChannel
        {
            public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.RequestUri = new Uri(endpoint, request.RequestUri!);
                return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                client.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
