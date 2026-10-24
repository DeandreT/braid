using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;
using Weft.Identity;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Owned real HTTPS, private DP and restricted PostgreSQL ports; synthetic mapping setup is not worker execution.</summary>
[SupportedOSPlatform("linux")]
internal sealed partial class CatalogSessionHttpsFixture : IAsyncDisposable
{
    internal const string SessionCookie = "__Host-Weft.Session", AntiforgeryCookie = "__Host-Weft.Antiforgery";
    internal const string TokenHeader = "X-XSRF-TOKEN", FixtureHeader = "X-Weft-114-Request";
    internal const string ContextPath = "/api/v1/catalog/context";
    internal PostgresMappingMetadataTestSupport Data { get; private set; } = null!;
    internal ExternalIdentityBinding Binding { get; private set; } = null!;
    internal ExternalIdentityBindingProvisioner IdentityOwner { get; private set; } = null!;
    internal SessionTicketQualificationRoot Files { get; private set; } = null!;
    internal DurableSessionKeyProfile Profile { get; private set; } = null!;
    internal PostgresServerSessionDatabase SessionReaderPool { get; private set; } = null!;
    internal PostgresServerSessionDatabase SessionWriterPool { get; private set; } = null!;
    internal PostgresProjectCreationDatabase CreationPool { get; private set; } = null!;
    internal PostgresDatabase ProjectPool { get; private set; } = null!;
    internal PostgresPayloadPermissionDatabase PayloadPool { get; private set; } = null!;
    internal X509Certificate2 Certificate { get; private set; } = null!;
    internal ServerSessionBrowser Browser => application!.Services.GetRequiredService<ServerSessionBrowser>();
    internal CatalogSessionWriterObserver SessionWriter { get; private set; } = null!;
    internal Uri Origin { get; private set; } = null!;
    internal DateTimeOffset VerifiedDeadline { get; set; }
    internal bool PoisonAmbient, BreakTokenProfileAfterOrganization, AllowDeferredWitness;
    internal CatalogSessionObserveGate? ObserveGate;
    internal CatalogSessionRequestRecord Current => current.Value ?? throw new InvalidOperationException("The owned HTTP port has no request observation.");
    internal bool Settled { get; private set; }
    private readonly AsyncLocal<CatalogSessionRequestRecord?> current = new();
    private readonly ConcurrentDictionary<int, CatalogSessionRequestRecord> records = new();
    private WebApplication? application;
    private HttpClient? client;
    private int requestSequence, unscopedResolutions, disposed;
    private bool sessionReaderClosed;
    private Task? sessionReaderDisposal;

    internal static async Task RunAsync(PostgresFixture postgres, Func<CatalogSessionHttpsFixture, Task> body,
        bool mismatchedCatalogOrganization = false)
    {
        var test = new CatalogSessionHttpsFixture();
        Exception? primary = null; var cleanup = new List<Exception>();
        try
        {
            await test.InitializeAsync(postgres, mismatchedCatalogOrganization);
            await body(test);
        }
        catch (Exception error) { primary = Safe(error, "The owned catalog-session HTTPS case failed."); }
        finally
        {
            try { await test.DisposeAsync(); }
            catch (Exception error) { cleanup.Add(Safe(error, "The owned catalog-session HTTPS case did not settle.")); }
        }
        Throw(primary, cleanup);
    }

    private async Task InitializeAsync(PostgresFixture postgres, bool mismatch)
    {
        Data = await PostgresMappingMetadataTestSupport.CreateAsync(postgres);
        IdentityOwner = new(Data.Case.MigrationConnectionString);
        var binding = await IdentityOwner.BindAsync(new ExternalIdentityKey("catalog-session-fixture",
            "https://owned-identity.invalid/tenant", Guid.NewGuid().ToString("N")), Data.Actor.PrincipalId);
        Check(binding.Outcome == ExternalIdentityBindingOwnerOutcome.Created && binding.Binding is not null,
            "The captured fixture binding was not created.");
        Binding = binding.Binding!;
        Files = new();
        using (DurableSessionKeyProfile.BootstrapEmpty(Files.Options(Data.Seed.OrganizationA), Files.Certificate, [],
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(90))) { }
        Profile = DurableSessionKeyProfile.Open(Files.Options(Data.Seed.OrganizationA), Files.Certificate, []);
        Files.AssertEncryptedRing();
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=Weft owned catalog HTTPS fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
        }
        SessionReaderPool = new(Data.Case.SessionReaderConnectionString, ServerSessionDatabaseProfile.Reader);
        SessionWriterPool = new(Data.Case.SessionWriterConnectionString, ServerSessionDatabaseProfile.Writer);
        CreationPool = new(Data.Case.ProjectCreationConnectionString);
        ProjectPool = new(Data.Case.RuntimeConnectionString);
        PayloadPool = new(Data.Case.PayloadPermissionConnectionString);
        var sessionReader = new CatalogSessionReaderObserver(new PostgresServerSessionReader(SessionReaderPool), this);
        SessionWriter = new(new PostgresServerSessionWriter(SessionWriterPool), this);
        var creation = new CatalogSessionCreationObserver(new PostgresProjectCreator(CreationPool), this);
        VerifiedDeadline = await DatabaseDeadlineAsync(90);
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            reservation.Start(1);
            Origin = new Uri("https://127.0.0.1:" + ((IPEndPoint)reservation.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            { Args = [], EnvironmentName = Environments.Production, ApplicationName = typeof(CatalogSessionHttpsFixture).Assembly.FullName });
            builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 65_536; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
                options.Listen(IPAddress.Loopback, Origin.Port, listen => { listen.Protocols = HttpProtocols.Http1; listen.UseHttps(Certificate); });
            });
            builder.Services.AddWeftApi();
            builder.Services.AddWeftBrowserSessions(new ServerSessionBrowserOptions(Origin.GetLeftPart(UriPartial.Authority)),
                Profile, sessionReader, SessionWriter, new CatalogSessionDueClock());
            var descriptor = builder.Services.Single(value => value.ServiceType == typeof(ServerSessionBrowser) && !value.IsKeyedService);
            Check(descriptor.Lifetime == ServiceLifetime.Singleton && descriptor.ImplementationFactory is not null && descriptor.ImplementationInstance is null,
                "The browser registration is not the prior lazy singleton composition.");
            int antiforgery = builder.Services.Count(value => value.ServiceType == typeof(IAntiforgery));
            builder.Services.AddWeftSessionCatalog(new CatalogBrowserSessionOptions(mismatch ? Data.Seed.OrganizationB : Data.Seed.OrganizationA),
                new CatalogMetadataEditOptions(true), new CatalogRevisionIngestionOptions(true),
                new CatalogProjectCreationOptions(true), new CatalogRevisionReportOptions(true, true));
            Check(ReferenceEquals(descriptor, builder.Services.Single(value => value.ServiceType == typeof(ServerSessionBrowser) && !value.IsKeyedService))
                && builder.Services.Count(value => value.ServiceType == typeof(IAntiforgery)) == antiforgery,
                "Catalog registration replaced/duplicated the browser or installed global Development antiforgery.");
            builder.Services.AddSingleton<ICatalogReader>(new CatalogSessionCatalogObserver(Data.Reader, this));
            builder.Services.AddSingleton<IMappingMetadataWriter>(new CatalogSessionMetadataObserver(Data.Writer, this));
            builder.Services.AddSingleton<IOrganizationProjectCreationReader>(creation);
            builder.Services.AddSingleton<IProjectCreator>(creation);
            builder.Services.AddSingleton<IProjectReader>(new PostgresProjectReader(ProjectPool));
            builder.Services.AddSingleton<IRevisionIngestionService>(_ => { Resolution(); return new CatalogSessionForbiddenIngestion(this); });
            builder.Services.AddSingleton<IRevisionReportReader>(_ => { Resolution(); return new CatalogSessionForbiddenReports(this); });
            builder.Services.AddSingleton<IRevisionReportDeriver>(_ => { Resolution(); return new CatalogSessionForbiddenReports(this); });
            application = builder.Build();
            application.Use(async (context, next) =>
            {
                if (!int.TryParse(context.Request.Headers[FixtureHeader], out int id) || !records.TryGetValue(id, out var record))
                { context.Response.StatusCode = 400; return; }
                record.Entered = true; record.Context = context; current.Value = record;
                var body = context.Request.Body; context.Request.Body = new CatalogSessionCountingBody(body, record);
                if (record.SeedEtag) context.Response.Headers.ETag = "\"fixture-before-admission\"";
                if (PoisonAmbient) context.User = ForgedUser(Data.Seed.OtherProjectAdmin);
                try { await next(context); }
                catch (OperationCanceledException) { record.Canceled = true; throw; }
                catch (Exception) { record.UnexpectedFailure = true; throw; }
                finally { context.Request.Body = body; current.Value = null; record.Context = null; record.Ended.TrySetResult(); }
            });
            application.MapGet("/__weft114/issue", async (HttpContext context) =>
            {
                // Captured fixture data only; no HTTP identity/issuer/binding input.
                await Browser.IssueFreshAsync(context, Binding.BindingId, Binding.Version, Data.Actor.PrincipalId,
                    VerifiedDeadline, context.RequestAborted);
                context.Response.StatusCode = 204;
            }).ExcludeFromDescription();
            application.MapWeftCatalog();
            application.MapWeftHelp();
            byte[] pin = Certificate.GetCertHash(HashAlgorithmName.SHA256);
            var handler = new HttpClientHandler { UseCookies = false, UseProxy = false, AllowAutoRedirect = false };
            handler.ServerCertificateCustomValidationCallback = (message, certificate, _, errors) =>
                message.RequestUri is not null && message.RequestUri.GetLeftPart(UriPartial.Authority) == Origin.GetLeftPart(UriPartial.Authority)
                && certificate is not null && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0
                && CryptographicOperations.FixedTimeEquals(pin, certificate.GetCertHash(HashAlgorithmName.SHA256));
            client = new(handler)
            {
                BaseAddress = Origin,
                Timeout = TimeSpan.FromSeconds(15),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            reservation.Stop(); await application.StartAsync(startup.Token);
            Check(application.Urls.Count == 1 && application.Urls.Single().TrimEnd('/') == Origin.GetLeftPart(UriPartial.Authority),
                "The owned HTTPS server retargeted its reserved origin.");
            Check(unscopedResolutions == 0, "A deferred feature was resolved before an admitted business request.");
        }
        finally { reservation.Stop(); }
    }

    private void Resolution()
    {
        if (current.Value is { } record) Interlocked.Increment(ref record.DeferredResolutions);
        else Interlocked.Increment(ref unscopedResolutions);
    }
    internal void CheckDeferredWitnessWiring()
    {
        // Deliberate post-oracle resolution proves witnesses exist. It is not a
        // production route or worker/store invocation, and occurs after measures.
        var record = new CatalogSessionRequestRecord(); current.Value = record;
        try
        {
            Check(application!.Services.GetRequiredService<IRevisionIngestionService>() is CatalogSessionForbiddenIngestion
                && application.Services.GetRequiredService<IRevisionReportReader>() is CatalogSessionForbiddenReports
                && application.Services.GetRequiredService<IRevisionReportDeriver>() is CatalogSessionForbiddenReports
                && record.DeferredResolutions == 3, "Deferred feature witnesses were not registered as the enabled path dependencies.");
        }
        finally { current.Value = null; }
    }
    internal static ClaimsPrincipal ForgedUser(Guid other) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, other.ToString("D")), new Claim(ClaimTypes.Role, "administrator"),
        new Claim("oid", other.ToString("D")), new Claim("roles", "run.payload.read"), new Claim("weft:organization_id", Guid.NewGuid().ToString("D"))
    ], "untrusted ambient fixture"));
    internal async Task<DateTimeOffset> DatabaseDeadlineAsync(int minutes)
    {
        var now = await Data.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()+$1*interval '1 minute'", minutes);
        return new DateTimeOffset(now);
    }
    internal Task BreakSessionReaderAsync() => SettleOriginalSessionReaderDisposalAsync();
    private async Task SettleOriginalSessionReaderDisposalAsync()
    {
        // The first concrete async ValueTask invocation is retained before any bounded wait.
        // Every later observer awaits this exact task; idempotent re-entry is not completion proof.
        Task original = sessionReaderDisposal ??= SessionReaderPool.DisposeAsync().AsTask();
        await original.WaitAsync(TimeSpan.FromSeconds(10));
        sessionReaderClosed = true;
    }
    internal Task SetOrganizationAsync(bool active) => Data.Case.ExecuteAsync("UPDATE weft.organization_memberships SET active=$1 WHERE organization_id=$2 AND principal_id=$3",
        active, Data.Seed.OrganizationA, Data.Seed.Alice);
    internal Task SetMembershipAsync(bool active, string role = "publisher") => Data.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=$1,role=$2,version=version+1 WHERE organization_id=$3 AND project_id=$4 AND principal_id=$5",
        active, role, Data.Scope.OrganizationId, Data.Scope.ProjectId, Data.Seed.Alice);
    internal async Task SetCapabilityAsync(bool allowed)
    {
        await using var owner = new OrganizationProjectCreationCapabilityProvisioner(Data.Case.MigrationConnectionString);
        await owner.SetAsync(Data.Seed.OrganizationA, Data.Actor.PrincipalId, allowed);
    }
    internal async Task<bool> EffectivePayloadAsync(ActorContext actor)
    {
        var project = new ProjectScope(Data.Scope.OrganizationId, Data.Scope.ProjectId);
        var access = await new PostgresProjectReader(ProjectPool).GetAccessAsync(actor, project);
        var grant = await new PostgresProjectPayloadPermissionReader(PayloadPool).ObserveAsync(actor, project);
        return ProjectPayloadPermissionPolicy.ObserveCurrent(actor, project, access, grant) is not null;
    }
    internal async Task SetPayloadAsync(bool allowed, long version)
    {
        await using var owner = new ProjectPayloadGrantProvisioner(Data.Case.MigrationConnectionString);
        var result = await owner.SetAsync(new(Data.Scope.OrganizationId, Data.Scope.ProjectId), Data.Actor.PrincipalId, allowed, version);
        Check(result.Outcome is ProjectPayloadGrantOwnerOutcome.Created or ProjectPayloadGrantOwnerOutcome.Updated, "The explicit fixture grant transition was not acknowledged.");
    }
    internal static void Check(bool value, string category)
    { if (!value) throw new CatalogSessionQualificationFailure(category); }
    private static Exception Safe(Exception error, string category) => error is CatalogSessionQualificationFailure ? error : new InvalidOperationException(category);
    private static void Throw(Exception? primary, List<Exception> cleanup)
    {
        if (primary is not null)
        {
            if (cleanup.Count != 0) throw new AggregateException("Owned catalog-session qualification and settlement failed.", new[] { primary }.Concat(cleanup));
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup.Count != 0) throw new AggregateException("Owned catalog-session settlement failed.", cleanup);
    }
    private static async Task CleanupAsync(Func<ValueTask> action, string category, List<Exception> errors)
    { try { await action().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { errors.Add(new InvalidOperationException(category)); } }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ObserveGate?.Release.TrySetResult();
        foreach (var record in records.Values) record.BodyGate?.Release.TrySetResult();
        var cleanup = new List<Exception>();
        try { client?.Dispose(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned HTTPS client disposal failed.")); }
        bool appDisposed = application is null;
        if (application is not null)
        {
            try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await application.StopAsync(stop.Token); }
            catch (Exception) { cleanup.Add(new InvalidOperationException("Owned HTTPS host stop failed.")); }
            try { await application.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); appDisposed = true; }
            catch (Exception) { cleanup.Add(new InvalidOperationException("Owned HTTPS host disposal failed.")); }
        }
        // Host stop/drain attempts above may allow an already-owned reader disposal to finish.
        // Observe that exact task even if independent HTTP settlement also failed.
        if (sessionReaderDisposal is not null && !sessionReaderClosed)
        {
            try { await SettleOriginalSessionReaderDisposalAsync(); }
            catch (Exception) { cleanup.Add(new InvalidOperationException("The original owned session-reader disposal task did not settle successfully.")); }
        }
        bool requestsEnded = records.Values.All(record => !record.Entered || record.Ended.Task.IsCompleted);
        if (!appDisposed || !requestsEnded || (sessionReaderDisposal is not null && !sessionReaderClosed))
        {
            cleanup.Add(new InvalidOperationException("Owned pools, profile, keys and case retained because HTTP/work or original reader-disposal settlement is unproven."));
            Throw(null, cleanup); return;
        }
        // With no earlier break, first dispose the reader only after HTTP/work has settled.
        if (SessionReaderPool is not null && !sessionReaderClosed)
        {
            try { await SettleOriginalSessionReaderDisposalAsync(); }
            catch (Exception) { cleanup.Add(new InvalidOperationException("The original owned session-reader disposal task did not settle successfully.")); }
            if (!sessionReaderClosed)
            {
                cleanup.Add(new InvalidOperationException("Owned pools, profile, keys and case retained while original reader disposal is unproven."));
                Throw(null, cleanup); return;
            }
        }
        bool children = true;
        try { Profile?.Dispose(); } catch (Exception) { children = false; cleanup.Add(new InvalidOperationException("Owned DP profile did not settle.")); }
        int before = cleanup.Count;
        if (SessionWriterPool is not null) await CleanupAsync(SessionWriterPool.DisposeAsync, "Owned session writer pool did not settle.", cleanup);
        if (CreationPool is not null) await CleanupAsync(CreationPool.DisposeAsync, "Owned creation pool did not settle.", cleanup);
        if (ProjectPool is not null) await CleanupAsync(ProjectPool.DisposeAsync, "Owned project pool did not settle.", cleanup);
        if (PayloadPool is not null) await CleanupAsync(PayloadPool.DisposeAsync, "Owned grant reader pool did not settle.", cleanup);
        if (IdentityOwner is not null) await CleanupAsync(IdentityOwner.DisposeAsync, "Owned identity provisioner did not settle.", cleanup);
        children &= cleanup.Count == before;
        if (!children)
        { cleanup.Add(new InvalidOperationException("Owned key/case dependencies retained while child settlement is unproven.")); Throw(null, cleanup); return; }
        try { Certificate?.Dispose(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned TLS certificate disposal failed.")); }
        try { Files?.Dispose(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned private key root disposal failed.")); }
        if (Data is not null) await CleanupAsync(Data.DisposeAsync, "Owned catalog/metadata PostgreSQL case did not settle.", cleanup);
        Settled = cleanup.Count == 0;
        Throw(null, cleanup);
    }
    public override string ToString() => nameof(CatalogSessionHttpsFixture);
}

/// <summary>Only source-owned categorical messages may survive the outer redaction boundary.</summary>
internal sealed class CatalogSessionQualificationFailure(string category) : Exception(category)
{
    public override string ToString() => nameof(CatalogSessionQualificationFailure);
}
