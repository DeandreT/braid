namespace Braid.Contracts.Administration;

public sealed record AiUsageFailureV1(string Category, string Count);

// Canonical decimal strings preserve exact Int64 counts in JavaScript clients.
public sealed record AiUsageTotalsV1(string Turns, string Rounds, string ToolCalls, string FailedTurns,
    string? InputTokens, string? OutputTokens, string? TotalTokens,
    string KnownInputTokens, string KnownOutputTokens, string KnownTotalTokens,
    string UnknownInputRounds, string UnknownOutputRounds, string UnknownTotalRounds,
    IReadOnlyList<AiUsageFailureV1> Failures);

public sealed record AiUsageGroupV1(Guid OrganizationId, Guid? ProjectId, Guid? PrincipalId,
    string? ProfileId, int? ProfileVersion, AiUsageTotalsV1 Usage);

public sealed record AiUsagePageV1(DateTimeOffset Since, DateTimeOffset Until,
    IReadOnlyList<AiUsageGroupV1> Groups, string? NextCursor, bool OrganizationWide,
    DateTimeOffset ObservedAtUtc, DateTimeOffset CompleteHistorySince, bool IncludesPartialHistory);
