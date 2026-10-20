using Weft.Domain;

namespace Weft.Application;

/// <summary>Only constructed from a verified exact retained report and an existing resource or emitted diagnostic ordinal.</summary>
public sealed class ChatValidatedCitation
{
    private ChatValidatedCitation(ChatCitationFact facts) => Facts = facts;
    public ChatCitationFact Facts { get; }
    public override string ToString() => nameof(ChatValidatedCitation);
    public static ChatValidatedCitation Validate(ChatConversationIntent conversation, VerifiedRevisionReport report, ChatCitationTarget target)
    {
        ArgumentNullException.ThrowIfNull(conversation); ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(target);
        var metadata = report.Metadata;
        var pin = new ChatReportPin(metadata.CacheEntryId, metadata.DerivationId, metadata.Key.Fingerprint, metadata.DerivedObject.Digest);
        if (metadata.Key.Scope != conversation.Key.Revision) throw ChatBounds.Invalid();
        bool exists;
        switch (report)
        {
            case VerifiedProjectionReport projection when pin == conversation.Projection:
                var document = projection.Report.Document;
                exists = target.Kind switch
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
                break;
            case VerifiedValidationReport validation when pin == conversation.Validation:
                exists = target.Kind == ChatCitationKind.ValidationReport || target.Kind == ChatCitationKind.ValidationDiagnostic &&
                    target.DiagnosticOrdinal is { } validationOrdinal && validationOrdinal < validation.Report.Diagnostics.Count;
                break;
            default: throw ChatBounds.Invalid();
        }
        if (!exists) throw ChatBounds.Invalid();
        return new(new(conversation.Key.Revision, pin, target));
    }
}

public sealed record ChatCitationEventData
{
    public ChatCitationEventData(Guid eventId, ChatValidatedCitation citation)
    {
        ChatBounds.Id(eventId); ArgumentNullException.ThrowIfNull(citation);
        EventId = eventId; Citation = citation; MetadataJson = ChatMetadataCodec.Citation(citation.Facts);
    }
    public Guid EventId { get; }
    public ChatValidatedCitation Citation { get; }
    public string MetadataJson { get; }
    public override string ToString() => nameof(ChatCitationEventData);
}
