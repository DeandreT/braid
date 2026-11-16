namespace Braid.Contracts.Catalog;

/// <summary>Fresh organization-level authority, separate from project roles and feature support.</summary>
public sealed record CatalogOrganizationActionsV1
{
    public CatalogOrganizationActionsV1(bool createProjects)
    {
        CreateProjects = createProjects;
    }

    public bool CreateProjects { get; }

    public static CatalogOrganizationActionsV1 None { get; } = new(false);
}
