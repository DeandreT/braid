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
    internal static IServiceCollection AddFerruleService(this IServiceCollection services, FerruleServiceSettings settings, ServiceRunners runners)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runners);
        // Hosted services stop in reverse order: the listener drains its answers before the runners close.
        services.AddSingleton<IHostedService>(new RunnerLifetime(runners));
        services.AddSingleton<IHostedService>(_ =>
        {
            var transport = settings.Transport(() => runners.Inspection.Available && runners.Preview.Available && runners.Compile?.Available != false,
                FerruleInspectionBackend.EngineRevision, runners.Compile?.ToolchainIdentity);
            var served = new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal)
            {
                [FerruleServiceProtocol.InspectionProfile] = runners.Inspection,
                [FerruleServiceProtocol.PreviewProfile] = runners.Preview
            };
            if (runners.Compile is { } compile) served[FerruleServiceProtocol.CompileProfile] = compile;
            return new WorkerTransportServer(transport, served);
        });
        return services;
    }

    /// <summary>
    /// Creates every runner before the host starts. With compilation enabled, one launcher must prove that this
    /// platform enforces the compile sandbox; otherwise the service does not start.
    /// </summary>
    internal static async Task<ServiceRunners> CreateRunnersAsync(FerruleServiceSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var inspection = new ProcessWorkerRunner(new ProcessWorkerOptions(settings.WorkerExecutable, DockerWorkerProfile.Inspection, settings.TemporaryRoot));
        var preview = new ProcessWorkerRunner(new ProcessWorkerOptions(settings.WorkerExecutable, DockerWorkerProfile.Preview, settings.TemporaryRoot));
        if (settings.Compile is not { } installation) return new(inspection, preview, null);
        var compile = new ProcessCompileRunner(new ProcessCompileOptions(settings.WorkerExecutable, installation.Launcher, settings.TemporaryRoot,
            installation.Toolchain));
        if (!await compile.ProbeAsync(cancellationToken).ConfigureAwait(false))
        {
            await compile.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The compile sandbox is unavailable on this platform.");
        }

        return new(inspection, preview, compile);
    }

    internal sealed record ServiceRunners(ProcessWorkerRunner Inspection, ProcessWorkerRunner Preview, ProcessCompileRunner? Compile);

    /// <summary>Joins every child; unconfirmed cleanup faults the service's shutdown.</summary>
    private sealed class RunnerLifetime(ServiceRunners runners) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.WhenAll(runners.Inspection.StopAsync(), runners.Preview.StopAsync(), runners.Compile?.StopAsync() ?? Task.CompletedTask)
                .WaitAsync(cancellationToken);
    }
}
