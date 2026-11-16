using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// Only constructed from a verified exact retained report and an existing resource or emitted diagnostic ordinal,
/// or, for a workspace conversation, from a fresh authorized read of the cited resource.
/// </summary>
public sealed class ChatValidatedCitation
{
    private ChatValidatedCitation(ChatCitationFact facts) => Facts = facts;
    private ChatValidatedCitation(ChatWorkspaceCitationFact workspace) => Workspace = workspace;
    /// <summary>A revision conversation's citation of its pinned reports; null for a workspace citation.</summary>
    public ChatCitationFact? Facts { get; }
    /// <summary>A workspace conversation's citation; null for a revision citation.</summary>
    public ChatWorkspaceCitationFact? Workspace { get; }
    public override string ToString() => nameof(ChatValidatedCitation);
    public static ChatValidatedCitation Validate(ChatConversationIntent conversation, VerifiedRevisionReport report, ChatCitationTarget target)
    {
        ArgumentNullException.ThrowIfNull(conversation); ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(target);
        var metadata = report.Metadata;
        var pin = new ChatReportPin(metadata.CacheEntryId, metadata.DerivationId, metadata.Key.Fingerprint, metadata.DerivedObject.Digest);
        if (conversation.Key.Revision is not { } revision || metadata.Key.Scope != revision) throw ChatBounds.Invalid();
        if (!Exists(report, target, report is VerifiedProjectionReport ? pin == conversation.Projection : pin == conversation.Validation))
            throw ChatBounds.Invalid();
        return new(new ChatCitationFact(revision, pin, target));
    }

    /// <summary>A resource of the exact verified report it was read from, at any revision of the conversation's organization.</summary>
    public static ChatValidatedCitation ValidateWorkspaceReport(ChatConversationIntent conversation, VerifiedRevisionReport report,
        ChatCitationTarget target)
    {
        ArgumentNullException.ThrowIfNull(conversation); ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(target);
        var metadata = report.Metadata;
        var pin = new ChatReportPin(metadata.CacheEntryId, metadata.DerivationId, metadata.Key.Fingerprint, metadata.DerivedObject.Digest);
        if (conversation.Key.Kind != ChatConversationKind.Workspace || metadata.Key.Scope.OrganizationId != conversation.Key.OrganizationId ||
            !Exists(report, target, true)) throw ChatBounds.Invalid();
        return new(ChatWorkspaceCitationFact.ReportResource(metadata.Key.Scope, pin, target));
    }

    /// <summary>
    /// A catalog, run, control or guide resource the caller has just read under fresh authority. Report resources
    /// require <see cref="ValidateWorkspaceReport"/>, which checks the resource inside its exact report.
    /// </summary>
    public static ChatValidatedCitation ForReadResource(ChatConversationIntent conversation, ChatWorkspaceCitationFact fact)
    {
        ArgumentNullException.ThrowIfNull(conversation); ArgumentNullException.ThrowIfNull(fact);
        if (conversation.Key.Kind != ChatConversationKind.Workspace || fact.OrganizationId != conversation.Key.OrganizationId ||
            fact.Kind == ChatWorkspaceCitationKind.Report || !Enum.IsDefined(fact.Kind)) throw ChatBounds.Invalid();
        return new(fact);
    }

    private static bool Exists(VerifiedRevisionReport report, ChatCitationTarget target, bool pinned)
    {
        switch (report)
        {
            case VerifiedProjectionReport projection when pinned:
                var document = projection.Report.Document;
                return target.Kind switch
                {
                    ChatCitationKind.Boundary => document?.Boundaries.Any(v => v.Id == target.ResourceId) == true,
                    ChatCitationKind.Field => document?.Fields.Any(v => v.Id == target.ResourceId) == true,
                    ChatCitationKind.Parameter => document?.Parameters.Any(v => v.Id == target.ResourceId) == true,
                    ChatCitationKind.Rule => document?.Rules.Any(v => v.Id == target.ResourceId) == true,
                    ChatCitationKind.Control => document?.Controls.Any(v => v.Id == target.ResourceId) == true,
                    ChatCitationKind.ProjectionReport => true,
                    // Operation Diagnostics, not Document.Diagnostics or Coverage.Gaps; no hidden concatenation.
                    ChatCitationKind.ProjectionDiagnostic => target.DiagnosticOrdinal is { } ordinal && ordinal < projection.Report.Diagnostics.Count,
                    _ => false
                };
            case VerifiedValidationReport validation when pinned:
                return target.Kind == ChatCitationKind.ValidationReport || target.Kind == ChatCitationKind.ValidationDiagnostic &&
                    target.DiagnosticOrdinal is { } validationOrdinal && validationOrdinal < validation.Report.Diagnostics.Count;
            case VerifiedProjectionReport or VerifiedValidationReport:
                return false;
            default: throw ChatBounds.Invalid();
        }
    }
}

public sealed record ChatCitationEventData
{
    public ChatCitationEventData(Guid eventId, ChatValidatedCitation citation)
    {
        ChatBounds.Id(eventId); ArgumentNullException.ThrowIfNull(citation);
        EventId = eventId; Citation = citation;
        (Kind, MetadataJson) = citation.Facts is { } facts
            ? (ChatEventKind.Citation, ChatMetadataCodec.Citation(facts))
            : (ChatEventKind.WorkspaceCitation, ChatMetadataCodec.WorkspaceCitation(citation.Workspace!));
    }
    public Guid EventId { get; }
    public ChatValidatedCitation Citation { get; }
    /// <summary><see cref="ChatEventKind.Citation"/> in a revision conversation, <see cref="ChatEventKind.WorkspaceCitation"/> otherwise.</summary>
    public ChatEventKind Kind { get; }
    public string MetadataJson { get; }
    public override string ToString() => nameof(ChatCitationEventData);
}
