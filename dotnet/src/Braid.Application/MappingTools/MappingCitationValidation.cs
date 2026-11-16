using Braid.Domain;

namespace Braid.Application.MappingTools;

public sealed partial class MappingToolService
{
    /// <summary>
    /// All-or-nothing fresh verification of candidate targets against the conversation's exact pinned reports.
    /// Never interpret text containing a URL/ID as a verified citation; only a Valid check's citations may be persisted.
    /// </summary>
    public Task<MappingCitationCheck> ValidateCitationsAsync(ActorContext actor, ChatConversationIntent conversation,
        IReadOnlyList<ChatCitationTarget> candidates, CancellationToken cancellationToken = default) =>
        CheckCitationsAsync(actor, conversation, candidates, omitUnverified: false, cancellationToken);

    /// <summary>
    /// The same fresh check, except that a target absent from its pinned report is omitted instead of failing the
    /// whole check. A Valid result holds the verified subset in candidate order; authority, report and bound
    /// failures still yield no citations.
    /// </summary>
    public Task<MappingCitationCheck> FilterCitationsAsync(ActorContext actor, ChatConversationIntent conversation,
        IReadOnlyList<ChatCitationTarget> candidates, CancellationToken cancellationToken = default) =>
        CheckCitationsAsync(actor, conversation, candidates, omitUnverified: true, cancellationToken);

    /// <summary>Chat turn delivery: tool candidates that cannot be verified are dropped, never streamed.</summary>
    async Task<IReadOnlyList<ChatValidatedCitation>> IChatCitationPort.VerifyAsync(ActorContext actor,
        ChatConversationIntent conversation, IReadOnlyList<ChatCitationTarget> candidates, CancellationToken ct)
    {
        var check = await FilterCitationsAsync(actor, conversation, candidates, ct);
        return check.Outcome == MappingCitationOutcome.Valid ? check.Citations : Array.Empty<ChatValidatedCitation>();
    }

    private Task<MappingCitationCheck> CheckCitationsAsync(ActorContext actor, ChatConversationIntent conversation,
        IReadOnlyList<ChatCitationTarget> candidates, bool omitUnverified, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(conversation);
        // Conversations are creator-private; another principal never borrows their pins.
        if (actor.PrincipalId != conversation.Key.Creator || conversation.Key.Kind != ChatConversationKind.Revision)
            return Task.FromResult(new MappingCitationCheck(MappingCitationOutcome.NotAuthorizedOrNotFound));
        return CheckCitationsAsync(actor, MappingReportContext.Of(conversation), candidates, omitUnverified,
            (report, target) => ChatValidatedCitation.Validate(conversation, report, target), cancellationToken);
    }

    /// <summary>
    /// The filtering check over an explicit report context, for a workspace conversation: verified targets become
    /// workspace citations of the exact report they were read from.
    /// </summary>
    public Task<MappingCitationCheck> FilterWorkspaceCitationsAsync(ActorContext actor, ChatConversationIntent conversation,
        MappingReportContext reports, IReadOnlyList<ChatCitationTarget> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(reports);
        if (actor.PrincipalId != conversation.Key.Creator || conversation.Key.Kind != ChatConversationKind.Workspace ||
            reports.Revision.OrganizationId != conversation.Key.OrganizationId)
            return Task.FromResult(new MappingCitationCheck(MappingCitationOutcome.NotAuthorizedOrNotFound));
        return CheckCitationsAsync(actor, reports, candidates, omitUnverified: true,
            (report, target) => ChatValidatedCitation.ValidateWorkspaceReport(conversation, report, target), cancellationToken);
    }

    private async Task<MappingCitationCheck> CheckCitationsAsync(ActorContext actor, MappingReportContext context,
        IReadOnlyList<ChatCitationTarget> candidates, bool omitUnverified, Func<VerifiedRevisionReport, ChatCitationTarget, ChatValidatedCitation> validate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var admitted = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.Duration);
        var token = deadline.Token;
        try
        {
            var source = await AuthorizeAsync(actor, context.Revision, null, token);
            if (!capacity.Wait(0)) return new(MappingCitationOutcome.Busy);
            admitted = true;
            if (candidates is null || candidates.Count > limits.CitationCount)
                return new(MappingCitationOutcome.Invalid);
            var snapshot = new ChatCitationTarget[candidates.Count];
            for (var index = 0; index < snapshot.Length; index++)
            {
                if (candidates[index] is not { } candidate) return new(MappingCitationOutcome.Invalid);
                snapshot[index] = candidate;
            }
            var work = new MappingToolWork(limits.WorkUnits, token);
            VerifiedProjectionReport? projection = null;
            VerifiedValidationReport? validation = null;
            var validated = new List<ChatValidatedCitation>(snapshot.Length);
            foreach (var target in snapshot)
            {
                VerifiedRevisionReport report = target.Kind is ChatCitationKind.ValidationDiagnostic or ChatCitationKind.ValidationReport
                    ? validation ??= await ReadValidationAsync(actor, context, source, token)
                    : projection ??= await ReadProjectionAsync(actor, context, source, token);
                work.Charge(Cost(report, target));
                try { validated.Add(validate(report, target)); }
                catch (ArgumentException) when (omitUnverified) { }
                catch (ArgumentException) { return new(MappingCitationOutcome.Invalid); }
            }
            await AuthorizeAsync(actor, context.Revision, source, token);
            token.ThrowIfCancellationRequested();
            return new(MappingCitationOutcome.Valid, Array.AsReadOnly(validated.ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { cancellationToken.ThrowIfCancellationRequested(); throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { cancellationToken.ThrowIfCancellationRequested(); return new(MappingCitationOutcome.TimedOut); }
        catch (MappingToolRefusal refusal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(refusal.Outcome switch
            {
                MappingToolOutcome.NotAuthorizedOrNotFound => MappingCitationOutcome.NotAuthorizedOrNotFound,
                MappingToolOutcome.ReportUnavailable => MappingCitationOutcome.ReportUnavailable,
                MappingToolOutcome.ResourceLimit => MappingCitationOutcome.ResourceLimit,
                MappingToolOutcome.Busy => MappingCitationOutcome.Busy,
                _ => MappingCitationOutcome.DependencyFailure
            });
        }
        catch (RevisionReportValidationException error)
        { cancellationToken.ThrowIfCancellationRequested(); return new(error.Failure == RevisionReportValidationFailure.ResourceLimit ? MappingCitationOutcome.ResourceLimit : MappingCitationOutcome.DependencyFailure); }
        catch (RevisionReportServiceException)
        { cancellationToken.ThrowIfCancellationRequested(); return new(MappingCitationOutcome.DependencyFailure); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { cancellationToken.ThrowIfCancellationRequested(); return new(MappingCitationOutcome.DependencyFailure); }
        finally { if (admitted) capacity.Release(); }
    }

    // ChatValidatedCitation scans the one list that owns the cited kind.
    private static int Cost(VerifiedRevisionReport report, ChatCitationTarget target) => 1 + (report switch
    {
        VerifiedProjectionReport { Report.Document: { } document } => target.Kind switch
        {
            ChatCitationKind.Boundary => document.Boundaries.Count,
            ChatCitationKind.Field => document.Fields.Count,
            ChatCitationKind.Parameter => document.Parameters.Count,
            ChatCitationKind.Rule => document.Rules.Count,
            ChatCitationKind.Control => document.Controls.Count,
            _ => 0
        },
        _ => 0
    });
}
