using Microsoft.Extensions.DependencyInjection;
using Weft.Deployment;

namespace Weft.Api.Catalog;

public sealed record CatalogBuildOptions
{
    public CatalogBuildOptions(bool enabled = false, BuildBackendIdentity? backend = null, BuildRuntimeProfile? profile = null)
    {
        if (enabled && (backend is null || profile is null)) throw new ArgumentException("Build API requires an explicit trusted runtime selection.");
        Enabled = enabled; Backend = backend; Profile = profile;
    }
    public bool Enabled { get; }
    public BuildBackendIdentity? Backend { get; }
    public BuildRuntimeProfile? Profile { get; }
}

public static class CatalogBuildRegistration
{
    public static IServiceCollection AddWeftBuildApi(this IServiceCollection services, CatalogBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(options ?? new CatalogBuildOptions());
        return services;
    }
}
