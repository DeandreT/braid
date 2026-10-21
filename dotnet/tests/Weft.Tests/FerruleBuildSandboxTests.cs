using System.Text;
using System.Text.Json.Nodes;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed partial class FerruleDockerRunnerTests
{
    private static JsonNode BuildMetadata()
    {
        var value = JsonNode.Parse(ContainerMetadata)!;
        value["Config"]!["Entrypoint"] = new JsonArray("/weft-build-worker");
        var host = value["HostConfig"]!;
        host["Memory"] = DockerWorkerProfile.Build.MemoryBytes; host["MemorySwap"] = DockerWorkerProfile.Build.MemoryBytes;
        host["PidsLimit"] = 128;
        host["Tmpfs"] = new JsonObject { ["/tmp"] = DockerWorkerProfile.BuildTemporaryMount };
        foreach (var limit in host["Ulimits"]!.AsArray())
        {
            if (limit!["Name"]!.GetValue<string>() == "nofile") { limit["Soft"] = 256; limit["Hard"] = 256; }
            if (limit["Name"]!.GetValue<string>() == "cpu") { limit["Soft"] = 120; limit["Hard"] = 120; }
        }
        return value;
    }

    [Fact]
    public void Compiler_profile_accepts_only_the_fixed_private_temporary_mount_and_resource_limits()
    {
        var value = BuildMetadata();
        Assert.True(DockerSandboxMetadata.IsIsolatedContainer(Encoding.UTF8.GetBytes(value.ToJsonString()), ImageId, DockerWorkerProfile.Build));
        Assert.False(DockerSandboxMetadata.IsIsolatedContainer(Encoding.UTF8.GetBytes(value.ToJsonString()), ImageId, DockerWorkerProfile.Inspection));
        value["HostConfig"]!["Tmpfs"]!["/tmp"] = "rw,exec,size=536870912";
        Assert.False(DockerSandboxMetadata.IsIsolatedContainer(Encoding.UTF8.GetBytes(value.ToJsonString()), ImageId, DockerWorkerProfile.Build));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("mount")]
    [InlineData("environment")]
    [InlineData("entrypoint")]
    [InlineData("pids")]
    public void Compiler_profile_refuses_expanded_sandbox_boundaries(string mutation)
    {
        var value = BuildMetadata();
        switch (mutation)
        {
            case "network": value["HostConfig"]!["NetworkMode"] = "bridge"; break;
            case "mount": value["HostConfig"]!["Tmpfs"]!["/extra"] = "rw"; break;
            case "environment": value["Config"]!["Env"]!.AsArray().Add("MSBuildSDKsPath=/untrusted"); break;
            case "entrypoint": value["Config"]!["Entrypoint"] = new JsonArray("/bin/sh"); break;
            default: value["HostConfig"]!["PidsLimit"] = 256; break;
        }
        Assert.False(DockerSandboxMetadata.IsIsolatedContainer(Encoding.UTF8.GetBytes(value.ToJsonString()), ImageId, DockerWorkerProfile.Build));
    }
}
