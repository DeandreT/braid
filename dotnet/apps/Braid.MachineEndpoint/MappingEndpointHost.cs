using System.Net.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Abstractions;
using Microsoft.IdentityModel.Logging;
using Braid.Identity;

namespace Braid.MachineEndpoint;

/// <summary>
/// One explicitly assembled machine-facing host for one target. No browser session, static files, default
/// authentication or developer pipeline; only fixed status lines are written.
/// </summary>
internal static class MappingEndpointHost
{
    private static readonly IIdentityLogger Silent = new SilentLogger();

    internal static async Task<int> RunAsync(MappingEndpointSettings settings)
    {
        Privacy();
        MappingEndpointResources resources;
        try { resources = new(settings, new EntraMachineTokenValidator(new(settings.TenantId, settings.Audience)), TimeProvider.System); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            // The process exits; nothing was started.
            Console.Error.WriteLine("BraidMappingEndpointStartupFailed");
            return 1;
        }
        var ready = false;
        try
        {
            using var host = Build(resources);
            var routes = host.Services.GetRequiredService<MappingEndpointRoutes>();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStopping.Register(() => { routes.MarkStopping(); stopping.TrySetResult(); });
            await host.StartAsync();
            routes.MarkReady();
            ready = true;
            Console.Out.WriteLine("BraidMappingEndpointReady");
            await stopping.Task;
            await host.StopAsync();
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            Console.Error.WriteLine(ready ? "BraidMappingEndpointShutdownFailed" : "BraidMappingEndpointStartupFailed");
            await resources.DisposeAsync();
            return 1;
        }
        try { await resources.DisposeAsync(); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            // Unconfirmed runtime cleanup is a failed shutdown, not a clean exit.
            Console.Error.WriteLine("BraidMappingEndpointShutdownFailed");
            return 1;
        }
        return 0;
    }

    /// <summary>The host over already admitted resources; the caller owns and disposes them after the host stops.</summary>
    internal static IHost Build(MappingEndpointResources resources)
    {
        var settings = resources.Settings;
        var builder = Host.CreateDefaultBuilder(Array.Empty<string>())
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureAppConfiguration((_, configuration) => configuration.Sources.Clear())
            .ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.None))
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(options => options.ShutdownTimeout = MappingEndpointRoutes.Deadline + TimeSpan.FromSeconds(10));
                services.AddSingleton(resources);
                services.AddSingleton(resources.Calls);
                services.AddSingleton<MappingEndpointRoutes>();
                services.AddHostedService<ResultPurge>();
            })
            .ConfigureWebHostDefaults(web =>
            {
                web.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
                web.UseSetting(WebHostDefaults.CaptureStartupErrorsKey, "false");
                web.UseSetting(WebHostDefaults.DetailedErrorsKey, "false");
                web.ConfigureKestrel(server =>
                {
                    server.AddServerHeader = false;
                    server.Limits.MaxRequestBodySize = MappingEndpointRoutes.MaximumBodyBytes;
                    server.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
                    server.Limits.MaxRequestLineSize = 8 * 1024;
                    server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                    server.Limits.MaxConcurrentConnections = 256;
                    server.Listen(settings.ListenEndpoint, listen =>
                    {
                        listen.Protocols = HttpProtocols.Http1;
                        if (settings.Certificate is { } certificate)
                        {
                            var context = SslStreamCertificateContext.Create(certificate, settings.Chain, offline: true);
                            listen.UseHttps(new TlsHandshakeCallbackOptions
                            {
                                OnConnection = _ => ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificateContext = context })
                            });
                        }
                    });
                });
                web.Configure(Compose);
            });
        return builder.Build();
    }

    /// <summary>The request pipeline: the exact-route gate, then routing to the endpoint's routes.</summary>
    internal static void Compose(IApplicationBuilder app)
    {
        app.Use(app.ApplicationServices.GetRequiredService<MappingEndpointRoutes>().GateAsync);
        app.UseRouting();
        app.UseEndpoints(MappingEndpointRoutes.Map);
    }

    private static void Privacy()
    {
        IdentityModelEventSource.ShowPII = false;
        IdentityModelEventSource.LogCompleteSecurityArtifact = false;
        LogHelper.Logger = Silent;
    }

    private sealed class SilentLogger : IIdentityLogger
    {
        public bool IsEnabled(EventLogLevel eventLogLevel) => false;
        public void Log(LogEntry entry) { }
    }

    /// <summary>Removes expired retained responses for this target; the audit remains.</summary>
    private sealed class ResultPurge(MappingEndpointResources resources) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            do
            {
                try
                {
                    while (await resources.Ledger.PurgeExpiredAsync(resources.Settings.TargetReference, 100, stoppingToken) == 100) { }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
    }
}
