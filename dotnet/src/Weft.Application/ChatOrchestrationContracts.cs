using System.Collections.Immutable;
using Weft.Ai;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Trusted current destination/profile policy. Not a provider or credential resolver.</summary>
public interface IChatProfilePolicy
{
    Task<bool> AllowsAsync(ActorContext actor, ChatConversationIntent conversation,
        ChatTurnIntent turn, AiProfileSpec profile, CancellationToken ct);
}

/// <summary>Trusted read-only execution boundary. Validate has no execution side effects.
/// Execute independently checks current scope/schema and never grants payload, edit or run access.</summary>
public interface IChatReadOnlyToolPort
{
    Task<bool> ValidateAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct);
    Task<ChatReadOnlyToolResult> ExecuteAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct);
}

/// <summary>
/// Fresh authority for the trusted payload disclosures already retained in this turn's model context.
/// A model-selected citation or argument is never a disclosure. Losing authority refuses further dispatch;
/// it does not erase bytes already delivered in an earlier round.
/// </summary>
public interface IChatPayloadAuthorizationPort
{
    Task<bool> AllowsAsync(ActorContext actor, ChatConversationIntent conversation, ChatTurnIntent turn,
        AiProfileSpec profile, IReadOnlyList<ChatPayloadDisclosureFact> sources, CancellationToken ct);
}

public sealed record ChatReadOnlyToolContext
{
    public ChatReadOnlyToolContext(ActorContext actor, ChatConversationIntent conversation,
        ChatTurnIntent turn, Guid roundId, int ordinal, int remainingResultBytes, int remainingRetrievedNodes)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(turn); ChatBounds.Id(roundId);
        if (ordinal is < 1 or > 8 || remainingResultBytes < 1 || remainingResultBytes > turn.Envelope.GroundingPolicy.MaxToolResultBytes ||
            remainingRetrievedNodes < 0 || remainingRetrievedNodes > turn.Envelope.GroundingPolicy.MaxRetrievedNodes) throw ChatBounds.Invalid();
        Actor = actor; Conversation = conversation; Turn = turn; RoundId = roundId; Ordinal = ordinal;
        RemainingResultBytes = remainingResultBytes; RemainingRetrievedNodes = remainingRetrievedNodes;
    }
    public ActorContext Actor { get; }
    public ChatConversationIntent Conversation { get; }
    public ChatTurnIntent Turn { get; }
    public Guid RoundId { get; }
    public int Ordinal { get; }
    public int RemainingResultBytes { get; }
    public int RemainingRetrievedNodes { get; }
    public override string ToString() => nameof(ChatReadOnlyToolContext);
}

/// <summary>
/// Citations are unverified candidates; only <see cref="IChatCitationPort"/> or <see cref="IChatWorkspaceCitationPort"/>
/// can turn them into citations. <see cref="DataShared"/> describes payload bytes inside <see cref="Result"/>.
/// </summary>
public sealed record ChatReadOnlyToolResult
{
    public const int MaxCitationCandidates = 64;

    public ChatReadOnlyToolResult(AiJsonObject result, int retrievedNodes, IEnumerable<ChatCitationTarget>? citations = null,
        IEnumerable<ChatWorkspaceCitationFact>? workspaceCitations = null, ChatPayloadDisclosureFact? dataShared = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ByteCount > 32768 || retrievedNodes is < 0 or > 1024) throw ChatBounds.Invalid();
        var owned = (citations ?? []).ToImmutableArray();
        var workspace = (workspaceCitations ?? []).ToImmutableArray();
        if (owned.Length + workspace.Length > MaxCitationCandidates || owned.Any(candidate => candidate is null) ||
            workspace.Any(candidate => candidate is null) || dataShared is not null && dataShared.ByteCount > result.ByteCount)
            throw ChatBounds.Invalid();
        Result = result; RetrievedNodes = retrievedNodes; Citations = owned; WorkspaceCitations = workspace; DataShared = dataShared;
    }
    public AiJsonObject Result { get; }
    public int RetrievedNodes { get; }
    public ImmutableArray<ChatCitationTarget> Citations { get; }
    /// <summary>Unverified workspace candidates of any resource kind; empty for revision conversations.</summary>
    public ImmutableArray<ChatWorkspaceCitationFact> WorkspaceCitations { get; }
    /// <summary>Payload bytes the result carries to the model; recorded and audited before the next provider round.</summary>
    public ChatPayloadDisclosureFact? DataShared { get; }
    public override string ToString() => nameof(ChatReadOnlyToolResult);
}

/// <summary>
/// Trusted workspace citation boundary. Freshly authorizes and reads each candidate resource, returning only
/// verified workspace citations of the conversation's organization; an unverifiable candidate is omitted.
/// </summary>
public interface IChatWorkspaceCitationPort
{
    Task<IReadOnlyList<ChatValidatedCitation>> VerifyAsync(ActorContext actor, ChatConversationIntent conversation,
        IReadOnlyList<ChatWorkspaceCitationFact> candidates, CancellationToken ct);
}

/// <summary>
/// Trusted turn context: a bounded description of the turn's resolved focus and selectors, built from fresh reads.
/// It is sent as data in a user-role message, never appended to the pinned instructions, and grants nothing.
/// </summary>
public interface IChatTurnContextPort
{
    Task<string?> DescribeAsync(ActorContext actor, ChatConversationIntent conversation, ChatTurnIntent turn, CancellationToken ct);
    /// <summary>Whether an earlier turn can be copied into automatic model context. False skips its private event text entirely.</summary>
    bool IncludeHistory(ChatTurnIntent turn) => true;
}

/// <summary>
/// Trusted citation boundary. Freshly authorizes and checks candidates against the conversation's exact pinned
/// reports, returning only verified citations; an unverifiable candidate is omitted, never repaired.
/// </summary>
public interface IChatCitationPort
{
    Task<IReadOnlyList<ChatValidatedCitation>> VerifyAsync(ActorContext actor, ChatConversationIntent conversation,
        IReadOnlyList<ChatCitationTarget> candidates, CancellationToken ct);
}

public sealed record ChatOrchestrationLimits
{
    public ChatOrchestrationLimits(int maxHistoryTurns, int maxHistoryEvents, int maxHistoryBytes, int maxContextBytes)
    {
        if (maxHistoryTurns is < 0 or > 8 || maxHistoryEvents is < 1 or > 4096 ||
            maxHistoryBytes is < 1 or > 65536 || maxContextBytes is < 1 or > 131072) throw ChatBounds.Invalid();
        MaxHistoryTurns = maxHistoryTurns; MaxHistoryEvents = maxHistoryEvents;
        MaxHistoryBytes = maxHistoryBytes; MaxContextBytes = maxContextBytes;
    }
    public int MaxHistoryTurns { get; }
    public int MaxHistoryEvents { get; }
    public int MaxHistoryBytes { get; }
    public int MaxContextBytes { get; }
    public override string ToString() => nameof(ChatOrchestrationLimits);
}

public enum ChatOrchestrationOutcome
{
    Completed, Failed, Cancelled, Interrupted, Duplicate, Denied, InvalidRequest,
    BudgetExceeded, PersistenceUncertain, ProviderUnavailable, CleanupUnproven
}

/// <summary>Phase is present only for an acknowledged terminal publication (or acknowledged duplicate).</summary>
public sealed record ChatOrchestrationResult
{
    internal ChatOrchestrationResult(ChatOrchestrationOutcome outcome, ChatTurnPhase? phase = null)
    { Outcome = outcome; Phase = phase; }
    public ChatOrchestrationOutcome Outcome { get; }
    public ChatTurnPhase? Phase { get; }
    public override string ToString() => nameof(ChatOrchestrationResult);
}

public sealed class ChatOrchestrationCleanupException : Exception
{
    internal ChatOrchestrationCleanupException() : base("Chat cleanup remains unproven.") { }
}
