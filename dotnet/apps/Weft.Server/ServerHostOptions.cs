using System.Globalization;
using System.Net;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Identity;

using Weft.MappingBackends.Workers;

namespace Weft.Server;

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

    internal static ServerHostOptions Read(Func<string, string?> read)
    {
        var enabled = Flag(read, "WEFT_CATALOG_ENABLED");
        var mode = read("WEFT_CATALOG_AUTH_MODE") switch
        {
            null => enabled ? AuthenticationHttpMode.Development : AuthenticationHttpMode.Disabled,
            "disabled" when !enabled => AuthenticationHttpMode.Disabled,
            "development" when enabled => AuthenticationHttpMode.Development,
            "session" when enabled => AuthenticationHttpMode.Session,
            _ => throw Failure()
        };
        if (mode != AuthenticationHttpMode.Session)
        {
            var listener = DevelopmentListenAddress.Parse(read("WEFT_DEV_LISTEN_ADDR"));
            var origin = mode == AuthenticationHttpMode.Development
                ? Required(read, "WEFT_DEV_BROWSER_ORIGIN") : LoopbackOrigin(listener);
            return new(mode, listener, origin, false, null, default, default, default, null, null)
            {
                Chat = Flag(read, "WEFT_CHAT_ENABLED")
            };
        }
        if (Flag(read, "WEFT_DEV_IDENTITY_ENABLED") || new[]
            { "WEFT_DEV_PRINCIPAL_ID", "WEFT_DEV_ORGANIZATION_ID", "WEFT_DEV_BROWSER_ORIGIN", "WEFT_DEV_LISTEN_ADDR" }
            .Any(name => read(name) is not null)) throw Failure();
        var publicOrigin = Required(read, "WEFT_PUBLIC_ORIGIN");
        _ = new ServerSessionBrowserOptions(publicOrigin);
        var tenant = Id(read, "WEFT_ENTRA_TENANT_ID");
        var client = Id(read, "WEFT_ENTRA_CLIENT_ID");
        _ = new EntraClientOptions(tenant, client, publicOrigin);
        var configuredListener = Required(read, "WEFT_LISTEN_ADDR");
        if (!IPEndPoint.TryParse(configuredListener, out var address) || address.Port == 0 ||
            configuredListener != address.ToString() || address.Address.Equals(IPAddress.Any) ||
            address.Address.Equals(IPAddress.IPv6Any)) throw Failure();
        var proxy = Required(read, "WEFT_HOST_TRANSPORT") switch
        {
            "directHttps" => false,
            "trustedProxyOneHop" => true,
            _ => throw Failure()
        };
        IPAddress? knownProxy = null;
        if (proxy)
        {
            var configuredProxy = Required(read, "WEFT_KNOWN_PROXY_IP");
            if (!IPAddress.IsLoopback(address.Address) ||
                !IPAddress.TryParse(configuredProxy, out knownProxy) || !IPAddress.IsLoopback(knownProxy) ||
                configuredProxy != knownProxy.ToString()) throw Failure();
            if (read("WEFT_TLS_CERTIFICATE_FILE") is not null || read("WEFT_TLS_PRIVATE_KEY_FILE") is not null)
                throw Failure();
        }
        else if (read("WEFT_KNOWN_PROXY_IP") is not null || new Uri(publicOrigin).Port != address.Port)
            throw Failure();
        var organization = Id(read, "WEFT_SESSION_ORGANIZATION_ID");
        var application = Required(read, "WEFT_SESSION_APPLICATION_NAME");
        var directory = Required(read, "WEFT_SESSION_KEY_DIRECTORY");
        _ = new ServerSessionKeyProfileOptions(organization, application, directory);
        // Snapshot the selected business scalars before any PEM, ring or credential delivery is opened.
        var metadata = new CatalogMetadataEditOptions(Flag(read, "WEFT_METADATA_EDITS_ENABLED"));
        var ingestion = new CatalogRevisionIngestionOptions(Flag(read, "WEFT_REVISION_INGESTION_ENABLED"));
        var creation = new CatalogProjectCreationOptions(Flag(read, "WEFT_PROJECT_CREATION_ENABLED"));
        var reports = new CatalogRevisionReportOptions(Flag(read, "WEFT_REVISION_REPORTS_ENABLED"),
            Flag(read, "WEFT_REVISION_REPORT_DERIVATION_ENABLED"));
        long? nativeLimit = ingestion.Enabled ? CatalogBusinessServices.NativeLimit(read("WEFT_NATIVE_BYTE_LIMIT")) : null;
        int? derivedLimit = reports.Enabled ? CatalogRevisionReportHostConfiguration.DerivedReadLimit(read("WEFT_DERIVED_READ_BYTE_LIMIT")) : null;
        var chat = Flag(read, "WEFT_CHAT_ENABLED");
        // Conversations pin verified reports; refuse the inconsistent profile before any delivery is opened.
        if (chat && !reports.Enabled) throw Failure();
        var previews = new CatalogPreviewOptions(Flag(read, "WEFT_PREVIEWS_ENABLED"));
        var builds = CatalogBuildHostConfiguration.ReadRuntime(read, catalogEnabled: true);
        // Native admission and derivation run only through a separately hosted, pinned runner; this host never runs
        // Docker. The hosted Weft.Worker stays the default; the Ferrule service is an explicit selection.
        SessionInspectionWorker? inspection = null;
        FerruleServiceEndpoint? service = null;
        if (ingestion.Enabled || reports.DerivationEnabled)
        {
            try
            {
                if (FerruleRunnerSelection.Read(read, FerruleRunnerKind.InspectionWorker, FerruleRunnerKind.InspectionWorker,
                    FerruleRunnerKind.Service) == FerruleRunnerKind.Service)
                {
                    if (read("WEFT_INSPECTION_WORKER_URL") is not null || read("WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256") is not null)
                        throw Failure();
                    service = FerruleServiceEndpoint.Read(read);
                }
                else
                {
                    if (FerruleServiceEndpoint.Configured(read)) throw Failure();
                    inspection = SessionInspectionWorker.Read(read);
                }
            }
            catch (Exception error) when (SessionHostResources.Nonfatal(error)) { throw Failure(); }
        }
        return new(mode, address, publicOrigin, proxy, knownProxy, organization, tenant, client, application, directory)
        {
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
            FerruleService = service
        };
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
