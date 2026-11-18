using Braid.Application;

namespace Braid.Tests;

public sealed class AiTurnQuotaTests
{
    [Fact]
    public void Admission_at_the_limit_is_refused_without_incrementing_a_count()
    {
        var policy = new AiTurnQuota(AiQuotaPeriod.Day, 2);
        Assert.True(policy.HasCapacity(0)); Assert.True(policy.HasCapacity(1));
        Assert.False(policy.HasCapacity(2)); Assert.False(policy.HasCapacity(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.HasCapacity(-1));
    }

    [Theory]
    [InlineData(2024, 2, 29, 2024, 3, 1)]
    [InlineData(2026, 12, 31, 2027, 1, 1)]
    public void Daily_windows_roll_over_in_UTC(int year, int month, int day, int endYear, int endMonth, int endDay)
    {
        var window = new AiTurnQuota(AiQuotaPeriod.Day, 5).WindowAt(new(year, month, day, 17, 13, 20, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero), window.Since);
        Assert.Equal(new DateTimeOffset(endYear, endMonth, endDay, 0, 0, 0, TimeSpan.Zero), window.Until);
    }

    [Fact]
    public void Calendar_months_and_offset_inputs_do_not_use_fixed_thirty_day_periods()
    {
        var quota = new AiTurnQuota(AiQuotaPeriod.Month, 100);
        var window = quota.WindowAt(new(2024, 2, 29, 23, 30, 0, TimeSpan.FromHours(-1)));
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), window.Since);
        Assert.Equal(new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero), window.Until);
    }

    [Fact]
    public void Invalid_or_unrepresentable_policies_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiTurnQuota((AiQuotaPeriod)0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiTurnQuota(AiQuotaPeriod.Hour, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiTurnQuota(AiQuotaPeriod.Month, 1).WindowAt(DateTimeOffset.MaxValue));
    }
}
