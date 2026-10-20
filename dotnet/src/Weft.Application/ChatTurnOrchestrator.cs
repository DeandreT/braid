using System.Collections.Immutable;
using System.Text;
using Weft.Ai;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Creator-private, once-only application orchestration over the real provider and ledger.</summary>
public sealed partial class ChatTurnOrchestrator
{
    private readonly IChatConversationStore _conversations;
    private readonly IChatTurnStore _turns;
    private readonly AiRoundClient _provider;
    private readonly AiProfileSpec _profile;
    private readonly string _instructions;
    private readonly ImmutableArray<AiToolDefinition> _tools;
    private readonly IChatProfilePolicy _profilePolicy;
    private readonly IChatReadOnlyToolPort _toolPort;
    private readonly Guid _instance;
    private readonly ChatOrchestrationLimits _limits;
    internal TimeProvider Clock { get; }
    internal DateTimeOffset Now
    {
        get
        {
            var now = Clock.GetUtcNow().ToUniversalTime();
            return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
        }
    }

    public ChatTurnOrchestrator(IChatConversationStore conversations, IChatTurnStore turns,
        AiRoundClient provider, AiProfileSpec profile, string trustedInstructions,
        IEnumerable<AiToolDefinition> tools, IChatProfilePolicy profilePolicy,
        IChatReadOnlyToolPort toolPort, TimeProvider time, Guid instanceId, ChatOrchestrationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(conversations); ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tools); ArgumentNullException.ThrowIfNull(profilePolicy);
        ArgumentNullException.ThrowIfNull(toolPort); ArgumentNullException.ThrowIfNull(time); ArgumentNullException.ThrowIfNull(limits);
        ChatBounds.Id(instanceId); ChatBounds.Text(trustedInstructions, 65536, false);
        var owned = ImmutableArray.CreateBuilder<AiToolDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var tool in tools)
            {
                if (tool is null || owned.Count == 16 || !names.Add(tool.Name)) throw ChatBounds.Invalid();
                owned.Add(tool);
            }
        }
        catch (Exception e) when (ChatTurnExecution.Recoverable(e)) { throw ChatBounds.Invalid(); }
        _conversations = conversations; _turns = turns; _provider = provider; _profile = profile;
        _instructions = trustedInstructions; _tools = owned.ToImmutable(); _profilePolicy = profilePolicy;
        _toolPort = toolPort; Clock = time; _instance = instanceId; _limits = limits;
    }

    public ChatTurnExecution Begin(ActorContext actor, ChatConversationKey conversation, ChatTurnIntent intent,
        IEnumerable<Guid> historyTurnIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(historyTurnIds);
        var history = new List<Guid>();
        try
        {
            foreach (var id in historyTurnIds)
            {
                ChatBounds.Id(id);
                if (history.Count == _limits.MaxHistoryTurns || id == intent.TurnId || history.Contains(id)) throw ChatBounds.Invalid();
                history.Add(id);
            }
        }
        catch (Exception e) when (ChatTurnExecution.Recoverable(e)) { throw ChatBounds.Invalid(); }
        var execution = new ChatTurnExecution(this, actor, conversation, intent, history.ToArray(), cancellationToken);
        execution.Start();
        return execution;
    }

    internal async Task<ChatOrchestrationResult> ExecuteAsync(ChatTurnExecution execution, ActorContext actor,
        ChatConversationKey key, ChatTurnIntent intent, Guid[] history)
    {
        State? state = null;
        try
        {
            CheckTime(execution, intent);
            if (actor.PrincipalId != key.Creator) throw Stop(ChatOrchestrationOutcome.Denied);
            var conversation = await _conversations.ReadAsync(actor, key, execution.Token).ConfigureAwait(false);
            if (conversation is null || conversation.Key != key) throw Stop(ChatOrchestrationOutcome.Denied);
            ValidateConfiguration(intent);
            if (!await _profilePolicy.AllowsAsync(actor, conversation, intent, _profile, execution.Token).ConfigureAwait(false))
                throw Stop(ChatOrchestrationOutcome.Denied);
            CheckTime(execution, intent);
            var admission = await _turns.StartAsync(actor, key, intent, execution.Token).ConfigureAwait(false);
            if (admission.Outcome == ChatStoreOutcome.Replayed)
                return admission.Facts is { } duplicate && duplicate.Intent == intent
                    ? new(ChatOrchestrationOutcome.Duplicate, duplicate.Phase) : new(ChatOrchestrationOutcome.PersistenceUncertain);
            if (admission.Outcome != ChatStoreOutcome.Applied) return new(StoreFailure(admission.Outcome));
            var facts = admission.Facts;
            // Acknowledged creation is not enough when the returned facts are missing or stale.
            if (facts is null || facts.Intent != intent || facts.Phase != ChatTurnPhase.Active ||
                facts.Version != 1 || facts.RoundCount != 0 || facts.EventCount != 1 || facts.EventBytes != 0)
                return new(ChatOrchestrationOutcome.PersistenceUncertain);
            execution.Admitted(facts);
            state = new(execution, actor, conversation, facts);
            state.Messages.Add(AiSystemMessage.Create(_instructions));
            await AddHistoryAsync(state, history).ConfigureAwait(false);
            state.Messages.Add(AiUserMessage.Create(intent.OriginalRequest));

            while (true)
            {
                CheckTime(execution, intent);
                if (ChatTurnPolicy.Reserve(state.Facts, state.Round, state.Facts.Version, Now, _instance) != ChatStoreOutcome.Applied)
                    throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
                CheckContext(state.Messages);
                var request = AiRoundRequest.Create(Guid.NewGuid(), state.Messages, _tools, intent.Envelope.ProviderBudget.MaxOutputTokens);
                var policy = intent.Envelope.GroundingPolicy;
                var prepared = ChatAiBridge.PrepareRound(conversation, intent, _profile, policy, request, Guid.NewGuid(), _instance);
                var previous = state.Facts;
                var reservation = await _turns.ReserveRoundAsync(actor, key, intent.TurnId, previous.Version,
                    prepared.Intent, execution.Token).ConfigureAwait(false);
                // NEVER adopt Replayed or discover a reservation after an exception to obtain dispatch permission.
                request = prepared.TakeAfterAcknowledgement(reservation);
                var reservedTurn = reservation.Turn ?? throw Stop(ChatOrchestrationOutcome.PersistenceUncertain);
                var reservedRound = reservation.Round ?? throw Stop(ChatOrchestrationOutcome.PersistenceUncertain);
                if (reservedTurn.Version != previous.Version + 1 || reservedTurn.RoundCount != previous.RoundCount + 1 ||
                    reservedTurn.EventCount != previous.EventCount || reservedTurn.EventBytes != previous.EventBytes ||
                    reservedTurn.CreatedAtUtc != previous.CreatedAtUtc)
                    throw Stop(ChatOrchestrationOutcome.PersistenceUncertain);
                state.Facts = reservedTurn; state.Round = reservedRound; state.ObservedMetadata = null;
                if (state.Lease is null)
                {
                    await RecheckAsync(state).ConfigureAwait(false);
                    var profileKey = ChatAiBridge.ProfileKey(intent.Profile, _profile);
                    var limits = ChatAiBridge.OpenLimits(state.Facts, Now);
                    execution.EnterClientAdmission();
                    // Network-free admission must expose its real lease before observing caller cancellation.
                    var opened = _provider.OpenTurn(profileKey, limits, CancellationToken.None);
                    if (opened is TurnRejected rejection)
                    {
                        execution.RejectedClientAdmission(rejection.Failure);
                        await PublishRejectionAsync(state, rejection.Failure).ConfigureAwait(false);
                        return await FinishAsync(state, ChatTurnPhase.Failed, ChatOrchestrationOutcome.ProviderUnavailable).ConfigureAwait(false);
                    }
                    if (opened is not TurnOpened turn) throw Stop(ChatOrchestrationOutcome.ProviderUnavailable);
                    execution.Own(turn.Turn); state.Lease = turn.Turn;
                    // Cancellation now leaves an owned lease whose original cleanup the execution joins.
                    CheckTime(execution, intent);
                }
                await RecheckAsync(state).ConfigureAwait(false);
                // Concrete #111 call, once, after exact reservation acknowledgement and fresh authority.
                var round = await _provider.OpenRoundAsync(state.Lease!, request, execution.Token).ConfigureAwait(false);
                if (round is RoundRejected rejected)
                {
                    await PublishRejectionAsync(state, rejected.Failure).ConfigureAwait(false);
                    var cancelled = rejected.Failure.Code == AiFailureCode.Cancelled;
                    return await FinishAsync(state, cancelled ? ChatTurnPhase.Cancelled : ChatTurnPhase.Failed,
                        cancelled ? ChatOrchestrationOutcome.Cancelled : ChatOrchestrationOutcome.Failed).ConfigureAwait(false);
                }
                if (round is not RoundOpened available) throw Stop(ChatOrchestrationOutcome.Interrupted);
                var batch = await ReadRoundAsync(state, available).ConfigureAwait(false);
                if (batch.Terminal is AiRoundFailed failed)
                {
                    await AppendAsync(state, ChatEventData.ForFailure(Guid.NewGuid(), ChatAiBridge.Failure(request.RoundId, failed.Failure))).ConfigureAwait(false);
                    await CompleteAsync(state, ChatAiBridge.Completion(request.RoundId, failed)).ConfigureAwait(false);
                    bool cancelled = failed.Failure.Code == AiFailureCode.Cancelled;
                    return await FinishAsync(state, cancelled ? ChatTurnPhase.Cancelled : ChatTurnPhase.Failed,
                        cancelled ? ChatOrchestrationOutcome.Cancelled : ChatOrchestrationOutcome.Failed).ConfigureAwait(false);
                }
                var terminal = (AiRoundFinished)batch.Terminal;
                if (terminal.Reason != AiFinishReason.ToolCalls)
                {
                    await CompleteAsync(state, ChatAiBridge.Completion(request.RoundId, terminal)).ConfigureAwait(false);
                    bool success = terminal.Reason == AiFinishReason.Stop;
                    return await FinishAsync(state, success ? ChatTurnPhase.Completed : ChatTurnPhase.Failed,
                        success ? ChatOrchestrationOutcome.Completed : ChatOrchestrationOutcome.Failed).ConfigureAwait(false);
                }
                await ExecuteToolsAsync(state, batch).ConfigureAwait(false);
                // Appends must precede completion: #124 forbids appending to a completed round.
                await CompleteAsync(state, ChatAiBridge.Completion(request.RoundId, terminal)).ConfigureAwait(false);
            }
        }
        catch (StopException stop)
        { return await FailAsync(state, stop.Outcome).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            return await FailAsync(state, execution.TimedOut ? ChatOrchestrationOutcome.BudgetExceeded :
            execution.Cancelled ? ChatOrchestrationOutcome.Cancelled : ChatOrchestrationOutcome.Interrupted).ConfigureAwait(false);
        }
        catch (ArgumentException)
        { return await FailAsync(state, ChatOrchestrationOutcome.InvalidRequest).ConfigureAwait(false); }
        catch (Exception e) when (ChatTurnExecution.Recoverable(e))
        { return await FailAsync(state, ChatOrchestrationOutcome.PersistenceUncertain).ConfigureAwait(false); }
    }

    private void ValidateConfiguration(ChatTurnIntent intent)
    {
        var p = intent.Envelope.GroundingPolicy;
        if (ChatAiBridge.PinProfile(intent.Profile.ProviderId, _profile) != intent.Profile || !_profile.MappingMetadataAllowed ||
            (_profile.Capabilities & AiCapability.Text) == 0 || _tools.Length > 0 && (_profile.Capabilities & AiCapability.FunctionTools) == 0 ||
            ChatAiBridge.PinGroundingPolicy(p.Id, p.Version, _instructions, _tools, p.MaxRetrievedNodes, p.MaxToolResultBytes, p.MaxToolIterations) != p)
            throw Stop(ChatOrchestrationOutcome.Denied);
    }

    private async Task RecheckAsync(State s)
    {
        CheckTime(s.Execution, s.Facts.Intent);
        var conversation = await _conversations.ReadAsync(s.Actor, s.Conversation.Key, s.Execution.Token).ConfigureAwait(false);
        if (conversation != s.Conversation) throw Stop(ChatOrchestrationOutcome.Denied);
        ValidateConfiguration(s.Facts.Intent);
        if (!await _profilePolicy.AllowsAsync(s.Actor, s.Conversation, s.Facts.Intent, _profile, s.Execution.Token).ConfigureAwait(false))
            throw Stop(ChatOrchestrationOutcome.Denied);
        var facts = await _turns.ReadTurnAsync(s.Actor, s.Conversation.Key, s.Facts.Intent.TurnId, s.Execution.Token).ConfigureAwait(false);
        if (facts is null || facts != s.Facts || facts.Phase != ChatTurnPhase.Active) throw Stop(ChatOrchestrationOutcome.Interrupted);
        if (s.Round is not null)
        {
            var round = await _turns.ReadRoundAsync(s.Actor, s.Conversation.Key, s.Facts.Intent.TurnId,
                s.Round.Intent.RoundId, s.Execution.Token).ConfigureAwait(false);
            if (round != s.Round) throw Stop(ChatOrchestrationOutcome.Interrupted);
        }
        CheckTime(s.Execution, s.Facts.Intent);
    }

    private void CheckTime(ChatTurnExecution execution, ChatTurnIntent intent)
    {
        execution.Token.ThrowIfCancellationRequested();
        if (execution.Cancelled) throw Stop(execution.TimedOut ? ChatOrchestrationOutcome.BudgetExceeded : ChatOrchestrationOutcome.Cancelled);
        if (Now >= intent.DeadlineUtc) throw Stop(ChatOrchestrationOutcome.BudgetExceeded);
    }

    private async Task PublishRejectionAsync(State s, AiFailure failure)
    {
        var round = s.Round ?? throw Stop(ChatOrchestrationOutcome.Interrupted);
        s.ObservedMetadata = ChatAiBridge.Rejection(round.Intent.RoundId, failure).Metadata;
        await AppendAsync(s, ChatEventData.ForFailure(Guid.NewGuid(), ChatAiBridge.Failure(round.Intent.RoundId, failure))).ConfigureAwait(false);
        await CompleteAsync(s, ChatAiBridge.Rejection(round.Intent.RoundId, failure)).ConfigureAwait(false);
    }

    private async Task AppendAsync(State s, ChatEventData data)
    {
        var before = s.Facts;
        var round = s.Round ?? throw Stop(ChatOrchestrationOutcome.Interrupted);
        var policy = ChatTurnPolicy.Append(before, round, before.Version, round.Intent.Fence, data, Now);
        if (policy != ChatStoreOutcome.Applied) throw Stop(StoreFailure(policy));
        var result = await _turns.AppendAsync(s.Actor, s.Conversation.Key, before.Intent.TurnId, before.Version,
            round.Intent.RoundId, round.Intent.Fence, data, s.Execution.Token).ConfigureAwait(false);
        Adopt(s, before, result, 1, data.ByteCount);
    }

    private async Task CompleteAsync(State s, ChatRoundCompletion completion)
    {
        var before = s.Facts;
        var round = s.Round ?? throw Stop(ChatOrchestrationOutcome.Interrupted);
        var policy = ChatTurnPolicy.CompleteRound(before, round, before.Version, round.Intent.Fence, completion.Outcome, Now);
        if (policy != ChatStoreOutcome.Applied) throw Stop(StoreFailure(policy));
        var bytes = completion.Metadata is null ? 0 : Encoding.UTF8.GetByteCount(ChatMetadataCodec.Terminal(completion.Metadata));
        var result = await _turns.CompleteRoundAsync(s.Actor, s.Conversation.Key, before.Intent.TurnId, before.Version,
            round.Intent.RoundId, round.Intent.Fence, completion, s.Execution.Token).ConfigureAwait(false);
        Adopt(s, before, result, completion.Metadata is null ? 0 : 1, bytes);
        s.Round = new(round.Intent, round.Ordinal, completion.Outcome, completion.Metadata);
    }

    private static void Adopt(State s, ChatTurnFacts before, ChatTurnWriteResult result, int events, int bytes)
    {
        if (result.Outcome != ChatStoreOutcome.Applied) throw Stop(StoreFailure(result.Outcome));
        var next = result.Facts;
        if (next is null || next.Intent != before.Intent || next.Phase != ChatTurnPhase.Active || next.Version != before.Version + 1 ||
            next.RoundCount != before.RoundCount || next.EventCount != before.EventCount + events || next.EventBytes != before.EventBytes + bytes ||
            next.CreatedAtUtc != before.CreatedAtUtc) throw Stop(ChatOrchestrationOutcome.PersistenceUncertain);
        s.Facts = next;
    }

    private async Task<ChatOrchestrationResult> FailAsync(State? s, ChatOrchestrationOutcome outcome)
    {
        if (s is null) return new(outcome);
        if (s.TerminalAttempted) return new(ChatOrchestrationOutcome.PersistenceUncertain);
        var phase = outcome == ChatOrchestrationOutcome.Cancelled ? ChatTurnPhase.Cancelled :
            outcome is ChatOrchestrationOutcome.PersistenceUncertain or ChatOrchestrationOutcome.Interrupted ? ChatTurnPhase.Interrupted : ChatTurnPhase.Failed;
        try { return await FinishAsync(s, phase, outcome).ConfigureAwait(false); }
        catch (Exception e) when (ChatTurnExecution.Recoverable(e)) { return new(ChatOrchestrationOutcome.PersistenceUncertain); }
    }

    private async Task<ChatOrchestrationResult> FinishAsync(State s, ChatTurnPhase phase, ChatOrchestrationOutcome outcome)
    {
        if (s.TerminalAttempted) return new(ChatOrchestrationOutcome.PersistenceUncertain);
        if (phase == ChatTurnPhase.Completed && (s.Execution.Cancelled || Now >= s.Facts.Intent.DeadlineUtc))
        {
            phase = s.Execution.Cancelled && !s.Execution.TimedOut ? ChatTurnPhase.Cancelled : ChatTurnPhase.Failed;
            outcome = phase == ChatTurnPhase.Cancelled ? ChatOrchestrationOutcome.Cancelled : ChatOrchestrationOutcome.BudgetExceeded;
        }
        // One attempt at the last acknowledged version, not a publication/dispatch retry.
        if (ChatTurnPolicy.Finish(s.Facts, s.Round, s.Facts.Version, phase, Now) != ChatStoreOutcome.Applied)
            return new(ChatOrchestrationOutcome.PersistenceUncertain);
        s.TerminalAttempted = true;
        var result = await _turns.FinishWithMetadataAsync(s.Actor, s.Conversation.Key, s.Facts.Intent.TurnId,
            s.Facts.Version, phase, s.Round?.Metadata ?? s.ObservedMetadata, CancellationToken.None).ConfigureAwait(false);
        if (result.Outcome != ChatStoreOutcome.Applied) return new(ChatOrchestrationOutcome.PersistenceUncertain);
        var next = result.Facts;
        if (next is not null && (next.Intent != s.Facts.Intent || next.Phase != phase || next.Version != s.Facts.Version + 1 ||
            next.EventCount != s.Facts.EventCount + 1 || next.EventBytes != s.Facts.EventBytes || next.RoundCount != s.Facts.RoundCount))
            return new(ChatOrchestrationOutcome.PersistenceUncertain);
        return new(outcome, phase);
    }

    private static ChatOrchestrationOutcome StoreFailure(ChatStoreOutcome outcome) => outcome switch
    {
        ChatStoreOutcome.NotAuthorizedOrNotFound => ChatOrchestrationOutcome.Denied,
        ChatStoreOutcome.LimitReached or ChatStoreOutcome.Expired => ChatOrchestrationOutcome.BudgetExceeded,
        _ => ChatOrchestrationOutcome.PersistenceUncertain
    };
    private static StopException Stop(ChatOrchestrationOutcome outcome) => new(outcome);
    private sealed class StopException(ChatOrchestrationOutcome outcome) : Exception("Chat operation stopped.")
    { internal ChatOrchestrationOutcome Outcome { get; } = outcome; }

    private sealed class State(ChatTurnExecution execution, ActorContext actor, ChatConversationIntent conversation, ChatTurnFacts facts)
    {
        internal ChatTurnExecution Execution { get; } = execution;
        internal ActorContext Actor { get; } = actor;
        internal ChatConversationIntent Conversation { get; } = conversation;
        internal ChatTurnFacts Facts { get; set; } = facts;
        internal ChatRoundFacts? Round { get; set; }
        internal ChatTerminalMetadata? ObservedMetadata { get; set; }
        internal bool TerminalAttempted { get; set; }
        internal AiTurnLease? Lease { get; set; }
        internal List<AiMessage> Messages { get; } = [];
        internal HashSet<string> CallIds { get; } = new(StringComparer.Ordinal);
        internal long OutputBytes { get; set; }
        internal int ToolCalls { get; set; }
        internal int ToolIterations { get; set; }
        internal int ResultBytes { get; set; }
        internal int RetrievedNodes { get; set; }
    }
    public override string ToString() => nameof(ChatTurnOrchestrator);
}
