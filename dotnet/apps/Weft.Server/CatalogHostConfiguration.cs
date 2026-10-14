using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.MappingBackends.Ingestion;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Server;

/// <summary>Explicit host composition; disabled catalog never creates a database pool.</summary>
public static class CatalogHostConfiguration
{
    public static void Configure(WebApplicationBuilder builder, IPEndPoint listener,
        Func<string, string?>? readEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(listener);
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var options = new CatalogApiOptions(
            Flag("WEFT_CATALOG_ENABLED"), Flag("WEFT_DEV_IDENTITY_ENABLED"),
            Id("WEFT_DEV_PRINCIPAL_ID"), Id("WEFT_DEV_ORGANIZATION_ID"),
            readEnvironment("WEFT_DEV_BROWSER_ORIGIN"));
        var metadataOptions = new CatalogMetadataEditOptions(Flag("WEFT_METADATA_EDITS_ENABLED"));
        var ingestionOptions = new CatalogRevisionIngestionOptions(Flag("WEFT_REVISION_INGESTION_ENABLED"));
        var creationOptions = new CatalogProjectCreationOptions(Flag("WEFT_PROJECT_CREATION_ENABLED"));
        var reportOptions = new CatalogRevisionReportOptions(Flag("WEFT_REVISION_REPORTS_ENABLED"),
            Flag("WEFT_REVISION_REPORT_DERIVATION_ENABLED"));
        builder.Services.AddWeftCatalog(options, builder.Environment, metadataOptions, ingestionOptions, creationOptions, reportOptions);
        if (!options.Enabled)
            return;

        var origin = new Uri(options.BrowserOrigin!, UriKind.Absolute);
        if (!IPAddress.IsLoopback(listener.Address) || origin.Port != listener.Port ||
            !IPAddress.Parse(origin.Host.Trim('[', ']')).Equals(listener.Address))
            throw new InvalidOperationException("The catalog browser origin must match the concrete configured loopback listener.");
        var connectionString = readEnvironment("WEFT_CATALOG_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Enabled catalog requires its dedicated reader connection configuration.");
        builder.Services.AddSingleton(_ => new PostgresCatalogDatabase(connectionString));
        builder.Services.AddSingleton(services =>
            new PostgresCatalogReader(services.GetRequiredService<PostgresCatalogDatabase>()));
        builder.Services.AddSingleton<ICatalogReader>(services => services.GetRequiredService<PostgresCatalogReader>());
        if (creationOptions.Enabled)
        {
            var creationConnectionString = readEnvironment("WEFT_POSTGRES_PROJECT_CREATION_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(creationConnectionString))
                throw new InvalidOperationException("Enabled project creation requires its dedicated creator connection configuration.");
            builder.Services.AddSingleton(_ => CreateProjectCreationDatabase(creationConnectionString));
            builder.Services.AddSingleton(services =>
                new PostgresProjectCreator(services.GetRequiredService<PostgresProjectCreationDatabase>()));
            builder.Services.AddSingleton<IOrganizationProjectCreationReader>(services => services.GetRequiredService<PostgresProjectCreator>());
            builder.Services.AddSingleton<IProjectCreator>(services => services.GetRequiredService<PostgresProjectCreator>());
        }
        if (metadataOptions.Enabled)
        {
            var metadataConnectionString = readEnvironment("WEFT_POSTGRES_METADATA_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(metadataConnectionString))
                throw new InvalidOperationException("Enabled metadata edits require their dedicated editor connection configuration.");
            builder.Services.AddSingleton(_ => new PostgresMappingMetadataDatabase(metadataConnectionString));
            builder.Services.AddSingleton<IMappingMetadataWriter>(services =>
                new PostgresMappingMetadataWriter(services.GetRequiredService<PostgresMappingMetadataDatabase>()));
        }
        FileSystemStorageOptions? storageOptions = null;
        if (ingestionOptions.Enabled)
        {
            var ingestionConnectionString = readEnvironment("WEFT_POSTGRES_INGESTION_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(ingestionConnectionString))
                throw new InvalidOperationException("Enabled revision ingestion requires its dedicated ingestion connection configuration.");
            var root = readEnvironment("WEFT_PRIVATE_STORAGE_ROOT");
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Enabled revision ingestion requires an explicit private storage root.");
            var nativeLimit = NativeLimit(readEnvironment("WEFT_NATIVE_BYTE_LIMIT"));
            storageOptions = new FileSystemStorageOptions(root,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(builder.Environment.ContentRootPath)));
            // Current admission reductions must not prevent verification of an
            // original reserved object. Physical and registration caps stay equal.
            builder.Services.AddSingleton(_ => new PostgresIngestionDatabase(ingestionConnectionString));
            builder.Services.AddSingleton(services => new PostgresRevisionIngestionStore(services.GetRequiredService<PostgresIngestionDatabase>()));
            builder.Services.AddSingleton<IIngestionMetadataStore>(services => services.GetRequiredService<PostgresRevisionIngestionStore>());
            builder.Services.AddSingleton<IIngestionWorkflowReader>(services => services.GetRequiredService<PostgresRevisionIngestionStore>());
            builder.Services.AddSingleton<IProjectReader>(services => services.GetRequiredService<PostgresCatalogReader>());
            var ingestionStorageOptions = storageOptions;
            builder.Services.AddSingleton(services => new IngestionPrivateStoreRegistration(
                services.GetRequiredService<IPrivateObjectStore>(), ingestionStorageOptions.NativeByteLimit));
            builder.Services.AddSingleton<IIngestionBackendRegistry>(services =>
            {
                var runner = services.GetService<IInspectionWorkerRunner>();
                return new IngestionBackendRegistry(runner is null ? [] : [FerruleIngestionAdmissionBackend.CreateRegistration(runner)]);
            });
            builder.Services.AddSingleton(new RevisionIngestionOptions(nativeLimit));
            builder.Services.AddSingleton<IRevisionIngestionService, RevisionIngestionService>();
        }
        if (ingestionOptions.Enabled || reportOptions.Enabled)
        {
            if (storageOptions is not null)
                builder.Services.AddSingleton(storageOptions);
            else
                builder.Services.AddSingleton(_ => CatalogRevisionReportHostConfiguration.CreateStorageOptions(
                    builder.Environment.ContentRootPath, readEnvironment));
            builder.Services.AddSingleton<LazyPrivateObjectStore>();
            builder.Services.AddSingleton<IPrivateObjectStore>(services => services.GetRequiredService<LazyPrivateObjectStore>());
        }
        CatalogRevisionReportHostConfiguration.Configure(builder, reportOptions, readEnvironment);

        bool Flag(string variable) => readEnvironment(variable) switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("Catalog enablement options must be explicit true or false.")
        };

        Guid? Id(string variable)
        {
            var value = readEnvironment(variable);
            if (value is null)
                return null;
            if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D"))
                throw new ArgumentException("Catalog identity configuration requires canonical nonempty UUIDs.");
            return id;
        }
    }

    private static PostgresProjectCreationDatabase CreateProjectCreationDatabase(string connectionString)
    {
        try { return new PostgresProjectCreationDatabase(connectionString); }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("The project creation connection configuration is invalid.");
        }
    }

    private static long NativeLimit(string? value)
    {
        if (value is null) return FileSystemStorageOptions.NativeHardMaximum;
        if (value.Length is < 1 or > 7 || value[0] == '0' || value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) ||
            limit is < 1 or > FileSystemStorageOptions.NativeHardMaximum)
            throw new ArgumentException("The native byte admission limit must be a canonical positive decimal at most 4194304.");
        return limit;
    }
}
