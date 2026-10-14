using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Actual loopback Kestrel, qualified C2 service, restricted PostgreSQL pools and physical native storage.</summary>
[SupportedOSPlatform("linux")]
internal sealed class RevisionUploadHttpTestSupport : IAsyncDisposable
{
    internal const int MaximumNativeBytes = 4 * 1024 * 1024;
    internal const int MaximumRequestLineBytes = 65_536;
    internal const string MediaType = "application/octet-stream";
    // Identity adversaries and restarts use the same explicit memory-only framework keys.
    private static readonly IDataProtectionProvider TestKeys = new EphemeralDataProtectionProvider();
    private readonly WebApplication app;
    private readonly PostgresCatalogDatabase? catalogDatabase;
    private BodyGate? bodyGate;
    private RequestObservation? requestObservation;
    private int resourceReads;
    private int serviceCalls;
    private int completedServiceCalls;
    private int serviceResolutions;
    private int receiptCalls;
    private int bodyReads;
    private long bodyBytes;
    internal HttpClient Client { get; }
    internal CookieContainer Cookies { get; } = new();
    internal Uri Origin { get; }
    internal int ResourceReads => Volatile.Read(ref resourceReads);
    internal int ServiceCalls => Volatile.Read(ref serviceCalls);
    internal int CompletedServiceCalls => Volatile.Read(ref completedServiceCalls);
    internal int ServiceResolutions => Volatile.Read(ref serviceResolutions);
    internal int ReceiptCalls => Volatile.Read(ref receiptCalls);
    internal int BodyReads => Volatile.Read(ref bodyReads);
    internal long BodyBytes => Interlocked.Read(ref bodyBytes);
    internal ConcurrentQueue<string> Events { get; } = new();

    private RevisionUploadHttpTestSupport(WebApplication app, Uri origin, PostgresCatalogDatabase? catalogDatabase)
    {
        this.app = app;
        Origin = origin;
        this.catalogDatabase = catalogDatabase;
        Client = NewClient(Cookies);
    }

    internal HttpClient NewClient(CookieContainer? cookies = null) => new(new HttpClientHandler
    {
        UseCookies = cookies is not null,
        CookieContainer = cookies ?? new CookieContainer(),
        UseProxy = false,
        AllowAutoRedirect = false
    })
    { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(45) };

    internal static async Task<RevisionUploadHttpTestSupport> StartAsync(RevisionIngestionTestSupport? test = null,
        IRevisionIngestionService? service = null, bool enabled = true, int maximumConcurrentOperations = 2,
        bool registerIngestion = true, Guid? principal = null, Guid? organization = null,
        Action<Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions>? configureListener = null)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}");
        WebApplication? app = null;
        PostgresCatalogDatabase? reader = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Limits.MaxRequestLineSize = MaximumRequestLineBytes;
                server.Listen(IPAddress.Loopback, origin.Port, configureListener ?? (_ => { }));
            });
            builder.Services.AddWeftApi();
            builder.Services.AddSingleton(TestKeys);
            var options = test is null ? new CatalogApiOptions() : new CatalogApiOptions(true, true,
                principal ?? test.Seed.Alice, organization ?? test.Project.OrganizationId,
                origin.GetLeftPart(UriPartial.Authority));
            builder.Services.AddWeftCatalog(options, builder.Environment, new CatalogMetadataEditOptions(),
                new CatalogRevisionIngestionOptions(test is not null && enabled, maximumConcurrentOperations));
            RevisionUploadHttpTestSupport? support = null;
            if (test is not null)
            {
                reader = new(test.Case.CatalogConnectionString);
                var actualReader = new PostgresCatalogReader(reader);
                builder.Services.AddSingleton<ICatalogReader>(_ => new ObservedReader(actualReader, support!));
                builder.Services.AddSingleton<IProjectReader>(new PostgresProjectReader(test.ReaderDatabase));
                if (registerIngestion)
                    builder.Services.AddSingleton<IRevisionIngestionService>(_ =>
                    {
                        Interlocked.Increment(ref support!.serviceResolutions);
                        return new ObservedService(service ?? test.Service, support!);
                    });
            }
            app = builder.Build();
            support = new(app, origin, reader);
            app.Use(async (context, next) =>
            {
                var candidate = Volatile.Read(ref support.bodyGate);
                // A refusal or receipt poll concurrent with the selected request cannot
                // complete that request's gate or inherit its first-read suspension.
                var gate = context.Request.Method == "POST" && candidate?.TryCapture() == true ? candidate : null;
                var observationCandidate = Volatile.Read(ref support.requestObservation);
                var observation = context.Request.Method == "POST" && observationCandidate?.TryCapture() == true
                    ? observationCandidate : null;
                observation?.Capture(context.RequestAborted);
                if (context.Request.Method == "POST")
                    context.Request.Body = new ObservedBody(context.Request.Body, support, gate, observation);
                try { await next(context); }
                catch (Exception error)
                {
                    observation?.RecordPipelineException(error);
                    throw;
                }
                finally
                {
                    observation?.Finish(context.RequestAborted.IsCancellationRequested,
                        context.Response.HasStarted, context.Response.StatusCode);
                    gate?.RequestEnded.TrySetResult();
                }
            });
            app.MapWeftCatalog();
            app.MapOpenApi();
            reservation.Stop();
            await app.StartAsync();
            Assert.Equal(origin.GetLeftPart(UriPartial.Authority), Assert.Single(app.Urls).TrimEnd('/'));
            return support;
        }
        catch
        {
            try { if (app is not null) await app.DisposeAsync(); }
            finally { if (reader is not null) await reader.DisposeAsync(); }
            throw;
        }
        finally { reservation.Stop(); }
    }

    internal void ResetObservations()
    {
        Volatile.Write(ref resourceReads, 0);
        Volatile.Write(ref serviceCalls, 0);
        Volatile.Write(ref completedServiceCalls, 0);
        Volatile.Write(ref receiptCalls, 0);
        Volatile.Write(ref bodyReads, 0);
        Interlocked.Exchange(ref bodyBytes, 0);
        Events.Clear();
    }

    internal BodyGate GateBody()
    {
        var gate = new BodyGate();
        Volatile.Write(ref bodyGate, gate);
        return gate;
    }

    internal void ClearBodyGate() => Volatile.Write(ref bodyGate, null);

    /// <summary>Explicitly armed, nonblocking observation of one real POST; records categories and counts only.</summary>
    internal RequestObservation ObserveNextPost(long successfulReadThreshold)
    {
        var observation = new RequestObservation(successfulReadThreshold);
        Volatile.Write(ref requestObservation, observation);
        return observation;
    }

    internal async Task AssertNoHeldCatalogTransactionAsync()
    {
        var database = catalogDatabase ?? throw new InvalidOperationException("The catalog observer requires the actual restricted pool.");
        await using var observer = await database.DataSource.OpenConnectionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND usename=current_user AND pid<>pg_backend_pid() AND xact_start IS NOT NULL", observer);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    internal async Task<string> TokenAsync()
    {
        using var response = await Client.GetAsync("/api/v1/catalog/context");
        var context = await CatalogHttpTestSupport.JsonAsync(response);
        return context.GetProperty("requestToken").GetString()!;
    }

    internal static string CreatePath(ProjectScope scope) => CatalogHttpTestSupport.ProjectPath(scope) + "/mappings";
    internal static string AppendPath(MappingScope scope) =>
        $"{CatalogHttpTestSupport.ProjectPath(new(scope.OrganizationId, scope.ProjectId))}/mappings/{scope.MappingId:D}/revisions";
    internal static string ReceiptPath(ProjectScope scope, Guid operationId) =>
        $"{CatalogHttpTestSupport.ProjectPath(scope)}/revision-ingestions/{operationId:D}";
    internal static string CreateQuery(string name = "HTTP native integration", string description = "Actual HTTP PostgreSQL private store Ferrule",
        string backendId = "ferrule", string? filename = null) =>
        $"?name={Uri.EscapeDataString(name)}&description={Uri.EscapeDataString(description)}&backendId={Uri.EscapeDataString(backendId)}" +
        (filename is null ? "" : $"&originalFilename={Uri.EscapeDataString(filename)}");

    internal async Task<HttpResponseMessage> PostAsync(string path, string? token, Guid? key, byte[] body,
        bool chunked = false, string? contentType = MediaType, string? encoding = null, string? origin = null,
        HttpClient? client = null, CancellationToken cancellationToken = default,
        IEnumerable<KeyValuePair<string, string>>? additionalHeaders = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = chunked ? new UnknownLengthContent(body) : new ByteArrayContent(body)
        };
        if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (encoding is not null) request.Content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);
        if (token is not null) request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token);
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key.Value.ToString("D"));
        request.Headers.TryAddWithoutValidation("Origin", origin ?? Origin.GetLeftPart(UriPartial.Authority));
        if (additionalHeaders is not null)
            foreach (var header in additionalHeaders)
                Assert.True(request.Headers.TryAddWithoutValidation(header.Key, header.Value));
        return await (client ?? Client).SendAsync(request, cancellationToken);
    }

    internal static async Task<JsonElement> ResultAsync(HttpResponseMessage response, HttpStatusCode status, string outcome)
    {
        var result = await CatalogHttpTestSupport.JsonAsync(response, status);
        Assert.Equal(outcome, result.GetProperty("outcome").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
        AssertSafeResult(result);
        return result;
    }

    internal static void AssertSafeResult(JsonElement result)
    {
        Assert.Equal(new[] { "committedRevisionUrl", "failure", "outcome", "receipt", "rejection" },
            result.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        if (result.GetProperty("receipt").ValueKind != JsonValueKind.Null)
            AssertSafeReceipt(result.GetProperty("receipt"));
    }

    internal static void AssertSafeReceipt(JsonElement receipt)
    {
        Assert.Equal(new[] { "claimCount", "committedAdmission", "committedSequence", "completedAtUtc", "futureMapping", "futureRevision", "latestFailure", "operationId", "originalFilename", "phase", "project", "reservedAtUtc" },
            receipt.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.String, receipt.GetProperty("operationId").ValueKind);
        Assert.Equal(new[] { "organizationId", "projectId" }, receipt.GetProperty("project").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "mappingId", "organizationId", "projectId" }, receipt.GetProperty("futureMapping").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "mappingId", "organizationId", "projectId", "revisionId" }, receipt.GetProperty("futureRevision").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        Assert.Equal(TimeSpan.Zero, receipt.GetProperty("reservedAtUtc").GetDateTimeOffset().Offset);
        if (receipt.GetProperty("completedAtUtc").ValueKind != JsonValueKind.Null)
            Assert.Equal(TimeSpan.Zero, receipt.GetProperty("completedAtUtc").GetDateTimeOffset().Offset);
    }

    internal static Guid Operation(JsonElement result) => result.GetProperty("receipt").GetProperty("operationId").GetGuid();
    internal static MappingScope Mapping(JsonElement result)
    {
        var scope = result.GetProperty("receipt").GetProperty("futureMapping");
        return new(scope.GetProperty("organizationId").GetGuid(), scope.GetProperty("projectId").GetGuid(), scope.GetProperty("mappingId").GetGuid());
    }
    internal static Guid Revision(JsonElement result) => result.GetProperty("receipt").GetProperty("futureRevision").GetProperty("revisionId").GetGuid();

    internal static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var error = await CatalogHttpTestSupport.JsonAsync(response, status);
        Assert.Equal("code", Assert.Single(error.EnumerateObject()).Name);
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);
        Assert.Null(response.Headers.ETag);
    }

    /// <summary>By default only headers are transmitted; an explicit prefix/half-close exercises real truncated framing.</summary>
    internal async Task<RawReply> HeadersOnlyPostAsync(string path, string? token, string? key,
        long contentLength = 128, string? contentType = MediaType, string? origin = null,
        string? host = null, string? encoding = null, IEnumerable<KeyValuePair<string, string>>? additionalHeaders = null,
        string? cookie = null, byte[]? bodyPrefix = null, bool halfClose = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Origin.Port, deadline.Token);
        await using var stream = client.GetStream();
        var headers = new StringBuilder($"POST {path} HTTP/1.1\r\nHost: {host ?? Origin.Authority}\r\nConnection: close\r\nContent-Length: {contentLength.ToString(CultureInfo.InvariantCulture)}\r\n");
        // All tested routes are under the framework cookie's /api/v1 path. An oversized
        // raw target must reach Kestrel rather than fail in System.Uri before transmission.
        headers.Append("Cookie: ").Append(cookie ?? Cookies.GetCookieHeader(new Uri(Origin, "/api/v1"))).Append("\r\n");
        headers.Append("Origin: ").Append(origin ?? Origin.GetLeftPart(UriPartial.Authority)).Append("\r\n");
        if (token is not null) headers.Append("X-XSRF-TOKEN: ").Append(token).Append("\r\n");
        if (key is not null) headers.Append("Idempotency-Key: ").Append(key).Append("\r\n");
        if (contentType is not null) headers.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (encoding is not null) headers.Append("Content-Encoding: ").Append(encoding).Append("\r\n");
        if (additionalHeaders is not null)
            foreach (var header in additionalHeaders)
                headers.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), deadline.Token);
        if (bodyPrefix is not null) await stream.WriteAsync(bodyPrefix, deadline.Token);
        if (halfClose) client.Client.Shutdown(SocketShutdown.Send);
        return await ReadReplyAsync(stream, deadline.Token);
    }

    private static async Task<RawReply> ReadReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var first = Encoding.ASCII.GetString(await ReadLineAsync(stream, cancellationToken)).TrimEnd('\r', '\n').Split(' ', 3);
        Assert.StartsWith("HTTP/1.", first[0], StringComparison.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = Encoding.ASCII.GetString(await ReadLineAsync(stream, cancellationToken)).TrimEnd('\r', '\n');
            if (line.Length == 0) break;
            var separator = line.IndexOf(':');
            Assert.True(separator > 0);
            values.Add(line[..separator], line[(separator + 1)..].Trim(' ', '\t'));
        }
        using var body = new MemoryStream();
        if (values.TryGetValue("Content-Length", out var length))
            await ReadExactAsync(stream, int.Parse(length, CultureInfo.InvariantCulture), body, cancellationToken);
        else if (values.TryGetValue("Transfer-Encoding", out var transfer))
        {
            Assert.Equal("chunked", transfer);
            while (true)
            {
                var size = Encoding.ASCII.GetString(await ReadLineAsync(stream, cancellationToken)).TrimEnd('\r', '\n').Split(';', 2)[0];
                var count = int.Parse(size, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (count == 0) { Assert.Equal(new byte[] { 13, 10 }, await ReadLineAsync(stream, cancellationToken)); break; }
                await ReadExactAsync(stream, count, body, cancellationToken);
                Assert.Equal(new byte[] { 13, 10 }, await ReadLineAsync(stream, cancellationToken));
            }
        }
        return new((HttpStatusCode)int.Parse(first[1], CultureInfo.InvariantCulture), values, body.ToArray());
    }

    internal static void AssertRawError(RawReply reply, HttpStatusCode status, string code)
    {
        Assert.Equal(status, reply.Status);
        Assert.Contains("no-store", reply.Headers["Cache-Control"], StringComparison.Ordinal);
        Assert.False(reply.Headers.ContainsKey("Location"));
        Assert.False(reply.Headers.ContainsKey("ETag"));
        using var body = JsonDocument.Parse(reply.Body);
        Assert.Equal("code", Assert.Single(body.RootElement.EnumerateObject()).Name);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    /// <summary>Holds the real FIN client's receive side through observed cancellation and terminal encoded-output settlement.</summary>
    internal async Task<FinReply> TruncatedFinPostAsync(string path, string token, Guid key, RequestObservation observation,
        RevisionUploadTransportObserver transport)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Origin.Port, deadline.Token);
        await using var stream = client.GetStream();
        var selected = await transport.SelectAsync(((IPEndPoint)client.Client.LocalEndPoint!).Port, deadline.Token);
        var headers = $"POST {path} HTTP/1.1\r\nHost: {Origin.Authority}\r\nConnection: close\r\nContent-Length: 128\r\n" +
            $"Cookie: {Cookies.GetCookieHeader(new Uri(Origin, "/api/v1"))}\r\n" +
            $"Origin: {Origin.GetLeftPart(UriPartial.Authority)}\r\nX-XSRF-TOKEN: {token}\r\n" +
            $"Idempotency-Key: {key:D}\r\nContent-Type: {MediaType}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token);
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, deadline.Token);
        await observation.SuccessfulReadThreshold.Task.WaitAsync(deadline.Token);
        Assert.Equal(3L, observation.SuccessfulBytes);
        client.Client.Shutdown(SocketShutdown.Send);
        int? firstWireReadCount = null;
        long deliveredBytes = 0;
        FinTermination termination;
        try
        {
            firstWireReadCount = await stream.ReadAsync(new byte[1], deadline.Token);
            deliveredBytes += firstWireReadCount.Value;
            termination = firstWireReadCount == 0 ? FinTermination.EndOfStream : FinTermination.ResponseBytes;
        }
        catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            // The peer reset is distinct from a returned zero and never becomes a
            // fabricated EOF. Every unrelated read/socket failure still propagates.
            termination = FinTermination.PeerConnectionReset;
        }
        await observation.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await observation.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var settledDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (ServiceCalls != 1 || CompletedServiceCalls != 1)
                await Task.Delay(1, settledDeadline.Token);
        }
        // This is the completed HTTP connection pipeline, not an assumed
        // ConnectionClosed callback. No application bytes can be encoded later.
        await selected.PipelineSettled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return new(termination, firstWireReadCount, deliveredBytes, selected.EncodedBytes,
            selected.PipelineSettled.Task.IsCompletedSuccessfully);
    }

    internal enum FinTermination { EndOfStream, PeerConnectionReset, ResponseBytes }
    internal sealed record FinReply(FinTermination Termination, int? FirstWireReadCount, long DeliveredBytes,
        long EncodedBytes, bool ConnectionPipelineSettled);

    /// <summary>A valid observed chunk precedes an invalid size; the client send side stays open through the complete reply.</summary>
    internal async Task<RawReply> MalformedChunkPostAsync(string path, string token, Guid key, RequestObservation observation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Origin.Port, deadline.Token);
        await using var stream = client.GetStream();
        var headers = $"POST {path} HTTP/1.1\r\nHost: {Origin.Authority}\r\nConnection: close\r\nTransfer-Encoding: chunked\r\n" +
            $"Cookie: {Cookies.GetCookieHeader(new Uri(Origin, "/api/v1"))}\r\n" +
            $"Origin: {Origin.GetLeftPart(UriPartial.Authority)}\r\nX-XSRF-TOKEN: {token}\r\n" +
            $"Idempotency-Key: {key:D}\r\nContent-Type: {MediaType}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token);
        await stream.WriteAsync(new byte[] { (byte)'3', 13, 10, 1, 2, 3, 13, 10 }, deadline.Token);
        await observation.SuccessfulReadThreshold.Task.WaitAsync(deadline.Token);
        Assert.Equal(3L, observation.SuccessfulBytes);
        await stream.WriteAsync("G\r\n"u8.ToArray(), deadline.Token);
        // There is no FIN or shutdown before the entire actual HTTP response is read.
        return await ReadReplyAsync(stream, deadline.Token);
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one, cancellationToken) != 0)
        {
            line.WriteByte(one[0]);
            if (line.Length > 8192) throw new InvalidOperationException("The owned raw HTTP response line exceeded its bound.");
            if (line.Length >= 2 && one[0] == 10 && line.GetBuffer()[(int)line.Length - 2] == 13) return line.ToArray();
        }
        throw new IOException("The owned raw HTTP response ended before its line.");
    }

    private static async Task ReadExactAsync(Stream stream, int count, MemoryStream result, CancellationToken cancellationToken)
    {
        if (count < 0 || result.Length + count > 65_536) throw new InvalidOperationException("The owned raw HTTP response exceeded its bound.");
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        await result.WriteAsync(bytes, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Volatile.Read(ref bodyGate)?.Release.TrySetResult();
        Volatile.Read(ref requestObservation)?.Dispose();
        Client.Dispose();
        try { await app.DisposeAsync(); }
        finally { if (catalogDatabase is not null) await catalogDatabase.DisposeAsync(); }
    }

    internal sealed record RawReply(HttpStatusCode Status, IReadOnlyDictionary<string, string> Headers, byte[] Body);
    internal sealed class BodyGate
    {
        private int captured;
        internal bool TryCapture() => Interlocked.Exchange(ref captured, 1) == 0;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RequestEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class RequestObservation(long successfulReadThreshold) : IDisposable
    {
        private int captured;
        private long successfulBytes;
        private string? pipelineExceptionType;
        private CancellationToken requestAborted;
        private CancellationTokenRegistration abortRegistration;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SuccessfulReadThreshold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<RequestFinish> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<BodyReadFailure> BodyFailures { get; } = new();
        internal long SuccessfulBytes => Interlocked.Read(ref successfulBytes);
        internal bool CanObserveCancellation => requestAborted.CanBeCanceled;
        internal bool IsRequestAborted => requestAborted.IsCancellationRequested;
        internal bool IsCaptured => Volatile.Read(ref captured) != 0;
        internal bool TryCapture() => Interlocked.Exchange(ref captured, 1) == 0;
        internal void Capture(CancellationToken token)
        {
            requestAborted = token;
            // This registration remains alive through test observation, including an
            // asynchronously scheduled cancellation after middleware settlement.
            abortRegistration = token.Register(() => Aborted.TrySetResult());
            Entered.TrySetResult();
        }
        internal void RecordSuccessfulRead(int count)
        {
            if (Interlocked.Add(ref successfulBytes, count) >= successfulReadThreshold)
                SuccessfulReadThreshold.TrySetResult();
        }
        internal void RecordBodyFailure(Exception error) => BodyFailures.Enqueue(new(
            error.GetType().FullName ?? error.GetType().Name,
            error is BadHttpRequestException badRequest ? badRequest.StatusCode : null,
            requestAborted.IsCancellationRequested));
        internal void RecordPipelineException(Exception error) =>
            Volatile.Write(ref pipelineExceptionType, error.GetType().FullName ?? error.GetType().Name);
        internal void Finish(bool canceled, bool responseStarted, int responseStatus) =>
            Finished.TrySetResult(new(canceled, responseStarted, responseStatus, Volatile.Read(ref pipelineExceptionType)));
        public void Dispose() => abortRegistration.Dispose();
    }

    internal sealed record BodyReadFailure(string Type, int? StatusCode, bool RequestAbortedAtCatch);
    internal sealed record RequestFinish(bool RequestAbortedAtFinish, bool ResponseStarted, int ResponseStatus, string? PipelineExceptionType);

    /// <summary>A test-owned lazy adapter opens the real marked filesystem store only on real I/O.</summary>
    internal sealed class LazyPhysicalStore(PrivateStorageTestRoot root) : IPrivateObjectStore, IAsyncDisposable
    {
        private readonly object sync = new();
        private FileSystemPrivateObjectStore? store;
        private int opens;
        private int publications;
        private int reads;
        internal int Opens => Volatile.Read(ref opens);
        internal int Publications => Volatile.Read(ref publications);
        internal int Reads => Volatile.Read(ref reads);
        private FileSystemPrivateObjectStore Get()
        {
            lock (sync)
            {
                if (store is null)
                {
                    Interlocked.Increment(ref opens);
                    store = root.Open();
                }
                return store;
            }
        }
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref publications);
            return Get().PublishCreateOnlyAsync(scope, reference, source, cancellationToken);
        }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            return Get().ReadVerifiedAsync(scope, reference, cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            FileSystemPrivateObjectStore? current;
            lock (sync) current = store;
            if (current is not null) await current.DisposeAsync();
        }
    }

    private sealed class ObservedReader(ICatalogReader actual, RevisionUploadHttpTestSupport support) : ICatalogReader
    {
        public Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionAsync(ActorContext actor, Guid organizationId, CancellationToken cancellationToken = default) => actual.GetOrganizationAdmissionAsync(actor, organizationId, cancellationToken);
        public Task<CatalogProjectPage?> ListProjectsAsync(ActorContext actor, Guid organizationId, int limit, CatalogProjectCursor? cursor = null, CancellationToken cancellationToken = default) => actual.ListProjectsAsync(actor, organizationId, limit, cursor, cancellationToken);
        public async Task<CatalogProject?> GetProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default)
        {
            var result = await actual.GetProjectAsync(actor, scope, cancellationToken);
            Interlocked.Increment(ref support.resourceReads);
            support.Events.Enqueue("project-read-completed");
            return result;
        }
        public Task<CatalogMappingPage?> ListMappingsAsync(ActorContext actor, ProjectScope scope, int limit, CatalogMappingCursor? cursor = null, CancellationToken cancellationToken = default) => actual.ListMappingsAsync(actor, scope, limit, cursor, cancellationToken);
        public async Task<CatalogMapping?> GetMappingAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default)
        {
            var result = await actual.GetMappingAsync(actor, scope, cancellationToken);
            Interlocked.Increment(ref support.resourceReads);
            support.Events.Enqueue("mapping-read-completed");
            return result;
        }
        public Task<CatalogMappingMetadata?> GetMappingMetadataAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default) => actual.GetMappingMetadataAsync(actor, scope, cancellationToken);
        public Task<CatalogRevisionPage?> ListRevisionsAsync(ActorContext actor, MappingScope scope, int limit, CatalogRevisionCursor? cursor = null, CancellationToken cancellationToken = default) => actual.ListRevisionsAsync(actor, scope, limit, cursor, cancellationToken);
        public Task<CatalogRevision?> GetRevisionAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default) => actual.GetRevisionAsync(actor, scope, cancellationToken);
    }

    private sealed class ObservedService(IRevisionIngestionService actual, RevisionUploadHttpTestSupport support) : IRevisionIngestionService
    {
        public Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope scope, Guid key, CreateRevisionRequest request, Stream source, CancellationToken cancellationToken = default) => Observe(() => actual.CreateAsync(actor, scope, key, request, source, cancellationToken));
        public Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope scope, Guid key, AppendRevisionRequest request, Stream source, CancellationToken cancellationToken = default) => Observe(() => actual.AppendAsync(actor, scope, key, request, source, cancellationToken));
        public Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope scope, Guid operation, CancellationToken cancellationToken = default) => Observe(() => actual.ResumeAsync(actor, scope, operation, cancellationToken));
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope scope, Guid operation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref support.receiptCalls);
            return actual.GetOwnReceiptAsync(actor, scope, operation, cancellationToken);
        }
        private async Task<RevisionIngestionResult> Observe(Func<Task<RevisionIngestionResult>> call)
        {
            Interlocked.Increment(ref support.serviceCalls);
            support.Events.Enqueue("service-entered");
            try { return await call(); }
            finally { Interlocked.Increment(ref support.completedServiceCalls); support.Events.Enqueue("service-settled"); }
        }
    }

    private sealed class ObservedBody(Stream actual, RevisionUploadHttpTestSupport support, BodyGate? gate,
        RequestObservation? observation) : Stream
    {
        public override bool CanRead => actual.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref support.bodyReads);
            support.Events.Enqueue("body-read-entered");
            gate?.Entered.TrySetResult();
            if (gate is not null) await gate.Release.Task.WaitAsync(cancellationToken);
            int count;
            try { count = await actual.ReadAsync(buffer, cancellationToken); }
            catch (Exception error)
            {
                observation?.RecordBodyFailure(error);
                throw;
            }
            Interlocked.Add(ref support.bodyBytes, count);
            observation?.RecordSuccessfulRead(count);
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Upload qualification requires cancellable asynchronous body reads.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();
    }
}
