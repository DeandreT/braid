namespace Weft.Domain;

public sealed record MappingScope
{
    public MappingScope(Guid organizationId, Guid projectId, Guid mappingId)
    {
        OrganizationId = IngestionValue.RequireId(organizationId, nameof(organizationId));
        ProjectId = IngestionValue.RequireId(projectId, nameof(projectId));
        MappingId = IngestionValue.RequireId(mappingId, nameof(mappingId));
    }

    public Guid OrganizationId { get; }
    public Guid ProjectId { get; }
    public Guid MappingId { get; }
}
