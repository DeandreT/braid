using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application.Automation;
using Weft.Application.Regression;
using Weft.Jobs;
using Weft.Server;
using Weft.Worker;

namespace Weft.Tests;

public sealed class NotificationCompositionTests
{
    [Fact]
    public void Notification_api_and_worker_do_not_require_a_mapping_engine_or_case_credentials()
    {
        var values = new Dictionary<string, string?>
        {
            ["WEFT_NOTIFICATIONS_ENABLED"] = "true",
            ["WEFT_PREVIEW_WORKER_ENABLED"] = "false",
            ["WEFT_NOTIFICATION_CONNECTION_STRING"] = "Host=localhost;Database=synthetic;Username=weft_run_writer;Password=fixture-only"
        };
        string? Read(string name) => values.GetValueOrDefault(name);
        var api = new ServiceCollection();
        CatalogRegressionHostConfiguration.Configure(api, Read, true);
        using var provider = api.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<CatalogAutomationOptions>().Enabled);
        Assert.DoesNotContain(api, d => d.ServiceType == typeof(IRegressionCaseExecutor) || d.ServiceType == typeof(IRegressionCaseReader));
        var settings = PreviewWorkerSettings.Read(Read);
        Assert.False(settings.PreviewEnabled); Assert.Equal("", settings.WorkerImage); Assert.Null(settings.Storage);
        var worker = new ServiceCollection(); worker.AddWeftNotificationWorker(Read);
        Assert.Contains(worker, d => d.ServiceType == typeof(IAutomationQueue));
        Assert.DoesNotContain(worker, d => d.ServiceType == typeof(IRegressionEvaluationExecutor));
    }

    [Fact]
    public void Disabled_notification_worker_reads_no_credentials()
    {
        var services = new ServiceCollection();
        services.AddWeftNotificationWorker(name => name == "WEFT_NOTIFICATIONS_ENABLED" ? null : throw new InvalidOperationException("Unexpected credential read."));
        Assert.Empty(services);
    }
}
