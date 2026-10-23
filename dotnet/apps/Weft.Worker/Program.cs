using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Jobs;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.Hosting;
using Weft.Worker;
using Wolverine;

var settings = PreviewWorkerSettings.Read(Environment.GetEnvironmentVariable);
var inspection = InspectionWorkerSettings.Read(Environment.GetEnvironmentVariable, settings.WorkerImage, settings.DockerExecutable);
var previewRunner = PreviewRunnerSettings.Read(Environment.GetEnvironmentVariable, settings);
const string previewRunnerKey = "preview-runner";
if (!settings.PreviewEnabled && inspection is null)
    throw new InvalidOperationException("The worker requires preview execution or the inspection service.");
var builder = Host.CreateApplicationBuilder(args);
var contentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(builder.Environment.ContentRootPath));
if (settings.PreviewEnabled)
{
    builder.Services.AddSingleton(new PreviewWorkerInstance(Guid.NewGuid()));
    builder.Services.AddSingleton(new PreviewRunOptions { Lease = settings.Lease });
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(_ => new PostgresPreviewDatabase(settings.WriterConnectionString!, PreviewDatabaseProfile.Writer));
    builder.Services.AddSingleton(provider => new PostgresPreviewRunStore(provider.GetRequiredService<PostgresPreviewDatabase>()));
    // Read-only: no publication registry, so publication refuses. The store is admitted at first read.
    var storage = settings.Storage!.Bind(contentRoot);
    builder.Services.AddSingleton<IPrivateObjectStore>(_ => new LazyPrivateObjectStore(storage));
    builder.Services.AddKeyedSingleton(previewRunnerKey, (_, _) => previewRunner.CreateRunner(settings));
    builder.Services.AddSingleton<IMappingPreviewBackend>(provider =>
        new FerrulePreviewBackend(provider.GetRequiredKeyedService<IInspectionWorkerRunner>(previewRunnerKey)));
    builder.Services.AddSingleton(provider => new PreviewRunExecutor(provider.GetRequiredService<PostgresPreviewRunStore>(),
        provider.GetRequiredService<IPrivateObjectStore>(), provider.GetRequiredService<IMappingPreviewBackend>(),
        provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<PreviewRunOptions>()));
    builder.Services.AddWolverine(options => PreviewJobs.Configure(options, settings.WriterConnectionString!, PreviewJobRole.Worker));
}
builder.Services.AddWeftRegressionWorker(Environment.GetEnvironmentVariable, () =>
    new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, previewRunner.ProfileIdentity(settings),
        FerrulePreviewBackend.AdapterBuild, PreviewPolicy.Profile));
builder.Services.AddWeftBuildWorkerWithRunner(Environment.GetEnvironmentVariable, () => previewRunner.CreateBuildRunner(settings));
if (inspection is { } hosted)
    builder.Services.AddWeftInspectionWorker(hosted.Transport, hosted.Runner);
await builder.Build().RunAsync();
