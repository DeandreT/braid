using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Jobs;
using Weft.Persistence.Postgres;
using Wolverine;
using Wolverine.Runtime;

namespace Weft.Server;

/// <summary>
/// Optional preview submission and reads. The web host only records runs and their durable
/// commands; the separate Weft.Worker host executes them.
/// </summary>
internal static class CatalogPreviewHostConfiguration
{
    internal static void Configure(IServiceCollection services, CatalogPreviewOptions options, Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        services.AddWeftPreviews(options);
        if (!options.Enabled)
            return;
        var writer = Required(readEnvironment, "WEFT_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING");
        var reader = Required(readEnvironment, "WEFT_POSTGRES_PREVIEW_READER_CONNECTION_STRING");
        services.AddKeyedSingleton(PreviewDatabaseProfile.Writer, (_, _) => new PostgresPreviewDatabase(writer, PreviewDatabaseProfile.Writer));
        services.AddKeyedSingleton(PreviewDatabaseProfile.Reader, (_, _) => new PostgresPreviewDatabase(reader, PreviewDatabaseProfile.Reader));
        services.AddWolverine(wolverine => PreviewJobs.Configure(wolverine, writer, PreviewJobRole.Submitter));
        services.AddSingleton(provider => new PreviewRunService(
            new OutboxPreviewRunStore(new PostgresPreviewRunStore(provider.GetRequiredKeyedService<PostgresPreviewDatabase>(PreviewDatabaseProfile.Writer)),
                provider.GetRequiredService<IWolverineRuntime>()),
            new PostgresPreviewRunReader(provider.GetRequiredKeyedService<PostgresPreviewDatabase>(PreviewDatabaseProfile.Reader)),
            TimeProvider.System));
    }

    private static string Required(Func<string, string?> readEnvironment, string name) =>
        readEnvironment(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Enabled previews require their dedicated preview database configuration.");
}
