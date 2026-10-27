using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Application.Regression;

namespace Weft.Api.Catalog;

/// <summary>Explicit server-owned selection. A configured runtime is not evidence that a case executed.</summary>
public sealed record CatalogRegressionCaseOptions
{
    public CatalogRegressionCaseOptions(bool enabled = false, RegressionRuntimeIdentity? runtime = null, RegressionRuntimeIdentity? documentRuntime = null)
    {
        if (enabled && (runtime is null || runtime.Profile != PreviewPolicy.Profile))
            throw new ArgumentException("Regression cases require an explicit supported runtime selection.");
        Enabled = enabled;
        Runtime = runtime;
        if (documentRuntime is not null && (runtime is null || documentRuntime.Profile != DocumentPreviewPolicy.Profile ||
            documentRuntime.BackendId != runtime.BackendId || documentRuntime.EngineRevision != runtime.EngineRevision || documentRuntime.BackendBuild != runtime.BackendBuild))
            throw new ArgumentException("Document cases require a separately pinned supported runtime selection.");
        DocumentRuntime = documentRuntime;
    }
    public bool Enabled { get; }
    public RegressionRuntimeIdentity? Runtime { get; }
    public RegressionRuntimeIdentity? DocumentRuntime { get; }

    internal RegressionRuntimeIdentity? SelectRuntime(RegressionRuntimeIdentity candidate) => !Enabled ? null :
        candidate == Runtime ? Runtime : candidate == DocumentRuntime ? DocumentRuntime : null;
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
