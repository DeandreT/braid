using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual Kestrel and catalog/editor PostgreSQL pools; admission setup remains explicitly synthetic C1.</summary>
internal sealed class MetadataHttpTestSupport : IAsyncDisposable
{
    internal const string MediaType = "application/vnd.braid.mapping-metadata-edit.v1+json";
    internal const int MaximumBodyBytes = 65_536;
    private static readonly IDataProtectionProvider TestKeys = new EphemeralDataProtectionProvider();
    private readonly WebApplication app;
    private readonly PostgresCatalogDatabase? readerDatabase;
    private readonly PostgresMappingMetadataDatabase? writerDatabase;
    private BodyGate? bodyGate;
    private int completedMappingReads;
    private int writerCalls;
    private int bodyReads;
    private long bodyBytes;
    internal HttpClient Client { get; }
    internal CookieContainer Cookies { get; } = new();
    internal Uri Origin { get; }
    internal int CompletedMappingReads => Volatile.Read(ref completedMappingReads);
    internal int WriterCalls => Volatile.Read(ref writerCalls);
    internal int BodyReads => Volatile.Read(ref bodyReads);
    internal long BodyBytes => Interlocked.Read(ref bodyBytes);
    internal ConcurrentQueue<string> Events { get; } = new();

    private MetadataHttpTestSupport(WebApplication app, Uri origin, PostgresCatalogDatabase? readerDatabase,
        PostgresMappingMetadataDatabase? writerDatabase)
    {
        this.app = app;
        Origin = origin;
        this.readerDatabase = readerDatabase;
        this.writerDatabase = writerDatabase;
        Client = NewClient(Cookies);
    }

    internal HttpClient NewClient(CookieContainer? cookies = null) => new(new HttpClientHandler
    {
        UseCookies = cookies is not null,
        CookieContainer = cookies ?? new CookieContainer(),
        UseProxy = false,
        AllowAutoRedirect = false
    })
    { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(20) };

    internal static async Task<MetadataHttpTestSupport> StartAsync(PostgresCase? test = null,
        bool editsEnabled = true, Guid? principal = null, Guid? organization = null, bool registerWriter = true)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}");
        WebApplication? app = null;
        PostgresCatalogDatabase? reader = null;
        PostgresMappingMetadataDatabase? writer = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, origin.Port));
            builder.Services.AddBraidApi();
            builder.Services.AddSingleton(TestKeys);
            var options = test is null ? new CatalogApiOptions() : new CatalogApiOptions(true, true,
                principal ?? Guid.Parse("33333333-3333-3333-3333-333333333333"),
                organization ?? Guid.Parse("11111111-1111-1111-1111-111111111111"), origin.GetLeftPart(UriPartial.Authority));
            builder.Services.AddBraidCatalog(options, builder.Environment, new CatalogMetadataEditOptions(test is not null && editsEnabled));
            MetadataHttpTestSupport? support = null;
            if (test is not null)
            {
                reader = new(test.CatalogConnectionString);
                var actualReader = new PostgresCatalogReader(reader);
                builder.Services.AddSingleton<ICatalogReader>(_ => new ObservedReader(actualReader, support!));
                if (editsEnabled && registerWriter)
                {
                    writer = new(test.MetadataConnectionString);
                    var actualWriter = new PostgresMappingMetadataWriter(writer);
                    builder.Services.AddSingleton<IMappingMetadataWriter>(_ => new ObservedWriter(actualWriter, support!));
                }
            }
            app = builder.Build();
            support = new(app, origin, reader, writer);
            app.Use(async (context, next) =>
            {
                var gate = Volatile.Read(ref support.bodyGate);
                if (context.Request.Method == "PATCH" && context.Request.Path.Value?.EndsWith("/metadata", StringComparison.Ordinal) == true)
                    context.Request.Body = new ObservedBody(context.Request.Body, support, gate);
                try { await next(context); }
                finally { gate?.RequestEnded.TrySetResult(); }
            });
            app.MapBraidCatalog();
            app.MapOpenApi();
            reservation.Stop();
            await app.StartAsync();
            Assert.Equal(origin.GetLeftPart(UriPartial.Authority), Assert.Single(app.Urls).TrimEnd('/'));
            return support;
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            if (writer is not null) await writer.DisposeAsync();
            if (reader is not null) await reader.DisposeAsync();
            throw;
        }
        finally { reservation.Stop(); }
    }

    internal void ResetObservations()
    {
        Volatile.Write(ref completedMappingReads, 0);
        Volatile.Write(ref writerCalls, 0);
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

    internal async Task<string> TokenAsync()
    {
        using var response = await Client.GetAsync("/api/v1/catalog/context");
        return (await CatalogHttpTestSupport.JsonAsync(response)).GetProperty("requestToken").GetString()!;
    }

    internal static string Path(RevisionScope scope) => CatalogHttpTestSupport.MappingPath(scope) + "/metadata";

    internal async Task<(string Tag, byte[] Bytes, JsonElement Metadata)> MetadataAsync(string path)
    {
        using var response = await Client.GetAsync(path);
        var metadata = await CatalogHttpTestSupport.JsonAsync(response);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag.IsWeak);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (response.Headers.ETag.ToString(), await response.Content.ReadAsByteArrayAsync(), metadata);
    }

    internal async Task<HttpResponseMessage> PatchAsync(string path, string? token, string? tag,
        byte[] body, string? contentType = MediaType, string? origin = null, string? contentEncoding = null,
        string? ifNoneMatch = null, bool chunked = false, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = chunked ? new UnknownLengthContent(body) : new ByteArrayContent(body)
        };
        if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (contentEncoding is not null) request.Content.Headers.TryAddWithoutValidation("Content-Encoding", contentEncoding);
        if (token is not null) request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token);
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        request.Headers.TryAddWithoutValidation("Origin", origin ?? Origin.GetLeftPart(UriPartial.Authority));
        return await (client ?? Client).SendAsync(request, cancellationToken);
    }

    internal static byte[] Edit(string name, string description) => JsonSerializer.SerializeToUtf8Bytes(new { name, description });

    internal static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var error = await CatalogHttpTestSupport.JsonAsync(response, status);
        Assert.Equal("code", Assert.Single(error.EnumerateObject()).Name);
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Null(response.Headers.ETag);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    /// <summary>Headers only: no declared body byte is sent before the complete real HTTP reply.</summary>
    internal async Task<RawReply> HeadersOnlyPatchAsync(string path, string? token, string? tag,
        long contentLength = 128, string? contentType = MediaType, string? origin = null,
        string? host = null, string? contentEncoding = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Origin.Port, deadline.Token);
        await using var stream = client.GetStream();
        var headers = new StringBuilder($"PATCH {path} HTTP/1.1\r\nHost: {host ?? Origin.Authority}\r\nConnection: close\r\nContent-Length: {contentLength.ToString(CultureInfo.InvariantCulture)}\r\n");
        headers.Append("Cookie: ").Append(Cookies.GetCookieHeader(new Uri(Origin, path))).Append("\r\n");
        headers.Append("Origin: ").Append(origin ?? Origin.GetLeftPart(UriPartial.Authority)).Append("\r\n");
        if (token is not null) headers.Append("X-XSRF-TOKEN: ").Append(token).Append("\r\n");
        if (tag is not null) headers.Append("If-Match: ").Append(tag).Append("\r\n");
        if (contentType is not null) headers.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (contentEncoding is not null) headers.Append("Content-Encoding: ").Append(contentEncoding).Append("\r\n");
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), deadline.Token);
        var first = Encoding.ASCII.GetString(await ReadLineAsync(stream, deadline.Token)).TrimEnd('\r', '\n').Split(' ', 3);
        Assert.StartsWith("HTTP/1.", first[0], StringComparison.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = Encoding.ASCII.GetString(await ReadLineAsync(stream, deadline.Token)).TrimEnd('\r', '\n');
            if (line.Length == 0) break;
            var separator = line.IndexOf(':');
            Assert.True(separator > 0);
            values.Add(line[..separator], line[(separator + 1)..].Trim(' ', '\t'));
        }
        using var body = new MemoryStream();
        if (values.TryGetValue("Content-Length", out var length))
            await ReadExactAsync(stream, int.Parse(length, CultureInfo.InvariantCulture), body, deadline.Token);
        else if (values.TryGetValue("Transfer-Encoding", out var transfer))
        {
            Assert.Equal("chunked", transfer);
            while (true)
            {
                var size = Encoding.ASCII.GetString(await ReadLineAsync(stream, deadline.Token)).TrimEnd('\r', '\n').Split(';', 2)[0];
                var count = int.Parse(size, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (count == 0) { Assert.Equal(new byte[] { 13, 10 }, await ReadLineAsync(stream, deadline.Token)); break; }
                await ReadExactAsync(stream, count, body, deadline.Token);
                Assert.Equal(new byte[] { 13, 10 }, await ReadLineAsync(stream, deadline.Token));
            }
        }
        return new((HttpStatusCode)int.Parse(first[1], CultureInfo.InvariantCulture), values, body.ToArray());
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
        Client.Dispose();
        try { await app.DisposeAsync(); }
        finally
        {
            try { if (writerDatabase is not null) await writerDatabase.DisposeAsync(); }
            finally { if (readerDatabase is not null) await readerDatabase.DisposeAsync(); }
        }
    }

    internal sealed record RawReply(HttpStatusCode Status, IReadOnlyDictionary<string, string> Headers, byte[] Body);
    internal sealed class BodyGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RequestEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ObservedReader(ICatalogReader actual, MetadataHttpTestSupport support) : ICatalogReader
    {
        public Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionAsync(ActorContext actor, Guid organizationId, CancellationToken cancellationToken = default) => actual.GetOrganizationAdmissionAsync(actor, organizationId, cancellationToken);
        public Task<CatalogProjectPage?> ListProjectsAsync(ActorContext actor, Guid organizationId, int limit, CatalogProjectCursor? cursor = null, CancellationToken cancellationToken = default) => actual.ListProjectsAsync(actor, organizationId, limit, cursor, cancellationToken);
        public Task<CatalogProject?> GetProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => actual.GetProjectAsync(actor, scope, cancellationToken);
        public Task<CatalogMappingPage?> ListMappingsAsync(ActorContext actor, ProjectScope scope, int limit, CatalogMappingCursor? cursor = null, CancellationToken cancellationToken = default) => actual.ListMappingsAsync(actor, scope, limit, cursor, cancellationToken);
        public async Task<CatalogMapping?> GetMappingAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default)
        {
            var mapping = await actual.GetMappingAsync(actor, scope, cancellationToken);
            Interlocked.Increment(ref support.completedMappingReads);
            support.Events.Enqueue("mapping-read-completed");
            return mapping;
        }
        public Task<CatalogMappingMetadata?> GetMappingMetadataAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default) => actual.GetMappingMetadataAsync(actor, scope, cancellationToken);
        public Task<CatalogRevisionPage?> ListRevisionsAsync(ActorContext actor, MappingScope scope, int limit, CatalogRevisionCursor? cursor = null, CancellationToken cancellationToken = default) => actual.ListRevisionsAsync(actor, scope, limit, cursor, cancellationToken);
        public Task<CatalogRevision?> GetRevisionAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default) => actual.GetRevisionAsync(actor, scope, cancellationToken);
    }

    private sealed class ObservedWriter(IMappingMetadataWriter actual, MetadataHttpTestSupport support) : IMappingMetadataWriter
    {
        public Task<MappingMetadataWriteResult> ReplaceAsync(ActorContext actor, MappingScope scope,
            MappingMetadataVersion expectedVersion, MappingMetadataEdit edit, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref support.writerCalls);
            support.Events.Enqueue("writer-entered");
            return actual.ReplaceAsync(actor, scope, expectedVersion, edit, cancellationToken);
        }
    }

    private sealed class ObservedBody(Stream actual, MetadataHttpTestSupport support, BodyGate? gate) : Stream
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
            var count = await actual.ReadAsync(buffer, cancellationToken);
            Interlocked.Add(ref support.bodyBytes, count);
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Metadata qualification requires cancellable asynchronous body reads.");
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
