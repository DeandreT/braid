namespace Weft.Domain;

public sealed record RevisionScope
{
    public RevisionScope(Guid organizationId, Guid projectId, Guid mappingId, Guid revisionId)
    {
        OrganizationId = RequireId(organizationId, nameof(organizationId));
        ProjectId = RequireId(projectId, nameof(projectId));
        MappingId = RequireId(mappingId, nameof(mappingId));
        RevisionId = RequireId(revisionId, nameof(revisionId));
    }

    public Guid OrganizationId { get; }
    public Guid ProjectId { get; }
    public Guid MappingId { get; }
    public Guid RevisionId { get; }

    private static Guid RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A revision scope identifier cannot be empty.", parameterName);

        return value;
    }
}
