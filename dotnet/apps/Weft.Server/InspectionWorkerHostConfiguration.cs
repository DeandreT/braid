using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Server;

public static class InspectionWorkerHostConfiguration
{
    public static void Configure(IServiceCollection services, DockerInspectionWorkerOptions options,
        IWorkerProcessLauncher? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(_ => launcher is null
            ? new DockerInspectionWorkerRunner(options)
            : new DockerInspectionWorkerRunner(options, launcher));
        services.AddSingleton<IInspectionWorkerRunner>(provider => provider.GetRequiredService<DockerInspectionWorkerRunner>());
        services.AddSingleton<IHostedService, InspectionWorkerHostLifetime>();
        services.AddSingleton<FerruleInspectionBackend>();
        services.AddSingleton<IMappingInspectionBackend>(provider => provider.GetRequiredService<FerruleInspectionBackend>());
        services.AddSingleton<IMappingProjectionBackend>(provider => provider.GetRequiredService<FerruleInspectionBackend>());
    }
}

/// <summary>Drains the exact singleton before hosted-server stop and service-provider disposal.</summary>
public sealed class InspectionWorkerHostLifetime(DockerInspectionWorkerRunner runner) : IHostedLifecycleService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => runner.StopAsync().WaitAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => runner.StopAsync().WaitAsync(cancellationToken);
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
