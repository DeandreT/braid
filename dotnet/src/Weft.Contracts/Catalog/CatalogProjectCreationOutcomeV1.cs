using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weft.Contracts.Catalog;

[JsonConverter(typeof(CatalogProjectCreationOutcomeConverterV1))]
public enum CatalogProjectCreationOutcomeV1
{
    Created = 1,
    Replayed = 2
}

internal sealed class CatalogProjectCreationOutcomeConverterV1 : JsonStringEnumConverter<CatalogProjectCreationOutcomeV1>
{
    public CatalogProjectCreationOutcomeConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}
