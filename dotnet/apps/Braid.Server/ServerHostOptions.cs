using System.Globalization;
using System.Net;
using Braid.Api.Auth;
using Braid.Api.Catalog;
using Braid.Identity;

using Braid.MappingBackends.AzureSessions;
using Braid.MappingBackends.Workers;

namespace Braid.Server;

/// <summary>One explicit host identity/transport profile. No credential is read while parsing scalars.</summary>
internal sealed class ServerHostOptions
{
    private ServerHostOptions(AuthenticationHttpMode mode, IPEndPoint listener, string origin,
        bool proxy, IPAddress? knownProxy, Guid organization, Guid tenant, Guid client,
        string? applicationName, string? keyDirectory)
    {
        Mode = mode; Listener = listener; Origin = origin; Proxy = proxy; KnownProxy = knownProxy;
        OrganizationId = organization; TenantId = tenant; ClientId = client;
        ApplicationName = applicationName; KeyDirectory = keyDirectory;
    }

    internal AuthenticationHttpMode Mode { get; }
    internal IPEndPoint Listener { get; }
    internal string Origin { get; }
    internal bool Proxy { get; }
    internal IPAddress? KnownProxy { get; }
    internal Guid OrganizationId { get; }
    internal Guid TenantId { get; }
    internal Guid ClientId { get; }
    internal string? ApplicationName { get; }
    internal string? KeyDirectory { get; }
    internal bool Session => Mode == AuthenticationHttpMode.Session;
    internal CatalogMetadataEditOptions Metadata { get; private init; } = new(false);
    internal CatalogRevisionIngestionOptions Ingestion { get; private init; } = new(false);
    internal CatalogProjectCreationOptions Creation { get; private init; } = new(false);
    internal CatalogRevisionReportOptions Reports { get; private init; } = new(false);
    internal bool Chat { get; private init; }
    internal CatalogPreviewOptions Previews { get; private init; } = new(false);
    internal BuildHostRuntime Builds { get; private init; } = new(false);
    internal long? NativeByteLimit { get; private init; }
    internal int? DerivedReadByteLimit { get; private init; }
    internal SessionInspectionWorker? InspectionWorker { get; private init; }
    internal FerruleServiceEndpoint? FerruleService { get; private init; }
    internal AzureSessionsRunnerSettings? FerruleSessions { get; private init; }
    internal EntraUserTokenOptions? Bearer { get; private init; }

    internal static ServerHostOptions Read(Func<string, string?> read)
    {
        var enabled = Flag(read, "BRAID_CATALOG_ENABLED");
        var mode = read("BRAID_CATALOG_AUTH_MODE") switch
        {
            null => enabled ? AuthenticationHttpMode.Development : AuthenticationHttpMode.Disabled,
            "disabled" when !enabled => AuthenticationHttpMode.Disabled,
            "development" when enabled => AuthenticationHttpMode.Development,
            "session" when enabled => AuthenticationHttpMode.Session,
            _ => throw Failure()
        };
        if (mode != AuthenticationHttpMode.Session)
        {
            // Bearer access exists only beside Entra sessions; a dormant setting elsewhere is a mistake.
            if (Flag(read, BearerEnabledSetting) || BearerSettings.Any(name => read(name) is not null)) throw Failure();
            var listener = DevelopmentListenAddress.Parse(read("BRAID_DEV_LISTEN_ADDR"));
            var origin = mode == AuthenticationHttpMode.Development
                ? Required(read, "BRAID_DEV_BROWSER_ORIGIN") : LoopbackOrigin(listener);
            return new(mode, listener, origin, false, null, default, default, default, null, null)
            {
                Chat = Flag(read, "BRAID_CHAT_ENABLED")
            };
        }
        if (Flag(read, "BRAID_DEV_IDENTITY_ENABLED") || new[]
            { "BRAID_DEV_PRINCIPAL_ID", "BRAID_DEV_ORGANIZATION_ID", "BRAID_DEV_BROWSER_ORIGIN", "BRAID_DEV_LISTEN_ADDR" }
            .Concat(LocalSandboxHostConfiguration.Settings)
            .Any(name => read(name) is not null)) throw Failure();
        var publicOrigin = Required(read, "BRAID_PUBLIC_ORIGIN");
        _ = new ServerSessionBrowserOptions(publicOrigin);
        var tenant = Id(read, "BRAID_ENTRA_TENANT_ID");
        var client = Id(read, "BRAID_ENTRA_CLIENT_ID");
        _ = new EntraClientOptions(tenant, client, publicOrigin);
        var configuredListener = Required(read, "BRAID_LISTEN_ADDR");
        if (!IPEndPoint.TryParse(configuredListener, out var address) || address.Port == 0 ||
            configuredListener != address.ToString() || address.Address.Equals(IPAddress.Any) ||
            address.Address.Equals(IPAddress.IPv6Any)) throw Failure();
        var proxy = Required(read, "BRAID_HOST_TRANSPORT") switch
        {
            "directHttps" => false,
            "trustedProxyOneHop" => true,
            _ => throw Failure()
        };
        IPAddress? knownProxy = null;
        if (proxy)
        {
            var configuredProxy = Required(read, "BRAID_KNOWN_PROXY_IP");
            if (!IPAddress.IsLoopback(address.Address) ||
                !IPAddress.TryParse(configuredProxy, out knownProxy) || !IPAddress.IsLoopback(knownProxy) ||
                configuredProxy != knownProxy.ToString()) throw Failure();
            if (read("BRAID_TLS_CERTIFICATE_FILE") is not null || read("BRAID_TLS_PRIVATE_KEY_FILE") is not null)
                throw Failure();
        }
        else if (read("BRAID_KNOWN_PROXY_IP") is not null || new Uri(publicOrigin).Port != address.Port)
            throw Failure();
        var organization = Id(read, "BRAID_SESSION_ORGANIZATION_ID");
        var application = Required(read, "BRAID_SESSION_APPLICATION_NAME");
        var directory = Required(read, "BRAID_SESSION_KEY_DIRECTORY");
        _ = new ServerSessionKeyProfileOptions(organization, application, directory);
        // Snapshot the selected business scalars before any PEM, ring or credential delivery is opened.
        var metadata = new CatalogMetadataEditOptions(Flag(read, "BRAID_METADATA_EDITS_ENABLED"));
        var ingestion = new CatalogRevisionIngestionOptions(Flag(read, "BRAID_REVISION_INGESTION_ENABLED"));
        var creation = new CatalogProjectCreationOptions(Flag(read, "BRAID_PROJECT_CREATION_ENABLED"));
        var reports = new CatalogRevisionReportOptions(Flag(read, "BRAID_REVISION_REPORTS_ENABLED"),
            Flag(read, "BRAID_REVISION_REPORT_DERIVATION_ENABLED"));
        long? nativeLimit = ingestion.Enabled ? CatalogBusinessServices.NativeLimit(read("BRAID_NATIVE_BYTE_LIMIT")) : null;
        int? derivedLimit = reports.Enabled ? CatalogRevisionReportHostConfiguration.DerivedReadLimit(read("BRAID_DERIVED_READ_BYTE_LIMIT")) : null;
        var chat = Flag(read, "BRAID_CHAT_ENABLED");
        // Conversations pin verified reports; refuse the inconsistent profile before any delivery is opened.
        if (chat && !reports.Enabled) throw Failure();
        var previews = new CatalogPreviewOptions(Flag(read, "BRAID_PREVIEWS_ENABLED"), csvEnabled: Flag(read, CatalogPreviewOptions.CsvEnabledSetting),
            xmlEnabled: Flag(read, Braid.Application.XmlPreviewAdmission.EnabledSetting), nativeFormats: Braid.Application.NativeDocumentFormat.EnabledFormats(read, false));
        var builds = CatalogBuildHostConfiguration.ReadRuntime(read, catalogEnabled: true);
        _ = ReleaseOperationsHostConfiguration.ReadEnabled(read, catalogEnabled: true);
        // Native admission and derivation run only through a separately hosted, pinned runner; this host never runs
        // Docker. The hosted Braid.Worker stays the default; the Ferrule service is an explicit selection.
        SessionInspectionWorker? inspection = null;
        FerruleServiceEndpoint? service = null;
        AzureSessionsRunnerSettings? sessions = null;
        var nativeWork = ingestion.Enabled || reports.DerivationEnabled;
        // A supplied provenance pin must be valid even when this host only reads retained reports.
        if (nativeWork || read(FerruleRunnerSelection.CompileToolchainSetting) is not null)
        {
            try
            {
                var runner = FerruleRunnerSelection.Read(read, FerruleRunnerKind.InspectionWorker, FerruleRunnerKind.InspectionWorker,
                    FerruleRunnerKind.Service, FerruleRunnerKind.AzureContainerAppsSessions);
                _ = FerruleRunnerSelection.ReadCompileToolchain(read, runner);
                if (nativeWork)
                {
                    // Settings of a runner that is not selected are ambiguous, not ignored.
                    var worker = read("BRAID_INSPECTION_WORKER_URL") is not null || read("BRAID_INSPECTION_WORKER_CERTIFICATE_SHA256") is not null;
                    if ((runner != FerruleRunnerKind.InspectionWorker && worker) ||
                        (runner != FerruleRunnerKind.Service && FerruleServiceEndpoint.Configured(read)) ||
                        (runner != FerruleRunnerKind.AzureContainerAppsSessions && AzureSessionsRunnerSettings.Configured(read)))
                        throw Failure();
                    if (runner == FerruleRunnerKind.Service) service = FerruleServiceEndpoint.Read(read);
                    else if (runner == FerruleRunnerKind.AzureContainerAppsSessions) sessions = AzureSessionsRunnerSettings.Read(read);
                    else inspection = SessionInspectionWorker.Read(read);
                }
            }
            catch (Exception error) when (SessionHostResources.Nonfatal(error)) { throw Failure(); }
        }
        var bearer = ReadBearer(read, tenant, client);
        return new(mode, address, publicOrigin, proxy, knownProxy, organization, tenant, client, application, directory)
        {
            Bearer = bearer,
            Metadata = metadata,
            Ingestion = ingestion,
            Creation = creation,
            Reports = reports,
            Chat = chat,
            Previews = previews,
            Builds = builds,
            NativeByteLimit = nativeLimit,
            DerivedReadByteLimit = derivedLimit,
            InspectionWorker = inspection,
            FerruleService = service,
            FerruleSessions = sessions
        };
    }

    internal const string BearerEnabledSetting = "BRAID_API_BEARER_ENABLED";
    internal const string BearerAuditSetting = "BRAID_API_AUDIT_WRITER_CONNECTION_STRING";
    private static readonly string[] BearerSettings =
        ["BRAID_API_BEARER_AUDIENCE", "BRAID_API_BEARER_SCOPE", "BRAID_API_BEARER_CLIENT_IDS", BearerAuditSetting, BearerAuditSetting + "_FILE"];

    /// <summary>
    /// Bearer access is off unless explicitly enabled with all of its settings; any partial or mixed setting refuses.
    /// The audience is the Session app registration that exposes the scope, so it must be the configured client.
    /// </summary>
    private static EntraUserTokenOptions? ReadBearer(Func<string, string?> read, Guid tenant, Guid client)
    {
        if (!Flag(read, BearerEnabledSetting))
        {
            if (BearerSettings.Any(name => read(name) is not null)) throw Failure();
            return null;
        }
        // The audit login is delivered only as a protected file; the plain setting refuses startup.
        if (read(BearerAuditSetting) is not null || read(BearerAuditSetting + "_FILE") is not { Length: > 0 }) throw Failure();
        var audience = Id(read, "BRAID_API_BEARER_AUDIENCE");
        var scope = Required(read, "BRAID_API_BEARER_SCOPE");
        var clients = Required(read, "BRAID_API_BEARER_CLIENT_IDS").Split(',');
        if (audience != client || !EntraUserTokenOptions.IsScope(scope) || clients.Length > EntraUserTokenOptions.MaximumClients)
            throw Failure();
        var ids = new List<Guid>();
        foreach (var text in clients)
        {
            if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D")) throw Failure();
            ids.Add(id);
        }
        try { return new EntraUserTokenOptions(tenant, audience, scope, ids); }
        catch (ArgumentException) { throw Failure(); }
    }

    internal static bool Flag(Func<string, string?> read, string name) => read(name) switch
    {
        null or "false" => false,
        "true" => true,
        _ => throw Failure()
    };

    internal static string Required(Func<string, string?> read, string name) =>
        read(name) is { Length: > 0 } value ? value : throw Failure();

    private static Guid Id(Func<string, string?> read, string name)
    {
        var text = Required(read, name);
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D"))
            throw Failure();
        return id;
    }

    internal static string LoopbackOrigin(IPEndPoint listener)
    {
        var host = listener.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "[" + listener.Address + "]" : listener.Address.ToString();
        return "http://" + host + (listener.Port == 80 ? "" : ":" + listener.Port.ToString(CultureInfo.InvariantCulture));
    }

    internal static InvalidOperationException Failure() => new("The server host profile is invalid.");
    public override string ToString() => nameof(ServerHostOptions);
}
