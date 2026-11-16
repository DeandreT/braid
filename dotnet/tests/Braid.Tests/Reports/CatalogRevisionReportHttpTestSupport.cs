using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual HTTP1 Kestrel and catalog pool; supplied report ports default to the qualified real workflow.</summary>
[SupportedOSPlatform("linux")]
internal sealed class CatalogRevisionReportHttpTestSupport : IAsyncDisposable
{
    private static readonly IDataProtectionProvider Keys = new EphemeralDataProtectionProvider();
    private readonly WebApplication app;
    private readonly PostgresCatalogDatabase database;
    private readonly List<RequestObservation> observations = [];
    private RequestObservation? nextObservation;
    private int revisionReads, readerResolutions, deriverResolutions, currentReads, retainedReads, ensures, completedCalls, bodyReads;
    internal Uri Origin { get; }
    internal HttpClient Client { get; }
    internal CookieContainer Cookies { get; } = new();
    internal int RevisionReads => Volatile.Read(ref revisionReads);
    internal int ReaderResolutions => Volatile.Read(ref readerResolutions);
    internal int DeriverResolutions => Volatile.Read(ref deriverResolutions);
    internal int CurrentReads => Volatile.Read(ref currentReads);
    internal int RetainedReads => Volatile.Read(ref retainedReads);
    internal int Ensures => Volatile.Read(ref ensures);
    internal int CompletedCalls => Volatile.Read(ref completedCalls);
    internal int BodyReads => Volatile.Read(ref bodyReads);
    internal Func<ActorContext, RevisionScope, int, CancellationToken, Task>? BeforeRevisionAsync { get; set; }

    private CatalogRevisionReportHttpTestSupport(WebApplication app, PostgresCatalogDatabase database, Uri origin)
    {
        this.app = app;
        this.database = database;
        Origin = origin;
        Client = NewClient(Cookies);
    }

    internal HttpClient NewClient(CookieContainer? cookies = null) => new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = cookies is not null,
        CookieContainer = cookies ?? new CookieContainer()
    })
    {
        BaseAddress = Origin,
        Timeout = TimeSpan.FromSeconds(40),
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
    };

    internal static async Task<CatalogRevisionReportHttpTestSupport> StartAsync(RevisionReportTestSupport test,
        bool enabled = true, bool derivationEnabled = true, Guid? principal = null,
        IRevisionReportReader? reader = null, IRevisionReportDeriver? deriver = null,
        bool registerReader = true, bool registerDeriver = true, RevisionReportPolicy? activeReadCaps = null)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}");
        WebApplication? app = null;
        PostgresCatalogDatabase? database = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Limits.MaxRequestLineSize = 65536;
                server.Listen(IPAddress.Loopback, origin.Port, listen => listen.Protocols = HttpProtocols.Http1);
            });
            builder.Services.AddBraidApi();
            builder.Services.AddSingleton(Keys);
            builder.Services.AddBraidCatalog(new(true, true, principal ?? test.Actor.PrincipalId.Value,
                test.Project.OrganizationId, origin.GetLeftPart(UriPartial.Authority)), builder.Environment,
                new CatalogMetadataEditOptions(), new CatalogRevisionIngestionOptions(), new CatalogProjectCreationOptions(),
                new CatalogRevisionReportOptions(enabled, derivationEnabled, activeReadCaps));
            database = new(test.Case.CatalogConnectionString);
            var actualCatalog = new PostgresCatalogReader(database);
            CatalogRevisionReportHttpTestSupport? support = null;
            builder.Services.AddSingleton<ICatalogReader>(_ => new CatalogObserver(actualCatalog, support!));
            if (registerReader) builder.Services.AddSingleton<IRevisionReportReader>(_ =>
            {
                Interlocked.Increment(ref support!.readerResolutions);
                return new ReportReaderObserver(reader ?? test.Service, support);
            });
            if (registerDeriver) builder.Services.AddSingleton<IRevisionReportDeriver>(_ =>
            {
                Interlocked.Increment(ref support!.deriverResolutions);
                return new ReportDeriverObserver(deriver ?? test.Service, support);
            });
            app = builder.Build();
            support = new(app, database, origin);
            app.Use(async (context, next) =>
            {
                var isReport = context.Request.Path.Value?.Contains("/reports/", StringComparison.Ordinal) == true;
                var candidate = Volatile.Read(ref support.nextObservation);
                var observation = isReport && candidate?.TryCapture() == true ? candidate : null;
                observation?.Capture(context.RequestAborted);
                if (isReport) context.Request.Body = new BodyObserver(context.Request.Body, support);
                try { await next(context); }
                finally { observation?.Finish(context.Response.HasStarted, context.Response.StatusCode, context.RequestAborted.IsCancellationRequested); }
            });
            app.MapBraidCatalog();
            reservation.Stop();
            await app.StartAsync();
            return support;
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            if (database is not null) await database.DisposeAsync();
            throw;
        }
        finally { reservation.Stop(); }
    }

    internal void ResetObservations()
    {
        Volatile.Write(ref revisionReads, 0);
        Volatile.Write(ref currentReads, 0);
        Volatile.Write(ref retainedReads, 0);
        Volatile.Write(ref ensures, 0);
        Volatile.Write(ref completedCalls, 0);
        Volatile.Write(ref bodyReads, 0);
    }

    internal RequestObservation ObserveNextRequest()
    {
        var observation = new RequestObservation();
        observations.Add(observation);
        Volatile.Write(ref nextObservation, observation);
        return observation;
    }

    internal async Task<string> TokenAsync()
    {
        using var response = await Client.GetAsync("/api/v1/catalog/context");
        return (await CatalogHttpTestSupport.JsonAsync(response)).GetProperty("requestToken").GetString()!;
    }

    internal static string RevisionPath(RevisionScope scope) =>
        $"/api/v1/organizations/{scope.OrganizationId:D}/projects/{scope.ProjectId:D}/mappings/{scope.MappingId:D}/revisions/{scope.RevisionId:D}";
    internal static string CurrentPath(RevisionScope scope, RevisionReportOperation operation) => RevisionPath(scope) +
        "/reports/" + (operation == RevisionReportOperation.Projection ? "projection" : "validation");
    internal static string RetainedPath(RevisionScope scope, RevisionReportOperation operation, Guid derivationId) =>
        CurrentPath(scope, operation) + $"/{derivationId:D}";
    internal static string EnsurePath(RevisionScope scope, RevisionReportOperation operation) => CurrentPath(scope, operation) + "/ensure";

    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token = null,
        string? origin = null, IEnumerable<KeyValuePair<string, string>>? headers = null, byte[]? body = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post || body is not null) request.Content = new ByteArrayContent(body ?? []);
        if (token is not null) request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token);
        if (method == HttpMethod.Post) request.Headers.TryAddWithoutValidation("Origin", origin ?? Origin.GetLeftPart(UriPartial.Authority));
        if (headers is not null)
            foreach (var header in headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) request.Headers.Host = header.Value;
                else if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    request.Content ??= new ByteArrayContent([]);
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        return await Client.SendAsync(request, cancellationToken);
    }

    internal static async Task<JsonElement> ReportAsync(HttpResponseMessage response, string operation,
        string state = "available", HttpStatusCode status = HttpStatusCode.OK)
    {
        var value = await CatalogHttpTestSupport.JsonAsync(response, status);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
        Assert.Equal("braid.catalog-revision-report.v1", value.GetProperty("schemaVersion").GetString());
        Assert.Equal(operation, value.GetProperty("operation").GetString());
        Assert.Equal(state, value.GetProperty("state").GetString());
        var expected = new List<string> { "schemaVersion", "revision", "operation", "state", "readOutcome", "status",
            "result", "currentAttempt", "nextAttemptAtUtc", "failure", "report" };
        if (operation == "validation") expected.Add("projectionDerivationId");
        Assert.Equal(expected.Order(StringComparer.Ordinal), value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        if (value.GetProperty("result").ValueKind != JsonValueKind.Null)
            Assert.Equal(new[] { "derivationId", "finishedAtUtc", "generation", "startedAtUtc" },
                value.GetProperty("result").EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        return value;
    }

    internal static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var value = await CatalogHttpTestSupport.JsonAsync(response, status);
        Assert.Equal("code", Assert.Single(value.EnumerateObject()).Name);
        Assert.Equal(code, value.GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
    }

    internal async Task AssertCatalogPoolUnboundAsync()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await using var command = new NpgsqlCommand("SELECT NULLIF(current_setting('braid.organization_id',true),''), " +
            "(SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND usename=current_user " +
            "AND pid<>pg_backend_pid() AND xact_start IS NOT NULL)", connection);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.True(rows.IsDBNull(0));
        Assert.Equal(0L, rows.GetInt64(1));
    }

    internal async Task<RawRequest> StartRawAsync(string method, string path, string? token,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, Origin.Port, timeout.Token);
            var text = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: {Origin.Authority}\r\nConnection: close\r\n");
            var requestUri = new Uri(Origin, path);
            if (token is not null)
                Assert.True(Cookies.GetCookies(requestUri).Cast<Cookie>().Any(cookie =>
                    cookie.Name == ".Braid.Catalog.Antiforgery" && cookie.Value.Length > 0),
                    "An authorized raw request requires its actual route-scoped framework cookie.");
            text.Append("Cookie: ").Append(Cookies.GetCookieHeader(requestUri)).Append("\r\n");
            if (method == "POST") text.Append("Origin: ").Append(Origin.GetLeftPart(UriPartial.Authority)).Append("\r\n");
            if (token is not null) text.Append("X-XSRF-TOKEN: ").Append(token).Append("\r\n");
            var supplied = headers?.ToArray() ?? [];
            foreach (var header in supplied) text.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            if (method == "POST" && !supplied.Any(header => header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))) text.Append("Content-Length: 0\r\n");
            text.Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(text.ToString());
            Assert.True(bytes.Length <= 65536);
            var sent = 0;
            while (sent < bytes.Length)
            {
                var count = await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, timeout.Token);
                Assert.True(count > 0);
                sent += count;
            }
            return new(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    internal sealed class RawRequest(Socket socket) : IDisposable
    {
        private int disposed;
        internal async Task<int> ReadStatusAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[16384];
            var count = 0;
            while (true)
            {
                Assert.True(count < buffer.Length);
                var read = await socket.ReceiveAsync(buffer.AsMemory(count, 1), SocketFlags.None, timeout.Token);
                Assert.True(read > 0);
                count += read;
                if (count >= 2 && buffer[count - 2] == '\r' && buffer[count - 1] == '\n')
                    return int.Parse(Encoding.ASCII.GetString(buffer, 0, count).Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        internal void Abort()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) socket.Close(0);
        }
        public void Dispose() => Abort();
    }

    internal sealed class RequestObservation : IDisposable
    {
        private int captured;
        private CancellationTokenRegistration registration;
        internal TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool AbortedAtFinish { get; private set; }
        internal bool ResponseStartedAtFinish { get; private set; }
        internal int StatusAtFinish { get; private set; }
        internal bool TryCapture() => Interlocked.CompareExchange(ref captured, 1, 0) == 0;
        internal void Capture(CancellationToken token) => registration = token.Register(() => Aborted.TrySetResult());
        internal void Finish(bool started, int status, bool aborted)
        {
            ResponseStartedAtFinish = started;
            StatusAtFinish = status;
            AbortedAtFinish = aborted;
            Finished.TrySetResult();
        }
        public void Dispose() => registration.Dispose();
    }

    private sealed class BodyObserver(Stream inner, CatalogRevisionReportHttpTestSupport owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Interlocked.Increment(ref owner.bodyReads); return inner.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { Interlocked.Increment(ref owner.bodyReads); return inner.Read(buffer); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref owner.bodyReads); return inner.ReadAsync(buffer, cancellationToken); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Interlocked.Increment(ref owner.bodyReads); return inner.ReadAsync(buffer, offset, count, cancellationToken); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ReportReaderObserver(IRevisionReportReader actual, CatalogRevisionReportHttpTestSupport owner) : IRevisionReportReader
    {
        public async Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope,
            RevisionReportOperation operation, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner.currentReads);
            var result = await actual.GetCurrentAsync(actor, scope, operation, projectionDerivationId, cancellationToken);
            Interlocked.Increment(ref owner.completedCalls);
            return result;
        }
        public async Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope,
            RevisionReportOperation operation, Guid derivationId, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner.retainedReads);
            var result = await actual.GetRetainedAsync(actor, scope, operation, derivationId, projectionDerivationId, cancellationToken);
            Interlocked.Increment(ref owner.completedCalls);
            return result;
        }
    }

    private sealed class ReportDeriverObserver(IRevisionReportDeriver actual, CatalogRevisionReportHttpTestSupport owner) : IRevisionReportDeriver
    {
        public async Task<RevisionReportEnsureResult> EnsureAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
            Guid? repairDerivationId = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner.ensures);
            var result = await actual.EnsureAsync(actor, scope, operation, repairDerivationId, cancellationToken);
            Interlocked.Increment(ref owner.completedCalls);
            return result;
        }
    }

    private sealed class CatalogObserver(ICatalogReader actual, CatalogRevisionReportHttpTestSupport owner) : ICatalogReader
    {
        public Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionAsync(ActorContext actor, Guid organizationId, CancellationToken cancellationToken = default) => actual.GetOrganizationAdmissionAsync(actor, organizationId, cancellationToken);
        public Task<CatalogProjectPage?> ListProjectsAsync(ActorContext actor, Guid organizationId, int limit, CatalogProjectCursor? cursor = null, CancellationToken cancellationToken = default) => actual.ListProjectsAsync(actor, organizationId, limit, cursor, cancellationToken);
        public Task<CatalogProject?> GetProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => actual.GetProjectAsync(actor, scope, cancellationToken);
        public Task<CatalogMappingPage?> ListMappingsAsync(ActorContext actor, ProjectScope scope, int limit, CatalogMappingCursor? cursor = null, CancellationToken cancellationToken = default) => actual.ListMappingsAsync(actor, scope, limit, cursor, cancellationToken);
        public Task<CatalogMapping?> GetMappingAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default) => actual.GetMappingAsync(actor, scope, cancellationToken);
        public Task<CatalogMappingMetadata?> GetMappingMetadataAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default) => actual.GetMappingMetadataAsync(actor, scope, cancellationToken);
        public Task<CatalogRevisionPage?> ListRevisionsAsync(ActorContext actor, MappingScope scope, int limit, CatalogRevisionCursor? cursor = null, CancellationToken cancellationToken = default) => actual.ListRevisionsAsync(actor, scope, limit, cursor, cancellationToken);
        public async Task<CatalogRevision?> GetRevisionAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref owner.revisionReads);
            if (owner.BeforeRevisionAsync is { } observe) await observe(actor, scope, call, cancellationToken);
            return await actual.GetRevisionAsync(actor, scope, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { await app.DisposeAsync(); }
        finally
        {
            foreach (var observation in observations) observation.Dispose();
            await database.DisposeAsync();
        }
    }
}
