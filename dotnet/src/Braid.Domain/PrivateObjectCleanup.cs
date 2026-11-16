namespace Braid.Domain;

/// <summary>The fixed first maintenance policy; it never expires successful history.</summary>
public static class ObjectCleanupPolicy
{
    public const string Identity = "braid.orphan-maintenance.v1";
    public const int GraceHours = 24;
    public const int MaximumTargets = 16;
    public const int MaximumCandidates = 100;
    public const int RunBudgetSeconds = 30;
    public const int ClaimLeaseSeconds = 60;
    public const int RetryDelaySeconds = 30;
    public const int MaximumAttempts = 8;
}

public enum CleanupTargetKind { FinalObject = 1, TrackedTemporary = 2 }
public enum PrivateObjectOwnerKind { NativeIngestion = 1, ReportAttempt = 2 }
public enum ObjectCleanupReason
{
    TerminalNativeStage = 1,
    FailedDerivedCandidate = 2,
    ExpiredDerivedCandidate = 3,
    AbandonedTrackedTemporary = 4
}

public enum ObjectCleanupScanStream
{
    DueRetirement = 1,
    NativeStage = 2,
    DerivedCandidate = 3,
    TrackedTemporary = 4
}

/// <summary>
/// Exact logical identity, never a path or evidence of deletion permission.
/// Retiring one tracked publication does not retire its final object.
/// </summary>
public sealed record ObjectCleanupTarget
{
    public ObjectCleanupTarget(PrivateObjectId @object, CleanupTargetKind kind,
        Guid? publicationAttemptId = null)
    {
        ArgumentNullException.ThrowIfNull(@object);
        CleanupDomainValue.Known(kind, nameof(kind));
        if (publicationAttemptId == Guid.Empty ||
            (kind == CleanupTargetKind.TrackedTemporary) != publicationAttemptId.HasValue)
            throw new ArgumentException("Only a tracked temporary target requires a nonempty publication identity.", nameof(publicationAttemptId));

        Object = @object;
        Kind = kind;
        PublicationAttemptId = publicationAttemptId;
    }

    public PrivateObjectId Object { get; }
    public CleanupTargetKind Kind { get; }
    public Guid? PublicationAttemptId { get; }
    public RevisionScope Scope => Object.Scope;
}

/// <summary>The immutable persisted source of an exact object, not the current actor's access.</summary>
public sealed record ObjectCleanupOrigin
{
    private ObjectCleanupOrigin(RevisionScope scope, PrivateObjectOwnerKind kind, Guid? ingestionOperationId,
        Guid? cacheEntryId, Guid? derivationId, long? generation)
    {
        Scope = scope;
        Kind = kind;
        IngestionOperationId = ingestionOperationId;
        CacheEntryId = cacheEntryId;
        DerivationId = derivationId;
        Generation = generation;
    }

    public RevisionScope Scope { get; }
    public PrivateObjectOwnerKind Kind { get; }
    public Guid? IngestionOperationId { get; }
    public Guid? CacheEntryId { get; }
    public Guid? DerivationId { get; }
    public long? Generation { get; }

    public static ObjectCleanupOrigin ForIngestion(RevisionScope scope, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CleanupDomainValue.Id(operationId, nameof(operationId));
        return new(scope, PrivateObjectOwnerKind.NativeIngestion, operationId, null, null, null);
    }

    public static ObjectCleanupOrigin ForReport(RevisionScope scope, Guid cacheEntryId, Guid derivationId,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CleanupDomainValue.Id(cacheEntryId, nameof(cacheEntryId));
        CleanupDomainValue.Id(derivationId, nameof(derivationId));
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        return new(scope, PrivateObjectOwnerKind.ReportAttempt, null, cacheEntryId, derivationId, generation);
    }
}

/// <summary>A scoped seek position, ordered by stream, eligibility time and complete target tuple.</summary>
public sealed record ObjectCleanupScanCursor
{
    public ObjectCleanupScanCursor(ProjectScope project, ObjectCleanupScanStream stream,
        DateTimeOffset eligibleAtUtc, ObjectCleanupTarget target)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(target);
        CleanupDomainValue.Known(stream, nameof(stream));
        CleanupDomainValue.Utc(eligibleAtUtc, nameof(eligibleAtUtc));
        if (project.OrganizationId != target.Scope.OrganizationId || project.ProjectId != target.Scope.ProjectId)
            throw new ArgumentException("A cleanup cursor must belong to its exact project.", nameof(target));
        if ((stream == ObjectCleanupScanStream.NativeStage &&
                (target.Kind != CleanupTargetKind.FinalObject || target.Object.Class != PrivateObjectClass.NativeArtifact)) ||
            (stream == ObjectCleanupScanStream.DerivedCandidate &&
                (target.Kind != CleanupTargetKind.FinalObject || target.Object.Class != PrivateObjectClass.DerivedArtifact)) ||
            (stream == ObjectCleanupScanStream.TrackedTemporary && target.Kind != CleanupTargetKind.TrackedTemporary))
            throw new ArgumentException("The cleanup cursor target must match its scan stream.", nameof(target));

        Project = project;
        Stream = stream;
        EligibleAtUtc = eligibleAtUtc;
        Target = target;
    }

    public ProjectScope Project { get; }
    public ObjectCleanupScanStream Stream { get; }
    public DateTimeOffset EligibleAtUtc { get; }
    public ObjectCleanupTarget Target { get; }
}

internal static class CleanupDomainValue
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
}
