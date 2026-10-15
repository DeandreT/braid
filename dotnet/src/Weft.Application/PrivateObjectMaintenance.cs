using Weft.Domain;

namespace Weft.Application;

/// <summary>
/// Permanent metadata retirement of one exact target. This fact alone is not a
/// physical storage fence, a deletion observation or authority for another target.
/// </summary>
public sealed record ObjectCleanupRetirement
{
    public ObjectCleanupRetirement(Guid retirementId, ObjectCleanupTarget target, ObjectCleanupOrigin origin,
        ObjectCleanupReason reason, DateTimeOffset eligibilityOriginAtUtc, DateTimeOffset retiredAtUtc,
        PrivatePublicationRecord? publication = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(origin);
        RetirementId = MaintenanceValue.Id(retirementId, nameof(retirementId));
        MaintenanceValue.Known(reason, nameof(reason));
        MaintenanceValue.Utc(eligibilityOriginAtUtc, nameof(eligibilityOriginAtUtc));
        MaintenanceValue.Utc(retiredAtUtc, nameof(retiredAtUtc));
        if (target.Scope != origin.Scope ||
            (target.Object.Class == PrivateObjectClass.NativeArtifact) != (origin.Kind == PrivateObjectOwnerKind.NativeIngestion))
            throw new ArgumentException("Retirement must retain its complete object scope and immutable origin.", nameof(origin));
        if (retiredAtUtc - eligibilityOriginAtUtc < TimeSpan.FromHours(ObjectCleanupPolicy.GraceHours))
            throw new ArgumentException("Retirement requires the complete database-time grace.", nameof(retiredAtUtc));

        if (target.Kind == CleanupTargetKind.TrackedTemporary)
        {
            if (reason != ObjectCleanupReason.AbandonedTrackedTemporary || publication is null ||
                publication.Reservation.PublicationId != target.PublicationAttemptId ||
                publication.Reservation.Key.Object != target.Object || publication.Reservation.Origin != origin)
                throw new ArgumentException("Temporary retirement requires its exact positive publication registration.", nameof(publication));
            var eligibleOrigin = publication.Reservation.Owner.LeaseExpiresAtUtc;
            if (publication.Reservation.RegisteredAtUtc > eligibleOrigin)
                eligibleOrigin = publication.Reservation.RegisteredAtUtc;
            if (publication.Binding is { } binding && binding.BoundAtUtc > eligibleOrigin)
                eligibleOrigin = binding.BoundAtUtc;
            if (eligibilityOriginAtUtc != eligibleOrigin)
                throw new ArgumentException("Temporary retirement must preserve the captured owner's eligibility time.", nameof(eligibilityOriginAtUtc));
        }
        else if (publication is not null || reason == ObjectCleanupReason.AbandonedTrackedTemporary ||
            (origin.Kind == PrivateObjectOwnerKind.NativeIngestion && reason != ObjectCleanupReason.TerminalNativeStage) ||
            (origin.Kind == PrivateObjectOwnerKind.ReportAttempt && reason is not
                (ObjectCleanupReason.FailedDerivedCandidate or ObjectCleanupReason.ExpiredDerivedCandidate)))
            throw new ArgumentException("Final retirement requires the matching native or derived orphan reason.", nameof(reason));

        Target = target;
        Origin = origin;
        Reason = reason;
        EligibilityOriginAtUtc = eligibilityOriginAtUtc;
        RetiredAtUtc = retiredAtUtc;
        Publication = publication;
    }

    public Guid RetirementId { get; }
    public ObjectCleanupTarget Target { get; }
    public ObjectCleanupOrigin Origin { get; }
    public ObjectCleanupReason Reason { get; }
    public DateTimeOffset EligibilityOriginAtUtc { get; }
    public DateTimeOffset RetiredAtUtc { get; }
    public PrivatePublicationRecord? Publication { get; }
}

/// <summary>The exact database-issued unrenewed cleanup claim; it contains no physical paths or made-up content facts.</summary>
public sealed record ObjectCleanupClaim
{
    public ObjectCleanupClaim(ObjectCleanupRetirement retirement, Guid attemptId, long generation,
        Guid serverInstanceId, Guid leaseToken, DateTimeOffset startedAtUtc, DateTimeOffset leaseExpiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(retirement);
        AttemptId = MaintenanceValue.Id(attemptId, nameof(attemptId));
        ServerInstanceId = MaintenanceValue.Id(serverInstanceId, nameof(serverInstanceId));
        LeaseToken = MaintenanceValue.Id(leaseToken, nameof(leaseToken));
        MaintenanceValue.Generation(generation, nameof(generation));
        MaintenanceValue.Utc(startedAtUtc, nameof(startedAtUtc));
        MaintenanceValue.Utc(leaseExpiresAtUtc, nameof(leaseExpiresAtUtc));
        if (startedAtUtc < retirement.RetiredAtUtc ||
            leaseExpiresAtUtc - startedAtUtc != TimeSpan.FromSeconds(ObjectCleanupPolicy.ClaimLeaseSeconds))
            throw new ArgumentException("A cleanup claim must retain its exact lifetime after retirement.", nameof(leaseExpiresAtUtc));
        if (leaseExpiresAtUtc > DateTimeOffset.MaxValue.Subtract(TimeSpan.FromSeconds(ObjectCleanupPolicy.RetryDelaySeconds)))
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc), "A cleanup lifetime must permit its bounded retry time.");

        Retirement = retirement;
        Generation = generation;
        StartedAtUtc = startedAtUtc;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
    }

    public ObjectCleanupRetirement Retirement { get; }
    public Guid AttemptId { get; }
    public long Generation { get; }
    public Guid ServerInstanceId { get; }
    public Guid LeaseToken { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset LeaseExpiresAtUtc { get; }
}

public enum ObjectCleanupDisposition { Deleted = 1, Missing = 2, Canceled = 3, Deadline = 4, StorageIo = 5, UnsafeStorage = 6 }
public enum ObjectCleanupAttemptState { Leased = 1, RetryWaiting = 2, RetryDue = 3, Completed = 4, Exhausted = 5 }

/// <summary>Retained exact evidence. An expired unfinished attempt is not retrospectively labeled deleted.</summary>
public sealed record ObjectCleanupAttempt
{
    public ObjectCleanupAttempt(ObjectCleanupClaim claim, DateTimeOffset? finishedAtUtc = null,
        ObjectCleanupDisposition? disposition = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (finishedAtUtc.HasValue != disposition.HasValue)
            throw new ArgumentException("Cleanup completion requires both time and disposition.");
        if (disposition is { } known)
            MaintenanceValue.Known(known, nameof(disposition));
        if (finishedAtUtc is { } finished)
        {
            MaintenanceValue.Utc(finished, nameof(finishedAtUtc));
            if (finished < claim.StartedAtUtc || finished >= claim.LeaseExpiresAtUtc)
                throw new ArgumentException("Cleanup completion must belong to its original live lease.", nameof(finishedAtUtc));
        }
        Claim = claim;
        FinishedAtUtc = finishedAtUtc;
        Disposition = disposition;
        NextAttemptAtUtc = IsSuccess || claim.Generation == ObjectCleanupPolicy.MaximumAttempts ? null :
            (finishedAtUtc ?? claim.LeaseExpiresAtUtc).AddSeconds(ObjectCleanupPolicy.RetryDelaySeconds);
    }

    public ObjectCleanupClaim Claim { get; }
    public DateTimeOffset? FinishedAtUtc { get; }
    public ObjectCleanupDisposition? Disposition { get; }
    public bool IsSuccess => Disposition is ObjectCleanupDisposition.Deleted or ObjectCleanupDisposition.Missing;
    public DateTimeOffset? NextAttemptAtUtc { get; }

    public ObjectCleanupAttemptState StateAt(DateTimeOffset observedAtUtc)
    {
        MaintenanceValue.Utc(observedAtUtc, nameof(observedAtUtc));
        if (observedAtUtc < Claim.StartedAtUtc || (FinishedAtUtc is { } finished && observedAtUtc < finished))
            throw new ArgumentException("Cleanup state requires a current database observation.", nameof(observedAtUtc));
        if (IsSuccess) return ObjectCleanupAttemptState.Completed;
        if (FinishedAtUtc is null && observedAtUtc < Claim.LeaseExpiresAtUtc) return ObjectCleanupAttemptState.Leased;
        if (Claim.Generation == ObjectCleanupPolicy.MaximumAttempts) return ObjectCleanupAttemptState.Exhausted;
        return observedAtUtc < NextAttemptAtUtc!.Value ? ObjectCleanupAttemptState.RetryWaiting : ObjectCleanupAttemptState.RetryDue;
    }
}

public sealed record ObjectCleanupClaimBatch
{
    public ObjectCleanupClaimBatch(ProjectScope project, IEnumerable<ObjectCleanupClaim> claims,
        int examinedCount, int protectedCount, int notDueCount, ObjectCleanupScanCursor? nextCursor = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(claims);
        MaintenanceValue.Count(examinedCount, ObjectCleanupPolicy.MaximumCandidates, nameof(examinedCount));
        MaintenanceValue.Count(protectedCount, examinedCount, nameof(protectedCount));
        MaintenanceValue.Count(notDueCount, examinedCount, nameof(notDueCount));
        if (nextCursor is not null && nextCursor.Project != project)
            throw new ArgumentException("The next cleanup cursor must retain its project.", nameof(nextCursor));

        var owned = new List<ObjectCleanupClaim>();
        var targets = new HashSet<ObjectCleanupTarget>();
        var retirements = new HashSet<Guid>();
        var attempts = new HashSet<Guid>();
        foreach (var claim in claims)
        {
            if (owned.Count == ObjectCleanupPolicy.MaximumTargets)
                throw new ArgumentException("A cleanup batch exceeds its fixed target budget.", nameof(claims));
            if (claim is null || claim.Retirement.Target.Scope.OrganizationId != project.OrganizationId ||
                claim.Retirement.Target.Scope.ProjectId != project.ProjectId ||
                !targets.Add(claim.Retirement.Target) || !retirements.Add(claim.Retirement.RetirementId) ||
                !attempts.Add(claim.AttemptId) || (owned.Count > 0 && owned[0].ServerInstanceId != claim.ServerInstanceId))
                throw new ArgumentException("A cleanup batch requires unique exact targets in one project and instance.", nameof(claims));
            owned.Add(claim);
        }
        if (owned.Count + protectedCount + notDueCount > examinedCount)
            throw new ArgumentException("Cleanup counts must fit the actual bounded candidate observation.", nameof(examinedCount));
        Project = project;
        Claims = owned.AsReadOnly();
        ExaminedCount = examinedCount;
        ProtectedCount = protectedCount;
        NotDueCount = notDueCount;
        NextCursor = nextCursor;
    }

    public ProjectScope Project { get; }
    public IReadOnlyList<ObjectCleanupClaim> Claims { get; }
    public int ExaminedCount { get; }
    public int ProtectedCount { get; }
    public int NotDueCount { get; }
    public ObjectCleanupScanCursor? NextCursor { get; }
}

public enum ObjectCleanupFinishOutcome { Applied = 1, AlreadyApplied = 2, ClaimLost = 3, SafetyRefusal = 4 }

public sealed record ObjectCleanupFinishResult
{
    private ObjectCleanupFinishResult(ObjectCleanupFinishOutcome outcome, ObjectCleanupAttempt? attempt)
    {
        Outcome = outcome;
        Attempt = attempt;
    }

    public ObjectCleanupFinishOutcome Outcome { get; }
    public ObjectCleanupAttempt? Attempt { get; }

    public static ObjectCleanupFinishResult ForAttempt(ObjectCleanupFinishOutcome outcome, ObjectCleanupAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (outcome is not (ObjectCleanupFinishOutcome.Applied or ObjectCleanupFinishOutcome.AlreadyApplied))
            throw new ArgumentOutOfRangeException(nameof(outcome), "Only acknowledged cleanup outcomes carry an attempt.");
        if (attempt.Disposition is null)
            throw new ArgumentException("An acknowledged cleanup outcome requires its exact completion.", nameof(attempt));
        return new(outcome, attempt);
    }

    public static ObjectCleanupFinishResult WithoutAttempt(ObjectCleanupFinishOutcome outcome)
    {
        MaintenanceValue.Known(outcome, nameof(outcome));
        if (outcome is ObjectCleanupFinishOutcome.Applied or ObjectCleanupFinishOutcome.AlreadyApplied)
            throw new ArgumentException("An acknowledged cleanup outcome requires an attempt.", nameof(outcome));
        return new(outcome, null);
    }
}

/// <summary>
/// Dedicated scoped maintenance authority. Claims and acknowledgements commit before
/// returning; no transaction spans filesystem I/O. Native history, resumable ingestion
/// and every usable derived result remain protected. Database faults are exceptions.
/// </summary>
public interface IPrivateObjectMaintenanceMetadata
{
    Task<ObjectCleanupClaimBatch> ClaimAsync(ProjectScope project, Guid serverInstanceId,
        ObjectCleanupScanCursor? after = null, CancellationToken cancellationToken = default);

    Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>Bounds acknowledgement recovery with the caller's shared absolute settlement token.</summary>
    Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
        CancellationToken cancellationToken, CancellationToken settlementCancellationToken);

    /// <summary>Recovers only the retained exact generation after the original call settles, never a newer current attempt.</summary>
    Task<ObjectCleanupAttempt?> GetAttemptAsync(ProjectScope project, Guid retirementId, long generation,
        CancellationToken cancellationToken = default);
}

internal static class MaintenanceValue
{
    internal static Guid Id(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A private maintenance identifier cannot be empty.", parameterName);
        return value;
    }

    internal static void Known<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "A private maintenance category must be known.");
    }

    internal static void Utc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A private maintenance timestamp must be UTC.", parameterName);
    }

    internal static void Generation(long value, string parameterName)
    {
        if (value is < 1 or > ObjectCleanupPolicy.MaximumAttempts)
            throw new ArgumentOutOfRangeException(parameterName, value, "Cleanup generation must fit its fixed attempt budget.");
    }

    internal static void Count(int value, int maximum, string parameterName)
    {
        if (value < 0 || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, value, "A cleanup count exceeds its fixed bound.");
    }
}
