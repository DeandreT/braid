using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;

namespace Braid.Jobs;

/// <summary>Default-off, machine-only execution; actor submission and browser routes are separate.</summary>
public static class BuildWorkerRegistration
{
    private const string Key = "build-worker";

    public static IServiceCollection AddBraidBuildWorker(this IServiceCollection services,
        Func<string, string?> environment, Func<DockerInspectionWorkerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Register(services, environment, () =>
        {
            var configured = options();
            if (configured is null || configured.Profile != DockerWorkerProfile.Build)
                throw new ArgumentException("The build worker requires the fixed compiler profile.");
            return () => new DockerInspectionWorkerRunner(configured);
        });
    }

    /// <summary>The host supplies an explicitly configured compile-profile runner; disabled workers never create it.</summary>
    public static IServiceCollection AddBraidBuildWorkerWithRunner(this IServiceCollection services,
        Func<string, string?> environment, Func<IInspectionWorkerRunner> createRunner)
    {
        ArgumentNullException.ThrowIfNull(createRunner);
        return Register(services, environment, () => createRunner);
    }

    private static IServiceCollection Register(IServiceCollection services,
        Func<string, string?> environment, Func<Func<IInspectionWorkerRunner>> prepareRunner)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        var enabled = environment("BRAID_BUILD_WORKER_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("Build worker enablement must be true or false.")
        };
        if (!enabled) return services;
        string Required(string name) => environment(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Build worker requires {name}.");
        var dispatcher = Required("BRAID_BUILD_DISPATCHER_CONNECTION_STRING");
        var writer = Required("BRAID_BUILD_WRITER_CONNECTION_STRING");
        var access = Required("BRAID_BUILD_ACCESS_CONNECTION_STRING");
        var reports = Required("BRAID_BUILD_REPORT_READER_CONNECTION_STRING");
        var runs = Required("BRAID_BUILD_REGRESSION_RUN_READER_CONNECTION_STRING");
        var cases = Required("BRAID_BUILD_CASE_READER_CONNECTION_STRING");
        var createRunner = prepareRunner();

        services.AddKeyedSingleton<PostgresBuildRunStore>(Key, (_, _) => new(dispatcher, BuildRunDatabaseProfile.Dispatcher));
        services.AddSingleton<IBuildRunStore>(provider => provider.GetRequiredKeyedService<PostgresBuildRunStore>(Key));
        services.AddSingleton<IBuildRunQueue>(provider => provider.GetRequiredKeyedService<PostgresBuildRunStore>(Key));
        services.AddKeyedSingleton<PostgresDatabase>(Key, (_, _) => new(access));
        services.AddKeyedSingleton<PostgresRevisionReportDatabase>(Key, (_, _) => new(reports));
        services.AddKeyedSingleton<PostgresRegressionRunStore>(Key, (_, _) => new(runs, writer: false));
        services.AddKeyedSingleton<PostgresRegressionCaseStore>(Key, (_, _) => new(cases, RegressionCaseDatabaseProfile.Reader));
        // Shared runner ownership and cleanup-failure latching apply across every claimed build.
        services.AddKeyedSingleton<IInspectionWorkerRunner>(Key, (_, _) => createRunner());
        services.AddSingleton<IBuildRunExecution>(provider => new Execution(writer,
            provider.GetRequiredService<IBuildRunStore>(), provider.GetRequiredService<IPrivateObjectStore>(),
            new PostgresProjectReader(provider.GetRequiredKeyedService<PostgresDatabase>(Key)),
            new PostgresRevisionReportMetadataReader(provider.GetRequiredKeyedService<PostgresRevisionReportDatabase>(Key)),
            new RegressionRunReadService(provider.GetRequiredKeyedService<PostgresRegressionRunStore>(Key),
                provider.GetRequiredKeyedService<PostgresRegressionCaseStore>(Key)),
            provider.GetRequiredKeyedService<IInspectionWorkerRunner>(Key)));
        services.AddSingleton<BuildRunExecutor>();
        services.AddSingleton<BuildRunPump>();
        services.AddHostedService<BuildWorkerService>();
        return services;
    }

    private sealed class Execution(string writer, IBuildRunStore runs, IPrivateObjectStore native, IProjectReader projects,
        IRevisionReportMetadataReader reports, RegressionRunReadService evidence, IInspectionWorkerRunner runner) : IBuildRunExecution, IExecutionAdmission
    {
        public bool CanExecute => runner is not IExecutionAdmission { CanExecute: false };

        public async Task<BuildExecutionResult> ExecuteAsync(BuildRunClaim claim, CancellationToken token = default)
        {
            var actor = new ActorContext(claim.Actor);
            await using var controls = new PostgresBuildStore(writer, writer: true, claim);
            var admission = new ClaimedAdmission(runs, claim, new BuildControlService(controls, projects, reports, evidence));
            var objects = new PostgresBuildObjectStore(controls, native, actor);
            var compiler = new FerruleBuildCompiler(runner, new(actor, admission, objects));
            return await new BuildWorkflow(admission, compiler, controls, objects).ExecuteAsync(actor, claim.Intent, token).ConfigureAwait(false);
        }
    }
    private sealed class ClaimedAdmission(IBuildRunStore runs, BuildRunClaim claim, IBuildAdmissionReader inner) : IBuildAdmissionReader
    {
        public async Task<BuildAdmission?> ReadAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default)
        {
            if (actor.PrincipalId != claim.Actor || intent.Fingerprint != claim.Intent.Fingerprint ||
                !await runs.RenewAsync(claim, token).ConfigureAwait(false)) return null;
            return await inner.ReadAsync(actor, intent, token).ConfigureAwait(false);
        }
    }
}
