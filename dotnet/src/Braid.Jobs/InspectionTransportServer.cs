using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Braid.MappingBackends.Workers;
using Braid.WorkerTransport;

namespace Braid.Jobs;

/// <summary>Trusted worker configuration: one private listener and the single pinned client certificate.</summary>
public sealed class InspectionTransportServerOptions
{
    public InspectionTransportServerOptions(IPEndPoint listen, X509Certificate2 serverCertificate, string clientCertificateSha256)
    {
        ArgumentNullException.ThrowIfNull(listen);
        ArgumentNullException.ThrowIfNull(serverCertificate);
        if (!serverCertificate.HasPrivateKey)
            throw new ArgumentException("The inspection worker certificate requires its private key.", nameof(serverCertificate));
        if (!InspectionTransport.IsCertificateDigest(clientCertificateSha256))
            throw new ArgumentException("The inspection client pin must be a lowercase SHA-256 digest.", nameof(clientCertificateSha256));
        Listen = listen;
        ServerCertificate = serverCertificate;
        ClientCertificateSha256 = clientCertificateSha256;
    }

    public IPEndPoint Listen { get; }
    /// <summary>Borrowed; its owner disposes it after the server has stopped.</summary>
    public X509Certificate2 ServerCertificate { get; }
    public string ClientCertificateSha256 { get; }
    internal TimeSpan AdmissionWait { get; init; } = InspectionTransport.AdmissionWait;
}

/// <summary>
/// Serves <c>braid.inspection-transport.v1</c> with this worker's own singleton inspection runner: one operation runs
/// at a time, at most eight are queued or running, and a queued one is refused after the admission wait. Only the
/// pinned client completes the TLS handshake.
/// </summary>
public sealed class InspectionTransportServer(InspectionTransportServerOptions options, IInspectionWorkerRunner runner)
    : IHostedService, IAsyncDisposable
{
    private readonly WorkerTransportServer server = new(
        new WorkerTransportServerOptions(options.Listen, options.ServerCertificate, [options.ClientCertificateSha256])
        {
            Concurrency = 1,
            Capacity = InspectionTransport.MaximumOperations,
            AdmissionWait = options.AdmissionWait
        },
        new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal) { [FerruleServiceProtocol.InspectionProfile] = runner });

    /// <summary>The actual bound port, for an explicitly selected ephemeral test listener.</summary>
    internal int Port => server.Port;

    public Task StartAsync(CancellationToken cancellationToken) => server.StartAsync(cancellationToken);

    /// <summary>Refuses new operations, cancels admitted ones through the runner and drains their answers.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => server.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => server.DisposeAsync();
}
