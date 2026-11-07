using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Contracts.Catalog;

namespace Weft.Contracts.Chat;

[JsonConverter(typeof(ChatErrorCodeConverterV1))]
public enum ChatErrorCodeV1
{
    Unavailable = 1,
    InvalidRequest = 2,
    NotFound = 3,
    IdentityRejected = 4,
    PersistenceFailure = 5,
    InternalFailure = 6,
    UnsupportedMediaType = 7,
    BodyLimit = 8,
    ReportsUnavailable = 9,
    ProfileUnavailable = 10,
    TurnActive = 11,
    IdempotencyConflict = 12,
    TurnNotStarted = 13,
    RateLimited = 14
}

public sealed record ChatErrorV1
{
    public ChatErrorV1(ChatErrorCodeV1 code)
    {
        if (!Enum.IsDefined(code))
            throw new ArgumentOutOfRangeException(nameof(code), "A chat error code must be known.");
        Code = code;
    }

    public ChatErrorCodeV1 Code { get; }
}

[JsonConverter(typeof(ChatTurnPhaseConverterV1))]
public enum ChatTurnPhaseV1 { Active = 1, Completed = 2, Failed = 3, Cancelled = 4, Interrupted = 5 }

[JsonConverter(typeof(ChatSelectorKindConverterV1))]
public enum ChatSelectorKindV1
{
    Boundary = 1, Field = 2, Parameter = 3, Rule = 4, Control = 5,
    ProjectionDiagnostic = 6, ValidationDiagnostic = 7, ProjectionReport = 8, ValidationReport = 9
}

/// <summary>An exact pinned-report resource or diagnostic ordinal. It records intent; it is not authority.</summary>
public sealed record ChatSelectorV1
{
    public ChatSelectorV1(ChatSelectorKindV1 kind, string? resourceId, int? diagnosticOrdinal)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), "A chat selector kind must be known.");
        var diagnostic = kind is ChatSelectorKindV1.ProjectionDiagnostic or ChatSelectorKindV1.ValidationDiagnostic;
        var report = kind is ChatSelectorKindV1.ProjectionReport or ChatSelectorKindV1.ValidationReport;
        if (diagnostic ? resourceId is not null || diagnosticOrdinal is null or < 0 or > 32767
            : report ? resourceId is not null || diagnosticOrdinal is not null
            : diagnosticOrdinal is not null || resourceId is null)
            throw new ArgumentException("The chat selector shape is invalid.", nameof(kind));
        Kind = kind;
        ResourceId = resourceId is null ? null : CatalogValueV1.RequireText(resourceId, 2048, nameof(resourceId));
        DiagnosticOrdinal = diagnosticOrdinal;
    }

    public ChatSelectorKindV1 Kind { get; }
    public string? ResourceId { get; }
    public int? DiagnosticOrdinal { get; }
}

public sealed record ChatProfileV1
{
    public ChatProfileV1(string profileId, int version, string displayName, string providerId, string modelId, bool supportsTools)
    {
        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version), "A chat profile version must be positive.");
        ProfileId = CatalogValueV1.RequireText(profileId, 64, nameof(profileId));
        Version = version;
        DisplayName = CatalogValueV1.RequireText(displayName, 256, nameof(displayName));
        ProviderId = CatalogValueV1.RequireText(providerId, 64, nameof(providerId));
        ModelId = CatalogValueV1.RequireText(modelId, 256, nameof(modelId));
        SupportsTools = supportsTools;
    }

    public string ProfileId { get; }
    public int Version { get; }
    public string DisplayName { get; }
    public string ProviderId { get; }
    public string ModelId { get; }
    public bool SupportsTools { get; }
}

public sealed record ChatLimitsV1
{
    public ChatLimitsV1(int maxTextBytes, int maxSelectors, int turnSeconds)
    {
        if (maxTextBytes < 1 || maxSelectors < 0 || turnSeconds < 1)
            throw new ArgumentException("Chat limits must be positive.");
        MaxTextBytes = maxTextBytes;
        MaxSelectors = maxSelectors;
        TurnSeconds = turnSeconds;
    }

    public int MaxTextBytes { get; }
    public int MaxSelectors { get; }
    public int TurnSeconds { get; }
}

/// <summary>Configured, currently selectable model profiles for an authorized revision.</summary>
public sealed record ChatAvailabilityV1
{
    public ChatAvailabilityV1(CatalogRevisionScopeV1 revision, IReadOnlyList<ChatProfileV1> profiles, ChatLimitsV1 limits, bool toolsEnabled)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(limits);
        Revision = revision;
        Profiles = profiles.ToArray();
        if (Profiles.Count == 0)
            throw new ArgumentException("Available chat requires a configured profile.", nameof(profiles));
        Limits = limits;
        ToolsEnabled = toolsEnabled;
    }

    public string SchemaVersion => "weft.chat.v1";
    public CatalogRevisionScopeV1 Revision { get; }
    public IReadOnlyList<ChatProfileV1> Profiles { get; }
    public ChatLimitsV1 Limits { get; }
    public bool ToolsEnabled { get; }
}

public sealed record ChatConversationV1
{
    public ChatConversationV1(string conversationId, CatalogRevisionScopeV1 revision, DateTimeOffset createdAtUtc,
        DateTimeOffset retainUntilUtc, string projectionDerivationId, string validationDerivationId, string? title,
        int turnCount, DateTimeOffset? lastTurnAtUtc)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (turnCount < 0 || retainUntilUtc <= createdAtUtc)
            throw new ArgumentException("The chat conversation facts are invalid.");
        ConversationId = CatalogValueV1.RequireId(conversationId, nameof(conversationId));
        Revision = revision;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        RetainUntilUtc = retainUntilUtc.ToUniversalTime();
        ProjectionDerivationId = CatalogValueV1.RequireId(projectionDerivationId, nameof(projectionDerivationId));
        ValidationDerivationId = CatalogValueV1.RequireId(validationDerivationId, nameof(validationDerivationId));
        Title = title;
        TurnCount = turnCount;
        LastTurnAtUtc = lastTurnAtUtc?.ToUniversalTime();
    }

    public string ConversationId { get; }
    public CatalogRevisionScopeV1 Revision { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset RetainUntilUtc { get; }
    public string ProjectionDerivationId { get; }
    public string ValidationDerivationId { get; }
    public string? Title { get; }
    public int TurnCount { get; }
    public DateTimeOffset? LastTurnAtUtc { get; }
    public override string ToString() => $"ChatConversationV1 {{ ConversationId = {ConversationId} }}";
}

public sealed record ChatConversationPageV1
{
    public ChatConversationPageV1(IReadOnlyList<ChatConversationV1> conversations, bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        Conversations = conversations.ToArray();
        HasMore = hasMore;
    }

    public IReadOnlyList<ChatConversationV1> Conversations { get; }
    public bool HasMore { get; }
}

public sealed record ChatTurnProfileV1
{
    public ChatTurnProfileV1(string profileId, int version, string providerId, string modelId)
    {
        ProfileId = CatalogValueV1.RequireText(profileId, 64, nameof(profileId));
        Version = version;
        ProviderId = CatalogValueV1.RequireText(providerId, 64, nameof(providerId));
        ModelId = CatalogValueV1.RequireText(modelId, 256, nameof(modelId));
    }

    public string ProfileId { get; }
    public int Version { get; }
    public string ProviderId { get; }
    public string ModelId { get; }
}

/// <summary>One private turn. LastSequence is the last durable event sequence, for stream resumption.</summary>
public sealed record ChatTurnV1
{
    public ChatTurnV1(string turnId, string conversationId, ChatTurnPhaseV1 phase, string text,
        IReadOnlyList<ChatSelectorV1> selectors, ChatTurnProfileV1 profile, DateTimeOffset createdAtUtc,
        DateTimeOffset deadlineUtc, DateTimeOffset? finishedAtUtc, int lastSequence, ChatTurnFocusV1? focus = null)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(phase) || lastSequence < 1 || (phase == ChatTurnPhaseV1.Active) != (finishedAtUtc is null))
            throw new ArgumentException("The chat turn facts are invalid.");
        TurnId = CatalogValueV1.RequireId(turnId, nameof(turnId));
        ConversationId = CatalogValueV1.RequireId(conversationId, nameof(conversationId));
        Phase = phase;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Selectors = selectors.ToArray();
        Profile = profile;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        DeadlineUtc = deadlineUtc.ToUniversalTime();
        FinishedAtUtc = finishedAtUtc?.ToUniversalTime();
        LastSequence = lastSequence;
        Focus = focus;
    }

    public string TurnId { get; }
    public string ConversationId { get; }
    public ChatTurnPhaseV1 Phase { get; }
    public string Text { get; }
    public IReadOnlyList<ChatSelectorV1> Selectors { get; }
    public ChatTurnProfileV1 Profile { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public DateTimeOffset? FinishedAtUtc { get; }
    public int LastSequence { get; }
    /// <summary>A workspace turn's resolved focus; null for a revision turn.</summary>
    public ChatTurnFocusV1? Focus { get; }
    public override string ToString() => $"ChatTurnV1 {{ TurnId = {TurnId}, Phase = {Phase} }}";
}

public sealed record ChatConversationDetailV1
{
    public ChatConversationDetailV1(ChatConversationV1 conversation, IReadOnlyList<ChatTurnV1> turns, bool hasEarlierTurns)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(turns);
        Conversation = conversation;
        Turns = turns.ToArray();
        HasEarlierTurns = hasEarlierTurns;
    }

    public ChatConversationV1 Conversation { get; }
    public IReadOnlyList<ChatTurnV1> Turns { get; }
    public bool HasEarlierTurns { get; }
}

/// <summary>Request body for conversation creation. The client-generated ID is the idempotency key.</summary>
public sealed record ChatConversationCreateV1(string ConversationId);

/// <summary>Request body for turn creation. The client-generated turn ID is the idempotency key.</summary>
public sealed record ChatTurnCreateV1(string TurnId, string Text, string ProfileId, int ProfileVersion,
    IReadOnlyList<ChatSelectorV1> Selectors);

internal sealed class ChatErrorCodeConverterV1 : JsonStringEnumConverter<ChatErrorCodeV1>
{
    public ChatErrorCodeConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class ChatTurnPhaseConverterV1 : JsonStringEnumConverter<ChatTurnPhaseV1>
{
    public ChatTurnPhaseConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class ChatSelectorKindConverterV1 : JsonStringEnumConverter<ChatSelectorKindV1>
{
    public ChatSelectorKindConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}
