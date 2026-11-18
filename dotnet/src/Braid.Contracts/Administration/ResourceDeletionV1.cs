namespace Braid.Contracts.Administration;

/// <summary>A deleted item's receipt; it outlives the item and carries no name or content.</summary>
public sealed record ResourceDeletionReceiptV1(string Outcome, Guid OperationId, string ResourceKind, Guid ResourceId,
    Guid? ProjectId, Guid? MappingId, DateTimeOffset RecordedAtUtc);

/// <summary>Counts are decimal strings; only the categories relevant to the item are present.</summary>
public sealed record ResourceDeletionReferencesV1(string? RegressionRuns, string? AutomationEvaluations, string? BuildApprovals, string? ReleaseTargets);
public sealed record ResourceDeletionErrorV1(string Code, ResourceDeletionReferencesV1? References);
