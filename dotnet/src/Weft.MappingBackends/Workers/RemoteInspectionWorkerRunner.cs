using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Weft.MappingBackends.Workers;

/// <summary>
/// Forwards exact inspection frames to a separate pinned worker. Each operation uses one fresh connection and is
/// never retried. Cancellation asks the worker to cancel and waits for its settled answer. An answer that cannot
/// be confirmed after the frame may have been delivered is a cleanup failure and closes further admission, as
/// the local runner does for unconfirmed container cleanup.
/// </summary>
public sealed class RemoteInspectionWorkerRunner : IInspectionWorkerRunner, IAsyncDisposable
{
    private readonly RemoteInspectionWorkerOptions options;
    private readonly SslStreamCertificateContext clientContext;
    private readonly object lifetimeGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? stopOperation;
    private int outstandingOperations;
    private int unavailable;
    private int lifetimeDisposed;
    private bool stopping;

    public RemoteInspectionWorkerRunner(RemoteInspectionWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        // The worker pins this exact certificate; no issuer chain is fetched or sent.
        clientContext = SslStreamCertificateContext.Create(options.ClientCertificate, additionalCertificates: null, offline: true);
    }

    public string BuildIdentity => options.ImageId;

    public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
    {
        if (requestFrame.Length > InspectionTransport.MaximumFrameBytes)
            throw new ArgumentException("The request frame exceeds the fixed transport limit.", nameof(requestFrame));

        lock (lifetimeGate)
        {
            if (stopping) return Failed(WorkerProcessFailure.SandboxUnavailable);
            outstandingOperations++;
        }
        try
        {
            if (Volatile.Read(ref unavailable) != 0) return Failed(WorkerProcessFailure.SandboxUnavailable);
            if (cancellationToken.IsCancellationRequested) return Failed(WorkerProcessFailure.Canceled);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            var result = await ExchangeAsync(requestFrame, lifetime.Token).ConfigureAwait(false);
            if (result.Failure == WorkerProcessFailure.CleanupFailed) Interlocked.Exchange(ref unavailable, 1);
            return result;
        }
        catch
        {
            Interlocked.Exchange(ref unavailable, 1);
            throw;
        }
        finally
        {
            lock (lifetimeGate)
            {
                outstandingOperations--;
                if (stopping && outstandingOperations == 0) drained.TrySetResult();
            }
        }
    }

    private async Task<WorkerProcessResult> ExchangeAsync(ReadOnlyMemory<byte> frame, CancellationToken token)
    {
        var operation = Guid.NewGuid().ToString("N");
        using var abort = new CancellationTokenSource();
        var content = new FrameContent(frame);
        var exchange = SendAsync(operation, content, abort.Token);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var bound = new CancellationTokenSource(options.ResponseBound))
        using (bound.Token.Register(() => interrupted.TrySetResult()))
        using (token.Register(() => interrupted.TrySetResult()))
        {
            if (await Task.WhenAny(exchange, interrupted.Task).ConfigureAwait(false) == exchange)
                return await SettleAsync(exchange, content, expired: false).ConfigureAwait(false);
        }

        var expired = !token.IsCancellationRequested;
        if (!content.Started)
        {
            // No frame byte was written, so the worker cannot have received this operation.
            await abort.CancelAsync().ConfigureAwait(false);
            var unsent = await SettleAsync(exchange, content, expired).ConfigureAwait(false);
            return unsent.Failure == WorkerProcessFailure.SandboxUnavailable
                ? Failed(expired ? WorkerProcessFailure.TimedOut : WorkerProcessFailure.Canceled) : unsent;
        }

        // The worker cancels the operation, or refuses it if the cancellation arrives first, then answers
        // the original request once its own runner has settled.
        await CancelAsync(operation).ConfigureAwait(false);
        using (var settlement = new CancellationTokenSource(options.SettlementBound))
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (settlement.Token.Register(() => settled.TrySetResult()))
            {
                if (await Task.WhenAny(exchange, settled.Task).ConfigureAwait(false) == exchange)
                    return await SettleAsync(exchange, content, expired).ConfigureAwait(false);
            }
        }

        await abort.CancelAsync().ConfigureAwait(false);
        await SettleAsync(exchange, content, expired).ConfigureAwait(false);
        return Failed(WorkerProcessFailure.CleanupFailed);
    }

    private static async Task<WorkerProcessResult> SettleAsync(Task<WorkerProcessResult> exchange, FrameContent content, bool expired)
    {
        WorkerProcessResult result;
        try
        {
            result = await exchange.ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException
            or AuthenticationException or InvalidOperationException or InvalidDataException)
        {
            // Before any frame byte was written, nothing could have executed. Afterwards the outcome is unknown.
            return Failed(content.Started ? WorkerProcessFailure.CleanupFailed : WorkerProcessFailure.SandboxUnavailable);
        }

        return expired && result.Failure == WorkerProcessFailure.Canceled ? Failed(WorkerProcessFailure.TimedOut) : result;
    }

    private async Task<WorkerProcessResult> SendAsync(string operation, FrameContent content, CancellationToken abort)
    {
        using var client = CreateClient(reuse: true);
        using (var ready = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Endpoint, InspectionTransport.ReadyPath))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        })
        using (var admitted = await client.SendAsync(ready, HttpCompletionOption.ResponseHeadersRead, abort).ConfigureAwait(false))
        {
            if (admitted.StatusCode != HttpStatusCode.NoContent || !Single(admitted.Headers, InspectionTransport.ImageHeader, out var worker) ||
                !string.Equals(worker, options.ImageId, StringComparison.Ordinal))
                return Failed(WorkerProcessFailure.SandboxUnavailable);
        }

        // The frame travels on the same, now mutually authenticated connection; the guard forbids any other.
        using var request = Request(InspectionTransport.OperationsPath + operation, content);
        request.Headers.TryAddWithoutValidation(InspectionTransport.ImageHeader, options.ImageId);
        content.Headers.ContentType = new MediaTypeHeaderValue(InspectionTransport.MediaType);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort).ConfigureAwait(false);
        // Every non-success status is a refusal the worker returns before executing the frame.
        if (response.StatusCode != HttpStatusCode.OK)
            return Failed(WorkerProcessFailure.SandboxUnavailable);
        if (!Single(response.Headers, InspectionTransport.ResultHeader, out var code) ||
            !InspectionTransport.TryDecode(code, out var failure) ||
            !Single(response.Headers, InspectionTransport.ImageHeader, out var image) ||
            !string.Equals(image, options.ImageId, StringComparison.Ordinal) ||
            response.Content.Headers.ContentEncoding.Count != 0 ||
            response.Content.Headers.ContentType?.MediaType != InspectionTransport.MediaType ||
            response.Content.Headers.ContentType.Parameters.Count != 0 ||
            response.Content.Headers.ContentLength is not { } length ||
            length < 0 || length > InspectionTransport.MaximumOutputBytes ||
            (failure != WorkerProcessFailure.None && length != 0))
            throw new InvalidDataException("The inspection worker answer is not admissible.");

        var output = new byte[length];
        await using (var stream = await response.Content.ReadAsStreamAsync(abort).ConfigureAwait(false))
        {
            await stream.ReadExactlyAsync(output, abort).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], abort).ConfigureAwait(false) != 0)
                throw new InvalidDataException("The inspection worker answer exceeds its declared length.");
        }

        return new WorkerProcessResult(failure == WorkerProcessFailure.None ? output : [], failure);
    }

    private async Task CancelAsync(string operation)
    {
        try
        {
            using var bound = new CancellationTokenSource(options.CancelRequestBound);
            using var client = CreateClient(reuse: false);
            using var request = Request(InspectionTransport.OperationsPath + operation + InspectionTransport.CancelSuffix,
                new ByteArrayContent([]));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bound.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException
            or AuthenticationException or InvalidOperationException)
        {
            // The original exchange remains the only source of a settled outcome.
        }
    }

    private HttpRequestMessage Request(string path, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Endpoint, path))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = content
        };
        request.Headers.ConnectionClose = true;
        return request;
    }

    private HttpClient CreateClient(bool reuse)
    {
        var guard = new SingleConnection(options.Endpoint);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = null,
            PreAuthenticate = false,
            PooledConnectionLifetime = reuse ? Timeout.InfiniteTimeSpan : TimeSpan.Zero,
            PooledConnectionIdleTimeout = reuse ? options.ResponseBound : TimeSpan.Zero,
            ConnectTimeout = options.ConnectTimeout,
            MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = 16,
            MaxResponseDrainSize = 0,
            ResponseDrainTimeout = TimeSpan.Zero,
            ActivityHeadersPropagator = null,
            ConnectCallback = guard.ConnectAsync,
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificateContext = clientContext,
                // Only the pinned worker certificate is admitted; neither trust stores nor host names select it.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    InspectionTransport.HasDigest(certificate, options.WorkerCertificateSha256),
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static bool Single(HttpResponseHeaders headers, string name, out string? value)
    {
        value = null;
        if (!headers.TryGetValues(name, out var values)) return false;
        var items = values.ToArray();
        if (items.Length != 1) return false;
        value = items[0];
        return true;
    }

    /// <summary>
    /// Closes admission, cancels queued and active operations through the normal cancellation path and joins
    /// them. Unconfirmed remote settlement faults this task instead of reporting a clean stop.
    /// </summary>
    public Task StopAsync()
    {
        lock (lifetimeGate)
        {
            if (stopOperation is not null) return stopOperation;
            stopping = true;
            if (outstandingOperations == 0) drained.TrySetResult();
            return stopOperation = StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        try { await shutdown.CancelAsync().ConfigureAwait(false); }
        finally { await drained.Task.ConfigureAwait(false); }
        if (Volatile.Read(ref unavailable) != 0)
            throw new InvalidOperationException("Inspection transport shutdown could not confirm remote settlement.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            if (Interlocked.Exchange(ref lifetimeDisposed, 1) == 0) shutdown.Dispose();
        }
    }

    private static WorkerProcessResult Failed(WorkerProcessFailure failure) => new([], failure);

    /// <summary>Records whether any frame byte may have reached the connection.</summary>
    private sealed class FrameContent(ReadOnlyMemory<byte> frame) : HttpContent
    {
        private int started;
        internal bool Started => Volatile.Read(ref started) != 0;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref started, 1);
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = frame.Length;
            return true;
        }
    }

    /// <summary>One connection per client: an internal handler retry cannot deliver the frame a second time.</summary>
    private sealed class SingleConnection(Uri endpoint)
    {
        private int reserved;

        internal async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref reserved, 1) != 0 ||
                !string.Equals(context.DnsEndPoint.Host.Trim('[', ']'), endpoint.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ||
                context.DnsEndPoint.Port != endpoint.Port)
                throw new HttpRequestException("The inspection worker connection was refused.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
