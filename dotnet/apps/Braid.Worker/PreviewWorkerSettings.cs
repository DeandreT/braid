using Braid.Application;
using Braid.Storage.Hosting;

namespace Braid.Worker;

/// <summary>Explicit environment configuration; nothing is inferred from a request or mapping.</summary>
internal sealed record PreviewWorkerSettings(
    bool PreviewEnabled,
    string? WriterConnectionString,
    PrivateStorageSettings? Storage,
    string WorkerImage,
    string DockerExecutable,
    TimeSpan Lease)
{
    internal static PreviewWorkerSettings Read(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string Required(string name) => environment(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"The preview worker requires {name}.");
        // Previews stay on unless explicitly disabled; a worker that only serves inspection opts out.
        var preview = environment("BRAID_PREVIEW_WORKER_ENABLED") switch
        {
            null or "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException("Preview worker enablement must be true or false.")
        };
        // Regression and build execution read native objects and run previews through the preview composition.
        if (!preview && (environment("BRAID_REGRESSION_WORKER_ENABLED") == "true" || environment("BRAID_BUILD_WORKER_ENABLED") == "true"))
            throw new InvalidOperationException("Regression and build execution require the preview worker.");
        return new(preview,
            preview ? Required("BRAID_PREVIEW_WRITER_CONNECTION_STRING") : null,
            // Previews read native objects from the explicitly selected private store.
            preview ? PrivateStorageSettings.Read(environment) : null,
            !preview && environment("BRAID_NOTIFICATIONS_ENABLED") == "true" && environment("BRAID_INSPECTION_WORKER_ENABLED") != "true"
                ? "" : Required("BRAID_FERRULE_WORKER_IMAGE"),
            environment("BRAID_DOCKER_EXECUTABLE") is { Length: > 0 } docker ? docker : "/usr/bin/docker",
            preview && environment("BRAID_PREVIEW_LEASE_SECONDS") is { Length: > 0 } lease
                ? TimeSpan.FromSeconds(int.Parse(lease, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture))
                : new PreviewRunOptions().Lease);
    }
}
