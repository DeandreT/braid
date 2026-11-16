using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Braid.Application.Automation;
using Braid.Persistence.Postgres;

namespace Braid.Jobs;

/// <summary>A notification-only worker never needs a mapping engine, sample credentials or private payload storage.</summary>
public static class NotificationWorkerRegistration
{
    public static IServiceCollection AddBraidNotificationWorker(this IServiceCollection services, Func<string, string?> environment, Func<string, int, string>? privateReader = null)
    {
        var enabled = environment("BRAID_NOTIFICATIONS_ENABLED") switch { null or "false" => false, "true" => true, _ => throw new ArgumentException("Notification enablement must be true or false.") };
        // The existing regression worker already drains both kinds of work when selected.
        if (!enabled || environment("BRAID_REGRESSION_WORKER_ENABLED") == "true") return services;
        var connection = environment("BRAID_NOTIFICATION_CONNECTION_STRING") ?? environment("BRAID_REGRESSION_RUN_WRITER_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection)) throw new ArgumentException("Notification worker requires its restricted connection.");
        var configuration = NotificationConfigurationFile.Read(environment, privateReader);
        services.AddSingleton<IAutomationQueue>(_ => new PostgresAutomationStore(connection, true));
        services.AddSingleton<INotificationTransport>(_ => new NotificationTransport(configuration, TimeProvider.System));
        services.AddHostedService<NotificationOnlyWorker>();
        return services;
    }
}

public sealed class NotificationOnlyWorker(IAutomationQueue queue, INotificationTransport transport, ILogger<NotificationOnlyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claim = await queue.ClaimDeliveryAsync(stoppingToken).ConfigureAwait(false);
                if (claim is not null)
                {
                    var outcome = await transport.SendAsync(claim, stoppingToken).ConfigureAwait(false);
                    await queue.FinishDeliveryAsync(claim, outcome, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { logger.LogWarning("Notification delivery remains retryable."); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
