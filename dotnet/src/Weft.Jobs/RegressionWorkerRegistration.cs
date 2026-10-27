using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Application.Automation;
using Weft.Application.Regression;
using Weft.Persistence.Postgres;

namespace Weft.Jobs;

/// <summary>Explicit worker-only composition. Disabled means no extra credentials, pools or background service.</summary>
public static class RegressionWorkerRegistration
{
    private const string Key = "regression-worker";

    public static IServiceCollection AddWeftRegressionWorker(this IServiceCollection services,
        Func<string, string?> environment, Func<RegressionRuntimeIdentity> runtime, Func<RegressionRuntimeIdentity>? documentRuntime = null,
        Func<string, int, string>? privateReader = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(runtime);
        var enabled = environment("WEFT_REGRESSION_WORKER_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("Regression worker enablement must be true or false.")
        };
        if (!enabled) return services;
        string Required(string name) => environment(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Regression worker requires {name}.");
        var run = Required("WEFT_REGRESSION_RUN_WRITER_CONNECTION_STRING");
        var cases = Required("WEFT_REGRESSION_CASE_READER_CONNECTION_STRING");
        var access = Required("WEFT_REGRESSION_ACCESS_CONNECTION_STRING");
        var reports = Required("WEFT_REGRESSION_REPORT_READER_CONNECTION_STRING");
        var comparisons = Required("WEFT_AUTOMATION_COMPARISON_CONNECTION_STRING");
        var identity = runtime();
        if (identity is null || identity.BackendId != "ferrule" || identity.Profile != PreviewPolicy.Profile)
            throw new ArgumentException("Regression worker runtime is unavailable.");
        var documentIdentity = documentRuntime?.Invoke();
        if (documentIdentity is not null && (documentIdentity.Profile != DocumentPreviewPolicy.Profile || documentIdentity.BackendId != identity.BackendId ||
            documentIdentity.EngineRevision != identity.EngineRevision || documentIdentity.BackendBuild != identity.BackendBuild))
            throw new ArgumentException("Document regression worker runtime is unavailable.");

        services.AddKeyedSingleton<PostgresDatabase>(Key, (_, _) => new(access));
        services.AddKeyedSingleton<PostgresRevisionReportDatabase>(Key, (_, _) => new(reports));
        services.AddSingleton<IRegressionRunQueue>(_ => new PostgresRegressionRunQueue(run));
        services.AddSingleton<IRegressionRunStore>(_ => new PostgresRegressionRunStore(run, writer: true));
        services.AddSingleton<IRegressionCaseReader>(_ => new PostgresRegressionCaseStore(cases, RegressionCaseDatabaseProfile.Reader));
        services.AddSingleton<RegressionCaseExecutionService>(provider => new RegressionCaseExecutionService(
            provider.GetRequiredService<IRegressionCaseReader>(),
            new PostgresProjectReader(provider.GetRequiredKeyedService<PostgresDatabase>(Key)),
            new PostgresRevisionReportMetadataReader(provider.GetRequiredKeyedService<PostgresRevisionReportDatabase>(Key)),
            provider.GetRequiredService<IPrivateObjectStore>(), provider.GetRequiredService<IMappingPreviewBackend>(), identity, documentIdentity));
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
