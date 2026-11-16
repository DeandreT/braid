using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Deployment;
using Braid.Persistence.Postgres;
using Braid.Server;
using Braid.Storage.FileSystem;
using Braid.Storage.Hosting;

namespace Braid.Tests;

public sealed class CsvSourceCompositionTests
{
    [Theory]
    [InlineData("development")]
    [InlineData("session")]
    public void Standalone_csv_regression_without_source_ports_fails_before_regression_credentials(string mode)
    {
        var requested = new List<string>();
        var values = Settings(); values["BRAID_CATALOG_AUTH_MODE"] = mode;
        var error = Assert.Throws<InvalidOperationException>(() => Configure(new ServiceCollection(), name =>
        {
            requested.Add(name);
            return values.GetValueOrDefault(name);
        }));
        Assert.Contains("IPrivateObjectStore and IRevisionReportMetadataReader", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(requested, name => name.StartsWith("BRAID_REGRESSION_", StringComparison.Ordinal));
    }

    [Fact]
    public void Standalone_csv_regression_uses_registered_sources_without_enabling_builds_or_reports()
    {
        var services = new ServiceCollection();
        var resolved = 0;
        CsvSourceTestServices.Register(services, () => resolved++);
        var values = Settings();
        Configure(services, values.GetValueOrDefault);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<CatalogRegressionCaseOptions>();
        Assert.NotNull(options.CsvRuntime);
        Assert.Equal(0, resolved);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ICsvBuildBoundaryReader) ||
            descriptor.ServiceType == typeof(IRevisionReportReader) || descriptor.ServiceType == typeof(IMappingPreviewBackend));
    }

    [Fact]
    public void Csv_build_aliases_are_registered_before_regression_source_admission()
    {
        var values = Settings();
        values["BRAID_BUILDS_ENABLED"] = "true";
        values["BRAID_BUILDS_CSV_ENABLED"] = "true";
        values["BRAID_PRIVATE_STORAGE_ROOT"] = "/configuration-only/csv-source";
        var services = new ServiceCollection();
        Configure(services, name => values.GetValueOrDefault(name) ??
            (name.StartsWith("BRAID_BUILD_", StringComparison.Ordinal) && name.EndsWith("_CONNECTION_STRING", StringComparison.Ordinal)
                ? "configuration-only" : null));
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<CatalogRegressionCaseOptions>().CsvRuntime);
        Assert.NotNull(provider.GetRequiredService<CatalogBuildOptions>().CsvProfile);
        Assert.Single(services, item => !item.IsKeyedService && item.ServiceType == typeof(IPrivateObjectStore));
        Assert.Single(services, item => !item.IsKeyedService && item.ServiceType == typeof(IRevisionReportMetadataReader));
        Assert.DoesNotContain(services, item => !item.IsKeyedService && item.ServiceType == typeof(IRevisionReportReader));
    }

    [Fact]
    public void Csv_preview_registration_requires_sources_but_legacy_registration_does_not()
    {
        var missing = new ServiceCollection();
        missing.AddBraidCatalog(new CatalogApiOptions(), new Environment());
        Assert.Throws<InvalidOperationException>(() => missing.AddBraidPreviews(new(true, csvEnabled: true)));
        Assert.DoesNotContain(missing, item => item.ServiceType == typeof(CatalogPreviewOptions));
        missing.AddBraidPreviews(new(true));
        Assert.False(((CatalogPreviewOptions)missing.Single(item => item.ServiceType == typeof(CatalogPreviewOptions)).ImplementationInstance!).CsvEnabled);

        var present = new ServiceCollection();
        present.AddBraidCatalog(new CatalogApiOptions(), new Environment());
        var resolved = 0;
        CsvSourceTestServices.Register(present, () => resolved++);
        present.AddBraidPreviews(new(true, csvEnabled: true));
        using var provider = present.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<CatalogPreviewOptions>().CsvEnabled);
        Assert.Equal(0, resolved);
    }

    private static void Configure(IServiceCollection services, Func<string, string?> read) =>
        CatalogBusinessServices.Configure(services, new Environment(), new(), new(), new(), new(), read);

    private static Dictionary<string, string> Settings() => new()
    {
        ["BRAID_CATALOG_CONNECTION_STRING"] = "Host=127.0.0.1;Database=braid;Username=braid_catalog_reader;Password=configuration-only",
        ["BRAID_REGRESSIONS_ENABLED"] = "true",
        [CsvDocumentPolicy.RegressionEnabledSetting] = "true",
        ["BRAID_BUILDS_ENABLED"] = "false",
        ["BRAID_BUILDS_CSV_ENABLED"] = "false",
        ["BRAID_REVISION_REPORTS_ENABLED"] = "false",
        ["BRAID_CATALOG_AUTH_MODE"] = "development",
        ["BRAID_FERRULE_WORKER_IMAGE"] = "sha256:" + new string('a', 64),
        ["BRAID_REGRESSION_CASE_READER_CONNECTION_STRING"] = "configuration-only",
        ["BRAID_REGRESSION_CASE_WRITER_CONNECTION_STRING"] = "configuration-only",
        ["BRAID_REGRESSION_RUN_READER_CONNECTION_STRING"] = "configuration-only",
        ["BRAID_REGRESSION_RUN_WRITER_CONNECTION_STRING"] = "configuration-only"
    };

    private sealed class Environment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Braid.Server";
        public string WebRootPath { get; set; } = Path.GetFullPath("wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetFullPath(".");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

internal static class CsvSourceTestServices
{
    internal static void Register(IServiceCollection services, Action? resolved = null)
    {
        services.AddSingleton(_ => new PostgresRevisionReportDatabase(
            "Host=127.0.0.1;Database=braid;Username=braid_report_reader;Password=configuration-only"));
        services.AddSingleton<IRevisionReportMetadataReader>(provider =>
        {
            resolved?.Invoke();
            return new PostgresRevisionReportMetadataReader(provider.GetRequiredService<PostgresRevisionReportDatabase>());
        });
        services.AddSingleton<IPrivateObjectStore>(_ =>
        {
            resolved?.Invoke();
            return new LazyPrivateObjectStore(new FileSystemStorageOptions("/configuration-only/private", "/configuration-only/application"));
        });
    }
}
