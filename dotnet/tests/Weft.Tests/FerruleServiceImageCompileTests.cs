using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Weft.Deployment;
using Weft.MappingBackends;
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

/// <summary>Runs the packaged service image itself.</summary>
public sealed class ServiceImageFactAttribute : FactAttribute
{
    public ServiceImageFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEFT_FERRULE_SERVICE_IMAGE")))
            Skip = "Set WEFT_FERRULE_SERVICE_IMAGE to a built service image.";
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
            var identity = await NativeIdentityAsync(image);
            var endpoint = await StartCompileReplicaAsync(image, name);
            using var metadata = new HttpClient { BaseAddress = endpoint };
            var toolchain = await AdvertisedToolchainAsync(metadata);
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

    /// <summary>
    /// Execution enabled in the image as a platform runs it: startup probes the installed <c>/runtime</c> host in the
    /// launcher before readiness, and bytes the image's own compile profile produced run in a fresh child.
    /// </summary>
    [ServiceImageFact]
    public async Task ServiceImageExecutesCompiledBytesInAFreshRuntimeChild()
    {
        var image = Environment.GetEnvironmentVariable("WEFT_FERRULE_SERVICE_IMAGE")!;
        var name = "weft-compiled-execution-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var identity = await NativeIdentityAsync(image);
            await DockerAsync("run", "--detach", "--name", name, "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
                "--user", "65532:65532", "--tmpfs", "/tmp:size=1g,mode=1777", "--publish", "127.0.0.1::8080",
                "--env", "WEFT_FERRULE_SERVICE_AUTHENTICATION=platformSandbox", "--env", "WEFT_FERRULE_SERVICE_LISTEN_ADDR=0.0.0.0:8080",
                "--env", "WEFT_FERRULE_SERVICE_CONCURRENCY=1", "--env", "WEFT_FERRULE_SERVICE_QUEUE=0",
                "--env", "WEFT_FERRULE_SERVICE_COMPILE_ENABLED=true", "--env", "WEFT_FERRULE_SERVICE_EXECUTION_ENABLED=true", image);
            var port = int.Parse((await DockerAsync("port", name, "8080/tcp")).Split('\n')[0].Split(':')[^1], CultureInfo.InvariantCulture);
            var endpoint = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            await WaitForReadinessAsync(endpoint, name);

            using var metadata = new HttpClient { BaseAddress = endpoint };
            using var capabilities = JsonDocument.Parse(await metadata.GetStringAsync(FerruleServiceProtocol.CapabilitiesPath));
            var profiles = capabilities.RootElement.GetProperty("profiles").EnumerateArray().ToArray();
            Assert.Empty(capabilities.RootElement.GetProperty("reserved").EnumerateArray());
            var toolchain = profiles.Single(profile => profile.GetProperty("name").GetString() == FerruleServiceProtocol.CompileProfile)
                .GetProperty("toolchain").GetProperty("identity").GetString()!;
            var execution = profiles.Single(profile => profile.GetProperty("name").GetString() == FerruleServiceProtocol.CompiledProfile).GetProperty("runtime");
            var runtimeIdentity = execution.GetProperty("identity").GetString()!;
            Assert.Equal(BuildWorkerProtocol.RuntimeVersion, execution.GetProperty("version").GetString());
            Assert.NotEqual(identity, runtimeIdentity);

            await using var compile = new FerruleServiceRunner(new(new LoopbackConnector(endpoint), FerruleServiceProtocol.CompileProfile, identity, toolchain));
            var (compiler, intent, native) = FerruleServiceCompileTests.Compiler(compile, "valid-basic");
            var capability = await compiler.InspectAsync(intent);
            var bytes = await compiler.BuildAsync(intent, new(native, native.Length));
            var artifact = BuildArtifactReference.Create(intent, capability, bytes.Digest, bytes.Length);
            await using var execute = new FerruleServiceRunner(new(new LoopbackConnector(endpoint), FerruleServiceProtocol.CompiledProfile, identity,
                compiledRuntimeIdentity: runtimeIdentity));
            var runtime = new FerruleCompiledBuildRuntime(execute, compiler.Backend);
            var replay = new Weft.Application.Regression.RegressionReplay(Weft.Application.PreviewTargetSelection.All, "2026-10-22T00:00:00Z",
                Weft.Application.PreviewLimits.Default, new(null, "{\"code\":\"A1\",\"a.b\":\"dot\",\"nested\":{\"b\":\"x\"}}"u8), [], []);
            var result = await runtime.ExecuteAsync(artifact, bytes, replay);
            Assert.Equal(CompiledBuildOutcome.Completed, result.Outcome);
            Assert.Equal("{\n  \"code\": \"A1\",\n  \"dotted\": \"dot\",\n  \"nestedOutput\": {\n    \"b\": \"x\"\n  }\n}\n"u8.ToArray(),
                Assert.Single(result.Outputs!.Outputs).CopyBytes());
            Assert.Equal(WorkerProcessFailure.WorkerFailed, (await execute.RunAsync(new byte[] { 0 }, CancellationToken.None)).Failure);
            Assert.Equal(CompiledBuildOutcome.Completed, (await runtime.ExecuteAsync(artifact, bytes, replay)).Outcome);
            using (var ready = await metadata.GetAsync(FerruleServiceProtocol.ReadinessPath))
                Assert.Equal(HttpStatusCode.NoContent, ready.StatusCode);
            await execute.StopAsync();
            await compile.StopAsync();
        }
        finally
        {
            await DockerAsync("rm", "--force", name).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The image's <c>identity</c> command, run as the acceptance harness and an operator run it before starting a
    /// stack, measures the same toolchain that a compile-enabled replica of that image advertises and admits.
    /// </summary>
    [ServiceImageFact]
    public async Task TheIdentityCommandMeasuresTheToolchainAReplicaOfTheSameImageAdvertises()
    {
        var image = Environment.GetEnvironmentVariable("WEFT_FERRULE_SERVICE_IMAGE")!;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var name = "weft-compile-replica-" + suffix;
        var measurement = "weft-compile-identity-" + suffix;
        try
        {
            var native = await NativeIdentityAsync(image);
            using var measured = JsonDocument.Parse(await DockerAsync("run", "--rm", "--name", measurement, "--network", "none", "--read-only",
                "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:size=256m,mode=1777",
                "--memory", "3g", "--pids-limit", "256", image, "identity"));
            var endpoint = await StartCompileReplicaAsync(image, name);
            using var metadata = new HttpClient { BaseAddress = endpoint };
            using var ready = await metadata.GetAsync(FerruleServiceProtocol.ReadyPath);
            Assert.Equal(HttpStatusCode.NoContent, ready.StatusCode);
            var advertised = Assert.Single(ready.Headers.GetValues(FerruleServiceProtocol.CompileToolchainHeader));
            Assert.Equal(native, Assert.Single(ready.Headers.GetValues(InspectionTransport.ImageHeader)));
            Assert.Equal(advertised, await AdvertisedToolchainAsync(metadata));

            Assert.Equal(4, measured.RootElement.EnumerateObject().Count());
            Assert.Equal("weft.compiler-image-identity.v1", measured.RootElement.GetProperty("schema").GetString());
            Assert.Equal(native, measured.RootElement.GetProperty("native").GetString());
            Assert.Equal(advertised, measured.RootElement.GetProperty("toolchain").GetString());
            Assert.Equal(CompileWorkerIdentity.Combine(native, advertised), measured.RootElement.GetProperty("build").GetString());
        }
        finally
        {
            await DockerAsync("rm", "--force", name, measurement).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private static async Task<string> NativeIdentityAsync(string image) =>
        "sha256:" + await DockerAsync("image", "inspect", "--format", "{{index .Config.Labels \"io.weft.ferrule.worker-sha256\"}}", image);

    /// <summary>One compile-enabled replica in platform-sandbox mode, published on a loopback port once ready.</summary>
    private static async Task<Uri> StartCompileReplicaAsync(string image, string name)
    {
        await DockerAsync("run", "--detach", "--name", name, "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
            "--user", "65532:65532", "--tmpfs", "/tmp:size=1g,mode=1777", "--publish", "127.0.0.1::8080",
            "--env", "WEFT_FERRULE_SERVICE_AUTHENTICATION=platformSandbox", "--env", "WEFT_FERRULE_SERVICE_LISTEN_ADDR=0.0.0.0:8080",
            "--env", "WEFT_FERRULE_SERVICE_CONCURRENCY=1", "--env", "WEFT_FERRULE_SERVICE_QUEUE=0",
            "--env", "WEFT_FERRULE_SERVICE_COMPILE_ENABLED=true", image);
        var port = int.Parse((await DockerAsync("port", name, "8080/tcp")).Split('\n')[0].Split(':')[^1], CultureInfo.InvariantCulture);
        var endpoint = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
        await WaitForReadinessAsync(endpoint, name);
        return endpoint;
    }

    private static async Task<string> AdvertisedToolchainAsync(HttpClient metadata)
    {
        using var capabilities = JsonDocument.Parse(await metadata.GetStringAsync(FerruleServiceProtocol.CapabilitiesPath));
        return capabilities.RootElement.GetProperty("profiles").EnumerateArray()
            .Single(profile => profile.GetProperty("name").GetString() == FerruleServiceProtocol.CompileProfile)
            .GetProperty("toolchain").GetProperty("identity").GetString()!;
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
