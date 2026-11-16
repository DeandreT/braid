using System.Text.Json;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Chat;
using Braid.Domain;

namespace Braid.Api.Chat;

/// <summary>Explicit public projection. Round requests, fences, instances, provider call IDs and raw metadata stay private.</summary>
internal static class ChatPublicMapper
{
    internal static ChatProfileV1 Profile(ChatModelProfile profile) => new(profile.Spec.Key.Id, profile.Spec.Key.Version,
        profile.DisplayName, profile.ProviderId, profile.Spec.Model, (profile.Spec.Capabilities & Braid.Ai.AiCapability.FunctionTools) != 0);

    internal static ChatConversationV1 Conversation(ChatConversationSummary summary)
    {
        var conversation = summary.Conversation;
        return new(Id(conversation.Key.ConversationId), CatalogPublicMapper.RevisionScope(conversation.Key.PinnedRevision),
            summary.CreatedAtUtc, conversation.RetainUntilUtc, Id(conversation.PinnedProjection.DerivationId),
            Id(conversation.PinnedValidation.DerivationId), summary.Title, summary.TurnCount, summary.LastTurnAtUtc);
    }

    /// <summary>Either kind in an organization listing: a revision conversation by its pinned revision, a workspace one by its focus.</summary>
    internal static ChatOrganizationConversationV1 OrganizationConversation(ChatConversationSummary summary)
    {
        var conversation = summary.Conversation;
        var key = conversation.Key;
        return new(Id(key.ConversationId), key.Kind == ChatConversationKind.Revision ? ChatConversationKindV1.Revision : ChatConversationKindV1.Workspace,
            key.Revision is { } revision ? CatalogPublicMapper.RevisionScope(revision) : null,
            conversation.Focus is { } focus ? new(Id(focus.Mapping.ProjectId), Id(focus.Mapping.MappingId), focus.RevisionId is { } id ? Id(id) : null) : null,
            summary.CreatedAtUtc, conversation.RetainUntilUtc, summary.Title, summary.TurnCount, summary.LastTurnAtUtc);
    }

    internal static ChatTurnV1 Turn(ChatTurnFacts facts, Guid conversationId) => new(Id(facts.Intent.TurnId), Id(conversationId),
        Phase(facts.Phase), facts.Intent.OriginalRequest, [.. facts.Intent.Envelope.Selectors.Select(Selector)],
        new(facts.Intent.Profile.ProfileId, facts.Intent.Profile.Version, facts.Intent.Profile.ProviderId, facts.Intent.Profile.ModelId),
        facts.CreatedAtUtc, facts.Intent.DeadlineUtc, facts.FinishedAtUtc, facts.EventCount, Focus(facts.Intent.Envelope.Focus));

    private static ChatTurnFocusV1? Focus(ChatTurnFocus? focus) => focus?.State switch
    {
        null => null,
        ChatTurnFocusState.None => new(ChatTurnFocusStateV1.None, null),
        ChatTurnFocusState.Unavailable => new(ChatTurnFocusStateV1.Unavailable, null),
        ChatTurnFocusState.Resolved => new(ChatTurnFocusStateV1.Resolved, CatalogPublicMapper.RevisionScope(focus.Revision!)),
        _ => throw new InvalidOperationException("The chat turn focus is unknown.")
    };

    internal static ChatSelectorV1 Selector(ChatCitationTarget target) =>
        new((ChatSelectorKindV1)(int)target.Kind, target.ResourceId, target.DiagnosticOrdinal);

    internal static ChatTurnPhaseV1 Phase(ChatTurnPhase phase) => phase switch
    {
        ChatTurnPhase.Active => ChatTurnPhaseV1.Active,
        ChatTurnPhase.Completed => ChatTurnPhaseV1.Completed,
        ChatTurnPhase.Failed => ChatTurnPhaseV1.Failed,
        ChatTurnPhase.Cancelled => ChatTurnPhaseV1.Cancelled,
        ChatTurnPhase.Interrupted => ChatTurnPhaseV1.Interrupted,
        _ => throw new InvalidOperationException("The chat turn phase is unknown.")
    };

    internal static string Name(ChatTurnEventV1 item) => item switch
    {
        ChatTurnStartedEventV1 => "turn.started",
        ChatTextDeltaEventV1 => "text.delta",
        ChatToolStartedEventV1 => "tool.started",
        ChatToolCompletedEventV1 => "tool.completed",
        ChatUsageEventV1 => "usage",
        ChatCitationEventV1 => "citation",
        ChatWorkspaceCitationEventV1 => "workspace.citation",
        ChatDataSharedEventV1 => "data.shared",
        ChatMappingWriteEventV1 => "mapping.write",
        ChatTurnCompletedEventV1 => "turn.completed",
        ChatTurnFailedEventV1 => "turn.failed",
        ChatTurnCancelledEventV1 => "turn.cancelled",
        _ => throw new InvalidOperationException("The chat event type is unknown.")
    };

    internal static bool Terminal(ChatEventKind kind) =>
        kind is ChatEventKind.Completed or ChatEventKind.Failed or ChatEventKind.Cancelled or ChatEventKind.Interrupted;

    /// <summary>Null for ledger-internal events (provider failure details, unknown round accounting).</summary>
    internal static ChatTurnEventV1? Event(ChatStoredEvent stored)
    {
        var sequence = checked((int)stored.Sequence);
        switch (stored.Kind)
        {
            case ChatEventKind.Started:
                return new ChatTurnStartedEventV1(sequence);
            case ChatEventKind.TextDelta:
                return new ChatTextDeltaEventV1(sequence, Round(stored), ChatOrchestrationEventCodec.ReadText(stored.Text));
            case ChatEventKind.ToolStarted:
            case ChatEventKind.ToolCompleted:
                using (var document = JsonDocument.Parse(stored.Text, new JsonDocumentOptions { MaxDepth = 2 }))
                {
                    var tool = document.RootElement;
                    var ordinal = tool.GetProperty("ordinal").GetInt32();
                    var name = tool.GetProperty("name").GetString()!;
                    return stored.Kind == ChatEventKind.ToolStarted
                        ? new ChatToolStartedEventV1(sequence, Round(stored), ordinal, name)
                        : new ChatToolCompletedEventV1(sequence, Round(stored), ordinal, name,
                            tool.GetProperty("resultBytes").GetInt32(), tool.GetProperty("retrievedNodes").GetInt32());
                }
            case ChatEventKind.Usage:
                return Usage(sequence, stored.Usage!);
            case ChatEventKind.RoundCompleted:
                return stored.TerminalMetadata?.Usage is { IsKnown: true } usage ? Usage(sequence, usage) : null;
            case ChatEventKind.Citation:
                var citation = stored.Citation!;
                return new ChatCitationEventV1(sequence, Round(stored), Selector(citation.Target), Id(citation.Report.DerivationId));
            case ChatEventKind.Completed:
                return new ChatTurnCompletedEventV1(sequence);
            case ChatEventKind.Cancelled:
                return new ChatTurnCancelledEventV1(sequence);
            case ChatEventKind.Interrupted:
                return new ChatTurnFailedEventV1(sequence, ChatTurnFailureV1.Interrupted, null);
            case ChatEventKind.Failed:
                var metadata = stored.TerminalMetadata;
                if (metadata?.Failure is { } failure)
                    return new ChatTurnFailedEventV1(sequence, ChatTurnFailureV1.Provider, (ChatProviderFailureV1)(int)failure.Code);
                return new ChatTurnFailedEventV1(sequence, metadata?.FinishReason switch
                {
                    ChatProviderFinishReason.OutputLimit => ChatTurnFailureV1.OutputLimit,
                    ChatProviderFinishReason.Refused => ChatTurnFailureV1.Refused,
                    ChatProviderFinishReason.ContentFiltered => ChatTurnFailureV1.ContentFiltered,
                    _ => ChatTurnFailureV1.Stopped
                }, null);
            case ChatEventKind.ProviderFailure:
                return null;
            case ChatEventKind.WorkspaceCitation:
                return new ChatWorkspaceCitationEventV1(sequence, Round(stored), WorkspaceCitation(stored.WorkspaceCitation!));
            case ChatEventKind.DataShared:
                var shared = stored.DataShared!;
                return new ChatDataSharedEventV1(sequence, Round(stored), Id(shared.ProjectId), (ChatPayloadSourceV1)(int)shared.Source,
                    Id(shared.SubjectId), shared.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture), shared.ByteCount);
            case ChatEventKind.MappingWrite:
                var write = stored.MappingWrite!;
                static string? Number(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new ChatMappingWriteEventV1(sequence, Round(stored), (ChatMappingWriteOutcomeV1)(int)write.Outcome,
                    write.Reason is { } reason ? (ChatMappingWriteReasonV1)(int)reason : null, Catalog.CatalogPublicMapper.RevisionScope(write.Base),
                    Number(write.BaseSequence)!, write.RevisionId is { } saved ? Id(saved) : null, Number(write.Sequence),
                    write.HeadRevisionId is { } head ? Id(head) : null, Number(write.HeadSequence), write.OperationCount,
                    write.Changes.Select(change => new ChatMappingChangeV1(change.Field, change.Before, change.After)).ToArray(), write.ChangesTruncated);
            default:
                throw new InvalidOperationException("The chat event kind is unknown.");
        }
    }

    internal static ChatWorkspaceCitationV1 WorkspaceCitation(ChatWorkspaceCitationFact fact) => new((ChatWorkspaceCitationKindV1)(int)fact.Kind,
        Id(fact.OrganizationId), fact.ProjectId is { } project ? Id(project) : null, fact.MappingId is { } mapping ? Id(mapping) : null,
        fact.RevisionId is { } revision ? Id(revision) : null, fact.Target is { } target ? Selector(target) : null,
        fact.Report is { } report ? Id(report.DerivationId) : null, fact.SubjectId is { } subject ? Id(subject) : null,
        fact.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture), fact.SectionId, fact.ProposalFingerprint?.Value);

    private static ChatUsageEventV1 Usage(int sequence, ChatUsageFact usage) => new(sequence, Id(usage.RoundId), usage.Origin switch
    {
        ChatUsageOrigin.ProviderObservation => ChatUsageOriginV1.Observation,
        ChatUsageOrigin.ProviderRoundTerminal => ChatUsageOriginV1.RoundTerminal,
        ChatUsageOrigin.ProviderOpenRejection => ChatUsageOriginV1.OpenRejection,
        _ => throw new InvalidOperationException("The chat usage origin is unknown.")
    }, usage.InputTokens, usage.OutputTokens, usage.TotalTokens);

    private static string Round(ChatStoredEvent stored) =>
        Id(stored.RoundId ?? throw new InvalidOperationException("A chat round event lacks its round."));

    internal static string Id(Guid value) => value.ToString("D");
}
