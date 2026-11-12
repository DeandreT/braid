using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Contracts.Catalog;

namespace Weft.Contracts.Chat;

/// <summary>A profile that can run workspace turns, with what its destination may receive.</summary>
public sealed record ChatWorkspaceProfileV1
{
    public ChatWorkspaceProfileV1(ChatProfileV1 profile, bool payloadsAllowed)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        PayloadsAllowed = payloadsAllowed;
    }

    public ChatProfileV1 Profile { get; }
    /// <summary>The destination may receive sample payload data, for a user who also holds a payload-read grant.</summary>
    public bool PayloadsAllowed { get; }
}

/// <summary>Workspace chat in the caller's organization: the profiles that can run a workspace turn and the limits.</summary>
public sealed record ChatWorkspaceAvailabilityV1
{
    public ChatWorkspaceAvailabilityV1(string organizationId, IReadOnlyList<ChatWorkspaceProfileV1> profiles, ChatLimitsV1 limits)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        OrganizationId = CatalogValueV1.RequireId(organizationId, nameof(organizationId));
        Profiles = profiles.ToArray();
        if (Profiles.Count == 0 || Profiles.Any(profile => profile is null))
            throw new ArgumentException("Available workspace chat requires a configured profile.", nameof(profiles));
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string SchemaVersion => "weft.chat.v1";
    public string OrganizationId { get; }
    public IReadOnlyList<ChatWorkspaceProfileV1> Profiles { get; }
    public ChatLimitsV1 Limits { get; }
}

/// <summary>What a workspace conversation is about: a mapping, or one exact revision of it. It grants nothing.</summary>
public sealed record ChatFocusV1
{
    public ChatFocusV1(string projectId, string mappingId, string? revisionId)
    {
        ProjectId = CatalogValueV1.RequireId(projectId, nameof(projectId));
        MappingId = CatalogValueV1.RequireId(mappingId, nameof(mappingId));
        RevisionId = revisionId is null ? null : CatalogValueV1.RequireId(revisionId, nameof(revisionId));
    }

    public string ProjectId { get; }
    public string MappingId { get; }
    public string? RevisionId { get; }
}

[JsonConverter(typeof(ChatConversationKindConverterV1))]
public enum ChatConversationKindV1 { Revision = 1, Workspace = 2 }

/// <summary>
/// One of the caller's conversations in an organization. A revision conversation carries its pinned revision and is
/// continued through the revision routes; a workspace conversation carries its optional focus.
/// </summary>
public sealed record ChatOrganizationConversationV1
{
    public ChatOrganizationConversationV1(string conversationId, ChatConversationKindV1 kind, CatalogRevisionScopeV1? revision,
        ChatFocusV1? focus, DateTimeOffset createdAtUtc, DateTimeOffset retainUntilUtc, string? title, int turnCount,
        DateTimeOffset? lastTurnAtUtc)
    {
        if (!Enum.IsDefined(kind) || (kind == ChatConversationKindV1.Revision) != (revision is not null) ||
            kind == ChatConversationKindV1.Revision && focus is not null || turnCount < 0 || retainUntilUtc <= createdAtUtc)
            throw new ArgumentException("The chat conversation facts are invalid.");
        ConversationId = CatalogValueV1.RequireId(conversationId, nameof(conversationId));
        Kind = kind;
        Revision = revision;
        Focus = focus;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        RetainUntilUtc = retainUntilUtc.ToUniversalTime();
        Title = title;
        TurnCount = turnCount;
        LastTurnAtUtc = lastTurnAtUtc?.ToUniversalTime();
    }

    public string ConversationId { get; }
    public ChatConversationKindV1 Kind { get; }
    public CatalogRevisionScopeV1? Revision { get; }
    public ChatFocusV1? Focus { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset RetainUntilUtc { get; }
    public string? Title { get; }
    public int TurnCount { get; }
    public DateTimeOffset? LastTurnAtUtc { get; }
    public override string ToString() => $"ChatOrganizationConversationV1 {{ ConversationId = {ConversationId}, Kind = {Kind} }}";
}

/// <summary>Newest first. With more conversations, continue with the last conversation's identity as <c>after</c>.</summary>
public sealed record ChatOrganizationConversationPageV1
{
    public ChatOrganizationConversationPageV1(IReadOnlyList<ChatOrganizationConversationV1> conversations, bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        Conversations = conversations.ToArray();
        HasMore = hasMore;
    }

    public IReadOnlyList<ChatOrganizationConversationV1> Conversations { get; }
    public bool HasMore { get; }
}

public sealed record ChatWorkspaceConversationDetailV1
{
    public ChatWorkspaceConversationDetailV1(ChatOrganizationConversationV1 conversation, IReadOnlyList<ChatTurnV1> turns, bool hasEarlierTurns)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(turns);
        if (conversation.Kind != ChatConversationKindV1.Workspace) throw new ArgumentException("The conversation is not a workspace conversation.", nameof(conversation));
        Conversation = conversation;
        Turns = turns.ToArray();
        HasEarlierTurns = hasEarlierTurns;
    }

    public ChatOrganizationConversationV1 Conversation { get; }
    public IReadOnlyList<ChatTurnV1> Turns { get; }
    public bool HasEarlierTurns { get; }
}

/// <summary>Request body for workspace conversation creation. The client-generated ID is the idempotency key.</summary>
public sealed record ChatWorkspaceConversationCreateV1(string ConversationId, ChatFocusV1? Focus = null);

[JsonConverter(typeof(ChatTurnFocusStateConverterV1))]
public enum ChatTurnFocusStateV1 { None = 1, Resolved = 2, Unavailable = 3 }

/// <summary>The exact revision a workspace turn was answered about, resolved when it started and never followed afterwards.</summary>
public sealed record ChatTurnFocusV1
{
    public ChatTurnFocusV1(ChatTurnFocusStateV1 state, CatalogRevisionScopeV1? revision)
    {
        if (!Enum.IsDefined(state) || (state == ChatTurnFocusStateV1.Resolved) != (revision is not null))
            throw new ArgumentException("The chat turn focus is invalid.", nameof(state));
        State = state;
        Revision = revision;
    }

    public ChatTurnFocusStateV1 State { get; }
    public CatalogRevisionScopeV1? Revision { get; }
}

[JsonConverter(typeof(ChatWorkspaceCitationKindConverterV1))]
public enum ChatWorkspaceCitationKindV1
{
    Project = 1, Mapping, Revision, Report, PreviewRun, RegressionCase, RegressionRun, BuildRun, BuildApproval, BuildEnvironment, GuideSection
}

/// <summary>
/// A resource a workspace tool read in the turn, verified under fresh authority. Identities only: the browser reads
/// the resource itself through its own route. It does not prove an interpretation.
/// </summary>
public sealed record ChatWorkspaceCitationV1
{
    public ChatWorkspaceCitationV1(ChatWorkspaceCitationKindV1 kind, string organizationId, string? projectId, string? mappingId,
        string? revisionId, ChatSelectorV1? target, string? reportDerivationId, string? subjectId, string? version, string? sectionId)
    {
        if (!Enum.IsDefined(kind) || (kind == ChatWorkspaceCitationKindV1.Report) != (target is not null) ||
            (target is null) != (reportDerivationId is null) || (kind == ChatWorkspaceCitationKindV1.GuideSection) != (sectionId is not null))
            throw new ArgumentException("The workspace citation shape is invalid.", nameof(kind));
        Kind = kind;
        OrganizationId = CatalogValueV1.RequireId(organizationId, nameof(organizationId));
        ProjectId = projectId is null ? null : CatalogValueV1.RequireId(projectId, nameof(projectId));
        MappingId = mappingId is null ? null : CatalogValueV1.RequireId(mappingId, nameof(mappingId));
        RevisionId = revisionId is null ? null : CatalogValueV1.RequireId(revisionId, nameof(revisionId));
        Target = target;
        ReportDerivationId = reportDerivationId is null ? null : CatalogValueV1.RequireId(reportDerivationId, nameof(reportDerivationId));
        SubjectId = subjectId is null ? null : CatalogValueV1.RequireId(subjectId, nameof(subjectId));
        Version = version is null ? null : CatalogValueV1.RequireText(version, 20, nameof(version));
        SectionId = sectionId is null ? null : CatalogValueV1.RequireText(sectionId, 64, nameof(sectionId));
    }

    public ChatWorkspaceCitationKindV1 Kind { get; }
    public string OrganizationId { get; }
    public string? ProjectId { get; }
    public string? MappingId { get; }
    public string? RevisionId { get; }
    /// <summary>The report resource, for a report citation.</summary>
    public ChatSelectorV1? Target { get; }
    /// <summary>The exact retained report attempt the resource belongs to, for a report citation.</summary>
    public string? ReportDerivationId { get; }
    /// <summary>The run, case, approval or environment identity.</summary>
    public string? SubjectId { get; }
    /// <summary>The regression case or build environment version, as a canonical decimal.</summary>
    public string? Version { get; }
    /// <summary>The user guide section, for a guide citation.</summary>
    public string? SectionId { get; }
}

public sealed record ChatWorkspaceCitationEventV1 : ChatTurnEventV1
{
    public ChatWorkspaceCitationEventV1(int sequence, string roundId, ChatWorkspaceCitationV1 citation) : base(sequence)
    {
        RoundId = CatalogValueV1.RequireId(roundId, nameof(roundId));
        Citation = citation ?? throw new ArgumentNullException(nameof(citation));
    }

    public string RoundId { get; }
    public ChatWorkspaceCitationV1 Citation { get; }
}

[JsonConverter(typeof(ChatPayloadSourceConverterV1))]
public enum ChatPayloadSourceV1 { PreviewInput = 1, PreviewOutput, RegressionCase, RegressionRunOutput }

/// <summary>Sample payload bytes a tool result shared with the turn's model: identities and the byte count, never content.</summary>
public sealed record ChatDataSharedEventV1 : ChatTurnEventV1
{
    public ChatDataSharedEventV1(int sequence, string roundId, string projectId, ChatPayloadSourceV1 source, string subjectId, string? version,
        int bytes) : base(sequence)
    {
        if (!Enum.IsDefined(source) || bytes < 1) throw new ArgumentOutOfRangeException(nameof(source));
        RoundId = CatalogValueV1.RequireId(roundId, nameof(roundId));
        ProjectId = CatalogValueV1.RequireId(projectId, nameof(projectId));
        Source = source;
        SubjectId = CatalogValueV1.RequireId(subjectId, nameof(subjectId));
        Version = version is null ? null : CatalogValueV1.RequireText(version, 20, nameof(version));
        Bytes = bytes;
    }

    public string RoundId { get; }
    public string ProjectId { get; }
    public ChatPayloadSourceV1 Source { get; }
    public string SubjectId { get; }
    public string? Version { get; }
    public int Bytes { get; }
}

[JsonConverter(typeof(ChatMappingWriteOutcomeConverterV1))]
public enum ChatMappingWriteOutcomeV1 { Saved = 1, Proposed }

[JsonConverter(typeof(ChatMappingWriteReasonConverterV1))]
public enum ChatMappingWriteReasonV1 { NotPermitted = 1, OutOfDate }

/// <summary>One field's plain-language rule before and after the edit, derived from verified projections.</summary>
public sealed record ChatMappingChangeV1(string Field, string Before, string After);

/// <summary>
/// A mapping edit this turn saved as the next version of its exact base, or showed read-only because the user may
/// not edit or the base is no longer the latest. Saving never puts a version in use or starts a build or run.
/// </summary>
public sealed record ChatMappingWriteEventV1 : ChatTurnEventV1
{
    public ChatMappingWriteEventV1(int sequence, string roundId, ChatMappingWriteOutcomeV1 outcome, ChatMappingWriteReasonV1? reason,
        CatalogRevisionScopeV1 baseRevision, string baseSequence, string? revisionId, string? revisionSequence, string? headRevisionId,
        string? headSequence, int operationCount, IReadOnlyList<ChatMappingChangeV1> changes, bool changesTruncated) : base(sequence)
    {
        if (!Enum.IsDefined(outcome) || reason is { } r && !Enum.IsDefined(r) || operationCount < 1) throw new ArgumentOutOfRangeException(nameof(outcome));
        RoundId = CatalogValueV1.RequireId(roundId, nameof(roundId));
        Outcome = outcome; Reason = reason;
        BaseRevision = baseRevision ?? throw new ArgumentNullException(nameof(baseRevision));
        BaseSequence = CatalogValueV1.RequireText(baseSequence, 20, nameof(baseSequence));
        RevisionId = revisionId is null ? null : CatalogValueV1.RequireId(revisionId, nameof(revisionId));
        RevisionSequence = revisionSequence is null ? null : CatalogValueV1.RequireText(revisionSequence, 20, nameof(revisionSequence));
        HeadRevisionId = headRevisionId is null ? null : CatalogValueV1.RequireId(headRevisionId, nameof(headRevisionId));
        HeadSequence = headSequence is null ? null : CatalogValueV1.RequireText(headSequence, 20, nameof(headSequence));
        OperationCount = operationCount;
        Changes = changes ?? throw new ArgumentNullException(nameof(changes));
        ChangesTruncated = changesTruncated;
    }

    public string RoundId { get; }
    public ChatMappingWriteOutcomeV1 Outcome { get; }
    public ChatMappingWriteReasonV1? Reason { get; }
    public CatalogRevisionScopeV1 BaseRevision { get; }
    public string BaseSequence { get; }
    /// <summary>The saved version; null for a proposal.</summary>
    public string? RevisionId { get; }
    public string? RevisionSequence { get; }
    /// <summary>The newer version an out-of-date proposal observed.</summary>
    public string? HeadRevisionId { get; }
    public string? HeadSequence { get; }
    public int OperationCount { get; }
    public IReadOnlyList<ChatMappingChangeV1> Changes { get; }
    public bool ChangesTruncated { get; }
}

internal sealed class ChatMappingWriteOutcomeConverterV1 : JsonStringEnumConverter<ChatMappingWriteOutcomeV1>
{
    public ChatMappingWriteOutcomeConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class ChatMappingWriteReasonConverterV1 : JsonStringEnumConverter<ChatMappingWriteReasonV1>
{
    public ChatMappingWriteReasonConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class ChatConversationKindConverterV1 : JsonStringEnumConverter<ChatConversationKindV1>
{
    public ChatConversationKindConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class ChatTurnFocusStateConverterV1 : JsonStringEnumConverter<ChatTurnFocusStateV1>
{
    public ChatTurnFocusStateConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class ChatWorkspaceCitationKindConverterV1 : JsonStringEnumConverter<ChatWorkspaceCitationKindV1>
{
    public ChatWorkspaceCitationKindConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class ChatPayloadSourceConverterV1 : JsonStringEnumConverter<ChatPayloadSourceV1>
{
    public ChatPayloadSourceConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}
