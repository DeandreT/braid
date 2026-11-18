using System.Text;
using Braid.Application;
using Braid.Contracts.Administration;

namespace Braid.Tests;

public sealed class AuditLogUnitTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly DateTimeOffset Since = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until = Since.AddDays(1);

    [Fact]
    public void Cursor_retains_exact_event_identity_and_is_bound_to_every_filter()
    {
        var actor = Guid.NewGuid(); var project = Guid.NewGuid();
        var query = new AuditLogQuery(Org, project, actor, "approval", Since, Until, 5);
        var entry = Entry("90071992-5474-4099-8300-000000000001");
        var cursor = query.Cursor(entry);
        var resumed = new AuditLogQuery(Org, project, actor, "approval", Since, Until, 100, cursor);
        Assert.Equal(entry.EventId, resumed.Position!.Id);
        Assert.Equal(entry.OccurredAtUtc, resumed.Position.At);
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, project, actor, "build", Since, Until, cursor: cursor));
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, null, actor, "approval", Since, Until, cursor: cursor));
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, project, Guid.NewGuid(), "approval", Since, Until, cursor: cursor));
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, project, actor, "approval", Since.AddMinutes(-1), Until, cursor: cursor));
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Guid.NewGuid(), project, actor, "approval", Since, Until, cursor: cursor));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Page_size_is_bounded(int limit) => Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, null, null, null, Since, Until, limit));

    [Fact]
    public void Query_rejects_empty_identity_unknown_kind_and_invalid_interval()
    {
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Guid.Empty, null, null, null, Since, Until));
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, Guid.Empty, null, null, Since, Until));
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, null, Guid.Empty, null, Since, Until));
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, null, null, "payloads", Since, Until));
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, null, null, null, Until, Since));
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, null, null, null, Since.ToOffset(TimeSpan.FromHours(1)), Until));
    }

    [Theory]
    [InlineData("not a cursor")]
    [InlineData("e30")]
    [InlineData("AAAA")]
    public void Malformed_cursors_are_not_interpreted_as_first_page(string value) =>
        Assert.Throws<ArgumentException>(() => new AuditLogQuery(Org, null, null, null, Since, Until, cursor: value));

    [Fact]
    public void Csv_is_quoted_formula_safe_and_preserves_unknown_actors()
    {
        var entry = Entry("90071992-5474-4099-8300-000000000001") with { Action = "=formula,\"quoted\"", ActorId = null, ActorKind = "unknown", HistoricalObservation = true };
        var csv = AuditLogCsv.Write(new([entry], null, Since, Until, true));
        Assert.StartsWith(AuditLogCsv.Header, csv, StringComparison.Ordinal);
        Assert.Contains("\"'=formula,\"\"quoted\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"unknown\",\"\"", csv, StringComparison.Ordinal);
        Assert.EndsWith("\"true\"\r\n", csv, StringComparison.Ordinal);
        Assert.Equal(csv, new UTF8Encoding(false, true).GetString(Encoding.UTF8.GetBytes(csv)));
    }

    private static AuditLogEntryV1 Entry(string id) => new(Guid.Parse(id), Since.AddHours(1), "approval", "approved", "user",
        Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), "approved", false);
}
