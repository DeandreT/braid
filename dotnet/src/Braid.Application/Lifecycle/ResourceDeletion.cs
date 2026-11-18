using Braid.Domain;

namespace Braid.Application.Lifecycle;

public enum ResourceItemKind { Preview = 1, RegressionCase, BuildEnvironment, Conversation }

/// <summary>Closed outcomes; a database failure is never reported as not found.</summary>
public enum ResourceDeletionOutcome { Deleted = 1, Replayed, NotAuthorizedOrNotFound, PreconditionFailed, ActiveWork, ReferencedResource, Conflict, InvalidRequest }

/// <summary>
/// One item deletion. Cases and environments carry their project scope and exact head version;
/// previews and conversations are located by identity and owned by their submitter or creator.
/// </summary>
public sealed record ResourceItemDeletion
{
    public ResourceItemDeletion(Guid organizationId, ResourceItemKind kind, Guid resourceId, Guid operationId,
        Guid? projectId = null, Guid? mappingId = null, long? expectedVersion = null)
    {
        var scoped = kind is ResourceItemKind.RegressionCase or ResourceItemKind.BuildEnvironment;
        if (!Enum.IsDefined(kind) || organizationId == Guid.Empty || resourceId == Guid.Empty || operationId == Guid.Empty ||
            scoped != (projectId is not null) || scoped != (expectedVersion is not null) || expectedVersion < 1 ||
            (kind == ResourceItemKind.RegressionCase) != (mappingId is not null) || projectId == Guid.Empty || mappingId == Guid.Empty)
            throw new ArgumentException("Invalid resource deletion.");
        OrganizationId = organizationId; Kind = kind; ResourceId = resourceId; OperationId = operationId;
        ProjectId = projectId; MappingId = mappingId; ExpectedVersion = expectedVersion;
    }

    public Guid OrganizationId { get; }
    public ResourceItemKind Kind { get; }
    public Guid ResourceId { get; }
    public Guid OperationId { get; }
    public Guid? ProjectId { get; }
    public Guid? MappingId { get; }
    public long? ExpectedVersion { get; }
}

public sealed record ResourceDeletionReceipt(ResourceDeletionOutcome Outcome, Guid OperationId, ResourceItemKind Kind, Guid ResourceId,
    Guid? ProjectId, Guid? MappingId, DateTimeOffset RecordedAtUtc);

/// <summary>Reference counts by category only, reported after authority is established.</summary>
public sealed record ResourceDeletionReferences(long? RegressionRuns, long? AutomationEvaluations, long? BuildApprovals, long? ReleaseTargets);

public sealed record ResourceDeletionResult(ResourceDeletionOutcome Outcome, ResourceDeletionReceipt? Receipt = null, ResourceDeletionReferences? References = null);

/// <summary>Fresh authority on every call; replaying a receipt never repeats the deletion.</summary>
public interface IResourceDeletionStore
{
    Task<ResourceDeletionResult> DeleteAsync(ActorContext actor, ResourceItemDeletion deletion, CancellationToken token);
    Task<ResourceDeletionReceipt?> ReadOperationAsync(ActorContext actor, Guid organizationId, Guid operationId, CancellationToken token);
}

/// <summary>Recognizes the database refusal to reuse an identity retired by an item deletion.</summary>
public static class ResourceIdentityRetirement
{
    public const string SqlState = "PWD01";

    public static bool Refused(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 8; error = error.InnerException, depth++)
            if (error is System.Data.Common.DbException { SqlState: SqlState }) return true;
        return false;
    }
}
