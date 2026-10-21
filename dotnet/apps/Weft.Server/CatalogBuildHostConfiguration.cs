using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.MappingBackends;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Server;

internal sealed record BuildHostRuntime(bool Enabled, string? Image = null, string? StorageRoot = null,
    int DerivedReadLimit = Weft.Domain.RevisionReportPolicy.MaximumStoredObjectByteLimit);

/// <summary>Explicit control/query composition. It registers no compiler, dispatcher, publication or derivation service.</summary>
public static class CatalogBuildHostConfiguration
{
    private const string Key = "build-http";

    internal static BuildHostRuntime ReadRuntime(Func<string, string?> environment, bool catalogEnabled)
    {
        var enabled = environment("WEFT_BUILDS_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("Build API enablement must be true or false.")
        };
        if (!enabled) return new(false);
        if (!catalogEnabled) throw new ArgumentException("Build API requires an enabled catalog.");
        var image = environment("WEFT_FERRULE_WORKER_IMAGE");
        if (image is null || image.Length != 71 || !image.StartsWith("sha256:", StringComparison.Ordinal) || image[7..].Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Build API requires the exact configured immutable image.");
        var root = environment("WEFT_PRIVATE_STORAGE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Build API requires its private report storage root.");
        if (!root.StartsWith('/') || root.EndsWith('/') || root.Contains('\\') || root.Any(char.IsControl) ||
            root.Split('/').Skip(1).Any(value => value.Length == 0 || value is "." or "..") ||
            System.Text.Encoding.UTF8.GetByteCount(root) > 4095 || root.Split('/').Any(value => System.Text.Encoding.UTF8.GetByteCount(value) > 255))
            throw new ArgumentException("Build report storage requires a canonical private Unix path.");
        return new(true, image, root, CatalogRevisionReportHostConfiguration.DerivedReadLimit(environment("WEFT_DERIVED_READ_BYTE_LIMIT")));
    }

    internal static void Configure(IServiceCollection services, IWebHostEnvironment environment, Func<string, string?> read,
        BuildHostRuntime runtime)
    {
        if (!runtime.Enabled) return;
        var session = read("WEFT_CATALOG_AUTH_MODE") == "session";
        string Connection(string name) => session ? SessionHostResources.ReadConnection(read, name + "_FILE") :
            read(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Build API requires {name}.");
        var metadata = Connection("WEFT_BUILD_METADATA_CONNECTION_STRING");
        var controls = Connection("WEFT_BUILD_CONTROL_CONNECTION_STRING");
        var access = Connection("WEFT_BUILD_ACCESS_CONNECTION_STRING");
        var reports = Connection("WEFT_BUILD_REPORT_CONNECTION_STRING");
        var cases = Connection("WEFT_BUILD_CASE_READER_CONNECTION_STRING");
        var evidence = Connection("WEFT_BUILD_EVIDENCE_READER_CONNECTION_STRING");
        var runReader = Connection("WEFT_BUILD_RUN_READER_CONNECTION_STRING");
        var runWriter = Connection("WEFT_BUILD_RUN_WRITER_CONNECTION_STRING");
        var backend = new BuildBackendIdentity("ferrule", FerruleInspectionBackend.EngineRevision, runtime.Image!, FerrulePreviewBackend.AdapterBuild);
        var profile = FerruleBuildCompiler.Profile;
        services.AddWeftBuildApi(new(true, backend, profile));
        services.AddKeyedSingleton<PostgresBuildMetadataReader>(Key, (_, _) => new(metadata));
        services.AddSingleton<IBuildMetadataReader>(provider => provider.GetRequiredKeyedService<PostgresBuildMetadataReader>(Key));
        services.AddKeyedSingleton<PostgresBuildStore>(Key, (_, _) => new(controls, writer: true));
        services.AddKeyedSingleton<PostgresDatabase>(Key, (_, _) => new(access));
        services.AddKeyedSingleton<IProjectReader>(Key, (provider, _) => new PostgresProjectReader(provider.GetRequiredKeyedService<PostgresDatabase>(Key)));
        services.AddKeyedSingleton<PostgresRevisionReportDatabase>(Key, (_, _) => new(reports));
        services.AddKeyedSingleton<IRevisionReportMetadataReader>(Key, (provider, _) => new PostgresRevisionReportMetadataReader(provider.GetRequiredKeyedService<PostgresRevisionReportDatabase>(Key)));
        services.AddKeyedSingleton<PostgresRegressionCaseStore>(Key, (_, _) => new(cases, RegressionCaseDatabaseProfile.Reader));
        services.AddKeyedSingleton<PostgresRegressionRunStore>(Key, (_, _) => new(evidence, writer: false));
        services.AddKeyedSingleton<RegressionRunReadService>(Key, (provider, _) => new(provider.GetRequiredKeyedService<PostgresRegressionRunStore>(Key), provider.GetRequiredKeyedService<PostgresRegressionCaseStore>(Key)));
        services.AddKeyedSingleton<PostgresBuildRunStore>(Key + "-read", (_, _) => new(runReader, BuildRunDatabaseProfile.Reader));
        services.AddKeyedSingleton<PostgresBuildRunStore>(Key + "-write", (_, _) => new(runWriter, BuildRunDatabaseProfile.Writer));
        services.AddSingleton<IBuildRunReader>(provider => provider.GetRequiredKeyedService<PostgresBuildRunStore>(Key + "-read"));
        services.AddSingleton<IBuildRunCommands>(provider => provider.GetRequiredKeyedService<PostgresBuildRunStore>(Key + "-write"));
        services.AddKeyedSingleton<BuildControlService>(Key, (provider, _) => new(provider.GetRequiredKeyedService<PostgresBuildStore>(Key),
            provider.GetRequiredKeyedService<IProjectReader>(Key), provider.GetRequiredKeyedService<IRevisionReportMetadataReader>(Key),
            provider.GetRequiredKeyedService<RegressionRunReadService>(Key)));
        services.AddKeyedSingleton<IRevisionReportReader>(Key, (provider, _) =>
        {
            var storage = new RevisionReportStorageRegistration(checked((int)FileSystemStorageOptions.NativeHardMaximum),
                checked((int)FileSystemStorageOptions.DerivedHardMaximum), runtime.DerivedReadLimit,
                () => provider.GetRequiredKeyedService<LazyPrivateObjectStore>(Key));
            var policy = storage.ApplyTo(new Weft.Domain.RevisionReportPolicy());
            var registry = CatalogRevisionReportHostConfiguration.CreateRegistry(runtime.Image, policy,
                () => throw new InvalidOperationException("Build HTTP does not execute report workers."));
            return new RevisionReportService(provider.GetRequiredKeyedService<IRevisionReportMetadataReader>(Key),
                new UnavailableRevisionReportMetadataStore(), registry, storage, Guid.NewGuid(), policy);
        });
        services.AddKeyedSingleton<LazyPrivateObjectStore>(Key, (_, _) => new(new FileSystemStorageOptions(runtime.StorageRoot!,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath))),
            () => throw new InvalidOperationException("Build HTTP does not publish private objects.")));
        services.AddKeyedSingleton<PersistedBuildIntentFactory>(Key, (provider, _) => new(provider.GetRequiredService<IBuildMetadataReader>(),
            provider.GetRequiredKeyedService<IRevisionReportMetadataReader>(Key), provider.GetRequiredKeyedService<IRevisionReportReader>(Key),
            provider.GetRequiredKeyedService<RegressionRunReadService>(Key), backend, profile));
        services.AddSingleton<IBuildIntentFactory>(provider => new LazyIntentFactory(() => provider.GetRequiredKeyedService<PersistedBuildIntentFactory>(Key)));
        services.AddSingleton<IBuildApprovalWriter>(provider => new LazyApprovalWriter(() => provider.GetRequiredKeyedService<BuildControlService>(Key)));
        services.AddSingleton<IBuildAdmissionReader>(provider => new LazyAdmission(() => provider.GetRequiredKeyedService<BuildControlService>(Key)));
        services.AddSingleton(provider => new BuildApprovalRecoveryService(provider.GetRequiredService<IBuildMetadataReader>(),
            provider.GetRequiredService<IBuildIntentFactory>(), provider.GetRequiredService<IBuildApprovalWriter>(), provider.GetRequiredKeyedService<IProjectReader>(Key)));
        services.AddSingleton(provider => new BuildSubmissionService(provider.GetRequiredService<IBuildMetadataReader>(),
            provider.GetRequiredService<IBuildRunReader>(), provider.GetRequiredService<IBuildRunCommands>(), provider.GetRequiredService<IBuildAdmissionReader>(),
            provider.GetRequiredKeyedService<IProjectReader>(Key), backend, profile));
        services.AddSingleton(provider => new LazyAdministration(() => provider.GetRequiredKeyedService<PostgresBuildStore>(Key)));
        services.AddSingleton<IBuildEnvironmentCommands>(provider => provider.GetRequiredService<LazyAdministration>());
        services.AddSingleton<IBuildApprovalRevocation>(provider => provider.GetRequiredService<LazyAdministration>());
        services.AddSingleton(provider => new BuildAdministrationService(provider.GetRequiredService<IBuildMetadataReader>(),
            provider.GetRequiredService<IBuildEnvironmentCommands>(), provider.GetRequiredService<IBuildApprovalRevocation>(),
            provider.GetRequiredKeyedService<IProjectReader>(Key), profile));
    }

    private sealed class LazyIntentFactory(Func<IBuildIntentFactory> factory) : IBuildIntentFactory
    {
        public Task<BuildIntent?> CreateAsync(ActorContext actor, Weft.Domain.MappingScope mapping, Guid id, BuildApprovalSelection selection, CancellationToken token = default) =>
            factory().CreateAsync(actor, mapping, id, selection, token);
    }
    private sealed class LazyApprovalWriter(Func<IBuildApprovalWriter> factory) : IBuildApprovalWriter
    {
        public Task<BuildControlOutcome> ApproveAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default) => factory().ApproveAsync(actor, intent, token);
    }
    private sealed class LazyAdmission(Func<IBuildAdmissionReader> factory) : IBuildAdmissionReader
    {
        public Task<BuildAdmission?> ReadAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default) => factory().ReadAsync(actor, intent, token);
    }
    private sealed class LazyAdministration(Func<IBuildControlStore> factory) : IBuildEnvironmentCommands, IBuildApprovalRevocation
    {
        public Task<BuildControlOutcome> SaveEnvironmentAsync(ActorContext actor, BuildEnvironment environment, CancellationToken token = default) =>
            factory().SaveEnvironmentAsync(actor, environment, token);
        public Task<bool> RevokeAsync(ActorContext actor, Guid organizationId, Guid projectId, Guid approvalId, CancellationToken token = default) =>
            factory().RevokeAsync(actor, organizationId, projectId, approvalId, token);
    }
}
