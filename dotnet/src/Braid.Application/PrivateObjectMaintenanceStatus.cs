using Braid.Domain;

namespace Braid.Application;

public sealed record ObjectCleanupStatusCursor
{
    public ObjectCleanupStatusCursor(ProjectScope project, Guid afterRetirementId)
    {
        ArgumentNullException.ThrowIfNull(project);
        AfterRetirementId = MaintenanceValue.Id(afterRetirementId, nameof(afterRetirementId));
        Project = project;
    }

    public ProjectScope Project { get; }
    public Guid AfterRetirementId { get; }
}

/// <summary>Flat immutable attempt facts; no token, instance or original workflow claim.</summary>
public sealed record CleanupAttemptStatusHeader
{
    public CleanupAttemptStatusHeader(Guid attemptId, long generation, DateTimeOffset startedAtUtc,
        DateTimeOffset leaseExpiresAtUtc, DateTimeOffset? finishedAtUtc, ObjectCleanupDisposition? disposition)
    {
        AttemptId = MaintenanceValue.Id(attemptId, nameof(attemptId));
        MaintenanceValue.Generation(generation, nameof(generation));
        MaintenanceValue.Utc(startedAtUtc, nameof(startedAtUtc));
        MaintenanceValue.Utc(leaseExpiresAtUtc, nameof(leaseExpiresAtUtc));
        if (leaseExpiresAtUtc - startedAtUtc != TimeSpan.FromSeconds(ObjectCleanupPolicy.ClaimLeaseSeconds))
            throw new ArgumentException("An observed cleanup lease must retain its exact lifetime.", nameof(leaseExpiresAtUtc));
        if (leaseExpiresAtUtc > DateTimeOffset.MaxValue.AddSeconds(-ObjectCleanupPolicy.RetryDelaySeconds))
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc), "An observed cleanup lease must permit its retry time.");
        if (finishedAtUtc.HasValue != disposition.HasValue)
            throw new ArgumentException("Observed cleanup completion requires paired facts.");
        if (disposition is { } known) MaintenanceValue.Known(known, nameof(disposition));
        if (finishedAtUtc is { } finished)
        {
            MaintenanceValue.Utc(finished, nameof(finishedAtUtc));
            if (finished < startedAtUtc || finished >= leaseExpiresAtUtc)
                throw new ArgumentException("Observed cleanup completion must retain its original lease.", nameof(finishedAtUtc));
        }
        Generation = generation;
        StartedAtUtc = startedAtUtc;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        FinishedAtUtc = finishedAtUtc;
        Disposition = disposition;
    }

    public Guid AttemptId { get; }
    public long Generation { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset LeaseExpiresAtUtc { get; }
    public DateTimeOffset? FinishedAtUtc { get; }
    public ObjectCleanupDisposition? Disposition { get; }
}

public enum ObjectCleanupObservedState
{
    Unattempted = 1, Leased = 2, RetryWaiting = 3, RetryDue = 4, Completed = 5, Exhausted = 6
}

/// <summary>State is computed only at the supplied database observation time.</summary>
public sealed record ObjectCleanupStatusHeader
{
    public ObjectCleanupStatusHeader(Guid retirementId, ObjectCleanupTarget target, DateTimeOffset retiredAtUtc,
        long currentGeneration, CleanupAttemptStatusHeader? currentAttempt, Guid? completedAttemptId,
        DateTimeOffset observedAtUtc)
    {
        RetirementId = MaintenanceValue.Id(retirementId, nameof(retirementId));
        ArgumentNullException.ThrowIfNull(target);
        MaintenanceValue.Utc(retiredAtUtc, nameof(retiredAtUtc));
        MaintenanceValue.Utc(observedAtUtc, nameof(observedAtUtc));
        if (currentGeneration is < 0 or > ObjectCleanupPolicy.MaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(currentGeneration));
        if (observedAtUtc < retiredAtUtc || completedAttemptId == Guid.Empty ||
            (currentGeneration == 0) != (currentAttempt is null))
            throw new ArgumentException("Observed retirement and attempt facts must agree.");
        if (currentAttempt is { } attempt && (attempt.Generation != currentGeneration ||
            attempt.StartedAtUtc < retiredAtUtc || attempt.StartedAtUtc > observedAtUtc ||
            attempt.FinishedAtUtc > observedAtUtc))
            throw new ArgumentException("The current observed attempt must retain its generation and times.", nameof(currentAttempt));
        var success = currentAttempt?.Disposition is ObjectCleanupDisposition.Deleted or ObjectCleanupDisposition.Missing;
        if (success != completedAttemptId.HasValue ||
            (completedAttemptId.HasValue && completedAttemptId != currentAttempt?.AttemptId))
            throw new ArgumentException("Observed success and its completed pointer must agree.", nameof(completedAttemptId));

        Target = target;
        RetiredAtUtc = retiredAtUtc;
        CurrentGeneration = currentGeneration;
        CurrentAttempt = currentAttempt;
        CompletedAttemptId = completedAttemptId;
        ObservedAtUtc = observedAtUtc;
        NextAttemptAtUtc = currentAttempt is not null && !success && currentGeneration < ObjectCleanupPolicy.MaximumAttempts
            ? (currentAttempt.FinishedAtUtc ?? currentAttempt.LeaseExpiresAtUtc).AddSeconds(ObjectCleanupPolicy.RetryDelaySeconds)
            : null;
        State = currentAttempt is null ? ObjectCleanupObservedState.Unattempted :
            success ? ObjectCleanupObservedState.Completed :
            currentAttempt.FinishedAtUtc is null && observedAtUtc < currentAttempt.LeaseExpiresAtUtc ? ObjectCleanupObservedState.Leased :
            currentGeneration == ObjectCleanupPolicy.MaximumAttempts ? ObjectCleanupObservedState.Exhausted :
            observedAtUtc < NextAttemptAtUtc!.Value ? ObjectCleanupObservedState.RetryWaiting : ObjectCleanupObservedState.RetryDue;
    }

    public Guid RetirementId { get; }
    public ObjectCleanupTarget Target { get; }
    public DateTimeOffset RetiredAtUtc { get; }
    public long CurrentGeneration { get; }
    public CleanupAttemptStatusHeader? CurrentAttempt { get; }
    public Guid? CompletedAttemptId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public ObjectCleanupObservedState State { get; }
    public DateTimeOffset? NextAttemptAtUtc { get; }
}

/// <summary>EOF applies to this selected statement page, not a project-wide snapshot.</summary>
public sealed class ObjectCleanupStatusPage
{
    public ObjectCleanupStatusPage(ProjectScope project, DateTimeOffset observedAtUtc,
        IEnumerable<ObjectCleanupStatusHeader> headers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(project);
        MaintenanceValue.Utc(observedAtUtc, nameof(observedAtUtc));
        var owned = MaintenanceObservationValue.Snapshot(headers, MaintenanceObservationValue.MaximumEntries, cancellationToken);
        for (var index = 0; index < owned.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = owned[index];
            if (!MaintenanceObservationValue.InProject(header.Target.Scope, project) || header.ObservedAtUtc != observedAtUtc ||
                (index > 0 && MaintenanceObservationValue.CompareIds(owned[index - 1].RetirementId, header.RetirementId) >= 0))
                throw new ArgumentException("Observed status headers must retain one project, database time and UUID order.", nameof(headers));
        }
        Project = project;
        ObservedAtUtc = observedAtUtc;
        Headers = owned;
        IsComplete = owned.Count < MaintenanceObservationValue.MaximumEntries;
        Next = IsComplete ? null : new(project, owned[^1].RetirementId);
    }

    public ProjectScope Project { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public IReadOnlyList<ObjectCleanupStatusHeader> Headers { get; }
    public int ExaminedCount => Headers.Count;
    public bool IsComplete { get; }
    public ObjectCleanupStatusCursor? Next { get; }
}
