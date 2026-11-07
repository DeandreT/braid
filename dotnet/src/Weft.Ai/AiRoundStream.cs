using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Weft.Ai.Wire;

namespace Weft.Ai;

public sealed class AiRoundStream : IAsyncDisposable
{
    private readonly object _transportGate = new object();
    private readonly object _disposeGate = new object();
    private readonly AiTurnLease _turn;
    private readonly Guid _round;
    private readonly HttpClient _client;
    private readonly HttpRequestMessage _message;
    private readonly HttpResponseMessage _response;
    private readonly AiBearerSecret? _secret;
    private readonly AiCancellation _cancellation;
    private readonly CancellationToken _token;
    private CancellationTokenRegistration _registration;
    private Task? _cleanup;
    private TaskCompletionSource<bool>? _transport;
    private readonly byte[] _requestBytes;
    private int _read, _enumerating, _disposed, _settled, _cleanupSucceeded, _transportThread;
    internal AiRoundStream(AiTurnLease turn, Guid round, HttpClient client, HttpRequestMessage message,
        HttpResponseMessage response, AiBearerSecret? secret, AiCancellation cancellation, byte[] bytes)
    {
        _turn = turn; _round = round; _client = client; _message = message; _response = response;
        _secret = secret; _cancellation = cancellation; _token = cancellation.Token; _requestBytes = bytes;
    }
    internal bool Cancelled => _cancellation.Requested || _turn.Cancelled;
    internal bool DisposalUnproven
    {
        // Full cleanup is required from construction, not only once someone
        // starts DisposeAsync. A native terminal/closed socket cannot discharge it.
        get { return _cancellation.Unproven || Volatile.Read(ref _cleanupSucceeded) == 0; }
    }
    internal bool CleanupFailed => Volatile.Read(ref _cleanup)?.IsFaulted == true;
    internal void MarkDisposed() => Volatile.Write(ref _disposed, 1);
    internal bool AttachCancellation() => _cancellation.TryRegister(CancelTransport, out _registration);
    public IAsyncEnumerable<AiRoundEvent> ReadAllAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _read, 1) != 0)
            throw new InvalidOperationException("An AI stream can be read once.");
        return ReadCore(ct);
    }
    private async IAsyncEnumerable<AiRoundEvent> ReadCore([EnumeratorCancellation] CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _enumerating, 1, 0) != 0)
            throw new InvalidOperationException("An AI stream can be enumerated once.");
        using CancellationTokenRegistration registration = ct.Register(static state => ((AiRoundStream)state!).Cancel(), this);
        AiTokenUsage usage = AiTokenUsage.Unknown;
        IAsyncEnumerator<AiDecoded>? reader = null;
        try
        {
            Stream? source = null; AiFailure? failure = null;
            try
            {
                CheckCancellation();
                source = await _response.Content.ReadAsStreamAsync(_token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            { failure = Map(exception); }
            if (failure is not null)
            {
                Complete(null); _turn.ChargeEvent(true);
                yield return AiRoundFailed.Create(failure, usage); yield break;
            }
            ChatCompletionsReader? chat = _turn.Profile.Dialect == AiDialect.OllamaChatNdjsonV1 ? null
                : new ChatCompletionsReader(_turn, _cancellation, _turn.Profile.Dialect == AiDialect.DatabricksChatCompletionsSseV1);
            IAsyncEnumerable<AiDecoded> native = chat is null
                ? new OllamaChatReader(_turn, _round, _cancellation).ReadAsync(source!, _token)
                : chat.ReadAsync(source!, _token);
            reader = native.GetAsyncEnumerator(_token);
            // A failure still reports accounting the provider already sent: published usage, or Databricks usage the
            // reader holds for its terminal. Usage stays unknown only when none arrived.
            AiTokenUsage Known() => chat?.PendingUsage ?? usage;
            while (true)
            {
                AiDecoded? decoded = null; bool available = false; failure = null;
                try
                {
                    CheckCancellation();
                    available = await reader.MoveNextAsync().ConfigureAwait(false);
                    if (available) decoded = reader.Current;
                    else failure = AiFailure.Create(AiFailureCode.TruncatedResponse, AiDispatchState.Attempted);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                { failure = Map(exception); }
                if (failure is not null)
                {
                    Complete(null); _turn.ChargeEvent(true);
                    yield return AiRoundFailed.Create(failure, Known()); yield break;
                }
                if (decoded is AiDecodedText text)
                {
                    try { CheckCancellation(); _turn.ChargeText(AiGuard.Utf8.GetByteCount(text.Text)); }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failure = Map(exception); }
                    if (failure is not null)
                    {
                        Complete(null); _turn.ChargeEvent(true);
                        yield return AiRoundFailed.Create(failure, Known()); yield break;
                    }
                    foreach (string piece in AiTextPieces.Split(text.Text))
                    {
                        try { CheckCancellation(); _turn.ChargeEvent(); }
                        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failure = Map(exception); }
                        if (failure is not null)
                        {
                            Complete(null); _turn.ChargeEvent(true);
                            yield return AiRoundFailed.Create(failure, Known()); yield break;
                        }
                        yield return AiTextDelta.Create(piece);
                    }
                    continue;
                }
                if (decoded is AiDecodedUsage reported)
                {
                    usage = reported.Usage;
                    try { CheckCancellation(); _turn.ChargeEvent(); }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failure = Map(exception); }
                    if (failure is not null)
                    {
                        Complete(null); _turn.ChargeEvent(true);
                        yield return AiRoundFailed.Create(failure, Known()); yield break;
                    }
                    yield return AiUsageObserved.Create(usage);
                    continue;
                }
                if (decoded is not AiDecodedEnd end)
                {
                    Complete(null); _turn.ChargeEvent(true);
                    yield return AiRoundFailed.Create(AiFailure.Create(AiFailureCode.MalformedResponse, AiDispatchState.Attempted), Known()); yield break;
                }
                usage = end.Usage;
                // Native framing/JSON/calls are fully validated. Close the owned
                // HTTP connection before exposing any completed intent.
                if (!CloseTransport()) failure = AiFailure.Create(AiFailureCode.TransportFailure, AiDispatchState.Attempted);
                foreach (AiToolIntent call in failure is null ? end.Calls : System.Collections.Immutable.ImmutableArray<AiToolIntent>.Empty)
                {
                    try { CheckCancellation(); _turn.ChargeEvent(); }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failure = Map(exception); }
                    if (failure is not null) break;
                    yield return AiToolIntentReady.Create(call);
                }
                if (failure is null && end.UsageObserved)
                {
                    try { CheckCancellation(); _turn.ChargeEvent(); }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failure = Map(exception); }
                    if (failure is null) yield return AiUsageObserved.Create(usage);
                }
                if (failure is null)
                {
                    try { CheckCancellation(); _turn.ChargeEvent(true); }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failure = Map(exception); }
                }
                if (failure is not null)
                {
                    Complete(null); _turn.ChargeEvent(true);
                    yield return AiRoundFailed.Create(failure, Known()); yield break;
                }
                if (Complete(end.Reason)) yield return AiRoundFinished.Create(end.Reason, usage);
                else yield return AiRoundFailed.Create(AiFailure.Create(CancellationCode, AiDispatchState.Attempted), Known());
                yield break;
            }
        }
        finally
        {
            if (reader is not null)
            {
                try { await reader.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
            }
            try { await DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
            try { await _turn.JoinClosedCleanupAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
        }
    }
    private void CheckCancellation()
    {
        if (Cancelled) throw new OperationCanceledException(_token);
        if (_turn.Remaining <= TimeSpan.Zero)
        { _ = _cancellation.Request(); throw AiGuard.Protocol(AiFailureCode.DeadlineExceeded); }
    }
    private AiFailure Map(Exception exception)
    {
        AiFailureCode code = exception is AiProtocolException protocol ? protocol.Code
            : exception is OperationCanceledException || Cancelled ? CancellationCode
            : exception is ArgumentException ? AiFailureCode.MalformedResponse : AiFailureCode.TransportFailure;
        return AiFailure.Create(code, AiDispatchState.Attempted);
    }
    private AiFailureCode CancellationCode => _cancellation.DeadlineExpired ? AiFailureCode.DeadlineExceeded : _turn.CancellationCode;
    private bool Complete(AiFinishReason? reason)
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0) return false;
        if (!CloseTransport()) return false;
        bool cancelled = Cancelled;
        _turn.EndRound(this, cancelled ? null : reason);
        return reason is null || !cancelled;
    }
    private void Cancel()
    {
        _ = _cancellation.Request();
    }
    private void CancelTransport()
    {
        Task<bool> original = CloseTransportAsync();
        // Reentrant cancellation cannot wait on the synchronous cleanup frame
        // that invoked it. That frame owns the retained task and notification;
        // every asynchronous disposal still joins it before completing.
        if (!original.IsCompleted && Volatile.Read(ref _transportThread) == Environment.CurrentManagedThreadId)
        { _ = EnsureCleanup(); return; }
        _ = EnsureCleanup();
        if (!original.GetAwaiter().GetResult()) throw AiTurnLease.CleanupFailure();
        _turn.TransportClosed(this);
    }
    private bool CloseTransport()
    {
        Task<bool> original = CloseTransportAsync();
        if (!original.IsCompleted && Volatile.Read(ref _transportThread) == Environment.CurrentManagedThreadId) return false;
        return original.GetAwaiter().GetResult();
    }
    private Task<bool> CloseTransportAsync()
    {
        TaskCompletionSource<bool> original; bool ownsCleanup;
        lock (_transportGate)
        {
            if (_transport is not null)
            {
                original = _transport; ownsCleanup = false;
            }
            else
            {
                original = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _transport = original; _transportThread = Environment.CurrentManagedThreadId; ownsCleanup = true;
            }
        }
        if (!ownsCleanup) return original.Task;
        // Resource disposal and waits never run while holding the transport gate.
        // A failed proof is cached; retained fields are not automatically retried.
        bool cleaned = TryDispose(_response);
        cleaned &= TryDispose(_message); cleaned &= TryDispose(_client); cleaned &= TryDispose(_secret);
        Array.Clear(_requestBytes, 0, _requestBytes.Length);
        Volatile.Write(ref _transportThread, 0); original.TrySetResult(cleaned);
        if (cleaned && Cancelled) _turn.TransportClosed(this);
        return original.Task;
    }
    private static bool TryDispose(IDisposable? resource)
    {
        try { resource?.Dispose(); return true; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
    public ValueTask DisposeAsync()
    {
        MarkDisposed(); return new ValueTask(EnsureCleanup());
    }
    internal Task EnsureCleanup()
    {
        TaskCompletionSource completion;
        lock (_disposeGate)
        {
            if (_cleanup is not null) return _cleanup;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _cleanup, completion.Task);
        }
        _ = CleanupCoreAsync(completion);
        return completion.Task;
    }
    private async Task CleanupCoreAsync(TaskCompletionSource completion)
    {
        bool failed = false;
        // Close HTTP even if an earlier cancellation callback is still gated.
        // Every trigger shares the owner's one original callback operation.
        if (!await CloseTransportAsync().ConfigureAwait(false)) failed = true;
        try { await _cancellation.RequestAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failed = true; }
        if (!failed && Volatile.Read(ref _settled) == 0) Complete(null);
        try { _registration.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failed = true; }
        try { await _cancellation.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failed = true; }
        if (failed) completion.TrySetException(AiTurnLease.CleanupFailure());
        else
        {
            Volatile.Write(ref _cleanupSucceeded, 1);
            _turn.DisposalSettled();
            completion.TrySetResult();
        }
    }
    public override string ToString() => nameof(AiRoundStream);
}
