using Weft.Api;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Api.Chat;
using Weft.Api.Specs;

namespace Weft.Server;

/// <summary>One explicitly assembled session host; no minimal-host automatic authentication pipeline.</summary>
internal static class SessionHostEntrypoint
{
    private static readonly List<SessionHostResources> RetainedPartialResources = [];

    internal static async Task<int> RunAsync(ServerHostOptions options, Func<string, string?> read)
    {
        var resources = new SessionHostResources();
        var requests = new SessionRequestGate();
        SessionHostLifetime? ownership = null;
        Exception? primary = null;
        var ready = false;
        try
        {
            SessionHostPrivacy.BeforeBuilder(read);
            // Generic Host's explicit Configure pipeline has no WebApplication automatic
            // UseAuthentication, UseAuthorization, or Development exception page.
            var builder = Host.CreateDefaultBuilder(Array.Empty<string>())
                .UseContentRoot(AppContext.BaseDirectory)
                .ConfigureWebHostDefaults(web =>
                {
                    web.UseContentRoot(AppContext.BaseDirectory);
                    web.UseWebRoot(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
                    web.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
                    web.UseSetting(WebHostDefaults.CaptureStartupErrorsKey, "false");
                    web.UseSetting(WebHostDefaults.DetailedErrorsKey, "false");
                    web.ConfigureKestrel(server => SessionHostTransport.Configure(server, options, resources));
                    web.ConfigureServices((context, services) =>
                    {
                        SessionHostTransport.ValidateConfiguration(context.Configuration, read);
                        services.AddWeftApi();
                        services.SuppressSessionDiagnostics();
                        resources.PrepareAndRegister(services, context.HostingEnvironment, options, read);
                    });
                    web.Configure(app =>
                    {
                        requests.Use(app);
                        SessionHostTransport.Use(app, options);
                        app.Use(async (context, next) =>
                        {
                            try { await next(context); }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception error) when (SessionHostResources.Nonfatal(error))
                            {
                                if (context.Response.HasStarted) { context.Abort(); return; }
                                context.Response.Clear();
                                SessionHostTransport.Reject(context, 503);
                                context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
                            }
                        });
                        BrowserContentPolicy.Use(app, options.Origin);
                        app.UseDefaultFiles();
                        app.UseStaticFiles();
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapWeftHealth();
                            endpoints.MapWeftAuthentication();
                            endpoints.MapWeftCatalog();
                            endpoints.MapWeftSpecs();
                            endpoints.MapWeftRegressionCases();
                            endpoints.MapWeftRegressionRuns();
                            endpoints.MapWeftAutomation();
                            endpoints.MapWeftBuilds();
                            endpoints.MapWeftReleaseOperations();
                            endpoints.MapWeftChat();
                            endpoints.MapWeftHelp();
                            endpoints.Map("/api/{**path}", () => Results.NotFound());
                            endpoints.MapFallbackToFile("index.html");
                        });
                    });
                });
            var host = builder.Build();
            ownership = new(host, requests, resources);
            ownership.ResolveBeforeStart();
            SessionHostPrivacy.BeforeStart(host.Services);
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stopRegistration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
            await host.StartAsync();
            ready = true;
            Console.Out.WriteLine("WeftSessionHostReady");
            // WaitForShutdownAsync/RunAsync would issue and dispose their own shutdown work.
            // This owner must retain the first exact Stop/Dispose tasks instead.
            await stopping.Task;
        }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { primary = error; }
        finally
        {
            if (ownership is not null)
            {
                try { await ownership.CloseAsync(); }
                catch (Exception error) when (SessionHostResources.Nonfatal(error)) { primary ??= error; }
            }
            else
            {
                // Build never completed: no request/provider method could have entered.
                var disposal = resources.DisposeAfterSettlementAsync();
                _ = disposal.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                try { await disposal.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (Exception error) when (SessionHostResources.Nonfatal(error))
                {
                    lock (RetainedPartialResources) RetainedPartialResources.Add(resources);
                    primary ??= error;
                }
            }
        }
        if (primary is null) return 0;
        Console.Error.WriteLine(ready ? "WeftSessionHostShutdownFailed" : "WeftSessionHostStartupFailed");
        return 1;
    }
}
