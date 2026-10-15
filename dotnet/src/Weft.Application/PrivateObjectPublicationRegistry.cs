using Weft.Domain;

namespace Weft.Application;

/// <summary>
/// The actor and actual already-owned claim supplied by a trusted workflow.
/// The registry rechecks persisted ownership and fresh access; this is not a grant.
/// </summary>
public sealed record PrivatePublicationOwner
{
    private PrivatePublicationOwner(ActorContext actor, IngestionClaim? ingestionClaim,
        RevisionReportClaim? reportClaim)
    {
        Actor = actor;
        IngestionClaim = ingestionClaim;
        ReportClaim = reportClaim;
        Kind = ingestionClaim is not null ? PrivateObjectOwnerKind.NativeIngestion : PrivateObjectOwnerKind.ReportAttempt;
        Project = ingestionClaim?.Project ?? new ProjectScope(reportClaim!.Key.Scope.OrganizationId, reportClaim.Key.Scope.ProjectId);
    }

    public PrivateObjectOwnerKind Kind { get; }
    public ActorContext Actor { get; }
    public ProjectScope Project { get; }
    public IngestionClaim? IngestionClaim { get; }
    public RevisionReportClaim? ReportClaim { get; }
    public PrincipalId OriginalActor => Actor.PrincipalId;
    public Guid ServerInstanceId => IngestionClaim?.ServerInstanceId ?? ReportClaim!.ServerInstanceId;
    public Guid LeaseToken => IngestionClaim?.LeaseToken ?? ReportClaim!.LeaseToken;
    public long Generation => IngestionClaim?.Generation ?? ReportClaim!.Generation;
    public DateTimeOffset LeaseExpiresAtUtc => IngestionClaim?.LeaseExpiresAtUtc ?? ReportClaim!.LeaseExpiresAtUtc;

    internal DateTimeOffset LeaseStartedAtUtc => ReportClaim?.StartedAtUtc ??
        LeaseExpiresAtUtc.Subtract(TimeSpan.FromSeconds(IngestionWorkflowLimits.ClaimLeaseSeconds));

    public static PrivatePublicationOwner ForIngestion(ActorContext actor, IngestionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(claim);
        if (actor.PrincipalId != claim.OriginalActor)
            throw new ArgumentException("Publication must retain the actual claim's original actor.", nameof(actor));
        MaintenanceValue.Utc(claim.LeaseExpiresAtUtc, nameof(claim));
        if (claim.LeaseExpiresAtUtc < DateTimeOffset.MinValue.AddSeconds(IngestionWorkflowLimits.ClaimLeaseSeconds))
            throw new ArgumentException("A publication owner must have a complete claim lifetime.", nameof(claim));
        return new(actor, claim, null);
    }

    public static PrivatePublicationOwner ForReport(ActorContext actor, RevisionReportClaim claim)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(claim);
        if (actor.PrincipalId != claim.OriginalActor)
            throw new ArgumentException("Publication must retain the actual claim's original actor.", nameof(actor));
        return new(actor, null, claim);
    }

    public ObjectCleanupOrigin OriginFor(PrivateObjectId objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        if (objectId.Scope.OrganizationId != Project.OrganizationId || objectId.Scope.ProjectId != Project.ProjectId)
            throw new ArgumentException("Publication must retain its actual owner's project.", nameof(objectId));
        if (IngestionClaim is { } ingestion)
        {
            if (objectId.Class != PrivateObjectClass.NativeArtifact)
                throw new ArgumentException("An ingestion owner publishes only a native object.", nameof(objectId));
            // The claim intentionally does not contain future revision/object IDs.
            // The registry must bind them to the actual immutable reservation.
            return ObjectCleanupOrigin.ForIngestion(objectId.Scope, ingestion.OperationId);
        }
        if (objectId != ReportClaim!.CandidateObjectId)
            throw new ArgumentException("A report owner publishes only its exact candidate object.", nameof(objectId));
        return ObjectCleanupOrigin.ForReport(objectId.Scope, ReportClaim.CacheEntryId, ReportClaim.DerivationId,
            ReportClaim.Generation);
    }
}

/// <summary>Database-issued immutable registration, recoverable by its caller-known natural key.</summary>
public sealed record PrivatePublicationReservation
{
    public PrivatePublicationReservation(Guid publicationId, PrivatePublicationKey key, PrivatePublicationOwner owner,
        Sha256Digest absenceRecordDigest, DateTimeOffset registeredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(absenceRecordDigest);
        PublicationId = MaintenanceValue.Id(publicationId, nameof(publicationId));
        MaintenanceValue.Utc(registeredAtUtc, nameof(registeredAtUtc));
        if (registeredAtUtc < owner.LeaseStartedAtUtc || registeredAtUtc >= owner.LeaseExpiresAtUtc)
            throw new ArgumentException("Publication registration must occur within its actual owner's lease.", nameof(registeredAtUtc));
        Origin = owner.OriginFor(key.Object);
        Key = key;
        Owner = owner;
        AbsenceRecordDigest = absenceRecordDigest;
        RegisteredAtUtc = registeredAtUtc;
    }

    public Guid PublicationId { get; }
    public PrivatePublicationKey Key { get; }
    public PrivatePublicationOwner Owner { get; }
    public Sha256Digest AbsenceRecordDigest { get; }
    public DateTimeOffset RegisteredAtUtc { get; }
    public ObjectCleanupOrigin Origin { get; }
}

/// <summary>Actual identity is bound once before any source read or payload write.</summary>
public sealed record PrivatePublicationBinding
{
    public PrivatePublicationBinding(PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest,
        DateTimeOffset boundAtUtc)
    {
        ArgumentNullException.ThrowIfNull(actualIdentity);
        ArgumentNullException.ThrowIfNull(boundRecordDigest);
        MaintenanceValue.Utc(boundAtUtc, nameof(boundAtUtc));
        ActualIdentity = actualIdentity;
        BoundRecordDigest = boundRecordDigest;
        BoundAtUtc = boundAtUtc;
    }

    public PrivatePublicationFileIdentity ActualIdentity { get; }
    public Sha256Digest BoundRecordDigest { get; }
    public DateTimeOffset BoundAtUtc { get; }
}

/// <summary>Retained facts only. A bound record is neither a content reference nor current permission.</summary>
public sealed record PrivatePublicationRecord
{
    public PrivatePublicationRecord(PrivatePublicationReservation reservation, PrivatePublicationBinding? binding = null)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (binding is not null && (binding.ActualIdentity.RootId != reservation.Key.RootId ||
                binding.BoundAtUtc < reservation.RegisteredAtUtc || binding.BoundAtUtc >= reservation.Owner.LeaseExpiresAtUtc))
            throw new ArgumentException("A publication binding must retain its exact root and original lease.", nameof(binding));
        Reservation = reservation;
        Binding = binding;
    }

    public PrivatePublicationReservation Reservation { get; }
    public PrivatePublicationBinding? Binding { get; }
}

/// <summary>One fresh database observation; later writes recheck the exact original owner.</summary>
public sealed record PrivatePublicationSnapshot
{
    public PrivatePublicationSnapshot(PrivatePublicationRecord record, DateTimeOffset observedAtUtc,
        bool ownerIsLive, bool finalObjectRetired, bool temporaryRetired)
    {
        ArgumentNullException.ThrowIfNull(record);
        MaintenanceValue.Utc(observedAtUtc, nameof(observedAtUtc));
        if (observedAtUtc < record.Reservation.RegisteredAtUtc ||
            (record.Binding is { } binding && observedAtUtc < binding.BoundAtUtc) ||
            (ownerIsLive && observedAtUtc >= record.Reservation.Owner.LeaseExpiresAtUtc))
            throw new ArgumentException("Publication availability must agree with its observation time.", nameof(observedAtUtc));
        Record = record;
        ObservedAtUtc = observedAtUtc;
        OwnerIsLive = ownerIsLive;
        FinalObjectRetired = finalObjectRetired;
        TemporaryRetired = temporaryRetired;
    }

    public PrivatePublicationRecord Record { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public bool OwnerIsLive { get; }
    public bool FinalObjectRetired { get; }
    public bool TemporaryRetired { get; }
    public bool CanWrite => OwnerIsLive && !FinalObjectRetired && !TemporaryRetired;
}

public enum PrivatePublicationReserveOutcome { Reserved = 1, Existing = 2, OwnerUnavailable = 3, Conflict = 4, Retired = 5 }
public enum PrivatePublicationBindOutcome { Bound = 1, Existing = 2, OwnerUnavailable = 3, Conflict = 4, Retired = 5 }

public sealed record PrivatePublicationReserveResult
{
    private PrivatePublicationReserveResult(PrivatePublicationReserveOutcome outcome, PrivatePublicationSnapshot? snapshot)
    {
        Outcome = outcome;
        Snapshot = snapshot;
    }

    public PrivatePublicationReserveOutcome Outcome { get; }
    public PrivatePublicationSnapshot? Snapshot { get; }

    public static PrivatePublicationReserveResult ForSnapshot(PrivatePublicationReserveOutcome outcome,
        PrivatePublicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (outcome is not (PrivatePublicationReserveOutcome.Reserved or PrivatePublicationReserveOutcome.Existing))
            throw new ArgumentOutOfRangeException(nameof(outcome), "Only successful reservation outcomes carry a snapshot.");
        if (!snapshot.CanWrite || (outcome == PrivatePublicationReserveOutcome.Reserved && snapshot.Record.Binding is not null))
            throw new ArgumentException("A successful reservation requires fresh writable matching facts.", nameof(snapshot));
        return new(outcome, snapshot);
    }

    public static PrivatePublicationReserveResult WithoutSnapshot(PrivatePublicationReserveOutcome outcome)
    {
        MaintenanceValue.Known(outcome, nameof(outcome));
        if (outcome is PrivatePublicationReserveOutcome.Reserved or PrivatePublicationReserveOutcome.Existing)
            throw new ArgumentException("A successful reservation requires a snapshot.", nameof(outcome));
        return new(outcome, null);
    }
}

public sealed record PrivatePublicationBindResult
{
    private PrivatePublicationBindResult(PrivatePublicationBindOutcome outcome, PrivatePublicationSnapshot? snapshot)
    {
        Outcome = outcome;
        Snapshot = snapshot;
    }

    public PrivatePublicationBindOutcome Outcome { get; }
    public PrivatePublicationSnapshot? Snapshot { get; }

    public static PrivatePublicationBindResult ForSnapshot(PrivatePublicationBindOutcome outcome,
        PrivatePublicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (outcome is not (PrivatePublicationBindOutcome.Bound or PrivatePublicationBindOutcome.Existing))
            throw new ArgumentOutOfRangeException(nameof(outcome), "Only successful binding outcomes carry a snapshot.");
        if (!snapshot.CanWrite || snapshot.Record.Binding is null)
            throw new ArgumentException("A successful binding requires fresh writable actual identity facts.", nameof(snapshot));
        return new(outcome, snapshot);
    }

    public static PrivatePublicationBindResult WithoutSnapshot(PrivatePublicationBindOutcome outcome)
    {
        MaintenanceValue.Known(outcome, nameof(outcome));
        if (outcome is PrivatePublicationBindOutcome.Bound or PrivatePublicationBindOutcome.Existing)
            throw new ArgumentException("A successful binding requires a snapshot.", nameof(outcome));
        return new(outcome, null);
    }
}

/// <summary>
/// Trusted metadata registration only: Reserve commits before CreateNew; Bind commits
/// before payload I/O. Implementations recheck actual current ownership and fresh access.
/// Faults remain exceptions and cancellation remains OperationCanceledException.
/// </summary>
public interface IPrivateObjectPublicationRegistry
{
    Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, Guid rootId,
        PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest,
        CancellationToken cancellationToken = default);

    Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
        PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovers only this natural key and original owner after the original call settles.
    /// A retained record never substitutes another/current claim or grants permission by age.
    /// </summary>
    Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, Guid rootId,
        PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default);
}
