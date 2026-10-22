using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;
using Weft.Storage.Hosting;

namespace Weft.Server;

/// <summary>Optional report dependencies are deferred until admitted report access.</summary>
internal static class CatalogRevisionReportHostConfiguration
{
    internal static void Configure(WebApplicationBuilder builder, CatalogRevisionReportOptions options,
        Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Configure(builder.Services, builder.Environment, options, readEnvironment);
    }

    internal static void Configure(IServiceCollection services, IWebHostEnvironment environment,
        CatalogRevisionReportOptions options, Func<string, string?> readEnvironment, int? derivedReadByteLimit = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        if (!options.Enabled)
            return;

        var instanceId = Guid.NewGuid();
        services.AddSingleton(_ => ReadDatabase(readEnvironment));
        services.AddSingleton(provider =>
            new PostgresRevisionReportMetadataReader(provider.GetRequiredService<PostgresRevisionReportDatabase>()));
        services.AddSingleton<IRevisionReportMetadataReader>(provider =>
            provider.GetRequiredService<PostgresRevisionReportMetadataReader>());
        services.AddSingleton(provider => new RevisionReportStorageRegistration(
            checked((int)FileSystemStorageOptions.NativeHardMaximum), checked((int)FileSystemStorageOptions.DerivedHardMaximum),
            derivedReadByteLimit ?? DerivedReadLimit(readEnvironment("WEFT_DERIVED_READ_BYTE_LIMIT")),
            () => provider.GetRequiredService<IPrivateObjectStore>()));
        services.AddSingleton<IRevisionReportProfileRegistry>(provider => CreateRegistry(
            readEnvironment("WEFT_FERRULE_WORKER_IMAGE"),
            provider.GetRequiredService<RevisionReportStorageRegistration>().ApplyTo(options.ActiveReadCaps),
            () => SharedRunner(provider)));

        if (options.DerivationEnabled)
        {
            services.AddSingleton(_ => WriteDatabase(readEnvironment));
            services.AddSingleton(provider => new PostgresRevisionReportPublicationRegistry(
                provider.GetRequiredService<PostgresRevisionDerivationDatabase>()));
            services.AddSingleton(provider =>
                new PostgresRevisionDerivationStore(provider.GetRequiredService<PostgresRevisionDerivationDatabase>()));
            services.AddSingleton<IRevisionReportMetadataStore>(provider => new LazyRevisionReportMetadataStore(() =>
            {
                // Pure configuration checks precede the first claim. They neither open
                // the root nor create a container, and never run for a cached Ensure.
                _ = provider.GetRequiredService<PrivateStorageConfiguration>();
                var registry = provider.GetRequiredService<IRevisionReportProfileRegistry>();
                if (registry.FindCurrent("ferrule", RevisionReportOperation.Projection) is null)
                    throw Unavailable(RevisionReportFailureCategory.ProfileUnavailable);
                // Inspect registration metadata only. Resolving the runner remains
                // behind the qualified service's native/header admission and dispatch.
                if (!provider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IInspectionWorkerRunner)))
                    throw Unavailable(RevisionReportFailureCategory.BackendUnavailable);
                return provider.GetRequiredService<PostgresRevisionDerivationStore>();
            }));
        }
        else
            services.AddSingleton<IRevisionReportMetadataStore>(_ => new UnavailableRevisionReportMetadataStore());

        services.AddSingleton(provider => new RevisionReportService(
            provider.GetRequiredService<IRevisionReportMetadataReader>(), provider.GetRequiredService<IRevisionReportMetadataStore>(),
            provider.GetRequiredService<IRevisionReportProfileRegistry>(), provider.GetRequiredService<RevisionReportStorageRegistration>(),
            instanceId, options.ActiveReadCaps));
        services.AddSingleton<IRevisionReportReader>(provider => provider.GetRequiredService<RevisionReportService>());
        services.AddSingleton<IRevisionReportDeriver>(provider => provider.GetRequiredService<RevisionReportService>());
    }

    internal static IRevisionReportProfileRegistry CreateRegistry(string? workerImage, RevisionReportPolicy policy,
        Func<IInspectionWorkerRunner> sharedRunnerFactory)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sharedRunnerFactory);
        if (workerImage is not null)
        {
            try
            {
                var profile = FerruleRevisionReportBackendFactory.CreateProfile(workerImage);
                return FerruleRevisionReportBackendFactory.CreateRegistry(profile, policy, sharedRunnerFactory);
            }
            catch (ArgumentException) { }
        }
        // Historical format support is pure and independent of current image configuration.
        var validator = new FerruleRevisionReportFormatValidator();
        var projection = new RevisionReportFormat("ferrule", RevisionReportOperation.Projection,
            "weft.mapping-projection.v1", FerruleInspectionBackend.ProjectionVersion, FerruleRevisionReportBackendFactory.CodecVersion);
        var validation = new RevisionReportFormat("ferrule", RevisionReportOperation.Validation,
            "weft.mapping-validation.v1", FerruleRevisionReportBackendFactory.ValidationAlgorithm,
            FerruleRevisionReportBackendFactory.CodecVersion);
        return new RevisionReportProfileRegistry([], [new(projection, validator), new(validation, validator)]);
    }

    internal static PrivateStorageConfiguration CreateStorageConfiguration(string contentRoot, Func<string, string?> readEnvironment)
    {
        try
        {
            return PrivateStorageSettings.Read(readEnvironment)
                .Bind(Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot)));
        }
        catch (RevisionReportServiceException) { throw; }
        catch (Exception) { throw Unavailable(RevisionReportFailureCategory.StorageIo); }
    }

    internal static int DerivedReadLimit(string? value)
    {
        if (value is null)
            return RevisionReportPolicy.MaximumStoredObjectByteLimit;
        if (value.Length is < 1 or > 8 || value[0] == '0' || value.Any(character => character is < '0' or > '9') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) ||
            limit is < 1 or > RevisionReportPolicy.MaximumStoredObjectByteLimit)
            throw Unavailable(RevisionReportFailureCategory.ResourceLimit);
        return limit;
    }

    private static PostgresRevisionReportDatabase ReadDatabase(Func<string, string?> readEnvironment)
    {
        try { return new(Required(readEnvironment("WEFT_POSTGRES_REPORT_CONNECTION_STRING"))); }
        catch (Exception) { throw Unavailable(RevisionReportFailureCategory.BackendUnavailable); }
    }

    private static PostgresRevisionDerivationDatabase WriteDatabase(Func<string, string?> readEnvironment)
    {
        try { return new(Required(readEnvironment("WEFT_POSTGRES_DERIVATION_CONNECTION_STRING"))); }
        catch (Exception) { throw Unavailable(RevisionReportFailureCategory.BackendUnavailable); }
    }

    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value)
        ? value : throw Unavailable(RevisionReportFailureCategory.BackendUnavailable);

    private static IInspectionWorkerRunner SharedRunner(IServiceProvider services)
    {
        try { return services.GetRequiredService<IInspectionWorkerRunner>(); }
        catch (Exception) { throw Unavailable(RevisionReportFailureCategory.BackendUnavailable); }
    }

    private static RevisionReportServiceException Unavailable(RevisionReportFailureCategory failure) => new(failure);
}
