using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Identity;
using Braid.Persistence.Postgres;
using Xunit.Sdk;

namespace Braid.Tests;

/// <summary>
/// Test-only real HTTPS and restricted PG composition. All issue inputs are
/// captured fixture primitives, never HTTP-supplied identity or verification.
/// Cookie/token/row material remains in memory and never enters assertion text.
/// HttpClient is not a browser cookie-policy enforcement oracle.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class BrowserHttpsFixture
{
    internal const string SessionCookie = "__Host-Braid.Session";
    internal const string AntiforgeryCookie = "__Host-Braid.Antiforgery";
    internal const string TokenHeader = "X-XSRF-TOKEN";
    internal PostgresServerSessionTestSupport Data { get; }
    internal SessionTicketQualificationRoot Files { get; }
    internal X509Certificate2 TlsCertificate { get; }
    internal BrowserHttpsHost First { get; private set; } = null!;
    internal BrowserHttpsHost Second { get; private set; } = null!;
    private readonly List<BrowserHttpsHost> hosts = [];
    private readonly List<IDisposable> rotatedCryptographicResources = [];
    private int sequence;
    private bool partialHostRetained;

    private BrowserHttpsFixture(PostgresServerSessionTestSupport data, SessionTicketQualificationRoot files,
        X509Certificate2 tlsCertificate)
    { Data = data; Files = files; TlsCertificate = tlsCertificate; }

    internal static async Task RunAsync(PostgresFixture postgres, Func<BrowserHttpsFixture, Task> body)
    {
        PostgresServerSessionTestSupport? data = null;
        SessionTicketQualificationRoot? files = null;
        X509Certificate2? tls = null;
        BrowserHttpsFixture? test = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            data = await PostgresServerSessionTestSupport.CreateAsync(postgres);
            files = new();
            using (DurableSessionKeyProfile.BootstrapEmpty(files.Options(data.Organization), files.Certificate, [],
                DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(90))) { }
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Braid owned HTTPS fixture", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: true));
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            tls = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
            test = new(data, files, tls);
            test.First = await test.StartHostAsync();
            test.Second = await test.StartHostAsync();
            Assert.True(!ReferenceEquals(test.First.Profile, test.Second.Profile) &&
                !ReferenceEquals(test.First.Services, test.Second.Services) &&
                !ReferenceEquals(test.First.ReaderDatabase, test.Second.ReaderDatabase) &&
                !ReferenceEquals(test.First.WriterDatabase, test.Second.WriterDatabase) &&
                !ReferenceEquals(test.First.Browser, test.Second.Browser),
                "The two HTTPS instances share an identity, provider or restricted-pool composition.");
            await body(test);
        }
        catch (Exception error) { primary = Safe(error, "The owned HTTPS session qualification failed."); }
        finally
        {
            if (test is not null)
                foreach (var host in test.hosts.AsEnumerable().Reverse())
                    await CleanupAsync(host.DisposeAsync, "An owned HTTPS instance did not settle.", cleanup);
            if (test is not null && (test.partialHostRetained || test.hosts.Any(host => host.RetainsDependencies)))
                cleanup.Add(new InvalidOperationException("Owned dependencies were retained because an HTTPS host did not settle."));
            else
            {
                if (test is not null)
                    foreach (var resource in test.rotatedCryptographicResources.AsEnumerable().Reverse())
                        try { resource.Dispose(); }
                        catch (Exception) { cleanup.Add(new InvalidOperationException("Owned rotated cryptographic resource disposal failed.")); }
                try { tls?.Dispose(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned TLS certificate disposal failed.")); }
                try { files?.Dispose(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned private key fixture disposal failed.")); }
                if (data is not null)
                    await CleanupAsync(data.DisposeAsync, "The owned PostgreSQL case did not settle.", cleanup);
            }
        }
        Throw(primary, cleanup);
    }

    internal async Task<BrowserHttpsHost> StartHostAsync(TimeProvider? clock = null, Guid? organization = null,
        string? writerConnectionString = null, X509Certificate2? currentCertificate = null,
        IReadOnlyCollection<X509Certificate2>? overlaps = null, string? keyDirectory = null, Uri? restartOrigin = null)
    {
        Assert.True(restartOrigin is null || hosts.Any(host => host.Settled && host.Origin == restartOrigin),
            "A restart origin was not previously owned and ordinarily settled by this fixture.");
        var host = await BrowserHttpsHost.StartAsync(this, clock ?? TimeProvider.System,
            organization ?? Data.Organization, writerConnectionString ?? Data.Case.SessionWriterConnectionString,
            currentCertificate ?? Files.Certificate, overlaps ?? [], keyDirectory ?? Files.Keys, restartOrigin);
        hosts.Add(host);
        return host;
    }

    internal int NextSequence() => Interlocked.Increment(ref sequence);
    internal X509Certificate2 CreateOwnedRotationCertificate()
    {
        var rsa = RSA.Create(2048);
        rotatedCryptographicResources.Add(rsa);
        var request = new CertificateRequest("CN=Braid owned overlap DP certificate", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(100));
        rotatedCryptographicResources.Add(certificate);
        return certificate;
    }
    internal void RetainPartialHostDependencies() => partialHostRetained = true;
    internal static Exception Safe(Exception error, string category) => error is XunitException ? error : new InvalidOperationException(category);
    internal static async Task CleanupAsync(Func<ValueTask> action, string category, List<Exception> errors)
    { try { await action(); } catch (Exception) { errors.Add(new InvalidOperationException(category)); } }
    internal static void Throw(Exception? primary, List<Exception> cleanup)
    {
        if (primary is not null)
        {
            if (cleanup.Count != 0) throw new AggregateException("Qualification and owned cleanup failed.", new[] { primary }.Concat(cleanup));
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup.Count != 0) throw new AggregateException("Owned HTTPS qualification cleanup failed.", cleanup);
    }

    internal async Task<long> RowCountAsync() => await Data.Case.ScalarAsync<long>("SELECT count(*) FROM ONLY braid.server_sessions");
    internal Task<DateTimeOffset> DatabaseNowAsync(CancellationToken token) => ClockAsync(
        "SELECT statement_timestamp()", token);
    internal Task<DateTimeOffset> FinalFractionDeadlineAsync(CancellationToken token) => ClockAsync(
        "SELECT date_trunc('second',statement_timestamp())+interval '5 seconds'+interval '900000 microseconds'", token);
    internal Task<DateTimeOffset> DefaultDeadlineAsync(CancellationToken token) => ClockAsync(
        "SELECT statement_timestamp()+interval '90 minutes'", token);
    private async Task<DateTimeOffset> ClockAsync(string fixedQuery, CancellationToken token)
    {
        await using var owner = PostgresServerSessionTestSupport.Unpooled(Data.Case.MigrationConnectionString);
        await owner.OpenAsync(token);
        await using var command = new NpgsqlCommand(fixedQuery, owner) { CommandTimeout = 3 };
        var value = await command.ExecuteScalarAsync(token);
        Assert.True(value is DateTime { Kind: DateTimeKind.Utc }, "The actual PostgreSQL statement time is not finite UTC fixture metadata.");
        return new((DateTime)value!);
    }
    internal async Task<bool> IndependentlyRevokedAsync(ServerSessionKey key, CancellationToken token)
    {
        await using var owner = PostgresServerSessionTestSupport.Unpooled(Data.Case.MigrationConnectionString);
        await owner.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=1 AND bool_and(revoked_at IS NOT NULL)
            FROM ONLY braid.server_sessions WHERE organization_id=$1 AND handle_digest=$2
            """, owner) { CommandTimeout = 3 };
        command.Parameters.AddWithValue(key.OrganizationId);
        command.Parameters.AddWithValue(Convert.FromHexString(key.HandleDigest.Value));
        return await command.ExecuteScalarAsync(token) is true;
    }

    internal static void Kind(BrowserExchange reply, ServerSessionRequestKind expected)
    {
        Assert.True(reply.Record.Admission?.Kind == expected && !reply.Record.UnexpectedFailure,
            "The actual HTTPS request did not retain its expected closed admission category.");
    }
    internal static void Failure(BrowserExchange reply, ServerSessionIdentityFailureKind expected)
    {
        Assert.True(reply.Record.Failure == expected && reply.Record.ClosedWithoutInner && !reply.Record.UnexpectedFailure,
            "The public helper did not retain its expected closed failure category.");
    }
    internal static void NoBodyOrDispatch(BrowserExchange reply)
    {
        Assert.True(reply.Record.BodyReads == 0 && reply.Record.Dispatched == 0,
            "A refused or observational request read its body or dispatched application work.");
    }
    internal static void Identity(BrowserExchange reply, PrincipalId owner, Guid organization, Guid? epoch = null)
    {
        var identity = reply.Record.Admission?.Identity;
        Assert.True(identity is not null && identity.PrincipalId == owner && identity.OrganizationId == organization &&
            (epoch is null || identity.LoginEpoch == epoch), "The admitted private owner, scope or epoch is incorrect.");
        var user = reply.Record.User;
        var claims = user?.Claims.ToArray() ?? [];
        Assert.True(user is not null && user.Identities.Count() == 1 && user.Identities.Single().AuthenticationType == ServerSessionSchemes.Session && claims.Length == 3 &&
            claims.All(c => c.Issuer == ServerSessionSchemes.Session && c.OriginalIssuer == ServerSessionSchemes.Session) &&
            claims.Count(c => c.Type == ClaimTypes.NameIdentifier && c.Value == owner.Value.ToString("D")) == 1 &&
            claims.Count(c => c.Type == "braid:organization_id" && c.Value == organization.ToString("D")) == 1 &&
            claims.Count(c => c.Type == "braid:login_epoch" && c.Value == identity!.LoginEpoch.ToString("D")) == 1,
            "The actual HTTP user is not the closed canonical three-claim identity.");
    }
}

[SupportedOSPlatform("linux")]
internal sealed class BrowserHttpsHost : IAsyncDisposable
{
    internal BrowserHttpsFixture Test { get; }
    internal Uri Origin { get; }
    internal IServiceProvider Services => application.Services;
    internal DurableSessionKeyProfile Profile { get; }
    internal PostgresServerSessionDatabase ReaderDatabase { get; }
    internal PostgresServerSessionDatabase WriterDatabase { get; }
    internal BrowserCountingReader Reader { get; }
    internal BrowserCountingWriter Writer { get; }
    internal ServerSessionBrowser Browser => Services.GetRequiredService<ServerSessionBrowser>();
    internal DateTimeOffset VerifiedDeadline { get; set; }
    internal long BindingVersion { get; set; }
    internal PrincipalId VerifiedOwner { get; set; }
    internal bool PoisonAmbientUser { get; set; }
    internal bool StartHeadersAfterCreateAcknowledgement { get; set; }
    internal bool Settled { get; private set; }
    internal bool RetainsDependencies { get; private set; }
    private readonly WebApplication application;
    private readonly HttpClient client;
    private readonly ConcurrentDictionary<int, BrowserRequestObservation> observations = new();
    private int requests, disposed;

    private BrowserHttpsHost(BrowserHttpsFixture test, Uri origin, WebApplication app, HttpClient http,
        DurableSessionKeyProfile profile, PostgresServerSessionDatabase readerDatabase,
        PostgresServerSessionDatabase writerDatabase, BrowserCountingReader reader, BrowserCountingWriter writer)
    {
        Test = test; Origin = origin; application = app; client = http; Profile = profile;
        ReaderDatabase = readerDatabase; WriterDatabase = writerDatabase; Reader = reader; Writer = writer;
        BindingVersion = test.Data.Binding.Version; VerifiedOwner = test.Data.Principal;
    }

    internal static async Task<BrowserHttpsHost> StartAsync(BrowserHttpsFixture test, TimeProvider clock,
        Guid organization, string writerConnectionString, X509Certificate2 currentCertificate,
        IReadOnlyCollection<X509Certificate2> overlaps, string keyDirectory, Uri? restartOrigin)
    {
        var reservation = new TcpListener(IPAddress.Loopback, restartOrigin?.Port ?? 0);
        DurableSessionKeyProfile? profile = null;
        PostgresServerSessionDatabase? readerDatabase = null, writerDatabase = null;
        WebApplication? application = null;
        HttpClient? client = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            using var starting = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            reservation.Start(1);
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            var origin = new Uri("https://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            profile = DurableSessionKeyProfile.Open(test.Files.Options(organization, directory: keyDirectory), currentCertificate, overlaps);
            readerDatabase = new(test.Data.Case.SessionReaderConnectionString, ServerSessionDatabaseProfile.Reader);
            writerDatabase = new(writerConnectionString, ServerSessionDatabaseProfile.Writer);
            var reader = new BrowserCountingReader(new PostgresServerSessionReader(readerDatabase));
            var writer = new BrowserCountingWriter(new PostgresServerSessionWriter(writerDatabase), test.NextSequence);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            { Args = [], EnvironmentName = Environments.Production, ApplicationName = typeof(BrowserHttpsHost).Assembly.FullName });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 4096;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
                options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(test.TlsCertificate));
            });
            builder.Services.AddBraidBrowserSessions(new ServerSessionBrowserOptions(origin.GetLeftPart(UriPartial.Authority)),
                profile, reader, writer, clock);
            application = builder.Build();
            var pin = test.TlsCertificate.GetCertHash(HashAlgorithmName.SHA256);
            var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false };
            handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
                request.RequestUri is not null && request.RequestUri.GetLeftPart(UriPartial.Authority) == origin.GetLeftPart(UriPartial.Authority) &&
                certificate is not null && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0 &&
                CryptographicOperations.FixedTimeEquals(pin, certificate.GetCertHash(HashAlgorithmName.SHA256));
            client = new(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
            var host = new BrowserHttpsHost(test, origin, application, client, profile, readerDatabase, writerDatabase, reader, writer)
            { VerifiedDeadline = await test.DefaultDeadlineAsync(starting.Token) };
            application.Run(host.HandleAsync);
            // The origin is fixed before registration. The reservation is released
            // once, immediately before bind; collisions fail rather than retarget.
            reservation.Stop();
            await application.StartAsync(starting.Token);
            Assert.True(application.Urls.Count == 1 && application.Urls.Single().TrimEnd('/') == origin.GetLeftPart(UriPartial.Authority),
                "The owned HTTPS host did not bind its original concrete reserved origin.");
            return host;
        }
        catch (Exception error) { primary = BrowserHttpsFixture.Safe(error, "Owned HTTPS host construction failed."); }
        finally { reservation.Stop(); }
        try { client?.Dispose(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned partial HTTP client disposal failed.")); }
        if (application is not null)
        {
            try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await application.StopAsync(stop.Token); }
            catch (Exception) { cleanup.Add(new InvalidOperationException("Owned partial HTTPS host stop failed.")); }
            try { await application.DisposeAsync(); }
            catch (Exception)
            {
                cleanup.Add(new InvalidOperationException("Owned partial HTTPS host disposal failed; its dependencies were retained."));
                test.RetainPartialHostDependencies();
                BrowserHttpsFixture.Throw(primary, cleanup);
                throw new InvalidOperationException("Owned partial HTTPS host settlement did not complete.");
            }
        }
        try { profile?.Dispose(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned partial profile disposal failed.")); }
        if (writerDatabase is not null) await BrowserHttpsFixture.CleanupAsync(writerDatabase.DisposeAsync, "Owned partial writer pool disposal failed.", cleanup);
        if (readerDatabase is not null) await BrowserHttpsFixture.CleanupAsync(readerDatabase.DisposeAsync, "Owned partial reader pool disposal failed.", cleanup);
        if (cleanup.Count != 0) test.RetainPartialHostDependencies();
        BrowserHttpsFixture.Throw(primary, cleanup);
        throw new InvalidOperationException("Owned HTTPS host construction did not complete.");
    }

    internal async Task<BrowserExchange> SendAsync(string path, BrowserCookies? cookies = null, string method = "GET",
        string? token = null, string[]? originValues = null, string[]? tokenValues = null,
        string? rawCookie = null, byte[]? body = null, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref requests);
        Assert.True(id <= 128 && (body is null || body.Length <= 4096), "The owned HTTPS protocol exceeded its request or body bound.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        var transportCancellation = budget.Token;
        var observation = new BrowserRequestObservation();
        Assert.True(observations.TryAdd(id, observation), "An owned HTTPS request identifier was reused.");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Braid-Test-Request", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (rawCookie is not null || cookies?.Header is { Length: > 0 })
            Assert.True(request.Headers.TryAddWithoutValidation("Cookie", rawCookie ?? cookies!.Header), "Owned cookie transport could not be formed.");
        if (originValues is null || originValues.Length != 0)
            Assert.True(request.Headers.TryAddWithoutValidation("Origin", originValues ?? [Origin.GetLeftPart(UriPartial.Authority)]),
                "Owned origin transport could not be formed.");
        if ((tokenValues is null && token is not null) || tokenValues is { Length: > 0 })
            Assert.True(request.Headers.TryAddWithoutValidation(BrowserHttpsFixture.TokenHeader, tokenValues ?? [token!]),
                "Owned request-token transport could not be formed.");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/x-www-form-urlencoded");
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, transportCancellation);
        await using var input = await response.Content.ReadAsStreamAsync(transportCancellation);
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, transportCancellation);
            if (read == 0) break;
            Assert.True(output.Length + read <= 8192, "An owned HTTPS response exceeded its private bound.");
            output.Write(buffer, 0, read);
        }
        await observation.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), transportCancellation);
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? SetCookieHeaderValue.ParseList(values.ToList()).ToArray() : [];
        return new(response.StatusCode, Encoding.UTF8.GetString(output.ToArray()), setCookies,
            response.Headers.Location is not null, observation);
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (!int.TryParse(context.Request.Headers["X-Braid-Test-Request"], out var id) || !observations.TryGetValue(id, out var record))
        { context.Response.StatusCode = 400; return; }
        record.Entered = true;
        var originalBody = context.Request.Body;
        var countingBody = new CountingRequestBody(originalBody, record);
        context.Request.Body = countingBody;
        if (PoisonAmbientUser)
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D"))], "untrusted ambient"));
        try
        {
            switch (context.Request.Path.Value)
            {
                case "/issue":
                case "/issue-twice":
                case "/issue-then-token":
                case "/issue-then-observe":
                case "/issue-then-admit":
                case "/started-issue":
                    if (context.Request.Path == "/started-issue") await context.Response.StartAsync(context.RequestAborted);
                    if (StartHeadersAfterCreateAcknowledgement)
                        Writer.AfterCreateAcknowledgement = async (_, cancellation) => await context.Response.StartAsync(cancellation);
                    await IssueAsync(context);
                    if (context.Request.Path == "/issue-twice") await IssueAsync(context);
                    if (context.Request.Path == "/issue-then-token") await Browser.IssueRequestTokenAsync(context, context.RequestAborted);
                    if (context.Request.Path == "/issue-then-observe") record.Admission = await Browser.ObserveAsync(context, context.RequestAborted);
                    if (context.Request.Path == "/issue-then-admit") record.Admission = await Browser.AdmitRequestAsync(context, context.RequestAborted);
                    if (!context.Response.HasStarted)
                        context.Response.StatusCode = record.Admission?.Kind == ServerSessionRequestKind.Unavailable ? 503 : 204;
                    break;
                case "/revoke":
                    await Browser.RevokeLocalAsync(context, context.RequestAborted);
                    context.Response.StatusCode = 204;
                    break;
                case "/challenge":
                    await context.ChallengeAsync(ServerSessionSchemes.Session);
                    break;
                case "/forbid":
                    await context.ForbidAsync(ServerSessionSchemes.Session);
                    break;
                case "/observe":
                case "/token":
                case "/started-token":
                    record.Admission = await Browser.ObserveAsync(context, context.RequestAborted);
                    record.User = CopyUser(context.User);
                    if (context.Request.Path == "/observe")
                        await context.Response.WriteAsync(record.Admission.Kind.ToString(), context.RequestAborted);
                    else
                    {
                        if (context.Request.Path == "/started-token") await context.Response.StartAsync(context.RequestAborted);
                        var requestToken = await Browser.IssueRequestTokenAsync(context, context.RequestAborted);
                        Assert.True(requestToken.Length is > 0 and <= 4096, "The private AF response is outside its fixed bound.");
                        await context.Response.WriteAsync(requestToken, context.RequestAborted);
                    }
                    break;
                case "/admit":
                    record.Admission = await Browser.AdmitRequestAsync(context, context.RequestAborted);
                    record.User = CopyUser(context.User);
                    if (record.Admission.Kind == ServerSessionRequestKind.Authenticated)
                    {
                        using var body = new MemoryStream();
                        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                        record.Dispatched++;
                        context.Response.StatusCode = 204;
                    }
                    else context.Response.StatusCode = record.Admission.Kind switch
                    {
                        ServerSessionRequestKind.CsrfRejected => 403,
                        ServerSessionRequestKind.Unavailable => 503,
                        _ => 401
                    };
                    break;
                default: context.Response.StatusCode = 404; break;
            }
        }
        catch (ServerSessionIdentityException error)
        {
            record.Failure = error.Kind;
            record.ClosedWithoutInner = error.InnerException is null;
            if (!context.Response.HasStarted) context.Response.StatusCode = error.Kind switch
            { ServerSessionIdentityFailureKind.Denied => 401, ServerSessionIdentityFailureKind.UntrustedIssuance => 409, _ => 503 };
        }
        catch (OperationCanceledException) { record.Canceled = true; if (!context.Response.HasStarted) context.Response.StatusCode = 503; }
        catch (Exception) { record.UnexpectedFailure = true; if (!context.Response.HasStarted) context.Response.StatusCode = 503; }
        finally
        {
            Writer.AfterCreateAcknowledgement = null;
            context.Request.Body = originalBody;
            record.Completed.TrySetResult();
        }
    }

    private Task IssueAsync(HttpContext context) => Browser.IssueFreshAsync(context, Test.Data.Binding.BindingId,
        BindingVersion, VerifiedOwner, VerifiedDeadline, context.RequestAborted);
    private static ClaimsPrincipal CopyUser(ClaimsPrincipal value) => new(value.Identities.Select(identity => new ClaimsIdentity(identity)));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var cleanup = new List<Exception>();
        try { client.Dispose(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned HTTP client disposal failed.")); }
        try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await application.StopAsync(stop.Token); }
        catch (Exception) { cleanup.Add(new InvalidOperationException("Owned HTTPS host stop failed.")); }
        var hostDisposed = false;
        try { await application.DisposeAsync(); hostDisposed = true; }
        catch (Exception) { cleanup.Add(new InvalidOperationException("Owned HTTPS host disposal failed.")); }
        var requestsSettled = observations.Values.All(value => !value.Entered || value.Completed.Task.IsCompleted);
        if (!requestsSettled)
            cleanup.Add(new InvalidOperationException("Owned HTTPS request work remained unsettled after host disposal."));
        RetainsDependencies = !hostDisposed || !requestsSettled;
        if (RetainsDependencies)
        {
            cleanup.Add(new InvalidOperationException("Owned host dependencies were retained while settlement remained unproven."));
            BrowserHttpsFixture.Throw(null, cleanup);
            return;
        }
        try { Profile.Dispose(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned profile disposal failed.")); }
        await BrowserHttpsFixture.CleanupAsync(ReaderDatabase.DisposeAsync, "Owned reader pool disposal failed.", cleanup);
        await BrowserHttpsFixture.CleanupAsync(WriterDatabase.DisposeAsync, "Owned writer pool disposal failed.", cleanup);
        RetainsDependencies = cleanup.Count != 0;
        Settled = !RetainsDependencies;
        BrowserHttpsFixture.Throw(null, cleanup);
    }
}

[SupportedOSPlatform("linux")]
internal sealed class BrowserCookies
{
    internal string? Session { get; set; }
    internal string? Antiforgery { get; set; }
    internal string Header => string.Join("; ", new[]
    {
        Session is null ? null : BrowserHttpsFixture.SessionCookie + "=" + Session,
        Antiforgery is null ? null : BrowserHttpsFixture.AntiforgeryCookie + "=" + Antiforgery
    }.Where(value => value is not null));
    internal BrowserCookies Copy() => new() { Session = Session, Antiforgery = Antiforgery };
    internal void Apply(BrowserExchange reply)
    {
        foreach (var cookie in reply.SetCookies)
        {
            var value = cookie.Value.ToString();
            if (cookie.Name == BrowserHttpsFixture.SessionCookie) Session = value.Length == 0 ? null : value;
            else if (cookie.Name == BrowserHttpsFixture.AntiforgeryCookie) Antiforgery = value.Length == 0 ? null : value;
            else Assert.Fail("The owned browser composition emitted an unexpected cookie name.");
        }
    }
    public override string ToString() => nameof(BrowserCookies);
}

internal sealed record BrowserExchange(HttpStatusCode Status, [property: JsonIgnore] string PrivateBody,
    [property: JsonIgnore] SetCookieHeaderValue[] SetCookies, bool HasLocation,
    [property: JsonIgnore] BrowserRequestObservation Record)
{
    public override string ToString() => nameof(BrowserExchange);
}

internal sealed class BrowserRequestObservation
{
    internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ServerSessionRequestAdmission? Admission;
    internal ClaimsPrincipal? User;
    internal ServerSessionIdentityFailureKind? Failure;
    internal bool Entered, UnexpectedFailure, ClosedWithoutInner, Canceled;
    internal int BodyReads, Dispatched;
    public override string ToString() => nameof(BrowserRequestObservation);
}

internal sealed class CountingRequestBody(Stream inner, BrowserRequestObservation observation) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
    { Interlocked.Increment(ref observation.BodyReads); return inner.Read(buffer, offset, count); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    { Interlocked.Increment(ref observation.BodyReads); return inner.ReadAsync(buffer, offset, count, token); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { Interlocked.Increment(ref observation.BodyReads); return inner.ReadAsync(buffer, token); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class BrowserCountingReader(IServerSessionReader actual) : IServerSessionReader
{
    private int calls;
    internal int Calls => Volatile.Read(ref calls);
    internal Func<ServerSessionKey, CancellationToken, Task>? AfterObserve { get; set; }
    public async Task<CurrentServerSession?> ObserveAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        var result = await actual.ObserveAsync(key, cancellationToken);
        if (AfterObserve is { } after) await after(key, cancellationToken);
        return result;
    }
}

internal sealed class BrowserCountingWriter(IServerSessionWriter actual, Func<int> nextSequence) : IServerSessionWriter
{
    private int creates, renews, revokes;
    internal int Creates => Volatile.Read(ref creates);
    internal int Renews => Volatile.Read(ref renews);
    internal int Revokes => Volatile.Read(ref revokes);
    internal int CreateDispatchSequence { get; private set; }
    internal int RevokeAcknowledgementSequence { get; private set; }
    internal ServerSessionCreateIntent? LastCreate { get; private set; }
    internal ServerSessionWriteResult? LastRenew { get; private set; }
    internal Func<ServerSessionCreateIntent, CancellationToken, Task>? BeforeCreate { get; set; }
    internal Func<ServerSessionWriteResult, CancellationToken, Task>? AfterCreateAcknowledgement { get; set; }
    internal Func<ServerSessionKey, long, CancellationToken, Task>? BeforeRenew { get; set; }
    public async Task<ServerSessionWriteResult> CreateOnlyAsync(ServerSessionCreateIntent intent, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref creates); LastCreate = intent;
        if (BeforeCreate is { } before) await before(intent, cancellationToken);
        CreateDispatchSequence = nextSequence();
        var result = await actual.CreateOnlyAsync(intent, cancellationToken);
        if (AfterCreateAcknowledgement is { } after) await after(result, cancellationToken);
        return result;
    }
    public async Task<ServerSessionWriteResult> RenewAsync(ServerSessionKey key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref renews);
        if (BeforeRenew is { } before) await before(key, expectedVersion, cancellationToken);
        var result = await actual.RenewAsync(key, expectedVersion, cancellationToken);
        LastRenew = result;
        return result;
    }
    public async Task<ServerSessionWriteResult> RevokeAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref revokes);
        var result = await actual.RevokeAsync(key, cancellationToken);
        RevokeAcknowledgementSequence = nextSequence();
        return result;
    }
}

internal sealed class FixedBrowserSchedulingClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
