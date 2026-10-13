using Weft.Api;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Server;

var address = DevelopmentListenAddress.Parse(Environment.GetEnvironmentVariable("WEFT_DEV_LISTEN_ADDR"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.WebHost.ConfigureKestrel(server => DevelopmentListenAddress.ConfigureKestrel(server, address));
builder.Services.AddWeftApi();
if (builder.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE") is { Length: > 0 } workerImage)
{
    builder.Services.AddSingleton<IInspectionWorkerRunner>(new DockerInspectionWorkerRunner(
        new DockerInspectionWorkerOptions("/usr/bin/docker", workerImage)));
    builder.Services.AddSingleton<FerruleInspectionBackend>();
    builder.Services.AddSingleton<IMappingInspectionBackend>(services => services.GetRequiredService<FerruleInspectionBackend>());
    builder.Services.AddSingleton<IMappingProjectionBackend>(services => services.GetRequiredService<FerruleInspectionBackend>());
}
builder.Services.AddWeftNativeExamples();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapWeftHealth();
app.MapWeftProjectionFixtures();
app.MapWeftNativeExamples();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
// Unknown API routes must not return the Angular document.
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
await app.RunAsync();
