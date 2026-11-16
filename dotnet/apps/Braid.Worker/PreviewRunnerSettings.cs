using System.Security.Cryptography.X509Certificates;
using Braid.MappingBackends.AzureSessions;
using Braid.MappingBackends.Workers;
using Braid.Storage.FileSystem;
using Braid.WorkerTransport;

namespace Braid.Worker;

/// <summary>
/// The preview runner: the local Docker runner (default), the Ferrule service over pinned mutual TLS
/// (<c>BRAID_FERRULE_RUNNER=service</c>) or the Ferrule service in Azure Container Apps dynamic sessions
/// (<c>azureContainerAppsSessions</c>). <c>BRAID_FERRULE_WORKER_IMAGE</c> is the expected worker identity for each.
/// </summary>
internal sealed class PreviewRunnerSettings
{
    private readonly FerruleServiceEndpoint? service;
    private readonly X509Certificate2? clientCertificate;
    private readonly AzureSessionsRunnerSettings? sessions;
    private readonly DockerRunnerBudgets? dockerBudgets;

    private PreviewRunnerSettings(FerruleServiceEndpoint? service = null, X509Certificate2? clientCertificate = null,
        AzureSessionsRunnerSettings? sessions = null, DockerRunnerBudgets? dockerBudgets = null)
    {
        this.service = service;
        this.clientCertificate = clientCertificate;
        this.sessions = sessions;
        this.dockerBudgets = dockerBudgets;
    }

    internal FerruleRunnerKind Kind => service is not null ? FerruleRunnerKind.Service
        : sessions is not null ? FerruleRunnerKind.AzureContainerAppsSessions : FerruleRunnerKind.Docker;

    internal static PreviewRunnerSettings Read(Func<string, string?> environment, PreviewWorkerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.PreviewEnabled)
        {
            // This worker only serves inspection; no service compiler can consume a toolchain pin.
            if (environment(FerruleRunnerSelection.CompileToolchainSetting) is not null) throw Failure();
            return new();
        }
        try
        {
            var runner = FerruleRunnerSelection.Read(environment, FerruleRunnerKind.Docker, FerruleRunnerKind.Docker, FerruleRunnerKind.Service,
                FerruleRunnerKind.AzureContainerAppsSessions);
            _ = FerruleRunnerSelection.ReadCompileToolchain(environment, runner,
                required: environment("BRAID_BUILD_WORKER_ENABLED") == "true");
            // Settings of a runner that is not selected are ambiguous, not ignored.
            if ((runner != FerruleRunnerKind.Service && FerruleServiceEndpoint.Configured(environment)) ||
                (runner != FerruleRunnerKind.AzureContainerAppsSessions && AzureSessionsRunnerSettings.Configured(environment)))
                throw Failure();
            if (runner == FerruleRunnerKind.Docker) return new(dockerBudgets: DockerRunnerBudgets.Read(environment));
            if (runner == FerruleRunnerKind.AzureContainerAppsSessions) return new(sessions: AzureSessionsRunnerSettings.Read(environment));
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
    internal string ProfileIdentity(PreviewWorkerSettings settings) =>
        service?.ProfileIdentity ?? sessions?.ProfileIdentity ?? settings.WorkerImage.ToLowerInvariant();

    internal IInspectionWorkerRunner CreateRunner(PreviewWorkerSettings settings) => CreateRunner(settings, compile: false);

    internal IInspectionWorkerRunner CreateBuildRunner(PreviewWorkerSettings settings) => CreateRunner(settings, compile: true);

    private IInspectionWorkerRunner CreateRunner(PreviewWorkerSettings settings, bool compile)
    {
        var profile = compile ? FerruleServiceProtocol.CompileProfile : FerruleServiceProtocol.PreviewProfile;
        if (service is not null)
            return new FerruleServiceRunner(new(new PinnedServiceConnector(new(service.Endpoint, service.ServiceCertificateSha256, clientCertificate!)),
                profile, service.BuildIdentity, service.CompileToolchainIdentity));
        if (sessions is not null)
        {
            var connector = sessions.CreateConnector();
            return new ConnectorOwningRunner(new FerruleServiceRunner(new(connector, profile, sessions.BuildIdentity, sessions.CompileToolchainIdentity)),
                connector);
        }
        return new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions(settings.DockerExecutable, settings.WorkerImage,
            profile: compile ? DockerWorkerProfile.Build : DockerWorkerProfile.Preview, budgets: dockerBudgets));
    }


    private static InvalidOperationException Failure() => new("The preview runner configuration is invalid.");

    /// <summary>Disposes the sessions connector only after its runner has stopped and joined every operation.</summary>
    private sealed class ConnectorOwningRunner(FerruleServiceRunner runner, AzureSessionsConnector connector) : IInspectionWorkerRunner,
        ICompilationIdentitySource, IAsyncDisposable
    {
        public string BuildIdentity => runner.BuildIdentity;
        public string? CompilationBuildIdentity => runner.CompilationBuildIdentity;

        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken) =>
            runner.RunAsync(requestFrame, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try { await runner.DisposeAsync().ConfigureAwait(false); }
            finally { connector.Dispose(); }
        }
    }
}
