namespace Braid.Contracts.Catalog;

public sealed record CatalogProjectScopeV1
{
    public CatalogProjectScopeV1(string organizationId, string projectId)
    {
        OrganizationId = CatalogValueV1.RequireId(organizationId, nameof(organizationId));
        ProjectId = CatalogValueV1.RequireId(projectId, nameof(projectId));
    }

    public string OrganizationId { get; }
    public string ProjectId { get; }
}

public sealed record CatalogMappingScopeV1
{
    public CatalogMappingScopeV1(string organizationId, string projectId, string mappingId)
    {
        OrganizationId = CatalogValueV1.RequireId(organizationId, nameof(organizationId));
        ProjectId = CatalogValueV1.RequireId(projectId, nameof(projectId));
        MappingId = CatalogValueV1.RequireId(mappingId, nameof(mappingId));
    }

    public string OrganizationId { get; }
    public string ProjectId { get; }
    public string MappingId { get; }
}

public sealed record CatalogRevisionScopeV1
{
    public CatalogRevisionScopeV1(string organizationId, string projectId, string mappingId, string revisionId)
    {
        OrganizationId = CatalogValueV1.RequireId(organizationId, nameof(organizationId));
        ProjectId = CatalogValueV1.RequireId(projectId, nameof(projectId));
        MappingId = CatalogValueV1.RequireId(mappingId, nameof(mappingId));
        RevisionId = CatalogValueV1.RequireId(revisionId, nameof(revisionId));
    }

    public string OrganizationId { get; }
    public string ProjectId { get; }
    public string MappingId { get; }
    public string RevisionId { get; }
}
