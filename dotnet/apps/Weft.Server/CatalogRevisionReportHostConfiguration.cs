using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Server;

/// <summary>Optional report dependencies are deferred until admitted report access.</summary>
internal static class CatalogRevisionReportHostConfiguration
{
    internal static void Configure(WebApplicationBuilder builder, CatalogRevisionReportOptions options,
        Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        if (!options.Enabled)
            return;

        var instanceId = Guid.NewGuid();
        builder.Services.AddSingleton(_ => ReadDatabase(readEnvironment));
        builder.Services.AddSingleton(services =>
            new PostgresRevisionReportMetadataReader(services.GetRequiredService<PostgresRevisionReportDatabase>()));
        builder.Services.AddSingleton<IRevisionReportMetadataReader>(services =>
            services.GetRequiredService<PostgresRevisionReportMetadataReader>());
        builder.Services.AddSingleton(services => new RevisionReportStorageRegistration(
            checked((int)FileSystemStorageOptions.NativeHardMaximum), checked((int)FileSystemStorageOptions.DerivedHardMaximum),
            DerivedReadLimit(readEnvironment("WEFT_DERIVED_READ_BYTE_LIMIT")),
            () => services.GetRequiredService<IPrivateObjectStore>()));
        builder.Services.AddSingleton<IRevisionReportProfileRegistry>(services => CreateRegistry(
            readEnvironment("WEFT_FERRULE_WORKER_IMAGE"),
            services.GetRequiredService<RevisionReportStorageRegistration>().ApplyTo(options.ActiveReadCaps),
            () => SharedRunner(services)));

        if (options.DerivationEnabled)
        {
            builder.Services.AddSingleton(_ => WriteDatabase(readEnvironment));
            builder.Services.AddSingleton(services =>
                new PostgresRevisionDerivationStore(services.GetRequiredService<PostgresRevisionDerivationDatabase>()));
            builder.Services.AddSingleton<IRevisionReportMetadataStore>(services => new LazyRevisionReportMetadataStore(() =>
            {
                // Pure configuration checks precede the first claim. They neither open
                // the root nor create a container, and never run for a cached Ensure.
                _ = services.GetRequiredService<FileSystemStorageOptions>();
                var registry = services.GetRequiredService<IRevisionReportProfileRegistry>();
                if (registry.FindCurrent("ferrule", RevisionReportOperation.Projection) is null)
                    throw Unavailable(RevisionReportFailureCategory.ProfileUnavailable);
                // Inspect registration metadata only. Resolving the runner remains
                // behind the qualified service's native/header admission and dispatch.
                if (!services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IInspectionWorkerRunner)))
                    throw Unavailable(RevisionReportFailureCategory.BackendUnavailable);
                return services.GetRequiredService<PostgresRevisionDerivationStore>();
            }));
        }
        else
            builder.Services.AddSingleton<IRevisionReportMetadataStore>(_ => new UnavailableRevisionReportMetadataStore());

        builder.Services.AddSingleton(services => new RevisionReportService(
            services.GetRequiredService<IRevisionReportMetadataReader>(), services.GetRequiredService<IRevisionReportMetadataStore>(),
            services.GetRequiredService<IRevisionReportProfileRegistry>(), services.GetRequiredService<RevisionReportStorageRegistration>(),
            instanceId, options.ActiveReadCaps));
        builder.Services.AddSingleton<IRevisionReportReader>(services => services.GetRequiredService<RevisionReportService>());
        builder.Services.AddSingleton<IRevisionReportDeriver>(services => services.GetRequiredService<RevisionReportService>());
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

    internal static FileSystemStorageOptions CreateStorageOptions(string contentRoot, Func<string, string?> readEnvironment)
    {
        try
        {
            var root = readEnvironment("WEFT_PRIVATE_STORAGE_ROOT");
            if (string.IsNullOrWhiteSpace(root))
                throw Unavailable(RevisionReportFailureCategory.StorageIo);
            return new(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot)));
        }
        catch (RevisionReportServiceException) { throw; }
        catch (Exception) { throw Unavailable(RevisionReportFailureCategory.StorageIo); }
    }

    private static int DerivedReadLimit(string? value)
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
