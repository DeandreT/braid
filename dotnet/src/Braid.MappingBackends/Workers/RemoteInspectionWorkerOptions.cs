using System.Security.Cryptography.X509Certificates;

namespace Braid.MappingBackends.Workers;

/// <summary>Trusted host configuration for one pinned inspection worker, never supplied by a mapping or browser.</summary>
public sealed class RemoteInspectionWorkerOptions
{
    public RemoteInspectionWorkerOptions(Uri endpoint, string workerCertificateSha256, X509Certificate2 clientCertificate, string imageId)
    {
        ArgumentNullException.ThrowIfNull(clientCertificate);
        RequireEndpoint(endpoint);
        if (!InspectionTransport.IsCertificateDigest(workerCertificateSha256))
            throw new ArgumentException("The inspection worker certificate pin must be a lowercase SHA-256 digest.", nameof(workerCertificateSha256));
        if (!clientCertificate.HasPrivateKey)
            throw new ArgumentException("The inspection client certificate requires its private key.", nameof(clientCertificate));
        Endpoint = endpoint;
        WorkerCertificateSha256 = workerCertificateSha256;
        ClientCertificate = clientCertificate;
        ImageId = InspectionTransport.RequireImage(imageId);
    }

    /// <summary>One canonical HTTPS origin: no user information, path, query or fragment.</summary>
    public static Uri RequireEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
            endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            endpoint.OriginalString != endpoint.GetLeftPart(UriPartial.Authority))
            throw new ArgumentException("The inspection worker endpoint must be one canonical HTTPS origin.", nameof(endpoint));
        return endpoint;
    }

    public Uri Endpoint { get; }
    public string WorkerCertificateSha256 { get; }
    /// <summary>Borrowed; its owner disposes it after the runner has stopped.</summary>
    public X509Certificate2 ClientCertificate { get; }
    public string ImageId { get; }

    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Worker admission, the inspection wall deadline and its cleanup, plus transport margin.</summary>
    internal TimeSpan ResponseBound { get; init; } = InspectionTransport.AdmissionWait + DockerWorkerProfile.Inspection.MaximumWallTimeout
        + DockerInspectionWorkerOptions.DefaultCleanupTimeout + DockerInspectionWorkerOptions.KillTimeout + TimeSpan.FromSeconds(5);

    /// <summary>After a cancellation request: creation settlement within the wall budget, cleanup, and margin.</summary>
    internal TimeSpan SettlementBound { get; init; } = DockerWorkerProfile.Inspection.MaximumWallTimeout
        + DockerInspectionWorkerOptions.DefaultCleanupTimeout + TimeSpan.FromSeconds(3);

    internal TimeSpan CancelRequestBound { get; init; } = TimeSpan.FromSeconds(5);
}
