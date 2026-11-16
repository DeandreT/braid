using System.Text.Json.Nodes;

namespace Braid.Tests;

public sealed class CatalogInterchangeOpenApiTests
{
    [Fact]
    public async Task GeneratedInterchangeOperationsMatchCommittedSpecificationAndStrictBodies()
    {
        await using var host = await CatalogHttpTestSupport.StartAsync();
        var generated = JsonNode.Parse(await host.Client.GetStringAsync("/openapi/v1.json"))!;
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Contracts", "braid-v1.json")))!;
        var paths = generated["paths"]!.AsObject().Where(path => path.Key.Contains("mfd-", StringComparison.Ordinal) || path.Key == "/api/v1/catalog/interchange").ToArray();
        Assert.Equal(7, paths.Length);
        foreach (var (path, operations) in paths)
        {
            Assert.True(JsonNode.DeepEquals(operations, committed["paths"]![path]), $"Specification drift at {path}");
            foreach (var operation in operations!.AsObject())
            {
                var responses = operation.Value!["responses"]!;
                Assert.NotNull(responses["404"]);
                Assert.NotNull(responses["503"]);
            }
        }
        var confirm = paths.Single(path => path.Key.EndsWith("/confirm", StringComparison.Ordinal)).Value!["post"]!;
        var schema = confirm["requestBody"]!["content"]!["application/json"]!["schema"]!;
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("^[0-9a-f]{64}$", schema["properties"]!["reviewDigest"]!["pattern"]!.GetValue<string>());
        var upload = paths.Single(path => path.Key.EndsWith("/mfd-imports/review", StringComparison.Ordinal) && !path.Key.Contains("{mappingId}", StringComparison.Ordinal)).Value!["post"]!;
        Assert.NotNull(upload["requestBody"]!["content"]!["application/vnd.braid.mfd-package.v1"]);
        var download = paths.Single(path => path.Key.EndsWith("/download", StringComparison.Ordinal)).Value!["post"]!;
        Assert.Equal("binary", download["responses"]!["200"]!["content"]!["application/zip"]!["schema"]!["format"]!.GetValue<string>());
    }
}
