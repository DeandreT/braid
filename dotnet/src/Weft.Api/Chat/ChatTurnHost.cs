using System.Collections.Concurrent;
using Weft.Ai;
using Weft.Application;
using Weft.Domain;

namespace Weft.Api.Chat;

/// <summary>Host-supplied creator-private ledger ports: a reader-credential store and a writer-credential store.</summary>
public sealed class ChatPersistencePorts
{
    public ChatPersistencePorts(IChatConversationReader readerListings, IChatTurnStore readerTurns,
        IChatConversationStore writerConversations, IChatTurnStore writerTurns)
    {
        ArgumentNullException.ThrowIfNull(readerListings);
        ArgumentNullException.ThrowIfNull(readerTurns);
        ArgumentNullException.ThrowIfNull(writerConversations);
        ArgumentNullException.ThrowIfNull(writerTurns);
        ReaderListings = readerListings;
        ReaderTurns = readerTurns;
        WriterConversations = writerConversations;
        WriterTurns = writerTurns;
    }

    public IChatConversationReader ReaderListings { get; }
    public IChatTurnStore ReaderTurns { get; }
    public IChatConversationStore WriterConversations { get; }
    public IChatTurnStore WriterTurns { get; }
    public override string ToString() => nameof(ChatPersistencePorts);
}

internal readonly record struct ChatTurnKey(Guid OrganizationId, Guid CreatorId, Guid TurnId)
{
    internal static ChatTurnKey Of(ChatConversationKey key, Guid turnId) =>
        new(key.Revision.OrganizationId, key.Creator.Value, turnId);
}

/// <summary>
/// Owns the one shared provider client, this process's live turn executions and their change signals.
/// Executions outlive the HTTP request that began them; disconnecting never cancels or re-dispatches work.
/// </summary>
public sealed class ChatTurnHost : IAsyncDisposable
{
    private static readonly TimeSpan SettlementLimit = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly ChatApiOptions _options;
    private readonly ChatPersistencePorts _stores;
    private readonly ChatToolRegistry _tools;
    private readonly IChatProfilePolicy _policy;
    private readonly AiRoundClient _client;
    private readonly IChatTurnStore _signalingTurns;
    private readonly Dictionary<AiProfileKey, ChatTurnOrchestrator> _orchestrators = [];
    private readonly ConcurrentDictionary<ChatTurnKey, ChatTurnExecution> _live = new();
    private readonly ConcurrentDictionary<ChatTurnKey, DateTimeOffset> _finished = new();
    private readonly Dictionary<ChatTurnKey, ChangeSignal> _signals = [];
    private readonly List<ChatTurnExecution> _retained = [];
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _disposal;

    internal ChatTurnHost(ChatApiOptions options, ChatPersistencePorts stores, IAiCredentialResolver credentials,
        ChatToolRegistry tools, IChatProfilePolicy policy, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(time);
        if (!options.Enabled) throw new InvalidOperationException("Chat is not configured.");
        _options = options;
        _stores = stores;
        _tools = tools;
        _policy = policy;
        Clock = time;
        _client = new AiRoundClient(AiProfileCatalog.Create(options.Profiles.Select(profile => profile.Spec)), credentials, time);
        _signalingTurns = new SignalingTurnStore(stores.WriterTurns, this);
        InstanceId = Guid.NewGuid();
        StartedAtUtc = Now;
    }

    internal TimeProvider Clock { get; }
    internal Guid InstanceId { get; }
    internal DateTimeOffset StartedAtUtc { get; }
    internal bool ToolsEnabled => !_tools.Definitions.IsEmpty;

    /// <summary>Tools are offered only to profiles that advertise function tools; others run text-only turns.</summary>
    internal ChatToolRegistry Tools(ChatModelProfile profile) =>
        (profile.Spec.Capabilities & AiCapability.FunctionTools) != 0 ? _tools : ChatToolRegistry.Empty;

    internal ChatGroundingPolicyPin GroundingPolicy(ChatModelProfile profile) => ChatGrounding.Pin(Tools(profile));
    internal ChatPersistencePorts Stores => _stores;
    internal IChatTurnStore WriterTurns => _signalingTurns;

    /// <summary>Microsecond UTC instant, matching the ledger's precision.</summary>
    internal DateTimeOffset Now
    {
        get
        {
            var now = Clock.GetUtcNow().ToUniversalTime();
            return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
        }
    }

    /// <summary>Null when this process is stopping or already runs the same turn.</summary>
    internal ChatTurnExecution? Begin(ActorContext actor, ChatConversationKey key, ChatModelProfile profile,
        ChatTurnIntent intent, IReadOnlyList<Guid> history)
    {
        var turn = ChatTurnKey.Of(key, intent.TurnId);
        ChatTurnOrchestrator orchestrator;
        lock (_gate)
        {
            if (_disposal is not null || _live.ContainsKey(turn)) return null;
            if (!_orchestrators.TryGetValue(profile.Spec.Key, out orchestrator!))
            {
                var tools = Tools(profile);
                orchestrator = new ChatTurnOrchestrator(_stores.WriterConversations, _signalingTurns, _client, profile.Spec,
                    ChatGrounding.Instructions, tools.Definitions, _policy, tools.Port, Clock, InstanceId, ChatGrounding.Limits);
                _orchestrators.Add(profile.Spec.Key, orchestrator);
            }
            // Registration precedes any ledger write, so this process never mistakes its own turn for an abandoned one.
            var execution = orchestrator.Begin(actor, key, intent, history, _shutdown.Token);
            _live[turn] = execution;
            _ = SettleAsync(turn, execution, intent.DeadlineUtc);
            return execution;
        }
    }

    internal ChatTurnExecution? Live(ChatConversationKey key, Guid turnId) =>
        _live.TryGetValue(ChatTurnKey.Of(key, turnId), out var execution) ? execution : null;

    /// <summary>
    /// An active turn no execution in this process owns, created before this process, past its deadline, or
    /// whose execution here ended without an acknowledged terminal. Closing it never resends provider work.
    /// </summary>
    internal bool Abandoned(ChatConversationKey key, ChatTurnFacts turn)
    {
        if (turn.Phase != ChatTurnPhase.Active) return false;
        var id = ChatTurnKey.Of(key, turn.Intent.TurnId);
        if (_live.ContainsKey(id)) return false;
        return turn.CreatedAtUtc < StartedAtUtc || Now >= turn.Intent.DeadlineUtc || _finished.ContainsKey(id);
    }

    internal void Pulse(ChatConversationKey key, Guid turnId) => Pulse(ChatTurnKey.Of(key, turnId));

    private void Pulse(ChatTurnKey turn)
    {
        TaskCompletionSource? previous = null;
        lock (_signals)
            if (_signals.TryGetValue(turn, out var signal))
            {
                previous = signal.Source;
                signal.Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        previous?.TrySetResult();
    }

    /// <summary>A follower's subscription to this process's ledger writes for one turn; dispose to release it.</summary>
    internal ChangeLease Follow(ChatConversationKey key, Guid turnId)
    {
        var turn = ChatTurnKey.Of(key, turnId);
        lock (_signals)
        {
            if (!_signals.TryGetValue(turn, out var signal)) _signals.Add(turn, signal = new());
            signal.Followers++;
            return new(this, turn, signal);
        }
    }

    /// <summary>Waits for a signal taken before the last read, or the bounded interval.</summary>
    internal async Task WaitForChangeAsync(Task signal, TimeSpan interval, CancellationToken ct)
    {
        try { await signal.WaitAsync(interval, Clock, ct).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    internal sealed class ChangeSignal
    {
        internal TaskCompletionSource Source { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Followers { get; set; }
    }

    internal sealed class ChangeLease(ChatTurnHost host, ChatTurnKey turn, ChangeSignal signal) : IDisposable
    {
        private bool _disposed;
        /// <summary>Take before each read; it completes at the next write after it was taken.</summary>
        internal Task Changed { get { lock (host._signals) return signal.Source.Task; } }
        public void Dispose()
        {
            lock (host._signals)
            {
                if (_disposed) return;
                _disposed = true;
                if (--signal.Followers == 0) host._signals.Remove(turn);
            }
        }
    }

    private async Task SettleAsync(ChatTurnKey turn, ChatTurnExecution execution, DateTimeOffset deadline)
    {
        try { await execution.Completion.ConfigureAwait(false); }
        catch (Exception error) when (ChatTurnExecutionFailure.Recoverable(error)) { }
        lock (_gate)
        {
            if (!execution.ReleaseSafe) _retained.Add(execution); // keep its borrowed client scope for shutdown
            _finished[turn] = deadline;
            _live.TryRemove(new KeyValuePair<ChatTurnKey, ChatTurnExecution>(turn, execution));
            var now = Now;
            foreach (var item in _finished)
                if (item.Value < now) _finished.TryRemove(item);
        }
        Pulse(turn);
    }

    /// <summary>Cancels live turns, joins every original execution, then disposes the client only when all released.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate) _disposal ??= DisposeCoreAsync();
        return new ValueTask(_disposal);
    }

    private async Task DisposeCoreAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        ChatTurnExecution[] executions;
        lock (_gate) executions = [.. _live.Values, .. _retained];
        var settled = true;
        foreach (var execution in executions)
        {
            try { await execution.Completion.WaitAsync(SettlementLimit).ConfigureAwait(false); }
            catch (Exception error) when (ChatTurnExecutionFailure.Recoverable(error)) { settled = false; }
            if (!execution.ReleaseSafe) settled = false;
        }
        lock (_signals)
            foreach (var signal in _signals.Values) signal.Source.TrySetResult();
        // A failed or unproven execution retains the shared client rather than releasing it under live work.
        if (!settled) throw new InvalidOperationException("Chat turn cleanup could not be confirmed.");
        await _client.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    public override string ToString() => nameof(ChatTurnHost);

    private sealed class SignalingTurnStore(IChatTurnStore inner, ChatTurnHost host) : IChatTurnStore
    {
        public async Task<ChatTurnWriteResult> StartAsync(ActorContext actor, ChatConversationKey key, ChatTurnIntent intent, CancellationToken ct = default) =>
            Signal(key, intent.TurnId, await inner.StartAsync(actor, key, intent, ct).ConfigureAwait(false));
        public Task<ChatTurnFacts?> ReadTurnAsync(ActorContext actor, ChatConversationKey key, Guid turnId, CancellationToken ct = default) =>
            inner.ReadTurnAsync(actor, key, turnId, ct);
        public Task<IReadOnlyList<ChatStoredEvent>> ReadEventsAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
            long afterSequence, int limit, CancellationToken ct = default) => inner.ReadEventsAsync(actor, key, turnId, afterSequence, limit, ct);
        public Task<ChatRoundFacts?> ReadRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId, Guid roundId, CancellationToken ct = default) =>
            inner.ReadRoundAsync(actor, key, turnId, roundId, ct);
        public Task<ChatRoundWriteResult> ReserveRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
            long expectedVersion, ChatRoundIntent intent, CancellationToken ct = default) =>
            inner.ReserveRoundAsync(actor, key, turnId, expectedVersion, intent, ct);
        public async Task<ChatTurnWriteResult> CompleteRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
            long expectedVersion, Guid roundId, Guid fence, ChatRoundOutcome outcome, CancellationToken ct = default) =>
            Signal(key, turnId, await inner.CompleteRoundAsync(actor, key, turnId, expectedVersion, roundId, fence, outcome, ct).ConfigureAwait(false));
        public async Task<ChatTurnWriteResult> CompleteRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
            long expectedVersion, Guid roundId, Guid fence, ChatRoundCompletion completion, CancellationToken ct = default) =>
            Signal(key, turnId, await inner.CompleteRoundAsync(actor, key, turnId, expectedVersion, roundId, fence, completion, ct).ConfigureAwait(false));
        public async Task<ChatTurnWriteResult> AppendAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
            long expectedVersion, Guid roundId, Guid fence, ChatEventData data, CancellationToken ct = default) =>
            Signal(key, turnId, await inner.AppendAsync(actor, key, turnId, expectedVersion, roundId, fence, data, ct).ConfigureAwait(false));
        public async Task<ChatTurnWriteResult> AppendCitationAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
            long expectedVersion, Guid roundId, Guid fence, ChatCitationEventData data, CancellationToken ct = default) =>
            Signal(key, turnId, await inner.AppendCitationAsync(actor, key, turnId, expectedVersion, roundId, fence, data, ct).ConfigureAwait(false));
        public async Task<ChatTurnWriteResult> FinishAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
            long expectedVersion, ChatTurnPhase phase, CancellationToken ct = default) =>
            Signal(key, turnId, await inner.FinishAsync(actor, key, turnId, expectedVersion, phase, ct).ConfigureAwait(false));
        public async Task<ChatTurnWriteResult> FinishWithMetadataAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
            long expectedVersion, ChatTurnPhase phase, ChatTerminalMetadata? metadata, CancellationToken ct = default) =>
            Signal(key, turnId, await inner.FinishWithMetadataAsync(actor, key, turnId, expectedVersion, phase, metadata, ct).ConfigureAwait(false));

        private ChatTurnWriteResult Signal(ChatConversationKey key, Guid turnId, ChatTurnWriteResult result)
        {
            if (result.Outcome == ChatStoreOutcome.Applied) host.Pulse(key, turnId);
            return result;
        }

        public override string ToString() => nameof(SignalingTurnStore);
    }
}

internal static class ChatTurnExecutionFailure
{
    internal static bool Recoverable(Exception error) => error is not (OutOfMemoryException or StackOverflowException);
}
