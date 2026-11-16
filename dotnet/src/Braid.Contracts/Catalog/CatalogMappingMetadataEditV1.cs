using System.Text.Json.Serialization;

namespace Braid.Contracts.Catalog;

/// <summary>
/// The metadata edit document preserves its input text. The accepted name is trimmed before
/// applying the persisted Unicode limit; strict document parsing and body limits belong to the API.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogMappingMetadataEditV1
{
    public CatalogMappingMetadataEditV1(string name, string description)
    {
        ArgumentNullException.ThrowIfNull(name);
        _ = CatalogValueV1.RequireName(name.Trim(), nameof(name));
        Name = name;
        Description = CatalogValueV1.RequireDescription(description);
    }

    public string Name { get; }
    public string Description { get; }
}
