namespace Braid.Application.Lifecycle;

public enum TargetRetirementOutcome { Retired = 1, Replayed, NotAuthorizedOrNotFound, PreconditionFailed, Conflict, InvalidRequest }

/// <summary>Retires one target at its exact event version and current active release.</summary>
public sealed record TargetRetirement
{
    public TargetRetirement(Guid organizationId, Guid projectId, Guid targetId, Guid operationId, long expectedVersion, Guid activeReleaseId)
    {
        if (organizationId == Guid.Empty || projectId == Guid.Empty || targetId == Guid.Empty || operationId == Guid.Empty ||
            expectedVersion < 1 || activeReleaseId == Guid.Empty) throw new ArgumentException("Invalid target retirement.");
        OrganizationId = organizationId; ProjectId = projectId; TargetId = targetId; OperationId = operationId;
        ExpectedVersion = expectedVersion; ActiveReleaseId = activeReleaseId;
    }

    public Guid OrganizationId { get; }
    public Guid ProjectId { get; }
    public Guid TargetId { get; }
    public Guid OperationId { get; }
    public long ExpectedVersion { get; }
    public Guid ActiveReleaseId { get; }
}

public sealed record TargetRetirementReceipt(TargetRetirementOutcome Outcome, Guid OperationId, Guid ProjectId, Guid MappingId, Guid TargetId,
    Guid RetiredReleaseId, long TargetVersion, DateTimeOffset RecordedAtUtc);

/// <summary>Fresh exact-project administration on every call; a replay never repeats the change.</summary>
public interface ITargetRetirementStore
{
    Task<(TargetRetirementOutcome Outcome, TargetRetirementReceipt? Receipt)> RetireAsync(ActorContext actor, TargetRetirement retirement, CancellationToken token);
    Task<TargetRetirementReceipt?> ReadRetirementAsync(ActorContext actor, Guid organizationId, Guid operationId, CancellationToken token);
}

/// <summary>Worker-only bounded erasure of items an organization policy has expired.</summary>
public interface IRetentionSweepStore
{
    /// <summary>Erases at most <paramref name="limit"/> due items of one family in one organization; returns how many.</summary>
    Task<int> SweepAsync(Guid instanceId, int limit, CancellationToken token);
}
