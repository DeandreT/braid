using Weft.Domain;

namespace Weft.Application;

public sealed partial class RevisionReportService
{
    private async Task<Acknowledgement> FinishAcknowledgedAsync(ActorContext actor, RevisionReportClaim claim,
        RevisionReportCompletion expected, CancellationToken token, RecoveryBudget recovery)
    {
        RevisionReportStoreResult finished;
        try { finished = await MetadataReadAsync(() => _metadata.FinishAsync(actor, claim, expected, token)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Awaiting the original public operation settles its transaction/connection before
            // any retained lookup. Only this exact immutable attempt can acknowledge the write.
            var retained = await RetainedAsync(actor, claim.Key.Scope, claim.Key.Operation,
                claim.DerivationId, recovery.Token);
            if (retained is null) return new(RevisionReportStoreOutcome.NotAuthorizedOrNotFound, null, null);
            MatchAttempt(claim, retained);
            if (retained.Attempt.Completion is null)
                throw new RevisionReportServiceException(RevisionReportFailureCategory.BackendUnavailable);
            MatchCompletion(claim, expected, retained.Attempt, retained.Result);
            return new(RevisionReportStoreOutcome.Applied, null, retained);
        }
        if (finished.Outcome == RevisionReportStoreOutcome.Applied)
        {
            var snapshot = finished.Snapshot ?? throw Inconsistent();
            MatchSnapshot(actor, claim.Key, snapshot);
            if (snapshot.CacheEntryId != claim.CacheEntryId || snapshot.Generation != claim.Generation ||
                snapshot.CurrentAttempt is not { } attempt) throw Inconsistent();
            MatchCompletion(claim, expected, attempt, snapshot.SelectedResult);
            return new(RevisionReportStoreOutcome.Applied, snapshot, null);
        }
        if (finished.Outcome is not (RevisionReportStoreOutcome.ClaimLost or RevisionReportStoreOutcome.NotAuthorizedOrNotFound))
            throw Inconsistent();
        return new(finished.Outcome, null, null);
    }

    private async Task<RevisionReportEnsureResult> DeliverAcknowledgedAsync(ActorContext actor,
        RevisionReportClaim claim, RevisionReportCompletion expected, Acknowledgement acknowledgement, CancellationToken token)
    {
        if (acknowledgement.Outcome == RevisionReportStoreOutcome.NotAuthorizedOrNotFound) return HiddenEnsure();
        if (acknowledgement.Outcome == RevisionReportStoreOutcome.ClaimLost)
            return RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.ClaimLost);
        if (acknowledgement.Outcome != RevisionReportStoreOutcome.Applied) throw Inconsistent();
        if (expected.IsUsable)
        {
            var result = acknowledgement.Snapshot?.SelectedResult ?? acknowledgement.Retained?.Result ?? throw Inconsistent();
            // Read the acknowledged identity itself, even if a later repair selected a different
            // result. A subsequently missing original blob is not replaced by local staged bytes.
            // This operation already owns work capacity. Public read capacity is independent;
            // two held cached reads cannot prevent an admitted derivation from being delivered.
            return EnsureFromRead(await ReadSelectedAsync(actor, result, null, token));
        }

        var snapshot = acknowledgement.Snapshot;
        if (snapshot is null)
        {
            // Retained facts, not current selection, established acknowledgement above. The
            // existing response factory needs a current failure snapshot: only an exact match
            // can shape it. A newer attempt is never substituted for the acknowledged failure.
            snapshot = await CurrentAsync(actor, claim.Key, token);
            if (snapshot is null) return HiddenEnsure();
            if (snapshot.CacheEntryId != claim.CacheEntryId || snapshot.Generation != claim.Generation ||
                snapshot.CurrentAttempt != acknowledgement.Retained?.Attempt)
                return RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.ClaimLost);
            MatchCompletion(claim, expected, snapshot.CurrentAttempt ?? throw Inconsistent(), snapshot.SelectedResult);
        }
        if (await SourceAsync(actor, claim.Key.Scope, claim.Key.Source, token) is null) return HiddenEnsure();
        return RevisionReportEnsureResult.ForObserved(RevisionReportReadResult.ForSnapshot(snapshot));
    }

    private async Task<RevisionReportEnsureResult> RecoverCanceledAsync(ActorContext actor, RevisionReportClaim claim,
        RevisionReportCompletion? expected, RevisionReportObservation? observation, CancellationToken workToken,
        CancellationToken caller, RecoveryBudget recovery)
    {
        RevisionReportEnsureResult? response = null;
        try
        {
            var token = recovery.Token;
            var retained = await RetainedAsync(actor, claim.Key.Scope, claim.Key.Operation, claim.DerivationId, token);
            if (retained is not null)
            {
                MatchAttempt(claim, retained);
                if (retained.Attempt.Completion is not null)
                {
                    if (expected is not null) MatchCompletion(claim, expected, retained.Attempt, retained.Result);
                    // Never overwrite a terminal attempt after cancellation, including an actual
                    // acknowledged usable result. Caller cancellation still wins delivery below.
                    if (expected is { IsUsable: false } && !caller.IsCancellationRequested)
                        response = await DeliverAcknowledgedAsync(actor, claim, expected,
                            new(RevisionReportStoreOutcome.Applied, null, retained), token);
                }
                else
                {
                    var failure = expected is { IsUsable: false } ? expected
                        : RevisionReportCompletion.ForFailure(caller.IsCancellationRequested || !workToken.IsCancellationRequested
                            ? RevisionReportFailureCategory.Canceled : RevisionReportFailureCategory.TimedOut, observation);
                    // The original claim carries fencing and ownership; the fresh retained read
                    // supplies authority/open-state evidence, not a manufactured replacement claim.
                    var acknowledgement = await FinishAcknowledgedAsync(actor, claim, failure, token, recovery);
                    if (!caller.IsCancellationRequested)
                        response = await DeliverAcknowledgedAsync(actor, claim, failure, acknowledgement, token);
                }
            }
        }
        catch (Exception)
        {
            // One independent fixed budget only. Failed bookkeeping leaves the pending intent or
            // immutable terminal facts untouched; it cannot extend work or clear runner quarantine.
        }
        caller.ThrowIfCancellationRequested();
        return response ?? throw new RevisionReportServiceException(workToken.IsCancellationRequested
            ? RevisionReportFailureCategory.TimedOut : RevisionReportFailureCategory.Canceled);
    }

    private static void MatchAttempt(RevisionReportClaim claim, AuthorizedRevisionReportAttempt retained)
    {
        if (retained.Key != claim.Key || retained.CacheEntryId != claim.CacheEntryId ||
            retained.Source.Actor != claim.OriginalActor || retained.Source.Source != claim.Key.Source)
            throw Inconsistent();
        MatchHeader(claim, retained.Attempt);
        if (retained.Result is { } result && (result.OriginalActor != claim.OriginalActor ||
            result.DerivedObject.Id != claim.CandidateObjectId)) throw Inconsistent();
    }

    private static void MatchHeader(RevisionReportClaim claim, RevisionReportAttemptSummary attempt)
    {
        if (attempt.DerivationId != claim.DerivationId || attempt.Generation != claim.Generation ||
            attempt.StartedAtUtc != claim.StartedAtUtc || attempt.LeaseExpiresAtUtc != claim.LeaseExpiresAtUtc ||
            attempt.RepairTargetDerivationId != claim.RepairTargetDerivationId) throw Inconsistent();
    }

    private static void MatchCompletion(RevisionReportClaim claim, RevisionReportCompletion expected,
        RevisionReportAttemptSummary attempt, RevisionReportResult? result)
    {
        MatchHeader(claim, attempt);
        if (attempt.FinishedAtUtc is null || attempt.Completion != expected) throw Inconsistent();
        if (expected.IsUsable)
        {
            if (result is null || result.Key != claim.Key || result.CacheEntryId != claim.CacheEntryId ||
                result.DerivationId != claim.DerivationId || result.Generation != claim.Generation ||
                result.OriginalActor != claim.OriginalActor || result.StartedAtUtc != claim.StartedAtUtc ||
                result.FinishedAtUtc != attempt.FinishedAtUtc || result.DerivedObject != expected.DerivedObject ||
                result.DerivedObject.Id != claim.CandidateObjectId || result.Observation != expected.Observation)
                throw Inconsistent();
        }
        else if (result is not null) throw Inconsistent();
    }

    private sealed record Acknowledgement(RevisionReportStoreOutcome Outcome,
        RevisionReportReadSnapshot? Snapshot, AuthorizedRevisionReportAttempt? Retained);

    private sealed class RecoveryBudget : IDisposable
    {
        private CancellationTokenSource? _deadline;
        internal CancellationToken Token
        {
            get
            {
                _deadline ??= new CancellationTokenSource(RecoveryLimit);
                return _deadline.Token;
            }
        }
        public void Dispose() => _deadline?.Dispose();
    }
}
