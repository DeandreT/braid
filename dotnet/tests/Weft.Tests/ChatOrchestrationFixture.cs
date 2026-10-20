using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Weft.Ai;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

// Public-contract fixture only. These are ports, not alternate application/provider
// models. Ledger decisions use the real #124 policy and all facts are real types.
internal sealed class ChatOrchestrationFixture : IAsyncDisposable
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 17, 1, 0, 0, TimeSpan.Zero);
    internal const string Instructions = "Trusted mapping explanation only.";
    internal static readonly AiToolDefinition[] Tools = [AiToolDefinition.Create("read_mapping", "Read a pinned field.",
        AiJsonObject.CopyFrom("{\"type\":\"object\",\"properties\":{\"field\":{\"type\":\"string\"}},\"required\":[\"field\"],\"additionalProperties\":false}"))];
    internal readonly TraceLog Trace = new();
    internal readonly AiHttpFixture Server;
    internal readonly Ledger Store;
    internal readonly Policy ProfilePolicy;
    internal readonly ToolPort Port;
    internal readonly AiProfileSpec Profile;
    internal readonly AiRoundClient Client;
    internal readonly Resolver Credentials;
    internal readonly Clock ApplicationClock;
    internal readonly Guid InstanceId = Guid.NewGuid();
    internal ActorContext Actor { get; } = new(new PrincipalId(Guid.Parse("12900000-0000-4000-8000-000000000001")));
    internal ChatConversationIntent Conversation { get; }
    private readonly List<ChatTurnExecution> executions = [];
    private readonly List<Action> releases = [];
    private readonly List<AiTurnLease> idle = [];
    internal bool ExpectCleanupFault;
    internal Task? ExpectedClientCleanupFailure;

    internal ChatOrchestrationFixture(Func<AiHttpFixture.Exchange, CancellationToken, Task>? script = null,
        Resolver? credentials = null, TimeProvider? providerClock = null, Clock? applicationClock = null,
        bool metadataAllowed = true)
    {
        Server = new(async (exchange, ct) =>
        {
            Trace.Add("wire.request", exchange.Ordinal);
            if (script is null) await exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(Text("answer") + Finish("stop") + Done), ct);
            else await script(exchange, ct);
        });
        ApplicationClock = applicationClock ?? new Clock();
        Profile = AiProfileSpec.Create(AiProfileKey.Create("orchestration-fixture", 1), AiDialect.ChatCompletionsSseV1,
            "fixture-model", Server.Endpoint("/v1/chat/completions"), true, AiAuthentication.Bearer, "fixture-reference",
            AiCapability.Text | AiCapability.FunctionTools, "mapping-metadata", 1, metadataAllowed, false);
        Credentials = credentials ?? new Resolver();
        Client = new(AiProfileCatalog.Create([Profile]), Credentials, providerClock ?? new Clock());
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var digest = Sha256Digest.FromBytes([1, 2, 9]);
        Conversation = new(new(scope, Actor.PrincipalId, Guid.NewGuid()),
            new(Guid.NewGuid(), Guid.NewGuid(), digest, digest), new(Guid.NewGuid(), Guid.NewGuid(), digest, digest), Now.AddDays(1));
        Store = new(Conversation, Actor, Profile, Trace);
        ProfilePolicy = new(Trace);
        Port = new(Trace);
    }

    internal ChatTurnIntent Intent(string text = "PRIVATE question", int rounds = 3, long outputBytes = 131072,
        int calls = 8, int nodes = 64, int resultBytes = 8192, int iterations = 3, int events = 256,
        int eventBytes = 262144, long requestBytes = 1048576, long durationTicks = 600_000_000)
    {
        var budget = new ChatProviderBudget(durationTicks, rounds, requestBytes, 4194304, outputBytes, calls, 128);
        var grounding = ChatAiBridge.PinGroundingPolicy("fixture-grounding", 1, Instructions, Tools, nodes, resultBytes, iterations);
        return new(Guid.NewGuid(), new(Guid.NewGuid(), text, [], budget, grounding), ChatAiBridge.PinProfile("fixture", Profile),
            new(events, eventBytes, rounds), Now.AddTicks(durationTicks));
    }

    internal ChatTurnExecution Begin(ChatTurnIntent? intent = null, IEnumerable<Guid>? history = null,
        CancellationToken ct = default, ChatOrchestrationLimits? limits = null, ActorContext? actor = null)
    {
        var orchestrator = new ChatTurnOrchestrator(Store, Store, Client, Profile, Instructions, Tools, ProfilePolicy, Port,
            ApplicationClock, InstanceId, limits ?? new(8, 4096, 65536, 131072));
        var result = orchestrator.Begin(actor ?? Actor, Conversation.Key, intent ?? Intent(), history ?? [], ct);
        executions.Add(result);
        return result;
    }

    internal AsyncGate Gate()
    {
        var gate = new AsyncGate(); releases.Add(gate.Open); return gate;
    }
    internal void ReleaseWith(Action action) => releases.Add(action);
    internal static Task Bound(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    internal static Task<T> Bound<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    internal static async Task<ChatOrchestrationResult> FinishExecution(ChatTurnExecution execution) => await Bound(execution.Completion);
    internal void FillOtherCapacity()
    {
        for (var i = 0; i < 31; i++) idle.Add(Assert.IsType<TurnOpened>(Client.OpenTurn(Profile.Key, AiTurnLimits.Default, CancellationToken.None)).Turn);
    }
    internal void AssertBusy()
    {
        var rejected = Assert.IsType<TurnRejected>(Client.OpenTurn(Profile.Key, AiTurnLimits.Default, CancellationToken.None));
        Assert.Equal(AiFailureCode.Busy, rejected.Failure.Code);
        Assert.Equal(AiDispatchState.NotAttempted, rejected.Failure.Dispatch);
    }
    internal async Task AssertOneCapacityRecovered()
    {
        await using var recovered = Assert.IsType<TurnOpened>(Client.OpenTurn(Profile.Key, AiTurnLimits.Default, CancellationToken.None)).Turn;
        AssertBusy();
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var release in releases) release();
        foreach (var execution in executions)
        {
            try { await Bound(execution.CancelAsync()); }
            catch (ChatOrchestrationCleanupException error)
            {
                Assert.False(execution.ReleaseSafe); Assert.Null(error.InnerException);
                Assert.Equal("Chat cleanup remains unproven.", error.Message);
            }
            await Bound(execution.Completion);
        }
        foreach (var lease in idle) await lease.DisposeAsync();
        var clientCleanup = Client.DisposeAsync().AsTask();
        try
        {
            if (ExpectedClientCleanupFailure is { } cached) Assert.Same(cached, clientCleanup);
            if (ExpectCleanupFault)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Bound(clientCleanup));
                AssertProviderCleanupFailure(error);
            }
            else await Bound(clientCleanup);
        }
        finally { Credentials.Dispose(); await Server.DisposeAsync(); }
    }

    internal static void AssertProviderCleanupFailure(InvalidOperationException error)
    { Assert.Equal("AI cleanup did not complete.", error.Message); Assert.Null(error.InnerException); }

    internal const string Done = "data: [DONE]\n\n";
    internal static string Data(string json) => "data: " + json + "\n\n";
    internal static string Text(string value) => Data(JsonSerializer.Serialize(new
    {
        choices = new[] {
        new { index = 0, delta = new { role = "assistant", content = value }, finish_reason = (string?)null } }
    }));
    internal static string Finish(string reason) => Data(JsonSerializer.Serialize(new
    {
        choices = new[] {
        new { index = 0, delta = new { }, finish_reason = reason } }
    }));
    internal static string Call(int index, string id, string arguments = "{\"field\":\"same\"}", string name = "read_mapping") =>
        Data(JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0,
            delta = new { tool_calls = new[] { new { index, id, type = "function", function = new { name, arguments } } } },
            finish_reason = (string?)null } }
        }));
    internal static string Usage(long? input, long? output, long? total) => Data(JsonSerializer.Serialize(new
    {
        choices = Array.Empty<object>(),
        usage = new { prompt_tokens = input, completion_tokens = output, total_tokens = total }
    }));

    internal sealed class AsyncGate
    {
        internal TaskCompletionSource Entered { get; } = AiHttpFixture.NewSignal();
        internal TaskCompletionSource Released { get; } = AiHttpFixture.NewSignal();
        internal TaskCompletionSource Returned { get; } = AiHttpFixture.NewSignal();
        internal async Task Hold()
        {
            Entered.TrySetResult();
            await Released.Task; // Intentionally noncooperative; this IS the original port task.
            Returned.TrySetResult();
        }
        internal void HoldSync() { Entered.TrySetResult(); Released.Task.GetAwaiter().GetResult(); Returned.TrySetResult(); }
        internal void Open() => Released.TrySetResult();
    }

    internal sealed record Mark(long Sequence, string Kind, int Ordinal, string? CallId);
    internal sealed class TraceLog
    {
        private readonly object sync = new();
        private readonly List<Mark> values = [];
        internal void Add(string kind, int ordinal = 0, string? callId = null)
        { lock (sync) values.Add(new(values.Count + 1, kind, ordinal, callId)); }
        internal Mark[] Snapshot { get { lock (sync) return values.ToArray(); } }
    }

    internal sealed class Policy(TraceLog trace) : IChatProfilePolicy
    {
        internal Func<ActorContext, ChatConversationIntent, ChatTurnIntent, AiProfileSpec, CancellationToken, Task<bool>>? Check;
        internal int Calls;
        public Task<bool> AllowsAsync(ActorContext actor, ChatConversationIntent conversation, ChatTurnIntent turn, AiProfileSpec profile, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); trace.Add("policy");
            return Check?.Invoke(actor, conversation, turn, profile, ct) ?? Task.FromResult(true);
        }
    }

    internal sealed class ToolPort(TraceLog trace) : IChatReadOnlyToolPort
    {
        internal readonly ConcurrentQueue<(ChatReadOnlyToolContext Context, AiToolIntent Intent)> Validations = new();
        internal readonly ConcurrentQueue<(ChatReadOnlyToolContext Context, AiToolIntent Intent)> Executions = new();
        internal readonly ConcurrentQueue<Task<ChatReadOnlyToolResult>> OriginalExecutions = new();
        internal readonly TaskCompletionSource<Task<ChatReadOnlyToolResult>> FirstOriginalExecution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<ChatReadOnlyToolContext, AiToolIntent, CancellationToken, Task<bool>>? Validate;
        internal Func<ChatReadOnlyToolContext, AiToolIntent, CancellationToken, Task<ChatReadOnlyToolResult>>? Execute;
        public async Task<bool> ValidateAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct)
        {
            Validations.Enqueue((context, intent)); trace.Add("tool.validate", context.Ordinal, intent.CallId);
            if (Validate is not null) return await Validate(context, intent, ct);
            using var json = JsonDocument.Parse(intent.Arguments.Json);
            return intent.Name == "read_mapping" && json.RootElement.EnumerateObject().Count() == 1 &&
                json.RootElement.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.String;
        }
        public Task<ChatReadOnlyToolResult> ExecuteAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct)
        {
            var original = ExecuteOriginalAsync(context, intent, ct); OriginalExecutions.Enqueue(original); FirstOriginalExecution.TrySetResult(original); return original;
        }
        private async Task<ChatReadOnlyToolResult> ExecuteOriginalAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct)
        {
            Executions.Enqueue((context, intent)); trace.Add("tool.execute", context.Ordinal, intent.CallId);
            ChatReadOnlyToolResult result = Execute is null ? new(AiJsonObject.CopyFrom("{\"value\":1}"), 1) : await Execute(context, intent, ct);
            trace.Add("tool.return", context.Ordinal, intent.CallId); return result;
        }
    }

    internal sealed class Resolver : IAiCredentialResolver, IDisposable
    {
        internal Func<CancellationToken, Task>? Opening;
        internal Action? OnCancellation;
        internal int Calls, Callbacks;
        internal readonly ConcurrentQueue<Task<AiCredentialResult>> OriginalOpenings = new();
        internal readonly TaskCompletionSource<Task<AiCredentialResult>> FirstOriginalOpening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<CancellationTokenRegistration> registrations = new();
        public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct)
        {
            var original = ResolveOriginalAsync(ct); OriginalOpenings.Enqueue(original); FirstOriginalOpening.TrySetResult(original); return new(original);
        }
        private async Task<AiCredentialResult> ResolveOriginalAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (OnCancellation is { } callback) registrations.Enqueue(ct.Register(() => { Interlocked.Increment(ref Callbacks); callback(); }));
            if (Opening is not null) await Opening(ct);
            // Synthetic token, never a credential-store or environment dependency.
            return CredentialFound.Create(AiBearerSecret.CopyFrom("synthetic129token"));
        }
        public void Dispose() { while (registrations.TryDequeue(out var registration)) registration.Dispose(); }
        public override string ToString() => nameof(Resolver);
    }

    // Inject separately into application vs provider to avoid relying on a private
    // timer ordinal or duration. Fire only the publicly created application timer.
    internal sealed class Clock : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<OwnedTimer> timers = [];
        private long ticks;
        internal AsyncGate? AsyncDisposal;
        internal Action? SynchronousDisposal;
        internal Action? TimerCreation;
        internal int TimerCreations;
        internal AsyncGate? TimestampGate;
        internal bool ThrowTimestamp, ThrowAsyncDisposal;
        internal readonly ConcurrentQueue<Task> OriginalDisposals = new();
        internal readonly TaskCompletionSource<Task> FirstOriginalDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (sync) return Now.AddTicks(ticks); }
        public override long GetTimestamp()
        {
            Interlocked.Exchange(ref TimestampGate, null)?.HoldSync();
            if (ThrowTimestamp) { ThrowTimestamp = false; throw new InvalidOperationException("PRIVATE initialization error"); }
            lock (sync) return ticks;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref TimerCreations);
            Interlocked.Exchange(ref TimerCreation, null)?.Invoke();
            var timer = new OwnedTimer(this, callback, state); lock (sync) timers.Add(timer); return timer;
        }
        internal void Fire(TimeSpan elapsed)
        {
            OwnedTimer[] snapshot;
            lock (sync) { ticks += elapsed.Ticks; snapshot = timers.ToArray(); }
            foreach (var timer in snapshot) timer.Fire();
        }
        private sealed class OwnedTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            internal void Fire() { if (!disposed) callback(state); }
            public void Dispose() { clock.SynchronousDisposal?.Invoke(); disposed = true; lock (clock.sync) clock.timers.Remove(this); }
            public ValueTask DisposeAsync()
            {
                var original = DisposeOriginalAsync(); clock.OriginalDisposals.Enqueue(original); clock.FirstOriginalDisposal.TrySetResult(original); return new(original);
            }
            private async Task DisposeOriginalAsync()
            {
                if (clock.AsyncDisposal is { } gate) await gate.Hold();
                if (clock.ThrowAsyncDisposal) throw new InvalidOperationException("PRIVATE asynchronous timer cleanup error");
                Dispose();
            }
        }
    }

    internal sealed record StoreCall(string Method, Guid TurnId, long Version, Guid? RoundId = null,
        Guid? Fence = null, ChatRoundIntent? Reservation = null, ChatEventData? Event = null, int PageLimit = 0);

    internal sealed class Ledger(ChatConversationIntent conversation, ActorContext owner, AiProfileSpec profile, TraceLog trace) : IChatConversationStore, IChatTurnStore
    {
        private readonly object sync = new();
        private readonly Dictionary<Guid, ChatTurnFacts> turns = [];
        private readonly Dictionary<Guid, List<ChatRoundFacts>> rounds = [];
        private readonly Dictionary<Guid, List<ChatStoredEvent>> events = [];
        internal readonly ConcurrentQueue<StoreCall> Calls = new();
        internal readonly ConcurrentQueue<(Guid TurnId, long AfterSequence, int Limit, int Returned)> HistoryPages = new();
        internal readonly TaskCompletionSource<Task<ChatTurnWriteResult>> FirstOriginalAppend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<StoreCall, Task>? Returning;
        internal Func<ChatTurnWriteResult, ChatTurnWriteResult>? StartReply;
        internal Func<ChatRoundWriteResult, ChatRoundWriteResult>? ReserveReply;
        internal Func<ChatTurnFacts?, ChatTurnFacts?>? TurnRead;
        internal Func<ChatRoundFacts?, ChatRoundFacts?>? RoundRead;
        internal Func<Guid, IReadOnlyList<ChatStoredEvent>, IReadOnlyList<ChatStoredEvent>>? EventsRead;
        internal bool Authorized = true;
        internal ChatTurnFacts Facts(Guid id) { lock (sync) return turns[id]; }
        internal ChatStoredEvent[] Events(Guid id) { lock (sync) return events.GetValueOrDefault(id, []).ToArray(); }
        internal ChatRoundFacts[] Rounds(Guid id) { lock (sync) return rounds.GetValueOrDefault(id, []).ToArray(); }
        internal int Count(string method) => Calls.Count(call => call.Method == method);
        private bool Allowed(ActorContext actor, ChatConversationKey key) => Authorized && actor.PrincipalId == owner.PrincipalId && key == conversation.Key;
        private void Record(StoreCall call) { Calls.Enqueue(call); trace.Add("store." + call.Method); }
        private async Task Returned(StoreCall call)
        { if (Returning is not null) await Returning(call); trace.Add("ack." + call.Method); }
        public Task<ChatStoreOutcome> CreateAsync(ActorContext actor, ChatConversationIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("Conversations are pre-created fixture inputs.");
        public Task<ChatConversationIntent?> ReadAsync(ActorContext actor, ChatConversationKey key, CancellationToken ct = default)
        { trace.Add("store.conversation-read"); return Task.FromResult(Allowed(actor, key) ? conversation : null); }
        public Task<ChatStoreOutcome> DeleteExpiredAsync(ActorContext actor, ChatConversationKey key, ChatErasurePolicy policy, CancellationToken ct = default) =>
            throw new InvalidOperationException("Erasure is not orchestration authority.");
        public async Task<ChatTurnWriteResult> StartAsync(ActorContext actor, ChatConversationKey key, ChatTurnIntent intent, CancellationToken ct = default)
        {
            var call = new StoreCall("start", intent.TurnId, 0); Record(call); ChatTurnWriteResult result;
            lock (sync)
            {
                if (!Allowed(actor, key)) result = new(ChatStoreOutcome.NotAuthorizedOrNotFound);
                else if (turns.TryGetValue(intent.TurnId, out var existing)) result = new(existing.Intent == intent ? ChatStoreOutcome.Replayed : ChatStoreOutcome.Conflict, existing);
                else if (turns.Values.Any(t => t.Phase == ChatTurnPhase.Active)) result = new(ChatStoreOutcome.Conflict);
                else
                {
                    var created = new ChatTurnFacts(intent, ChatTurnPhase.Active, 1, 1, 0, 0, Now, null);
                    turns.Add(intent.TurnId, created); rounds.Add(intent.TurnId, []);
                    events.Add(intent.TurnId, [new(1, Guid.NewGuid(), ChatEventKind.Started, "", null)]);
                    result = new(ChatStoreOutcome.Applied, created);
                }
            }
            await Returned(call); return StartReply?.Invoke(result) ?? result;
        }
        public Task<ChatTurnFacts?> ReadTurnAsync(ActorContext actor, ChatConversationKey key, Guid turnId, CancellationToken ct = default)
        {
            Record(new("read-turn", turnId, 0)); ChatTurnFacts? result;
            lock (sync) result = Allowed(actor, key) ? turns.GetValueOrDefault(turnId) : null;
            return Task.FromResult(TurnRead is null ? result : TurnRead(result));
        }
        public Task<IReadOnlyList<ChatStoredEvent>> ReadEventsAsync(ActorContext actor, ChatConversationKey key, Guid turnId, long afterSequence, int limit, CancellationToken ct = default)
        {
            Record(new("read-events", turnId, afterSequence, PageLimit: limit));
            if (afterSequence is < 0 or > 4096 || limit is < 1 or > 100) throw ChatBounds.Invalid();
            IReadOnlyList<ChatStoredEvent> page;
            lock (sync) page = Allowed(actor, key) ? events.GetValueOrDefault(turnId, []).Where(e => e.Sequence > afterSequence).Take(limit).ToArray() : [];
            var returned = EventsRead is null ? page : EventsRead(turnId, page);
            HistoryPages.Enqueue((turnId, afterSequence, limit, returned.Count));
            return Task.FromResult(returned);
        }
        public Task<ChatRoundFacts?> ReadRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId, Guid roundId, CancellationToken ct = default)
        {
            Record(new("read-round", turnId, 0, roundId)); ChatRoundFacts? result;
            lock (sync) result = Allowed(actor, key) ? rounds.GetValueOrDefault(turnId, []).SingleOrDefault(r => r.Intent.RoundId == roundId) : null;
            return Task.FromResult(RoundRead is null ? result : RoundRead(result));
        }
        public async Task<ChatRoundWriteResult> ReserveRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId, long expectedVersion, ChatRoundIntent intent, CancellationToken ct = default)
        {
            var call = new StoreCall("reserve", turnId, expectedVersion, intent.RoundId, intent.Fence, intent); Record(call); ChatRoundWriteResult result;
            lock (sync)
            {
                if (!Allowed(actor, key) || !turns.TryGetValue(turnId, out var turn)) result = new(ChatStoreOutcome.NotAuthorizedOrNotFound);
                else
                {
                    var last = rounds[turnId].LastOrDefault();
                    var outcome = ChatTurnPolicy.Reserve(turn, last, expectedVersion, Now, intent.InstanceId);
                    if (outcome != ChatStoreOutcome.Applied) result = new(outcome, turn);
                    else
                    {
                        var next = Copy(turn, version: turn.Version + 1, roundCount: turn.RoundCount + 1);
                        var round = new ChatRoundFacts(intent, next.RoundCount, ChatRoundOutcome.Uncertain);
                        turns[turnId] = next; rounds[turnId].Add(round); result = new(ChatStoreOutcome.Applied, next, round);
                    }
                }
            }
            await Returned(call); return ReserveReply?.Invoke(result) ?? result;
        }
        public Task<ChatTurnWriteResult> CompleteRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId, long expectedVersion, Guid roundId, Guid fence, ChatRoundOutcome outcome, CancellationToken ct = default) =>
            throw new InvalidOperationException("Orchestration must publish typed provider metadata.");
        public async Task<ChatTurnWriteResult> CompleteRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId, long expectedVersion, Guid roundId, Guid fence, ChatRoundCompletion completion, CancellationToken ct = default)
        {
            var call = new StoreCall("complete-round", turnId, expectedVersion, roundId, fence); Record(call); ChatTurnWriteResult result;
            lock (sync)
            {
                var turn = turns[turnId]; var last = rounds[turnId].Last();
                var outcome = Allowed(actor, key) ? ChatTurnPolicy.CompleteRound(turn, last, expectedVersion, fence, completion.Outcome, Now) : ChatStoreOutcome.NotAuthorizedOrNotFound;
                if (last.Intent.RoundId != roundId) outcome = ChatStoreOutcome.InvalidState;
                if (outcome != ChatStoreOutcome.Applied) result = new(outcome, turn);
                else
                {
                    var body = completion.Metadata is null ? null : ChatMetadataCodec.Terminal(completion.Metadata);
                    var bytes = body is null ? 0 : Encoding.UTF8.GetByteCount(body);
                    if (turn.EventCount >= turn.Intent.Budget.MaxEvents - 1 || bytes > turn.Intent.Budget.MaxEventBytes - turn.EventBytes) result = new(ChatStoreOutcome.LimitReached, turn);
                    else
                    {
                        rounds[turnId][^1] = new(last.Intent, last.Ordinal, completion.Outcome, completion.Metadata);
                        var next = Copy(turn, version: turn.Version + 1, eventCount: turn.EventCount + 1, eventBytes: turn.EventBytes + bytes);
                        turns[turnId] = next; events[turnId].Add(new(next.EventCount, Guid.NewGuid(), ChatEventKind.RoundCompleted, "", roundId, body));
                        result = new(ChatStoreOutcome.Applied, next);
                    }
                }
            }
            await Returned(call); return result;
        }
        public Task<ChatTurnWriteResult> AppendAsync(ActorContext actor, ChatConversationKey key, Guid turnId, long expectedVersion, Guid roundId, Guid fence, ChatEventData data, CancellationToken ct = default)
        {
            var original = AppendOriginalAsync(actor, key, turnId, expectedVersion, roundId, fence, data, ct);
            FirstOriginalAppend.TrySetResult(original); return original;
        }
        private async Task<ChatTurnWriteResult> AppendOriginalAsync(ActorContext actor, ChatConversationKey key, Guid turnId, long expectedVersion, Guid roundId, Guid fence, ChatEventData data, CancellationToken ct)
        {
            var call = new StoreCall("append", turnId, expectedVersion, roundId, fence, Event: data); Record(call); ChatTurnWriteResult result;
            lock (sync)
            {
                var turn = turns[turnId]; var round = rounds[turnId].Last();
                var outcome = Allowed(actor, key) ? ChatTurnPolicy.Append(turn, round, expectedVersion, fence, data, Now) : ChatStoreOutcome.NotAuthorizedOrNotFound;
                if (round.Intent.RoundId != roundId) outcome = ChatStoreOutcome.InvalidState;
                if (outcome != ChatStoreOutcome.Applied) result = new(outcome, turn);
                else
                {
                    var next = Copy(turn, version: turn.Version + 1, eventCount: turn.EventCount + 1, eventBytes: turn.EventBytes + data.ByteCount);
                    turns[turnId] = next; events[turnId].Add(new(next.EventCount, data.EventId, data.Kind, data.Text, roundId, data.MetadataJson));
                    trace.Add("event." + data.Kind); result = new(ChatStoreOutcome.Applied, next);
                }
            }
            await Returned(call); return result;
        }
        public Task<ChatTurnWriteResult> AppendCitationAsync(ActorContext actor, ChatConversationKey key, Guid turnId, long expectedVersion, Guid roundId, Guid fence, ChatCitationEventData data, CancellationToken ct = default) =>
            throw new InvalidOperationException("Mapping citation composition is excluded.");
        public Task<ChatTurnWriteResult> FinishAsync(ActorContext actor, ChatConversationKey key, Guid turnId, long expectedVersion, ChatTurnPhase phase, CancellationToken ct = default) =>
            FinishWithMetadataAsync(actor, key, turnId, expectedVersion, phase, null, ct);
        public async Task<ChatTurnWriteResult> FinishWithMetadataAsync(ActorContext actor, ChatConversationKey key, Guid turnId, long expectedVersion, ChatTurnPhase phase, ChatTerminalMetadata? metadata, CancellationToken ct = default)
        {
            var call = new StoreCall("finish", turnId, expectedVersion); Record(call); ChatTurnWriteResult result;
            lock (sync)
            {
                var turn = turns[turnId]; var last = rounds[turnId].LastOrDefault();
                var outcome = Allowed(actor, key) ? ChatTurnPolicy.Finish(turn, last, expectedVersion, phase, Now) : ChatStoreOutcome.NotAuthorizedOrNotFound;
                if (outcome != ChatStoreOutcome.Applied) result = new(outcome, turn);
                else
                {
                    var terminal = metadata ?? last?.Metadata;
                    var body = terminal is null ? null : ChatMetadataCodec.Terminal(terminal);
                    if (body is not null && Encoding.UTF8.GetByteCount(body) > 2048) throw ChatBounds.Invalid();
                    var next = Copy(turn, version: turn.Version + 1, eventCount: turn.EventCount + 1,
                        eventBytes: turn.EventBytes, phase: phase);
                    turns[turnId] = next;
                    events[turnId].Add(new(next.EventCount, Guid.NewGuid(), phase switch
                    {
                        ChatTurnPhase.Completed => ChatEventKind.Completed,
                        ChatTurnPhase.Failed => ChatEventKind.Failed,
                        ChatTurnPhase.Cancelled => ChatEventKind.Cancelled,
                        _ => ChatEventKind.Interrupted
                    }, "", null, body));
                    result = new(ChatStoreOutcome.Applied, next);
                }
            }
            await Returned(call); return result;
        }
        internal static bool IsTurnTerminal(ChatStoredEvent value) => value.Kind is ChatEventKind.Completed or ChatEventKind.Failed or ChatEventKind.Cancelled or ChatEventKind.Interrupted;
        internal static int CountedEventBytes(ChatStoredEvent value) => IsTurnTerminal(value) ? 0 :
            Encoding.UTF8.GetByteCount(value.Text) + (value.MetadataJson is null ? 0 : Encoding.UTF8.GetByteCount(value.MetadataJson));
        internal void SeedHistory(ChatTurnIntent intent, DateTimeOffset created, string finalText, string intermediate = "PRIVATE intermediate", IReadOnlyList<string>? finalTextParts = null)
        {
            var parts = finalTextParts ?? [finalText];
            if (parts.Count == 0 || string.Concat(parts) != finalText) throw new InvalidOperationException("Invalid authored history pieces.");
            var instance = Guid.NewGuid();
            var firstRequest = AiRoundRequest.Create(Guid.NewGuid(), [AiSystemMessage.Create(Instructions), AiUserMessage.Create(intent.OriginalRequest)], Tools, 128);
            var first = ChatAiBridge.PrepareRound(conversation, intent, profile, intent.Envelope.GroundingPolicy, firstRequest, Guid.NewGuid(), instance).Intent;
            var call = AiToolIntent.Create("seed-call", "read_mapping", AiJsonObject.CopyFrom("{\"field\":\"same\"}"));
            var result = AiJsonObject.CopyFrom("{\"value\":1}");
            var finalRequest = AiRoundRequest.Create(Guid.NewGuid(), [AiSystemMessage.Create(Instructions), AiUserMessage.Create(intent.OriginalRequest),
                AiAssistantMessage.Create(intermediate, [call]), AiToolMessage.Create(call.CallId, call.Name, result)], Tools, 128);
            var round = ChatAiBridge.PrepareRound(conversation, intent, profile, intent.Envelope.GroundingPolicy, finalRequest, Guid.NewGuid(), instance).Intent;
            var toolTerminal = new ChatTerminalMetadata(new(first.RoundId, ChatUsageOrigin.ProviderRoundTerminal, null, null, null), finishReason: ChatProviderFinishReason.ToolCalls);
            var terminal = new ChatTerminalMetadata(new(round.RoundId, ChatUsageOrigin.ProviderRoundTerminal, null, null, null), finishReason: ChatProviderFinishReason.Stop);
            var prefix = JsonSerializer.Serialize(new { v = 1, text = intermediate });
            var started = JsonSerializer.Serialize(new { v = 1, call = call.CallId, name = call.Name, ordinal = 1 });
            var completed = JsonSerializer.Serialize(new { v = 1, call = call.CallId, name = call.Name, ordinal = 1, bytes = result.ByteCount, nodes = 1 });
            lock (sync)
            {
                var stored = new List<ChatStoredEvent> { new(1, Guid.NewGuid(), ChatEventKind.Started, "", null),
                    new(2, Guid.NewGuid(), ChatEventKind.TextDelta, prefix, first.RoundId),
                    new(3, Guid.NewGuid(), ChatEventKind.ToolStarted, started, first.RoundId),
                    new(4, Guid.NewGuid(), ChatEventKind.ToolCompleted, completed, first.RoundId),
                    new(5, Guid.NewGuid(), ChatEventKind.RoundCompleted, "", first.RoundId, ChatMetadataCodec.Terminal(toolTerminal)) };
                foreach (var part in parts) stored.Add(new(stored.Count + 1, Guid.NewGuid(), ChatEventKind.TextDelta,
                    JsonSerializer.Serialize(new { v = 1, text = part }), round.RoundId));
                stored.Add(new(stored.Count + 1, Guid.NewGuid(), ChatEventKind.RoundCompleted, "", round.RoundId, ChatMetadataCodec.Terminal(terminal)));
                stored.Add(new(stored.Count + 1, Guid.NewGuid(), ChatEventKind.Completed, "", null, ChatMetadataCodec.Terminal(terminal)));
                events.Add(intent.TurnId, stored);
                turns.Add(intent.TurnId, new(intent, ChatTurnPhase.Completed, stored.Count + 2, stored.Count,
                    stored.Sum(CountedEventBytes), 2, created, created.AddSeconds(1)));
                rounds.Add(intent.TurnId, [new(first, 1, ChatRoundOutcome.ToolCalls, toolTerminal), new(round, 2, ChatRoundOutcome.Stopped, terminal)]);
            }
        }
        internal static ChatTurnFacts Copy(ChatTurnFacts t, long? version = null, int? eventCount = null, int? eventBytes = null,
            int? roundCount = null, ChatTurnPhase? phase = null, ChatTurnIntent? intent = null) => new(intent ?? t.Intent, phase ?? t.Phase,
                version ?? t.Version, eventCount ?? t.EventCount, eventBytes ?? t.EventBytes, roundCount ?? t.RoundCount,
                t.CreatedAtUtc, (phase ?? t.Phase) == ChatTurnPhase.Active ? null : t.FinishedAtUtc ?? Now.AddSeconds(1));
    }
}
