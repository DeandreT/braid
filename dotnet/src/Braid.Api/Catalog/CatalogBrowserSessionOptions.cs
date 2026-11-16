using System.Text.Json.Serialization;

namespace Braid.Api.Catalog;

/// <summary>One explicit catalog organization; session identity and transport belong to the browser-session module.</summary>
public sealed class CatalogBrowserSessionOptions
{
    public CatalogBrowserSessionOptions(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("The configured catalog organization cannot be empty.", nameof(organizationId));
        OrganizationId = organizationId;
    }

    [JsonIgnore]
    public Guid OrganizationId { get; }

    public override string ToString() => nameof(CatalogBrowserSessionOptions);
}
