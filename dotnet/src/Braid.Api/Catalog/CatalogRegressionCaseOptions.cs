using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Application.Regression;

namespace Braid.Api.Catalog;

/// <summary>Explicit server-owned selection. A configured runtime is not evidence that a case executed.</summary>
public sealed record CatalogRegressionCaseOptions
{
    public CatalogRegressionCaseOptions(bool enabled = false, RegressionRuntimeIdentity? runtime = null, RegressionRuntimeIdentity? documentRuntime = null,
        RegressionRuntimeIdentity? csvRuntime = null, RegressionRuntimeIdentity? xmlRuntime = null, IReadOnlyList<RegressionRuntimeIdentity>? nativeRuntimes = null)
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
        if (xmlRuntime is not null && (!enabled || runtime is null || xmlRuntime.Profile != XmlPreviewAdmission.Profile ||
            xmlRuntime.BackendId != runtime.BackendId || xmlRuntime.EngineRevision != runtime.EngineRevision || xmlRuntime.BackendBuild != runtime.BackendBuild))
            throw new ArgumentException("XML cases require an enabled, separately pinned runtime.");
        NativeRuntimes = nativeRuntimes?.Where(r => r.Profile != NativeDocumentFormat.Xlsx.Profile).ToArray() ?? [];
        if (NativeRuntimes.Any(r => !enabled || runtime is null || NativeDocumentFormat.FromProfile(r.Profile)?.AdapterBuild != r.AdapterBuild || r.BackendBuild != runtime.BackendBuild || r.BackendId != runtime.BackendId || r.EngineRevision != runtime.EngineRevision)) throw new ArgumentException("Invalid native runtime.");
        XmlRuntime = xmlRuntime;
    }
    public IReadOnlyList<RegressionRuntimeIdentity> NativeRuntimes { get; }
    public bool Enabled { get; }
    public RegressionRuntimeIdentity? Runtime { get; }
    public RegressionRuntimeIdentity? DocumentRuntime { get; }
    public RegressionRuntimeIdentity? CsvRuntime { get; }
    public RegressionRuntimeIdentity? XmlRuntime { get; }

    internal RegressionRuntimeIdentity? SelectRuntime(RegressionRuntimeIdentity candidate) => !Enabled ? null :
        candidate == Runtime ? Runtime : candidate == DocumentRuntime ? DocumentRuntime : candidate == CsvRuntime ? CsvRuntime :
        candidate == XmlRuntime ? XmlRuntime : NativeRuntimes.FirstOrDefault(r => r == candidate);
}

public static class CatalogRegressionCaseRegistration
{
    public static IServiceCollection AddBraidRegressionCases(this IServiceCollection services, CatalogRegressionCaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (options is { Enabled: true } && (options.CsvRuntime is not null || options.XmlRuntime is not null || options.NativeRuntimes.Count != 0))
            CatalogPreviewSourceAdmission.RequireNativeSources(services);
        services.AddSingleton(options ?? new CatalogRegressionCaseOptions());
        return services;
    }
}
