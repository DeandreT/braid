using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using Weft.Application;

namespace Weft.MappingBackends.Workers;

/// <summary>Trusted host configuration for one profile of a Ferrule service, never supplied by a mapping or browser.</summary>
public sealed class FerruleServiceRunnerOptions
{
    public FerruleServiceRunnerOptions(IFerruleServiceConnector connector, string profile, string buildIdentity,
        string? compileToolchainIdentity = null, string? compiledRuntimeIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        Profile = FerruleServiceProtocol.Profile(profile) ??
            throw new ArgumentException("The Ferrule service profile must be inspection, edit, preview, interchange, compile or execute.", nameof(profile));
        Connector = connector;
        ProfileName = profile;
        BuildIdentity = InspectionTransport.RequireImage(buildIdentity);
        if (compileToolchainIdentity is not null)
        {
            CompileToolchainIdentity = InspectionTransport.RequireImage(compileToolchainIdentity);
            if (profile == FerruleServiceProtocol.CompileProfile)
                CompilationBuildIdentity = CompileWorkerIdentity.Combine(BuildIdentity, CompileToolchainIdentity);
        }
        else if (profile == FerruleServiceProtocol.CompileProfile)
            throw new ArgumentException("Compilation requires an explicitly pinned toolchain identity.", nameof(compileToolchainIdentity));
        if (profile == FerruleServiceProtocol.CompiledProfile)
        {
            if (compileToolchainIdentity is not null) throw new ArgumentException("Execution pins its runtime independently from the compiler.");
            CompiledRuntimeIdentity = InspectionTransport.RequireImage(compiledRuntimeIdentity ??
                throw new ArgumentException("Execution requires an explicitly pinned runtime identity."));
        }
        else if (compiledRuntimeIdentity is not null) throw new ArgumentException("Only execution accepts a compiled runtime pin.");
        ResponseBound = MaximumServiceAdmissionWait + Profile.MaximumWallTimeout * 2 + ProcessWorkerOptions.KillTimeout + TimeSpan.FromSeconds(5);
        QuarantineBound = ResponseBound;
        SettlementBound = Profile.MaximumWallTimeout + ProcessWorkerOptions.KillTimeout + TimeSpan.FromSeconds(3);
        cancellationSettlement = CancelRequestBound + SettlementBound;
    }

    private readonly TimeSpan cancellationSettlement;

    /// <summary>The longest wait a service may queue an operation before it starts or refuses it.</summary>
    public static readonly TimeSpan MaximumServiceAdmissionWait = TimeSpan.FromSeconds(20);

    public IFerruleServiceConnector Connector { get; }
    public string ProfileName { get; }
    public DockerWorkerProfile Profile { get; }
    /// <summary>The exact worker build every answering instance must report.</summary>
    public string BuildIdentity { get; }
    public string? CompileToolchainIdentity { get; }
    public string? CompilationBuildIdentity { get; }
    public string? CompiledRuntimeIdentity { get; }

    /// <summary>Bounded body transfer, service admission, execution, child reaping, and transport margin.</summary>
    internal TimeSpan ResponseBound { get; init; }

    /// <summary>After a cancellation request: the wall deadline, child reaping, and margin.</summary>
    internal TimeSpan SettlementBound { get; init; }

    internal TimeSpan CancelRequestBound { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest a canceled operation may take from its cancellation to its settled answer: the remote cancellation
    /// request and the wait for the original answer share it. The default lets a service that never sees the request
    /// reach its own wall deadline. A caller whose deadline leaves less, such as the machine endpoint, sets that
    /// margin; an operation not settled within it has an unknown outcome and closes admission, like any unconfirmed
    /// settlement.
    /// </summary>
    public TimeSpan CancellationSettlement
    {
        get => cancellationSettlement;
        init => cancellationSettlement = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Operations sent for one call when refusals prove that nothing ran; each uses a fresh identity and channel.</summary>
    internal int Attempts { get; init; } = 3;

    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    // Include a still-buffering or queued request as well as its child: a broken channel does not prove when
    // the replica noticed the disconnect. These are recovery delays, not relaxed execution deadlines.
    internal TimeSpan QuarantineBound { get; init; }
    internal TimeSpan RecoveryProbeDelay { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan UncertaintyWindow { get; init; } = TimeSpan.FromMinutes(10);
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Sends exact frames to a Ferrule service through a connector. Each operation uses one fresh identity on one fresh
/// channel. A refusal that proves nothing ran (busy, stopping, unreachable before any frame byte) is retried with a new
/// operation, so another or a newly added replica can take it. Cancellation travels on the operation's own channel and
/// waits for the settled answer. An answer that cannot be confirmed after frame bytes may have been delivered is a
/// cleanup failure and quarantines further admission until bounded remote lifetime and fresh readiness are proven.
/// </summary>
public sealed class FerruleServiceRunner : IInspectionWorkerRunner, ICompilationIdentitySource, IExecutionAdmission, IAsyncDisposable
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
    private bool permanentlyUnavailable;
    private Task? recovery;
    private long quarantineVersion;
    private long quarantineStarted;
    private TimeSpan quarantineDuration;
    private readonly Queue<long> uncertainties = new();

    public FerruleServiceRunner(FerruleServiceRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    public string BuildIdentity => options.CompiledRuntimeIdentity ?? (options.CompileToolchainIdentity is { } toolchain
        ? CompileWorkerIdentity.Combine(options.BuildIdentity, toolchain) : options.BuildIdentity);
    public string? CompilationBuildIdentity => options.CompilationBuildIdentity;

    /// <summary>The service profile every operation of this runner is sent to.</summary>
    public string ProfileName => options.ProfileName;

    /// <summary>False while stopping, quarantined, or awaiting a fresh identity-checked recovery probe.</summary>
    public bool Available { get { lock (lifetimeGate) return !stopping && Volatile.Read(ref unavailable) == 0; } }
    public bool CanExecute => Available;

    public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
    {
        if (requestFrame.Length > options.Profile.MaximumRequestBytes)
            throw new ArgumentException("The request frame exceeds the fixed transport limit.", nameof(requestFrame));

        lock (lifetimeGate)
        {
            if (stopping || Volatile.Read(ref unavailable) != 0) return Failed(WorkerProcessFailure.SandboxUnavailable);
            outstandingOperations++;
        }
        try
        {
            if (Volatile.Read(ref unavailable) != 0) return Failed(WorkerProcessFailure.SandboxUnavailable);
            if (cancellationToken.IsCancellationRequested) return Failed(WorkerProcessFailure.Canceled);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            var result = await RunAttemptsAsync(requestFrame, lifetime.Token).ConfigureAwait(false);
            if (result.Failure == WorkerProcessFailure.CleanupFailed) Quarantine();
            return result;
        }
        catch
        {
            lock (lifetimeGate)
            {
                permanentlyUnavailable = true;
                Interlocked.Exchange(ref unavailable, 1);
            }
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

    private void Quarantine()
    {
        lock (lifetimeGate)
        {
            Interlocked.Exchange(ref unavailable, 1);
            quarantineVersion++;
            quarantineStarted = options.Clock.GetTimestamp();
            while (uncertainties.TryPeek(out var first) && options.Clock.GetElapsedTime(first, quarantineStarted) >= options.UncertaintyWindow)
                uncertainties.Dequeue();
            // A fixed-size history and one recovery loop per runner bound both memory and probe traffic.
            if (uncertainties.Count == 3) uncertainties.Dequeue();
            uncertainties.Enqueue(quarantineStarted);
            quarantineDuration = uncertainties.Count >= 3 && options.UncertaintyWindow > options.QuarantineBound
                ? options.UncertaintyWindow : options.QuarantineBound;
            if (!stopping && !permanentlyUnavailable && recovery is null)
            {
                // Recovery belongs to the host, not the failed request's identity or execution context.
                if (ExecutionContext.IsFlowSuppressed()) recovery = Task.Run(RecoverAsync);
                else using (ExecutionContext.SuppressFlow()) recovery = Task.Run(RecoverAsync);
            }
        }
    }

    private async Task RecoverAsync()
    {
        try
        {
            while (true)
            {
                long version;
                TimeSpan remaining;
                lock (lifetimeGate)
                {
                    if (stopping || permanentlyUnavailable) return;
                    version = quarantineVersion;
                    remaining = quarantineDuration - options.Clock.GetElapsedTime(quarantineStarted);
                }
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, options.Clock, shutdown.Token).ConfigureAwait(false);
                    // Recheck the monotonic deadline and generation, including a newer quarantine during the wait.
                    continue;
                }

                var ready = false;
                try
                {
                    await using var channel = options.Connector.Open();
                    using var bound = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                    bound.CancelAfter(options.CancelRequestBound);
                    ready = (await ReadyAsync(channel, bound.Token).ConfigureAwait(false)).Ready;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException
                    or AuthenticationException or InvalidOperationException or InvalidDataException)
                {
                    // Failed recovery proves nothing. Stay closed and probe at a bounded rate, never send a frame.
                }
                lock (lifetimeGate)
                {
                    if (stopping || permanentlyUnavailable) return;
                    if (version != quarantineVersion) continue;
                    if (ready && outstandingOperations == 0)
                    {
                        Interlocked.Exchange(ref unavailable, 0);
                        recovery = null;
                        return;
                    }
                }
                await Task.Delay(options.RecoveryProbeDelay, options.Clock, shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task<WorkerProcessResult> RunAttemptsAsync(ReadOnlyMemory<byte> frame, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (Volatile.Read(ref unavailable) != 0) return Failed(WorkerProcessFailure.SandboxUnavailable);
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
            // original request once its child has settled. The request and that wait share one settlement bound.
            using var settling = new CancellationTokenSource(options.CancellationSettlement);
            await CancelAsync(channel, operation, settling.Token).ConfigureAwait(false);
            using (var settlement = CancellationTokenSource.CreateLinkedTokenSource(settling.Token))
            {
                settlement.CancelAfter(options.SettlementBound);
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
        var readiness = await ReadyAsync(channel, abort).ConfigureAwait(false);
        if (!readiness.Ready) return (Failed(WorkerProcessFailure.SandboxUnavailable), readiness.Retryable);

        return await SendFrameAsync(channel, operation, content, abort).ConfigureAwait(false);
    }

    private async Task<(bool Ready, bool Retryable)> ReadyAsync(IFerruleServiceChannel channel, CancellationToken abort)
    {
        using (var ready = new HttpRequestMessage(HttpMethod.Get, new Uri(FerruleServiceProtocol.ReadyPath, UriKind.Relative)))
        using (var admitted = await channel.SendAsync(ready, abort).ConfigureAwait(false))
        {
            if (admitted.StatusCode == HttpStatusCode.ServiceUnavailable)
                return (false, true);
            if (admitted.StatusCode != HttpStatusCode.NoContent || !Single(admitted.Headers, InspectionTransport.ImageHeader, out var identity) ||
                !string.Equals(identity, options.BuildIdentity, StringComparison.Ordinal) || !FromService(admitted.Headers))
                return (false, false);
            if (options.CompileToolchainIdentity is { } expected &&
                (!Single(admitted.Headers, FerruleServiceProtocol.CompileToolchainHeader, out var toolchain) || toolchain != expected))
                return (false, false);
            if (options.CompiledRuntimeIdentity is { } runtime &&
                (!Single(admitted.Headers, FerruleServiceProtocol.CompiledRuntimeHeader, out var receivedRuntime) || receivedRuntime != runtime))
                return (false, false);
        }
        return (true, false);
    }

    private async Task<(WorkerProcessResult Result, bool Retryable)> SendFrameAsync(IFerruleServiceChannel channel, string operation,
        TransportFrameContent content, CancellationToken abort)
    {
        if (Volatile.Read(ref unavailable) != 0) return (Failed(WorkerProcessFailure.SandboxUnavailable), false);
        // The frame travels on the same channel, which reaches the instance that just answered.
        var path = new Uri(FerruleServiceProtocol.OperationPath(options.ProfileName, operation), UriKind.Relative);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.TryAddWithoutValidation(InspectionTransport.ImageHeader, options.BuildIdentity);
        if (options.CompileToolchainIdentity is { } pinnedToolchain)
            request.Headers.TryAddWithoutValidation(FerruleServiceProtocol.CompileToolchainHeader, pinnedToolchain);
        if (options.CompiledRuntimeIdentity is { } runtimeIdentity)
            request.Headers.TryAddWithoutValidation(FerruleServiceProtocol.CompiledRuntimeHeader, runtimeIdentity);
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
            options.CompileToolchainIdentity is { } expectedToolchain &&
                (!Single(response.Headers, FerruleServiceProtocol.CompileToolchainHeader, out var responseToolchain) || responseToolchain != expectedToolchain) ||
            options.CompiledRuntimeIdentity is { } expectedRuntime &&
                (!Single(response.Headers, FerruleServiceProtocol.CompiledRuntimeHeader, out var responseRuntime) || responseRuntime != expectedRuntime) ||
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

    private async Task CancelAsync(IFerruleServiceChannel channel, string operation, CancellationToken settling)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(settling);
            bound.CancelAfter(options.CancelRequestBound);
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
        if (recovery is { } recovering) await recovering.ConfigureAwait(false);
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
