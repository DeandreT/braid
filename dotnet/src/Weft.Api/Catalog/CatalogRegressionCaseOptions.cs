using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Application.Regression;

namespace Weft.Api.Catalog;

/// <summary>Explicit server-owned selection. A configured runtime is not evidence that a case executed.</summary>
public sealed record CatalogRegressionCaseOptions
{
    public CatalogRegressionCaseOptions(bool enabled = false, RegressionRuntimeIdentity? runtime = null)
    {
        if (enabled && (runtime is null || runtime.Profile != PreviewPolicy.Profile))
            throw new ArgumentException("Regression cases require an explicit supported runtime selection.");
        Enabled = enabled;
        Runtime = runtime;
    }
    public bool Enabled { get; }
    public RegressionRuntimeIdentity? Runtime { get; }
}

public static class CatalogRegressionCaseRegistration
{
    public static IServiceCollection AddWeftRegressionCases(this IServiceCollection services, CatalogRegressionCaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(options ?? new CatalogRegressionCaseOptions());
        return services;
    }
}
