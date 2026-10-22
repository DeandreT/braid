namespace Weft.MappingBackends.Workers;

/// <summary>Where a host runs Ferrule frames. Each host admits only the kinds it can compose.</summary>
public enum FerruleRunnerKind
{
    /// <summary>A per-operation restricted container through the local Docker daemon (development and tests).</summary>
    Docker,

    /// <summary>The pinned inspection transport to the separately hosted <c>Weft.Worker</c>.</summary>
    InspectionWorker,

    /// <summary>The Ferrule service over pinned mutual TLS.</summary>
    Service,

    /// <summary>The Ferrule service inside Azure Container Apps dynamic sessions, one session per operation.</summary>
    AzureContainerAppsSessions
}

/// <summary>The explicit <c>WEFT_FERRULE_RUNNER</c> selection; nothing is inferred from other settings.</summary>
public static class FerruleRunnerSelection
{
    public const string Setting = "WEFT_FERRULE_RUNNER";

    public static FerruleRunnerKind Read(Func<string, string?> read, FerruleRunnerKind defaultKind, params FerruleRunnerKind[] admitted)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(admitted);
        var kind = read(Setting) switch
        {
            null => defaultKind,
            "docker" => FerruleRunnerKind.Docker,
            "worker" => FerruleRunnerKind.InspectionWorker,
            "service" => FerruleRunnerKind.Service,
            "azureContainerAppsSessions" => FerruleRunnerKind.AzureContainerAppsSessions,
            _ => throw Failure()
        };
        return admitted.Contains(kind) ? kind : throw Failure();
    }

    private static InvalidOperationException Failure() => new("The Ferrule runner selection is invalid for this host.");
}

/// <summary>
/// The pinned Ferrule service scalars, read before any private delivery: one canonical HTTPS origin, the service
/// certificate's SHA-256 pin and the expected worker build identity (<c>WEFT_FERRULE_WORKER_IMAGE</c>). The client
/// certificate is admitted separately with each host's protected deliveries.
/// </summary>
public sealed record FerruleServiceEndpoint(Uri Endpoint, string ServiceCertificateSha256, string BuildIdentity)
{
    public const string Url = "WEFT_FERRULE_SERVICE_URL";
    public const string CertificatePin = "WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256";
    public const string Identity = "WEFT_FERRULE_WORKER_IMAGE";
    public const string ClientCertificateFile = "WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_FILE";
    public const string ClientPrivateKeyFile = "WEFT_FERRULE_SERVICE_CLIENT_PRIVATE_KEY_FILE";

    public static FerruleServiceEndpoint Read(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            string Required(string name) => read(name) is { Length: > 0 } value ? value : throw Failure();
            var endpoint = RemoteInspectionWorkerOptions.RequireEndpoint(new Uri(Required(Url), UriKind.Absolute));
            var pin = Required(CertificatePin);
            if (!InspectionTransport.IsCertificateDigest(pin)) throw Failure();
            return new(endpoint, pin, InspectionTransport.RequireImage(Required(Identity)));
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException) { throw Failure(); }
    }

    /// <summary>A service scalar beside another runner selection is an ambiguous configuration.</summary>
    public static bool Configured(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read(Url) is not null || read(CertificatePin) is not null;
    }

    private static InvalidOperationException Failure() => new("The Ferrule service configuration is invalid.");

    public override string ToString() => nameof(FerruleServiceEndpoint);
}
