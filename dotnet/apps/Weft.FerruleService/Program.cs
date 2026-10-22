using Microsoft.Extensions.Hosting;
using Weft.FerruleService;

// The shell-less image's own health check for platforms without HTTP probes.
if (args is ["probe"])
    return await FerruleServiceProbe.RunAsync(Environment.GetEnvironmentVariable);

FerruleServiceSettings settings;
try { settings = FerruleServiceSettings.Read(Environment.GetEnvironmentVariable); }
catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
{
    // Settings never echo values; invalid or incomplete configuration stops the service.
    Console.Error.WriteLine("WeftFerruleServiceConfigurationFailed");
    return 1;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddFerruleService(settings);
await builder.Build().RunAsync();
return 0;
