using Microsoft.Extensions.DependencyInjection;

namespace Braid.Api.Catalog;

public sealed record CatalogRegressionRunOptions(bool Enabled = false);

public static class CatalogRegressionRunRegistration
{
    public static IServiceCollection AddBraidRegressionRuns(this IServiceCollection services, CatalogRegressionRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(options ?? new());
        return services;
    }
}
