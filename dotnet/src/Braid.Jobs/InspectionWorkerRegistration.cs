using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Braid.MappingBackends.Workers;

namespace Braid.Jobs;

/// <summary>
/// Explicit worker-only composition of the hosted inspection service: a dedicated singleton Docker runner with the
/// fixed inspection profile behind the pinned transport. Previews, regressions and builds keep their own runners.
/// </summary>
public static class InspectionWorkerRegistration
{
    private const string Key = "inspection-worker";

    public static IServiceCollection AddBraidInspectionWorker(this IServiceCollection services,
        InspectionTransportServerOptions transport, DockerInspectionWorkerOptions runner)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(runner);
        if (runner.Profile != DockerWorkerProfile.Inspection)
            throw new ArgumentException("The inspection worker requires the fixed inspection profile.", nameof(runner));

        services.AddKeyedSingleton(Key, (_, _) => new DockerInspectionWorkerRunner(runner));
        // Hosted services stop in reverse order: the transport drains its answers before the runner closes.
        services.AddSingleton<IHostedService>(provider => new RunnerLifetime(provider.GetRequiredKeyedService<DockerInspectionWorkerRunner>(Key)));
        services.AddSingleton<IHostedService>(provider =>
            new InspectionTransportServer(transport, provider.GetRequiredKeyedService<DockerInspectionWorkerRunner>(Key)));
        return services;
    }

    /// <summary>Joins every original runner operation; unconfirmed cleanup faults the worker's shutdown.</summary>
    private sealed class RunnerLifetime(DockerInspectionWorkerRunner runner) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => runner.StopAsync().WaitAsync(cancellationToken);
    }
}
