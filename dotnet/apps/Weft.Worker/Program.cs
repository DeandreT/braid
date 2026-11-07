using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Jobs;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;
using Weft.Storage.Hosting;
using Weft.Worker;
using Wolverine;

// Database credentials may arrive as protected private files; every other setting is read unchanged.
var read = WorkerCredentialSettings.Resolve(Environment.GetEnvironmentVariable);
var settings = PreviewWorkerSettings.Read(read);
var inspection = InspectionWorkerSettings.Read(read, settings.WorkerImage, settings.DockerExecutable);
var previewRunner = PreviewRunnerSettings.Read(read, settings);
const string previewRunnerKey = "preview-runner";
if (!settings.PreviewEnabled && inspection is null && read("WEFT_NOTIFICATIONS_ENABLED") != "true")
    throw new InvalidOperationException("The worker requires execution, inspection or notifications.");
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
builder.Services.AddWeftRegressionWorker(read, () =>
    new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, previewRunner.ProfileIdentity(settings),
        FerrulePreviewBackend.AdapterBuild, PreviewPolicy.Profile), () =>
    new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, previewRunner.ProfileIdentity(settings),
        FerrulePreviewBackend.DocumentAdapterBuild, DocumentPreviewPolicy.Profile),
    (path, maximum) => ProtectedHostFiles.ReadText(path, maximum));
builder.Services.AddWeftNotificationWorker(read, (path, maximum) => ProtectedHostFiles.ReadText(path, maximum));
builder.Services.AddWeftBuildWorkerWithRunner(read, () => previewRunner.CreateBuildRunner(settings));
if (inspection is { } hosted)
    builder.Services.AddWeftInspectionWorker(hosted.Transport, hosted.Runner);
await builder.Build().RunAsync();
