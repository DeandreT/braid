using System.Security.Cryptography.X509Certificates;
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
        services.AddSingleton<IHostedService>(provider =>
            new InspectionWorkerHostLifetime(provider.GetRequiredService<DockerInspectionWorkerRunner>().StopAsync));
        AddBackends(services);
    }

    /// <summary>The Session host's only runner: exact frames forwarded to the separate pinned inspection worker.</summary>
    internal static void ConfigureRemote(IServiceCollection services, RemoteInspectionWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(_ => new RemoteInspectionWorkerRunner(options));
        services.AddSingleton<IInspectionWorkerRunner>(provider => provider.GetRequiredService<RemoteInspectionWorkerRunner>());
        services.AddSingleton<IHostedService>(provider =>
            new InspectionWorkerHostLifetime(provider.GetRequiredService<RemoteInspectionWorkerRunner>().StopAsync));
        AddBackends(services);
    }

    /// <summary>Exact inspection frames sent to the Ferrule service over pinned mutual TLS.</summary>
    internal static void ConfigureService(IServiceCollection services, FerruleServiceEndpoint service, X509Certificate2 clientCertificate)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(clientCertificate);
        ConfigureConnector(services,
            _ => new PinnedServiceConnector(new(service.Endpoint, service.ServiceCertificateSha256, clientCertificate)), service.BuildIdentity,
            service.CompileToolchainIdentity);
    }

    /// <summary>
    /// Exact inspection frames sent to the Ferrule service through a connector, for example a platform sandbox. The
    /// container owns the connector and disposes it after the runner has stopped.
    /// </summary>
    internal static void ConfigureConnector(IServiceCollection services, Func<IServiceProvider, IFerruleServiceConnector> connector, string buildIdentity,
        string? compileToolchainIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connector);
        services.AddKeyedSingleton(nameof(IFerruleServiceConnector), (provider, _) => connector(provider));
        services.AddSingleton(provider => new FerruleServiceRunner(new(
            provider.GetRequiredKeyedService<IFerruleServiceConnector>(nameof(IFerruleServiceConnector)), FerruleServiceProtocol.InspectionProfile,
            buildIdentity, compileToolchainIdentity)));
        services.AddSingleton<IInspectionWorkerRunner>(provider => provider.GetRequiredService<FerruleServiceRunner>());
        services.AddSingleton<IHostedService>(provider =>
            new InspectionWorkerHostLifetime(provider.GetRequiredService<FerruleServiceRunner>().StopAsync));
        AddBackends(services);
    }

    private static void AddBackends(IServiceCollection services)
    {
        services.AddSingleton<FerruleInspectionBackend>();
        services.AddSingleton<IMappingInspectionBackend>(provider => provider.GetRequiredService<FerruleInspectionBackend>());
        services.AddSingleton<IMappingProjectionBackend>(provider => provider.GetRequiredService<FerruleInspectionBackend>());
    }
}

/// <summary>Drains the exact singleton before hosted-server stop and service-provider disposal.</summary>
public sealed class InspectionWorkerHostLifetime : IHostedLifecycleService
{
    private readonly Func<Task> stop;

    public InspectionWorkerHostLifetime(DockerInspectionWorkerRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        stop = runner.StopAsync;
    }

    internal InspectionWorkerHostLifetime(Func<Task> stop) => this.stop = stop;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => stop().WaitAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => stop().WaitAsync(cancellationToken);
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Session scalars for the hosted inspection worker, read before any private delivery: one canonical HTTPS
/// origin, the worker certificate's SHA-256 pin and the shared immutable image identity. The client certificate
/// is admitted later with the other protected PEM deliveries.
/// </summary>
internal sealed record SessionInspectionWorker(Uri Endpoint, string WorkerCertificateSha256, string ImageId)
{
    internal const string ClientCertificateFile = "WEFT_INSPECTION_CLIENT_CERTIFICATE_FILE";
    internal const string ClientPrivateKeyFile = "WEFT_INSPECTION_CLIENT_PRIVATE_KEY_FILE";

    internal static SessionInspectionWorker Read(Func<string, string?> read)
    {
        try
        {
            var endpoint = RemoteInspectionWorkerOptions.RequireEndpoint(
                new Uri(ServerHostOptions.Required(read, "WEFT_INSPECTION_WORKER_URL"), UriKind.Absolute));
            var pin = ServerHostOptions.Required(read, "WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256");
            if (!InspectionTransport.IsCertificateDigest(pin)) throw ServerHostOptions.Failure();
            return new(endpoint, pin, InspectionTransport.RequireImage(ServerHostOptions.Required(read, "WEFT_FERRULE_WORKER_IMAGE")));
        }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { throw ServerHostOptions.Failure(); }
    }

    internal RemoteInspectionWorkerOptions Options(X509Certificate2 clientCertificate) =>
        new(Endpoint, WorkerCertificateSha256, clientCertificate, ImageId);

    public override string ToString() => nameof(SessionInspectionWorker);
}
