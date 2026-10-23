using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;

namespace Weft.MachineEndpoint;

/// <summary>
/// The endpoint's owned stores, runtime and token validator. Each call composes the existing release invocation
/// path under the current release's publisher; the compiled artifact runs only in the isolated runtime.
/// </summary>
internal sealed class MappingEndpointResources : IAsyncDisposable
{
    /// <summary>Endpoint inputs and outputs are smaller than the preview ceilings so a response fits its retained bound.</summary>
    internal static readonly PreviewLimits Limits = new(1024 * 1024, 1024 * 1024, PreviewPolicy.MaximumArtifacts, PreviewPolicy.MaximumTextBytes);

    private readonly PostgresInvocationLedger ledger;
    private readonly PostgresLocalReleaseStore releases;
    private readonly PostgresBuildStore builds;
    private readonly PostgresBuildRunStore runs;
    private readonly PostgresDatabase access;
    private readonly PostgresRevisionReportDatabase reports;
    private readonly PostgresRegressionRunStore regressionRuns;
    private readonly PostgresRegressionCaseStore cases;
    private readonly DockerInspectionWorkerRunner runner;
    private readonly IMachineTokenValidator validator;

    internal MappingEndpointResources(MappingEndpointSettings settings, IMachineTokenValidator validator, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(validator); ArgumentNullException.ThrowIfNull(time);
        this.validator = validator;
        Settings = settings;
        ledger = new(settings.Connections["weft_invocation_writer"]);
        releases = new(settings.Connections["weft_release_reader"], writer: false);
        builds = new(settings.Connections["weft_build_reader"], writer: false);
        runs = new(settings.Connections["weft_build_run_reader"], BuildRunDatabaseProfile.Reader);
        access = new(settings.Connections["weft_runtime"]);
        reports = new(settings.Connections["weft_report_reader"]);
        regressionRuns = new(settings.Connections["weft_run_reader"], writer: false);
        cases = new(settings.Connections["weft_case_reader"], RegressionCaseDatabaseProfile.Reader);
        runner = new(new(settings.DockerExecutable, settings.RuntimeImage, profile: DockerWorkerProfile.Compiled));
        var runtime = new FerruleCompiledBuildRuntime(runner,
            new("ferrule", FerruleInspectionBackend.EngineRevision, settings.CompilerImage, FerrulePreviewBackend.AdapterBuild));
        var admissions = new BuildControlService(builds, new PostgresProjectReader(access), new PostgresRevisionReportMetadataReader(reports),
            new RegressionRunReadService(regressionRuns, cases));
        Service = new(ledger, releases, actor => new LocalReleaseInvocation(releases,
            new CompiledBuildExecution(runs, admissions, builds, new PostgresBuildObjectStore(builds, NoNativeObjects.Instance, actor), runtime)), time, Limits);
        Calls = new(settings.TargetReference, settings.ConcurrencyLimit, validator, Service, time, MappingEndpointRoutes.Work, MappingEndpointRoutes.Deadline);
    }

    internal MappingEndpointSettings Settings { get; }
    internal MachineInvocationService Service { get; }
    internal MappingEndpointCalls Calls { get; }
    internal IMachineTokenValidator Validator => validator;
    internal IMachineInvocationLedger Ledger => ledger;

    public async ValueTask DisposeAsync()
    {
        // The runner joins every compiled-runtime container before the stores close.
        try { await runner.DisposeAsync(); }
        finally
        {
            if (validator is IAsyncDisposable owned) await owned.DisposeAsync();
            await ledger.DisposeAsync(); await releases.DisposeAsync(); await builds.DisposeAsync(); await runs.DisposeAsync();
            await access.DisposeAsync(); await reports.DisposeAsync(); await regressionRuns.DisposeAsync(); await cases.DisposeAsync();
        }
    }

    /// <summary>The endpoint reads only the compiled artifact; native mapping bytes are never read here.</summary>
    private sealed class NoNativeObjects : IPrivateObjectStore
    {
        internal static readonly NoNativeObjects Instance = new();
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream bytes,
            CancellationToken cancellationToken = default) => throw new PrivateObjectStorageException(PrivateObjectStorageError.ScopeMismatch);
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default) =>
            throw new PrivateObjectStorageException(PrivateObjectStorageError.ScopeMismatch);
    }
}
