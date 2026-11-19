using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;
using Braid.Storage.FileSystem;
using Braid.Storage.Hosting;

namespace Braid.Server;

internal sealed record BuildHostRuntime(bool Enabled, string? Image = null, PrivateStorageSettings? Storage = null,
    int DerivedReadLimit = Braid.Domain.RevisionReportPolicy.MaximumStoredObjectByteLimit, CsvBuildHostSettings? Csv = null, CsvBuildHostSettings? Xml = null);

/// <summary>Explicit control/query composition, with optional read-only CSV boundary inspection. No compiler execution queue or publication is registered.</summary>
public static class CatalogBuildHostConfiguration
{
    private const string Key = "build-http";

    internal static BuildHostRuntime ReadRuntime(Func<string, string?> environment, bool catalogEnabled)
    {
        var enabled = environment("BRAID_BUILDS_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("Build API enablement must be true or false.")
        };
        if (!enabled) return new(false);
        if (!catalogEnabled) throw new ArgumentException("Build API requires an enabled catalog.");
        var image = environment("BRAID_FERRULE_WORKER_IMAGE");
        if (image is null || image.Length != 71 || !image.StartsWith("sha256:", StringComparison.Ordinal) || image[7..].Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Build API requires the exact configured immutable image.");
        image = FerruleRunnerSelection.ProfileIdentity(environment, environment("BRAID_CATALOG_AUTH_MODE") == "session"
            ? FerruleRunnerKind.InspectionWorker : FerruleRunnerKind.Docker);
        var csvEnabled = environment(CsvBuildHostSettings.EnabledSetting) switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("CSV build enablement must be true or false.")
        };
        var xmlEnabled = BuildWorkerProtocol.XmlBuildEnabled(environment);
        var selection = csvEnabled || xmlEnabled ? CsvBuildHostSettings.Read(environment, environment("BRAID_CATALOG_AUTH_MODE") == "session", image) : null;
        PrivateStorageSettings storage;
        try { storage = PrivateStorageSettings.Read(environment); }
        catch (InvalidOperationException) { throw new ArgumentException("Build API requires its private report storage."); }
        return new(true, image, storage, CatalogRevisionReportHostConfiguration.DerivedReadLimit(environment("BRAID_DERIVED_READ_BYTE_LIMIT")), csvEnabled ? selection : null, xmlEnabled ? selection : null);
    }

    internal static void Configure(IServiceCollection services, IWebHostEnvironment environment, Func<string, string?> read,
        BuildHostRuntime runtime)
    {
        if (!runtime.Enabled) return;
        var session = read("BRAID_CATALOG_AUTH_MODE") == "session";
        string Connection(string name) => session ? SessionHostResources.ReadConnection(read, name + "_FILE") :
            read(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Build API requires {name}.");
        var metadata = Connection("BRAID_BUILD_METADATA_CONNECTION_STRING");
        var controls = Connection("BRAID_BUILD_CONTROL_CONNECTION_STRING");
        var access = Connection("BRAID_BUILD_ACCESS_CONNECTION_STRING");
        var reports = Connection("BRAID_BUILD_REPORT_CONNECTION_STRING");
        var cases = Connection("BRAID_BUILD_CASE_READER_CONNECTION_STRING");
        var evidence = Connection("BRAID_BUILD_EVIDENCE_READER_CONNECTION_STRING");
        var runReader = Connection("BRAID_BUILD_RUN_READER_CONNECTION_STRING");
        var runWriter = Connection("BRAID_BUILD_RUN_WRITER_CONNECTION_STRING");
        var backend = new BuildBackendIdentity("ferrule", FerruleInspectionBackend.EngineRevision, runtime.Image!, FerrulePreviewBackend.AdapterBuild);
        var profile = FerruleBuildCompiler.Profile;
        var documentBackend = new BuildBackendIdentity("ferrule", FerruleInspectionBackend.EngineRevision, runtime.Image!, FerrulePreviewBackend.DocumentAdapterBuild);
        var documentProfile = FerruleBuildCompiler.DocumentProfile;
        var interchangeProfile = FerruleBuildCompiler.X12InterchangesProfile;
        var csvBackend = runtime.Csv is null ? null : new BuildBackendIdentity("ferrule", FerruleInspectionBackend.EngineRevision,
            runtime.Image!, FerrulePreviewBackend.CsvAdapterBuild);
        var csvProfile = runtime.Csv is null ? null : FerruleBuildCompiler.CsvProfile;
        var xmlBackend = runtime.Xml is null ? null : new BuildBackendIdentity("ferrule", BuildWorkerProtocol.Engine, runtime.Image!, FerrulePreviewBackend.XmlAdapterBuild);
        var xmlProfile = runtime.Xml is null ? null : FerruleBuildCompiler.XmlProfile;
        services.AddBraidBuildApi(new(true, backend, profile, documentBackend, documentProfile, interchangeProfile, csvBackend, csvProfile, xmlBackend, xmlProfile));
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
            var policy = storage.ApplyTo(new Braid.Domain.RevisionReportPolicy());
            var registry = CatalogRevisionReportHostConfiguration.CreateRegistry(runtime.Image, policy,
                () => throw new InvalidOperationException("Build HTTP does not execute report workers."));
            return new RevisionReportService(provider.GetRequiredKeyedService<IRevisionReportMetadataReader>(Key),
                new UnavailableRevisionReportMetadataStore(), registry, storage, Guid.NewGuid(), policy);
        });
        services.AddKeyedSingleton<LazyPrivateObjectStore>(Key, (_, _) => new(runtime.Storage!.Bind(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath))),
            () => throw new InvalidOperationException("Build HTTP does not publish private objects.")));
        if ((runtime.Csv ?? runtime.Xml) is { } compilerTransport) CsvBuildHostConfiguration.Register(services, compilerTransport);
        if (runtime.Xml is not null) services.AddKeyedSingleton<ICsvBuildBoundaryReader>(Key + "-xml", (provider, _) => new FerruleCsvBuildBoundaryReader(
            provider.GetRequiredService<CsvBuildHostRunner>(), provider.GetRequiredKeyedService<IRevisionReportMetadataReader>(Key),
            provider.GetRequiredKeyedService<IProjectReader>(Key), provider.GetRequiredKeyedService<LazyPrivateObjectStore>(Key), xml: true));
        if (runtime.Csv is not null)
        {
            // A CSV build-only host already has qualified native metadata and private storage.
            // Reuse those read ports for admitted CSV preview/case requests without replacing
            // reporting/ingestion ports or resolving storage, transport, or credentials here.
            services.TryAddSingleton<IRevisionReportMetadataReader>(provider =>
                provider.GetRequiredKeyedService<IRevisionReportMetadataReader>(Key));
            services.TryAddSingleton<IPrivateObjectStore>(provider =>
                provider.GetRequiredKeyedService<LazyPrivateObjectStore>(Key));
            services.AddKeyedSingleton<FerruleCsvBuildBoundaryReader>(Key, (provider, _) => new(
                provider.GetRequiredService<CsvBuildHostRunner>(), provider.GetRequiredKeyedService<IRevisionReportMetadataReader>(Key),
                provider.GetRequiredKeyedService<IProjectReader>(Key), provider.GetRequiredKeyedService<LazyPrivateObjectStore>(Key)));
            services.AddSingleton<ICsvBuildBoundaryReader>(provider => new LazyCsvBoundaryReader(() => provider.GetRequiredKeyedService<FerruleCsvBuildBoundaryReader>(Key)));
        }
        services.AddKeyedSingleton<PersistedBuildIntentFactory>(Key, (provider, _) => new(provider.GetRequiredService<IBuildMetadataReader>(),
            provider.GetRequiredKeyedService<IRevisionReportMetadataReader>(Key), provider.GetRequiredKeyedService<IRevisionReportReader>(Key),
            provider.GetRequiredKeyedService<RegressionRunReadService>(Key), backend, profile, documentBackend, documentProfile, interchangeProfile,
            csvBackend, csvProfile, provider.GetService<ICsvBuildBoundaryReader>(), xmlBackend, xmlProfile, provider.GetKeyedService<ICsvBuildBoundaryReader>(Key + "-xml")));
        services.AddSingleton<IBuildIntentFactory>(provider => new LazyIntentFactory(() => provider.GetRequiredKeyedService<PersistedBuildIntentFactory>(Key)));
        services.AddSingleton<IBuildApprovalWriter>(provider => new LazyApprovalWriter(() => provider.GetRequiredKeyedService<BuildControlService>(Key)));
        services.AddSingleton<IBuildAdmissionReader>(provider => new LazyAdmission(() => provider.GetRequiredKeyedService<BuildControlService>(Key)));
        services.AddSingleton(provider => new BuildApprovalRecoveryService(provider.GetRequiredService<IBuildMetadataReader>(),
            provider.GetRequiredService<IBuildIntentFactory>(), provider.GetRequiredService<IBuildApprovalWriter>(), provider.GetRequiredKeyedService<IProjectReader>(Key)));
        services.AddSingleton(provider => new BuildSubmissionService(provider.GetRequiredService<IBuildMetadataReader>(),
            provider.GetRequiredService<IBuildRunReader>(), provider.GetRequiredService<IBuildRunCommands>(), provider.GetRequiredService<IBuildAdmissionReader>(),
            provider.GetRequiredKeyedService<IProjectReader>(Key), backend, profile, documentBackend, documentProfile, interchangeProfile, csvBackend, csvProfile, xmlBackend, xmlProfile));
        services.AddSingleton(provider => new LazyAdministration(() => provider.GetRequiredKeyedService<PostgresBuildStore>(Key)));
        services.AddSingleton<IBuildEnvironmentCommands>(provider => provider.GetRequiredService<LazyAdministration>());
        services.AddSingleton<IBuildApprovalRevocation>(provider => provider.GetRequiredService<LazyAdministration>());
        services.AddSingleton(provider => new BuildAdministrationService(provider.GetRequiredService<IBuildMetadataReader>(),
            provider.GetRequiredService<IBuildEnvironmentCommands>(), provider.GetRequiredService<IBuildApprovalRevocation>(),
            provider.GetRequiredKeyedService<IProjectReader>(Key), profile, documentProfile, interchangeProfile, csvProfile, xmlProfile));
    }

    private sealed class LazyCsvBoundaryReader(Func<ICsvBuildBoundaryReader> factory) : ICsvBuildBoundaryReader
    {
        public Task<CsvBuildBoundaryObservation?> ReadAsync(ActorContext actor, RevisionReportSource source, CancellationToken token = default) =>
            factory().ReadAsync(actor, source, token);
    }

    private sealed class LazyIntentFactory(Func<IBuildIntentFactory> factory) : IBuildIntentFactory
    {
        public Task<BuildIntent?> CreateAsync(ActorContext actor, Braid.Domain.MappingScope mapping, Guid id, BuildApprovalSelection selection, CancellationToken token = default) =>
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
