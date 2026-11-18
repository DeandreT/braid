namespace Braid.Contracts.Administration;

/// <summary>The exact target event version (decimal string) and the active release being retired.</summary>
public sealed record TargetRetirementRequestV1(string ExpectedVersion, Guid ActiveReleaseId);
public sealed record TargetRetirementReceiptV1(string Outcome, Guid OperationId, Guid ProjectId, Guid MappingId, Guid TargetId,
    Guid RetiredReleaseId, string TargetVersion, DateTimeOffset RecordedAtUtc);
