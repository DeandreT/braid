using System.Text;
using Braid.Application;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Pure cron grammar, DST gaps and folds, catch-up planning, batch envelopes and the schedule configuration contract.</summary>
public sealed class TriggerScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0 * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("5-1 * * * *")]
    [InlineData("5/10 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("0 0 1 * 1")]
    [InlineData("0  0 * * *")]
    [InlineData("0 0 * JAN *")]
    [InlineData("0 0 L * *")]
    public void The_cron_grammar_is_numeric_explicit_and_refuses_an_implicit_day_rule(string text) =>
        Assert.Throws<ArgumentException>(() => new CronExpression(text));

    [Fact]
    public void Fields_lists_ranges_and_steps_match_exactly()
    {
        var cron = new CronExpression("0,30 9-17/4 * * 1-5");
        Assert.Equal([new TimeOnly(9, 0), new(9, 30), new(13, 0), new(13, 30), new(17, 0), new(17, 30)], cron.Times());
        Assert.True(cron.Matches(new DateOnly(2026, 11, 9)));
        Assert.False(cron.Matches(new DateOnly(2026, 11, 8)));
        Assert.True(new CronExpression("0 0 * * 7").Matches(new DateOnly(2026, 11, 8)));
    }

    [Fact]
    public void A_spring_forward_gap_is_skipped_and_a_fall_back_fold_follows_the_explicit_selection()
    {
        var after = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);
        var gap = Schedule("30 2 * * *", "America/New_York").Between(after, after.AddDays(2));
        // 2:30 does not exist on 8 March in New York; 9 March runs at 2:30 EDT.
        Assert.Equal([new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero)], gap.Select(value => value.ScheduledAt));
        var fold = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var first = Schedule("30 1 1 11 *", "America/New_York", ScheduleFold.First).Between(fold, fold.AddDays(2));
        var second = Schedule("30 1 1 11 *", "America/New_York", ScheduleFold.Second).Between(fold, fold.AddDays(2));
        var both = Schedule("30 1 1 11 *", "America/New_York", ScheduleFold.Both).Between(fold, fold.AddDays(2));
        Assert.Equal([new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero)], first.Select(value => value.ScheduledAt));
        Assert.Equal([new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero)], second.Select(value => value.ScheduledAt));
        Assert.Equal([TimeSpan.FromHours(-4), TimeSpan.FromHours(-5)], both.Select(value => value.Offset));
        Assert.Equal(TimeSpan.Zero, Assert.Single(Schedule("0 12 8 11 *", "UTC").Between(fold, fold.AddDays(8))).Offset);
    }

    [Fact]
    public void Catch_up_none_runs_only_the_latest_due_occurrence_and_records_the_rest_missed()
    {
        var schedule = Schedule("0 * * * *", "UTC", lookback: TimeSpan.FromHours(5));
        var now = new DateTimeOffset(2026, 11, 8, 10, 20, 0, TimeSpan.Zero);
        var plan = schedule.Plan(now.AddHours(-3).AddMinutes(-20), now, "2025b");
        Assert.Equal([OccurrenceDisposition.Missed, OccurrenceDisposition.Missed, OccurrenceDisposition.Run], plan.Select(value => value.Disposition));
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero), plan[^1].Occurrence.ScheduledAt);
        // Nothing older than the lookback or before the effective start, and nothing after a recorded occurrence repeats.
        Assert.Equal(5, schedule.Plan(null, now, "2025b").Count);
        Assert.Empty(schedule.Plan(now.AddMinutes(-20), now, "2025b"));
        Assert.Empty(Schedule("0 * * * *", "UTC", effectiveFrom: now.AddMinutes(1)).Plan(null, now, "2025b"));
    }

    [Fact]
    public void Bounded_catch_up_runs_the_explicit_number_of_latest_occurrences_in_order()
    {
        var schedule = Schedule("*/15 * * * *", "UTC", catchUp: ScheduleCatchUp.Bounded, maximumCatchUp: 2, lookback: TimeSpan.FromHours(1));
        var now = new DateTimeOffset(2026, 11, 8, 10, 50, 0, TimeSpan.Zero);
        var plan = schedule.Plan(null, now, "2025b");
        Assert.Equal(["10:00 Missed", "10:15 Missed", "10:30 Run", "10:45 Run"],
            plan.Select(value => $"{value.Occurrence.ScheduledAt:HH:mm} {value.Disposition}"));
    }

    [Fact]
    public void Other_time_zone_data_refuses_to_plan_and_every_choice_is_explicit()
    {
        Assert.Throws<InvalidOperationException>(() => Schedule("0 * * * *", "UTC").Plan(null, Start.AddDays(1), "2026a"));
        Assert.Throws<InvalidOperationException>(() => Schedule("0 * * * *", "UTC").Plan(null, Start.AddDays(1), null));
        var cron = new CronExpression("0 * * * *");
        TriggerSchedule Create(string zone = "UTC", string version = "2025b", ScheduleCatchUp catchUp = ScheduleCatchUp.None, int? maximum = null,
            TimeSpan? lookback = null, DateTimeOffset? from = null) =>
            new(cron, zone, version, from ?? Start, ScheduleGap.Skip, ScheduleFold.First, ScheduleOverlap.SkipIfRunning, catchUp, lookback ?? TimeSpan.FromHours(1), maximum);
        Assert.Throws<ArgumentException>(() => Create(zone: "Eastern Standard Time"));
        Assert.Throws<ArgumentException>(() => Create(zone: "Mars/Olympus"));
        Assert.Throws<ArgumentException>(() => Create(version: "latest"));
        Assert.Throws<ArgumentException>(() => Create(catchUp: ScheduleCatchUp.Bounded));
        Assert.Throws<ArgumentException>(() => Create(maximum: 1));
        Assert.Throws<ArgumentException>(() => Create(lookback: TimeSpan.FromDays(8)));
        Assert.Throws<ArgumentException>(() => Create(from: Start.ToOffset(TimeSpan.FromHours(1))));
        _ = Create(zone: "Europe/London", catchUp: ScheduleCatchUp.Bounded, maximum: 3);
    }

    [Fact]
    public void Schedule_configurations_round_trip_canonically_and_queue_bytes_are_unchanged()
    {
        var target = TriggerRuntimeTests.Target();
        var queue = TriggerRuntimeTests.Configuration(target);
        Assert.DoesNotContain("schedule", Encoding.UTF8.GetString(queue.CopyCanonicalBytes()), StringComparison.Ordinal);
        Assert.Contains("\"envelope\":\"singleOrder\"", Encoding.UTF8.GetString(queue.CopyCanonicalBytes()), StringComparison.Ordinal);
        var scheduled = Configuration(target, Schedule("15 6 * * 1-5", "Europe/Berlin", ScheduleFold.Both), TriggerEnvelope.Batch);
        var parsed = TriggerConfiguration.Parse(scheduled.CopyCanonicalBytes());
        Assert.Equal(scheduled.CopyCanonicalBytes(), parsed.CopyCanonicalBytes());
        Assert.Equal((TriggerKind.Schedule, TriggerEnvelope.Batch, "15 6 * * 1-5", ScheduleFold.Both),
            (parsed.Kind, parsed.Envelope, parsed.Schedule!.Cron.Text, parsed.Schedule.Fold));
        Assert.Throws<ArgumentException>(() => Configuration(target, null));
        Assert.Throws<ArgumentException>(() => new TriggerConfiguration(Guid.NewGuid(), target, TriggerKind.ServiceBusQueue, Guid.NewGuid(), new("orders-in", 1),
            new(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), "application/json", [], [new(null, Guid.NewGuid(), TriggerDestinationKind.Blob, new("blob-out", 1))],
            null, TriggerEnvelope.SingleOrder, Schedule("0 * * * *", "UTC")));
        var tampered = Encoding.UTF8.GetString(scheduled.CopyCanonicalBytes()).Replace("\"overlap\":\"skipIfRunning\"", "\"overlap\":\"parallel\"", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => TriggerConfiguration.Parse(Encoding.UTF8.GetBytes(tampered)));
    }

    [Fact]
    public void A_batch_envelope_is_exact_keyed_and_bounded_or_refused()
    {
        string Envelope(string orders, string schema = TriggerBatchEnvelope.Schema) => "{\"schema\":\"" + schema + "\",\"orders\":[" + orders + "]}";
        string Order(string key, string content) => "{\"key\":\"" + key + "\",\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) + "\"}";
        var orders = TriggerBatchEnvelope.Parse(Encoding.UTF8.GetBytes(Envelope(Order("A-1", "{}") + "," + Order("a-1", "{\"x\":1}"))), 64)!;
        Assert.Equal(["A-1", "a-1"], orders.Select(order => order.Key));
        Assert.Equal("{\"x\":1}"u8.ToArray(), orders[1].CopyContent());
        foreach (var invalid in new[]
        {
            Envelope(""), Envelope(Order("k", "{}"), "braid.trigger-batch.v2"), Envelope(Order("k", "{}") + "," + Order("k", "{}")),
            Envelope(Order("", "{}")), Envelope(Order("k", new string('x', 65))), Envelope("{\"key\":\"k\",\"content\":\"e30=\",\"extra\":1}"),
            Envelope("{\"key\":\"k\",\"content\":\"not base64!\"}"), "{\"schema\":\"braid.trigger-batch.v1\",\"orders\":[],\"orders\":[]}", "[]", "not json"
        })
            Assert.Null(TriggerBatchEnvelope.Parse(Encoding.UTF8.GetBytes(invalid), 64));
        var same = TriggerBatchEnvelope.Parse(Encoding.UTF8.GetBytes(Envelope(Order("A-1", "{}") + "," + Order("a-1", "{\"x\":1}"))), 64)!;
        var changed = TriggerBatchEnvelope.Parse(Encoding.UTF8.GetBytes(Envelope(Order("A-1", "{}") + "," + Order("a-1", "{\"x\":2}"))), 64)!;
        Assert.Equal(TriggerBatchEnvelope.ManifestDigest(orders), TriggerBatchEnvelope.ManifestDigest(same));
        Assert.NotEqual(TriggerBatchEnvelope.ManifestDigest(orders), TriggerBatchEnvelope.ManifestDigest(changed));
    }

    [Fact]
    public void A_source_identity_is_a_private_digest_of_the_exact_name_and_version()
    {
        var item = new TriggerSourceItem("orders/2026/a.json", "\"0x8D\"", 10);
        Assert.Matches("^src:[0-9a-f]{64}$", item.Identity);
        Assert.DoesNotContain("orders", item.Identity, StringComparison.Ordinal);
        Assert.NotEqual(item.Identity, new TriggerSourceItem("orders/2026/a.json", "\"0x8E\"", 10).Identity);
        Assert.NotEqual(item.Identity, new TriggerSourceItem("orders/2026/A.json", "\"0x8D\"", 10).Identity);
    }

    internal static TriggerSchedule Schedule(string cron, string zone, ScheduleFold fold = ScheduleFold.First, ScheduleCatchUp catchUp = ScheduleCatchUp.None,
        int? maximumCatchUp = null, TimeSpan? lookback = null, DateTimeOffset? effectiveFrom = null) =>
        new(new(cron), zone, "2025b", effectiveFrom ?? Start, ScheduleGap.Skip, fold, ScheduleOverlap.SkipIfRunning, catchUp, lookback ?? TimeSpan.FromDays(1), maximumCatchUp);

    internal static TriggerConfiguration Configuration(ReleaseTargetReference target, TriggerSchedule? schedule, TriggerEnvelope envelope = TriggerEnvelope.SingleOrder,
        MachinePrincipal? caller = null, Guid? application = null, Guid? triggerId = null, Guid? binding = null, IReadOnlyList<TriggerRoute>? routes = null,
        TriggerPolicies? policies = null) =>
        new(triggerId ?? Guid.NewGuid(), target, TriggerKind.Schedule, binding ?? Guid.NewGuid(), new("orders-blob", 1),
            caller ?? new(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid()), application ?? Guid.NewGuid(), "application/json",
            [new("region", PreviewParameterKind.String, "west")], routes ?? [new(null, Guid.NewGuid(), TriggerDestinationKind.Blob, new("blob-out", 1))],
            policies ?? TriggerRuntimeTests.DefaultTestPolicies, envelope, schedule);
}
