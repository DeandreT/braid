namespace Braid.Contracts.Administration;

/// <summary>Counts are decimal strings; the confirmation is opaque, short-lived and present only when deletion can be requested.</summary>
public sealed record ResourcePurgePreviewV1(string ResourceKind, Guid ProjectId, Guid? MappingId, string EffectiveState,
    string ProjectVersion, string? MappingVersion, string PlanVersion, string ActiveReleases, string PendingMappingDeletions,
    ResourcePurgeImpactV1 Impact, DeletionJobV1? Deletion, string? Confirmation, DateTimeOffset? ConfirmationExpiresAtUtc);

public sealed record ResourcePurgeImpactV1(string Mappings, string Revisions, string Conversations, string Previews, string RegressionCases,
    string RegressionRuns, string Builds, string ReleaseTargets, string RetainedArtifacts);

/// <summary>Truthful progress: catalog removal and private object erasure are reported separately.</summary>
public sealed record DeletionJobV1(Guid JobId, string ResourceKind, Guid ProjectId, Guid? MappingId, string State, string? Blocker,
    bool CatalogRemoved, string ObjectCount, string ObjectsPending, string ObjectsBlocked, string RetainedArtifacts, string RetainedAuditRecords,
    DateTimeOffset RequestedAtUtc, DateTimeOffset ChangedAtUtc, DateTimeOffset? CatalogRemovedAtUtc, DateTimeOffset? CompletedAtUtc);
