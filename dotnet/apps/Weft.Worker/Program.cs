using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.Application;
using Weft.Jobs;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;
using Weft.Worker;
using Wolverine;

var settings = PreviewWorkerSettings.Read(Environment.GetEnvironmentVariable);
var builder = Host.CreateApplicationBuilder(args);
var contentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(builder.Environment.ContentRootPath));
builder.Services.AddSingleton(new PreviewWorkerInstance(Guid.NewGuid()));
builder.Services.AddSingleton(new PreviewRunOptions { Lease = settings.Lease });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new PostgresPreviewDatabase(settings.WriterConnectionString, PreviewDatabaseProfile.Writer));
builder.Services.AddSingleton(provider => new PostgresPreviewRunStore(provider.GetRequiredService<PostgresPreviewDatabase>()));
builder.Services.AddSingleton<IPrivateObjectStore>(_ =>
    FileSystemPrivateObjectStore.OpenV2(new FileSystemStorageOptions(settings.StorageRoot, contentRoot)));
builder.Services.AddSingleton<IMappingPreviewBackend>(_ => new FerrulePreviewBackend(new DockerInspectionWorkerRunner(
    new DockerInspectionWorkerOptions(settings.DockerExecutable, settings.WorkerImage, profile: DockerWorkerProfile.Preview))));
builder.Services.AddSingleton(provider => new PreviewRunExecutor(provider.GetRequiredService<PostgresPreviewRunStore>(),
    provider.GetRequiredService<IPrivateObjectStore>(), provider.GetRequiredService<IMappingPreviewBackend>(),
    provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<PreviewRunOptions>()));
builder.Services.AddWolverine(options => PreviewJobs.Configure(options, settings.WriterConnectionString, PreviewJobRole.Worker));
await builder.Build().RunAsync();
