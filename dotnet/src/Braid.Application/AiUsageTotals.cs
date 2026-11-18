using Braid.Domain;

namespace Braid.Application;

public sealed record AiUsageFailure(string Category, long Count);

public sealed record AiUsageSnapshot(long Turns, long Rounds, long ToolCalls, long FailedTurns,
    long? InputTokens, long? OutputTokens, long? TotalTokens,
    long KnownInputTokens, long KnownOutputTokens, long KnownTotalTokens,
    long UnknownInputRounds, long UnknownOutputRounds, long UnknownTotalRounds,
    IReadOnlyList<AiUsageFailure> Failures);

/// <summary>Metadata-only accounting. Missing provider observations never become zero usage.</summary>
public sealed class AiUsageTotals
{
    private readonly SortedDictionary<string, long> failures = new(StringComparer.Ordinal);
    private long turns, rounds, toolCalls, failedTurns;
    private long input, output, total, unknownInput, unknownOutput, unknownTotal;

    public void AddTurn(bool failed)
    {
        var nextTurns = checked(turns + 1);
        var nextFailed = checked(failedTurns + (failed ? 1 : 0));
        turns = nextTurns; failedTurns = nextFailed;
    }

    public void AddToolCalls(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        toolCalls = checked(toolCalls + count);
    }

    public void AddRound(ChatUsageFact? observation, ChatProviderFailureCode? failure = null)
    {
        if (observation is { Origin: ChatUsageOrigin.ProviderObservation })
            throw new ArgumentException("Usage totals require a terminal round observation.", nameof(observation));
        if (failure is { } category && !Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(failure));
        // Compute every new value first: an overflow must not publish half a round.
        var nextRounds = checked(rounds + 1);
        var nextInput = checked(input + (observation?.InputTokens ?? 0));
        var nextOutput = checked(output + (observation?.OutputTokens ?? 0));
        var nextTotal = checked(total + (observation?.TotalTokens ?? 0));
        var nextUnknownInput = checked(unknownInput + (observation?.InputTokens is null ? 1 : 0));
        var nextUnknownOutput = checked(unknownOutput + (observation?.OutputTokens is null ? 1 : 0));
        var nextUnknownTotal = checked(unknownTotal + (observation?.TotalTokens is null ? 1 : 0));
        var name = failure?.ToString();
        var nextFailure = name is null ? 0 : checked(failures.GetValueOrDefault(name) + 1);
        rounds = nextRounds; input = nextInput; output = nextOutput; total = nextTotal;
        unknownInput = nextUnknownInput; unknownOutput = nextUnknownOutput; unknownTotal = nextUnknownTotal;
        if (name is not null) failures[name] = nextFailure;
    }

    public AiUsageSnapshot Snapshot() => new(turns, rounds, toolCalls, failedTurns,
        unknownInput == 0 ? input : null, unknownOutput == 0 ? output : null, unknownTotal == 0 ? total : null,
        input, output, total, unknownInput, unknownOutput, unknownTotal,
        failures.Select(item => new AiUsageFailure(item.Key, item.Value)).ToArray());
}
