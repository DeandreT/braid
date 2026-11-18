using System.Text.Json;
using NpgsqlTypes;
using Braid.Application;
using Braid.Contracts.Administration;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresAdministrationStore
{
    public async Task<AuditLogPageV1?> ReadAuditAsync(ActorContext actor, Guid organizationId, AuditLogQuery query, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(query);
        if (organizationId != query.OrganizationId) throw new ArgumentException("Audit scope differs.");
        await using var tx = await BeginAsync(organizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.read_audit_log(@org,@actor,@project,@filterActor,@kind,@since,@until,@beforeAt,@beforeId,@limit)::text");
        Scope(command, organizationId, actor.PrincipalId.Value, query.ProjectId);
        command.Parameters.AddWithValue("filterActor", NpgsqlDbType.Uuid, (object?)query.ActorId ?? DBNull.Value);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Text, (object?)query.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue("since", query.Since); command.Parameters.AddWithValue("until", query.Until);
        command.Parameters.AddWithValue("beforeAt", NpgsqlDbType.TimestampTz, (object?)query.Position?.At ?? DBNull.Value);
        command.Parameters.AddWithValue("beforeId", NpgsqlDbType.Uuid, (object?)query.Position?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", query.Limit);
        var value = await command.ExecuteScalarAsync(token);
        if (value is not string text) return null;
        if (text.Length > 512 * 1024) throw new InvalidOperationException("Audit page exceeded its bound.");
        var page = JsonSerializer.Deserialize<StoredAuditPage>(text, ProjectionJson.CreateOptions()) ?? throw Invalid();
        if (page.Entries.Count > query.Limit || page.HasMore && page.Entries.Count != query.Limit) throw Invalid();
        AuditLogEntryV1? previous = null;
        foreach (var entry in page.Entries)
        {
            if (entry.EventId == Guid.Empty || !AuditLogQuery.Kinds.Contains(entry.Kind) ||
                entry.ResourceId == Guid.Empty || entry.ActorKind is not ("user" or "machine" or "system" or "unknown") ||
                entry.OccurredAtUtc < query.Since || entry.OccurredAtUtc >= query.Until ||
                query.ProjectId is { } project && project != entry.ProjectId || query.ActorId is { } filter && filter != entry.ActorId ||
                query.Kind is { } kind && kind != entry.Kind ||
                previous is not null && (previous.OccurredAtUtc < entry.OccurredAtUtc || previous.OccurredAtUtc == entry.OccurredAtUtc &&
                    string.CompareOrdinal(previous.EventId.ToString("D"), entry.EventId.ToString("D")) <= 0)) throw Invalid();
            previous = entry;
        }
        await tx.CommitAsync(token);
        return new(page.Entries, page.HasMore ? query.Cursor(page.Entries[^1]) : null, query.Since, query.Until, page.OrganizationWide);
    }

    private sealed record StoredAuditPage(IReadOnlyList<AuditLogEntryV1> Entries, bool HasMore, bool OrganizationWide);
    private static InvalidOperationException Invalid() => new("Stored audit page is invalid.");
}
