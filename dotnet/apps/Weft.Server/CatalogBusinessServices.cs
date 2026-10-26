using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.MappingBackends.Ingestion;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;
using Weft.Storage.Hosting;

namespace Weft.Server;

/// <summary>Shared lazy business ports, independent of the selected catalog identity boundary.</summary>
internal static class CatalogBusinessServices
{
    internal static void Configure(IServiceCollection services, IWebHostEnvironment environment,
        CatalogMetadataEditOptions metadataOptions, CatalogRevisionIngestionOptions ingestionOptions,
        CatalogProjectCreationOptions creationOptions, CatalogRevisionReportOptions reportOptions,
        Func<string, string?> readEnvironment, long? nativeByteLimit = null, int? derivedReadByteLimit = null, BuildHostRuntime? buildRuntime = null)
    {
        var connectionString = readEnvironment("WEFT_CATALOG_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Enabled catalog requires its dedicated reader connection configuration.");
        services.AddSingleton(_ => new PostgresCatalogDatabase(connectionString));
        services.AddSingleton(provider => new PostgresCatalogReader(provider.GetRequiredService<PostgresCatalogDatabase>()));
        services.AddSingleton<ICatalogReader>(provider => provider.GetRequiredService<PostgresCatalogReader>());
        services.AddSingleton(_ => new SearchIndexConnection(() => readEnvironment("WEFT_POSTGRES_DERIVATION_CONNECTION_STRING")));
        services.AddSingleton<ICatalogSearch>(provider => new PostgresCatalogSearch(
            provider.GetRequiredService<PostgresCatalogReader>(),
            () => provider.GetService<IRevisionReportReader>(),
            () => provider.GetService<PostgresRevisionDerivationDatabase>() ??
                provider.GetRequiredService<SearchIndexConnection>().Database));
        AdministrationHostConfiguration.Configure(services, readEnvironment);
        if (creationOptions.Enabled)
        {
            var creationConnectionString = readEnvironment("WEFT_POSTGRES_PROJECT_CREATION_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(creationConnectionString))
                throw new InvalidOperationException("Enabled project creation requires its dedicated creator connection configuration.");
            services.AddSingleton(_ => CreateProjectCreationDatabase(creationConnectionString));
            services.AddSingleton(provider => new PostgresProjectCreator(provider.GetRequiredService<PostgresProjectCreationDatabase>()));
            services.AddSingleton<IOrganizationProjectCreationReader>(provider => provider.GetRequiredService<PostgresProjectCreator>());
            services.AddSingleton<IProjectCreator>(provider => provider.GetRequiredService<PostgresProjectCreator>());
        }
        if (metadataOptions.Enabled)
        {
            var metadataConnectionString = readEnvironment("WEFT_POSTGRES_METADATA_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(metadataConnectionString))
                throw new InvalidOperationException("Enabled metadata edits require their dedicated editor connection configuration.");
            services.AddSingleton(_ => new PostgresMappingMetadataDatabase(metadataConnectionString));
            services.AddSingleton<IMappingMetadataWriter>(provider =>
                new PostgresMappingMetadataWriter(provider.GetRequiredService<PostgresMappingMetadataDatabase>()));
        }
        PrivateStorageConfiguration? storageConfiguration = null;
        if (ingestionOptions.Enabled)
        {
            var ingestionConnectionString = readEnvironment("WEFT_POSTGRES_INGESTION_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(ingestionConnectionString))
                throw new InvalidOperationException("Enabled revision ingestion requires its dedicated ingestion connection configuration.");
            // The explicit storage selection is checked here, before any service is built.
            var storageSettings = PrivateStorageSettings.Read(readEnvironment);
            var nativeLimit = nativeByteLimit ?? NativeLimit(readEnvironment("WEFT_NATIVE_BYTE_LIMIT"));
            storageConfiguration = storageSettings.Bind(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath)));
            // Current admission reductions must not prevent verification of an
            // original reserved object. Physical and registration caps stay equal.
            services.AddSingleton(_ => new PostgresIngestionDatabase(ingestionConnectionString));
            services.AddSingleton(provider => new PostgresIngestionPublicationRegistry(provider.GetRequiredService<PostgresIngestionDatabase>()));
            services.AddSingleton(provider => new PostgresRevisionIngestionStore(provider.GetRequiredService<PostgresIngestionDatabase>()));
            services.AddSingleton<IIngestionMetadataStore>(provider => provider.GetRequiredService<PostgresRevisionIngestionStore>());
            services.AddSingleton<IIngestionWorkflowReader>(provider => provider.GetRequiredService<PostgresRevisionIngestionStore>());
            services.AddSingleton<IProjectReader>(provider => provider.GetRequiredService<PostgresCatalogReader>());
            var ingestionStorage = storageConfiguration;
            services.AddSingleton(provider => new IngestionPrivateStoreRegistration(
                provider.GetRequiredService<IPrivateObjectStore>(), ingestionStorage.NativeByteLimit));
            services.AddSingleton<IIngestionBackendRegistry>(provider =>
            {
                var runner = provider.GetService<IInspectionWorkerRunner>();
                return new IngestionBackendRegistry(runner is null ? [] : [FerruleIngestionAdmissionBackend.CreateRegistration(runner)]);
            });
            services.AddSingleton(new RevisionIngestionOptions(nativeLimit));
            services.AddSingleton<RevisionIngestionService>();
            services.AddSingleton<IRevisionIngestionService>(provider => provider.GetRequiredService<RevisionIngestionService>());
            services.AddSingleton<IReviewedRevisionIngestionService>(provider => provider.GetRequiredService<RevisionIngestionService>());
        }
        if (ingestionOptions.Enabled || reportOptions.Enabled)
        {
            if (storageConfiguration is not null)
                services.AddSingleton(storageConfiguration);
            else
                services.AddSingleton(_ => CatalogRevisionReportHostConfiguration.CreateStorageConfiguration(
                    environment.ContentRootPath, readEnvironment));
            services.AddSingleton(provider => new LazyPrivateObjectPublicationRegistry(
                () => provider.GetRequiredService<PostgresIngestionPublicationRegistry>(),
                () => provider.GetRequiredService<PostgresRevisionReportPublicationRegistry>()));
            services.AddSingleton(provider => new LazyPrivateObjectStore(
                provider.GetRequiredService<PrivateStorageConfiguration>(),
                () => provider.GetRequiredService<LazyPrivateObjectPublicationRegistry>()));
            services.AddSingleton<IPrivateObjectStore>(provider => provider.GetRequiredService<LazyPrivateObjectStore>());
            services.AddSingleton<IPrivateObjectPublicationStore>(provider => provider.GetRequiredService<LazyPrivateObjectStore>());
        }
        CatalogRevisionReportHostConfiguration.Configure(services, environment, reportOptions, readEnvironment, derivedReadByteLimit);
        var interchange = readEnvironment("WEFT_MFD_INTERCHANGE_ENABLED");
        if (interchange is not (null or "false" or "true"))
            throw new InvalidOperationException("WEFT_MFD_INTERCHANGE_ENABLED must be true or false.");
        if (interchange == "true")
        {
            if (!ingestionOptions.Enabled || !reportOptions.Enabled)
                throw new InvalidOperationException("MFD interchange requires revision ingestion and revision report read storage.");
            services.AddSingleton(new CatalogInterchangeOptions(true));
            services.AddSingleton<IImportReviewStore>(provider => provider.GetRequiredService<PostgresRevisionIngestionStore>());
            services.AddSingleton<IExportReviewStore>(provider => provider.GetRequiredService<PostgresRevisionIngestionStore>());
            services.AddSingleton(provider => new InterchangeImportService(provider.GetRequiredService<IProjectReader>(),
                provider.GetRequiredService<IIngestionWorkflowReader>(), provider.GetRequiredService<IInterchangeBackendRegistry>(),
                provider.GetRequiredService<IImportReviewStore>(), provider.GetRequiredService<IReviewedRevisionIngestionService>(), TimeProvider.System));
            services.AddSingleton(provider => new InterchangeExportService(provider.GetRequiredService<IRevisionReportMetadataReader>(),
                provider.GetRequiredService<IPrivateObjectStore>(), provider.GetRequiredService<IInterchangeBackendRegistry>(),
                provider.GetRequiredService<IExportReviewStore>(), TimeProvider.System));
        }
        CatalogRegressionHostConfiguration.Configure(services, readEnvironment, catalogEnabled: true);
        CatalogBuildHostConfiguration.Configure(services, environment, readEnvironment,
            buildRuntime ?? CatalogBuildHostConfiguration.ReadRuntime(readEnvironment, catalogEnabled: true));
    }

    private static PostgresProjectCreationDatabase CreateProjectCreationDatabase(string connectionString)
    {
        try { return new PostgresProjectCreationDatabase(connectionString); }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("The project creation connection configuration is invalid.");
        }
    }

    private static PostgresRevisionDerivationDatabase SearchIndexDatabase(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new CatalogSearchIndexPendingException();
        try { return new(connectionString); }
        catch (ArgumentException) { throw new CatalogSearchIndexPendingException(); }
    }

    // Read-only hosts keep native derivation disabled. A cold search may resolve
    // this separately owned cache connection, but resolving search itself must not.
    private sealed class SearchIndexConnection(Func<string?> connectionString) : IAsyncDisposable
    {
        private readonly Lazy<PostgresRevisionDerivationDatabase> database = new(() => SearchIndexDatabase(connectionString()));
        private readonly object lifetime = new();
        private bool disposed;

        internal PostgresRevisionDerivationDatabase Database
        {
            get
            {
                lock (lifetime)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    return database.Value;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            PostgresRevisionDerivationDatabase? value;
            lock (lifetime)
            {
                if (disposed) return;
                disposed = true;
                value = database.IsValueCreated ? database.Value : null;
            }
            if (value is not null) await value.DisposeAsync();
        }
    }

    internal static long NativeLimit(string? value)
    {
        if (value is null) return FileSystemStorageOptions.NativeHardMaximum;
        if (value.Length is < 1 or > 7 || value[0] == '0' || value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) ||
            limit is < 1 or > FileSystemStorageOptions.NativeHardMaximum)
            throw new ArgumentException("The native byte admission limit must be a canonical positive decimal at most 4194304.");
        return limit;
    }
}
