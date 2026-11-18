namespace Braid.Application;

public enum AiQuotaPeriod { Hour = 1, Day, Month }

public sealed record AiQuotaWindow(DateTimeOffset Since, DateTimeOffset Until);

/// <summary>UTC admission policy only. The database must check and reserve its count atomically.</summary>
public sealed class AiTurnQuota
{
    public AiTurnQuota(AiQuotaPeriod period, long maximumTurns)
    {
        if (!Enum.IsDefined(period)) throw new ArgumentOutOfRangeException(nameof(period));
        if (maximumTurns < 1) throw new ArgumentOutOfRangeException(nameof(maximumTurns));
        Period = period; MaximumTurns = maximumTurns;
    }

    public AiQuotaPeriod Period { get; }
    public long MaximumTurns { get; }

    public bool HasCapacity(long admittedTurns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(admittedTurns);
        return admittedTurns < MaximumTurns;
    }

    public AiQuotaWindow WindowAt(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        var start = Period switch
        {
            AiQuotaPeriod.Hour => new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero),
            AiQuotaPeriod.Day => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero),
            AiQuotaPeriod.Month => new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero),
            _ => throw new InvalidOperationException("Invalid AI quota period.")
        };
        var end = Period switch
        {
            AiQuotaPeriod.Hour => start.AddHours(1),
            AiQuotaPeriod.Day => start.AddDays(1),
            AiQuotaPeriod.Month => start.AddMonths(1),
            _ => throw new InvalidOperationException("Invalid AI quota period.")
        };
        return new(start, end);
    }
}
