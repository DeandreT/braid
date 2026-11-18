namespace Braid.Contracts.Administration;

public sealed record AuditLogEntryV1(Guid EventId, DateTimeOffset OccurredAtUtc, string Kind, string Action,
    string ActorKind, Guid? ActorId, Guid? ProjectId, Guid? MappingId, Guid ResourceId, string Outcome,
    bool HistoricalObservation);

public sealed record AuditLogPageV1(IReadOnlyList<AuditLogEntryV1> Entries, string? NextCursor,
    DateTimeOffset Since, DateTimeOffset Until, bool OrganizationWide);

public static class AuditLogFormat
{
    public const string CsvHeader = "eventId,occurredAtUtc,kind,action,actorKind,actorId,projectId,mappingId,resourceId,outcome,historicalObservation\r\n";
}
