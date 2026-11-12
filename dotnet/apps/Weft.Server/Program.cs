using Weft.Api;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Api.Chat;
using Weft.Api.Specs;
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
SpecHostConfiguration.Configure(builder.Services, Environment.GetEnvironmentVariable, session: false);
CreationHostConfiguration.Configure(builder.Services, Environment.GetEnvironmentVariable, session: false);
builder.Services.AddWeftAuthenticationHttp(new(hostOptions.Mode, hostOptions.Origin), builder.Environment);
if (builder.Environment.IsDevelopment())
    DevelopmentFerruleRunner.Configure(builder.Services, Environment.GetEnvironmentVariable);
var app = builder.Build();
BrowserContentPolicy.Use(app, hostOptions.Origin);
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapWeftHealth();
app.MapWeftProjectionFixtures();
app.MapWeftCatalog();
app.MapWeftSpecs();
Weft.Api.Creation.CreationEndpoints.MapWeftCreation(app);
app.MapWeftRegressionCases();
app.MapWeftRegressionRuns();
app.MapWeftAutomation();
app.MapWeftBuilds();
app.MapWeftReleaseOperations();
app.MapWeftChat();
app.MapWeftHelp();
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
