using System.Net;
using System.Security.Cryptography.X509Certificates;
using Weft.MappingBackends.Workers;

namespace Weft.WorkerTransport;

/// <summary>How a caller proves its identity to the worker transport.</summary>
public enum WorkerTransportAuthentication
{
    /// <summary>TLS where only clients presenting an admitted certificate pin complete the handshake.</summary>
    PinnedMutualTls,

    /// <summary>
    /// Plain HTTP inside a platform per-request sandbox whose front end authenticates every caller and is the only
    /// route to the listener (for example a dynamic-sessions pool). Never use on a shared network.
    /// </summary>
    PlatformSandbox
}

/// <summary>Trusted listener configuration, never supplied by a request.</summary>
public sealed class WorkerTransportServerOptions
{
    public const int MaximumConcurrency = 64;
    public const int MaximumCapacity = 512;
    public const int MaximumClientPins = 8;
    public static readonly TimeSpan MaximumAdmissionWait = TimeSpan.FromSeconds(20);

    public WorkerTransportServerOptions(IPEndPoint listen, X509Certificate2 serverCertificate, IReadOnlyCollection<string> clientCertificateSha256)
    {
        ArgumentNullException.ThrowIfNull(listen);
        ArgumentNullException.ThrowIfNull(serverCertificate);
        ArgumentNullException.ThrowIfNull(clientCertificateSha256);
        if (!serverCertificate.HasPrivateKey)
            throw new ArgumentException("The worker certificate requires its private key.", nameof(serverCertificate));
        if (clientCertificateSha256.Count is < 1 or > MaximumClientPins || clientCertificateSha256.Distinct(StringComparer.Ordinal).Count() != clientCertificateSha256.Count ||
            !clientCertificateSha256.All(InspectionTransport.IsCertificateDigest))
            throw new ArgumentException("The admitted client pins must be one to eight distinct lowercase SHA-256 digests.", nameof(clientCertificateSha256));
        Listen = listen;
        ServerCertificate = serverCertificate;
        ClientCertificateSha256 = [.. clientCertificateSha256];
        Authentication = WorkerTransportAuthentication.PinnedMutualTls;
    }

    /// <summary>A plain listener inside a platform sandbox; it serves only the service routes.</summary>
    public WorkerTransportServerOptions(IPEndPoint listen)
    {
        ArgumentNullException.ThrowIfNull(listen);
        Listen = listen;
        ClientCertificateSha256 = [];
        Authentication = WorkerTransportAuthentication.PlatformSandbox;
        ServiceRoutes = true;
    }

    public IPEndPoint Listen { get; }
    /// <summary>Borrowed; its owner disposes it after the server has stopped.</summary>
    public X509Certificate2? ServerCertificate { get; }
    public IReadOnlyList<string> ClientCertificateSha256 { get; }
    public WorkerTransportAuthentication Authentication { get; }

    /// <summary>Serves <c>weft.ferrule-service.v1</c> routes and HTTP/2 in addition to the inspection transport.</summary>
    public bool ServiceRoutes { get; init; }

    /// <summary>Operations that run at the same time.</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Body readers and queued/running requests together; more are refused before buffering.</summary>
    public int Capacity { get; init; } = InspectionTransport.MaximumOperations;

    /// <summary>A queued operation not started within this wait is refused unexecuted.</summary>
    public TimeSpan AdmissionWait { get; init; } = InspectionTransport.AdmissionWait;

    /// <summary>Optional plain HTTP listener for platform probes; it serves only liveness and readiness.</summary>
    public IPEndPoint? HealthListen { get; init; }

    /// <summary>Reported by the capability route.</summary>
    public string? EngineRevision { get; init; }

    /// <summary>Readiness also requires this host-supplied check, for example that no cleanup is unconfirmed.</summary>
    public Func<bool>? Healthy { get; init; }

    internal void Validate()
    {
        if (Concurrency is < 1 or > MaximumConcurrency || Capacity < Concurrency || Capacity > MaximumCapacity)
            throw new ArgumentException("Concurrency must be 1 to 64 and capacity at least the concurrency and at most 512.");
        if (AdmissionWait < TimeSpan.Zero || AdmissionWait > MaximumAdmissionWait)
            throw new ArgumentException("The admission wait must be between zero and 20 seconds.");
        if (Authentication == WorkerTransportAuthentication.PlatformSandbox && HealthListen is not null)
            throw new ArgumentException("A platform sandbox listener serves its probes itself.");
    }
}
