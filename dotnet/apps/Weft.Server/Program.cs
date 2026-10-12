using Weft.Api;
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
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapWeftHealth();
app.MapWeftProjectionFixtures();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
// Unknown API routes must not return the Angular document.
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
await app.RunAsync();
