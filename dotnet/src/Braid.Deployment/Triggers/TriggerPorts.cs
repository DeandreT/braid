using Braid.Application;
using Braid.Application.Regression;
using Braid.Domain;

namespace Braid.Deployment.Triggers;

public enum TriggerAction { Configure, Enable, Disable, Retire }

/// <summary>
/// One idempotent administrator operation. ExpectedGeneration is the control generation the caller read, or 0 to create.
/// Configure carries the complete new version; the other actions carry none.
/// </summary>
public sealed record TriggerOperation
{
    public TriggerOperation(Guid operationId, Guid triggerId, TriggerAction action, long expectedGeneration, TriggerConfiguration? configuration)
    {
        if (operationId == Guid.Empty || triggerId == Guid.Empty || !Enum.IsDefined(action) || expectedGeneration < 0 ||
            (action == TriggerAction.Configure) != (configuration is not null) || configuration is not null && configuration.TriggerId != triggerId ||
            action != TriggerAction.Configure && expectedGeneration == 0)
            throw new ArgumentException("Invalid trigger operation.");
        OperationId = operationId; TriggerId = triggerId; Action = action; ExpectedGeneration = expectedGeneration; Configuration = configuration;
    }
    public Guid OperationId { get; }
    public Guid TriggerId { get; }
    public TriggerAction Action { get; }
    public long ExpectedGeneration { get; }
    public TriggerConfiguration? Configuration { get; }
}

/// <summary>
/// Unchanged: the control already had that state. Stale: the expected generation is not current. Incomplete: an explicit
/// policy, a current grant for the configured caller or another required choice is missing.
/// </summary>
public enum TriggerOperationOutcome { Applied, Unchanged, Stale, Incomplete, Refused, IdempotencyConflict }

/// <summary>Generation changes on every control change. Disable and retire also fence already admitted work.</summary>
public sealed record TriggerSnapshot(TriggerConfiguration Configuration, long Version, bool Enabled, bool Retired, long Generation);

/// <summary>Administrator-only configuration and control, checked against current project authority in each transaction.</summary>
public interface ITriggerAdministrationStore
{
    Task<TriggerSnapshot?> ReadAsync(ActorContext administrator, ReleaseTargetReference target, Guid triggerId, CancellationToken token = default);
    Task<TriggerOperationOutcome> OperateAsync(ActorContext administrator, ReleaseTargetReference target, TriggerOperation operation, CancellationToken token = default);
    /// <summary>The recorded outcome of an earlier operation with the same body, or null when it was never committed.</summary>
    Task<TriggerOperationOutcome?> RecoverAsync(ActorContext administrator, ReleaseTargetReference target, TriggerOperation operation, CancellationToken token = default);
}

/// <summary>The current admitted configuration version for new work. Admitted work keeps its own version.</summary>
public sealed record TriggerDefinition(TriggerConfiguration Configuration, long Version);

public enum TriggerAdmissionStatus { Admitted, Existing, IdempotencyConflict, Refused, NoRelease, CapacityExceeded }

/// <summary>Existing returns the original work for a redelivery with identical bytes and application.</summary>
public sealed record TriggerAdmissionResult(TriggerAdmissionStatus Status, Guid? WorkId = null);

public interface ITriggerWorkStore
{
    Task<TriggerDefinition?> ReadDefinitionAsync(ReleaseTargetReference target, Guid triggerId, CancellationToken token = default);
    /// <summary>
    /// Durably admits one complete order with its exact input bytes, immutable release pin and request identity, or recovers
    /// the existing work for the same source identity. It never executes anything.
    /// </summary>
    Task<TriggerAdmissionResult> AdmitAsync(TriggerDefinition definition, QueueOrderIdentity identity, MachineCaller caller,
        ReadOnlyMemory<byte> source, Sha256Digest requestDigest, CancellationToken token = default);
}

/// <summary>Recorded returns the final outcome; Fenced and ReleaseSuperseded are permanent for this work.</summary>
public enum TriggerClaimStatus { Claimed, Recorded, InProgress, Refused, Fenced, ReleaseSuperseded }

/// <summary>A claim returns the original stored request material, never a request rebuilt from current configuration.</summary>
public sealed class TriggerWorkClaim
{
    private readonly byte[]? input;

    public TriggerWorkClaim(TriggerClaimStatus status, int attempt = 0, Guid claimId = default, TriggerWorkPin? pin = null, Guid requestId = default,
        Sha256Digest? requestDigest = null, TriggerConfiguration? configuration = null, ReadOnlySpan<byte> input = default,
        MachineInvocationOutcome? outcome = null)
    {
        if (!Enum.IsDefined(status) || (status == TriggerClaimStatus.Claimed) != (pin is not null && requestId != Guid.Empty &&
                requestDigest is not null && configuration is not null && claimId != Guid.Empty && attempt is >= 1 and <= MachineInvocationPolicy.MaximumAttempts) ||
            (status == TriggerClaimStatus.Recorded) != (outcome is not null))
            throw new ArgumentException("Invalid trigger work claim.");
        Status = status; Attempt = attempt; ClaimId = claimId; Pin = pin; RequestId = requestId; RequestDigest = requestDigest;
        Configuration = configuration; Outcome = outcome;
        this.input = status == TriggerClaimStatus.Claimed ? input.ToArray() : null;
    }

    public TriggerClaimStatus Status { get; }
    public int Attempt { get; }
    public Guid ClaimId { get; }
    public TriggerWorkPin? Pin { get; }
    public Guid RequestId { get; }
    public Sha256Digest? RequestDigest { get; }
    /// <summary>The configuration version admitted with the work, not the trigger's current version.</summary>
    public TriggerConfiguration? Configuration { get; }
    public MachineInvocationOutcome? Outcome { get; }
    public byte[]? CopyInput() => input is null ? null : (byte[])input.Clone();
    public override string ToString() => nameof(TriggerWorkClaim);
}

/// <summary>Fenced: revocation, disable, retirement or a target change ended the work. ReleaseSuperseded: the pointer moved.</summary>
public enum TriggerFinishStatus { Recorded, Fenced, Superseded, ReleaseSuperseded }

public interface ITriggeredInvocationLedger
{
    /// <summary>Binds the existing invocation ledger key to the stored pin and claims an attempt as the stored publisher.</summary>
    Task<TriggerWorkClaim> ClaimPinnedAsync(ReleaseTargetReference target, Guid workId, MachineCaller caller, CancellationToken token = default);
    /// <summary>
    /// One transaction: the final ledger outcome, the exact retained response, output receipts and bytes, and one delivery per
    /// configured route. A completed outcome is never committed without all of them.
    /// </summary>
    Task<TriggerFinishStatus> FinishAndEnqueueAsync(ReleaseTargetReference target, Guid workId, Guid claimId, Guid applicationId,
        MachineInvocationOutcome outcome, MachineInvocationProvenance? provenance, string? clock, byte[]? response, RegressionOutputSet? outputs,
        CancellationToken token = default);
}

/// <summary>
/// Completed means the outputs and every routed delivery are durable in Braid's outbox (the broker may then complete the
/// message); it says nothing about remote delivery. Retry keeps the same work and request identity.
/// </summary>
public enum TriggeredInvocationStatus { Completed, Failed, Retry, InProgress, Refused, Fenced, ReleaseSuperseded }

public sealed record TriggeredInvocationResult(TriggeredInvocationStatus Status, MachineInvocationOutcome? Outcome = null);

/// <summary>Settled: a due delivery was fenced, superseded or exhausted without a send; claim again for the next one.</summary>
public enum TriggerDeliveryClaimStatus { Claimed, None, Settled }

public sealed record TriggerDeliveryClaim(TriggerDeliveryClaimStatus Status, Guid ClaimId = default, int Send = 0, DateTimeOffset Deadline = default,
    FrozenTriggerDelivery? Delivery = null, TriggerRoute? Route = null)
{
    public override string ToString() => nameof(TriggerDeliveryClaim);
}

/// <summary>A live claim on one scheduled run's scan and the persisted cursor it continues from.</summary>
public sealed record TriggerRunClaim(Guid RunId, Guid ClaimId, string? Cursor, DateTimeOffset ScheduledAt)
{
    public override string ToString() => nameof(TriggerRunClaim);
}

/// <summary>Claimed: a new run for the due occurrence. Recorded: missed, skipped or already finished. InProgress: scanning elsewhere.</summary>
public enum TriggerOccurrenceStatus { Claimed, Recorded, InProgress, Refused }

public sealed record TriggerOccurrenceResult(TriggerOccurrenceStatus Status, TriggerRunClaim? Claim = null);

/// <summary>Claimed: the unfinished run's lease lapsed and this worker resumes it. Fenced: its configuration was revised.</summary>
public enum TriggerRunResumeStatus { Claimed, None, InProgress, Refused, Fenced }

public sealed record TriggerRunResumption(TriggerRunResumeStatus Status, TriggerRunClaim? Claim = null);

/// <summary>The current version of a schedule trigger and the latest occurrence recorded for its current schedule.</summary>
public sealed record TriggerScheduleState(long Version, DateTimeOffset? LatestRecorded);

/// <summary>One complete order to admit: its stable source identity (with its explicit key for a batch child) and exact bytes.</summary>
public sealed class TriggerDiscoveredOrder
{
    private readonly byte[] content;

    public TriggerDiscoveredOrder(QueueOrderIdentity identity, ReadOnlySpan<byte> content, Sha256Digest requestDigest)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(requestDigest);
        if (content.Length > MachineInvocationPolicy.MaximumRetainedBytes) throw new ArgumentException("The order exceeds the retained byte bound.");
        Identity = identity; RequestDigest = requestDigest; this.content = content.ToArray();
    }

    public QueueOrderIdentity Identity { get; }
    public Sha256Digest RequestDigest { get; }
    public int Length => content.Length;
    public byte[] CopyContent() => (byte[])content.Clone();
    public override string ToString() => nameof(TriggerDiscoveredOrder);
}

/// <summary>Counts for one page: listed items, versions that changed or vanished before they were read, and rejected items.</summary>
public sealed record TriggerScanCounts(int Discovered, int Changed, int Rejected, IReadOnlyList<(string Source, string Reason)>? Quarantine = null);

/// <summary>Stale: the claim or cursor moved on and nothing was written. Fenced: the run's configuration was revised.</summary>
public enum TriggerPageStatus { Recorded, Stale, Refused, NoRelease, CapacityExceeded, Fenced }

public sealed record TriggerPageResult(TriggerPageStatus Status, IReadOnlyList<TriggerAdmissionResult> Orders);

/// <summary>Admitted and Existing carry every order's work in envelope order; an existing parent never runs a changed manifest.</summary>
public sealed record TriggerBatchAdmission(TriggerAdmissionStatus Status, Guid? RunId = null, IReadOnlyList<Guid>? Works = null);

/// <summary>
/// Scheduled runs and batch parents for the restricted trigger worker. A run records discovery only; every order keeps its own
/// ledger boundary and pin. Occurrences are unique per schedule epoch and instant, and at most one run of a trigger scans at a time.
/// </summary>
public interface ITriggerRunStore
{
    Task<TriggerScheduleState?> ReadScheduleAsync(ReleaseTargetReference target, Guid triggerId, CancellationToken token = default);
    Task<TriggerRunResumption> ResumeRunAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, CancellationToken token = default);
    /// <summary>Records the missed occurrences and claims a new run for <paramref name="run"/>, or records it skipped behind an unfinished run.</summary>
    Task<TriggerOccurrenceResult> RecordOccurrencesAsync(TriggerDefinition definition, MachineCaller caller, IReadOnlyList<ScheduledOccurrence> missed,
        ScheduledOccurrence? run, CancellationToken token = default);
    /// <summary>The subset of these source identities already admitted for this trigger's source binding.</summary>
    Task<IReadOnlySet<string>> ReadKnownSourcesAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, IReadOnlyList<string> sources,
        CancellationToken token = default);
    /// <summary>
    /// One transaction: the orders and the next cursor. A null <paramref name="cursorAfter"/> on an incomplete pass keeps the cursor,
    /// so one source page may be admitted in bounded parts.
    /// </summary>
    Task<TriggerPageResult> RecordPageAsync(TriggerDefinition definition, MachineCaller caller, TriggerRunClaim claim, string? cursorBefore, string? cursorAfter,
        bool complete, TriggerScanCounts counts, IReadOnlyList<TriggerDiscoveredOrder> orders, CancellationToken token = default);
    /// <summary>The oldest admitted orders without a final outcome, to execute again under their original pins.</summary>
    Task<IReadOnlyList<Guid>> ReadPendingWorkAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, int maximum, CancellationToken token = default);
    Task<TriggerBatchAdmission> AdmitBatchAsync(TriggerDefinition definition, string messageId, MachineCaller caller, Sha256Digest manifestDigest,
        IReadOnlyList<TriggerDiscoveredOrder> orders, CancellationToken token = default);
}

/// <summary>
/// Accepted: receipt was confirmed out of band. Abandoned: no further send. Resend: send the same delivery ID and bytes again,
/// which the operator explicitly accepts may duplicate a delivery the receiver already took.
/// </summary>
public enum TriggerDeliveryResolution { Accepted, Abandoned, Resend }

/// <summary>Stale: the delivery is no longer unknown or its send count moved. Refused: no authority, scope or send budget.</summary>
public enum TriggerResolutionOutcome { Applied, Stale, Refused, IdempotencyConflict }

/// <summary>
/// Operations metadata for triggers, runs and deliveries (no payloads, digests or source identities), and the administrator-only,
/// version-conditional, idempotent resolution of a delivery whose remote outcome is unknown. Every resolution is journaled.
/// </summary>
public interface ITriggerOperationsStore
{
    Task<System.Text.Json.JsonElement?> ReadTriggerOperationsAsync(ActorContext actor, MappingScope mapping, Guid target, string section, int offset, int limit,
        CancellationToken token = default);
    Task<TriggerResolutionOutcome> ResolveDeliveryAsync(ActorContext administrator, ReleaseTargetReference target, Guid deliveryId, Guid operationId,
        int expectedSends, TriggerDeliveryResolution resolution, CancellationToken token = default);
}

/// <summary>Late observations are retained as history for reconciliation; they never change a newer state.</summary>
public enum TriggerObservationStatus { Recorded, Late }

public interface ITriggerDeliveryStore
{
    /// <summary>Claims the oldest due delivery for one destination after rechecking live authority against the pin.</summary>
    Task<TriggerDeliveryClaim> ClaimAsync(ReleaseTargetReference target, Guid destinationBindingId, CancellationToken token = default);
    /// <summary>Accepted, ProvenKnownNotAccepted, Unknown or PermanentFailure for the exact claimed attempt.</summary>
    Task<TriggerObservationStatus> RecordObservationAsync(ReleaseTargetReference target, Guid deliveryId, Guid claimId, DeliveryObservationKind kind,
        CancellationToken token = default);
}
