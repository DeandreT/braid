using Braid.Domain;

namespace Braid.Application.Lifecycle;

/// <summary>Closed outcomes of a permanent deletion request; a database failure is never reported as not found.</summary>
public enum ResourcePurgeOutcome { Accepted = 1, Replayed, NotAuthorizedOrNotFound, PreconditionFailed, ActiveRelease, ActiveWork, Conflict, InvalidRequest }

public enum DeletionJobState { Quiescing = 1, Inventory, CatalogPurging, ObjectsPending, Completed, Blocked }
public enum DeletionJobBlocker { ActiveRelease = 1, Integrity, Storage, AttemptsExhausted }

/// <summary>Categories and counts only: no names, payloads, locators or private creator identities.</summary>
public sealed record DeletionJobStatus(Guid JobId, ResourceKey Resource, DeletionJobState State, DeletionJobBlocker? Blocker, bool CatalogRemoved,
    long ObjectCount, long ObjectsPending, long ObjectsBlocked, long RetainedArtifacts, long RetainedAuditRecords,
    DateTimeOffset RequestedAtUtc, DateTimeOffset ChangedAtUtc, DateTimeOffset? CatalogRemovedAtUtc, DateTimeOffset? CompletedAtUtc);

public sealed record ResourcePurgeImpact(long Mappings, long Revisions, long Conversations, long Previews, long RegressionCases,
    long RegressionRuns, long Builds, long ReleaseTargets, long RetainedArtifacts);

/// <summary>A read-only preview for the exact-project administrator; nothing is persisted or reserved.</summary>
public sealed record ResourcePurgePreview(ResourceKey Resource, bool EffectivelyArchived, ResourceLifecycleVersion Version, int PlanVersion,
    DeletionJobStatus? Deletion, long ActiveReleases, long PendingMappingDeletions, ResourcePurgeImpact Impact);

public sealed record ResourcePurgeRequest
{
    public ResourcePurgeRequest(ResourceKey resource, Guid operationId, Guid confirmationId, ResourceLifecycleVersion expected)
    {
        ArgumentNullException.ThrowIfNull(resource); ArgumentNullException.ThrowIfNull(expected);
        if (operationId == Guid.Empty || confirmationId == Guid.Empty || resource.IsMapping != (expected.Mapping is not null))
            throw new ArgumentException("Invalid deletion request.");
        Resource = resource; OperationId = operationId; ConfirmationId = confirmationId; Expected = expected;
    }

    public ResourceKey Resource { get; }
    public Guid OperationId { get; }
    public Guid ConfirmationId { get; }
    public ResourceLifecycleVersion Expected { get; }
}

public sealed record ResourcePurgeResult(ResourcePurgeOutcome Outcome, DeletionJobStatus? Job = null, long? ActiveReleases = null);

public enum DeletionResumeOutcome { Resumed = 1, Replayed, NotAuthorizedOrNotFound, StillBlocked, Conflict, InvalidRequest }

/// <summary>Fresh authority on every call; replaying a receipt never repeats the request.</summary>
public interface IResourcePurgeStore
{
    Task<ResourcePurgePreview?> PreviewAsync(ActorContext actor, ResourceKey resource, CancellationToken token);
    Task<ResourcePurgeResult> RequestAsync(ActorContext actor, ResourcePurgeRequest request, CancellationToken token);
    Task<DeletionJobStatus?> ReadJobAsync(ActorContext actor, Guid organizationId, Guid jobId, CancellationToken token);
    Task<DeletionJobStatus?> ReadOperationAsync(ActorContext actor, Guid organizationId, Guid operationId, CancellationToken token);
    /// <summary>Resumes a blocked job where it stopped, once its blocker is gone; audited, never repeated for one operation.</summary>
    Task<(DeletionResumeOutcome Outcome, DeletionJobStatus? Job)> ResumeAsync(ActorContext actor, Guid organizationId, Guid jobId, Guid operationId, CancellationToken token);
}

/// <summary>Recognizes the database refusal to change a deleting or deleted lifecycle identity.</summary>
public static class ResourcePurgeFence
{
    public const string SqlState = "PWD02";

    public static bool Refused(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 8; error = error.InnerException, depth++)
            if (error is System.Data.Common.DbException { SqlState: SqlState }) return true;
        return false;
    }
}
