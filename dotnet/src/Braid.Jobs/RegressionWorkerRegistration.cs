using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Application.Automation;
using Braid.Application.Regression;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;

namespace Braid.Jobs;

internal sealed record RegressionWorkerRuntimes(RegressionRuntimeIdentity Legacy, RegressionRuntimeIdentity? Document, RegressionRuntimeIdentity? Csv, RegressionRuntimeIdentity? Xml);

/// <summary>Explicit worker-only composition. Disabled means no extra credentials, pools or background service.</summary>
public static class RegressionWorkerRegistration
{
    private const string Key = "regression-worker";
    public const string PreviewBackendKey = "regression-preview-backend";

    public static IServiceCollection AddBraidRegressionWorker(this IServiceCollection services,
        Func<string, string?> environment, Func<RegressionRuntimeIdentity> runtime, Func<RegressionRuntimeIdentity>? documentRuntime = null,
        Func<string, int, string>? privateReader = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(runtime);
        var enabled = environment("BRAID_REGRESSION_WORKER_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("Regression worker enablement must be true or false.")
        };
        var xmlEnabled = XmlPreviewAdmission.Enabled(environment, XmlPreviewAdmission.RegressionEnabledSetting, enabled);
        if (!enabled) return services;
        var csvEnabled = environment(CsvDocumentPolicy.RegressionEnabledSetting) switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("CSV regression enablement must be true or false.")
        };
        string Required(string name) => environment(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Regression worker requires {name}.");
        var run = Required("BRAID_REGRESSION_RUN_WRITER_CONNECTION_STRING");
        var cases = Required("BRAID_REGRESSION_CASE_READER_CONNECTION_STRING");
        var access = Required("BRAID_REGRESSION_ACCESS_CONNECTION_STRING");
        var reports = Required("BRAID_REGRESSION_REPORT_READER_CONNECTION_STRING");
        var comparisons = Required("BRAID_AUTOMATION_COMPARISON_CONNECTION_STRING");
        var identity = runtime();
        if (identity is null || identity.BackendId != "ferrule" || identity.Profile != PreviewPolicy.Profile)
            throw new ArgumentException("Regression worker runtime is unavailable.");
        var documentIdentity = documentRuntime?.Invoke();
        if (documentIdentity is not null && (documentIdentity.Profile != DocumentPreviewPolicy.Profile || documentIdentity.BackendId != identity.BackendId ||
            documentIdentity.EngineRevision != identity.EngineRevision || documentIdentity.BackendBuild != identity.BackendBuild))
            throw new ArgumentException("Document regression worker runtime is unavailable.");
        RegressionRuntimeIdentity? csvIdentity = null;
        if (csvEnabled)
        {
            if (identity.EngineRevision != FerruleInspectionBackend.EngineRevision || identity.AdapterBuild != FerrulePreviewBackend.AdapterBuild)
                throw new ArgumentException("CSV regression requires the installed Ferrule preview identity.");
            _ = InspectionTransport.RequireImage(identity.BackendBuild);
            csvIdentity = new(identity.BackendId, identity.EngineRevision, identity.BackendBuild, FerrulePreviewBackend.CsvAdapterBuild, CsvDocumentPolicy.Profile);
        }
        var xmlIdentity = xmlEnabled ? new RegressionRuntimeIdentity(identity.BackendId, identity.EngineRevision, identity.BackendBuild,
            FerrulePreviewBackend.XmlAdapterBuild, XmlPreviewAdmission.Profile) : null;
        services.AddSingleton(new RegressionWorkerRuntimes(identity, documentIdentity, csvIdentity, xmlIdentity));

        services.AddKeyedSingleton<PostgresDatabase>(Key, (_, _) => new(access));
        services.AddKeyedSingleton<PostgresRevisionReportDatabase>(Key, (_, _) => new(reports));
        services.AddSingleton<IRegressionRunQueue>(_ => new PostgresRegressionRunQueue(run));
        services.AddSingleton<IRegressionRunStore>(_ => new PostgresRegressionRunStore(run, writer: true));
        services.AddSingleton<IRegressionCaseReader>(_ => new PostgresRegressionCaseStore(cases, RegressionCaseDatabaseProfile.Reader));
        services.AddSingleton<RegressionCaseExecutionService>(provider => new RegressionCaseExecutionService(
            provider.GetRequiredService<IRegressionCaseReader>(),
            new PostgresProjectReader(provider.GetRequiredKeyedService<PostgresDatabase>(Key)),
            new PostgresRevisionReportMetadataReader(provider.GetRequiredKeyedService<PostgresRevisionReportDatabase>(Key)),
            provider.GetRequiredService<IPrivateObjectStore>(),
            provider.GetKeyedService<IMappingPreviewBackend>(PreviewBackendKey) ?? provider.GetRequiredService<IMappingPreviewBackend>(),
            identity, documentIdentity, csvIdentity, xmlIdentity, NativeDocumentFormat.EnabledFormats(environment, true).Select(f => new RegressionRuntimeIdentity(identity.BackendId, identity.EngineRevision, identity.BackendBuild, f.AdapterBuild, f.Profile)).ToArray()));
        services.AddSingleton<IRegressionCaseExecutor>(provider => provider.GetRequiredService<RegressionCaseExecutionService>());
        services.AddSingleton<IRegressionEvaluationExecutor>(provider => provider.GetRequiredService<RegressionCaseExecutionService>());
        services.AddSingleton<IAutomationQueue>(_ => new PostgresAutomationStore(run, writer: true));
        services.AddSingleton<IAutomationComparisonSettler>(_ => new PostgresAutomationComparisonSettler(comparisons, documentIdentity));
        var notificationConfiguration = NotificationConfigurationFile.Read(environment, privateReader);
        services.AddSingleton<INotificationTransport>(_ => new NotificationTransport(notificationConfiguration, TimeProvider.System));
        services.AddHostedService<AutomationWorker>();
        services.AddSingleton<IRegressionRunDispatcher>(provider => new RegressionRunExecutor(
            provider.GetRequiredService<IRegressionRunStore>(), provider.GetRequiredService<IRegressionCaseExecutor>()));
        services.AddSingleton<RegressionRunPump>();
        services.AddHostedService<RegressionWorkerService>();
        return services;
    }
}
