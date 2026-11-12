using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Application.Regression;
using Weft.MappingBackends;
using Weft.Server;

namespace Weft.Tests;

public sealed class CatalogRegressionHostConfigurationTests
{
    [Fact]
    public void Disabled_configuration_reads_no_runtime_or_credential_inputs()
    {
        var services = new ServiceCollection();
        CatalogRegressionHostConfiguration.Configure(services, name => name is "WEFT_REGRESSIONS_ENABLED" or "WEFT_NOTIFICATIONS_ENABLED" ? null : throw new InvalidOperationException(), false);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("")]
    public void Noncanonical_flags_are_rejected_before_credential_reads(string value)
    {
        Assert.Throws<ArgumentException>(() => CatalogRegressionHostConfiguration.Configure(new ServiceCollection(),
            name => name == "WEFT_REGRESSIONS_ENABLED" ? value : throw new InvalidOperationException(), true));
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("")]
    public void Invalid_csv_flag_is_rejected_before_private_configuration(string value)
    {
        Assert.Throws<ArgumentException>(() => CatalogRegressionHostConfiguration.Configure(new ServiceCollection(), name => name switch
        {
            "WEFT_REGRESSIONS_ENABLED" => "true",
            "WEFT_NOTIFICATIONS_ENABLED" => null,
            CsvDocumentPolicy.RegressionEnabledSetting => value,
            _ => throw new InvalidOperationException("CSV admission opened private configuration.")
        }, true));
    }

    [Fact]
    public void Catalog_is_a_required_shared_prerequisite()
    {
        Assert.Throws<ArgumentException>(() => CatalogRegressionHostConfiguration.Configure(new ServiceCollection(),
            name => name == "WEFT_REGRESSIONS_ENABLED" ? "true" : throw new InvalidOperationException(), false));
    }

    [Fact]
    public void Enabled_configuration_registers_separate_reader_and_writer_ports_without_execution()
    {
        var services = new ServiceCollection();
        CatalogRegressionHostConfiguration.Configure(services, name => name switch
        {
            "WEFT_REGRESSIONS_ENABLED" => "true",
            "WEFT_FERRULE_WORKER_IMAGE" => "sha256:" + new string('a', 64),
            "WEFT_CATALOG_AUTH_MODE" => "development",
            "WEFT_NOTIFICATIONS_ENABLED" or "WEFT_NOTIFICATION_CONFIG_FILE" => null,
            "WEFT_FERRULE_RUNNER" or "WEFT_FERRULE_COMPILE_TOOLCHAIN" => null,
            CsvDocumentPolicy.RegressionEnabledSetting => null,
            _ => "private-synthetic"
        }, true);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IRegressionRunReader));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IRegressionRunStore));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IRegressionCaseExecutor));
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<CatalogRegressionRunOptions>().Enabled);
        Assert.True(provider.GetRequiredService<CatalogRegressionCaseOptions>().Enabled);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Actual_host_registration_pins_csv_only_when_explicitly_enabled(string? flag, bool enabled)
    {
        var services = new ServiceCollection();
        if (enabled) CsvSourceTestServices.Register(services);
        var image = "sha256:" + new string('a', 64);
        CatalogRegressionHostConfiguration.Configure(services, name => name switch
        {
            "WEFT_REGRESSIONS_ENABLED" => "true",
            CsvDocumentPolicy.RegressionEnabledSetting => flag,
            "WEFT_FERRULE_WORKER_IMAGE" => image,
            "WEFT_CATALOG_AUTH_MODE" => "development",
            "WEFT_NOTIFICATIONS_ENABLED" or "WEFT_NOTIFICATION_CONFIG_FILE" or "WEFT_FERRULE_RUNNER" or "WEFT_FERRULE_COMPILE_TOOLCHAIN" => null,
            _ => "private-synthetic"
        }, true);
        using var provider = services.BuildServiceProvider();
        var selected = provider.GetRequiredService<CatalogRegressionCaseOptions>();
        Assert.Equal(enabled, selected.CsvRuntime is not null);
        if (enabled) Assert.Equal(new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, image,
            FerrulePreviewBackend.CsvAdapterBuild, CsvDocumentPolicy.Profile), selected.CsvRuntime);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IRegressionCaseExecutor));
    }

    [RegressionLinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Session_configuration_uses_only_the_four_protected_file_deliveries()
    {
        using var files = new ServerHostFiles();
        var profiles = new Dictionary<string, string>();
        foreach (var (name, role) in new[] { ("CASE_READER", "weft_case_reader"), ("CASE_WRITER", "weft_case_writer"),
            ("RUN_READER", "weft_run_reader"), ("RUN_WRITER", "weft_run_writer") })
            profiles["WEFT_REGRESSION_" + name + "_CONNECTION_STRING_FILE"] = files.Text(name + ".txt",
                new NpgsqlConnectionStringBuilder { Host = "localhost", Database = "synthetic", Username = role, Password = "synthetic-not-a-secret", SslMode = SslMode.Disable }.ConnectionString);
        var requested = new List<string>();
        var services = new ServiceCollection();
        CatalogRegressionHostConfiguration.Configure(services, name =>
        {
            requested.Add(name);
            return name switch
            {
                "WEFT_REGRESSIONS_ENABLED" => "true",
                "WEFT_FERRULE_WORKER_IMAGE" => "sha256:" + new string('a', 64),
                "WEFT_CATALOG_AUTH_MODE" => "session",
                _ => profiles.GetValueOrDefault(name)
            };
        }, true);
        foreach (var name in profiles.Keys) Assert.Contains(name, requested);
        Assert.DoesNotContain(requested, name => name.EndsWith("_CONNECTION_STRING", StringComparison.Ordinal));
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<CatalogRegressionRunOptions>().Enabled);
    }

    [RegressionLinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Session_csv_configuration_reuses_registered_sources_without_resolving_them()
    {
        using var files = new ServerHostFiles();
        var values = new Dictionary<string, string>();
        foreach (var (name, role) in new[] { ("CASE_READER", "weft_case_reader"), ("CASE_WRITER", "weft_case_writer"),
            ("RUN_READER", "weft_run_reader"), ("RUN_WRITER", "weft_run_writer") })
            values["WEFT_REGRESSION_" + name + "_CONNECTION_STRING_FILE"] = files.Text(name + ".txt",
                new NpgsqlConnectionStringBuilder { Host = "localhost", Database = "synthetic", Username = role, Password = "configuration-only", SslMode = SslMode.Disable }.ConnectionString);
        values["WEFT_REGRESSIONS_ENABLED"] = "true";
        values[CsvDocumentPolicy.RegressionEnabledSetting] = "true";
        values["WEFT_FERRULE_WORKER_IMAGE"] = "sha256:" + new string('a', 64);
        values["WEFT_CATALOG_AUTH_MODE"] = "session";
        var services = new ServiceCollection();
        var resolved = 0;
        CsvSourceTestServices.Register(services, () => resolved++);
        CatalogRegressionHostConfiguration.Configure(services, values.GetValueOrDefault, true);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<CatalogRegressionCaseOptions>().CsvRuntime);
        Assert.Equal(0, resolved);
    }
}

public sealed class RegressionLinuxFactAttribute : FactAttribute
{
    public RegressionLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Protected Session-host file delivery requires Linux qualification.";
    }
}
