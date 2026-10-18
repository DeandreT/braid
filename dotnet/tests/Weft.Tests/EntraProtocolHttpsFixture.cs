using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weft.Application;
using Weft.Identity;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
internal sealed partial class EntraProtocolHttpsFixture : IAsyncDisposable
{
    internal EntraProtocolCrypto Crypto { get; private set; } = null!;
    internal EntraProtocolHttpsIssuer Issuer { get; private set; } = null!;
    internal SessionTicketQualificationRoot Files { get; private set; } = null!;
    internal DurableSessionKeyProfile Profile { get; private set; } = null!;
    internal DurableSessionKeyProfile? SecondProfile { get; private set; }
    internal EntraHttpsFixtureTransport Transport { get; private set; } = null!;
    internal EntraBrowserClient Facade => app!.Services.GetRequiredService<EntraBrowserClient>();
    internal IServiceProvider Services => app!.Services;
    internal EntraClientOptions Options => optionsOverride ?? new(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client, Origin.GetLeftPart(UriPartial.Authority));
    internal Uri Origin { get; private set; } = null!;
    internal EntraProtocolForbiddenPorts Ports { get; } = new();
    internal EntraProtocolMemoryLogs Logs { get; } = new();
    internal bool PoisonAmbient = false;
    private readonly ConcurrentDictionary<int, EntraProtocolRequest> observations = new();
    private WebApplication? app;
    private HttpClient? browserClient, issuerClient;
    private EntraClientOptions? optionsOverride;
    private ServiceProvider? foreignProvider;
    private EntraBrowserClient? foreignFacade;
    private Task? hostDisposal, applicationStopOperation, applicationDisposeOperation, foreignDisposal, disposal;
    private int sequence, appPort;
    private bool hostSettled, wrongIssuerName, wrongTrustRoot;

    internal static async Task RunAsync(Func<EntraProtocolHttpsFixture, Task> body, bool wrongIssuerName = false, bool wrongTrustRoot = false)
    {
        var fixture = new EntraProtocolHttpsFixture { wrongIssuerName = wrongIssuerName, wrongTrustRoot = wrongTrustRoot }; Exception? primary = null; Exception? cleanup = null;
        try { await fixture.InitializeAsync(); await body(fixture); }
        catch (Exception error) { primary = Safe(error, "The owned Entra protocol case failed."); }
        finally { try { await fixture.DisposeAsync(); } catch (Exception error) { cleanup = Safe(error, "Owned Entra protocol dependencies remain unsettled."); } }
        if (primary is not null && cleanup is not null) throw new AggregateException("Owned Entra protocol and cleanup failed.", primary, cleanup);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanup is not null) ExceptionDispatchInfo.Capture(cleanup).Throw();
    }
    private static Exception Safe(Exception error, string category)
    {
        if (error is EntraProtocolFixtureFailure) return error;
        if (error is AggregateException aggregate) return new AggregateException(category, aggregate.InnerExceptions.Select(value => Safe(value, category)));
        return new EntraProtocolFixtureFailure(category);
    }

    private async Task InitializeAsync()
    {
        Crypto = new(wrongIssuerName, wrongTrustRoot); Files = new();
        using (DurableSessionKeyProfile.BootstrapEmpty(Files.Options(EntraProtocolFixturePolicy.Organization), Files.Certificate, [],
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2))) { }
        Profile = DurableSessionKeyProfile.Open(Files.Options(EntraProtocolFixturePolicy.Organization), Files.Certificate, []);
        Files.AssertEncryptedRing();
        Issuer = new(Crypto); await Issuer.StartAsync();
        Transport = new EntraHttpsFixtureTransport(Issuer.Port, Crypto.UntrustedRoot ?? Crypto.Root);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(1);
        try
        {
            appPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            Origin = new Uri("https://127.0.0.1:" + appPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Issuer.Callback = new Uri(Origin, EntraProtocolFixturePolicy.Callback).AbsoluteUri;
            reservation.Stop(); await StartApplicationAsync(Profile);
        }
        finally { reservation.Stop(); }
        browserClient = Client(Crypto.Root, null); browserClient.BaseAddress = Origin;
        issuerClient = Client(Crypto.Root, Issuer.Port); issuerClient.BaseAddress = new Uri("https://login.microsoftonline.com");
    }

    private static HttpClient Client(X509Certificate2 root, int? fixedPort)
    {
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        chain.CustomTrustStore.Add(root);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = chain }
        };
        if (fixedPort is { } port)
            handler.ConnectCallback = async (context, token) =>
            {
                EntraProtocolFixturePolicy.Check(context.DnsEndPoint.Host == "login.microsoftonline.com" && context.DnsEndPoint.Port == 443,
                    "The owned browser issuer client attempted an unadmitted logical authority.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, port, token); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    private async Task StartApplicationAsync(DurableSessionKeyProfile profile)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
        builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders(); builder.Logging.AddProvider(Logs);
        builder.Services.AddSingleton<IExternalIdentityReader>(Ports); builder.Services.AddSingleton<IServerSessionWriter>(Ports);
        int priorAttempts = Transport.SnapshotAttempts().Count;
        EntraClientRegistration.AddForHttpsFixture(builder.Services, Options, profile, Crypto.ClientCertificate, Transport);
        EntraProtocolFixturePolicy.Check(Transport.SnapshotAttempts().Count == priorAttempts, "Registration sent provider HTTP before an explicit operation.");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 65_536; options.Limits.MaxRequestHeadersTotalSize = 32_768; options.Limits.MaxRequestLineSize = 24_576; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Listen(IPAddress.Loopback, appPort, listen => { listen.Protocols = HttpProtocols.Http1; listen.UseHttps(Crypto.ApplicationTls); });
        });
        app = builder.Build(); hostSettled = false; hostDisposal = null; applicationStopOperation = null; applicationDisposeOperation = null;
        app.Use(async (context, next) =>
        {
            if (!int.TryParse(context.Request.Headers[EntraProtocolFixturePolicy.TraceHeader], out int id) || !observations.TryGetValue(id, out var record))
            { context.Response.StatusCode = 400; return; }
            record.Entered = true; record.Context = context;
            if (PoisonAmbient) context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", "ambient-untrusted"), new Claim("oid", "ambient-untrusted"),
                new Claim(ClaimTypes.Role, "administrator"), new Claim("roles", "run.payload.read") ], "fixture-untrusted"));
            var before = context.User;
            try { await next(context); }
            catch (OperationCanceledException) { record.Canceled = true; }
            catch (Exception) { record.UnexpectedFailure = true; }
            finally { record.UserChanged = !ReferenceEquals(before, context.User); record.Context = null; record.Ended.TrySetResult(); }
        });
        app.UseAuthentication();
        app.MapPost("/api/auth/login", InvokeAsync);
        app.MapGet(EntraProtocolFixturePolicy.Callback, InvokeAsync);
        using var start = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await app.StartAsync(start.Token);
    }

    private async Task InvokeAsync(HttpContext context)
    {
        var record = observations[int.Parse(context.Request.Headers[EntraProtocolFixturePolicy.TraceHeader]!, System.Globalization.CultureInfo.InvariantCulture)];
        if (record.Operation == EntraProtocolOperation.Ambient) { context.Response.StatusCode = 204; return; }
        AuthenticationOptions? defaultOptions = null; string? priorDefault = null;
        try
        {
            if (record.TamperDefault)
            {
                defaultOptions = context.RequestServices.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
                priorDefault = defaultOptions.DefaultScheme; defaultOptions.DefaultScheme = "Weft.Entra";
            }
            if (record.Operation == EntraProtocolOperation.StartedHeaders) await context.Response.StartAsync(context.RequestAborted);
            CancellationToken token = record.OriginalCancellation?.Token ?? context.RequestAborted;
            if (record.Operation is EntraProtocolOperation.Begin or EntraProtocolOperation.BeginTwice)
            {
                Task<EntraLoginChallenge> task = Facade.BeginLoginAsync(context, token); record.OwnedWork = task; record.OwnedStarted.TrySetResult();
                record.Challenge = await task;
                if (record.Operation == EntraProtocolOperation.BeginTwice)
                {
                    try { _ = await Facade.BeginLoginAsync(context, CancellationToken.None); record.UnexpectedFailure = true; }
                    catch (ServerSessionIdentityException error) { record.SecondFailure = error.Kind; }
                }
            }
            else
            {
                Task<EntraVerifiedLogin> task = Facade.VerifyCallbackAsync(context, token); record.OwnedWork = task; record.OwnedStarted.TrySetResult();
                if (record.Operation is EntraProtocolOperation.Overlap or EntraProtocolOperation.ForeignOwner)
                {
                    try
                    {
                        EntraBrowserClient other = record.Operation == EntraProtocolOperation.ForeignOwner ? foreignFacade! : Facade;
                        _ = await other.VerifyCallbackAsync(context, CancellationToken.None);
                        record.UnexpectedFailure = true;
                    }
                    catch (ServerSessionIdentityException error) { record.SecondFailure = error.Kind; }
                    finally { record.OverlapCompleted.TrySetResult(); }
                }
                record.Verified = await task; Interlocked.Increment(ref record.EvidencePublished);
                if (record.Operation == EntraProtocolOperation.Reuse)
                {
                    try { _ = await Facade.VerifyCallbackAsync(context, CancellationToken.None); record.UnexpectedFailure = true; }
                    catch (ServerSessionIdentityException error) { record.SecondFailure = error.Kind; }
                }
            }
            if (!context.Response.HasStarted) context.Response.StatusCode = 200;
        }
        catch (ServerSessionIdentityException error)
        { record.Failure = error.Kind; record.ClosedFailure = error; if (!context.Response.HasStarted) context.Response.StatusCode = 400; }
        catch (OperationCanceledException error) { record.Canceled = true; record.CancellationWasOriginal = error.CancellationToken == (record.OriginalCancellation?.Token ?? context.RequestAborted); if (!context.Response.HasStarted) context.Response.StatusCode = 499; }
        catch (Exception) { record.UnexpectedFailure = true; if (!context.Response.HasStarted) context.Response.StatusCode = 500; }
        finally
        {
            if (defaultOptions is not null) defaultOptions.DefaultScheme = priorDefault;
            var handler = await context.RequestServices.GetRequiredService<IAuthenticationHandlerProvider>().GetHandlerAsync(context, "Weft.Entra");
            if (handler is OpenIdConnectHandler remote) record.Policy = new EntraProtocolHandlerPolicy(remote.Options);
            else record.UnexpectedFailure = true;
        }
        if (!context.RequestAborted.IsCancellationRequested) await context.Response.WriteAsync("{}", context.RequestAborted);
    }

    internal async Task ReplaceApplicationAsync(bool independentProfile, Guid? profileOrganization = null, EntraClientOptions? fixedOptions = null)
    {
        await StopApplicationAsync(); optionsOverride = fixedOptions;
        browserClient?.Dispose(); browserClient = Client(Crypto.Root, null); browserClient.BaseAddress = Origin;
        Transport = new EntraHttpsFixtureTransport(Issuer.Port, Crypto.UntrustedRoot ?? Crypto.Root);
        if (independentProfile)
        {
            SecondProfile = DurableSessionKeyProfile.Open(Files.Options(profileOrganization ?? EntraProtocolFixturePolicy.Organization), Files.Certificate, []);
            await StartApplicationAsync(SecondProfile);
        }
        else await StartApplicationAsync(Profile);
    }
    internal void CreateForeignFacade()
    {
        var services = new ServiceCollection(); services.AddLogging();
        SecondProfile ??= DurableSessionKeyProfile.Open(Files.Options(EntraProtocolFixturePolicy.Organization), Files.Certificate, []);
        EntraClientRegistration.AddForHttpsFixture(services, Options, SecondProfile, Crypto.ClientCertificate,
            new EntraHttpsFixtureTransport(Issuer.Port, Crypto.Root));
        foreignProvider = services.BuildServiceProvider(); foreignFacade = foreignProvider.GetRequiredService<EntraBrowserClient>();
    }
    private Task StopApplicationAsync() => hostDisposal ??= StopApplicationCoreAsync();
    private async Task StopApplicationCoreAsync()
    {
        if (app is null) { hostSettled = true; return; }
        Exception? stopFailure = null;
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            applicationStopOperation ??= app.StopAsync(stop.Token); await applicationStopOperation.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception) { stopFailure = new EntraProtocolFixtureFailure("Owned protocol application stop failed."); }
        applicationDisposeOperation ??= app.DisposeAsync().AsTask(); await applicationDisposeOperation.WaitAsync(TimeSpan.FromSeconds(15));
        EntraProtocolFixturePolicy.Check(applicationStopOperation is { IsCompleted: true } && applicationDisposeOperation.IsCompleted
            && observations.Values.All(record => !record.Entered || record.Ended.Task.IsCompleted)
            && observations.Values.All(record => record.OwnedWork is null || record.OwnedWork.IsCompleted)
            && observations.Values.All(record => record.ConfigurationObservation is null || record.ConfigurationObservation.IsCompleted),
            "Owned protocol application/framework work remained unsettled.");
        hostSettled = true;
        if (stopFailure is not null) throw stopFailure;
    }
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        var errors = new List<Exception>();
        foreach (var record in observations.Values) record.OriginalCancellation?.Cancel();
        browserClient?.Dispose(); issuerClient?.Dispose();
        try { await StopApplicationAsync(); } catch (Exception) { errors.Add(new EntraProtocolFixtureFailure("Owned application original disposal did not settle.")); }
        bool foreignSettled = foreignProvider is null;
        if (foreignProvider is not null)
        {
            try { foreignDisposal ??= foreignProvider.DisposeAsync().AsTask(); await foreignDisposal.WaitAsync(TimeSpan.FromSeconds(15)); foreignSettled = true; }
            catch (Exception) { errors.Add(new EntraProtocolFixtureFailure("Owned foreign runtime original disposal did not settle.")); }
        }
        if (Issuer is not null)
        { try { await Issuer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new EntraProtocolFixtureFailure("Owned issuer original disposal did not settle.")); } }
        if (!hostSettled || !foreignSettled || Issuer is not null && !Issuer.Settled)
        {
            errors.Add(new EntraProtocolFixtureFailure("Borrowed certificates, profiles and private roots retained while owned work settlement is unproven."));
            throw new AggregateException("Owned protocol cleanup remains unproven.", errors);
        }
        try { SecondProfile?.Dispose(); Profile?.Dispose(); } catch (Exception) { errors.Add(new EntraProtocolFixtureFailure("Owned profiles did not settle.")); }
        if (errors.Count == 0)
        {
            foreach (var record in observations.Values) record.OriginalCancellation?.Dispose();
            try { Files?.Dispose(); Crypto?.Dispose(); } catch (Exception) { errors.Add(new EntraProtocolFixtureFailure("Owned key/certificate roots did not settle.")); }
        }
        if (errors.Count != 0) throw new AggregateException("Owned protocol cleanup failed.", errors);
    }
    public override string ToString() => nameof(EntraProtocolHttpsFixture);
}

internal sealed class EntraProtocolForbiddenPorts : IExternalIdentityReader, IServerSessionWriter
{
    internal int Calls;
    private Task<T> Refuse<T>() { Interlocked.Increment(ref Calls); return Task.FromException<T>(new EntraProtocolFixtureFailure("The dormant protocol adapter touched a binding/session port.")); }
    public Task<ExternalIdentityResolution> ResolveAsync(ExternalIdentityKey key, CancellationToken cancellationToken = default) => Refuse<ExternalIdentityResolution>();
    public Task<ExternalIdentityResolution> ResolveBindingAsync(Guid bindingId, CancellationToken cancellationToken = default) => Refuse<ExternalIdentityResolution>();
    public Task<ServerSessionWriteResult> CreateOnlyAsync(ServerSessionCreateIntent intent, CancellationToken cancellationToken = default) => Refuse<ServerSessionWriteResult>();
    public Task<ServerSessionWriteResult> RenewAsync(ServerSessionKey key, long version, CancellationToken cancellationToken = default) => Refuse<ServerSessionWriteResult>();
    public Task<ServerSessionWriteResult> RevokeAsync(ServerSessionKey key, CancellationToken cancellationToken = default) => Refuse<ServerSessionWriteResult>();
    public override string ToString() => nameof(EntraProtocolForbiddenPorts);
}

internal sealed class EntraProtocolMemoryLogs : ILoggerProvider
{
    private const int MaximumEvents = 256, MaximumDiagnostics = 2048, MaximumText = 16_384, MaximumCharacters = 1_048_576;
    private readonly object sync = new();
    private readonly List<string> messages = [];
    private int eventCount, characterCount;
    internal bool InspectionIncomplete { get; private set; }
    internal IReadOnlyList<string> Snapshot() { lock (sync) return messages.ToArray(); }
    public ILogger CreateLogger(string categoryName) => categoryName.StartsWith("Microsoft.AspNetCore.Authentication", StringComparison.Ordinal)
        || categoryName.StartsWith("Weft.Identity", StringComparison.Ordinal) ? new Recorder(this) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public void Dispose() { }
    public override string ToString() => nameof(EntraProtocolMemoryLogs);
    private void Capture(string? text)
    {
        if (text is null) return;
        if (messages.Count >= MaximumDiagnostics || text.Length > MaximumText || characterCount + text.Length > MaximumCharacters)
        { InspectionIncomplete = true; return; }
        characterCount += text.Length; messages.Add(text);
    }
    private void Record<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (sync)
        {
            if (eventCount >= MaximumEvents) { InspectionIncomplete = true; return; }
            eventCount++;
            try
            {
                Capture(formatter(state, exception));
                // Do not call Exception.ToString(): it may recursively expand an unbounded
                // graph, or hide inner diagnostics behind a redacted override.
                var pending = new Stack<Exception>();
                var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
                var diagnostics = new List<Exception>(); bool graphIncomplete = false;
                if (exception is not null) pending.Push(exception);
                while (pending.TryPop(out var diagnostic))
                {
                    if (!seen.Add(diagnostic)) continue;
                    if (seen.Count > 32) { graphIncomplete = true; break; }
                    diagnostics.Add(diagnostic);
                    if (diagnostic is AggregateException aggregate)
                    {
                        if (aggregate.InnerExceptions.Count > 32) graphIncomplete = true;
                        foreach (var inner in aggregate.InnerExceptions.Take(32)) pending.Push(inner);
                    }
                    else if (diagnostic.InnerException is { } inner) pending.Push(inner);
                }
                // AggregateException.Message itself recursively expands inner messages.
                // Establish the graph bound before reading any diagnostic messages.
                if (graphIncomplete) InspectionIncomplete = true;
                else foreach (var diagnostic in diagnostics) { Capture(diagnostic.Message); Capture(diagnostic.StackTrace); }
            }
            catch (Exception) { InspectionIncomplete = true; }
        }
    }
    private sealed class Recorder(EntraProtocolMemoryLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { owner.Record(state, exception, formatter); }
    }
}
