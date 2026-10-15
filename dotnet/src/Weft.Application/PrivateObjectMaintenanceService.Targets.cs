namespace Weft.Application;

public sealed partial class PrivateObjectMaintenanceService
{
    private async Task ProcessTargetAsync(RunState state, int index, RunDeadline deadline)
    {
        var claim = state.Batch!.Claims[index];
        state.Targets[index] = new(index, claim, PrivateObjectMaintenancePhysicalObservation.Unresolved,
            PrivateObjectMaintenanceAcknowledgement.NotAttempted);
        PrivateObjectRetirementOutcome actual;
        try { actual = await _retirementStore.RetireAsync(claim, deadline.WorkToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.Stopped)
        {
            state.Targets[index] = new(index, claim, deadline.CallerCanceled ? PrivateObjectMaintenancePhysicalObservation.Canceled :
                PrivateObjectMaintenancePhysicalObservation.Deadline, PrivateObjectMaintenanceAcknowledgement.NotAttempted);
            return;
        }
        catch (Exception error) { state.Failure = StorageFailure(error); return; }
        var physical = actual switch
        {
            PrivateObjectRetirementOutcome.Deleted => PrivateObjectMaintenancePhysicalObservation.Deleted,
            PrivateObjectRetirementOutcome.Missing => PrivateObjectMaintenancePhysicalObservation.Missing,
            PrivateObjectRetirementOutcome.Busy => PrivateObjectMaintenancePhysicalObservation.Busy,
            PrivateObjectRetirementOutcome.Unsupported => PrivateObjectMaintenancePhysicalObservation.Unsupported,
            PrivateObjectRetirementOutcome.UnsafeStorage => PrivateObjectMaintenancePhysicalObservation.UnsafeStorage,
            PrivateObjectRetirementOutcome.Io => PrivateObjectMaintenancePhysicalObservation.Io,
            _ => throw new InvalidRunEvidence()
        };
        state.Targets[index] = new(index, claim, physical, PrivateObjectMaintenanceAcknowledgement.NotAttempted);
        if (PrivateObjectMaintenanceTargetResult.DispositionFor(physical) is not { } disposition || !deadline.CanStart) return;
        state.Targets[index] = new(index, claim, physical, PrivateObjectMaintenanceAcknowledgement.Unresolved);
        ObjectCleanupFinishResult result;
        try
        {
            result = await _metadata.FinishAsync(claim, disposition, deadline.WorkToken, deadline.SettlementToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var stoppedCancellation = error is OperationCanceledException && deadline.Stopped;
            var lateFault = error is not OperationCanceledException && deadline.Stopped;
            if (lateFault) state.Failure = PrivateObjectMaintenanceRunFailure.MetadataUnavailable;
            var recovered = await RecoverAsync(state, index, disposition, deadline).ConfigureAwait(false);
            if (!recovered && !stoppedCancellation) state.Failure ??= PrivateObjectMaintenanceRunFailure.MetadataUnavailable;
            return;
        }
        if (result is null) throw new InvalidRunEvidence();
        switch (result.Outcome)
        {
            case ObjectCleanupFinishOutcome.Applied:
            case ObjectCleanupFinishOutcome.AlreadyApplied:
                if (!Acknowledges(result.Attempt, claim, disposition)) throw new InvalidRunEvidence();
                state.Targets[index] = new(index, claim, physical, result.Outcome == ObjectCleanupFinishOutcome.Applied
                    ? PrivateObjectMaintenanceAcknowledgement.Applied : PrivateObjectMaintenanceAcknowledgement.AlreadyApplied, disposition);
                break;
            case ObjectCleanupFinishOutcome.ClaimLost:
            case ObjectCleanupFinishOutcome.SafetyRefusal:
                if (result.Attempt is not null) throw new InvalidRunEvidence();
                state.Targets[index] = new(index, claim, physical, result.Outcome == ObjectCleanupFinishOutcome.ClaimLost
                    ? PrivateObjectMaintenanceAcknowledgement.ClaimLost : PrivateObjectMaintenanceAcknowledgement.SafetyRefusal);
                break;
            default: throw new InvalidRunEvidence();
        }
    }

    private async Task<bool> RecoverAsync(RunState state, int index, ObjectCleanupDisposition disposition, RunDeadline deadline)
    {
        if (!deadline.CanRecover) return false;
        var claim = state.Batch!.Claims[index];
        try
        {
            // The original Finish await (including adapter disposal) has already settled.
            var retained = await _metadata.GetAttemptAsync(state.Project, claim.Retirement.RetirementId,
                claim.Generation, deadline.SettlementToken).ConfigureAwait(false);
            if (retained is not null && (retained.Claim != claim ||
                (retained.Disposition is not null && retained.Disposition != disposition)))
            {
                state.Failure = PrivateObjectMaintenanceRunFailure.InvalidEvidence;
                return false;
            }
            if (!Acknowledges(retained, claim, disposition)) return false;
            state.Targets[index] = new(index, claim, state.Targets[index].Physical,
                PrivateObjectMaintenanceAcknowledgement.Recovered, disposition);
            return true;
        }
        catch (OperationCanceledException) when (!deadline.CanRecover) { return false; }
        catch (Exception) { state.Failure ??= PrivateObjectMaintenanceRunFailure.MetadataUnavailable; return false; }
    }

    private static bool Acknowledges(ObjectCleanupAttempt? attempt, ObjectCleanupClaim claim, ObjectCleanupDisposition disposition) =>
        attempt is not null && attempt.Claim == claim && attempt.Disposition == disposition && attempt.FinishedAtUtc is not null;
}
