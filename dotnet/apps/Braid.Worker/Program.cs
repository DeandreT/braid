using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Jobs;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;
using Braid.Storage.FileSystem;
using Braid.Storage.Hosting;
using Braid.Worker;
using Wolverine;

// Database credentials may arrive as protected private files; every other setting is read unchanged.
var read = WorkerCredentialSettings.Resolve(Environment.GetEnvironmentVariable);
var settings = PreviewWorkerSettings.Read(read);
var xmlPreview = XmlPreviewAdmission.Enabled(read, XmlPreviewAdmission.EnabledSetting, settings.PreviewEnabled);
var xmlRegression = XmlPreviewAdmission.Enabled(read, XmlPreviewAdmission.RegressionEnabledSetting, read("BRAID_REGRESSION_WORKER_ENABLED") == "true");
var inspection = InspectionWorkerSettings.Read(read, settings.WorkerImage, settings.DockerExecutable);
var previewRunner = PreviewRunnerSettings.Read(read, settings);
const string previewRunnerKey = "preview-runner";
var lifecycle = read(LifecycleDeletionWorker.EnabledSetting) == "true";
if (!settings.PreviewEnabled && inspection is null && read("BRAID_NOTIFICATIONS_ENABLED") != "true" && !lifecycle)
    throw new InvalidOperationException("The worker requires execution, inspection or notifications.");
var builder = Host.CreateApplicationBuilder(args);
var contentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(builder.Environment.ContentRootPath));
if (settings.PreviewEnabled)
{
    builder.Services.AddSingleton(new PreviewWorkerInstance(Guid.NewGuid()));
    builder.Services.AddSingleton(new PreviewRunOptions { Lease = settings.Lease, XmlEnabled = xmlPreview, NativeFormats = NativeDocumentFormat.EnabledFormats(read, false) });
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(_ => new PostgresPreviewDatabase(settings.WriterConnectionString!, PreviewDatabaseProfile.Writer));
    builder.Services.AddSingleton(provider => new PostgresPreviewRunStore(provider.GetRequiredService<PostgresPreviewDatabase>()));
    // Read-only: no publication registry, so publication refuses. The store is admitted at first read.
    var storage = settings.Storage!.Bind(contentRoot);
    builder.Services.AddSingleton<IPrivateObjectStore>(_ => new LazyPrivateObjectStore(storage));
    builder.Services.AddKeyedSingleton(previewRunnerKey, (_, _) => previewRunner.CreateRunner(settings));
    builder.Services.AddBraidPreviewBackends(provider => provider.GetRequiredKeyedService<IInspectionWorkerRunner>(previewRunnerKey),
        xmlPreview, xmlRegression, NativeDocumentFormat.EnabledFormats(read, false), NativeDocumentFormat.EnabledFormats(read, true));
    builder.Services.AddSingleton(provider => new PreviewRunExecutor(provider.GetRequiredService<PostgresPreviewRunStore>(),
        provider.GetRequiredService<IPrivateObjectStore>(), provider.GetRequiredService<IMappingPreviewBackend>(),
        provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<PreviewRunOptions>()));
    builder.Services.AddWolverine(options => PreviewJobs.Configure(options, settings.WriterConnectionString!, PreviewJobRole.Worker));
}
builder.Services.AddBraidRegressionWorker(read, () =>
    new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, previewRunner.ProfileIdentity(settings),
        FerrulePreviewBackend.AdapterBuild, PreviewPolicy.Profile), () =>
    new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, previewRunner.ProfileIdentity(settings),
        FerrulePreviewBackend.DocumentAdapterBuild, DocumentPreviewPolicy.Profile),
    (path, maximum) => ProtectedHostFiles.ReadText(path, maximum));
builder.Services.AddBraidNotificationWorker(read, (path, maximum) => ProtectedHostFiles.ReadText(path, maximum));
builder.Services.AddBraidBuildWorkerWithRunner(read, () => previewRunner.CreateBuildRunner(settings));
if (lifecycle)
{
    var connection = read(LifecycleDeletionWorker.ConnectionSetting) is { Length: > 0 } value ? value
        : throw new InvalidOperationException("The lifecycle worker requires its own restricted connection.");
    var storage = PrivateStorageSettings.Read(read).Bind(contentRoot);
    builder.Services.AddHostedService(_ => new LifecycleDeletionWorker(connection, storage));
}
if (inspection is { } hosted)
    builder.Services.AddBraidInspectionWorker(hosted.Transport, hosted.Runner);
await builder.Build().RunAsync();
