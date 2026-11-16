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

/// <summary>Actual ASP.NET OpenAPI generation; no PostgreSQL, worker or report-workflow claim.</summary>
public sealed class CatalogRevisionReportOpenApiTests
{
    private const string RevisionRoute = "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}";

    [Fact]
    public async Task GeneratedReportSchemasPreserveLiteralsRequiredNullablesAndExactIdentitySemantics()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var operation in new[] { "projection", "validation" })
        {
            var name = operation == "projection" ? "CatalogProjectionReportV1" : "CatalogValidationReportV1";
            var reportName = operation == "projection" ? "ProjectionReportV1" : "ValidationReportV1";
            var wrapper = schemas.GetProperty(name);
            string[] required = ["schemaVersion", "revision", "operation", "state", "readOutcome", "status", "result",
                "currentAttempt", "nextAttemptAtUtc", "failure", "report"];
            if (operation == "validation")
                required = [.. required, "projectionDerivationId"];
            Required(wrapper, required);
            Assert.False(wrapper.GetProperty("additionalProperties").GetBoolean());
            var properties = wrapper.GetProperty("properties");
            Literal(properties.GetProperty("schemaVersion"), "braid.catalog-revision-report.v1");
            Literal(properties.GetProperty("operation"), operation);
            NullableReference(properties.GetProperty("status"), "OperationStatusV1");
            NullableReference(properties.GetProperty("result"), "CatalogRevisionReportResultV1");
            NullableReference(properties.GetProperty("currentAttempt"), "CatalogRevisionReportAttemptV1");
            NullableReference(properties.GetProperty("failure"), "CatalogRevisionReportFailureV1");
            NullableReference(properties.GetProperty("report"), reportName);
            NullableString(properties.GetProperty("nextAttemptAtUtc"), "date-time");
            if (operation == "validation")
                NullableString(properties.GetProperty("projectionDerivationId"), "uuid");
        }
        var result = schemas.GetProperty("CatalogRevisionReportResultV1");
        Required(result, ["derivationId", "generation", "startedAtUtc", "finishedAtUtc"]);
        Identity(result);
        var attempt = schemas.GetProperty("CatalogRevisionReportAttemptV1");
        Required(attempt, ["derivationId", "generation", "startedAtUtc", "finishedAtUtc", "status", "failure"]);
        Identity(attempt);
        var attemptProperties = attempt.GetProperty("properties");
        NullableString(attemptProperties.GetProperty("finishedAtUtc"), "date-time");
        NullableReference(attemptProperties.GetProperty("status"), "OperationStatusV1");
        NullableReference(attemptProperties.GetProperty("failure"), "CatalogRevisionReportFailureV1");
        ClosedStrings(schemas.GetProperty("CatalogRevisionReportStateV1"), ["notDerived", "pending", "available", "failed"]);
        ClosedStrings(schemas.GetProperty("CatalogRevisionReportReadOutcomeV1"),
            ["observed", "objectUnavailable", "storageIntegrity", "unsupportedFormat", "resourceLimit"]);
        ClosedStrings(schemas.GetProperty("CatalogRevisionReportFailureV1"),
            ["objectUnavailable", "storageIntegrity", "storageIo", "backendUnavailable", "backendFailure", "timedOut",
                "canceled", "isolationFailure", "protocolFailure", "profileUnavailable", "resourceLimit", "unsupportedArtifact"]);
        Assert.All(schemas.GetProperty("OperationStatusV1").GetProperty("enum").EnumerateArray(),
            value => Assert.Equal(JsonValueKind.String, value.ValueKind));
    }

    [Fact]
    public async Task GeneratedSixRoutesDescribeEmptyRequestsAndOnlyTheirExactSelectors()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        foreach (var operation in new[] { "projection", "validation" })
        {
            foreach (var retained in new[] { false, true })
            {
                var get = paths.GetProperty(RevisionRoute + "/reports/" + operation + (retained ? "/{derivationId}" : ""))
                    .GetProperty("get");
                Assert.False(get.TryGetProperty("requestBody", out _));
                Queries(get, operation == "validation" ? ["projectionDerivationId"] : []);
                var responses = get.GetProperty("responses");
                Assert.True(responses.TryGetProperty("200", out _));
                Assert.False(responses.TryGetProperty("202", out _));
                Assert.True(responses.TryGetProperty("429", out _));
                Assert.True(responses.TryGetProperty("503", out _));
            }
            var ensure = paths.GetProperty(RevisionRoute + "/reports/" + operation + "/ensure").GetProperty("post");
            Assert.False(ensure.TryGetProperty("requestBody", out _));
            Queries(ensure, ["repairDerivationId"]);
            var ensureResponses = ensure.GetProperty("responses");
            Assert.True(ensureResponses.TryGetProperty("200", out _));
            Assert.True(ensureResponses.TryGetProperty("409", out _));
            Assert.True(ensureResponses.TryGetProperty("429", out _));
            Assert.True(ensureResponses.TryGetProperty("503", out _));
            var pending = ensureResponses.GetProperty("202");
            Assert.True(pending.GetProperty("headers").TryGetProperty("Location", out _));
            Assert.True(pending.GetProperty("headers").TryGetProperty("Retry-After", out _));
        }
    }

    private static void Required(JsonElement schema, string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), schema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()!).Order(StringComparer.Ordinal));

    private static void Literal(JsonElement schema, string expected)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("readOnly").GetBoolean());
        Assert.Equal(expected, Assert.Single(schema.GetProperty("enum").EnumerateArray()).GetString());
    }

    private static void NullableReference(JsonElement schema, string name)
    {
        var variants = schema.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(2, variants.Length);
        Assert.Single(variants, value => value.TryGetProperty("type", out var type) && type.GetString() == "null");
        var reference = Assert.Single(variants, value => value.TryGetProperty("$ref", out _));
        Assert.Equal("#/components/schemas/" + name, reference.GetProperty("$ref").GetString());
    }

    private static void NullableString(JsonElement schema, string format)
    {
        Assert.Equal(new[] { "null", "string" }, schema.GetProperty("type").EnumerateArray()
            .Select(value => value.GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(format, schema.GetProperty("format").GetString());
    }

    private static void Identity(JsonElement schema)
    {
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("derivationId").GetProperty("type").GetString());
        Assert.Equal("uuid", properties.GetProperty("derivationId").GetProperty("format").GetString());
        Assert.Equal("string", properties.GetProperty("generation").GetProperty("type").GetString());
        Assert.Equal("^[1-9][0-9]{0,18}$", properties.GetProperty("generation").GetProperty("pattern").GetString());
    }

    private static void ClosedStrings(JsonElement schema, string[] expected)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        var values = schema.GetProperty("enum").EnumerateArray().ToArray();
        Assert.All(values, value => Assert.Equal(JsonValueKind.String, value.ValueKind));
        Assert.Equal(expected, values.Select(value => value.GetString()!));
    }

    private static void Queries(JsonElement operation, string[] expected)
    {
        var queries = operation.GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "query").ToArray();
        Assert.Equal(expected, queries.Select(parameter => parameter.GetProperty("name").GetString()!));
        foreach (var query in queries)
        {
            Assert.False(query.TryGetProperty("required", out var required) && required.GetBoolean());
            Assert.Equal("string", query.GetProperty("schema").GetProperty("type").GetString());
            Assert.Equal("uuid", query.GetProperty("schema").GetProperty("format").GetString());
        }
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
