namespace Braid.Contracts.Operations;

public sealed record PilotRouteFormV1(string EnvironmentId, string EnvironmentVersion, string? SourceBinding, string? Source,
    string? SourceVersion, string? DestinationBinding, string? Destination, string? DestinationVersion, string? Tenant, string? Principal,
    string? Application, string? Cron, string? EffectiveUtc, string? TzVersion, string? ReferenceName, string? ReferenceJson, string? DestinationKind = null, string? InputMediaType = null, string? Envelope = null);
