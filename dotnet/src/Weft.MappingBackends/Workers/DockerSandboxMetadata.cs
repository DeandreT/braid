using System.Text.Json;

namespace Weft.MappingBackends.Workers;

internal static class DockerSandboxMetadata
{
    private const string DefaultPath = "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    internal static bool IsSupportedDaemon(byte[] bytes) => Read(bytes, root =>
        Text(root, "OSType", "linux")
        && Text(root, "CgroupVersion", "2")
        && Enabled(root, "MemoryLimit")
        && Enabled(root, "SwapLimit")
        && Enabled(root, "CpuCfsPeriod")
        && Enabled(root, "CpuCfsQuota")
        && Enabled(root, "PidsLimit")
        && root.TryGetProperty("SecurityOptions", out var security)
        && security.ValueKind == JsonValueKind.Array
        && security.EnumerateArray().Any(option => option.ValueKind == JsonValueKind.String
            && option.GetString()!.StartsWith("name=seccomp,profile=", StringComparison.Ordinal)
            && !option.GetString()!.Contains("unconfined", StringComparison.Ordinal)));

    internal static bool IsSafeImage(byte[] bytes, string imageId) => Read(bytes, root =>
        Text(root, "Id", imageId)
        && Text(root, "Os", "linux")
        && root.TryGetProperty("Config", out var config)
        && SafeImageEnvironment(config)
        && Empty(config, "Volumes", JsonValueKind.Object)
        && Empty(config, "Cmd"));

    internal static bool IsIsolatedContainer(byte[] bytes, string imageId, DockerWorkerProfile profile) => Read(bytes, root =>
    {
        if (!Text(root, "Image", imageId) || !root.TryGetProperty("Mounts", out _) || !Empty(root, "Mounts")
            || !root.TryGetProperty("HostConfig", out var host)
            || !root.TryGetProperty("Config", out var config))
        {
            return false;
        }

        return Text(config, "User", "65532:65532")
            && Text(config, "WorkingDir", "/")
            && ArrayEquals(config, "Entrypoint", [profile.Entrypoint])
            && Empty(config, "Cmd")
            && Enabled(config, "OpenStdin")
            && Disabled(config, "Tty")
            && SafeEnvironment(config)
            && Enabled(host, "ReadonlyRootfs")
            && Disabled(host, "Privileged")
            && Text(host, "NetworkMode", "none")
            && Text(host, "IpcMode", "none")
            && Text(host, "CgroupnsMode", "private")
            && Text(host, "PidMode", "")
            && Text(host, "UTSMode", "")
            && Integer(host, "NanoCpus", 1_000_000_000)
            && Integer(host, "Memory", profile.MemoryBytes)
            && Integer(host, "MemorySwap", profile.MemoryBytes)
            && Integer(host, "PidsLimit", profile.ProcessLimit)
            && ArrayEquals(host, "CapDrop", ["ALL"])
            && Empty(host, "CapAdd")
            && ArrayEquals(host, "SecurityOpt", ["no-new-privileges=true"])
            && Empty(host, "Binds")
            && Empty(host, "VolumesFrom")
            && Empty(host, "Devices")
            && Empty(host, "DeviceRequests")
            && Empty(host, "PortBindings", JsonValueKind.Object)
            && host.TryGetProperty("RestartPolicy", out var restart)
            && Text(restart, "Name", "no")
            && host.TryGetProperty("LogConfig", out var log)
            && Text(log, "Type", "none")
            && TemporaryMount(host, profile)
            && Limits(host, profile.CpuSeconds, profile.FileLimit);
    });

    private static bool SafeEnvironment(JsonElement config)
    {
        if (!config.TryGetProperty("Env", out var environment) || environment.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (environment.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var hostname = config.TryGetProperty("Hostname", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return environment.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String
            && (item.GetString() == DefaultPath
                || (hostname is not null && item.GetString() == $"HOSTNAME={hostname}")));
    }

    private static bool SafeImageEnvironment(JsonElement config)
    {
        if (!config.TryGetProperty("Env", out var environment) || environment.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return environment.ValueKind == JsonValueKind.Array && environment.GetArrayLength() <= 1
            && environment.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString() == DefaultPath);
    }

    private static bool TemporaryMount(JsonElement host, DockerWorkerProfile profile) => profile == DockerWorkerProfile.Build
        ? host.TryGetProperty("Tmpfs", out var mounts) && mounts.ValueKind == JsonValueKind.Object && mounts.EnumerateObject().Count() == 1
            && Text(mounts, "/tmp", DockerWorkerProfile.BuildTemporaryMount)
        : Empty(host, "Tmpfs", JsonValueKind.Object);

    private static bool Limits(JsonElement host, int cpuSeconds, int fileLimit)
    {
        if (!host.TryGetProperty("Ulimits", out var limits) || limits.ValueKind != JsonValueKind.Array || limits.GetArrayLength() != 3)
        {
            return false;
        }

        return HasLimit(limits, "nofile", fileLimit) && HasLimit(limits, "cpu", cpuSeconds) && HasLimit(limits, "core", 0);
    }

    private static bool HasLimit(JsonElement limits, string name, long value) =>
        limits.EnumerateArray().Any(item => Text(item, "Name", name) && Integer(item, "Soft", value) && Integer(item, "Hard", value));

    private static bool Read(byte[] bytes, Func<JsonElement, bool> validate)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.ValueKind == JsonValueKind.Object && validate(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Text(JsonElement parent, string name, string value) =>
        parent.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == value;

    private static bool Integer(JsonElement parent, string name, long value) =>
        parent.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var actual) && actual == value;

    private static bool Enabled(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static bool Disabled(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.False;

    private static bool Empty(JsonElement parent, string name, JsonValueKind expectedKind = JsonValueKind.Array) =>
        !parent.TryGetProperty(name, out var property)
        || (property.ValueKind == JsonValueKind.Null
            || (property.ValueKind == expectedKind
                && (expectedKind == JsonValueKind.Array ? property.GetArrayLength() == 0 : !property.EnumerateObject().Any())));

    private static bool ArrayEquals(JsonElement parent, string name, string[] expected) =>
        parent.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Array
        && property.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
        && property.EnumerateArray().Select(item => item.GetString()).SequenceEqual(expected);
}
