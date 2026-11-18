namespace Braid.Contracts.Administration;

/// <summary>Versions are decimal strings; the strong ETag carries the same effective versions.</summary>
public sealed record ResourceLifecycleV1(string ResourceKind, Guid ProjectId, Guid? MappingId, string State, string EffectiveState,
    string ProjectVersion, string? MappingVersion, DateTimeOffset? ArchivedAtUtc, bool CanArchive, bool CanRestore, Guid? DeletionJobId = null);
public sealed record ResourceLifecycleActionV1(string Action);
public sealed record ResourceLifecycleReceiptV1(string Outcome, Guid OperationId, string ResourceKind, Guid ProjectId, Guid? MappingId,
    string Action, string State, string ProjectVersion, string? MappingVersion, DateTimeOffset RecordedAtUtc);
