using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Application.Regression;

namespace Weft.Api.Catalog;

/// <summary>Explicit server-owned selection. A configured runtime is not evidence that a case executed.</summary>
public sealed record CatalogRegressionCaseOptions
{
    public CatalogRegressionCaseOptions(bool enabled = false, RegressionRuntimeIdentity? runtime = null, RegressionRuntimeIdentity? documentRuntime = null,
        RegressionRuntimeIdentity? csvRuntime = null)
    {
        if (enabled && (runtime is null || runtime.Profile != PreviewPolicy.Profile))
            throw new ArgumentException("Regression cases require an explicit supported runtime selection.");
        Enabled = enabled;
        Runtime = runtime;
        if (documentRuntime is not null && (runtime is null || documentRuntime.Profile != DocumentPreviewPolicy.Profile ||
            documentRuntime.BackendId != runtime.BackendId || documentRuntime.EngineRevision != runtime.EngineRevision || documentRuntime.BackendBuild != runtime.BackendBuild))
            throw new ArgumentException("Document cases require a separately pinned supported runtime selection.");
        DocumentRuntime = documentRuntime;
        if (csvRuntime is not null && (runtime is null || csvRuntime.Profile != CsvDocumentPolicy.Profile ||
            csvRuntime.BackendId != runtime.BackendId || csvRuntime.EngineRevision != runtime.EngineRevision || csvRuntime.BackendBuild != runtime.BackendBuild))
            throw new ArgumentException("CSV cases require a separately pinned supported runtime selection.");
        CsvRuntime = csvRuntime;
    }
    public bool Enabled { get; }
    public RegressionRuntimeIdentity? Runtime { get; }
    public RegressionRuntimeIdentity? DocumentRuntime { get; }
    public RegressionRuntimeIdentity? CsvRuntime { get; }

    internal RegressionRuntimeIdentity? SelectRuntime(RegressionRuntimeIdentity candidate) => !Enabled ? null :
        candidate == Runtime ? Runtime : candidate == DocumentRuntime ? DocumentRuntime : candidate == CsvRuntime ? CsvRuntime : null;
}

public static class CatalogRegressionCaseRegistration
{
    public static IServiceCollection AddWeftRegressionCases(this IServiceCollection services, CatalogRegressionCaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (options is { Enabled: true, CsvRuntime: not null }) CatalogPreviewSourceAdmission.RequireCsvSources(services);
        services.AddSingleton(options ?? new CatalogRegressionCaseOptions());
        return services;
    }
}
