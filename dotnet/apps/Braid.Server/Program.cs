using Braid.Api;
using Braid.Api.Auth;
using Braid.Api.Catalog;
using Braid.Api.Chat;
using Braid.Api.Specs;
using Braid.Server;

ServerHostOptions hostOptions;
try { hostOptions = ServerHostOptions.Read(Environment.GetEnvironmentVariable); }
catch (Exception error) when (SessionHostResources.Nonfatal(error))
{
    Console.Error.WriteLine("BraidHostConfigurationFailed");
    return 1;
}
if (hostOptions.Session)
    return await SessionHostEntrypoint.RunAsync(hostOptions, Environment.GetEnvironmentVariable);

var address = DevelopmentListenAddress.Parse(Environment.GetEnvironmentVariable("BRAID_DEV_LISTEN_ADDR"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.WebHost.ConfigureKestrel(server => DevelopmentListenAddress.ConfigureKestrel(server, address));
builder.Services.AddBraidApi();
builder.Services.SuppressRequestDiagnostics();
CatalogHostConfiguration.Configure(builder, address);
SpecHostConfiguration.Configure(builder.Services, Environment.GetEnvironmentVariable, session: false);
CreationHostConfiguration.Configure(builder.Services, Environment.GetEnvironmentVariable, session: false);
var authenticationOptions = new AuthenticationHttpOptions(hostOptions.Mode, hostOptions.Origin);
builder.Services.AddBraidAuthenticationHttp(authenticationOptions, builder.Environment);
if (builder.Environment.IsDevelopment())
    DevelopmentFerruleRunner.Configure(builder.Services, Environment.GetEnvironmentVariable);
var app = builder.Build();
app.UseBraidBrowserSecurityHeaders();
BrowserContentPolicy.Use(app, authenticationOptions);
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapBraidHealth();
app.MapBraidProjectionFixtures();
app.MapBraidCatalog();
app.MapBraidSpecs();
Braid.Api.Creation.CreationEndpoints.MapBraidCreation(app);
app.MapBraidRegressionCases();
app.MapBraidRegressionRuns();
app.MapBraidAutomation();
app.MapBraidBuilds();
app.MapBraidReleaseOperations();
app.MapBraidLocalSandbox();
app.MapBraidChat();
app.MapBraidHelp();
app.MapBraidAuthentication();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
// Unknown API routes must not return the Angular document.
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
await app.RunAsync();
return 0;
