using System.Text.Json;

namespace Weft.Tests;

public sealed class CatalogSearchOpenApiTests
{
    [Fact]
    public async Task SearchDocumentsItsRequiredLiteralQueryBoundedCursorAndExactResourceContract()
    {
        await using var host = await CatalogHttpTestSupport.StartAsync();
        using var response = await host.Client.GetAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty("/api/v1/organizations/{organizationId}/search").GetProperty("get");
        Assert.Equal("SearchCatalog", operation.GetProperty("operationId").GetString());
        var query = operation.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "q");
        Assert.True(query.GetProperty("required").GetBoolean());
        Assert.Equal(256, query.GetProperty("schema").GetProperty("maxLength").GetInt32());
        var cursor = operation.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "cursor");
        Assert.Equal(4096, cursor.GetProperty("schema").GetProperty("maxLength").GetInt32());
        Assert.True(operation.GetProperty("responses").GetProperty("503").GetProperty("headers").TryGetProperty("Retry-After", out _));
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var hit = schemas.GetProperty("CatalogSearchHitV1").GetProperty("properties");
        Assert.True(hit.TryGetProperty("projectionDerivationId", out _));
        Assert.True(hit.TryGetProperty("resourceId", out _));
        var text = schemas.GetProperty("CatalogSearchTextV1").GetProperty("properties");
        Assert.Equal(new[] { "highlights", "text" }, text.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }
}
