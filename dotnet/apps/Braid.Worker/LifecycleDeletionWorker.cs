using Microsoft.Extensions.Hosting;
using Braid.Application.Lifecycle;
using Braid.Persistence.Postgres;
using Braid.Storage.Hosting;

namespace Braid.Worker;

/// <summary>
/// Durable due-work scanning for permanent deletion jobs and organization retention. The database state is
/// authoritative: a restart or a duplicate worker only finds the same due jobs.
/// </summary>
internal sealed class LifecycleDeletionWorker(string connectionString, PrivateStorageConfiguration storage) : BackgroundService
{
    internal const string EnabledSetting = "BRAID_LIFECYCLE_WORKER_ENABLED";
    internal const string ConnectionSetting = "BRAID_LIFECYCLE_WORKER_CONNECTION_STRING";
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The existing store is admitted, never initialized, activated or replaced.
        await using var objects = await storage.OpenAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
        await using var jobs = new PostgresDeletionJobStore(connectionString);
        var instance = Guid.NewGuid();
        var service = new DeletionJobService(jobs, objects, instance);
        while (!stoppingToken.IsCancellationRequested)
        {
            bool claimed;
            // Deletion jobs first, then one bounded retention sweep; both are durable due work.
            try { claimed = await service.RunOnceAsync(stoppingToken).ConfigureAwait(false) | await jobs.SweepAsync(instance, 100, stoppingToken).ConfigureAwait(false) > 0; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            // A failed run leaves its claim to expire; the job stays due and nothing is acknowledged.
            catch (Exception error) when (error is not OutOfMemoryException) { claimed = false; }
            if (!claimed) await Task.Delay(Idle, stoppingToken).ConfigureAwait(false);
        }
    }
}
