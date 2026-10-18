using Weft.Api;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Server;

ServerHostOptions hostOptions;
try { hostOptions = ServerHostOptions.Read(Environment.GetEnvironmentVariable); }
catch (Exception error) when (SessionHostResources.Nonfatal(error))
{
    Console.Error.WriteLine("WeftHostConfigurationFailed");
    return 1;
}
if (hostOptions.Session)
    return await SessionHostEntrypoint.RunAsync(hostOptions, Environment.GetEnvironmentVariable);

var address = DevelopmentListenAddress.Parse(Environment.GetEnvironmentVariable("WEFT_DEV_LISTEN_ADDR"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.WebHost.ConfigureKestrel(server => DevelopmentListenAddress.ConfigureKestrel(server, address));
builder.Services.AddWeftApi();
builder.Services.SuppressRequestDiagnostics();
CatalogHostConfiguration.Configure(builder, address);
builder.Services.AddWeftAuthenticationHttp(new(hostOptions.Mode, hostOptions.Origin), builder.Environment);
if (builder.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE") is { Length: > 0 } workerImage)
{
    builder.Services.AddSingleton<IInspectionWorkerRunner>(_ => new DockerInspectionWorkerRunner(
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
app.MapWeftCatalog();
app.MapWeftAuthentication();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
// Unknown API routes must not return the Angular document.
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
await app.RunAsync();
return 0;
