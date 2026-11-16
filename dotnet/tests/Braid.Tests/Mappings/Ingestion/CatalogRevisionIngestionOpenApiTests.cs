using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;

namespace Braid.Tests;

/// <summary>Actual default-off ASP.NET OpenAPI generation; no persistence, storage or native execution.</summary>
public sealed class CatalogRevisionIngestionOpenApiTests
{
    [Fact]
    public async Task GeneratedIngestionSchemasPreserveNullableValuesAndExistingRequiredProperties()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var receipt = schemas.GetProperty("CatalogRevisionIngestionReceiptV1");
        Required(receipt, ["operationId", "project", "futureMapping", "futureRevision", "phase", "claimCount",
            "reservedAtUtc", "completedAtUtc", "originalFilename", "committedSequence", "committedAdmission", "latestFailure"]);
        NullableReference(receipt.GetProperty("properties").GetProperty("committedAdmission"), "CatalogAdmissionV1");

        var result = schemas.GetProperty("CatalogRevisionIngestionResultV1");
        Required(result, ["outcome"]);
        NullableReference(result.GetProperty("properties").GetProperty("receipt"), "CatalogRevisionIngestionReceiptV1");
        Assert.Equal("object", receipt.GetProperty("type").GetString());
        Assert.False(receipt.TryGetProperty("oneOf", out _));
    }

    [Fact]
    public async Task GeneratedIngestionNullabilityDoesNotWidenSharedAdmissionOrDuplicateNullableEnums()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.Equal(new[] { "valid", "decodedInvalid" }, schemas.GetProperty("CatalogAdmissionV1")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()!));
        foreach (var name in new[] { "CatalogHeadV1", "CatalogRevisionV1" })
            Reference(schemas.GetProperty(name).GetProperty("properties").GetProperty("admission"), "CatalogAdmissionV1");

        var receiptProperties = schemas.GetProperty("CatalogRevisionIngestionReceiptV1").GetProperty("properties");
        var resultProperties = schemas.GetProperty("CatalogRevisionIngestionResultV1").GetProperty("properties");
        Reference(receiptProperties.GetProperty("latestFailure"), "CatalogRevisionIngestionFailureV1");
        Reference(resultProperties.GetProperty("failure"), "CatalogRevisionIngestionFailureV1");
        Reference(resultProperties.GetProperty("rejection"), "CatalogRevisionIngestionRejectionV1");
        foreach (var name in new[] { "CatalogRevisionIngestionFailureV1", "CatalogRevisionIngestionRejectionV1" })
        {
            var values = schemas.GetProperty(name).GetProperty("enum").EnumerateArray().ToArray();
            Assert.Single(values, value => value.ValueKind == JsonValueKind.Null);
            Assert.All(values.Where(value => value.ValueKind != JsonValueKind.Null),
                value => Assert.Equal(JsonValueKind.String, value.ValueKind));
        }
    }

    private static void Required(JsonElement schema, string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), schema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()!).Order(StringComparer.Ordinal));

    private static void NullableReference(JsonElement schema, string name)
    {
        Assert.Equal(new[] { "oneOf" }, schema.EnumerateObject().Select(property => property.Name));
        var variants = schema.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(2, variants.Length);
        var empty = Assert.Single(variants, value => value.TryGetProperty("type", out var type) && type.GetString() == "null");
        Assert.Equal(new[] { "type" }, empty.EnumerateObject().Select(property => property.Name));
        var reference = Assert.Single(variants, value => value.TryGetProperty("$ref", out _));
        Reference(reference, name);
    }

    private static void Reference(JsonElement schema, string name)
    {
        Assert.Equal(new[] { "$ref" }, schema.EnumerateObject().Select(property => property.Name));
        Assert.Equal("#/components/schemas/" + name, schema.GetProperty("$ref").GetString());
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddBraidApi();
        builder.Services.AddBraidCatalog(new CatalogApiOptions(), builder.Environment);
        var app = builder.Build();
        app.MapBraidCatalog();
        app.MapOpenApi();
        await app.StartAsync();
        return app;
    }
}
