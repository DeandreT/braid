using Microsoft.Extensions.DependencyInjection;

namespace Weft.Api.Catalog;

public sealed record CatalogRegressionRunOptions(bool Enabled = false);

public static class CatalogRegressionRunRegistration
{
    public static IServiceCollection AddWeftRegressionRuns(this IServiceCollection services, CatalogRegressionRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(options ?? new());
        return services;
    }
}
