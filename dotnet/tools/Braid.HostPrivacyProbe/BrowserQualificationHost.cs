using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Abstractions;
using Microsoft.IdentityModel.Logging;
using Braid.Api;
using Braid.Api.Auth;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.BrowserQualification;
using Braid.Identity;
using Braid.Persistence.Postgres;
using Braid.Tests;

namespace Braid.HostPrivacyProbe;

// Not a published-Server startup path. Only existing real modules run in this owner.
internal sealed class BrowserQualificationHost
{
    private static readonly List<BrowserQualificationHost> Retained = [];
    private readonly QualificationHostInput input;
    private readonly TaskCompletionSource stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object requestSync = new();
    private readonly TaskCompletionSource requestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool requestIntakeClosed;
    private int requests;
    private IHost? host;
    private QualificationSocketServer? controls;
    private EntraProtocolCrypto? crypto;
    private EntraProtocolHttpsIssuer? issuer;
    private HttpClient? authorizer;
    private X509Certificate2? dataCertificate, tlsCertificate;
    private readonly X509Certificate2Collection tlsChain = [];
    private DurableSessionKeyProfile? profile;
    private PostgresServerSessionDatabase? readerPool, writerPool;
    private PostgresExternalIdentityDatabase? identityPool;
    private EntraBrowserAuthentication? authentication;
    private EntraBrowserClient? entra;
    private ServerSessionBrowser? browser;
    private Task? close, hostStop, authClose, entraClose, hostClose, controlClose, issuerClose;
    private BrowserQualificationAdmission admission = null!;
    private readonly List<Task> borrowedCloses = [];

    private BrowserQualificationHost(QualificationHostInput input) { this.input = input; }
    [StructLayout(LayoutKind.Sequential)] private struct Limit { internal nuint Current, Maximum; }
    [DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)] private static extern int SetLimit(int resource, ref Limit limit);
    internal static async Task<int> RunAsync()
    {
        BrowserQualificationHost? owner = null;
        bool failed = false;
        try
        {
            var noCore = new Limit { Current = 0, Maximum = 0 }; QualificationWire.Require(SetLimit(4, ref noCore) == 0);
            string root = Environment.GetEnvironmentVariable("BRAID_AUTH_BROWSER_ROOT") ?? throw new QualificationFailure();
            string path = Environment.GetEnvironmentVariable("BRAID_AUTH_BROWSER_HOST_INPUT") ?? throw new QualificationFailure();
            owner = new(PrivateQualificationFiles.Input<QualificationHostInput>(root, path));
            QualificationWire.Require(owner.input.Version == 1 && owner.input.Root == root);
            await owner.StartAsync(); Console.Out.WriteLine("BraidAuthBrowserReady");
            await owner.stopRequested.Task;
        }
        catch { failed = true; }
        if (owner is not null)
        {
            try { await owner.CloseAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            catch { failed = true; lock (Retained) Retained.Add(owner); }
        }
        if (failed) Console.Error.WriteLine("BraidAuthBrowserFailed");
        return failed ? 1 : 0;
    }

    private string Text(string path)
    {
        byte[] bytes = PrivateQualificationFiles.Read(input.Root, path);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static readonly IIdentityLogger Silent = new SilentLogger();
    private async Task StartAsync()
    {
        // This child has a narrow, independently owned privacy profile, not a claim
        // that the shipping SessionHostPrivacy/startup implementation was exercised.
        IdentityModelEventSource.ShowPII = false; IdentityModelEventSource.LogCompleteSecurityArtifact = false;
        LogHelper.Logger = Silent; IdentityModelEventSource.Logger.Dispose();
        var origin = new Uri(input.Origin);
        QualificationWire.Require(input.Organization != Guid.Empty && origin.Scheme == "https" && origin.Host == "127.0.0.1"
            && origin.Port is > 0 and <= 65535 && origin.GetLeftPart(UriPartial.Authority) == input.Origin);
        admission = new(input);
        ValidatePublication(input.WebRoot, input.Assets);
        dataCertificate = X509Certificate2.CreateFromPem(Text(input.DataCertificateFile), Text(input.DataKeyFile));
        string tlsPem = Text(input.TlsCertificateFile);
        tlsCertificate = X509Certificate2.CreateFromPem(tlsPem, Text(input.TlsKeyFile));
        QualificationWire.Require(tlsCertificate.MatchesHostname("127.0.0.1", allowWildcards: false, allowCommonName: false));
        tlsChain.ImportFromPem(tlsPem);
        var tls = SslStreamCertificateContext.Create(tlsCertificate, tlsChain, offline: true);
        profile = DurableSessionKeyProfile.Open(new(input.Organization, input.ApplicationName, input.KeyDirectory), dataCertificate, []);
        readerPool = new(input.ReaderConnection, ServerSessionDatabaseProfile.Reader);
        writerPool = new(input.WriterConnection, ServerSessionDatabaseProfile.Writer);
        identityPool = new(input.IdentityConnection);
        crypto = new(); issuer = new(crypto) { Callback = input.Origin + EntraProtocolFixturePolicy.Callback, TokenLifetimeSeconds = 1800 };
        await issuer.StartAsync();
        var transport = new EntraHttpsFixtureTransport(issuer.Port, crypto.Root);
        authorizer = Authorizer(issuer.Port, crypto.Root);
        var identities = new PostgresExternalIdentityReader(identityPool);
        var ownedProfile = profile ?? throw new QualificationFailure();
        var ownedReader = readerPool ?? throw new QualificationFailure();
        var ownedWriter = writerPool ?? throw new QualificationFailure();
        var ownedCrypto = crypto ?? throw new QualificationFailure();
        var builder = Host.CreateDefaultBuilder(Array.Empty<string>());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.Sources.Clear());
        builder.ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.None));
        builder.ConfigureWebHostDefaults(web =>
        {
            web.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            web.UseSetting(WebHostDefaults.CaptureStartupErrorsKey, "false");
            web.UseSetting(WebHostDefaults.DetailedErrorsKey, "false");
            web.UseContentRoot(input.WebRoot); web.UseWebRoot(input.WebRoot);
            web.ConfigureKestrel(server =>
            {
                server.AddServerHeader = false; server.Limits.MaxRequestBodySize = 65_536;
                server.Limits.MaxRequestLineSize = 24_576; server.Limits.MaxRequestHeadersTotalSize = 32_768;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                server.Listen(IPAddress.Loopback, origin.Port, listener =>
                {
                    listener.Protocols = HttpProtocols.Http1;
                    listener.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.TlsHandshakeCallbackOptions
                    { OnConnection = _ => new ValueTask<SslServerAuthenticationOptions>(new SslServerAuthenticationOptions { ServerCertificateContext = tls }) });
                });
            });
            web.ConfigureServices((context, services) =>
            {
                services.AddBraidApi();
                services.AddBraidBrowserSessions(new(input.Origin), ownedProfile,
                    new PostgresServerSessionReader(ownedReader), new PostgresServerSessionWriter(ownedWriter), TimeProvider.System);
                EntraClientRegistration.AddForHttpsFixture(services,
                    new(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client, input.Origin), ownedProfile, ownedCrypto.ClientCertificate, transport);
                services.AddSingleton<IExternalIdentityReader>(identities);
                services.AddBraidEntraBrowserAuthentication(new(input.Organization, input.Origin), ownedProfile, identities);
                services.AddBraidAuthenticationHttp(new(AuthenticationHttpMode.Session, input.Origin), context.HostingEnvironment);
                services.AddBraidSessionCatalog(new(input.Organization), new(false), new(false), new(false), new(false, false));
                services.AddSingleton(_ => new PostgresCatalogDatabase(input.CatalogConnection));
                services.AddSingleton<ICatalogReader>(services => new PostgresCatalogReader(services.GetRequiredService<PostgresCatalogDatabase>()));
                // Authentication's unused main-container warmup must not create a HOME ring.
                // This is qualification-owner registration, not a copied product class.
                var warmup = typeof(IKeyManager).Assembly.GetType("Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService", throwOnError: true)!;
                var entries = services.Where(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == warmup).ToArray();
                QualificationWire.Require(entries.Length == 1); services.Remove(entries[0]);
                var providers = services.Where(item => item.ServiceType == typeof(IDataProtectionProvider)).ToArray();
                QualificationWire.Require(providers.Length == 1 && !providers[0].IsKeyedService && providers[0].Lifetime == ServiceLifetime.Singleton);
                services.Remove(providers[0]);
                services.AddSingleton<IDataProtectionProvider>(new NoHostProtection());
            });
            web.Configure(app =>
            {
                app.UseBraidBrowserSecurityHeaders();
                app.Use(admission.NetworkLifetimeAsync);
                app.Use(admission.InvokeAsync);
                app.Use(GuardAsync);
                app.UseDefaultFiles(); app.UseStaticFiles(); app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapBraidHealth(); endpoints.MapBraidAuthentication(); endpoints.MapBraidCatalog();
                    endpoints.Map("/api/{**path}", () => Results.NotFound()); endpoints.MapFallbackToFile("index.html");
                });
            });
        });
        host = builder.Build();
        browser = host.Services.GetRequiredService<ServerSessionBrowser>();
        entra = host.Services.GetRequiredService<EntraBrowserClient>();
        authentication = host.Services.GetRequiredService<EntraBrowserAuthentication>();
        var defaults = host.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        QualificationWire.Require(defaults.DefaultScheme is null && defaults.DefaultAuthenticateScheme is null && defaults.DefaultChallengeScheme is null
            && defaults.DefaultForbidScheme is null && defaults.DefaultSignInScheme is null && defaults.DefaultSignOutScheme is null);
        var listeners = host.Services.GetServices<DiagnosticListener>().ToArray();
        var sources = host.Services.GetServices<ActivitySource>().ToArray();
        QualificationWire.Require(listeners.Length == 1 && sources.Length == 1 && listeners[0].Name == "Microsoft.AspNetCore"
            && sources[0].Name == "Microsoft.AspNetCore" && ReferenceEquals(listeners[0], host.Services.GetRequiredService<DiagnosticSource>())
            && !host.Services.GetServices<ILoggerProvider>().Any());
        listeners[0].Dispose(); sources[0].Dispose();
        QualificationWire.Require(!IdentityModelEventSource.ShowPII && !IdentityModelEventSource.LogCompleteSecurityArtifact && ReferenceEquals(LogHelper.Logger, Silent));
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await host.StartAsync(startup.Token);
        controls = new(input.Root, input.SocketPath, ControlAsync);
    }

    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        lock (requestSync)
        {
            if (requestIntakeClosed) { context.Response.StatusCode = 503; return; }
            requests++;
        }
        bool pipeline = false, framework = false, released = false;
        void Finish(bool completion)
        {
            lock (requestSync)
            {
                if (completion) framework = true; else pipeline = true;
                if (pipeline && framework && !released) { released = true; if (--requests == 0 && requestIntakeClosed) requestsDrained.TrySetResult(); }
            }
        }
        try { context.Response.OnCompleted(() => { Finish(true); return Task.CompletedTask; }); }
        catch { Finish(true); Finish(false); throw; }
        try
        {
            if (!context.Request.IsHttps || "https://" + context.Request.Host.Value != input.Origin || context.Request.PathBase.HasValue
                || context.Request.Headers.Keys.Any(name => name.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase) || name.StartsWith("X-Original", StringComparison.OrdinalIgnoreCase)))
            { context.Response.StatusCode = 403; return; }
            context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        }
        catch (OperationCanceledException) { throw; }
        catch { if (context.Response.HasStarted) context.Abort(); else { context.Response.Clear(); context.Response.StatusCode = 503; } }
        finally { Finish(false); }
    }

    private async Task<QualificationReply> ControlAsync(QualificationRequest request)
    {
        var currentIssuer = issuer ?? throw new QualificationFailure();
        var currentAuthorizer = authorizer ?? throw new QualificationFailure();
        if (request.Operation == "stop") { stopRequested.TrySetResult(); return QualificationReply.Ok(); }
        if (request.Operation == "register") return admission.RegisterIfOpen(request.Admission!);
        if (request.Operation == "withdraw") return admission.Withdraw(request.Admission!);
        if (request.Operation == "seal") { admission.Close(); return QualificationReply.Ok(); }
        if (request.Operation == "replay") { admission.ArmReplay(); return QualificationReply.Ok(); }
        if (request.Operation == "networkBegin") { admission.NetworkBegin(); return QualificationReply.Ok(); }
        if (request.Operation == "revoke") { admission.NetworkRevoke(); return QualificationReply.Ok(); }
        if (request.Operation == "barrier") { await admission.NetworkBarrierAsync(); return QualificationReply.Ok(); }
        if (request.Operation == "counts") return Counts();
        QualificationWire.Require(request.Operation == "authorize");
        var logical = EntraProtocolHttpsFixture.ValidateAuthorization(request.AuthorizationUrl!);
        currentIssuer.TokenCase = request.TokenCase == "badNonce" ? EntraProtocolTokenCase.BadNonce : EntraProtocolTokenCase.Good;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var reply = await currentAuthorizer.GetAsync(logical, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        await using var body = await reply.Content.ReadAsStreamAsync(budget.Token);
        byte[] tail = new byte[1]; QualificationWire.Require(await body.ReadAsync(tail, budget.Token) == 0);
        var callback = reply.Headers.Location;
        QualificationWire.Require(reply.StatusCode == HttpStatusCode.SeeOther && callback is not null
            && callback.GetLeftPart(UriPartial.Authority) == input.Origin && callback.AbsolutePath == EntraProtocolFixturePolicy.Callback
            && callback.Query.Length is > 0 and <= 16384 && callback.Fragment.Length == 0 && currentIssuer.AssertionFailure is null);
        string target = callback!.AbsoluteUri;
        if (request.CallbackCase == "badState")
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(callback.Query);
            QualificationWire.Require(query.Count == 2 && query["state"].Count == 1 && query["code"].Count == 1 && query["state"].ToString().Length > 0);
            string state = query["state"].ToString();
            target = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(input.Origin + EntraProtocolFixturePolicy.Callback,
                new Dictionary<string, string?> { ["state"] = (state[0] == 'a' ? "b" : "a") + state[1..], ["code"] = query["code"].ToString() });
        }
        admission.ArmCallback(target); return Counts() with { Status = 303, CallbackUrl = target };
    }
    private QualificationReply Counts() => admission.Counts(QualificationReply.Ok() with
    {
        AuthorizationRequests = issuer!.Snapshot().Count(send => send.Path == EntraProtocolFixturePolicy.Authorize),
        TokenRequests = issuer.Snapshot().Count(send => send.Path == EntraProtocolFixturePolicy.Token),
        AssertionsAccepted = issuer.AssertionAccepted,
        PkceAccepted = issuer.PkceAccepted,
        Unexpected = issuer.UnexpectedFailures + issuer.UnexpectedRequests
    });
    private static HttpClient Authorizer(int port, X509Certificate2 root)
    {
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        chain.CustomTrustStore.Add(root);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            ActivityHeadersPropagator = null,
            SslOptions = new() { CertificateChainPolicy = chain },
            ConnectCallback = async (context, token) =>
            {
                QualificationWire.Require(context.DnsEndPoint.Host == "login.microsoftonline.com" && context.DnsEndPoint.Port == 443);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        return new(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
    private Task CloseAsync() => close ??= CloseCoreAsync();
    private async Task CloseCoreAsync()
    {
        admission?.Close();
        lock (requestSync) { requestIntakeClosed = true; if (requests == 0) requestsDrained.TrySetResult(); }
        controlClose ??= controls?.CloseAsync() ?? Task.CompletedTask;
        hostStop ??= host?.StopAsync(CancellationToken.None) ?? Task.CompletedTask;
        authClose ??= authentication?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        entraClose ??= entra?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        await Task.WhenAll(controlClose, hostStop, authClose, entraClose, requestsDrained.Task);
        if (admission is not null) await admission.NetworkBarrierAsync();
        browser?.Dispose();
        hostClose ??= host is IAsyncDisposable asynchronous ? asynchronous.DisposeAsync().AsTask() : DisposeHost(); await hostClose;
        authorizer?.Dispose(); issuerClose ??= issuer?.DisposeAsync().AsTask() ?? Task.CompletedTask; await issuerClose;
        if (issuer is not null) await Task.WhenAll(issuer.SnapshotOperations());
        foreach (var resource in new IAsyncDisposable?[] { readerPool, writerPool, identityPool })
        { if (resource is null) continue; Task original = resource.DisposeAsync().AsTask(); borrowedCloses.Add(original); await original; }
        profile?.Dispose(); dataCertificate?.Dispose(); tlsCertificate?.Dispose();
        foreach (var certificate in tlsChain) certificate.Dispose(); crypto?.Dispose();
        admission?.WriteClosedObservation();
    }
    private Task DisposeHost() { host?.Dispose(); return Task.CompletedTask; }
    private static void ValidatePublication(string root, IReadOnlyDictionary<string, string> expected)
    {
        QualificationWire.Require(Path.IsPathFullyQualified(root) && Path.GetFullPath(root) == root && expected.Count is > 0 and <= 256 && expected.ContainsKey("index.html"));
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories); QualificationWire.Require(files.Length == expected.Count);
        foreach (string path in files)
        {
            var file = new FileInfo(path); QualificationWire.Require(file.LinkTarget is null && file.Length <= 16_777_216);
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            QualificationWire.Require(expected.TryGetValue(relative, out string? hash) && hash == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
        QualificationWire.Require(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Prepend(root).All(path => new DirectoryInfo(path).LinkTarget is null));
    }
    private sealed class SilentLogger : IIdentityLogger
    { public bool IsEnabled(EventLogLevel level) => false; public void Log(LogEntry entry) { } }
    private sealed class NoHostProtection : IDataProtectionProvider
    { public IDataProtector CreateProtector(string purpose) => throw new QualificationFailure(); }
}
