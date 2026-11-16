using System.Text.Json.Nodes;
using Braid.Deployment;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

public sealed partial class FerruleDockerRunnerTests
{
    private static string CompiledContainerMetadata => PreviewContainerMetadata
        .Replace("\"Entrypoint\":[\"/ferrule-host\"]", "\"Entrypoint\":[\"/braid-compiled-runtime\"]", StringComparison.Ordinal)
        .Replace("\"PidsLimit\":16", "\"PidsLimit\":64", StringComparison.Ordinal)
        .Replace("{\"Name\":\"nofile\",\"Soft\":64,\"Hard\":64}", "{\"Name\":\"nofile\",\"Soft\":128,\"Hard\":128}", StringComparison.Ordinal);

    [BuildWorkerLinuxFact]
    public async Task Compiled_invocation_profile_retains_network_and_write_isolation_without_compiler_tmpfs()
    {
        var worker = new FakeProcess([7]);
        var launcher = new FakeLauncher { Worker = worker, Container = TextProcess(CompiledContainerMetadata) };
        await using var runner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", ImageId, profile: DockerWorkerProfile.Compiled), launcher);
        Assert.Equal(WorkerProcessFailure.None, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
        var arguments = launcher.Commands.Single(value => Operation(value) == "create").ArgumentList;
        Assert.Contains("/braid-compiled-runtime", arguments);
        Assert.Contains("--read-only", arguments);
        Assert.Contains("none", arguments);
        Assert.DoesNotContain("--tmpfs", arguments);
        Assert.Contains("cpu=30:30", arguments);
        Assert.Contains("nofile=128:128", arguments);
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new byte[CompiledRuntimeProtocol.MaximumRequestBytes + 1], default));
    }

    [LinuxDockerProfileTheory]
    [InlineData("entrypoint")]
    [InlineData("network")]
    [InlineData("write")]
    [InlineData("tmpfs")]
    [InlineData("fileLimit")]
    public async Task A_broadened_compiled_sandbox_is_refused_before_start(string change)
    {
        var metadata = JsonNode.Parse(CompiledContainerMetadata)!;
        if (change == "entrypoint") metadata["Config"]!["Entrypoint"] = new JsonArray("/braid-build-worker");
        if (change == "network") metadata["HostConfig"]!["NetworkMode"] = "host";
        if (change == "write") metadata["HostConfig"]!["ReadonlyRootfs"] = false;
        if (change == "tmpfs") metadata["HostConfig"]!["Tmpfs"] = new JsonObject { ["/tmp"] = DockerWorkerProfile.BuildTemporaryMount };
        if (change == "fileLimit")
        {
            var limit = metadata["HostConfig"]!["Ulimits"]!.AsArray().Single(value => (string?)value!["Name"] == "nofile")!;
            limit["Soft"] = 64; limit["Hard"] = 64;
        }
        var launcher = new FakeLauncher { Container = TextProcess(metadata.ToJsonString()) };
        await using var runner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", ImageId, profile: DockerWorkerProfile.Compiled), launcher);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "start");
    }
}

public sealed class LinuxDockerProfileTheoryAttribute : TheoryAttribute
{
    public LinuxDockerProfileTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Docker profile contracts require canonical Linux paths; executed in Linux qualification.";
    }
}
