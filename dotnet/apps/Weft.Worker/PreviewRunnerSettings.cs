using System.Security.Cryptography.X509Certificates;
using Weft.MappingBackends.Workers;
using Weft.Storage.FileSystem;
using Weft.WorkerTransport;

namespace Weft.Worker;

/// <summary>
/// The preview runner: the local Docker runner (default) or, with <c>WEFT_FERRULE_RUNNER=service</c>, the Ferrule
/// service over pinned mutual TLS. <c>WEFT_FERRULE_WORKER_IMAGE</c> is the expected worker identity for either.
/// </summary>
internal sealed class PreviewRunnerSettings
{
    private readonly FerruleServiceEndpoint? service;
    private readonly X509Certificate2? clientCertificate;

    private PreviewRunnerSettings(FerruleServiceEndpoint? service, X509Certificate2? clientCertificate)
    {
        this.service = service;
        this.clientCertificate = clientCertificate;
    }

    internal FerruleRunnerKind Kind => service is null ? FerruleRunnerKind.Docker : FerruleRunnerKind.Service;

    internal static PreviewRunnerSettings Read(Func<string, string?> environment, PreviewWorkerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.PreviewEnabled) return new(null, null);
        try
        {
            if (FerruleRunnerSelection.Read(environment, FerruleRunnerKind.Docker, FerruleRunnerKind.Docker, FerruleRunnerKind.Service) ==
                FerruleRunnerKind.Docker)
                return FerruleServiceEndpoint.Configured(environment) ? throw Failure() : new(null, null);
            string Required(string name) => environment(name) is { Length: > 0 } value ? value : throw Failure();
            var endpoint = FerruleServiceEndpoint.Read(environment);
            var certificate = PinnedCertificates.FromPem(
                ProtectedHostFiles.ReadText(Required(FerruleServiceEndpoint.ClientCertificateFile), 65_536),
                ProtectedHostFiles.ReadText(Required(FerruleServiceEndpoint.ClientPrivateKeyFile), 65_536));
            return new(endpoint, certificate);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { throw Failure(); }
    }

    /// <summary>The preview runner; the container owns it, and with it the client certificate's use.</summary>
    internal IInspectionWorkerRunner CreateRunner(PreviewWorkerSettings settings) => service is null
        ? new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions(settings.DockerExecutable, settings.WorkerImage, profile: DockerWorkerProfile.Preview))
        : new FerruleServiceRunner(new(new PinnedServiceConnector(new(service.Endpoint, service.ServiceCertificateSha256, clientCertificate!)),
            FerruleServiceProtocol.PreviewProfile, service.BuildIdentity));

    private static InvalidOperationException Failure() => new("The preview runner configuration is invalid.");
}
