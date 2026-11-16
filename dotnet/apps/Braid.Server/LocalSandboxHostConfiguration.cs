using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Deployment.Sandbox;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;

namespace Braid.Server;

/// <summary>
/// The local deployment sandbox: a simulation for Development hosts only. It composes the existing qualification,
/// release operations, grant and invocation ledger stores with the Docker compiled runtime; Session hosts refuse every
/// setting below at startup.
/// </summary>
internal static class LocalSandboxHostConfiguration
{
    internal const string Key = "local-sandbox";
    internal const string Enabled = "BRAID_LOCAL_SANDBOX_ENABLED";
    internal const string History = "BRAID_LOCAL_SANDBOX_HISTORY_DIRECTORY";
    internal const string RuntimeImage = "BRAID_LOCAL_SANDBOX_RUNTIME_IMAGE";
    internal const string Docker = "BRAID_LOCAL_SANDBOX_DOCKER_EXECUTABLE";
    private static readonly (string Setting, string Role)[] Logins =
    [
        ("BRAID_LOCAL_SANDBOX_QUALIFICATION_WRITER_CONNECTION_STRING", "braid_qualification_writer"),
        ("BRAID_LOCAL_SANDBOX_BUILD_READER_CONNECTION_STRING", "braid_build_reader"),
        ("BRAID_LOCAL_SANDBOX_INVOCATION_WRITER_CONNECTION_STRING", "braid_invocation_writer"),
        ("BRAID_LOCAL_SANDBOX_INVOCATION_GRANT_WRITER_CONNECTION_STRING", "braid_invocation_grant_writer")
    ];
    internal static readonly string[] Settings = [Enabled, History, RuntimeImage, Docker, .. Logins.Select(login => login.Setting)];
    /// <summary>The mapping endpoint's bounds, so a sandbox call admits what an endpoint call admits.</summary>
    private static readonly PreviewLimits Limits = new(1024 * 1024, 1024 * 1024, PreviewPolicy.MaximumArtifacts, PreviewPolicy.MaximumTextBytes);

    internal static void Configure(IServiceCollection services, IWebHostEnvironment environment, Func<string, string?> read, bool catalogEnabled)
    {
        if (!ServerHostOptions.Flag(read, Enabled))
        {
            if (Settings.Any(name => read(name) is not null)) throw Invalid();
            return;
        }
        if (!catalogEnabled || !environment.IsDevelopment() || read("BRAID_CATALOG_AUTH_MODE") is not (null or "development")) throw Invalid();
        var image = read(RuntimeImage) is { Length: 71 } value && value.StartsWith("sha256:", StringComparison.Ordinal) &&
            value[7..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) ? value : throw Invalid();
        var history = read(History) is { } directory && Path.IsPathFullyQualified(directory) ? directory : throw Invalid();
        var docker = read(Docker) ?? "/usr/bin/docker";
        if (!Path.IsPathFullyQualified(docker)) throw Invalid();
        var logins = Logins.ToDictionary(login => login.Role, login => Login(read, login.Setting, login.Role), StringComparer.Ordinal);
        var builds = CatalogBuildHostConfiguration.ReadRuntime(read, true);
        if (!builds.Enabled || !ReleaseOperationsHostConfiguration.ReadEnabled(read, true)) throw Invalid();
        services.AddSingleton(new LocalSandboxOptions(true));
        // The host's Docker CLI budgets apply here too, and the host drains this runner and its reaper on stop.
        var runnerOptions = new DockerInspectionWorkerOptions(docker, image, profile: DockerWorkerProfile.Compiled, budgets: DockerRunnerBudgets.Read(read));
        services.AddKeyedSingleton(Key, runnerOptions);
        services.AddKeyedSingleton<DockerInspectionWorkerRunner>(Key, (provider, _) =>
            new(provider.GetRequiredKeyedService<DockerInspectionWorkerOptions>(Key)));
        services.AddSingleton<IHostedService>(provider =>
            new InspectionWorkerHostLifetime(provider.GetRequiredKeyedService<DockerInspectionWorkerRunner>(Key)));
        services.AddKeyedSingleton<PostgresBuildQualificationStore>(Key, (_, _) => new(logins["braid_qualification_writer"], writer: true));
        services.AddKeyedSingleton<PostgresBuildStore>(Key, (_, _) => new(logins["braid_build_reader"], writer: false));
        services.AddKeyedSingleton<PostgresInvocationLedger>(Key, (_, _) => new(logins["braid_invocation_writer"]));
        services.AddKeyedSingleton<PostgresInvocationGrantStore>(Key, (_, _) => new(logins["braid_invocation_grant_writer"]));
        services.AddSingleton(provider =>
        {
            const string build = "build-http";
            var store = provider.GetRequiredKeyedService<PostgresBuildStore>(Key);
            var runs = provider.GetRequiredKeyedService<PostgresBuildRunStore>(build + "-read");
            var cases = provider.GetRequiredKeyedService<PostgresRegressionCaseStore>(build);
            var regressionRuns = provider.GetRequiredKeyedService<PostgresRegressionRunStore>(build);
            var projects = provider.GetRequiredKeyedService<IProjectReader>(build);
            var admissions = new BuildControlService(store, projects, provider.GetRequiredKeyedService<IRevisionReportMetadataReader>(build),
                provider.GetRequiredKeyedService<RegressionRunReadService>(build));
            var runtime = new FerruleCompiledBuildRuntime(provider.GetRequiredKeyedService<DockerInspectionWorkerRunner>(Key),
                new("ferrule", FerruleInspectionBackend.EngineRevision, builds.Image!, FerrulePreviewBackend.AdapterBuild));
            CompiledBuildExecution Execution(ActorContext actor) => new(runs, admissions, store, new PostgresBuildObjectStore(store, NoNativeObjects.Instance, actor), runtime);
            var releaseReader = provider.GetRequiredKeyedService<PostgresLocalReleaseStore>("operations-reader");
            var ledger = provider.GetRequiredKeyedService<PostgresInvocationLedger>(Key);
            var time = TimeProvider.System;
            return new LocalSandboxService(releaseReader, provider.GetRequiredService<ReleaseOperationsService>(),
                provider.GetRequiredKeyedService<PostgresLocalReleaseStore>("operations-writer"),
                provider.GetRequiredKeyedService<PostgresInvocationGrantStore>(Key), provider.GetRequiredService<IBuildMetadataReader>(),
                actor => new BuildQualificationService(provider.GetRequiredKeyedService<PostgresBuildQualificationStore>(Key), runs, regressionRuns, cases,
                    Execution(actor), projects),
                new MachineInvocationService(ledger, releaseReader, actor => new LocalReleaseInvocation(releaseReader, Execution(actor)), time, Limits),
                ledger, new FileSandboxHistory(history), [new RegressionCaseSampleSource(cases)], [], image, time);
        });
    }

    /// <summary>Exactly the expected restricted login with an explicit password, as the release tool requires.</summary>
    private static string Login(Func<string, string?> read, string setting, string role)
    {
        var value = read(setting);
        if (string.IsNullOrWhiteSpace(value)) throw Invalid();
        NpgsqlConnectionStringBuilder connection;
        try { connection = new(value); }
        catch (Exception error) when (error is ArgumentException or FormatException or KeyNotFoundException) { throw Invalid(); }
        if (connection.Username != role || string.IsNullOrEmpty(connection.Password) || connection.Passfile is not null) throw Invalid();
        return value;
    }

    private static ArgumentException Invalid() => new("The local sandbox configuration is invalid; it is available only to a local Development host.");

    /// <summary>The sandbox reads only compiled artifacts; native mapping bytes are never read here.</summary>
    private sealed class NoNativeObjects : IPrivateObjectStore
    {
        internal static readonly NoNativeObjects Instance = new();
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream bytes,
            CancellationToken cancellationToken = default) => throw new PrivateObjectStorageException(PrivateObjectStorageError.ScopeMismatch);
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default) =>
            throw new PrivateObjectStorageException(PrivateObjectStorageError.ScopeMismatch);
    }
}
