using System.Net;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weft.Application;
using Weft.Identity;
using Weft.Persistence.Postgres;
using Weft.Server;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ServerHostLifetimeTests(PostgresFixture postgres)
{
    private static readonly List<object?[]> RetainedGateDependencies = [];

    [Fact]
    public async Task RealLifetimeRetainsFirstHostStopAndAsyncDisposeTasksAcrossConcurrentClose()
    {
        var host = new HostLifetimeWitness(); var resources = new SessionHostResources();
        var lifetime = new SessionHostLifetime(host, new SessionRequestGate(), resources);
        Task first = lifetime.CloseAsync();
        try
        {
            await host.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task second = lifetime.CloseAsync();
            ServerHostCheck.That(ReferenceEquals(first, second) && host.StopCalls == 1 && host.AsyncDisposeCalls == 0
                && !first.IsCompleted, "Concurrent close did not retain the actual first stop task or disposed dependencies early.");
            host.StopRelease.TrySetResult(); await host.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ServerHostCheck.That(host.StopCalls == 1 && host.AsyncDisposeCalls == 1 && host.SyncDisposeCalls == 0
                && !first.IsCompleted, "Concurrent close substituted a second host disposal or discarded its first task.");
            host.DisposeRelease.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            ServerHostCheck.That(host.StopCalls == 1 && host.AsyncDisposeCalls == 1, "Host close duplicated an already-owned task.");
        }
        finally
        {
            host.StopRelease.TrySetResult(); host.DisposeRelease.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealLifetimeStillAttemptsOnlyFirstHostDisposalAfterOriginalStopFailure(bool failDisposal)
    {
        var host = new HostLifetimeWitness { FailStop = true, FailDispose = failDisposal }; var resources = new SessionHostResources();
        var lifetime = new SessionHostLifetime(host, new SessionRequestGate(), resources);
        Task close = lifetime.CloseAsync();
        try
        {
            await host.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); host.StopRelease.TrySetResult();
            await host.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ServerHostCheck.That(host.StopCalls == 1 && host.AsyncDisposeCalls == 1 && !close.IsCompleted,
                "A stop failure prevented the independent original disposal attempt.");
            host.DisposeRelease.TrySetResult();
            try { await close.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
            ServerHostCheck.That(close.IsFaulted && ReferenceEquals(close, lifetime.CloseAsync())
                && host.StopCalls == 1 && host.AsyncDisposeCalls == 1 && host.SyncDisposeCalls == 0,
                "Host close discarded its primary stop failure or substituted another shutdown task.");
        }
        finally
        {
            host.StopRelease.TrySetResult(); host.DisposeRelease.TrySetResult();
            try { await close.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task SettledStopFaultCannotDisposeTheHostBeforeBothActualRequestWitnessesFinish()
    {
        var host = new HostLifetimeWitness { FailStop = true }; var gate = new SessionRequestGate();
        var lifetime = new SessionHostLifetime(host, gate, new SessionHostResources());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new HostLifetimeResponseCompletion(); var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(completion);
        var app = new ApplicationBuilder(host.Services); gate.Use(app);
        app.Run(async _ => { entered.TrySetResult(); await release.Task; });
        Task request = app.Build()(context); Task? close = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            close = lifetime.CloseAsync(); host.StopRelease.TrySetResult();
            Task originalStop = host.StopTask!;
            try { await originalStop.WaitAsync(TimeSpan.FromSeconds(5)); } catch (ServerHostCaseFailure) { }
            ServerHostCheck.That(originalStop.IsFaulted && host.AsyncDisposeCalls == 0 && !close.IsCompleted,
                "A settled stop failure bypassed the active request's actual settlement.");
            release.TrySetResult(); await request.WaitAsync(TimeSpan.FromSeconds(5));
            ServerHostCheck.That(!gate.CloseAndDrain().IsCompleted && host.AsyncDisposeCalls == 0 && !close.IsCompleted,
                "Middleware completion alone allowed provider disposal after a stop failure.");
            await completion.FinishAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await host.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ServerHostCheck.That(host.AsyncDisposeCalls == 1 && !close.IsCompleted,
                "Actual request settlement did not admit the single held provider disposal.");
            host.DisposeRelease.TrySetResult();
            try { await close.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
            ServerHostCheck.That(close.IsFaulted && ReferenceEquals(close, lifetime.CloseAsync()),
                "A completed request erased the original stop failure or replaced its close task.");
        }
        finally
        {
            release.TrySetResult(); host.StopRelease.TrySetResult(); host.DisposeRelease.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(5));
            await completion.FinishAsync().WaitAsync(TimeSpan.FromSeconds(5));
            close ??= lifetime.CloseAsync();
            try { await close.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task SettledStopFailureStillClosesBorrowedResourcesAfterTheFirstSuccessfulProviderDisposal()
    {
        var files = new ServerHostFiles(); var resources = new SessionHostResources();
        var host = new HostLifetimeWitness { FailStop = true };
        var lifetime = new SessionHostLifetime(host, new SessionRequestGate(), resources);
        Task? close = null; bool resourcesSettled = false;
        try
        {
            var organization = Guid.NewGuid();
            using (DurableSessionKeyProfile.BootstrapEmpty(files.Options(organization), files.DataCertificate, [],
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2))) { }
            var environment = new Dictionary<string, string?>
            {
                ["WEFT_CATALOG_ENABLED"] = "true",
                ["WEFT_CATALOG_AUTH_MODE"] = "session",
                ["WEFT_PUBLIC_ORIGIN"] = "https://127.0.0.1:58413",
                ["WEFT_LISTEN_ADDR"] = "127.0.0.1:58413",
                ["WEFT_HOST_TRANSPORT"] = "directHttps",
                ["WEFT_SESSION_ORGANIZATION_ID"] = organization.ToString("D"),
                ["WEFT_SESSION_APPLICATION_NAME"] = files.ApplicationName,
                ["WEFT_SESSION_KEY_DIRECTORY"] = files.Keys,
                ["WEFT_ENTRA_TENANT_ID"] = ServerHostSessionCase.Tenant.ToString("D"),
                ["WEFT_ENTRA_CLIENT_ID"] = ServerHostSessionCase.Client.ToString("D"),
                ["WEFT_SESSION_KEY_CERTIFICATE_FILE"] = files.DataPem,
                ["WEFT_SESSION_KEY_PRIVATE_KEY_FILE"] = files.DataKey,
                ["WEFT_ENTRA_CLIENT_CERTIFICATE_FILE"] = files.ClientPem,
                ["WEFT_ENTRA_CLIENT_PRIVATE_KEY_FILE"] = files.ClientKey,
                ["WEFT_TLS_CERTIFICATE_FILE"] = files.TlsPem,
                ["WEFT_TLS_PRIVATE_KEY_FILE"] = files.TlsKey,
                ["WEFT_CATALOG_CONNECTION_STRING"] = "Host=127.0.0.1;Database=owned;Username=owned;Password=owned;SSL Mode=Disable"
            };
            foreach (string role in new[] { "SESSION_READER", "SESSION_WRITER", "IDENTITY_READER" })
                environment["WEFT_" + role + "_CONNECTION_STRING_FILE"] = files.Text(role + ".txt",
                    "Host=127.0.0.1;Database=owned;Username=owned;Password=owned;SSL Mode=Disable");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            resources.PrepareAndRegister(new ServiceCollection(), builder.Environment,
                ServerHostOptions.Read(environment.GetValueOrDefault), environment.GetValueOrDefault);
            var certificate = resources.TlsCertificate ?? throw new ServerHostCaseFailure("The owned TLS resource was not acquired.");
            ServerHostCheck.That(certificate.Handle != IntPtr.Zero,
                "The resource-settlement witness did not acquire its own borrowed certificate.");
            close = lifetime.CloseAsync(); host.StopRelease.TrySetResult();
            await host.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ServerHostCheck.That(certificate.Handle != IntPtr.Zero && !close.IsCompleted,
                "Borrowed resources closed before the first actual provider disposal settled.");
            host.DisposeRelease.TrySetResult();
            try { await close.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
            resourcesSettled = certificate.Handle == IntPtr.Zero;
            ServerHostCheck.That(close.IsFaulted && resourcesSettled && host.AsyncDisposeCalls == 1,
                "A settled stop fault skipped safe resource cleanup or lost its failure.");
        }
        finally
        {
            host.StopRelease.TrySetResult(); host.DisposeRelease.TrySetResult();
            close ??= lifetime.CloseAsync();
            try { await close.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
            // The owner's first resource task disposes this last-acquired certificate after
            // the lazy pools and profile. Never call a disposer again to manufacture settlement.
            resourcesSettled = host.DisposeTask?.IsCompletedSuccessfully == true
                && resources.TlsCertificate?.Handle == IntPtr.Zero;
            if (resourcesSettled) files.Dispose();
            else throw new ServerHostCaseFailure("The owned lifetime resources remain retained for unproven settlement.");
        }
    }

    [PostgresFact]
    public Task GateRequiresActual112OnCompletedCleanupAfterMiddlewareHasAlreadyCompleted() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            var gate = new SessionRequestGate();
            var readerPool = new PostgresServerSessionDatabase(test.Data.Case.SessionReaderConnectionString, ServerSessionDatabaseProfile.Reader);
            var writerPool = new PostgresServerSessionDatabase(test.Data.Case.SessionWriterConnectionString, ServerSessionDatabaseProfile.Writer);
            var profile = DurableSessionKeyProfile.Open(test.Files.Options(test.Data.Project.OrganizationId), test.Files.DataCertificate, []);
            var reader = new HeldHostSessionReader(new PostgresServerSessionReader(readerPool));
            WebApplication? app = null; Task? stop = null, dispose = null, drain = null;
            Task? readerDisposal = null, writerDisposal = null;
            HttpClient? client = null; Task<ServerHostReply>? request = null;
            Task<ServerSessionRequestAdmission>? observation = null;
            Task<CurrentServerSession?>? readerWork = null;
            var caller = new CancellationTokenSource();
            var pipelineEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pipelineEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callerCanceledBeforeReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completedAfter112 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool settled = false; test.RequireExternalSettlement(() => settled);
            Exception? primary = null; var cleanup = new List<Exception>();
            static Task Begin(Func<Task> action)
            {
                try { return action(); }
                catch (Exception error) { return Task.FromException(error); }
            }
            async Task JoinAsync(Task original, string category, bool allowCancellation = false)
            {
                try { await original.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) when (allowCancellation && original.IsCanceled) { }
                catch (Exception) { cleanup.Add(new ServerHostCaseFailure(category)); }
            }
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
                builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
                builder.Services.AddWeftBrowserSessions(new(test.Origin.GetLeftPart(UriPartial.Authority)), profile,
                    reader, new PostgresServerSessionWriter(writerPool), TimeProvider.System);
                SessionHostDataProtectionPolicy.Apply(builder.Services);
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, test.Origin.Port,
                    listener => { listener.Protocols = HttpProtocols.Http1; listener.UseHttps(test.Files.TlsCertificate); }));
                app = builder.Build();
                app.Use(async (context, next) =>
                {
                    pipelineEntered.TrySetResult();
                    // First registration runs last: after the normal gate and downstream112 cleanup.
                    context.Response.OnCompleted(() =>
                    {
                        // Inspect the original port task here, not on a later test continuation.
                        completedAfter112.TrySetResult(reader.OriginalTask is { IsCompleted: true });
                        return Task.CompletedTask;
                    });
                    try { await next(context); } finally { pipelineEnded.TrySetResult(); }
                });
                gate.Use(app);
                app.MapGet("/__owned_observe", async (HttpContext context) =>
                {
                    Task<ServerSessionRequestAdmission> original = context.RequestServices.GetRequiredService<ServerSessionBrowser>()
                        .ObserveAsync(context, caller.Token);
                    observation = original;
                    // On failure, keep the framework request scope valid until this public caller
                    // settles too. On the positive path it is already canceled before return;
                    // this guard cannot stand in for #112 cleanup's held original-reader join.
                    context.Response.OnCompleted(async () => { try { await original; } catch (Exception) { } });
                    await Task.WhenAll(reader.Entered.Task, reader.Original.Task).WaitAsync(TimeSpan.FromSeconds(5));
                    caller.Cancel();
                    OperationCanceledException? canceled = null;
                    try { await original.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException error) { canceled = error; }
                    ServerHostCheck.That(original.IsCanceled && canceled?.CancellationToken == caller.Token
                        && !context.RequestAborted.IsCancellationRequested && reader.OriginalTask is { IsCompleted: false },
                        "The original public caller did not preserve its distinct cancellation before the held112 owner settled.");
                    callerCanceledBeforeReturn.TrySetResult();
                    context.Response.StatusCode = 204;
                });
                using var starting = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await app.StartAsync(starting.Token);
                client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
                request = ServerHostHttp.SendAsync(client, "/__owned_observe", headers: new Dictionary<string, string> { ["Cookie"] = test.Cookies.Header });
                var reply = await request;
                await pipelineEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                readerWork = await reader.Original.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ServerHostCheck.That(reply.Status == 204 && reader.Entered.Task.IsCompletedSuccessfully
                    && callerCanceledBeforeReturn.Task.IsCompletedSuccessfully && observation is { IsCanceled: true }
                    && !readerWork.IsCompleted && !reader.Release.Task.IsCompleted,
                    "Middleware did not complete with its public caller canceled and actual112 original reader still held.");
                drain ??= gate.CloseAndDrain();
                ServerHostCheck.That(!drain.IsCompleted && !completedAfter112.Task.IsCompleted && !readerWork.IsCompleted,
                    "The real gate treated middleware completion as actual112 cleanup settlement.");
                reader.Release.TrySetResult();
                try { await readerWork.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (readerWork.IsCanceled) { }
                await completedAfter112.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await drain.WaitAsync(TimeSpan.FromSeconds(5));
                ServerHostCheck.That(completedAfter112.Task.Result && readerWork.IsCompleted && observation is { IsCanceled: true },
                    "Response completion ran before actual112 original reader work settled.");
            }
            catch (Exception error) { primary = ServerHostCheck.Safe(error, "The actual112 request-settlement case failed."); }
            finally
            {
                try { caller.Cancel(); } catch (Exception) { cleanup.Add(new ServerHostCaseFailure("Owned gate caller cancellation failed.")); }
                reader.Release.TrySetResult();
                if (app is not null)
                {
                    drain ??= gate.CloseAndDrain();
                    using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    stop ??= Begin(() => app.StopAsync(stopping.Token));
                    await JoinAsync(stop, "Owned gate host stop failed.");
                    if (request is not null) await JoinAsync(request, "Owned gate HTTP request did not settle.");
                    if (observation is not null) await JoinAsync(observation, "Owned gate public observation did not settle.", allowCancellation: true);
                    readerWork ??= reader.OriginalTask;
                    if (readerWork is not null) await JoinAsync(readerWork, "Owned gate original reader did not settle.", allowCancellation: true);
                    await JoinAsync(drain, "Owned gate request drain failed.");
                    if (pipelineEntered.Task.IsCompleted)
                    {
                        await JoinAsync(pipelineEnded.Task, "Owned gate middleware did not settle.");
                        await JoinAsync(completedAfter112.Task, "Owned gate final response callback did not settle.");
                    }
                    bool requestsSettled = stop.IsCompletedSuccessfully && drain.IsCompletedSuccessfully
                        && (request is null || request.IsCompleted) && (observation is null || observation.IsCompleted)
                        && (readerWork is null || readerWork.IsCompleted)
                        && (!pipelineEntered.Task.IsCompleted || pipelineEnded.Task.IsCompletedSuccessfully
                            && completedAfter112.Task.IsCompletedSuccessfully && (readerWork is null || completedAfter112.Task.Result));
                    if (requestsSettled)
                    {
                        // Never attempt provider disposal until the original request/stop tasks
                        // certify settlement, even when the body already has a primary failure.
                        dispose ??= Begin(() => app.DisposeAsync().AsTask());
                        await JoinAsync(dispose, "Owned gate host disposal failed.");
                        settled = dispose.IsCompletedSuccessfully;
                    }
                }
                else settled = true;
                if (settled)
                {
                    settled = false;
                    readerDisposal ??= Begin(() => readerPool.DisposeAsync().AsTask());
                    await JoinAsync(readerDisposal, "Owned gate reader pool failed cleanup.");
                    writerDisposal ??= Begin(() => writerPool.DisposeAsync().AsTask());
                    await JoinAsync(writerDisposal, "Owned gate writer pool failed cleanup.");
                    if (readerDisposal.IsCompletedSuccessfully && writerDisposal.IsCompletedSuccessfully)
                    {
                        try { client?.Dispose(); caller.Dispose(); profile.Dispose(); settled = true; }
                        catch (Exception) { cleanup.Add(new ServerHostCaseFailure("Owned gate private dependencies failed cleanup.")); }
                    }
                }
                if (!settled)
                {
                    // Root the parent fixture too: its files, certificates and database case
                    // must remain borrowed while any original task lacks settlement proof.
                    lock (RetainedGateDependencies) RetainedGateDependencies.Add([test, app, client, caller, readerPool,
                        writerPool, profile, reader, request, observation, readerWork, stop, dispose, drain, readerDisposal, writerDisposal]);
                    cleanup.Add(new ServerHostCaseFailure("Owned gate dependencies retained for unproven request settlement."));
                }
            }
            ServerHostCheck.Throw(primary, cleanup);
        });
}

internal sealed class HeldHostSessionReader(IServerSessionReader actual) : IServerSessionReader
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<Task<CurrentServerSession?>> Original { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<CurrentServerSession?>? OriginalTask => Original.Task.IsCompletedSuccessfully ? Original.Task.Result : null;
    public Task<CurrentServerSession?> ObserveAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
    {
        Task<CurrentServerSession?> original = ObserveCoreAsync(key, cancellationToken);
        Original.TrySetResult(original);
        return original;
    }
    private async Task<CurrentServerSession?> ObserveCoreAsync(ServerSessionKey key, CancellationToken cancellationToken)
    {
        Entered.TrySetResult();
        // Deliberately noncooperative fixture work, released by finally; cancellation alone is not settlement.
        await Release.Task;
        return await actual.ObserveAsync(key, cancellationToken);
    }
}

internal sealed class HostLifetimeWitness : IHost, IAsyncDisposable
{
    public IServiceProvider Services { get; } = new ServiceCollection().AddLogging().BuildServiceProvider();
    internal TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource StopRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int StopCalls, AsyncDisposeCalls, SyncDisposeCalls;
    internal bool FailStop, FailDispose;
    internal Task? StopTask, DisposeTask;
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref StopCalls); StopEntered.TrySetResult(); return StopTask = CompleteStopAsync();
    }
    private async Task CompleteStopAsync()
    {
        await StopRelease.Task;
        if (FailStop) throw new ServerHostCaseFailure("Owned fixed stop failure.");
    }
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref AsyncDisposeCalls); DisposeEntered.TrySetResult(); return new(DisposeTask = CompleteDisposeAsync());
    }
    private async Task CompleteDisposeAsync()
    {
        await DisposeRelease.Task; ((IDisposable)Services).Dispose();
        if (FailDispose) throw new ServerHostCaseFailure("Owned fixed disposal failure.");
    }
    public void Dispose() { Interlocked.Increment(ref SyncDisposeCalls); ((IDisposable)Services).Dispose(); }
}

internal sealed class HostLifetimeResponseCompletion : IHttpResponseFeature
{
    private readonly Stack<(Func<object, Task> Callback, object State)> callbacks = new();
    private Task? completion;
    public int StatusCode { get; set; } = 200;
    public string? ReasonPhrase { get; set; }
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
    public Stream Body { get; set; } = Stream.Null;
    public bool HasStarted => false;
    public void OnStarting(Func<object, Task> callback, object state) { }
    public void OnCompleted(Func<object, Task> callback, object state) => callbacks.Push((callback, state));
    internal Task FinishAsync() => completion ??= FinishCoreAsync();
    private async Task FinishCoreAsync()
    {
        while (callbacks.TryPop(out var item)) await item.Callback(item.State);
    }
}
