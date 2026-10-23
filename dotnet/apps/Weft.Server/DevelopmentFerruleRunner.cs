using Microsoft.Extensions.DependencyInjection;
using Weft.MappingBackends.AzureSessions;
using Weft.MappingBackends.Workers;
using Weft.WorkerTransport;

namespace Weft.Server;

/// <summary>
/// The Development host's explicit runner: the local Docker runner when <c>WEFT_FERRULE_WORKER_IMAGE</c> is set (the
/// default), the Ferrule service (<c>service</c>) or Azure Container Apps dynamic sessions (<c>azureContainerAppsSessions</c>).
/// </summary>
internal static class DevelopmentFerruleRunner
{
    internal static void Configure(IServiceCollection services, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(read);
        var runner = FerruleRunnerSelection.Read(read, FerruleRunnerKind.Docker, FerruleRunnerKind.Docker, FerruleRunnerKind.Service,
            FerruleRunnerKind.AzureContainerAppsSessions);
        _ = FerruleRunnerSelection.ReadCompileToolchain(read, runner);
        if ((runner != FerruleRunnerKind.Service && FerruleServiceEndpoint.Configured(read)) ||
            (runner != FerruleRunnerKind.AzureContainerAppsSessions && AzureSessionsRunnerSettings.Configured(read)))
            throw new InvalidOperationException("The Ferrule runner selection is ambiguous.");
        if (runner == FerruleRunnerKind.AzureContainerAppsSessions)
        {
            var sessions = AzureSessionsRunnerSettings.Read(read);
            InspectionWorkerHostConfiguration.ConfigureConnector(services, _ => sessions.CreateConnector(), sessions.BuildIdentity, sessions.CompileToolchainIdentity);
            return;
        }

        if (runner == FerruleRunnerKind.Service)
        {
            var service = FerruleServiceEndpoint.Read(read);
            var certificate = PinnedCertificates.FromPem(
                HostPrivateFiles.ReadText(ServerHostOptions.Required(read, FerruleServiceEndpoint.ClientCertificateFile), 65_536),
                HostPrivateFiles.ReadText(ServerHostOptions.Required(read, FerruleServiceEndpoint.ClientPrivateKeyFile), 65_536));
            // The certificate lives as long as the host process.
            InspectionWorkerHostConfiguration.ConfigureService(services, service, certificate);
            return;
        }

        if (read("WEFT_FERRULE_WORKER_IMAGE") is { Length: > 0 } workerImage)
            InspectionWorkerHostConfiguration.Configure(services, new DockerInspectionWorkerOptions("/usr/bin/docker", workerImage));
    }
}
