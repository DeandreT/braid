using System.Text.Json.Serialization;

namespace Weft.Contracts.Catalog;

/// <summary>Creation input retaining the original name until application acceptance trims it once.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogProjectCreateV1
{
    public CatalogProjectCreateV1(string name, string description)
    {
        ArgumentNullException.ThrowIfNull(name);
        _ = CatalogValueV1.RequireName(name.Trim(), nameof(name));
        Name = name;
        Description = CatalogValueV1.RequireDescription(description);
    }

    public string Name { get; }
    public string Description { get; }
}
