namespace Weft.Api.Catalog;

/// <summary>Explicit capability for conditional mapping metadata edits; read-only catalog remains the default.</summary>
public sealed record CatalogMetadataEditOptions
{
    public CatalogMetadataEditOptions(bool enabled = false) => Enabled = enabled;

    public bool Enabled { get; }
}
