using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Catalog;

namespace Braid.Tests;

public sealed class CatalogPreviewOpenApiTests
{
    [Fact]
    public async Task GeneratedPreviewContractKeepsLegacyOptionalityAndListsExactDocumentProfilesAndMedia()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddBraidApi();
        builder.Services.AddBraidCatalog(new CatalogApiOptions(), builder.Environment);
        await using var app = builder.Build();
        app.MapBraidCatalog();
        app.MapOpenApi();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("CatalogPreviewSubmissionV1");
        Assert.Equal(new[] { "target", "parameters", "inputs" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new string?[] { PreviewPolicy.Profile, DocumentPreviewPolicy.Profile, CsvDocumentPolicy.Profile, XmlPreviewAdmission.Profile, null },
            schema.GetProperty("properties").GetProperty("profile").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        var output = document.RootElement.GetProperty("paths").EnumerateObject().Single(path => path.Name.EndsWith("/previews/{runId}/outputs/{ordinal}", StringComparison.Ordinal)).Value;
        Assert.Equal(new[] { "application/edi-x12", "application/edifact", "application/json", "application/xml", "text/csv", "text/tab-separated-values" },
            output.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").EnumerateObject()
                .Select(item => item.Name).Order(StringComparer.Ordinal));
        var success = output.GetProperty("get").GetProperty("responses").GetProperty("200");
        foreach (var media in success.GetProperty("content").EnumerateObject())
            Assert.Equal("binary", media.Value.GetProperty("schema").GetProperty("format").GetString());
        Assert.Equal(new[] { "Cache-Control", "Content-Digest", "Content-Disposition", "X-Content-Type-Options" },
            success.GetProperty("headers").EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        var submit = document.RootElement.GetProperty("paths").EnumerateObject().Single(path => path.Name.EndsWith("/revisions/{revisionId}/previews", StringComparison.Ordinal)).Value;
        Assert.Equal("#/components/schemas/CatalogXmlPreviewProblemV1", submit.GetProperty("post").GetProperty("responses").GetProperty("422")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var problem = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("CatalogXmlPreviewProblemV1");
        Assert.False(problem.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(CatalogXmlPreviewProblemV1.Reasons, problem.GetProperty("properties").GetProperty("reason").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        foreach (var name in new[] { "observed", "limit" })
            Assert.Equal(new[] { "null", "string" }, problem.GetProperty("properties").GetProperty(name).GetProperty("type").EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Xml_problem_mapping_is_closed_sanitized_and_keeps_exact_Int64_facts()
    {
        foreach (var reason in Enum.GetValues<XmlDocumentFailure>())
        {
            var result = CatalogXmlPreviewProblem.From(new(reason, new(2, 3), new(100, 4, 2, 0), long.MaxValue, 32768));
            Assert.True(result.IsSafe());
            Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Observed);
            Assert.Contains(result.Reason, CatalogXmlPreviewProblemV1.Reasons);
        }
        var unknown = CatalogXmlPreviewProblem.From(new((XmlDocumentFailure)int.MaxValue, statistics: new(-1, 0, 0, 0), observed: -1, limit: -2));
        Assert.Equal("invalidXml", unknown.Reason);
        Assert.Equal("invalidInput", unknown.Kind);
        Assert.Null(unknown.Location); Assert.Null(unknown.Statistics); Assert.Null(unknown.Observed); Assert.Null(unknown.Limit);
        Assert.True(unknown.IsSafe());
    }
}
