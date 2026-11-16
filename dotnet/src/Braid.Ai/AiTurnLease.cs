using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Braid.Ai;

public sealed class AiTurnLease : IAsyncDisposable
{
    private readonly object _gate = new object();
    private readonly AiRoundClient _owner;
    private readonly TimeProvider _clock;
    private long _started;
    private readonly AiTurnLimits _limits;
    private readonly AiCancellation _cancellation;
    private readonly CancellationToken _token;
    private CancellationTokenRegistration _registration;
    private readonly TaskCompletionSource _initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<Guid> _roundIds = new HashSet<Guid>();
    private readonly List<AiRoundStream> _streams = new List<AiRoundStream>();
    private readonly List<Opening> _openings = new List<Opening>();
    private AiRoundStream? _active;
    private Opening? _opening;
    private Task? _cleanup;
    private bool _activeCleaned, _cleanupFailed;
    private int _state, _rounds, _calls, _frames, _events;
    private long _requestBytes, _wireBytes, _textBytes, _elapsedTicks;
    internal AiTurnLease(AiRoundClient owner, AiProfileSpec profile, AiTurnLimits limits, TimeProvider clock)
    {
        // Pure allocation only: OpenTurn may construct this under its gate.
        _owner = owner; Profile = profile; _limits = limits; _clock = clock;
        _cancellation = new AiCancellation(EnsureClosedCleanup);
        _token = _cancellation.Token;
    }
    internal void Initialize(CancellationToken token)
    {
        try
        {
            _started = _clock.GetTimestamp();
            _cancellation.Arm(token, duration: _limits.Duration, clock: _clock);
            if (!_cancellation.TryRegister(Close, out _registration)) Close();
            _initialized.TrySetResult(); EnsureClosedCleanup();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _cancellation.FailInitialization();
            lock (_gate) { _cleanupFailed = true; _state = 2; }
            _initialized.TrySetException(CleanupFailure()); throw CleanupFailure();
        }
    }
    internal AiProfileSpec Profile { get; }
    internal CancellationToken Token => _token;
    internal bool Cancelled => _cancellation.Requested;
    internal TimeProvider Clock => _clock;
    internal bool OwnedBy(AiRoundClient owner) => ReferenceEquals(owner, _owner);
    internal TimeSpan Remaining
    {
        get
        {
            long elapsed = _clock.GetElapsedTime(_started, _clock.GetTimestamp()).Ticks;
            lock (_gate)
            {
                // UTC is never used; even a regressing supplied timestamp cannot
                // increase a previously observed remaining duration.
                _elapsedTicks = Math.Max(_elapsedTicks, Math.Max(0, elapsed));
                return _cancellation.DeadlineExpired || _elapsedTicks >= _limits.Duration.Ticks
                    ? TimeSpan.Zero : TimeSpan.FromTicks(_limits.Duration.Ticks - _elapsedTicks);
            }
        }
    }
    internal AiFailureCode CancellationCode => Remaining <= TimeSpan.Zero ? AiFailureCode.DeadlineExceeded : AiFailureCode.Cancelled;
    internal bool TryBegin(Guid id, int bytes, out int ordinal, out AiFailureCode refusal, out Opening? opening)
    {
        ordinal = 0; refusal = AiFailureCode.InvalidRequest; opening = null;
        TimeSpan remaining = Remaining;
        bool priorCleanupUnproven = false;
        lock (_gate)
        {
            if (_state == 1) throw new InvalidOperationException("An AI round is already active.");
            if (_state != 0) return false;
            foreach (AiRoundStream stream in _streams)
            {
                if (!stream.DisposalUnproven) continue;
                if (!stream.CleanupFailed) throw new InvalidOperationException("An AI round is already active.");
                priorCleanupUnproven = true;
            }
            if (priorCleanupUnproven) { _cleanupFailed = true; refusal = AiFailureCode.TransportFailure; }
            else if (Cancelled || remaining <= TimeSpan.Zero) refusal = remaining <= TimeSpan.Zero ? AiFailureCode.DeadlineExceeded : AiFailureCode.Cancelled;
            else if (_rounds >= _limits.MaxRounds || _events >= AiCeilings.Events || _requestBytes > _limits.MaxRequestBytes - bytes) refusal = AiFailureCode.BudgetExceeded;
            else if (!_roundIds.Add(id)) refusal = AiFailureCode.InvalidRequest;
            else
            {
                _requestBytes += bytes; ordinal = ++_rounds; _state = 1;
                opening = new Opening(); _opening = opening; _openings.Add(opening); return true;
            }
        }
        Close(); return false;
    }
    internal bool Attach(AiRoundStream stream)
    {
        TimeSpan remaining = Remaining;
        lock (_gate)
        {
            if (_state != 1 || Cancelled || remaining <= TimeSpan.Zero || stream.Cancelled) return false;
            _active = stream; _activeCleaned = false; _streams.Add(stream);
        }
        // Callback admission is atomic against cancellation, but neither
        // registration callbacks nor transport disposal run under this gate.
        if (!stream.AttachCancellation()) return false;
        lock (_gate) return _state == 1 && !_activeCleaned && !Cancelled && !stream.Cancelled;
    }
    internal void EndRound(AiRoundStream stream, AiFinishReason? reason)
    {
        TimeSpan remaining = Remaining;
        lock (_gate)
        {
            if (!ReferenceEquals(_active, stream)) return;
            _active = null; _activeCleaned = false;
            if (_state == 1 && reason == AiFinishReason.ToolCalls && !Cancelled && remaining > TimeSpan.Zero)
                _state = 0;
            else _state = 2;
        }
        EnsureClosedCleanup();
    }
    internal void TransportClosed(AiRoundStream stream)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, stream)) return;
            // Keep the consumption/disposal handle, but no longer own live
            // transport. Cancellation never manufactures a public terminal.
            _activeCleaned = true; _state = 2;
        }
        EnsureClosedCleanup();
    }
    internal void Close()
    {
        lock (_gate) _state = 2;
        _ = _cancellation.Request();
        EnsureClosedCleanup();
    }
    internal void EndOpening(Opening opening)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_opening, opening)) throw new InvalidOperationException("AI opening ownership changed.");
            if (opening.Cleaned) _opening = null;
            else { _state = 2; _cleanupFailed = true; }
        }
        EnsureClosedCleanup();
    }
    internal void PublishOpening(Opening opening, Task original)
    {
        opening.Publish(original);
    }
    internal void DisposalSettled() => EnsureClosedCleanup();
    private void EnsureClosedCleanup()
    {
        bool closed;
        lock (_gate) closed = _state == 2 && _initialized.Task.IsCompletedSuccessfully;
        if (closed) _ = EnsureCleanup();
    }
    internal Task JoinClosedCleanupAsync()
    {
        bool closed;
        lock (_gate) closed = _state == 2;
        return closed ? EnsureCleanup() : Task.CompletedTask;
    }
    internal void DisposeUnopened()
    {
        // OpenTurn is synchronous; its rejected lease remains in the shutdown
        // registry while this same retained disposal finishes (or stays faulted).
        _ = DisposeAsync();
    }
    internal void ChargeWire(int count)
    {
        lock (_gate)
        {
            if (count < 0 || _wireBytes > _limits.MaxWireBytes - count) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
            _wireBytes += count;
        }
    }
    internal void ChargeText(int count)
    {
        lock (_gate)
        {
            if (count < 0 || _textBytes > _limits.MaxTextBytes - count) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
            _textBytes += count;
        }
    }
    internal void ChargeCalls(int count)
    {
        lock (_gate)
        {
            if (count < 0 || _calls > _limits.MaxToolCalls - count) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
            _calls += count;
        }
    }
    internal void ChargeFrame() { lock (_gate) if (++_frames > AiCeilings.Frames) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded); }
    internal void ChargeEvent(bool terminal = false)
    {
        lock (_gate)
        {
            // Keep one slot for the mandatory failure/finish terminal.
            int limit = terminal ? AiCeilings.Events : AiCeilings.Events - 1;
            if (_events >= limit) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
            _events++;
        }
    }
    public ValueTask DisposeAsync()
    {
        AiRoundStream[] streams;
        lock (_gate) { _state = 2; streams = _streams.ToArray(); }
        // Public disposal invalidates consumption; automatic closed-turn
        // cleanup does not. These are metadata writes, never callbacks.
        foreach (AiRoundStream stream in streams) stream.MarkDisposed();
        return new ValueTask(EnsureCleanup());
    }
    private Task EnsureCleanup()
    {
        TaskCompletionSource completion; Task[] openings;
        lock (_gate)
        {
            if (_cleanup is not null) return _cleanup;
            _state = 2;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cleanup = completion.Task;
            openings = new Task[_openings.Count];
            for (int i = 0; i < openings.Length; i++) openings[i] = _openings[i].Completion;
        }
        _ = CleanupCoreAsync(openings, completion);
        return completion.Task;
    }
    private async Task CleanupCoreAsync(Task[] openings, TaskCompletionSource completion)
    {
        bool failed = false;
        try { await _initialized.Task.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failed = true; }
        try { await _cancellation.RequestAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failed = true; }
        // Join this exact original opening, not a cancellable waiter or a flag.
        foreach (Task opening in openings)
        {
            try { await opening.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failed = true; }
        }
        AiRoundStream[] streams;
        lock (_gate) streams = _streams.ToArray();
        foreach (AiRoundStream stream in streams)
        {
            try { await stream.EnsureCleanup().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failed = true; }
        }
        try { _registration.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failed = true; }
        try { await _cancellation.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { failed = true; }
        lock (_gate) { _cleanupFailed |= failed; failed = _cleanupFailed; }
        if (failed) completion.TrySetException(CleanupFailure());
        else _owner.CompleteDisposal(this, completion);
    }
    internal static InvalidOperationException CleanupFailure() => new InvalidOperationException("AI cleanup did not complete.");
    internal sealed class Opening
    {
        private readonly TaskCompletionSource<Task> _published = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Disposal may race before OpenOwnedAsync returns its Task to the caller.
        // Publication joins that exact original task, never a cancelled waiter.
        internal Opening() { Completion = _published.Task.Unwrap(); }
        internal Task Completion { get; }
        internal void Publish(Task original) => _published.TrySetResult(original);
        private readonly List<IDisposable> _unproven = new List<IDisposable>();
        private readonly List<AiCancellation> _unprovenCancellations = new List<AiCancellation>();
        private AiRoundStream? _unprovenStream;
        private Task<AiCredentialResult>? _credential;
        private bool _credentialClaimed;
        internal bool Cleaned => _unproven.Count == 0 && _unprovenCancellations.Count == 0 && _unprovenStream is null;
        internal async Task DisposeCancellationAsync(AiCancellation? cancellation, bool cancel = true)
        {
            if (cancellation is null) return;
            try
            {
                if (cancel) await cancellation.DisposeAsync().ConfigureAwait(false);
                else await cancellation.RetireAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { _unprovenCancellations.Add(cancellation); }
        }
        internal void Retain(AiRoundStream stream) => _unprovenStream = stream;
        internal void TrackCredential(Task<AiCredentialResult> credential) => _credential = credential;
        internal void ClaimCredential() => _credentialClaimed = true;
        internal async Task SettleCredentialAsync()
        {
            if (_credential is null) return;
            AiCredentialResult credential;
            try { credential = await _credential.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { return; }
            if (!_credentialClaimed && credential is CredentialFound found) Dispose(found.Secret);
        }
        internal void Dispose(IDisposable? resource)
        {
            try { resource?.Dispose(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            { if (resource is not null) _unproven.Add(resource); }
        }
    }
    public override string ToString() => nameof(AiTurnLease);
}
