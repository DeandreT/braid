using Microsoft.Extensions.DependencyInjection;
using Braid.Identity;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    /// <summary>
    /// Adds a dormant session-backed catalog after AddBraidBrowserSessions on the same collection.
    /// The host owns the browser-session composition; this registration neither replaces nor disposes it.
    /// </summary>
    public static IServiceCollection AddBraidSessionCatalog(this IServiceCollection services,
        CatalogBrowserSessionOptions options, CatalogMetadataEditOptions metadata,
        CatalogRevisionIngestionOptions ingestion, CatalogProjectCreationOptions creation,
        CatalogRevisionReportOptions reports)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(reports);
        RequireUnregisteredCatalog(services);
        var browsers = services.Where(item => item.ServiceType == typeof(ServerSessionBrowser)).ToArray();
        if (browsers.Length != 1 || browsers[0].IsKeyedService || browsers[0].Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("Session catalog requires one prior browser-session singleton registration.");
        services.AddSingleton(options);
        RegisterCatalogFeatures(services, metadata, ingestion, creation, reports);
        services.AddSingleton(provider => new CatalogRequestBoundary(options,
            provider.GetRequiredService<ServerSessionBrowser>(), provider.GetService<Auth.CatalogBearerAdmission>()));
        return services;
    }

    private static void RequireUnregisteredCatalog(IServiceCollection services)
    {
        if (services.Any(item => item.ServiceType == typeof(CatalogApiOptions) ||
            item.ServiceType == typeof(CatalogBrowserSessionOptions) || item.ServiceType == typeof(CatalogRequestBoundary)))
            throw new InvalidOperationException("The catalog identity boundary must be registered exactly once.");
    }

    private static void RegisterCatalogFeatures(IServiceCollection services,
        CatalogMetadataEditOptions metadata, CatalogRevisionIngestionOptions ingestion,
        CatalogProjectCreationOptions creation, CatalogRevisionReportOptions reports)
    {
        services.AddSingleton(metadata);
        services.AddSingleton(ingestion);
        services.AddSingleton(creation);
        services.AddSingleton(reports);
        services.AddSingleton<CatalogRevisionIngestionGate>();
        services.AddSingleton<CatalogProjectCreationGate>();
        services.AddSingleton(new CatalogInterchangeOptions());
        services.AddSingleton<CatalogInterchangeGate>();
    }
}
