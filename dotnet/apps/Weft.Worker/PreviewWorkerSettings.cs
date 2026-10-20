using Weft.Application;

namespace Weft.Worker;

/// <summary>Explicit environment configuration; nothing is inferred from a request or mapping.</summary>
internal sealed record PreviewWorkerSettings(
    string WriterConnectionString,
    string StorageRoot,
    string WorkerImage,
    string DockerExecutable,
    TimeSpan Lease)
{
    internal static PreviewWorkerSettings Read(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string Required(string name) => environment(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"The preview worker requires {name}.");
        return new(Required("WEFT_PREVIEW_WRITER_CONNECTION_STRING"),
            Required("WEFT_PRIVATE_STORAGE_ROOT"), Required("WEFT_FERRULE_WORKER_IMAGE"),
            environment("WEFT_DOCKER_EXECUTABLE") is { Length: > 0 } docker ? docker : "/usr/bin/docker",
            environment("WEFT_PREVIEW_LEASE_SECONDS") is { Length: > 0 } lease
                ? TimeSpan.FromSeconds(int.Parse(lease, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture))
                : new PreviewRunOptions().Lease);
    }
}
