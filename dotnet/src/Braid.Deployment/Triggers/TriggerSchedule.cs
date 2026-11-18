using System.Globalization;
using System.Text.RegularExpressions;

namespace Braid.Deployment.Triggers;

/// <summary>
/// A five-field cron expression (minute hour day-of-month month day-of-week) with numeric fields only: *, n, a-b, */s and
/// a-b/s, comma separated. Day of week is 0-7 with 0 and 7 both Sunday. Restricting both day-of-month and day-of-week is
/// refused because their combination rule would be implicit.
/// </summary>
public sealed partial class CronExpression
{
    public const string Grammar = "braid.cron5.v1";
    private readonly bool[] minutes, hours, days, months, weekdays;

    public CronExpression(string text)
    {
        if (text is null || text.Length > 256 || !ShapePattern().IsMatch(text)) throw new ArgumentException("Invalid cron expression.");
        var fields = text.Split(' ');
        if (fields[2] != "*" && fields[4] != "*") throw new ArgumentException("A cron expression may restrict day of month or day of week, not both.");
        minutes = Field(fields[0], 0, 59); hours = Field(fields[1], 0, 23); days = Field(fields[2], 1, 31); months = Field(fields[3], 1, 12);
        var week = Field(fields[4], 0, 7);
        week[0] |= week[7];
        weekdays = week[..7];
        Text = text;
    }

    public string Text { get; }
    public bool Matches(DateOnly date) => months[date.Month] && days[date.Day] && weekdays[(int)date.DayOfWeek];
    public IEnumerable<TimeOnly> Times()
    {
        for (var hour = 0; hour < 24; hour++)
            if (hours[hour])
                for (var minute = 0; minute < 60; minute++)
                    if (minutes[minute]) yield return new(hour, minute);
    }

    private static bool[] Field(string field, int minimum, int maximum)
    {
        var set = new bool[maximum + 1];
        foreach (var item in field.Split(','))
        {
            var match = ItemPattern().Match(item);
            if (!match.Success) throw new ArgumentException("Invalid cron field.");
            int from = minimum, to = maximum, step = 1;
            if (match.Groups["from"].Success)
            {
                from = Number(match.Groups["from"].Value);
                to = match.Groups["to"].Success ? Number(match.Groups["to"].Value) : from;
            }
            if (match.Groups["step"].Success)
            {
                step = Number(match.Groups["step"].Value);
                // A step applies to a wildcard or an explicit range; "5/10" would leave its end implicit.
                if (match.Groups["from"].Success && !match.Groups["to"].Success) throw new ArgumentException("Invalid cron step.");
            }
            if (from < minimum || to > maximum || from > to || step < 1 || step > maximum) throw new ArgumentException("Cron value out of range.");
            for (var value = from; value <= to; value += step) set[value] = true;
        }
        return set;
    }

    private static int Number(string text) => text.Length > 2 ? int.MaxValue : int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[0-9*,/-]+( [0-9*,/-]+){4}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShapePattern();
    [GeneratedRegex(@"^(?:\*|(?<from>[0-9]{1,2})(?:-(?<to>[0-9]{1,2}))?)(?:/(?<step>[0-9]{1,2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemPattern();
    public override string ToString() => Text;
}

/// <summary>A nonexistent local time (a spring-forward gap) is skipped; there is no other gap behavior.</summary>
public enum ScheduleGap { Skip }

/// <summary>Which instant of a repeated local time (a fall-back fold) runs: the earlier, the later or both.</summary>
public enum ScheduleFold { First, Second, Both }

/// <summary>An occurrence that falls due while a run of the same trigger is unfinished is recorded as skipped.</summary>
public enum ScheduleOverlap { SkipIfRunning }

/// <summary>
/// None runs only the latest due occurrence and records earlier ones as missed. Bounded runs up to the explicit number of the
/// latest due occurrences in chronological order. Both consider only occurrences inside the explicit lookback.
/// </summary>
public enum ScheduleCatchUp { None, Bounded }

/// <summary>One scheduled instant: its UTC time and the local offset it was resolved with.</summary>
public sealed record ScheduledOccurrence(DateTimeOffset ScheduledAt, TimeSpan Offset);

public enum OccurrenceDisposition { Run, Missed }

public sealed record PlannedOccurrence(ScheduledOccurrence Occurrence, OccurrenceDisposition Disposition);

/// <summary>
/// An explicit schedule: cron grammar, IANA time zone, the time-zone data version it was reviewed against, effective start,
/// gap, fold, overlap and catch-up choices. Nothing has a default; UTC is selectable, never assumed.
/// </summary>
public sealed partial class TriggerSchedule
{
    public static readonly TimeSpan MaximumLookback = TimeSpan.FromDays(7);
    public const int MaximumCatchUpRuns = 64;
    /// <summary>shortcut: one plan considers at most the latest 1024 occurrences in its lookback; a longer outage loses older missed records.</summary>
    public const int MaximumPlannedOccurrences = 1024;

    public TriggerSchedule(CronExpression cron, string timeZoneId, string timeZoneDataVersion, DateTimeOffset effectiveFrom, ScheduleGap gap,
        ScheduleFold fold, ScheduleOverlap overlap, ScheduleCatchUp catchUp, TimeSpan lookback, int? maximumCatchUp)
    {
        ArgumentNullException.ThrowIfNull(cron);
        if (timeZoneId is null || !ZonePattern().IsMatch(timeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found) ||
            timeZoneDataVersion is null || !VersionPattern().IsMatch(timeZoneDataVersion) ||
            effectiveFrom.Offset != TimeSpan.Zero || effectiveFrom.Ticks % TimeSpan.TicksPerSecond != 0 ||
            !Enum.IsDefined(gap) || !Enum.IsDefined(fold) || !Enum.IsDefined(overlap) || !Enum.IsDefined(catchUp) ||
            lookback <= TimeSpan.Zero || lookback > MaximumLookback || lookback.Ticks % TimeSpan.TicksPerSecond != 0 ||
            (catchUp == ScheduleCatchUp.Bounded ? maximumCatchUp is not (>= 1 and <= MaximumCatchUpRuns) : maximumCatchUp is not null))
            throw new ArgumentException("Invalid trigger schedule.");
        Cron = cron; TimeZoneId = timeZoneId; TimeZoneDataVersion = timeZoneDataVersion; EffectiveFrom = effectiveFrom; Gap = gap; Fold = fold;
        Overlap = overlap; CatchUp = catchUp; Lookback = lookback; MaximumCatchUp = maximumCatchUp; zone = found;
    }

    public CronExpression Cron { get; }
    public string TimeZoneId { get; }
    /// <summary>The IANA tz database release (for example 2025b) this schedule was reviewed against. Other rules refuse to plan.</summary>
    public string TimeZoneDataVersion { get; }
    public DateTimeOffset EffectiveFrom { get; }
    public ScheduleGap Gap { get; }
    public ScheduleFold Fold { get; }
    public ScheduleOverlap Overlap { get; }
    public ScheduleCatchUp CatchUp { get; }
    public TimeSpan Lookback { get; }
    public int? MaximumCatchUp { get; }
    private readonly TimeZoneInfo zone;

    /// <summary>Occurrences in (after, until], chronological, never before the effective start.</summary>
    public IReadOnlyList<ScheduledOccurrence> Between(DateTimeOffset after, DateTimeOffset until)
    {
        if (until - after > MaximumLookback + TimeSpan.FromDays(1)) throw new ArgumentException("The occurrence window exceeds the schedule bound.");
        var result = new List<ScheduledOccurrence>();
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(until, zone).DateTime).AddDays(1);
        for (var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime).AddDays(-1); date <= last; date = date.AddDays(1))
        {
            if (!Cron.Matches(date)) continue;
            foreach (var time in Cron.Times())
            {
                var local = date.ToDateTime(time, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local)) continue;
                TimeSpan[] offsets = zone.IsAmbiguousTime(local)
                    ? [.. zone.GetAmbiguousTimeOffsets(local).OrderDescending()] : [zone.GetUtcOffset(local)];
                if (offsets.Length == 2) offsets = Fold switch { ScheduleFold.First => [offsets[0]], ScheduleFold.Second => [offsets[1]], _ => offsets };
                foreach (var offset in offsets)
                {
                    var at = new DateTimeOffset(local, offset).ToUniversalTime();
                    if (at > after && at <= until && at >= EffectiveFrom) result.Add(new(at, offset));
                }
            }
        }
        return [.. result.OrderBy(value => value.ScheduledAt)];
    }

    /// <summary>
    /// Every unrecorded occurrence due by now inside the lookback, in chronological order, each either run or recorded missed.
    /// Planning refuses when the runtime's time-zone data is not the reviewed version.
    /// </summary>
    public IReadOnlyList<PlannedOccurrence> Plan(DateTimeOffset? latestRecorded, DateTimeOffset now, string? runtimeTimeZoneDataVersion)
    {
        if (runtimeTimeZoneDataVersion != TimeZoneDataVersion)
            throw new InvalidOperationException("The runtime time-zone data differs from the reviewed schedule; a schedule revision is required.");
        now = now.ToUniversalTime();
        var after = new[] { latestRecorded?.ToUniversalTime() ?? EffectiveFrom.AddTicks(-1), now - Lookback, EffectiveFrom.AddTicks(-1) }.Max();
        if (after >= now) return [];
        var due = Between(after, now);
        if (due.Count > MaximumPlannedOccurrences) due = due.Skip(due.Count - MaximumPlannedOccurrences).ToArray();
        var runs = CatchUp == ScheduleCatchUp.None ? 1 : MaximumCatchUp!.Value;
        return [.. due.Select((occurrence, index) => new PlannedOccurrence(occurrence,
            index >= due.Count - runs ? OccurrenceDisposition.Run : OccurrenceDisposition.Missed))];
    }

    [GeneratedRegex("^(?:UTC|[A-Z][A-Za-z0-9_+-]*(?:/[A-Za-z0-9_+-]+){1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ZonePattern();
    [GeneratedRegex("^[0-9]{4}[a-z]$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    public override string ToString() => nameof(TriggerSchedule);
}

/// <summary>The host's IANA tz database release, read from the zoneinfo directory .NET itself uses; null when it is not identifiable.</summary>
public static partial class TriggerTimeZoneData
{
    public static string? CurrentVersion()
    {
        var directory = Environment.GetEnvironmentVariable("TZDIR") is { Length: > 0 } configured ? configured : "/usr/share/zoneinfo";
        try
        {
            foreach (var (file, pattern) in new[] { ("tzdata.zi", ZiPattern()), ("+VERSION", PlainPattern()) })
            {
                var path = Path.Combine(directory, file);
                if (!File.Exists(path)) continue;
                using var reader = new StreamReader(path);
                if (reader.ReadLine() is { } line && pattern.Match(line.Trim()) is { Success: true } match) return match.Groups[1].Value;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return null;
    }

    [GeneratedRegex("^# version ([0-9]{4}[a-z])$", RegexOptions.CultureInvariant)]
    private static partial Regex ZiPattern();
    [GeneratedRegex("^([0-9]{4}[a-z])$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainPattern();
}
