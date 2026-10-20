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

public sealed record ChatReadOnlyToolResult
{
    public ChatReadOnlyToolResult(AiJsonObject result, int retrievedNodes)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ByteCount > 32768 || retrievedNodes is < 0 or > 1024) throw ChatBounds.Invalid();
        Result = result; RetrievedNodes = retrievedNodes;
    }
    public AiJsonObject Result { get; }
    public int RetrievedNodes { get; }
    public override string ToString() => nameof(ChatReadOnlyToolResult);
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
