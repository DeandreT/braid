using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed class AiUsageTotalsTests
{
    [Fact]
    public void No_activity_is_known_zero_but_an_unreported_round_is_unknown()
    {
        var usage = new AiUsageTotals();
        Assert.Equal(0, usage.Snapshot().TotalTokens);
        usage.AddRound(null);
        var result = usage.Snapshot();
        Assert.Equal(1, result.Rounds);
        Assert.Null(result.InputTokens); Assert.Null(result.OutputTokens); Assert.Null(result.TotalTokens);
        Assert.Equal(1, result.UnknownInputRounds); Assert.Equal(1, result.UnknownOutputRounds); Assert.Equal(1, result.UnknownTotalRounds);
        Assert.Equal(0, result.KnownTotalTokens);
    }

    [Fact]
    public void Partial_provider_usage_is_not_inferred_from_other_counts()
    {
        var usage = new AiUsageTotals();
        usage.AddRound(new(Guid.NewGuid(), ChatUsageOrigin.ProviderRoundTerminal, 8, 2, null));
        usage.AddRound(new(Guid.NewGuid(), ChatUsageOrigin.ProviderRoundTerminal, 7, null, 10));
        var result = usage.Snapshot();
        Assert.Equal(15, result.InputTokens);
        Assert.Null(result.OutputTokens); Assert.Null(result.TotalTokens);
        Assert.Equal(2, result.KnownOutputTokens); Assert.Equal(10, result.KnownTotalTokens);
        Assert.Equal(1, result.UnknownOutputRounds); Assert.Equal(1, result.UnknownTotalRounds);
    }

    [Fact]
    public void Failures_have_fixed_categories_and_counts_are_independent()
    {
        var usage = new AiUsageTotals();
        usage.AddTurn(false); usage.AddTurn(true); usage.AddToolCalls(3);
        usage.AddRound(null, ChatProviderFailureCode.ProviderRateLimited);
        usage.AddRound(null, ChatProviderFailureCode.ProviderRateLimited);
        var result = usage.Snapshot();
        Assert.Equal(2, result.Turns); Assert.Equal(1, result.FailedTurns); Assert.Equal(3, result.ToolCalls);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("ProviderRateLimited", failure.Category); Assert.Equal(2, failure.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => usage.AddRound(null, (ChatProviderFailureCode)999));
        Assert.Equal(result, usage.Snapshot() with { Failures = result.Failures });
    }

    [Fact]
    public void Snapshots_do_not_change_when_later_events_are_added()
    {
        var usage = new AiUsageTotals();
        usage.AddRound(null, ChatProviderFailureCode.DeadlineExceeded);
        var first = usage.Snapshot();
        usage.AddRound(null, ChatProviderFailureCode.DeadlineExceeded);
        Assert.Equal(1, Assert.Single(first.Failures).Count);
        Assert.Equal(2, Assert.Single(usage.Snapshot().Failures).Count);
    }

    [Fact]
    public void Invalid_or_overflowing_tool_counts_leave_the_previous_total_intact()
    {
        var usage = new AiUsageTotals();
        Assert.Throws<ArgumentOutOfRangeException>(() => usage.AddToolCalls(-1));
        usage.AddToolCalls(long.MaxValue);
        Assert.Throws<OverflowException>(() => usage.AddToolCalls(1));
        Assert.Equal(long.MaxValue, usage.Snapshot().ToolCalls);
    }

    [Fact]
    public void Intermediate_stream_observations_do_not_become_additional_rounds()
    {
        var usage = new AiUsageTotals();
        Assert.Throws<ArgumentException>(() => usage.AddRound(new(Guid.NewGuid(), ChatUsageOrigin.ProviderObservation, 2, 1, 3)));
        Assert.Equal(0, usage.Snapshot().Rounds);
    }
}
