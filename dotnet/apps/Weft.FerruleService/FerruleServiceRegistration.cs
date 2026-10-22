using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.WorkerTransport;

namespace Weft.FerruleService;

/// <summary>
/// The portable Ferrule service: one child-process runner per operation profile behind one transport listener.
/// The service holds no state between operations, so a platform scales it by adding replicas.
/// </summary>
internal static class FerruleServiceRegistration
{
    internal static IServiceCollection AddFerruleService(this IServiceCollection services, FerruleServiceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        services.AddSingleton(_ => new ServiceRunners(
            new ProcessWorkerRunner(new ProcessWorkerOptions(settings.WorkerExecutable, DockerWorkerProfile.Inspection, settings.TemporaryRoot)),
            new ProcessWorkerRunner(new ProcessWorkerOptions(settings.WorkerExecutable, DockerWorkerProfile.Preview, settings.TemporaryRoot))));
        // Hosted services stop in reverse order: the listener drains its answers before the runners close.
        services.AddSingleton<IHostedService>(provider => new RunnerLifetime(provider.GetRequiredService<ServiceRunners>()));
        services.AddSingleton<IHostedService>(provider =>
        {
            var runners = provider.GetRequiredService<ServiceRunners>();
            var transport = settings.Transport(() => runners.Inspection.Available && runners.Preview.Available,
                FerruleInspectionBackend.EngineRevision);
            return new WorkerTransportServer(transport, new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal)
            {
                [FerruleServiceProtocol.InspectionProfile] = runners.Inspection,
                [FerruleServiceProtocol.PreviewProfile] = runners.Preview
            });
        });
        return services;
    }

    internal sealed record ServiceRunners(ProcessWorkerRunner Inspection, ProcessWorkerRunner Preview);

    /// <summary>Joins every child; unconfirmed cleanup faults the service's shutdown.</summary>
    private sealed class RunnerLifetime(ServiceRunners runners) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.WhenAll(runners.Inspection.StopAsync(), runners.Preview.StopAsync()).WaitAsync(cancellationToken);
    }
}
