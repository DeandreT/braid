namespace Weft.Contracts.Catalog;

public enum CatalogSearchKindV1 { Project, Mapping, Revision, Field, Rule }
public sealed record CatalogSearchHighlightV1(int Start, int Length);
public sealed record CatalogSearchTextV1(string Text, IReadOnlyList<CatalogSearchHighlightV1> Highlights);
public sealed record CatalogSearchHitV1(CatalogSearchKindV1 Kind, Guid OrganizationId, Guid ProjectId,
    Guid? MappingId, Guid? RevisionId, Guid? ProjectionDerivationId, string? ResourceId,
    CatalogSearchTextV1 Title, CatalogSearchTextV1 Description);
public sealed record CatalogSearchPageV1(string Query, IReadOnlyList<CatalogSearchHitV1> Hits, string? NextCursor);
