using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Jobs;

/// <summary>Independent lazy preview/regression admission over the same bounded transport.</summary>
public static class PreviewBackendRegistration
{
    public static IServiceCollection AddWeftPreviewBackends(this IServiceCollection services,
        Func<IServiceProvider, IInspectionWorkerRunner> runner, bool xmlPreview = false, bool xmlRegression = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runner);
        services.AddSingleton<IMappingPreviewBackend>(provider => new FerrulePreviewBackend(runner(provider),
            PreviewExecutionProfile.LegacyJson, xmlEnabled: xmlPreview));
        services.AddKeyedSingleton<IMappingPreviewBackend>(RegressionWorkerRegistration.PreviewBackendKey,
            (provider, _) => new FerrulePreviewBackend(runner(provider), PreviewExecutionProfile.LegacyJson, xmlEnabled: xmlRegression));
        return services;
    }
}
