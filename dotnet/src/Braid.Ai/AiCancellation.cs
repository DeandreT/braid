using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Braid.Ai;

// The private source is cancelled exactly once. Upstream tokens/timers only
// request this operation; none can cancel the source behind its owner's back.
internal sealed class AiCancellation : IAsyncDisposable
{
    private readonly object _gate = new object();
    private readonly CancellationTokenSource _source = new CancellationTokenSource();
    private readonly TaskCompletionSource _armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<CancellationTokenRegistration> _links = new List<CancellationTokenRegistration>();
    private readonly Action _settled;
    private ITimer? _timer;
    private ITimer? _idleTimer;
    private TimeProvider _clock = TimeProvider.System;
    private ReadOperation? _activeRead, _lastRead;
    private Task? _cancellation, _disposal;
    private int _armStarted, _requested, _expired, _callbacksSettled, _failed, _disposing, _cleaned, _retired;
    internal AiCancellation(Action settled) { _settled = settled; Token = _source.Token; }
    internal CancellationToken Token { get; }
    internal bool Requested => Volatile.Read(ref _requested) != 0;
    internal bool DeadlineExpired => Volatile.Read(ref _expired) != 0;
    internal bool Unproven => Volatile.Read(ref _failed) != 0
        || (Requested && Volatile.Read(ref _callbacksSettled) == 0)
        || (Volatile.Read(ref _disposing) != 0 && Volatile.Read(ref _cleaned) == 0);
    internal void Arm(CancellationToken first, CancellationToken second = default, TimeSpan? duration = null, TimeProvider? clock = null, bool idleDeadline = false)
    {
        if (Interlocked.CompareExchange(ref _armStarted, 1, 0) != 0) throw AiTurnLease.CleanupFailure();
        try
        {
            _clock = clock ?? TimeProvider.System;
            if (first.CanBeCanceled) _links.Add(first.Register(static state => ((AiCancellation)state!).Request(), this));
            if (second.CanBeCanceled) _links.Add(second.Register(static state => ((AiCancellation)state!).Request(), this));
            if (duration is TimeSpan due)
                _timer = _clock.CreateTimer(static state => ((AiCancellation)state!).Expire(), this, due, Timeout.InfiniteTimeSpan);
            if (idleDeadline)
                _idleTimer = _clock.CreateTimer(static state => ((AiCancellation)state!).IdleExpired(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _armed.TrySetResult();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Volatile.Write(ref _failed, 1); _armed.TrySetException(AiTurnLease.CleanupFailure());
            throw AiTurnLease.CleanupFailure();
        }
    }
    internal void FailInitialization()
    {
        Volatile.Write(ref _failed, 1);
        if (Interlocked.CompareExchange(ref _armStarted, 1, 0) == 0) _armed.TrySetException(AiTurnLease.CleanupFailure());
    }
    internal bool TryRegister(Action callback, out CancellationTokenRegistration registration)
    {
        lock (_gate)
        {
            registration = default;
            if (_requested != 0 || _retired != 0 || _disposing != 0) return false;
            // Only Start can authorize source.Cancel, after leaving this gate.
            // Thus Register cannot invoke a cancellation callback inline here.
            registration = Token.Register(callback); return true;
        }
    }
    internal Task Request() => Start(false);
    internal Task RequestAsync() => Start(true);
    internal void Expire() { Volatile.Write(ref _expired, 1); _ = Request(); }
    internal ValueTask<int> ReadAsync(Stream source, Memory<byte> buffer, TimeSpan idleLimit)
    {
        var read = new ReadOperation(_clock.GetTimestamp(), idleLimit);
        lock (_gate)
        {
            if (_requested != 0 || _disposing != 0 || _expired != 0) throw new OperationCanceledException(Token);
            if (_activeRead is not null) throw new InvalidOperationException("An AI read is already active.");
            _activeRead = read; _lastRead = read;
        }
        Task<int> original = ReadCoreAsync(source, buffer, read);
        read.Publish(original); return new ValueTask<int>(original);
    }
    private async Task<int> ReadCoreAsync(Stream source, Memory<byte> buffer, ReadOperation read)
    {
        try
        {
            ChangeIdle(read.Limit);
            return await source.ReadAsync(buffer, Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_activeRead, read)) _activeRead = null;
            ChangeIdle(Timeout.InfiniteTimeSpan);
        }
    }
    private void ChangeIdle(TimeSpan due)
    {
        try
        {
            if (_idleTimer is null || !_idleTimer.Change(due, Timeout.InfiniteTimeSpan)) throw AiTurnLease.CleanupFailure();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { Volatile.Write(ref _failed, 1); throw AiTurnLease.CleanupFailure(); }
    }
    private void IdleExpired()
    {
        try { CheckIdleExpiration(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { Volatile.Write(ref _failed, 1); _ = Request(); }
    }
    private void CheckIdleExpiration()
    {
        ReadOperation? read;
        lock (_gate)
        {
            if (_requested != 0 || _disposing != 0) return;
            read = _activeRead;
        }
        if (read is null) return;
        TimeSpan elapsed = _clock.GetElapsedTime(read.Started, _clock.GetTimestamp());
        lock (_gate)
        {
            // Queued callbacks from a previous Change cannot expire a newer read.
            if (_requested != 0 || _disposing != 0 || !ReferenceEquals(_activeRead, read) || elapsed < read.Limit) return;
            Volatile.Write(ref _expired, 1);
        }
        _ = Request();
    }
    private Task Start(bool asynchronous)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_cancellation is not null) return _cancellation;
            if (_retired != 0) return Task.CompletedTask;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cancellation = completion.Task; Volatile.Write(ref _requested, 1);
        }
        // Never run cancellation callbacks while holding an owner mutex.
        // Disposal queues the first operation; upstream/reader/manual requests
        // may execute it synchronously. Later requests always return this task.
        if (asynchronous)
        {
            try { _ = Task.Run(() => CancelCore(completion)); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            { Volatile.Write(ref _failed, 1); completion.TrySetException(AiTurnLease.CleanupFailure()); }
        }
        else CancelCore(completion);
        return completion.Task;
    }
    private void CancelCore(TaskCompletionSource completion)
    {
        try { _source.Cancel(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { Volatile.Write(ref _failed, 1); }
        Volatile.Write(ref _callbacksSettled, 1);
        NotifySettlement();
        if (Volatile.Read(ref _failed) != 0) completion.TrySetException(AiTurnLease.CleanupFailure());
        else completion.TrySetResult();
    }
    private void NotifySettlement()
    {
        try { _settled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { Volatile.Write(ref _failed, 1); }
    }
    public ValueTask DisposeAsync() => Dispose(true);
    // Header-only native work has completed: detach its deadline/upstream links
    // without issuing a new cancellation against a handed-off response body.
    internal ValueTask RetireAsync() => Dispose(false);
    private ValueTask Dispose(bool cancel)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposal is not null) return new ValueTask(_disposal);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task; Volatile.Write(ref _disposing, 1);
            if (!cancel && _cancellation is null) _retired = 1;
        }
        _ = DisposeCoreAsync(completion); return new ValueTask(completion.Task);
    }
    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try { await _armed.Task.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { Volatile.Write(ref _failed, 1); }
        // This is the original operation even if a caller/reader/timer started it.
        try { await RequestAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { Volatile.Write(ref _failed, 1); }
        ReadOperation? read;
        lock (_gate) read = _lastRead;
        if (read is not null)
        {
            // A native read fault is an observed provider outcome, not evidence
            // of failed cleanup. Timer failures independently latch _failed.
            try { await read.Completion.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
        }
        foreach (CancellationTokenRegistration link in _links)
        {
            try { link.Dispose(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { Volatile.Write(ref _failed, 1); }
        }
        try { if (_timer is not null) await _timer.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { Volatile.Write(ref _failed, 1); }
        try { if (_idleTimer is not null) await _idleTimer.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { Volatile.Write(ref _failed, 1); }
        try { _source.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { Volatile.Write(ref _failed, 1); }
        if (Volatile.Read(ref _failed) == 0) Volatile.Write(ref _cleaned, 1);
        NotifySettlement();
        if (Volatile.Read(ref _failed) != 0) completion.TrySetException(AiTurnLease.CleanupFailure());
        else completion.TrySetResult();
    }
    public override string ToString() => nameof(AiCancellation);
    private sealed class ReadOperation
    {
        private readonly TaskCompletionSource<Task> _published = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadOperation(long started, TimeSpan limit) { Started = started; Limit = limit; Completion = _published.Task.Unwrap(); }
        internal long Started { get; }
        internal TimeSpan Limit { get; }
        internal Task Completion { get; }
        internal void Publish(Task original) => _published.TrySetResult(original);
    }
}
