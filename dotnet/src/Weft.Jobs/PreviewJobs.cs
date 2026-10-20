using JasperFx;
using Wolverine;
using Wolverine.Postgresql;

namespace Weft.Jobs;

public enum PreviewJobRole
{
    /// <summary>Records commands through the transactional outbox only; it runs no node agents or listeners.</summary>
    Submitter,
    /// <summary>The single preview worker: it listens, and on start reclaims every persisted envelope.</summary>
    Worker
}

/// <summary>
/// Wolverine composition for durable preview delivery. Envelopes, queues and node records live in
/// <see cref="Schema"/> through the preview writer login; run state stays in the weft schema.
/// </summary>
public static class PreviewJobs
{
    public const string Schema = "weft_messages";
    public const string Queue = "weft_preview";

    public static void Configure(WolverineOptions options, string writerConnectionString, PreviewJobRole role)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(writerConnectionString);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        options.ServiceName = "weft-preview";
        options.Durability.Mode = role == PreviewJobRole.Worker ? DurabilityMode.Solo : DurabilityMode.ServerlessWithOutbox;
        options.UsePostgresqlPersistenceAndTransport(writerConnectionString, Schema, Schema);
        options.AutoBuildMessageStorageOnStartup = AutoCreate.CreateOrUpdate;
        options.Discovery.DisableConventionalDiscovery();
        options.PublishMessage<RunPreview>().ToPostgresqlQueue(Queue);
        if (role == PreviewJobRole.Worker)
        {
            options.Discovery.IncludeType<RunPreviewHandler>();
            // The restricted worker runner admits one container at a time.
            options.ListenToPostgresqlQueue(Queue).MaximumParallelMessages(1).UseDurableInbox();
        }
    }
}
