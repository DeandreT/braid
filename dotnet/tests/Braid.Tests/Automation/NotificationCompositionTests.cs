using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Catalog;
using Braid.Application.Automation;
using Braid.Application.Regression;
using Braid.Jobs;
using Braid.Server;
using Braid.Worker;

namespace Braid.Tests;

public sealed class NotificationCompositionTests
{
    [Fact]
    public void Notification_api_and_worker_do_not_require_a_mapping_engine_or_case_credentials()
    {
        var values = new Dictionary<string, string?>
        {
            ["BRAID_NOTIFICATIONS_ENABLED"] = "true",
            ["BRAID_PREVIEW_WORKER_ENABLED"] = "false",
            ["BRAID_NOTIFICATION_CONNECTION_STRING"] = "Host=localhost;Database=synthetic;Username=braid_run_writer;Password=fixture-only"
        };
        string? Read(string name) => values.GetValueOrDefault(name);
        var api = new ServiceCollection();
        CatalogRegressionHostConfiguration.Configure(api, Read, true);
        using var provider = api.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<CatalogAutomationOptions>().Enabled);
        Assert.DoesNotContain(api, d => d.ServiceType == typeof(IRegressionCaseExecutor) || d.ServiceType == typeof(IRegressionCaseReader));
        var settings = PreviewWorkerSettings.Read(Read);
        Assert.False(settings.PreviewEnabled); Assert.Equal("", settings.WorkerImage); Assert.Null(settings.Storage);
        var worker = new ServiceCollection(); worker.AddBraidNotificationWorker(Read);
        Assert.Contains(worker, d => d.ServiceType == typeof(IAutomationQueue));
        Assert.DoesNotContain(worker, d => d.ServiceType == typeof(IRegressionEvaluationExecutor));
    }

    [Fact]
    public void Disabled_notification_worker_reads_no_credentials()
    {
        var services = new ServiceCollection();
        services.AddBraidNotificationWorker(name => name == "BRAID_NOTIFICATIONS_ENABLED" ? null : throw new InvalidOperationException("Unexpected credential read."));
        Assert.Empty(services);
    }
}
