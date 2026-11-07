using Weft.Domain;

namespace Weft.Deployment.Triggers;

public enum TriggerOrderAction
{
    RecoverDurableOutputs, PayloadUnavailable, RecoverOriginal, RecordedFailure, IdempotencyConflict, Refused, ReleaseSuperseded
}

/// <summary>
/// Metadata supplied by a trusted durable lookup, not proof of database integrity or authorization. A bare invocation
/// result has no order identity. Its retained binding must match before it can classify a failed-order correction.
/// Only response presence is captured; private response bytes are neither retained nor exposed by this policy DTO.
/// </summary>
public sealed class RetainedTriggerInvocationObservation
{
    private readonly MachineInvocationResult result;

    public RetainedTriggerInvocationObservation(Guid workId, Guid requestId, Sha256Digest requestDigest, TriggerWorkPin pin,
        MachineInvocationResult result)
    {
        ArgumentNullException.ThrowIfNull(requestDigest); ArgumentNullException.ThrowIfNull(pin); ArgumentNullException.ThrowIfNull(result);
        if (workId == Guid.Empty || requestId == Guid.Empty) throw new ArgumentException("Invalid retained invocation observation binding.");
        WorkId = workId; RequestId = requestId; RequestDigest = requestDigest; Pin = pin;
        this.result = result with { Response = result.Response is null ? null : [] };
    }

    public Guid WorkId { get; }
    public Guid RequestId { get; }
    public Sha256Digest RequestDigest { get; }
    public TriggerWorkPin Pin { get; }

    public TriggerOrderAction ClassifyFor(StoredQueueOrderWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (WorkId != work.WorkId || RequestId != work.RequestId || RequestDigest != work.RequestDigest || Pin != work.Pin)
            throw new ArgumentException("Retained invocation observation does not match the original work.");
        return TriggerOrderDisposition.Classify(result, work.Pin);
    }

    public override string ToString() => nameof(RetainedTriggerInvocationObservation);
}

/// <summary>
/// Per-order interpretation only. Recovery always uses original durable work; no result here authorizes execution or sending.
/// Completed output must come from durable receipts, never a remap or a reconstruction of the HTTP response.
/// </summary>
public static class TriggerOrderDisposition
{
    public static TriggerOrderAction Classify(MachineInvocationResult? result, TriggerWorkPin originalPin)
    {
        ArgumentNullException.ThrowIfNull(originalPin);
        if (result is null) return TriggerOrderAction.RecoverOriginal;
        if (!Enum.IsDefined(result.Status) || result.Outcome is { } outcome && !Enum.IsDefined(outcome) || result.ReleaseId == Guid.Empty)
            throw new ArgumentException("Invalid trigger invocation observation.");
        var action = result.Status switch
        {
            MachineInvocationStatus.Completed when result.Outcome == MachineInvocationOutcome.Completed && result.Response is not null
                => TriggerOrderAction.RecoverDurableOutputs,
            MachineInvocationStatus.Expired when result.Outcome == MachineInvocationOutcome.Completed && result.Response is null
                => TriggerOrderAction.PayloadUnavailable,
            MachineInvocationStatus.Failed when result.Response is null && result.Outcome is
                MachineInvocationOutcome.Refused or MachineInvocationOutcome.InvalidInput or MachineInvocationOutcome.InvalidParameter or
                MachineInvocationOutcome.ExecutionFailed or MachineInvocationOutcome.ResourceLimit or MachineInvocationOutcome.IntegrityFailure or
                MachineInvocationOutcome.Unsupported or MachineInvocationOutcome.Interrupted or MachineInvocationOutcome.AttemptsExhausted
                => result.Outcome == MachineInvocationOutcome.Refused ? TriggerOrderAction.Refused : TriggerOrderAction.RecordedFailure,
            MachineInvocationStatus.Conflict when Unconfirmed(result) => TriggerOrderAction.IdempotencyConflict,
            MachineInvocationStatus.Refused when Unconfirmed(result) => TriggerOrderAction.Refused,
            MachineInvocationStatus.InProgress or MachineInvocationStatus.NoRelease when Unconfirmed(result)
                => TriggerOrderAction.RecoverOriginal,
            MachineInvocationStatus.Unavailable when result.Response is null && !result.Replayed && result.Outcome is
                null or MachineInvocationOutcome.Unavailable or MachineInvocationOutcome.Busy or MachineInvocationOutcome.ReleaseChanged
                => result.Outcome == MachineInvocationOutcome.ReleaseChanged ? TriggerOrderAction.ReleaseSuperseded : TriggerOrderAction.RecoverOriginal,
            _ => throw new ArgumentException("Invalid trigger invocation observation.")
        };
        // An observation from a different release cannot silently repin durable trigger work.
        return result.ReleaseId is { } releaseId && releaseId != originalPin.Provenance.ReleaseId
            ? TriggerOrderAction.ReleaseSuperseded : action;
    }

    public static TriggerOrderAction ObserveActiveRelease(TriggerWorkPin originalPin, Guid? activeReleaseId)
    {
        ArgumentNullException.ThrowIfNull(originalPin);
        if (activeReleaseId == Guid.Empty) throw new ArgumentException("Invalid active release observation.");
        return activeReleaseId is { } releaseId && releaseId != originalPin.Provenance.ReleaseId
            ? TriggerOrderAction.ReleaseSuperseded : TriggerOrderAction.RecoverOriginal;
    }

    private static bool Unconfirmed(MachineInvocationResult result) => result.Outcome is null && result.Response is null && !result.Replayed;
}
