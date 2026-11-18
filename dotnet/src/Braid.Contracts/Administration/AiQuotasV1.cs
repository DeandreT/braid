namespace Braid.Contracts.Administration;

public sealed record AiQuotaLimitV1(string Period, string? MaximumTurns, string? MaximumTokens);
public sealed record AiQuotaSnapshotV1(Guid OrganizationId, Guid? PrincipalId, string Version, IReadOnlyList<AiQuotaLimitV1> Limits);
public sealed record AiQuotaWriteV1(string? MaximumTurns, string? MaximumTokens);
public sealed record AiQuotaWriteResultV1(string Outcome);
