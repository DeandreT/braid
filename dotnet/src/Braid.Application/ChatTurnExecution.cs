using Braid.Ai;
using Braid.Domain;

namespace Braid.Application;

/// <summary>A single original operation, including cleanup. A bounded observer never owns its lifetime.</summary>
public sealed class ChatTurnExecution
{
    private readonly object _gate = new();
    private readonly ChatTurnOrchestrator _owner;
    private readonly ActorContext _actor;
    private readonly ChatConversationKey _key;
    private readonly ChatTurnIntent _intent;
    private readonly Guid[] _history;
    private readonly CancellationToken _caller;
    private readonly CancellationTokenSource _source = new();
    private readonly List<Task> _streamCleanup = [];
    private CancellationTokenRegistration _registration;
    private ITimer? _timer;
    private Task? _cancel;
    private Task? _originalCancellation;
    private Task? _turnCleanup;
    private Task? _timerCleanup;
    private Task? _registrationCleanup;
    private bool _closed;
    private int _reason, _releaseSafe, _clientJoinUnproven;
    private AiTurnLease? _turn;
    private Task<ChatOrchestrationResult> _completion = null!;
    private readonly TaskCompletionSource<ChatTurnFacts?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ChatTurnExecution(ChatTurnOrchestrator owner, ActorContext actor, ChatConversationKey key,
        ChatTurnIntent intent, Guid[] history, CancellationToken caller)
    { _owner = owner; _actor = actor; _key = key; _intent = intent; _history = history; _caller = caller; }

    internal void Start() => _completion = RunAsync();
    public Task<ChatOrchestrationResult> Completion => _completion;
    /// <summary>The original acknowledged start facts, or null once execution ends without them.
    /// It grants nothing; Completion and ReleaseSafe remain the lifetime boundary.</summary>
    public Task<ChatTurnFacts?> Started => _started.Task;
    public bool ReleaseSafe => Volatile.Read(ref _releaseSafe) != 0;
    internal CancellationToken Token => _source.Token;
    internal bool Cancelled => Volatile.Read(ref _reason) != 0;
    internal bool TimedOut => Volatile.Read(ref _reason) == 2;
    internal void EnterClientAdmission() => _clientJoinUnproven = 1;
    internal void RejectedClientAdmission(AiFailure failure)
    {
        // Cancelled can be returned after internal admission/initialization, without returning its lease.
        // The shared client still owns cleanup, but this public API cannot join that hidden scope per turn.
        if (failure.Code != AiFailureCode.Cancelled) _clientJoinUnproven = 0;
    }
    internal void Own(AiTurnLease turn) { _turn = turn; _clientJoinUnproven = 0; }
    internal void Admitted(ChatTurnFacts facts) => _started.TrySetResult(facts);
    /// <summary>Joins the cancellation request/callback task, not the whole execution.
    /// Completion and ReleaseSafe remain the lifetime/release boundary.</summary>
    public Task CancelAsync() => RequestCancellation(1);
    public Task<ChatOrchestrationResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Completion.WaitAsync(timeout, cancellationToken);

    private Task RequestCancellation(int reason)
    {
        TaskCompletionSource completion; Task task;
        lock (_gate)
        {
            if (_closed) return _cancel ?? Task.CompletedTask;
            if (_cancel is not null) return _cancel;
            Interlocked.CompareExchange(ref _reason, reason, 0);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            task = _cancel = completion.Task;
        }
        // Never invoke cancellation callbacks while holding a project gate.
        _ = CancelCoreAsync(completion);
        return task;
    }

    private async Task CancelCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            _originalCancellation = _source.CancelAsync();
            await _originalCancellation.ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception e) when (Recoverable(e))
        { completion.TrySetException(new ChatOrchestrationCleanupException()); }
    }

    internal async Task JoinStreamAsync(AiRoundStream stream)
    {
        var task = stream.DisposeAsync().AsTask();
        _streamCleanup.Add(task); // retain the actual task, including failure
        await task.ConfigureAwait(false);
    }

    private async Task<ChatOrchestrationResult> RunAsync()
    {
        await Task.Yield(); // publish the handle before invoking any borrowed port
        ChatOrchestrationResult result;
        try
        {
            _registration = _caller.UnsafeRegister(static state =>
            { _ = ((ChatTurnExecution)state!).RequestCancellation(1); }, this);
            var remaining = _intent.DeadlineUtc - _owner.Now;
            var duration = TimeSpan.FromTicks(_intent.Envelope.ProviderBudget.DurationTicks);
            if (remaining > duration) remaining = duration;
            if (remaining <= TimeSpan.Zero) _ = RequestCancellation(2);
            else _timer = _owner.Clock.CreateTimer(static state =>
            { _ = ((ChatTurnExecution)state!).RequestCancellation(2); }, this, remaining, Timeout.InfiniteTimeSpan);
            result = await _owner.ExecuteAsync(this, _actor, _key, _intent, _history).ConfigureAwait(false);
        }
        catch (Exception e) when (Recoverable(e))
        { result = new(ChatOrchestrationOutcome.PersistenceUncertain); }
        _started.TrySetResult(null);

        // No timeout wraps cleanup. Join every original owner even if another one failed.
        bool clean = _clientJoinUnproven == 0;
        if (_timer is not null)
            try { _timerCleanup = _timer.DisposeAsync().AsTask(); await _timerCleanup.ConfigureAwait(false); }
            catch (Exception e) when (Recoverable(e)) { clean = false; }
        try { _registrationCleanup = _registration.DisposeAsync().AsTask(); await _registrationCleanup.ConfigureAwait(false); }
        catch (Exception e) when (Recoverable(e)) { clean = false; }
        Task? cancel;
        lock (_gate) { _closed = true; cancel = _cancel; }
        if (cancel is not null)
            try { await cancel.ConfigureAwait(false); }
            catch (Exception e) when (Recoverable(e)) { clean = false; }
        foreach (var task in _streamCleanup)
            try { await task.ConfigureAwait(false); }
            catch (Exception e) when (Recoverable(e)) { clean = false; }
        if (_turn is not null)
            try
            {
                _turnCleanup = _turn.DisposeAsync().AsTask();
                await _turnCleanup.ConfigureAwait(false);
            }
            catch (Exception e) when (Recoverable(e)) { clean = false; }
        if (clean)
        {
            _source.Dispose();
            Volatile.Write(ref _releaseSafe, 1);
            return result;
        }
        // Retain this handle and its borrowed dependency scope. No false release/recovery signal.
        return new(ChatOrchestrationOutcome.CleanupUnproven, result.Phase);
    }

    internal static bool Recoverable(Exception e) => e is not OutOfMemoryException and not StackOverflowException;
    public override string ToString() => nameof(ChatTurnExecution);
}
