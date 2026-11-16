using System.Text.Json.Serialization;

namespace Braid.Contracts.Catalog;

public sealed record CatalogProjectPageV1
{
    public CatalogProjectPageV1(IEnumerable<CatalogProjectV1> projects, string? nextCursor = null)
        : this(CatalogValueV1.OwnPage(projects, nameof(projects)), nextCursor) { }

    [JsonConstructor]
    public CatalogProjectPageV1(IReadOnlyList<CatalogProjectV1> projects, string? nextCursor = null)
    {
        Projects = CatalogValueV1.OwnPage(projects, nameof(projects));
        if (Projects.Any(project => project.Scope.OrganizationId != Projects[0].Scope.OrganizationId))
            throw new ArgumentException("A project page must preserve its organization ancestry.", nameof(projects));
        NextCursor = CatalogValueV1.RequireCursor(nextCursor);
    }

    public IReadOnlyList<CatalogProjectV1> Projects { get; }
    public string? NextCursor { get; }
}

public sealed record CatalogMappingPageV1
{
    public CatalogMappingPageV1(IEnumerable<CatalogMappingV1> mappings, string? nextCursor = null)
        : this(CatalogValueV1.OwnPage(mappings, nameof(mappings)), nextCursor) { }

    [JsonConstructor]
    public CatalogMappingPageV1(IReadOnlyList<CatalogMappingV1> mappings, string? nextCursor = null)
    {
        Mappings = CatalogValueV1.OwnPage(mappings, nameof(mappings));
        if (Mappings.Any(mapping => mapping.Scope.OrganizationId != Mappings[0].Scope.OrganizationId ||
            mapping.Scope.ProjectId != Mappings[0].Scope.ProjectId))
            throw new ArgumentException("A mapping page must preserve its project ancestry.", nameof(mappings));
        NextCursor = CatalogValueV1.RequireCursor(nextCursor);
    }

    public IReadOnlyList<CatalogMappingV1> Mappings { get; }
    public string? NextCursor { get; }
}

public sealed record CatalogRevisionPageV1
{
    public CatalogRevisionPageV1(IEnumerable<CatalogRevisionV1> revisions, string? nextCursor = null)
        : this(CatalogValueV1.OwnPage(revisions, nameof(revisions)), nextCursor) { }

    [JsonConstructor]
    public CatalogRevisionPageV1(IReadOnlyList<CatalogRevisionV1> revisions, string? nextCursor = null)
    {
        Revisions = CatalogValueV1.OwnPage(revisions, nameof(revisions));
        if (Revisions.Any(revision => revision.Scope.OrganizationId != Revisions[0].Scope.OrganizationId ||
            revision.Scope.ProjectId != Revisions[0].Scope.ProjectId || revision.Scope.MappingId != Revisions[0].Scope.MappingId))
            throw new ArgumentException("A revision page must preserve its mapping ancestry.", nameof(revisions));
        NextCursor = CatalogValueV1.RequireCursor(nextCursor);
    }

    public IReadOnlyList<CatalogRevisionV1> Revisions { get; }
    public string? NextCursor { get; }
}
