using Microsoft.Extensions.Hosting;
using Weft.FerruleService;

// The shell-less image's own health check for platforms without HTTP probes.
if (args is ["probe"])
    return await FerruleServiceProbe.RunAsync(Environment.GetEnvironmentVariable);
if (args is ["identity"])
    return await FerruleServiceIdentity.RunAsync(Environment.GetEnvironmentVariable);
if (args is ["runtime-identity"])
    return await FerruleRuntimeIdentity.RunAsync(Environment.GetEnvironmentVariable);

FerruleServiceSettings settings;
FerruleServiceRegistration.ServiceRunners runners;
try
{
    settings = FerruleServiceSettings.Read(Environment.GetEnvironmentVariable);
    using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    runners = await FerruleServiceRegistration.CreateRunnersAsync(settings, startup.Token);
}
catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
{
    // Settings never echo values; invalid or incomplete configuration, an unusable worker or temporary root, or
    // an unenforced compile sandbox stops the service.
    Console.Error.WriteLine("WeftFerruleServiceConfigurationFailed");
    return 1;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddFerruleService(settings, runners);
await builder.Build().RunAsync();
return 0;
