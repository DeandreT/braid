using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;

namespace Braid.Server;

internal static class CatalogMappingEditingHostConfiguration
{
    internal const string WriterConnection = "BRAID_POSTGRES_DRAFT_WRITER_CONNECTION_STRING";
    internal const string ReaderConnection = "BRAID_POSTGRES_DRAFT_READER_CONNECTION_STRING";

    internal static void Configure(IServiceCollection services, Func<string, string?> read, bool catalog, bool ingestion,
        bool reports, bool derivation, bool session = false)
    {
        var enabled = read("BRAID_MAPPING_EDITING_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new InvalidOperationException("Mapping editing enablement must be explicit true or false.")
        };
        services.AddSingleton(new CatalogMappingEditOptions(enabled));
        if (!enabled) return;
        if (!catalog || !ingestion || !reports || !derivation)
            throw new InvalidOperationException("Mapping editing requires catalog reads, revision ingestion and report derivation.");
        if (session && read(FerruleRunnerSelection.Setting) is null or "worker")
            throw new InvalidOperationException("Mapping editing requires the Ferrule service; the legacy inspection transport does not serve edit checkpoints.");
        var writer = session ? SessionHostResources.ReadProtectedConnection(read, WriterConnection) : Required(read, WriterConnection);
        var reader = session ? SessionHostResources.ReadProtectedConnection(read, ReaderConnection) : Required(read, ReaderConnection);
        if (writer == reader) throw new InvalidOperationException("Mapping drafts require distinct reader and writer credentials.");
        services.AddKeyedSingleton(MappingDraftDatabaseProfile.Reader, (_, _) => new PostgresMappingDraftDatabase(reader, MappingDraftDatabaseProfile.Reader));
        services.AddKeyedSingleton(MappingDraftDatabaseProfile.Writer, (_, _) => new PostgresMappingDraftDatabase(writer, MappingDraftDatabaseProfile.Writer));
        services.AddSingleton<IMappingDraftStore>(provider => new PostgresMappingDraftStore(
            provider.GetRequiredKeyedService<PostgresMappingDraftDatabase>(MappingDraftDatabaseProfile.Reader),
            provider.GetRequiredKeyedService<PostgresMappingDraftDatabase>(MappingDraftDatabaseProfile.Writer)));
        services.AddSingleton<MappingDraftService>();
        services.AddSingleton<MappingDirectEditService>();
        services.AddHostedService<MappingDraftExpiry>();
    }

    private static string Required(Func<string, string?> read, string name) => read(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Enabled mapping drafts require their dedicated database configuration.");

    private sealed class MappingDraftExpiry(IMappingDraftStore store) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try { await store.ExpireAsync(100, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException)
                {
                    // A failed expiry pass is retryable; content remains private and
                    // expired open drafts are hidden by all reader functions meanwhile.
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
    }
}
