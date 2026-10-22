using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;

namespace Weft.MappingBackends.Workers;

/// <summary>Trusted host configuration for one profile of a Ferrule service, never supplied by a mapping or browser.</summary>
public sealed class FerruleServiceRunnerOptions
{
    public FerruleServiceRunnerOptions(IFerruleServiceConnector connector, string profile, string buildIdentity)
    {
        ArgumentNullException.ThrowIfNull(connector);
        Profile = FerruleServiceProtocol.Profile(profile) ??
            throw new ArgumentException("The Ferrule service profile must be inspection or preview.", nameof(profile));
        Connector = connector;
        ProfileName = profile;
        BuildIdentity = InspectionTransport.RequireImage(buildIdentity);
        ResponseBound = MaximumServiceAdmissionWait + Profile.MaximumWallTimeout * 2 + ProcessWorkerOptions.KillTimeout + TimeSpan.FromSeconds(5);
        SettlementBound = Profile.MaximumWallTimeout + ProcessWorkerOptions.KillTimeout + TimeSpan.FromSeconds(3);
    }

    /// <summary>The longest wait a service may queue an operation before it starts or refuses it.</summary>
    public static readonly TimeSpan MaximumServiceAdmissionWait = TimeSpan.FromSeconds(20);

    public IFerruleServiceConnector Connector { get; }
    public string ProfileName { get; }
    public DockerWorkerProfile Profile { get; }
    /// <summary>The exact worker build every answering instance must report.</summary>
    public string BuildIdentity { get; }

    /// <summary>Bounded body transfer, service admission, execution, child reaping, and transport margin.</summary>
    internal TimeSpan ResponseBound { get; init; }

    /// <summary>After a cancellation request: the wall deadline, child reaping, and margin.</summary>
    internal TimeSpan SettlementBound { get; init; }

    internal TimeSpan CancelRequestBound { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Operations sent for one call when refusals prove that nothing ran; each uses a fresh identity and channel.</summary>
    internal int Attempts { get; init; } = 3;

    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>
/// Sends exact frames to a Ferrule service through a connector. Each operation uses one fresh identity on one fresh
/// channel. A refusal that proves nothing ran (busy, stopping, unreachable before any frame byte) is retried with a new
/// operation, so another or a newly added replica can take it. Cancellation travels on the operation's own channel and
/// waits for the settled answer. An answer that cannot be confirmed after frame bytes may have been delivered is a
/// cleanup failure and closes further admission.
/// </summary>
public sealed class FerruleServiceRunner : IInspectionWorkerRunner, IAsyncDisposable
{
    private readonly FerruleServiceRunnerOptions options;
    private readonly object lifetimeGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? stopOperation;
    private int outstandingOperations;
    private int unavailable;
    private int lifetimeDisposed;
    private bool stopping;

    public FerruleServiceRunner(FerruleServiceRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    public string BuildIdentity => options.BuildIdentity;

    public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
    {
        if (requestFrame.Length > options.Profile.MaximumRequestBytes)
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
            var result = await RunAttemptsAsync(requestFrame, lifetime.Token).ConfigureAwait(false);
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

    private async Task<WorkerProcessResult> RunAttemptsAsync(ReadOnlyMemory<byte> frame, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (result, retryable) = await ExchangeAsync(frame, token).ConfigureAwait(false);
            if (!retryable || attempt >= options.Attempts || token.IsCancellationRequested) return result;
            try
            {
                await Task.Delay(options.RetryDelay * attempt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Nothing of this call ran on any instance.
                return Failed(WorkerProcessFailure.Canceled);
            }
        }
    }

    private async Task<(WorkerProcessResult Result, bool Retryable)> ExchangeAsync(ReadOnlyMemory<byte> frame, CancellationToken token)
    {
        var operation = Guid.NewGuid().ToString("N");
        var channel = options.Connector.Open();
        try
        {
            using var abort = new CancellationTokenSource();
            var content = new TransportFrameContent(frame);
            var exchange = SendAsync(channel, operation, content, abort.Token);
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
                // No frame byte was written, so no instance can have received this operation.
                await abort.CancelAsync().ConfigureAwait(false);
                var unsent = await SettleAsync(exchange, content, expired).ConfigureAwait(false);
                return unsent.Result.Failure == WorkerProcessFailure.SandboxUnavailable
                    ? (Failed(expired ? WorkerProcessFailure.TimedOut : WorkerProcessFailure.Canceled), false) : (unsent.Result, false);
            }

            // The instance cancels the operation, or refuses it if the cancellation arrives first, then answers the
            // original request once its child has settled.
            await CancelAsync(channel, operation).ConfigureAwait(false);
            using (var settlement = new CancellationTokenSource(options.SettlementBound))
            {
                var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (settlement.Token.Register(() => settled.TrySetResult()))
                {
                    if (await Task.WhenAny(exchange, settled.Task).ConfigureAwait(false) == exchange)
                        return ((await SettleAsync(exchange, content, expired).ConfigureAwait(false)).Result, false);
                }
            }

            await abort.CancelAsync().ConfigureAwait(false);
            await SettleAsync(exchange, content, expired).ConfigureAwait(false);
            return (Failed(WorkerProcessFailure.CleanupFailed), false);
        }
        finally
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<(WorkerProcessResult Result, bool Retryable)> SettleAsync(
        Task<(WorkerProcessResult Result, bool Retryable)> exchange, TransportFrameContent content, bool expired)
    {
        (WorkerProcessResult Result, bool Retryable) outcome;
        try
        {
            outcome = await exchange.ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException
            or AuthenticationException or InvalidOperationException or InvalidDataException)
        {
            // Before any frame byte was written, nothing could have executed. Afterwards the outcome is unknown.
            return content.Started ? (Failed(WorkerProcessFailure.CleanupFailed), false) : (Failed(WorkerProcessFailure.SandboxUnavailable), !expired);
        }

        return expired && outcome.Result.Failure == WorkerProcessFailure.Canceled ? (Failed(WorkerProcessFailure.TimedOut), false) : outcome;
    }

    private async Task<(WorkerProcessResult Result, bool Retryable)> SendAsync(IFerruleServiceChannel channel, string operation,
        TransportFrameContent content, CancellationToken abort)
    {
        using (var ready = new HttpRequestMessage(HttpMethod.Get, new Uri(FerruleServiceProtocol.ReadyPath, UriKind.Relative)))
        using (var admitted = await channel.SendAsync(ready, abort).ConfigureAwait(false))
        {
            if (admitted.StatusCode == HttpStatusCode.ServiceUnavailable)
                return (Failed(WorkerProcessFailure.SandboxUnavailable), true);
            if (admitted.StatusCode != HttpStatusCode.NoContent || !Single(admitted.Headers, InspectionTransport.ImageHeader, out var identity) ||
                !string.Equals(identity, options.BuildIdentity, StringComparison.Ordinal) || !FromService(admitted.Headers))
                return (Failed(WorkerProcessFailure.SandboxUnavailable), false);
        }

        // The frame travels on the same channel, which reaches the instance that just answered.
        var path = new Uri(FerruleServiceProtocol.OperationPath(options.ProfileName, operation), UriKind.Relative);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.TryAddWithoutValidation(InspectionTransport.ImageHeader, options.BuildIdentity);
        content.Headers.ContentType = new MediaTypeHeaderValue(InspectionTransport.MediaType);
        using var response = await channel.SendAsync(request, abort).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            // Only the service's own refusals prove that nothing ran; any other answer after frame bytes is uncertain.
            if (!FromService(response.Headers) && content.Started)
                throw new InvalidDataException("An answer did not come from the Ferrule service.");
            if (content.Started && response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound or
                HttpStatusCode.Conflict or HttpStatusCode.NotImplemented or HttpStatusCode.ServiceUnavailable))
                throw new InvalidDataException("The Ferrule service did not provide a settled operation outcome.");
            return (Failed(WorkerProcessFailure.SandboxUnavailable), response.StatusCode == HttpStatusCode.ServiceUnavailable);
        }

        if (!FromService(response.Headers) ||
            !Single(response.Headers, InspectionTransport.ResultHeader, out var code) ||
            !InspectionTransport.TryDecode(code, out var failure) ||
            !Single(response.Headers, InspectionTransport.ImageHeader, out var image) ||
            !string.Equals(image, options.BuildIdentity, StringComparison.Ordinal) ||
            response.Content.Headers.ContentEncoding.Count != 0 ||
            response.Content.Headers.ContentType?.MediaType != InspectionTransport.MediaType ||
            response.Content.Headers.ContentType.Parameters.Count != 0 ||
            response.Content.Headers.ContentLength is not { } length ||
            length < 0 || length > options.Profile.MaximumOutputBytes ||
            (failure != WorkerProcessFailure.None && length != 0))
            throw new InvalidDataException("The Ferrule service answer is not admissible.");

        var output = new byte[length];
        await using (var stream = await response.Content.ReadAsStreamAsync(abort).ConfigureAwait(false))
        {
            await stream.ReadExactlyAsync(output, abort).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], abort).ConfigureAwait(false) != 0)
                throw new InvalidDataException("The Ferrule service answer exceeds its declared length.");
        }

        return (new WorkerProcessResult(failure == WorkerProcessFailure.None ? output : [], failure), false);
    }

    private async Task CancelAsync(IFerruleServiceChannel channel, string operation)
    {
        try
        {
            using var bound = new CancellationTokenSource(options.CancelRequestBound);
            var path = new Uri(FerruleServiceProtocol.CancelPath(options.ProfileName, operation), UriKind.Relative);
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent([]) };
            using var response = await channel.SendAsync(request, bound.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException
            or AuthenticationException or InvalidOperationException)
        {
            // The original exchange remains the only source of a settled outcome.
        }
    }

    private static bool FromService(HttpResponseHeaders headers) =>
        Single(headers, FerruleServiceProtocol.ProtocolHeader, out var protocol) &&
        string.Equals(protocol, FerruleServiceProtocol.Version, StringComparison.Ordinal);

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
    /// Closes admission, cancels outstanding operations through the normal cancellation path and joins them.
    /// Unconfirmed remote settlement faults this task instead of reporting a clean stop.
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
            throw new InvalidOperationException("Ferrule service shutdown could not confirm remote settlement.");
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
}
