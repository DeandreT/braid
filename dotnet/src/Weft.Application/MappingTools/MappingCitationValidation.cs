using Weft.Domain;

namespace Weft.Application.MappingTools;

public sealed partial class MappingToolService
{
    /// <summary>
    /// All-or-nothing fresh verification of candidate targets against the conversation's exact pinned reports.
    /// Never interpret text containing a URL/ID as a verified citation; only a Valid check's citations may be persisted.
    /// </summary>
    public async Task<MappingCitationCheck> ValidateCitationsAsync(ActorContext actor, ChatConversationIntent conversation,
        IReadOnlyList<ChatCitationTarget> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(conversation);
        cancellationToken.ThrowIfCancellationRequested();
        var admitted = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.Duration);
        var token = deadline.Token;
        try
        {
            var source = await AuthorizeAsync(actor, conversation, null, token);
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
            var validated = new ChatValidatedCitation[snapshot.Length];
            for (var index = 0; index < snapshot.Length; index++)
            {
                var target = snapshot[index];
                VerifiedRevisionReport report = target.Kind is ChatCitationKind.ValidationDiagnostic or ChatCitationKind.ValidationReport
                    ? validation ??= await ReadValidationAsync(actor, conversation, source, token)
                    : projection ??= await ReadProjectionAsync(actor, conversation, source, token);
                work.Charge(Cost(report, target));
                try { validated[index] = ChatValidatedCitation.Validate(conversation, report, target); }
                catch (ArgumentException) { return new(MappingCitationOutcome.Invalid); }
            }
            await AuthorizeAsync(actor, conversation, source, token);
            token.ThrowIfCancellationRequested();
            return new(MappingCitationOutcome.Valid, Array.AsReadOnly(validated));
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
