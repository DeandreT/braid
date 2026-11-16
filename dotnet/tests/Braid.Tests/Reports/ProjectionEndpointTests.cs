using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Braid.Api;
using Braid.Contracts.Projections;

namespace Braid.Tests;

public sealed class ProjectionEndpointTests
{
    [Theory]
    [InlineData("direct", CoverageStatus.Complete)]
    [InlineData("rich", CoverageStatus.Complete)]
    [InlineData("partial", CoverageStatus.Partial)]
    [InlineData("unavailable", CoverageStatus.Unavailable)]
    public async Task DevelopmentFixtureUsesThePublishedJsonVocabulary(string id, CoverageStatus coverage)
    {
        await using var app = await CreateApp("Development");
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/v1/fixtures/projections/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var projection = await response.Content.ReadFromJsonAsync<ProjectionDocumentV1>(ProjectionJson.CreateOptions());
        Assert.NotNull(projection);
        Assert.Equal("braid.mapping-projection.v1", projection.SchemaVersion);
        Assert.Equal("braid.synthetic", projection.Provenance.Backend.Id);
        Assert.Equal(coverage, projection.Coverage.Status);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"status\":\"{coverage.ToString().ToLowerInvariant()}\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\":0", raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development", "missing")]
    [InlineData("Production", "rich")]
    [InlineData("Staging", "direct")]
    public async Task UnknownOrNonDevelopmentFixturesReturnAnEmpty404(string environment, string id)
    {
        await using var app = await CreateApp(environment);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/v1/fixtures/projections/{id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApiDescribesTypedFixtureResponsesAndDiscriminatedUnions()
    {
        await using var app = await CreateApp("Development");
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var schema = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = schema.RootElement;
        Assert.StartsWith("3.1.", root.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        var fixtureResponses = root.GetProperty("paths").GetProperty("/api/v1/fixtures/projections/{fixtureId}")
            .GetProperty("get").GetProperty("responses");
        Assert.Equal("#/components/schemas/ProjectionDocumentV1", fixtureResponses.GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.False(fixtureResponses.GetProperty("404").TryGetProperty("content", out _));
        var schemas = root.GetProperty("components").GetProperty("schemas");
        foreach (var type in new[] { "PathSegment", "LiteralValue", "NativeLocator", "RuleDependency", "DetailNode", "ProjectionNodeReference" })
        {
            var union = schemas.GetProperty(type);
            Assert.Equal("kind", union.GetProperty("discriminator").GetProperty("propertyName").GetString());
            Assert.True(union.GetProperty("discriminator").GetProperty("mapping").EnumerateObject().Count() > 1);
            foreach (var variant in union.GetProperty("discriminator").GetProperty("mapping").EnumerateObject())
            {
                var component = variant.Value.GetString()!["#/components/schemas/".Length..];
                var subtype = schemas.GetProperty(component);
                Assert.Contains(subtype.GetProperty("required").EnumerateArray(), item => item.GetString() == "kind");
                Assert.Equal(variant.Name, Assert.Single(subtype.GetProperty("properties").GetProperty("kind")
                    .GetProperty("enum").EnumerateArray()).GetString());
            }
        }

        var projectionSchema = schemas.GetProperty("ProjectionDocumentV1");
        Assert.Contains(projectionSchema.GetProperty("required").EnumerateArray(), item => item.GetString() == "schemaVersion");
        var versionSchema = projectionSchema.GetProperty("properties").GetProperty("schemaVersion");
        Assert.Equal("string", versionSchema.GetProperty("type").GetString());
        Assert.True(versionSchema.GetProperty("readOnly").GetBoolean());
        Assert.Equal("braid.mapping-projection.v1", Assert.Single(versionSchema.GetProperty("enum").EnumerateArray()).GetString());
    }

    private static async Task<WebApplication> CreateApp(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddBraidApi();
        var app = builder.Build();
        app.MapBraidHealth();
        app.MapBraidProjectionFixtures();
        if (environment == "Development")
        {
            app.MapOpenApi();
        }
        await app.StartAsync();
        return app;
    }
}
