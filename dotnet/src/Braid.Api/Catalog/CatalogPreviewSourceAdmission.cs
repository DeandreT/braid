using Microsoft.Extensions.DependencyInjection;
using Braid.Application;

namespace Braid.Api.Catalog;

/// <summary>Pure registration admission, never source resolution, credential access or worker creation.</summary>
public static class CatalogPreviewSourceAdmission
{
    public static void RequireNativeSources(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(item => !item.IsKeyedService && item.ServiceType == typeof(IPrivateObjectStore)) ||
            !services.Any(item => !item.IsKeyedService && item.ServiceType == typeof(IRevisionReportMetadataReader)))
            throw new InvalidOperationException("Document preview/regression requires registered IPrivateObjectStore and IRevisionReportMetadataReader native-source ports.");
    }

    public static void RequireCsvSources(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(item => !item.IsKeyedService && item.ServiceType == typeof(IPrivateObjectStore)) ||
            !services.Any(item => !item.IsKeyedService && item.ServiceType == typeof(IRevisionReportMetadataReader)))
            throw new InvalidOperationException("CSV preview/regression requires registered IPrivateObjectStore and IRevisionReportMetadataReader native-source ports. Configure report sources or CSV build sources, or supply the qualified ports explicitly; other features are not enabled automatically.");
    }
}
