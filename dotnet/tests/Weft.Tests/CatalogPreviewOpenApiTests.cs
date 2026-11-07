using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Application;

namespace Weft.Tests;

public sealed class CatalogPreviewOpenApiTests
{
    [Fact]
    public async Task GeneratedPreviewContractKeepsLegacyOptionalityAndListsExactDocumentProfilesAndMedia()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddWeftApi();
        builder.Services.AddWeftCatalog(new CatalogApiOptions(), builder.Environment);
        await using var app = builder.Build();
        app.MapWeftCatalog();
        app.MapOpenApi();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("CatalogPreviewSubmissionV1");
        Assert.Equal(new[] { "target", "parameters", "inputs" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new string?[] { PreviewPolicy.Profile, DocumentPreviewPolicy.Profile, null },
            schema.GetProperty("properties").GetProperty("profile").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        var output = document.RootElement.GetProperty("paths").EnumerateObject().Single(path => path.Name.EndsWith("/previews/{runId}/outputs/{ordinal}", StringComparison.Ordinal)).Value;
        Assert.Equal(new[] { "application/edi-x12", "application/edifact", "application/json" },
            output.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").EnumerateObject()
                .Select(item => item.Name).Order(StringComparer.Ordinal));
        var success = output.GetProperty("get").GetProperty("responses").GetProperty("200");
        foreach (var media in success.GetProperty("content").EnumerateObject())
            Assert.Equal("binary", media.Value.GetProperty("schema").GetProperty("format").GetString());
        Assert.Equal(new[] { "Cache-Control", "Content-Digest", "Content-Disposition", "X-Content-Type-Options" },
            success.GetProperty("headers").EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
    }
}
