using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Contracts.Catalog;

namespace Weft.Contracts.Chat;

[JsonConverter(typeof(ChatUsageOriginConverterV1))]
public enum ChatUsageOriginV1 { Observation = 1, RoundTerminal = 2, OpenRejection = 3 }

[JsonConverter(typeof(ChatTurnFailureConverterV1))]
public enum ChatTurnFailureV1 { Provider = 1, OutputLimit = 2, Refused = 3, ContentFiltered = 4, Interrupted = 5, Stopped = 6 }

[JsonConverter(typeof(ChatProviderFailureConverterV1))]
public enum ChatProviderFailureV1
{
    InvalidRequest = 1, ProfileUnavailable, MetadataDisallowed, CapabilityUnavailable, CredentialUnavailable,
    Busy, BudgetExceeded, Cancelled, DeadlineExceeded, TransportFailure, ProviderAuthentication,
    ProviderRateLimited, ProviderRejected, ProviderUnavailable, UnsupportedResponse, MalformedResponse, TruncatedResponse
}

/// <summary>
/// Application-owned turn stream event. Each is framed as one SSE event whose id is Sequence and
/// whose event name is the type discriminator. Vendor event names and raw bodies never appear.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChatTurnStartedEventV1), "turn.started")]
[JsonDerivedType(typeof(ChatTextDeltaEventV1), "text.delta")]
[JsonDerivedType(typeof(ChatToolStartedEventV1), "tool.started")]
[JsonDerivedType(typeof(ChatToolCompletedEventV1), "tool.completed")]
[JsonDerivedType(typeof(ChatUsageEventV1), "usage")]
[JsonDerivedType(typeof(ChatCitationEventV1), "citation")]
[JsonDerivedType(typeof(ChatWorkspaceCitationEventV1), "workspace.citation")]
[JsonDerivedType(typeof(ChatDataSharedEventV1), "data.shared")]
[JsonDerivedType(typeof(ChatTurnCompletedEventV1), "turn.completed")]
[JsonDerivedType(typeof(ChatTurnFailedEventV1), "turn.failed")]
[JsonDerivedType(typeof(ChatTurnCancelledEventV1), "turn.cancelled")]
public abstract record ChatTurnEventV1
{
    private protected ChatTurnEventV1(int sequence)
    {
        if (sequence is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(sequence), "A chat event sequence is bounded.");
        Sequence = sequence;
    }

    public int Sequence { get; }
}

public sealed record ChatTurnStartedEventV1 : ChatTurnEventV1
{
    public ChatTurnStartedEventV1(int sequence) : base(sequence) { }
}

public sealed record ChatTextDeltaEventV1 : ChatTurnEventV1
{
    public ChatTextDeltaEventV1(int sequence, string roundId, string text) : base(sequence)
    {
        RoundId = CatalogValueV1.RequireId(roundId, nameof(roundId));
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public string RoundId { get; }
    public string Text { get; }
}

public sealed record ChatToolStartedEventV1 : ChatTurnEventV1
{
    public ChatToolStartedEventV1(int sequence, string roundId, int ordinal, string name) : base(sequence)
    {
        RoundId = CatalogValueV1.RequireId(roundId, nameof(roundId));
        Ordinal = ordinal is >= 1 and <= 8 ? ordinal : throw new ArgumentOutOfRangeException(nameof(ordinal));
        Name = CatalogValueV1.RequireText(name, 64, nameof(name));
    }

    public string RoundId { get; }
    public int Ordinal { get; }
    public string Name { get; }
}

public sealed record ChatToolCompletedEventV1 : ChatTurnEventV1
{
    public ChatToolCompletedEventV1(int sequence, string roundId, int ordinal, string name, int resultBytes, int retrievedNodes)
        : base(sequence)
    {
        RoundId = CatalogValueV1.RequireId(roundId, nameof(roundId));
        Ordinal = ordinal is >= 1 and <= 8 ? ordinal : throw new ArgumentOutOfRangeException(nameof(ordinal));
        Name = CatalogValueV1.RequireText(name, 64, nameof(name));
        if (resultBytes < 0 || retrievedNodes < 0)
            throw new ArgumentOutOfRangeException(nameof(resultBytes));
        ResultBytes = resultBytes;
        RetrievedNodes = retrievedNodes;
    }

    public string RoundId { get; }
    public int Ordinal { get; }
    public string Name { get; }
    public int ResultBytes { get; }
    public int RetrievedNodes { get; }
}

/// <summary>A per-round snapshot, not an increment. Null counts are unknown, never zero.</summary>
public sealed record ChatUsageEventV1 : ChatTurnEventV1
{
    public ChatUsageEventV1(int sequence, string roundId, ChatUsageOriginV1 origin, long? inputTokens,
        long? outputTokens, long? totalTokens) : base(sequence)
    {
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin));
        RoundId = CatalogValueV1.RequireId(roundId, nameof(roundId));
        Origin = origin;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        TotalTokens = totalTokens;
    }

    public string RoundId { get; }
    public ChatUsageOriginV1 Origin { get; }
    public long? InputTokens { get; }
    public long? OutputTokens { get; }
    public long? TotalTokens { get; }
}

/// <summary>A resource verified against the conversation's exact pinned report. It does not prove an interpretation.</summary>
public sealed record ChatCitationEventV1 : ChatTurnEventV1
{
    public ChatCitationEventV1(int sequence, string roundId, ChatSelectorV1 target, string reportDerivationId) : base(sequence)
    {
        RoundId = CatalogValueV1.RequireId(roundId, nameof(roundId));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        ReportDerivationId = CatalogValueV1.RequireId(reportDerivationId, nameof(reportDerivationId));
    }

    public string RoundId { get; }
    public ChatSelectorV1 Target { get; }
    public string ReportDerivationId { get; }
}

public sealed record ChatTurnCompletedEventV1 : ChatTurnEventV1
{
    public ChatTurnCompletedEventV1(int sequence) : base(sequence) { }
}

public sealed record ChatTurnFailedEventV1 : ChatTurnEventV1
{
    public ChatTurnFailedEventV1(int sequence, ChatTurnFailureV1 reason, ChatProviderFailureV1? providerFailure)
        : base(sequence)
    {
        if (!Enum.IsDefined(reason) || providerFailure is { } failure && (!Enum.IsDefined(failure) || reason != ChatTurnFailureV1.Provider))
            throw new ArgumentOutOfRangeException(nameof(reason));
        Reason = reason;
        ProviderFailure = providerFailure;
    }

    public ChatTurnFailureV1 Reason { get; }
    public ChatProviderFailureV1? ProviderFailure { get; }
}

public sealed record ChatTurnCancelledEventV1 : ChatTurnEventV1
{
    public ChatTurnCancelledEventV1(int sequence) : base(sequence) { }
}

internal sealed class ChatUsageOriginConverterV1 : JsonStringEnumConverter<ChatUsageOriginV1>
{
    public ChatUsageOriginConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class ChatTurnFailureConverterV1 : JsonStringEnumConverter<ChatTurnFailureV1>
{
    public ChatTurnFailureConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class ChatProviderFailureConverterV1 : JsonStringEnumConverter<ChatProviderFailureV1>
{
    public ChatProviderFailureConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}
