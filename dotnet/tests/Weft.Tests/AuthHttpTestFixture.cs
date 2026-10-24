using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Identity;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Independent real auth routes, owned HTTPS issuer and restricted PG. No118 implementation read.</summary>
[SupportedOSPlatform("linux")]
internal sealed partial class AuthHttpFixture : IAsyncDisposable
{
    internal const string StatusPath = "/api/auth/session", LoginPath = "/api/auth/login", CallbackPath = "/api/auth/callback/entra";
    internal const string LogoutPath = "/api/auth/logout", ForgetPath = "/api/auth/forget-local", ContextPath = "/api/v1/catalog/context";
    internal const string SessionCookie = "__Host-Weft.Session", EpochCookie = "__Host-Weft.Antiforgery", ActionCookie = "__Host-Weft.AuthAction";
    internal const string FixtureHeader = "X-Weft-118-Request";
    internal PostgresMappingMetadataTestSupport Data { get; private set; } = null!;
    internal ExternalIdentityBindingProvisioner Owner { get; private set; } = null!;
    internal ExternalIdentityBinding Binding { get; private set; } = null!;
    internal EntraProtocolCrypto Crypto { get; private set; } = null!;
    internal EntraProtocolHttpsIssuer Issuer { get; private set; } = null!;
    internal SessionTicketQualificationRoot Files { get; private set; } = null!;
    internal AuthHttpHost First { get; private set; } = null!;
    internal AuthHttpHost? Second { get; private set; }
    internal Uri Origin { get; private set; } = null!;
    internal AuthHttpRecord Current => current.Value ?? throw new AuthHttpFailure("An auth fixture port lost its request owner.");
    private readonly AsyncLocal<AuthHttpRecord?> current = new();
    private readonly ConcurrentDictionary<int, AuthHttpRecord> records = new();
    private readonly ConcurrentBag<AuthHttpOwnedTask> work = [];
    private readonly List<AuthHttpHost> hosts = [];
    private readonly List<DurableSessionKeyProfile> additionalProfiles = [];
    private bool foreignRuntimeUnsettled;
    internal void OwnProfile(DurableSessionKeyProfile profile) => additionalProfiles.Add(profile);
    internal void RetainForeignRuntime() => foreignRuntimeUnsettled = true;
    private HttpClient? issuerBrowser;
    private Task? disposal, issuerRelease, ownerRelease, dataRelease;
    private int sequence;

    internal static async Task RunAsync(PostgresFixture postgres, Func<AuthHttpFixture, Task> body, bool bindIssuer = true)
    {
        var test = new AuthHttpFixture(); Exception? primary = null; var cleanup = new List<Exception>();
        try { await test.InitializeAsync(postgres, bindIssuer); await body(test); }
        catch (Exception error) { primary = Safe(error, "The independent auth HTTP case failed."); }
        finally { try { await test.DisposeAsync(); } catch (Exception error) { cleanup.Add(Safe(error, "The independent auth HTTP cleanup failed.")); } }
        Throw(primary, cleanup);
    }
    internal static void Check(bool condition, string category) { if (!condition) throw new AuthHttpFailure(category); }
    internal static Exception Safe(Exception error, string category)
    {
        if (error is AuthHttpFailure) return error;
        if (error is AggregateException aggregate) return new AggregateException(category, aggregate.InnerExceptions.Select(item => Safe(item, category)));
        // Closed failure categories aid investigation without rendering an
        // exception message, stack, request operand or provider response.
        return new AuthHttpFailure(category + " (" + FailureKind(error) + ").");
    }
    internal static string FailureKind(Exception error) => error switch
    {
        AuthHttpFailure => "assertion",
        AggregateException => "aggregate",
        OperationCanceledException => "canceled",
        TimeoutException => "timeout",
        HttpRequestException http => "http-transport/" + http.HttpRequestError,
        IOException => "io",
        ObjectDisposedException => "disposed",
        InvalidOperationException => "invalid-operation",
        ArgumentException => "invalid-argument",
        NullReferenceException => "null-reference",
        System.Text.Json.JsonException => "json",
        _ => "unclassified"
    };
    internal static string TaskState(Task? original) => original?.Status.ToString() ?? "absent";
    private static void Throw(Exception? primary, IReadOnlyCollection<Exception> cleanup)
    {
        if (primary is not null && cleanup.Count != 0) throw new AggregateException("Auth HTTP and original cleanup failed.", new[] { primary }.Concat(cleanup));
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanup.Count != 0) throw new AggregateException("Auth HTTP original cleanup failed.", cleanup);
    }
    private async Task InitializeAsync(PostgresFixture postgres, bool bindIssuer)
    {
        Data = await PostgresMappingMetadataTestSupport.CreateAsync(postgres); Owner = new(Data.Case.MigrationConnectionString);
        if (bindIssuer)
        {
            var binding = await Owner.BindAsync(EntraProtocolFixturePolicy.UserKey, Data.Actor.PrincipalId);
            Check(binding.Outcome == ExternalIdentityBindingOwnerOutcome.Created && binding.Binding is not null, "The actual OIDC fixture binding was not prepared."); Binding = binding.Binding!;
        }
        Crypto = new(); Files = new();
        using (DurableSessionKeyProfile.BootstrapEmpty(Files.Options(Data.Seed.OrganizationA), Files.Certificate, [], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2))) { }
        Files.AssertEncryptedRing(); Issuer = new(Crypto); await Issuer.StartAsync();
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(1);
        try { Origin = new("https://127.0.0.1:" + ((IPEndPoint)reservation.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        finally { reservation.Stop(); }
        Issuer.Callback = new Uri(Origin, CallbackPath).AbsoluteUri;
        issuerBrowser = NewClient(Crypto.Root, "login.microsoftonline.com", 443, Issuer.Port);
        First = await StartHostAsync();
    }
    internal async Task<AuthHttpHost> StartHostAsync()
    {
        var host = new AuthHttpHost(this); hosts.Add(host); await host.InitializeAsync(); return host;
    }
    internal async Task AddSecondAsync()
    {
        Second = await StartHostAsync();
        Check(!ReferenceEquals(First.Profile, Second.Profile) && !ReferenceEquals(First.Services, Second.Services)
            && !ReferenceEquals(First.ReaderPool, Second.ReaderPool), "The two auth instances reused a borrowed profile/provider/pool.");
    }
    internal AuthHttpRecord NewRecord()
    {
        int id = Interlocked.Increment(ref sequence); Check(id <= 256, "The auth fixture exceeded its request budget.");
        var record = new AuthHttpRecord { Id = id }; Check(records.TryAdd(id, record), "An auth fixture request was reused."); return record;
    }
    internal Task<T> Track<T>(Task<T> original, AuthHttpRecord record, bool failure = false, AuthHttpTaskRole role = AuthHttpTaskRole.Unspecified)
    { work.Add(new(original, cancellation: true, failure, role)); return original; }
    internal Task Track(Task original, bool cancellation = false, bool failure = false, AuthHttpTaskRole role = AuthHttpTaskRole.Unspecified)
    { work.Add(new(original, cancellation, failure, role)); return original; }
    internal static HttpClient NewClient(X509Certificate2 root, string logicalHost, int logicalPort, int actualPort)
    {
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck }; chain.CustomTrustStore.Add(root);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ActivityHeadersPropagator = null,
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = chain }
        };
        handler.ConnectCallback = async (context, token) =>
        {
            Check(context.DnsEndPoint.Host == logicalHost && context.DnsEndPoint.Port == logicalPort, "A fixture browser attempted an unadmitted logical origin.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(IPAddress.Loopback, actualPort, token); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
    }
    internal async Task MiddlewareAsync(AuthHttpHost host, HttpContext context, RequestDelegate next)
    {
        if (!int.TryParse(context.Request.Headers[FixtureHeader], out var id) || !records.TryGetValue(id, out var record)) { context.Response.StatusCode = 400; return; }
        record.Entered = true; record.Context = context; current.Value = record;
        var originalBody = context.Request.Body; context.Request.Body = new AuthHttpBody(originalBody, record);
        context.Features.Set<IFormFeature>(new AuthHttpForm(record));
        if (record.MissingBodyDetector) context.Features.Set<IHttpRequestBodyDetectionFeature>(null);
        if (record.PositiveBodyDetector) context.Features.Set<IHttpRequestBodyDetectionFeature>(new AuthHttpBodyDetector());
        if (record.PoisonAmbient) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Data.Seed.OtherProjectAdmin.ToString("D")), new Claim("roles", "administrator")], "untrusted-fixture"));
        _ = context.Request.Cookies;
        var cookieFeature = context.Features.Get<IRequestCookiesFeature>();
        Check(cookieFeature is not null, "The actual request has no cookie feature.");
        context.Features.Set<IRequestCookiesFeature>(new AuthHttpCookieFeature(cookieFeature!, context, record, host.Profile));
        if (record.CaptureIdentity)
            context.Response.OnStarting(async () => { if (context.Response.StatusCode == 200) { var admitted = await host.Browser.ObserveAsync(context, context.RequestAborted); record.Identity = admitted.Identity; } });
        if (record.PriorAdmissionCancellation is not null)
            context.Response.OnCompleted(() => { record.RequestCompleted.TrySetResult(); return Task.CompletedTask; });
        try
        {
            if (record.PriorObserve)
            {
                try { record.PriorObservation = await host.Browser.ObserveAsync(context, context.RequestAborted); }
                catch (ServerSessionIdentityException error) { record.PriorFailure = error.Kind; }
            }
            if (record.PriorAdmissionCancellation is { } independent)
            {
                var original = host.Browser.AdmitRequestAsync(context, independent.Token);
                record.PriorAdmissionOperation = original; _ = Track(original, record, role: AuthHttpTaskRole.PublicAdmission);
            }
            if (record.PriorDeniedRevoke)
            {
                try { await host.Browser.RevokeLocalAsync(context, context.RequestAborted); }
                catch (ServerSessionIdentityException error) { record.PriorFailure = error.Kind; }
            }
            if (record.Direct != AuthHttpDirect.None)
            {
                var token = record.CallerCancellation?.Token ?? context.RequestAborted;
                if (record.Direct == AuthHttpDirect.Read)
                {
                    var original = host.Auth.ReadSessionAsync(context, token); record.ModuleOperation = original;
                    _ = Track(original, cancellation: true, failure: record.ExpectedDirectFailure, role: AuthHttpTaskRole.Module); record.ModuleStarted.TrySetResult(); record.SessionResult = await original;
                }
                else if (record.Direct == AuthHttpDirect.Begin)
                { var original = host.Auth.BeginLoginAsync(context, token); record.ModuleOperation = original; _ = Track(original, cancellation: true, failure: record.ExpectedDirectFailure, role: AuthHttpTaskRole.Module); record.ModuleStarted.TrySetResult(); record.ChallengeResult = await original; }
                else if (record.Direct == AuthHttpDirect.Logout)
                { var original = host.Auth.LogoutAsync(context, token); record.ModuleOperation = original; _ = Track(original, cancellation: true, failure: record.ExpectedDirectFailure, role: AuthHttpTaskRole.Module); record.ModuleStarted.TrySetResult(); await original; }
                else if (record.Direct == AuthHttpDirect.Forget)
                { var original = host.Auth.ForgetLocalAsync(context, token); record.ModuleOperation = original; _ = Track(original, cancellation: true, failure: record.ExpectedDirectFailure, role: AuthHttpTaskRole.Module); record.ModuleStarted.TrySetResult(); _ = await original; }
                else if (record.Direct == AuthHttpDirect.Complete)
                { var original = host.Auth.CompleteLoginAsync(context, token); record.ModuleOperation = original; _ = Track(original, cancellation: true, failure: record.StartHeadersAfterCreate || record.ExpectedDirectFailure, role: AuthHttpTaskRole.Module); record.ModuleStarted.TrySetResult(); await original; }
                else if (record.Direct == AuthHttpDirect.BorrowedObserve)
                { var original = host.Browser.ObserveAsync(context, token); record.ModuleOperation = original; _ = Track(original, cancellation: true); record.ModuleStarted.TrySetResult(); record.Identity = (await original).Identity; }
                else if (record.Direct == AuthHttpDirect.BorrowedIdentity)
                { var original = host.Identities.ResolveAsync(EntraProtocolFixturePolicy.UserKey, token); record.ModuleOperation = original; _ = Track(original, cancellation: true); record.ModuleStarted.TrySetResult(); record.BorrowedIdentityResolved = (await original).Outcome == ExternalIdentityResolutionOutcome.Resolved; }
                else
                { var original = host.Entra.BeginLoginAsync(context, token); record.ModuleOperation = original; _ = Track(original, cancellation: true); record.ModuleStarted.TrySetResult(); record.ChallengeResult = await original; }
                context.Response.StatusCode = 204;
            }
            else await next(context);
        }
        catch (ServerSessionIdentityException error) when (record.Direct != AuthHttpDirect.None) { record.ClosedError = error; record.Failure = error.Kind; if (!context.Response.HasStarted) context.Response.StatusCode = 503; }
        catch (BrowserAuthenticationException error) when (record.Direct != AuthHttpDirect.None) { record.ClosedError = error; record.AuthenticationFailure = error.Kind; if (!context.Response.HasStarted) context.Response.StatusCode = 503; }
        catch (OperationCanceledException) { record.Canceled = true; if (!context.Response.HasStarted) context.Response.StatusCode = 499; }
        catch (Exception) { record.Unexpected = true; if (!context.Response.HasStarted) context.Response.StatusCode = 500; }
        finally
        {
            try
            {
                if (record.PriorAdmissionOperation is { } original) await original;
            }
            finally
            {
                record.AdmissionAtRequestEnd = record.PriorAdmissionOperation?.Status;
                context.Request.Body = originalBody; current.Value = null; record.Context = null; record.Ended.TrySetResult();
            }
        }
    }
    internal Task<long> RowsAsync() => Data.Case.ScalarAsync<long>("SELECT count(*) FROM ONLY weft.server_sessions");
    internal Task<bool> RevokedAsync(ServerSessionKey key) => Data.Case.ScalarAsync<bool>("SELECT count(*)=1 AND bool_and(revoked_at IS NOT NULL) FROM ONLY weft.server_sessions WHERE organization_id=$1 AND handle_digest=$2", key.OrganizationId, Convert.FromHexString(key.HandleDigest.Value));
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        var errors = new List<Exception>();
        foreach (var record in records.Values) { record.ReleaseAll(); record.CallerCancellation?.Cancel(); record.PriorAdmissionCancellation?.Cancel(); }
        Issuer?.MetadataGate?.Release.TrySetResult(); Issuer?.TokenGate?.Release.TrySetResult();
        foreach (var host in hosts.AsEnumerable().Reverse())
            try { await host.DisposeAsync(); } catch (Exception error) { errors.Add(Safe(error, "An original auth host disposal did not settle.")); }
        foreach (var original in work)
        {
            try { await original.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) when (original.AllowsCancellation && original.Task.IsCanceled) { }
            catch (Exception) when (original.AllowsFailure && original.Task.IsFaulted) { }
            catch (Exception error)
            {
                errors.Add(new AggregateException("Original fixture operation closed diagnostics.",
                    Safe(error, "An original fixture operation did not settle in its expected category."),
                    new AuthHttpFailure("Retained role=" + original.Role + "; status=" + TaskState(original.Task) + "; kind=" + FailureKind(error) + ".")));
            }
        }
        if (Issuer is not null)
            try { issuerRelease ??= Issuer.DisposeAsync().AsTask(); await issuerRelease.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The owned original issuer disposal did not settle.")); }
        if (Issuer is not null)
            foreach (var original in Issuer.SnapshotOperations())
            {
                try { await original.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (OperationCanceledException) when (original.IsCanceled) { }
                catch (Exception) { errors.Add(new AuthHttpFailure("An original issuer process task did not settle in its expected category.")); }
            }
        if (foreignRuntimeUnsettled || hosts.Any(host => !host.Settled) || work.Any(item => !item.Task.IsCompleted) || records.Values.Any(record => record.Entered && !record.Ended.Task.IsCompleted) || Issuer is not null && (!Issuer.Settled || Issuer.SnapshotOperations().Any(task => !task.IsCompleted)))
        { errors.Add(new AuthHttpFailure("Borrowed pools/profile/certificates/roots retained because original work remains unsettled.")); Throw(null, errors); return; }
        try { issuerBrowser?.Dispose(); } catch (Exception) { errors.Add(new AuthHttpFailure("The owned issuer-browser client cleanup failed.")); }
        bool dependencies = true;
        foreach (var host in hosts)
            try { await host.ReleaseBorrowedAsync(); } catch (Exception) { dependencies = false; errors.Add(new AuthHttpFailure("An owned borrowed-resource release failed.")); }
        foreach (var profile in additionalProfiles) try { profile.Dispose(); } catch (Exception) { dependencies = false; errors.Add(new AuthHttpFailure("An owned extra profile did not settle.")); }
        if (Owner is not null) try { ownerRelease ??= Owner.DisposeAsync().AsTask(); await ownerRelease.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { dependencies = false; errors.Add(new AuthHttpFailure("The fixture identity owner did not settle.")); }
        if (!dependencies) { Throw(null, errors); return; }
        try { Files?.Dispose(); } catch (Exception) { errors.Add(new AuthHttpFailure("The owned qualification key root did not settle.")); }
        try { Crypto?.Dispose(); } catch (Exception) { errors.Add(new AuthHttpFailure("The owned protocol certificates did not settle.")); }
        if (Data is not null) try { dataRelease ??= Data.DisposeAsync().AsTask(); await dataRelease.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The restricted PostgreSQL case did not settle.")); }
        foreach (var record in records.Values) { record.CallerCancellation?.Dispose(); record.PriorAdmissionCancellation?.Dispose(); }
        Throw(null, errors);
    }
    public override string ToString() => nameof(AuthHttpFixture);
}

[SupportedOSPlatform("linux")]
internal sealed class AuthHttpHost(AuthHttpFixture test) : IAsyncDisposable
{
    internal WebApplication? Application;
    internal IServiceProvider Services => Application!.Services;
    internal ServerSessionBrowser Browser => Services.GetRequiredService<ServerSessionBrowser>();
    internal EntraBrowserClient Entra => Services.GetRequiredService<EntraBrowserClient>();
    internal EntraBrowserAuthentication Auth => Services.GetRequiredService<EntraBrowserAuthentication>();
    internal DurableSessionKeyProfile Profile = null!;
    internal PostgresServerSessionDatabase ReaderPool = null!, WriterPool = null!;
    internal PostgresExternalIdentityDatabase IdentityPool = null!;
    internal PostgresProjectCreationDatabase CreationPool = null!;
    internal EntraHttpsFixtureTransport Transport = null!;
    internal AuthHttpIdentityReader Identities = null!;
    internal HttpClient Client = null!;
    internal bool Settled;
    internal int PhysicalPort;
    private Task? moduleDisposal, stop, hostDisposal, disposal, readerRelease, writerRelease, identityRelease, creationRelease;
    internal Task BeginModuleDisposal() => moduleDisposal ??= Auth.DisposeAsync().AsTask();
    internal async Task InitializeAsync()
    {
        Profile = DurableSessionKeyProfile.Open(test.Files.Options(test.Data.Seed.OrganizationA), test.Files.Certificate, []);
        ReaderPool = new(test.Data.Case.SessionReaderConnectionString, ServerSessionDatabaseProfile.Reader);
        WriterPool = new(test.Data.Case.SessionWriterConnectionString, ServerSessionDatabaseProfile.Writer);
        IdentityPool = new(test.Data.Case.IdentityConnectionString); CreationPool = new(test.Data.Case.ProjectCreationConnectionString);
        var reader = new AuthHttpSessionReader(new PostgresServerSessionReader(ReaderPool), test);
        var writer = new AuthHttpSessionWriter(new PostgresServerSessionWriter(WriterPool), test);
        var identities = new AuthHttpIdentityReader(new PostgresExternalIdentityReader(IdentityPool), test); Identities = identities;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production, ApplicationName = typeof(AuthHttpFixture).Assembly.FullName });
        builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.Services.AddWeftApi();
        builder.Services.AddWeftBrowserSessions(new(test.Origin.GetLeftPart(UriPartial.Authority)), Profile, reader, writer, new AuthHttpDueClock());
        Transport = new(test.Issuer.Port, test.Crypto.Root);
        EntraClientRegistration.AddForHttpsFixture(builder.Services, new(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client, test.Origin.GetLeftPart(UriPartial.Authority)), Profile, test.Crypto.ClientCertificate, Transport);
        builder.Services.AddSingleton<IExternalIdentityReader>(identities);
        builder.Services.AddWeftEntraBrowserAuthentication(new(test.Data.Seed.OrganizationA, test.Origin.GetLeftPart(UriPartial.Authority)), Profile, identities);
        builder.Services.AddWeftSessionCatalog(new(test.Data.Seed.OrganizationA), new CatalogMetadataEditOptions(true), new CatalogRevisionIngestionOptions(false), new CatalogProjectCreationOptions(false), new CatalogRevisionReportOptions(false, false));
        builder.Services.AddSingleton<ICatalogReader>(new AuthHttpCatalogReader(test.Data.Reader, test));
        builder.Services.AddSingleton<IMappingMetadataWriter>(new AuthHttpMetadataWriter(test.Data.Writer, test));
        builder.Services.AddSingleton<IOrganizationProjectCreationReader>(new PostgresProjectCreator(CreationPool));
        builder.Services.AddWeftAuthenticationHttp(new(AuthenticationHttpMode.Session, test.Origin.GetLeftPart(UriPartial.Authority)), builder.Environment);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(1); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; PhysicalPort = port;
        try
        {
            builder.WebHost.ConfigureKestrel(options => { options.Limits.MaxRequestLineSize = 24576; options.Limits.MaxRequestHeadersTotalSize = 32768; options.Limits.MaxRequestBodySize = 65536; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5); options.Listen(IPAddress.Loopback, port, listen => { listen.Protocols = HttpProtocols.Http1; listen.UseHttps(test.Crypto.ApplicationTls); }); });
            Application = builder.Build();
            Application.Use((HttpContext context, RequestDelegate next) => test.MiddlewareAsync(this, context, next)); Application.UseAuthentication();
            Application.MapWeftAuthentication(); Application.MapWeftCatalog();
            Client = AuthHttpFixture.NewClient(test.Crypto.Root, "127.0.0.1", test.Origin.Port, port); Client.BaseAddress = test.Origin;
            reservation.Stop(); using var start = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await Application.StartAsync(start.Token);
            AuthHttpFixture.Check(Transport.SnapshotAttempts().Count == 0, "Dormant registration sent provider HTTP.");
        }
        finally { reservation.Stop(); }
    }
    internal async Task CloseIdentityPoolAsync()
    { identityRelease ??= IdentityPool.DisposeAsync().AsTask(); await identityRelease.WaitAsync(TimeSpan.FromSeconds(15)); AuthHttpFixture.Check(identityRelease.IsCompletedSuccessfully, "The original identity-pool close did not settle."); }
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        if (Application is null) { Settled = true; return; }
        var errors = new List<Exception>();
        try { await BeginModuleDisposal().WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The original auth module disposal failed.")); }
        try { stop ??= Application.StopAsync(); await stop.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The original auth host stop failed.")); }
        try { hostDisposal ??= Application.DisposeAsync().AsTask(); await hostDisposal.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The original auth host disposal failed.")); }
        Settled = moduleDisposal is { IsCompletedSuccessfully: true } && stop is { IsCompletedSuccessfully: true } && hostDisposal is { IsCompletedSuccessfully: true };
        if (errors.Count != 0) throw new AggregateException("Auth host original cleanup failed.", errors);
    }
    internal async Task ReleaseBorrowedAsync()
    {
        AuthHttpFixture.Check(Settled, "Borrowed auth dependencies were released before original module/host drain.");
        var errors = new List<Exception>();
        try { Client?.Dispose(); } catch (Exception) { errors.Add(new AuthHttpFailure("The owned app client did not settle.")); }
        try { Identities?.Dispose(); } catch (Exception) { errors.Add(new AuthHttpFailure("The borrowed identity reader did not settle after drain.")); }
        try { Profile?.Dispose(); } catch (Exception) { errors.Add(new AuthHttpFailure("The borrowed app profile did not settle after drain.")); }
        if (ReaderPool is not null) try { readerRelease ??= ReaderPool.DisposeAsync().AsTask(); await readerRelease.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The original reader-pool disposal did not settle.")); }
        if (WriterPool is not null) try { writerRelease ??= WriterPool.DisposeAsync().AsTask(); await writerRelease.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The original writer-pool disposal did not settle.")); }
        if (IdentityPool is not null) try { identityRelease ??= IdentityPool.DisposeAsync().AsTask(); await identityRelease.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The original identity-pool disposal did not settle.")); }
        if (CreationPool is not null) try { creationRelease ??= CreationPool.DisposeAsync().AsTask(); await creationRelease.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { errors.Add(new AuthHttpFailure("The original creation-pool disposal did not settle.")); }
        if (errors.Count != 0) throw new AggregateException("Borrowed-resource owner cleanup failed.", errors);
    }
    public override string ToString() => nameof(AuthHttpHost);
}
