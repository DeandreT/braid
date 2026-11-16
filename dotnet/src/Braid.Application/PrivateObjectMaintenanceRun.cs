using Braid.Domain;

namespace Braid.Application;

public enum PrivateObjectMaintenanceRunOutcome { Finished = 1, Canceled = 2, Deadline = 3, Failed = 4 }
public enum PrivateObjectMaintenanceRunFailure
{
    MetadataUnavailable = 1, StorageUnavailable = 2, UnsafeStorage = 3,
    UnsupportedStorage = 4, ObservationChanged = 5, InvalidEvidence = 6
}
public enum PrivateObjectMaintenancePhysicalObservation
{
    NotStarted = 1, Deleted = 2, Missing = 3, Busy = 4, Unsupported = 5,
    UnsafeStorage = 6, Io = 7, Canceled = 8, Deadline = 9, Unresolved = 10
}
public enum PrivateObjectMaintenanceAcknowledgement
{
    NotAttempted = 1, Applied = 2, AlreadyApplied = 3, Recovered = 4,
    ClaimLost = 5, SafetyRefusal = 6, Unresolved = 7
}

/// <summary>Sanitized per-target observations, without private claims or object identifiers.</summary>
public sealed class PrivateObjectMaintenanceTargetResult
{
    internal PrivateObjectMaintenanceTargetResult(int ordinal, ObjectCleanupClaim claim,
        PrivateObjectMaintenancePhysicalObservation physical, PrivateObjectMaintenanceAcknowledgement acknowledgement,
        ObjectCleanupDisposition? acknowledgedDisposition = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        MaintenanceValue.Count(ordinal, ObjectCleanupPolicy.MaximumTargets - 1, nameof(ordinal));
        MaintenanceValue.Known(physical, nameof(physical));
        MaintenanceValue.Known(acknowledgement, nameof(acknowledgement));
        var acknowledged = acknowledgement is PrivateObjectMaintenanceAcknowledgement.Applied or
            PrivateObjectMaintenanceAcknowledgement.AlreadyApplied or PrivateObjectMaintenanceAcknowledgement.Recovered;
        if (acknowledged != acknowledgedDisposition.HasValue || (acknowledged &&
            (DispositionFor(physical) is not { } expected || expected != acknowledgedDisposition)))
            throw new ArgumentException("Maintenance acknowledgement must match an actual physical observation.");
        if (acknowledgement != PrivateObjectMaintenanceAcknowledgement.NotAttempted && DispositionFor(physical) is null)
            throw new ArgumentException("An unattempted physical disposition cannot have an acknowledgement.");
        Ordinal = ordinal;
        TargetKind = claim.Retirement.Target.Kind;
        ObjectClass = claim.Retirement.Target.Object.Class;
        Generation = claim.Generation;
        Physical = physical;
        Acknowledgement = acknowledgement;
        AcknowledgedDisposition = acknowledgedDisposition;
    }

    public int Ordinal { get; }
    public CleanupTargetKind TargetKind { get; }
    public PrivateObjectClass ObjectClass { get; }
    public long Generation { get; }
    public PrivateObjectMaintenancePhysicalObservation Physical { get; }
    public PrivateObjectMaintenanceAcknowledgement Acknowledgement { get; }
    public ObjectCleanupDisposition? AcknowledgedDisposition { get; }

    internal static ObjectCleanupDisposition? DispositionFor(PrivateObjectMaintenancePhysicalObservation physical) => physical switch
    {
        PrivateObjectMaintenancePhysicalObservation.Deleted => ObjectCleanupDisposition.Deleted,
        PrivateObjectMaintenancePhysicalObservation.Missing => ObjectCleanupDisposition.Missing,
        PrivateObjectMaintenancePhysicalObservation.Io => ObjectCleanupDisposition.StorageIo,
        PrivateObjectMaintenancePhysicalObservation.UnsafeStorage => ObjectCleanupDisposition.UnsafeStorage,
        _ => null
    };
}

/// <summary>One bounded invocation, never a claim that the project is clean.</summary>
public sealed class PrivateObjectMaintenanceRunResult
{
    internal PrivateObjectMaintenanceRunResult(ProjectScope project, ObjectCleanupScanCursor? cleanupAfter,
        ObjectCleanupStatusCursor? statusAfter, PrivateObjectMaintenanceRunOutcome outcome,
        PrivateObjectMaintenanceRunFailure? failure, PrivateObjectMaintenanceObservationSummary observations,
        bool claimAttempted, ObjectCleanupClaimBatch? batch, IEnumerable<PrivateObjectMaintenanceTargetResult> targets)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(observations);
        MaintenanceValue.Known(outcome, nameof(outcome));
        if (failure is { } known) MaintenanceValue.Known(known, nameof(failure));
        if ((outcome == PrivateObjectMaintenanceRunOutcome.Failed && failure is null) ||
            (outcome == PrivateObjectMaintenanceRunOutcome.Finished && failure is not null) ||
            (outcome == PrivateObjectMaintenanceRunOutcome.Finished && batch is null) ||
            (observations.ObservedProject is { } observedProject && observedProject != project) ||
            (claimAttempted && !observations.ReadyToClaim) ||
            (cleanupAfter is not null && cleanupAfter.Project != project) ||
            (statusAfter is not null && statusAfter.Project != project) ||
            (batch is not null && (!claimAttempted || batch.Project != project)))
            throw new ArgumentException("Maintenance result facts must describe the same invocation.");
        var owned = MaintenanceObservationValue.Snapshot(targets, ObjectCleanupPolicy.MaximumTargets, CancellationToken.None);
        if (owned.Count != (batch?.Claims.Count ?? 0))
            throw new ArgumentException("Maintenance results require one row per acknowledged claim.", nameof(targets));
        var exhausted = observations.CopyExhaustedIds();
        for (var index = 0; index < owned.Count; index++)
        {
            var row = owned[index];
            var claim = batch!.Claims[index];
            if (row.Ordinal != index || row.TargetKind != claim.Retirement.Target.Kind ||
                row.ObjectClass != claim.Retirement.Target.Object.Class || row.Generation != claim.Generation ||
                (outcome == PrivateObjectMaintenanceRunOutcome.Finished && row.Physical is
                    PrivateObjectMaintenancePhysicalObservation.NotStarted or PrivateObjectMaintenancePhysicalObservation.Canceled or
                    PrivateObjectMaintenancePhysicalObservation.Deadline or PrivateObjectMaintenancePhysicalObservation.Unresolved))
                throw new ArgumentException("A sanitized result must retain its original claim position and facts.", nameof(targets));
            if (row.Physical == PrivateObjectMaintenancePhysicalObservation.Deleted) PhysicalDeleted++;
            if (row.Physical == PrivateObjectMaintenancePhysicalObservation.Missing) PhysicalMissing++;
            if (row.AcknowledgedDisposition == ObjectCleanupDisposition.Deleted) AcknowledgedDeleted++;
            if (row.AcknowledgedDisposition == ObjectCleanupDisposition.Missing) AcknowledgedMissing++;
            if (row.AcknowledgedDisposition is ObjectCleanupDisposition.StorageIo or ObjectCleanupDisposition.UnsafeStorage)
            {
                if (row.Generation < ObjectCleanupPolicy.MaximumAttempts) AcknowledgedRetryable++;
                else
                {
                    AcknowledgedExhausted++;
                    exhausted?.Add(claim.Retirement.RetirementId);
                }
            }
        }
        Project = project;
        CleanupAfter = cleanupAfter;
        StatusAfter = statusAfter;
        Outcome = outcome;
        Failure = failure;
        Observations = observations;
        ClaimAttempted = claimAttempted;
        CandidatesExamined = batch?.ExaminedCount;
        ProtectedCandidatesObserved = batch?.ProtectedCount;
        NotDueCandidatesObserved = batch?.NotDueCount;
        ClaimedCount = batch?.Claims.Count;
        NextCleanupCursor = batch?.NextCursor;
        Targets = owned;
        ExhaustedRetirementsObserved = exhausted?.Count;
    }

    public ProjectScope Project { get; }
    public ObjectCleanupScanCursor? CleanupAfter { get; }
    public ObjectCleanupStatusCursor? StatusAfter { get; }
    public PrivateObjectMaintenanceRunOutcome Outcome { get; }
    public PrivateObjectMaintenanceRunFailure? Failure { get; }
    public PrivateObjectMaintenanceObservationSummary Observations { get; }
    public bool ClaimAttempted { get; }
    public int? CandidatesExamined { get; }
    public int? ProtectedCandidatesObserved { get; }
    public int? NotDueCandidatesObserved { get; }
    public int? ClaimedCount { get; }
    public ObjectCleanupScanCursor? NextCleanupCursor { get; }
    public IReadOnlyList<PrivateObjectMaintenanceTargetResult> Targets { get; }
    public int PhysicalDeleted { get; }
    public int PhysicalMissing { get; }
    public int AcknowledgedDeleted { get; }
    public int AcknowledgedMissing { get; }
    public int AcknowledgedRetryable { get; }
    public int AcknowledgedExhausted { get; }
    public int? ExhaustedRetirementsObserved { get; }
}
