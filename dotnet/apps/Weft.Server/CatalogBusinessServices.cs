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
            services.AddSingleton<IRevisionIngestionService, RevisionIngestionService>();
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
