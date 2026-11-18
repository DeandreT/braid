using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Braid.Contracts.Administration;
using Braid.Domain;

namespace Braid.Application;

public interface IAuditLogReader
{
    Task<AuditLogPageV1?> ReadAuditAsync(ActorContext actor, Guid organizationId, AuditLogQuery query, CancellationToken token);
}

public sealed record AuditLogPosition(DateTimeOffset At, Guid Id);

public sealed class AuditLogQuery
{
    public static IReadOnlyList<string> Kinds { get; } = Array.AsReadOnly(new[]
        { "administration", "release", "build", "approval", "request", "payload", "grant", "invocation" });

    public AuditLogQuery(Guid organizationId, Guid? projectId, Guid? actorId, string? kind,
        DateTimeOffset since, DateTimeOffset until, int limit = 50, string? cursor = null)
    {
        if (organizationId == Guid.Empty || projectId == Guid.Empty || actorId == Guid.Empty || limit is < 1 or > 100 ||
            since.Offset != TimeSpan.Zero || until.Offset != TimeSpan.Zero || since >= until || kind is not null && !Kinds.Contains(kind))
            throw new ArgumentException("Invalid audit query.");
        OrganizationId = organizationId; ProjectId = projectId; ActorId = actorId; Kind = kind;
        Since = Microseconds(since); Until = Microseconds(until); Limit = limit;
        if (Since >= Until) throw new ArgumentException("Invalid audit interval.");
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { organizationId, projectId, actorId, kind, since = Since, until = Until }))));
        if (cursor is not null) Position = Decode(cursor);
    }

    public Guid OrganizationId { get; }
    public Guid? ProjectId { get; }
    public Guid? ActorId { get; }
    public string? Kind { get; }
    public DateTimeOffset Since { get; }
    public DateTimeOffset Until { get; }
    public int Limit { get; }
    public AuditLogPosition? Position { get; }
    private string Fingerprint { get; }

    public string Cursor(AuditLogEntryV1 last)
    {
        var id = last.EventId;
        if (id == Guid.Empty)
            throw new InvalidOperationException("Invalid stored audit identity.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new CursorValue(1, Fingerprint, Microseconds(last.OccurredAtUtc), id));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private AuditLogPosition Decode(string text)
    {
        if (text.Length is < 1 or > 1024 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid audit cursor.");
        try
        {
            var raw = text.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(raw.PadRight((raw.Length + 3) / 4 * 4, '='));
            if (Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != text)
                throw new ArgumentException("Invalid audit cursor.");
            var value = JsonSerializer.Deserialize<CursorValue>(bytes, new JsonSerializerOptions
            {
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                AllowDuplicateProperties = false,
                RespectRequiredConstructorParameters = true,
                MaxDepth = 2
            });
            if (value is null || value.Version != 1 || value.Fingerprint != Fingerprint || value.Id == Guid.Empty ||
                value.At.Offset != TimeSpan.Zero || value.At < Since || value.At >= Until || value.At != Microseconds(value.At))
                throw new ArgumentException("Invalid audit cursor.");
            return new(value.At, value.Id);
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw new ArgumentException("Invalid audit cursor."); }
    }

    private sealed record CursorValue(int Version, string Fingerprint, DateTimeOffset At, Guid Id);
    public static DateTimeOffset Microseconds(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}

public static class AuditLogCsv
{
    public const string Header = AuditLogFormat.CsvHeader;
    public static string Write(AuditLogPageV1 page)
    {
        var output = new StringBuilder(Header);
        foreach (var entry in page.Entries)
        {
            string[] values = [entry.EventId.ToString("D"), entry.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture), entry.Kind, entry.Action,
                entry.ActorKind, entry.ActorId?.ToString("D") ?? "", entry.ProjectId?.ToString("D") ?? "", entry.MappingId?.ToString("D") ?? "",
                entry.ResourceId.ToString("D"), entry.Outcome, entry.HistoricalObservation ? "true" : "false"];
            output.AppendJoin(',', values.Select(Escape)); output.Append("\r\n");
        }
        return output.ToString();
    }

    private static string Escape(string value)
    {
        // CSV may be opened in a spreadsheet. Never turn an event value into a formula.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
