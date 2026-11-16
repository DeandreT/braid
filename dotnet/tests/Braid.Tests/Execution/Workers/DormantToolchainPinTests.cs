using Braid.MappingBackends.Workers;
using Braid.Server;
using Braid.Worker;

namespace Braid.Tests;

public sealed class DormantToolchainPinTests
{
    private static readonly string Pin = "sha256:" + new string('a', 64);

    [Theory]
    [InlineData(null, true)]
    [InlineData("worker", true)]
    [InlineData("docker", true)]
    [InlineData("unknown", true)]
    [InlineData("service", false)]
    [InlineData("azureContainerAppsSessions", false)]
    public void A_session_validates_a_supplied_pin_without_native_work(string? runner, bool validPin)
    {
        var values = Session();
        if (runner is not null) values[FerruleRunnerSelection.Setting] = runner;
        values[FerruleRunnerSelection.CompileToolchainSetting] = validPin ? Pin : "latest";
        Assert.Throws<InvalidOperationException>(() => ServerHostOptions.Read(Read));
        string? Read(string key)
        {
            Assert.False(IsPrivateDelivery(key));
            return values.GetValueOrDefault(key);
        }
    }

    [Theory]
    [InlineData("service")]
    [InlineData("azureContainerAppsSessions")]
    public void A_valid_read_only_session_pin_needs_no_native_runner_or_private_delivery(string runner)
    {
        var values = Session();
        values[FerruleRunnerSelection.Setting] = runner;
        values[FerruleRunnerSelection.CompileToolchainSetting] = Pin;
        var options = ServerHostOptions.Read(Read);
        Assert.True(options.Reports.Enabled);
        Assert.False(options.Reports.DerivationEnabled);
        Assert.False(options.Ingestion.Enabled);
        Assert.Null(options.InspectionWorker);
        Assert.Null(options.FerruleService);
        Assert.Null(options.FerruleSessions);
        Assert.Null(options.Bearer);
        string? Read(string key)
        {
            Assert.False(IsPrivateDelivery(key));
            return values.GetValueOrDefault(key);
        }
    }

    // Disabled bearer admission probes the optional path setting for partial configuration;
    // it does not open that file. Every other private-delivery selector remains forbidden here.
    private static bool IsPrivateDelivery(string key) => key.EndsWith("_FILE", StringComparison.Ordinal)
        && key != ServerHostOptions.BearerAuditSetting + "_FILE";

    [Fact]
    public void A_dormant_session_without_a_pin_still_does_not_require_a_runner()
    {
        var values = Session();
        values[FerruleRunnerSelection.Setting] = "unused-dormant-selection";
        Assert.True(ServerHostOptions.Read(values.GetValueOrDefault).Session);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("service")]
    [InlineData("azureContainerAppsSessions")]
    public void An_inspection_only_worker_cannot_silently_ignore_a_compiler_pin(string? runner)
    {
        var settings = new PreviewWorkerSettings(false, null, null, Pin, "/usr/bin/docker", TimeSpan.FromMinutes(2));
        var reads = new List<string>();
        Assert.Throws<InvalidOperationException>(() => PreviewRunnerSettings.Read(Read, settings));
        Assert.Equal([FerruleRunnerSelection.CompileToolchainSetting], reads);
        string? Read(string key)
        {
            reads.Add(key);
            return key switch
            {
                FerruleRunnerSelection.CompileToolchainSetting => Pin,
                FerruleRunnerSelection.Setting => runner,
                _ => throw new InvalidOperationException("No other configuration should be read.")
            };
        }
    }

    [Fact]
    public void An_inspection_only_worker_without_a_pin_keeps_other_preview_settings_dormant()
    {
        var settings = new PreviewWorkerSettings(false, null, null, Pin, "/usr/bin/docker", TimeSpan.FromMinutes(2));
        var selected = PreviewRunnerSettings.Read(key => key == FerruleRunnerSelection.CompileToolchainSetting
            ? null : throw new InvalidOperationException("No other configuration should be read."), settings);
        Assert.Equal(FerruleRunnerKind.Docker, selected.Kind);
    }

    private static Dictionary<string, string> Session() => new(StringComparer.Ordinal)
    {
        ["BRAID_CATALOG_ENABLED"] = "true",
        ["BRAID_CATALOG_AUTH_MODE"] = "session",
        ["BRAID_PUBLIC_ORIGIN"] = "https://127.0.0.1:58413",
        ["BRAID_LISTEN_ADDR"] = "127.0.0.1:58413",
        ["BRAID_HOST_TRANSPORT"] = "directHttps",
        ["BRAID_SESSION_ORGANIZATION_ID"] = "97b79dcc-d078-4994-9a06-f75147842811",
        ["BRAID_ENTRA_TENANT_ID"] = "e6492421-de0b-4626-87a1-773d7f77e0aa",
        ["BRAID_ENTRA_CLIENT_ID"] = "39ceba91-5e4d-4ead-9be2-ddbf843be1c6",
        ["BRAID_SESSION_APPLICATION_NAME"] = "Dormant pin admission",
        ["BRAID_SESSION_KEY_DIRECTORY"] = Path.Combine(Path.GetTempPath(), "braid-unopened-test-key-ring"),
        ["BRAID_REVISION_REPORTS_ENABLED"] = "true"
    };
}
