using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// Actual HTTP/1 loopback Kestrel and separate restricted creator/catalog pools.
/// Supplied application ports are caller-owned; synthetic boundary ports do not
/// qualify persistence or native validation. This helper never seeds projects.
/// </summary>
internal sealed class ProjectCreationHttpTestSupport : IAsyncDisposable
{
    internal const string MediaType = "application/vnd.weft.project-create.v1+json";
    internal const int MaximumBodyBytes = 65_536;
    private static readonly IDataProtectionProvider TestKeys = new EphemeralDataProtectionProvider();
    private readonly WebApplication app;
    private readonly StartConfiguration configuration;
    private readonly ConcurrentQueue<BodyGate> pendingBodyGates = new();
    private readonly ConcurrentBag<BodyGate> armedBodyGates = [];
    private int admissionReads;
    private int completedAdmissionReads;
    private int creatorCalls;
    private int completedCreatorCalls;
    private int creatorResolutions;
    private int bodyReads;
    private long bodyBytes;
    private CreationCall? lastCreation;
    private int disposed;

    internal HttpClient Client { get; }
    internal CookieContainer Cookies { get; } = new();
    internal Uri Origin { get; }
    internal ActorContext Actor { get; }
    internal Guid OrganizationId { get; }
    internal PostgresProjectCreationDatabase? CreationDatabase { get; }
    internal PostgresCatalogDatabase? CatalogDatabase { get; }
    internal int AdmissionReads => Volatile.Read(ref admissionReads);
    internal int CompletedAdmissionReads => Volatile.Read(ref completedAdmissionReads);
    internal int CreatorCalls => Volatile.Read(ref creatorCalls);
    internal int CompletedCreatorCalls => Volatile.Read(ref completedCreatorCalls);
    internal int CreatorResolutions => Volatile.Read(ref creatorResolutions);
    internal int BodyReads => Volatile.Read(ref bodyReads);
    internal long BodyBytes => Interlocked.Read(ref bodyBytes);
    internal CreationCall? LastCreation => Volatile.Read(ref lastCreation);
    internal ConcurrentQueue<string> Events { get; } = new();

    private ProjectCreationHttpTestSupport(WebApplication app, Uri origin, StartConfiguration configuration,
        ActorContext actor, Guid organizationId, PostgresProjectCreationDatabase? creationDatabase,
        PostgresCatalogDatabase? catalogDatabase)
    {
        this.app = app;
        this.configuration = configuration;
        Origin = origin;
        Actor = actor;
        OrganizationId = organizationId;
        CreationDatabase = creationDatabase;
        CatalogDatabase = catalogDatabase;
        Client = NewClient(Cookies);
    }

    internal HttpClient NewClient(CookieContainer? cookies = null) => new(new HttpClientHandler
    {
        UseCookies = cookies is not null,
        CookieContainer = cookies ?? new CookieContainer(),
        UseProxy = false,
        AllowAutoRedirect = false
    })
    {
        BaseAddress = Origin,
        Timeout = TimeSpan.FromSeconds(45),
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
    };

    internal static Task<ProjectCreationHttpTestSupport> StartAsync(ProjectCreationTestSupport? test = null,
        bool enabled = true, int maximumConcurrentOperations = 2, Guid? principal = null,
        Guid? organization = null, IProjectCreator? creator = null,
        IOrganizationProjectCreationReader? admissionReader = null, ICatalogReader? catalogReader = null,
        bool registerCreator = true, IRevisionIngestionService? ingestionService = null,
        IProjectReader? ingestionProjectReader = null,
        RevisionUploadTransportObserver? transportObserver = null) => StartConfiguredAsync(new(test, enabled,
            maximumConcurrentOperations, principal, organization, creator, admissionReader, catalogReader,
            registerCreator, ingestionService, ingestionProjectReader, transportObserver));

    private static async Task<ProjectCreationHttpTestSupport> StartConfiguredAsync(StartConfiguration configuration)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}");
        WebApplication? app = null;
        PostgresProjectCreationDatabase? creationDatabase = null;
        PostgresCatalogDatabase? catalogDatabase = null;
        try
        {
            var actor = new ActorContext(new PrincipalId(configuration.Principal ?? configuration.Test?.Seed.Alice ??
                Guid.Parse("33333333-3333-3333-3333-333333333333")));
            var organization = configuration.Organization ?? configuration.Test?.OrganizationId ??
                Guid.Parse("11111111-1111-1111-1111-111111111111");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Limits.MaxRequestLineSize = 65_536;
                server.Listen(IPAddress.Loopback, origin.Port, listener =>
                {
                    listener.Protocols = HttpProtocols.Http1;
                    configuration.TransportObserver?.Configure(listener);
                });
            });
            builder.Services.AddWeftApi();
            builder.Services.AddSingleton(TestKeys);
            builder.Services.AddWeftCatalog(new CatalogApiOptions(true, true, actor.PrincipalId.Value,
                    organization, origin.GetLeftPart(UriPartial.Authority)), builder.Environment,
                new CatalogMetadataEditOptions(),
                new CatalogRevisionIngestionOptions(configuration.IngestionService is not null),
                new CatalogProjectCreationOptions(configuration.Enabled, configuration.MaximumConcurrentOperations));

            var creator = configuration.Creator;
            var admissionReader = configuration.AdmissionReader;
            var catalogReader = configuration.CatalogReader;
            if (configuration.Test is not null)
            {
                if (catalogReader is null)
                {
                    catalogDatabase = new(configuration.Test.Case.CatalogConnectionString);
                    catalogReader = new PostgresCatalogReader(catalogDatabase);
                }
                if (admissionReader is null || (configuration.RegisterCreator && creator is null))
                {
                    creationDatabase = new(configuration.Test.Case.ProjectCreationConnectionString);
                    var actualCreator = new PostgresProjectCreator(creationDatabase);
                    admissionReader ??= actualCreator;
                    creator ??= actualCreator;
                }
            }
            ProjectCreationHttpTestSupport? support = null;
            if (catalogReader is not null) builder.Services.AddSingleton(catalogReader);
            if (admissionReader is not null)
                builder.Services.AddSingleton<IOrganizationProjectCreationReader>(_ =>
                    new ObservedAdmissionReader(admissionReader, support!));
            if (configuration.RegisterCreator && creator is not null)
                builder.Services.AddSingleton<IProjectCreator>(_ =>
                {
                    Interlocked.Increment(ref support!.creatorResolutions);
                    return new ObservedCreator(creator, support!);
                });
            if (configuration.IngestionService is not null)
            {
                if (configuration.IngestionProjectReader is null)
                    throw new ArgumentException("Upload qualification requires an explicit project reader.", nameof(configuration));
                builder.Services.AddSingleton(configuration.IngestionService);
                builder.Services.AddSingleton(configuration.IngestionProjectReader);
            }
            app = builder.Build();
            support = new(app, origin, configuration, actor, organization, creationDatabase, catalogDatabase);
            app.Use(async (context, next) =>
            {
                BodyGate? gate = null;
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    support.pendingBodyGates.TryDequeue(out gate);
                    context.Request.Body = new ObservedBody(context.Request.Body, support, gate);
                }
                gate?.Capture(context.RequestAborted);
                try { await next(context); }
                catch (Exception error)
                {
                    gate?.RecordPipelineFailure(error);
                    throw;
                }
                finally
                {
                    if (gate is not null)
                    {
                        gate.AbortedAtFinish = context.RequestAborted.IsCancellationRequested;
                        gate.RecordFinish(new(gate.AbortedAtFinish, context.Response.HasStarted,
                            context.Response.StatusCode, context.Response.ContentLength,
                            gate.SuccessfulBytes, gate.PipelineException));
                        gate.RequestEnded.TrySetResult();
                    }
                }
            });
            app.MapWeftCatalog();
            app.MapOpenApi();
            reservation.Stop();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StartAsync(deadline.Token);
            Assert.Equal(origin.GetLeftPart(UriPartial.Authority), Assert.Single(app.Urls).TrimEnd('/'));
            return support;
        }
        catch
        {
            try { if (app is not null) await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); }
            finally
            {
                try { if (creationDatabase is not null) await creationDatabase.DisposeAsync(); }
                finally { if (catalogDatabase is not null) await catalogDatabase.DisposeAsync(); }
            }
            throw;
        }
        finally { reservation.Stop(); }
    }

    internal static string CreatePath(Guid organizationId) => $"/api/v1/organizations/{organizationId:D}/projects";

    internal static byte[] Body(string name, string description) =>
        JsonSerializer.SerializeToUtf8Bytes(new { name, description });

    internal async Task<JsonElement> ContextAsync()
    {
        using var response = await Client.GetAsync("/api/v1/catalog/context");
        return await JsonAsync(response);
    }

    internal async Task<string> TokenAsync() => (await ContextAsync()).GetProperty("requestToken").GetString()!;

    internal static Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK) =>
        CatalogHttpTestSupport.JsonAsync(response, status);

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

    /// <summary>
    /// Send a small real prefix while withholding the rest of the declared HTTP
    /// entity. The caller waits for body-gate entry, then resets this owned socket
    /// and observes Kestrel's actual RequestAborted rather than a client task.
    /// </summary>
    internal async Task<WithheldBodyRequest> StartWithheldBodyPostAsync(string path, string token, Guid key,
        byte[]? prefix = null, int declaredContentLength = 128)
    {
        if (path != CreatePath(OrganizationId))
            throw new ArgumentException("The abort probe requires this host's canonical organization path.", nameof(path));
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length > 4096 || token.Any(character => character is '\r' or '\n' || character > 127))
            throw new ArgumentException("The abort probe requires a bounded framework request token.", nameof(token));
        if (key == Guid.Empty) throw new ArgumentException("The abort probe requires a nonzero key.", nameof(key));
        var bodyPrefix = prefix?.ToArray() ?? Encoding.UTF8.GetBytes("{\"n");
        if (bodyPrefix.Length is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(prefix));
        if (declaredContentLength <= bodyPrefix.Length || declaredContentLength > MaximumBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(declaredContentLength));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var socket = new TcpClient { NoDelay = true };
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, Origin.Port, deadline.Token);
            var stream = socket.GetStream();
            var transport = configuration.TransportObserver is null ? null :
                await configuration.TransportObserver.SelectAsync(
                    ((IPEndPoint)socket.Client.LocalEndPoint!).Port, deadline.Token);
            var headers = $"POST {path} HTTP/1.1\r\n" +
                $"Host: {Origin.Authority}\r\n" +
                $"Origin: {Origin.GetLeftPart(UriPartial.Authority)}\r\n" +
                $"Cookie: {Cookies.GetCookieHeader(new Uri(Origin, path))}\r\n" +
                $"X-XSRF-TOKEN: {token}\r\n" +
                $"Idempotency-Key: {key:D}\r\n" +
                $"Content-Type: {MediaType}\r\n" +
                $"Content-Length: {declaredContentLength.ToString(CultureInfo.InvariantCulture)}\r\n" +
                "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token);
            await stream.WriteAsync(bodyPrefix, deadline.Token);
            return new WithheldBodyRequest(socket, transport);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal BodyGate GateBody() => GateBodies(1)[0];

    /// <summary>Observe one real POST's abort and completion without suspending its body reads.</summary>
    internal BodyGate ObserveNextPost()
    {
        var observation = GateBody();
        observation.Release.TrySetResult();
        return observation;
    }

    internal BodyGate[] GateBodies(int count)
    {
        if (count is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(count));
        var gates = Enumerable.Range(0, count).Select(_ => new BodyGate()).ToArray();
        foreach (var gate in gates)
        {
            armedBodyGates.Add(gate);
            pendingBodyGates.Enqueue(gate);
        }
        return gates;
    }

    internal void ClearBodyGate()
    {
        while (pendingBodyGates.TryDequeue(out var gate)) gate.Release.TrySetResult();
    }

    internal void ResetObservations()
    {
        Volatile.Write(ref admissionReads, 0);
        Volatile.Write(ref completedAdmissionReads, 0);
        Volatile.Write(ref creatorCalls, 0);
        Volatile.Write(ref completedCreatorCalls, 0);
        Volatile.Write(ref bodyReads, 0);
        Interlocked.Exchange(ref bodyBytes, 0);
        Volatile.Write(ref lastCreation, null);
        Events.Clear();
    }

    /// <summary>Real max-one pools can be borrowed while body I/O is held, with no leaked organization binding.</summary>
    internal async Task AssertNoHeldTransactionsAsync()
    {
        var creator = CreationDatabase ?? throw new InvalidOperationException("The creator observer requires a real pool.");
        var catalog = CatalogDatabase ?? throw new InvalidOperationException("The catalog observer requires a real pool.");
        await AssertIdleAsync(creator.DataSource);
        await AssertIdleAsync(catalog.DataSource);

        static async Task AssertIdleAsync(NpgsqlDataSource source)
        {
            Assert.Equal(1, new NpgsqlConnectionStringBuilder(source.ConnectionString).MaxPoolSize);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var connection = await source.OpenConnectionAsync(deadline.Token);
            await using var command = new NpgsqlCommand("""
                SELECT current_setting('weft.organization_id',true),
                    (SELECT count(*) FROM pg_stat_activity WHERE datname=current_database()
                     AND usename=current_user AND pid<>pg_backend_pid() AND xact_start IS NOT NULL)
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(deadline.Token);
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.True(reader.IsDBNull(0) || reader.GetString(0).Length == 0);
            Assert.Equal(0L, reader.GetInt64(1));
        }
    }

    internal async Task<ProjectCreationHttpTestSupport> RestartAsync()
    {
        await DisposeAsync();
        return await StartConfiguredAsync(configuration);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var gate in armedBodyGates) gate.Release.TrySetResult();
        Client.Dispose();
        try { await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); }
        finally
        {
            try
            {
                try { if (CreationDatabase is not null) await CreationDatabase.DisposeAsync(); }
                finally { if (CatalogDatabase is not null) await CatalogDatabase.DisposeAsync(); }
            }
            finally { foreach (var gate in armedBodyGates) gate.Dispose(); }
        }
    }

    internal sealed record CreationCall(ActorContext Actor, Guid OrganizationId, Guid IdempotencyKey,
        ProjectCreationMetadata Metadata);

    internal sealed class WithheldBodyRequest(TcpClient ownedSocket,
        RevisionUploadTransportObserver.ObservedConnection? observedTransport) : IDisposable
    {
        private TcpClient? socket = ownedSocket;
        internal RevisionUploadTransportObserver.ObservedConnection? Transport { get; } = observedTransport;

        internal async Task<TransportSettlement> WaitForTransportSettlementAsync(CancellationToken cancellationToken)
        {
            var observed = Transport ?? throw new InvalidOperationException("This probe requires explicit transport observation.");
            await observed.PipelineSettled.Task.WaitAsync(cancellationToken);
            // The encoded byte count comes from the settled server pipeline.
            // Disposing our own RST socket leaves peer delivery unobservable.
            return new(observed.EncodedBytes, observed.PipelineSettled.Task.IsCompletedSuccessfully, null);
        }

        internal void Abort()
        {
            var owned = Interlocked.Exchange(ref socket, null);
            if (owned is null) return;
            var rawSocket = owned.Client;
            try { rawSocket.Close(0); }
            finally { owned.Dispose(); }
        }

        public void Dispose() => Abort();
    }

    internal sealed class BodyGate : IDisposable
    {
        private readonly object lifetime = new();
        private CancellationTokenRegistration abortRegistration;
        private bool disposed;
        private long successfulBytes;
        private int innerReadEntries;
        private CancellationToken requestAborted;
        private Exception? pipelineException;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RequestEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RequestAborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> FirstReadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondReadPending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<RequestFinish> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<BodyReadFailure> BodyFailures { get; } = new();
        internal TaskCompletionSource Aborted => RequestAborted;
        internal bool AbortedAtFinish { get; set; }
        internal long SuccessfulBytes => Interlocked.Read(ref successfulBytes);
        internal int InnerReadEntries => Volatile.Read(ref innerReadEntries);
        internal Exception? PipelineException => Volatile.Read(ref pipelineException);

        internal void Capture(CancellationToken token)
        {
            lock (lifetime)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                requestAborted = token;
                // Retain the real registration through the test's final signal
                // audit, including cancellation scheduled after middleware ends.
                abortRegistration = token.Register(() => RequestAborted.TrySetResult());
            }
        }

        internal int RecordReadStarted(bool alreadyCompleted)
        {
            var ordinal = Interlocked.Increment(ref innerReadEntries);
            if (ordinal == 2 && !alreadyCompleted) SecondReadPending.TrySetResult();
            return ordinal;
        }

        internal void RecordReadCompleted(int ordinal, int count)
        {
            Interlocked.Add(ref successfulBytes, count);
            if (ordinal == 1) FirstReadCompleted.TrySetResult(count);
        }

        internal void RecordBodyFailure(Exception error) => BodyFailures.Enqueue(new(error,
            error.GetType().FullName ?? error.GetType().Name,
            error is Microsoft.AspNetCore.Http.BadHttpRequestException badRequest ? badRequest.StatusCode : null,
            requestAborted.IsCancellationRequested, SuccessfulBytes, InnerReadEntries));

        internal void RecordPipelineFailure(Exception error) => Volatile.Write(ref pipelineException, error);

        internal void RecordFinish(RequestFinish finish) => Finished.TrySetResult(finish);

        internal string DiagnosticSnapshot()
        {
            var finish = Finished.Task.IsCompletedSuccessfully ? Finished.Task.Result : null;
            return JsonSerializer.Serialize(new BodyGateDiagnostic(SuccessfulBytes, InnerReadEntries,
                FirstReadCompleted.Task.IsCompletedSuccessfully ? FirstReadCompleted.Task.Result : null,
                SecondReadPending.Task.IsCompletedSuccessfully, RequestAborted.Task.IsCompletedSuccessfully,
                requestAborted.IsCancellationRequested, RequestEnded.Task.IsCompletedSuccessfully,
                finish is null ? null : new RequestFinishDiagnostic(finish.RequestAbortedAtFinish,
                    finish.ResponseHasStarted, finish.ResponseStatus, finish.ResponseContentLength,
                    finish.SuccessfulRequestBytes, Categories(finish.PipelineException)),
                BodyFailures.Count, BodyFailures.Take(8).Select(failure => new BodyFailureDiagnostic(
                    Categories(failure.Exception), failure.RequestAbortedAtCatch,
                    failure.SuccessfulRequestBytes, failure.InnerReadEntries)).ToArray()));
        }

        public void Dispose()
        {
            CancellationTokenRegistration registration;
            lock (lifetime)
            {
                if (disposed) return;
                disposed = true;
                registration = abortRegistration;
            }
            registration.Dispose();
        }
    }

    private sealed class ObservedBody(Stream inner, ProjectCreationHttpTestSupport support, BodyGate? gate) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("HTTP qualification requires cancellable asynchronous body reads.");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref support.bodyReads);
            support.Events.Enqueue("body-read-entered");
            if (gate is not null)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            try
            {
                var read = inner.ReadAsync(buffer, cancellationToken);
                // Only the actual underlying ValueTask establishes pending I/O;
                // entering this wrapper or incrementing a counter does not.
                var ordinal = gate?.RecordReadStarted(read.IsCompleted) ?? 0;
                var count = await read;
                Interlocked.Add(ref support.bodyBytes, count);
                gate?.RecordReadCompleted(ordinal, count);
                return count;
            }
            catch (Exception error)
            {
                gate?.RecordBodyFailure(error);
                throw;
            }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class ObservedAdmissionReader(IOrganizationProjectCreationReader inner,
        ProjectCreationHttpTestSupport support) : IOrganizationProjectCreationReader
    {
        public async Task<OrganizationProjectCreationAdmission?> GetAdmissionAsync(ActorContext actor,
            Guid organizationId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref support.admissionReads);
            support.Events.Enqueue("admission-read-entered");
            var admission = await inner.GetAdmissionAsync(actor, organizationId, cancellationToken);
            Interlocked.Increment(ref support.completedAdmissionReads);
            support.Events.Enqueue("admission-read-completed");
            return admission;
        }
    }

    private sealed class ObservedCreator(IProjectCreator inner, ProjectCreationHttpTestSupport support) : IProjectCreator
    {
        public async Task<ProjectCreationResult> CreateAsync(ActorContext actor, Guid organizationId,
            Guid idempotencyKey, ProjectCreationMetadata metadata, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref support.creatorCalls);
            Volatile.Write(ref support.lastCreation, new(actor, organizationId, idempotencyKey, metadata));
            support.Events.Enqueue("creator-call-entered");
            try { return await inner.CreateAsync(actor, organizationId, idempotencyKey, metadata, cancellationToken); }
            finally
            {
                Interlocked.Increment(ref support.completedCreatorCalls);
                support.Events.Enqueue("creator-call-settled");
            }
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();
    }

    private sealed record StartConfiguration(ProjectCreationTestSupport? Test, bool Enabled,
        int MaximumConcurrentOperations, Guid? Principal, Guid? Organization, IProjectCreator? Creator,
        IOrganizationProjectCreationReader? AdmissionReader, ICatalogReader? CatalogReader, bool RegisterCreator,
        IRevisionIngestionService? IngestionService, IProjectReader? IngestionProjectReader,
        RevisionUploadTransportObserver? TransportObserver);

    internal sealed record TransportSettlement(long EncodedBytes, bool ConnectionPipelineSettled,
        long? DeliveredBytes);
    internal sealed record BodyReadFailure(Exception Exception, string Type, int? BadHttpStatus,
        bool RequestAbortedAtCatch, long SuccessfulRequestBytes, int InnerReadEntries);
    internal sealed record RequestFinish(bool RequestAbortedAtFinish, bool ResponseHasStarted,
        int ResponseStatus, long? ResponseContentLength, long SuccessfulRequestBytes,
        Exception? PipelineException);
    private sealed record ExceptionCategory(string Type, int? BadHttpStatus, string? SocketErrorCode);
    private sealed record BodyFailureDiagnostic(ExceptionCategory[] ExceptionChain, bool RequestAbortedAtCatch,
        long SuccessfulRequestBytes, int InnerReadEntries);
    private sealed record RequestFinishDiagnostic(bool RequestAbortedAtFinish, bool ResponseHasStarted,
        int ResponseStatus, long? ResponseContentLength, long SuccessfulRequestBytes,
        ExceptionCategory[] PipelineExceptionChain);
    private sealed record BodyGateDiagnostic(long SuccessfulBytes, int InnerReadEntries, int? FirstReadCount,
        bool SecondReadPendingObserved, bool CallbackObserved, bool RequestAbortedNow, bool RequestEnded,
        RequestFinishDiagnostic? Finish, int BodyFailureCount, BodyFailureDiagnostic[] BodyFailures);

    private static ExceptionCategory[] Categories(Exception? error)
    {
        var categories = new List<ExceptionCategory>();
        for (var current = error; current is not null && categories.Count < 8; current = current.InnerException)
            categories.Add(new(current.GetType().FullName ?? current.GetType().Name,
                current is Microsoft.AspNetCore.Http.BadHttpRequestException badRequest ? badRequest.StatusCode : null,
                current is SocketException socketError ? socketError.SocketErrorCode.ToString() : null));
        return categories.ToArray();
    }
}
