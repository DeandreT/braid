using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Application.Automation;
using Weft.Application.Regression;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Server;

/// <summary>Explicit case/run API composition; execution remains owned by the separate worker.</summary>
public static class CatalogRegressionHostConfiguration
{
    public static void Configure(IServiceCollection services, Func<string, string?> environment, bool catalogEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        var enabled = environment("WEFT_REGRESSIONS_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("Regression enablement must be true or false.")
        };
        if (enabled && !catalogEnabled) throw new ArgumentException("Regression APIs require an enabled catalog.");
        var notifications = environment("WEFT_NOTIFICATIONS_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("Notification enablement must be true or false.")
        };
        if (!enabled && !notifications) return;
        if (!catalogEnabled) throw new ArgumentException("Regression and notification APIs require an enabled catalog.");
        string Required(string name) => environment(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Regression APIs require {name}.");
        var session = environment("WEFT_CATALOG_AUTH_MODE") == "session";
        string Connection(string name) => session ? SessionHostResources.ReadConnection(environment, name + "_FILE") : Required(name);
        void Automation(string connection)
        {
            var config = NotificationConfigurationFile.Read(environment, (path, maximum) => ProtectedHostFiles.ReadText(path, maximum));
            if (session && !config.Hosted) throw new ArgumentException("Hosted notification targets require hosted admission.");
            services.AddSingleton(new CatalogAutomationOptions(true));
            services.AddSingleton(config);
            services.AddSingleton<IAutomationStore>(_ => new PostgresAutomationStore(connection, writer: true));
        }
        if (!enabled)
        {
            var name = environment("WEFT_NOTIFICATION_CONNECTION_STRING") is not null || environment("WEFT_NOTIFICATION_CONNECTION_STRING_FILE") is not null
                ? "WEFT_NOTIFICATION_CONNECTION_STRING" : "WEFT_REGRESSION_RUN_WRITER_CONNECTION_STRING";
            Automation(Connection(name));
            return;
        }
        var image = Required("WEFT_FERRULE_WORKER_IMAGE");
        if (image.Length != 71 || !image.StartsWith("sha256:", StringComparison.Ordinal) || image[7..].Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Regression APIs require the exact immutable worker image.");
        image = FerruleRunnerSelection.ProfileIdentity(environment, session ? FerruleRunnerKind.InspectionWorker : FerruleRunnerKind.Docker);
        var caseReader = Connection("WEFT_REGRESSION_CASE_READER_CONNECTION_STRING");
        var caseWriter = Connection("WEFT_REGRESSION_CASE_WRITER_CONNECTION_STRING");
        var runReader = Connection("WEFT_REGRESSION_RUN_READER_CONNECTION_STRING");
        var runWriter = Connection("WEFT_REGRESSION_RUN_WRITER_CONNECTION_STRING");
        var runtime = new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, image,
            FerrulePreviewBackend.AdapterBuild, PreviewPolicy.Profile);
        services.AddWeftRegressionCases(new(true, runtime));
        services.AddWeftRegressionRuns(new(true));
        Automation(runWriter);
        services.AddSingleton<IRegressionCaseReader>(_ => new PostgresRegressionCaseStore(caseReader, RegressionCaseDatabaseProfile.Reader));
        services.AddSingleton<IRegressionCaseWriter>(_ => new PostgresRegressionCaseStore(caseWriter, RegressionCaseDatabaseProfile.Writer));
        services.AddSingleton<IRegressionRunReader>(_ => new PostgresRegressionRunStore(runReader, writer: false));
        services.AddSingleton<IRegressionRunStore>(_ => new PostgresRegressionRunStore(runWriter, writer: true));
        services.AddSingleton(provider => new RegressionRunReadService(provider.GetRequiredService<IRegressionRunReader>(), provider.GetRequiredService<IRegressionCaseReader>()));
    }
}
