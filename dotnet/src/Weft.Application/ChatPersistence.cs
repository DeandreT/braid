using Weft.Domain;

namespace Weft.Application;

public enum ChatStoreOutcome { Applied, Replayed, Conflict, InvalidState, LimitReached, Expired, NotAuthorizedOrNotFound, IdentityRetired }

public sealed record ChatTurnFacts
{
    public ChatTurnFacts(ChatTurnIntent intent, ChatTurnPhase phase, long version, int eventCount,
        int eventBytes, int roundCount, DateTimeOffset createdAtUtc, DateTimeOffset? finishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ChatBounds.Instant(createdAtUtc);
        if (!Enum.IsDefined(phase) || version < 1 || eventCount < 1 || eventCount > intent.Budget.MaxEvents ||
            eventBytes < 0 || eventBytes > intent.Budget.MaxEventBytes || roundCount < 0 || roundCount > intent.Budget.MaxRounds ||
            createdAtUtc >= intent.DeadlineUtc || (phase == ChatTurnPhase.Active) != (finishedAtUtc is null)) throw ChatBounds.Invalid();
        if (finishedAtUtc is { } finished) { ChatBounds.Instant(finished); if (finished < createdAtUtc) throw ChatBounds.Invalid(); }
        Intent = intent; Phase = phase; Version = version; EventCount = eventCount; EventBytes = eventBytes;
        RoundCount = roundCount; CreatedAtUtc = createdAtUtc; FinishedAtUtc = finishedAtUtc;
    }
    public ChatTurnIntent Intent { get; }
    public ChatTurnPhase Phase { get; }
    public long Version { get; }
    public int EventCount { get; }
    public int EventBytes { get; }
    public int RoundCount { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? FinishedAtUtc { get; }
    public override string ToString() => nameof(ChatTurnFacts);
}

public sealed record ChatRoundFacts
{
    public ChatRoundFacts(ChatRoundIntent intent, int ordinal, ChatRoundOutcome outcome, ChatTerminalMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (ordinal is < 1 or > 4 || !Enum.IsDefined(outcome)) throw ChatBounds.Invalid();
        if (metadata is not null && outcome == ChatRoundOutcome.Uncertain) throw ChatBounds.Invalid();
        if (metadata is not null) _ = new ChatRoundCompletion(outcome, metadata);
        if (metadata?.Usage is { } usage && usage.RoundId != intent.RoundId || metadata?.Failure is { } failure && failure.RoundId != intent.RoundId) throw ChatBounds.Invalid();
        Intent = intent; Ordinal = ordinal; Outcome = outcome; Metadata = metadata;
    }
    public ChatRoundIntent Intent { get; }
    public int Ordinal { get; }
    public ChatRoundOutcome Outcome { get; }
    public ChatTerminalMetadata? Metadata { get; }
    public override string ToString() => nameof(ChatRoundFacts);
}

public sealed record ChatStoredEvent(long Sequence, Guid EventId, ChatEventKind Kind, string Text, Guid? RoundId, string? MetadataJson = null)
{
    public ChatUsageFact? Usage => Kind == ChatEventKind.Usage ? ChatMetadataCodec.ReadUsage(MetadataJson!) : TerminalMetadata?.Usage;
    public ChatProviderFailureFact? ProviderFailure => Kind == ChatEventKind.ProviderFailure ? ChatMetadataCodec.ReadFailure(MetadataJson!) : TerminalMetadata?.Failure;
    public ChatCitationFact? Citation => Kind == ChatEventKind.Citation ? ChatMetadataCodec.ReadCitation(MetadataJson!) : null;
    public ChatTerminalMetadata? TerminalMetadata => (Kind is ChatEventKind.Completed or ChatEventKind.Failed or ChatEventKind.Cancelled or ChatEventKind.Interrupted or ChatEventKind.RoundCompleted) && MetadataJson is not null
        ? ChatMetadataCodec.ReadTerminal(MetadataJson!) : null;
    public override string ToString() => nameof(ChatStoredEvent);
}

public sealed record ChatTurnWriteResult(ChatStoreOutcome Outcome, ChatTurnFacts? Facts = null)
{
    public override string ToString() => nameof(ChatTurnWriteResult);
}

/// <summary>Only Applied from ReserveRoundAsync grants outbound permission, and only to that awaiting caller.
/// Replayed/observed reservations NEVER grant it. A missing commit acknowledgement grants nothing.</summary>
public sealed record ChatRoundWriteResult(ChatStoreOutcome Outcome, ChatTurnFacts? Turn = null, ChatRoundFacts? Round = null)
{
    public override string ToString() => nameof(ChatRoundWriteResult);
}

/// <summary>All methods freshly check stable owner plus current active principal/admission/project membership.
/// Database failures escape as redacted exceptions, never ordinary absence. No administrator content override.</summary>
public interface IChatConversationStore
{
    Task<ChatStoreOutcome> CreateAsync(ActorContext actor, ChatConversationIntent intent, CancellationToken ct = default);
    Task<ChatConversationIntent?> ReadAsync(ActorContext actor, ChatConversationKey key, CancellationToken ct = default);
    /// <summary>Only after the trusted deadline and with no active turn. Deletes owned chat only;
    /// retained revision/report/run facts are never cascaded from chat. Expiration removes access before purge.</summary>
    Task<ChatStoreOutcome> DeleteExpiredAsync(ActorContext actor, ChatConversationKey key, ChatErasurePolicy policy, CancellationToken ct = default);
}

public interface IChatTurnStore
{
    Task<ChatTurnWriteResult> StartAsync(ActorContext actor, ChatConversationKey key, ChatTurnIntent intent, CancellationToken ct = default);
    Task<ChatTurnFacts?> ReadTurnAsync(ActorContext actor, ChatConversationKey key, Guid turnId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatStoredEvent>> ReadEventsAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long afterSequence, int limit, CancellationToken ct = default);
    Task<ChatRoundFacts?> ReadRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId, Guid roundId, CancellationToken ct = default);
    Task<ChatRoundWriteResult> ReserveRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, ChatRoundIntent intent, CancellationToken ct = default);
    Task<ChatTurnWriteResult> CompleteRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, Guid roundId, Guid fence, ChatRoundOutcome outcome, CancellationToken ct = default);
    Task<ChatTurnWriteResult> CompleteRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, Guid roundId, Guid fence, ChatRoundCompletion completion, CancellationToken ct = default);
    Task<ChatTurnWriteResult> AppendAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, Guid roundId, Guid fence, ChatEventData data, CancellationToken ct = default);
    Task<ChatTurnWriteResult> AppendCitationAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, Guid roundId, Guid fence, ChatCitationEventData data, CancellationToken ct = default);
    Task<ChatTurnWriteResult> FinishAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, ChatTurnPhase phase, CancellationToken ct = default);
    Task<ChatTurnWriteResult> FinishWithMetadataAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, ChatTurnPhase phase, ChatTerminalMetadata? metadata, CancellationToken ct = default);
}

/// <summary>Separate trusted lifecycle credential. No owner ACL bypass is exposed through ordinary stores;
/// no operation returns message, request, profile, report, event or round content.</summary>
public interface IChatLifecycleStore
{
    Task<ChatLifecycleObservation?> ObserveAsync(ChatConversationKey key, Guid turnId, CancellationToken ct = default);
    Task<ChatStoreOutcome> CloseAsync(ChatConversationKey key, Guid turnId, long expectedVersion,
        ChatTurnPhase phase, CancellationToken ct = default);
    Task<ChatStoreOutcome> CloseWithMetadataAsync(ChatConversationKey key, Guid turnId, long expectedVersion,
        ChatTurnPhase phase, ChatTerminalMetadata? metadata, CancellationToken ct = default);
    Task<ChatStoreOutcome> EraseExpiredAsync(ChatConversationKey key, ChatErasurePolicy policy, CancellationToken ct = default);
}

public sealed record ChatLifecycleObservation(ChatTurnPhase Phase, long Version)
{
    public override string ToString() => nameof(ChatLifecycleObservation);
}
