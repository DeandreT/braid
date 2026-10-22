using Microsoft.Extensions.DependencyInjection;
using Weft.MappingBackends.Workers;
using Weft.WorkerTransport;

namespace Weft.Server;

/// <summary>
/// The Development host's explicit runner: the local Docker runner when <c>WEFT_FERRULE_WORKER_IMAGE</c> is set (the
/// default), or the Ferrule service with <c>WEFT_FERRULE_RUNNER=service</c>.
/// </summary>
internal static class DevelopmentFerruleRunner
{
    internal static void Configure(IServiceCollection services, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(read);
        if (FerruleRunnerSelection.Read(read, FerruleRunnerKind.Docker, FerruleRunnerKind.Docker, FerruleRunnerKind.Service) == FerruleRunnerKind.Service)
        {
            var service = FerruleServiceEndpoint.Read(read);
            var certificate = PinnedCertificates.FromPem(
                HostPrivateFiles.ReadText(ServerHostOptions.Required(read, FerruleServiceEndpoint.ClientCertificateFile), 65_536),
                HostPrivateFiles.ReadText(ServerHostOptions.Required(read, FerruleServiceEndpoint.ClientPrivateKeyFile), 65_536));
            // The container owns the certificate and disposes it after the runner has stopped.
            services.AddSingleton(_ => certificate);
            InspectionWorkerHostConfiguration.ConfigureService(services, service, certificate);
            return;
        }

        if (FerruleServiceEndpoint.Configured(read)) throw new InvalidOperationException("The Ferrule runner selection is ambiguous.");
        if (read("WEFT_FERRULE_WORKER_IMAGE") is { Length: > 0 } workerImage)
            InspectionWorkerHostConfiguration.Configure(services, new DockerInspectionWorkerOptions("/usr/bin/docker", workerImage));
    }
}
