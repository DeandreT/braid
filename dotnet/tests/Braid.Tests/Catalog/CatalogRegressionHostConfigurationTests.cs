using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Application.Regression;
using Braid.MappingBackends;
using Braid.Server;

namespace Braid.Tests;

public sealed class CatalogRegressionHostConfigurationTests
{
    [Fact]
    public void Disabled_configuration_reads_no_runtime_or_credential_inputs()
    {
        var services = new ServiceCollection();
        CatalogRegressionHostConfiguration.Configure(services, name => name is "BRAID_REGRESSIONS_ENABLED" or "BRAID_NOTIFICATIONS_ENABLED" or XmlPreviewAdmission.RegressionEnabledSetting ? null : throw new InvalidOperationException(), false);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("")]
    public void Noncanonical_flags_are_rejected_before_credential_reads(string value)
    {
        Assert.Throws<ArgumentException>(() => CatalogRegressionHostConfiguration.Configure(new ServiceCollection(),
            name => name == "BRAID_REGRESSIONS_ENABLED" ? value : throw new InvalidOperationException(), true));
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("")]
    public void Invalid_csv_flag_is_rejected_before_private_configuration(string value)
    {
        Assert.Throws<ArgumentException>(() => CatalogRegressionHostConfiguration.Configure(new ServiceCollection(), name => name switch
        {
            "BRAID_REGRESSIONS_ENABLED" => "true",
            "BRAID_NOTIFICATIONS_ENABLED" => null,
            CsvDocumentPolicy.RegressionEnabledSetting => value,
            XmlPreviewAdmission.RegressionEnabledSetting => null,
            _ => throw new InvalidOperationException("CSV admission opened private configuration.")
        }, true));
    }

    [Fact]
    public void Catalog_is_a_required_shared_prerequisite()
    {
        Assert.Throws<ArgumentException>(() => CatalogRegressionHostConfiguration.Configure(new ServiceCollection(),
            name => name == "BRAID_REGRESSIONS_ENABLED" ? "true" : throw new InvalidOperationException(), false));
    }

    [Fact]
    public void Enabled_configuration_registers_separate_reader_and_writer_ports_without_execution()
    {
        var services = new ServiceCollection();
        CatalogRegressionHostConfiguration.Configure(services, name => name switch
        {
            "BRAID_REGRESSIONS_ENABLED" => "true",
            "BRAID_FERRULE_WORKER_IMAGE" => "sha256:" + new string('a', 64),
            "BRAID_CATALOG_AUTH_MODE" => "development",
            "BRAID_NOTIFICATIONS_ENABLED" or "BRAID_NOTIFICATION_CONFIG_FILE" => null,
            "BRAID_FERRULE_RUNNER" or "BRAID_FERRULE_COMPILE_TOOLCHAIN" => null,
            CsvDocumentPolicy.RegressionEnabledSetting or XmlPreviewAdmission.RegressionEnabledSetting => null,
            _ when name.EndsWith("_CONNECTION_STRING", StringComparison.Ordinal) => "private-synthetic",
            _ => null
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
            "BRAID_REGRESSIONS_ENABLED" => "true",
            CsvDocumentPolicy.RegressionEnabledSetting => flag,
            XmlPreviewAdmission.RegressionEnabledSetting => null,
            "BRAID_FERRULE_WORKER_IMAGE" => image,
            "BRAID_CATALOG_AUTH_MODE" => "development",
            "BRAID_NOTIFICATIONS_ENABLED" or "BRAID_NOTIFICATION_CONFIG_FILE" or "BRAID_FERRULE_RUNNER" or "BRAID_FERRULE_COMPILE_TOOLCHAIN" => null,
            _ when name.EndsWith("_CONNECTION_STRING", StringComparison.Ordinal) => "private-synthetic",
            _ => null
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
        foreach (var (name, role) in new[] { ("CASE_READER", "braid_case_reader"), ("CASE_WRITER", "braid_case_writer"),
            ("RUN_READER", "braid_run_reader"), ("RUN_WRITER", "braid_run_writer") })
            profiles["BRAID_REGRESSION_" + name + "_CONNECTION_STRING_FILE"] = files.Text(name + ".txt",
                new NpgsqlConnectionStringBuilder { Host = "localhost", Database = "synthetic", Username = role, Password = "synthetic-not-a-secret", SslMode = SslMode.Disable }.ConnectionString);
        var requested = new List<string>();
        var services = new ServiceCollection();
        CatalogRegressionHostConfiguration.Configure(services, name =>
        {
            requested.Add(name);
            return name switch
            {
                "BRAID_REGRESSIONS_ENABLED" => "true",
                "BRAID_FERRULE_WORKER_IMAGE" => "sha256:" + new string('a', 64),
                "BRAID_CATALOG_AUTH_MODE" => "session",
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
        foreach (var (name, role) in new[] { ("CASE_READER", "braid_case_reader"), ("CASE_WRITER", "braid_case_writer"),
            ("RUN_READER", "braid_run_reader"), ("RUN_WRITER", "braid_run_writer") })
            values["BRAID_REGRESSION_" + name + "_CONNECTION_STRING_FILE"] = files.Text(name + ".txt",
                new NpgsqlConnectionStringBuilder { Host = "localhost", Database = "synthetic", Username = role, Password = "configuration-only", SslMode = SslMode.Disable }.ConnectionString);
        values["BRAID_REGRESSIONS_ENABLED"] = "true";
        values[CsvDocumentPolicy.RegressionEnabledSetting] = "true";
        values["BRAID_FERRULE_WORKER_IMAGE"] = "sha256:" + new string('a', 64);
        values["BRAID_CATALOG_AUTH_MODE"] = "session";
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
