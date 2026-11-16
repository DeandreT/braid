namespace Braid.Jobs;

/// <summary>Durable command to execute one stored preview run. It carries identifiers only.</summary>
public sealed record RunPreview(Guid OrganizationId, Guid RunId);
