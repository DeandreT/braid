using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Weft.MappingBackends.Workers;
using Weft.Storage.FileSystem;
using Weft.WorkerTransport;

namespace Weft.FerruleService;

/// <summary>
/// Explicit environment configuration, read and admitted completely before the service starts. Nothing is
/// inferred from a request. Invalid or incomplete settings stop the service.
/// </summary>
internal sealed class FerruleServiceSettings
{
    internal const string Listen = "WEFT_FERRULE_SERVICE_LISTEN_ADDR";
    internal const string Authentication = "WEFT_FERRULE_SERVICE_AUTHENTICATION";
    internal const string ClientPins = "WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256";
    internal const string Pem = "WEFT_FERRULE_SERVICE_PEM";
    internal const string CertificateFile = "WEFT_FERRULE_SERVICE_CERTIFICATE_FILE";
    internal const string PrivateKeyFile = "WEFT_FERRULE_SERVICE_PRIVATE_KEY_FILE";
    internal const string HealthListen = "WEFT_FERRULE_SERVICE_HEALTH_ADDR";
    internal const string Concurrency = "WEFT_FERRULE_SERVICE_CONCURRENCY";
    internal const string Queue = "WEFT_FERRULE_SERVICE_QUEUE";
    internal const string AdmissionWait = "WEFT_FERRULE_SERVICE_ADMISSION_WAIT_SECONDS";
    internal const string WorkerExecutableSetting = "WEFT_FERRULE_SERVICE_WORKER_EXECUTABLE";
    internal const string TemporaryRootSetting = "WEFT_FERRULE_SERVICE_TEMPORARY_ROOT";

    private readonly IPEndPoint listen;
    private readonly X509Certificate2? certificate;
    private readonly string[] pins;
    private readonly IPEndPoint? health;

    private FerruleServiceSettings(IPEndPoint listen, X509Certificate2? certificate, string[] pins, IPEndPoint? health,
        int concurrency, int queue, TimeSpan admissionWait, string workerExecutable, string temporaryRoot)
    {
        this.listen = listen;
        this.certificate = certificate;
        this.pins = pins;
        this.health = health;
        ConcurrencyLimit = concurrency;
        QueueLimit = queue;
        Wait = admissionWait;
        WorkerExecutable = workerExecutable;
        TemporaryRoot = temporaryRoot;
    }

    internal bool PlatformSandbox => certificate is null;
    internal int ConcurrencyLimit { get; }
    internal int QueueLimit { get; }
    internal TimeSpan Wait { get; }
    internal string WorkerExecutable { get; }
    internal string TemporaryRoot { get; }

    internal static FerruleServiceSettings Read(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Optional(string name) => environment(name) is { Length: > 0 } value ? value : null;
        string Required(string name) => Optional(name) ?? throw Failure();

        var listen = Endpoint(Required(Listen));
        var platform = Optional(Authentication) switch
        {
            null or "pinnedMutualTls" => false,
            "platformSandbox" => true,
            _ => throw Failure()
        };
        var concurrency = Number(Optional(Concurrency), 1, WorkerTransportServerOptions.MaximumConcurrency) ??
            Math.Clamp(Environment.ProcessorCount, 1, WorkerTransportServerOptions.MaximumConcurrency);
        var queue = Number(Optional(Queue), 0, WorkerTransportServerOptions.MaximumCapacity - concurrency) ??
            Math.Min(concurrency, WorkerTransportServerOptions.MaximumCapacity - concurrency);
        var wait = TimeSpan.FromSeconds(Number(Optional(AdmissionWait), 0, (int)WorkerTransportServerOptions.MaximumAdmissionWait.TotalSeconds) ?? 5);
        var executable = Optional(WorkerExecutableSetting) ?? "/ferrule-host";
        var temporary = Optional(TemporaryRootSetting) ?? "/tmp";

        if (platform)
        {
            // The platform front end authenticates callers; no pinned material may be configured beside it.
            if (Optional(ClientPins) is not null || Optional(Pem) is not null || Optional(CertificateFile) is not null ||
                Optional(PrivateKeyFile) is not null || Optional(HealthListen) is not null)
                throw Failure();
            return new(listen, null, [], null, concurrency, queue, wait, executable, temporary);
        }

        var pins = Required(ClientPins).Split(',');
        if (pins.Length is < 1 or > WorkerTransportServerOptions.MaximumClientPins || !pins.All(InspectionTransport.IsCertificateDigest) ||
            pins.Distinct(StringComparer.Ordinal).Count() != pins.Length)
            throw Failure();
        var health = Optional(HealthListen) is { } healthText ? Endpoint(healthText) : null;
        if (health is not null && health.Port == listen.Port && (health.Address.Equals(listen.Address) ||
            health.Address.Equals(IPAddress.Any) || listen.Address.Equals(IPAddress.Any)))
            throw Failure();
        X509Certificate2 admitted;
        if (Optional(Pem) is { } bundle)
        {
            if (Optional(CertificateFile) is not null || Optional(PrivateKeyFile) is not null) throw Failure();
            admitted = PinnedCertificates.FromPemBundle(bundle);
        }
        else
        {
            admitted = PinnedCertificates.FromPem(ProtectedHostFiles.ReadText(Required(CertificateFile), 65_536),
                ProtectedHostFiles.ReadText(Required(PrivateKeyFile), 65_536));
        }

        return new(listen, admitted, pins, health, concurrency, queue, wait, executable, temporary);
    }

    /// <summary>The listener options; the certificate stays owned by these settings for the process lifetime.</summary>
    internal WorkerTransportServerOptions Transport(Func<bool> healthy, string engineRevision)
    {
        if (certificate is null)
        {
            return new WorkerTransportServerOptions(listen)
            {
                Concurrency = ConcurrencyLimit,
                Capacity = ConcurrencyLimit + QueueLimit,
                AdmissionWait = Wait,
                Healthy = healthy,
                EngineRevision = engineRevision
            };
        }

        return new WorkerTransportServerOptions(listen, certificate, pins)
        {
            ServiceRoutes = true,
            HealthListen = health,
            Concurrency = ConcurrencyLimit,
            Capacity = ConcurrencyLimit + QueueLimit,
            AdmissionWait = Wait,
            Healthy = healthy,
            EngineRevision = engineRevision
        };
    }

    private static IPEndPoint Endpoint(string value) =>
        IPEndPoint.TryParse(value, out var endpoint) && endpoint.Port != 0 && value == endpoint.ToString() ? endpoint : throw Failure();

    private static int? Number(string? value, int minimum, int maximum)
    {
        if (value is null) return null;
        if (value.Length is < 1 or > 3 || (value.Length > 1 && value[0] == '0') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
            throw Failure();
        return number;
    }

    private static InvalidOperationException Failure() => new("The Ferrule service configuration is invalid.");

    public override string ToString() => nameof(FerruleServiceSettings);
}
