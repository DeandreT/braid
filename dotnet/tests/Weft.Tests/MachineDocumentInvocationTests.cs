using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;
using Weft.MachineEndpoint;

namespace Weft.Tests;

public sealed class MachineDocumentInvocationTests
{
    [Fact]
    public void Openapi_document_routes_media_and_closed_request_shapes_match_the_running_parser()
    {
        using var specification = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts", "weft-endpoint-v1.json")));
        var root = specification.RootElement;
        var paths = root.GetProperty("paths");
        Assert.Equal(new[] { MappingEndpointRoutes.ReadyPath, MappingEndpointRoutes.LivePath, MappingEndpointRoutes.InvocationsPath }.Order(),
            paths.EnumerateObject().Select(value => value.Name).Order());
        var operation = paths.GetProperty(MappingEndpointRoutes.InvocationsPath).GetProperty("post");
        Assert.Equal(MappingEndpointRoutes.RequestIdHeader, operation.GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.True(operation.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var mediaValues = schemas.GetProperty("MappingDocumentInvocationInputV1").GetProperty("properties").GetProperty("mediaType").GetProperty("enum");
        Assert.Equal(new[] { "application/json", "application/edi-x12", "application/edifact", "text/csv", "text/tab-separated-values" },
            mediaValues.EnumerateArray().Select(value => value.GetString()));
        var body = Body();
        Assert.Equal(body["schema"]!.GetValue<string>(), schemas.GetProperty("MappingDocumentInvocationRequestV1").GetProperty("properties").GetProperty("schema").GetProperty("enum")[0].GetString());
        foreach (var (schema, sample) in new[] { ("MappingDocumentInvocationRequestV1", body), ("MappingInvocationRequestV1", new JsonObject { ["inputs"] = new JsonArray(new JsonObject { ["content"] = "e30=" }) }) })
        {
            var definition = schemas.GetProperty(schema);
            Assert.False(definition.GetProperty("additionalProperties").GetBoolean());
            foreach (var property in sample) Assert.True(definition.GetProperty("properties").TryGetProperty(property.Key, out _));
            Assert.NotNull(MappingEndpointRoutes.Parse(Encoding.UTF8.GetBytes(sample.ToJsonString())));
            sample["unknown"] = true;
            Assert.Throws<FormatException>(() => MappingEndpointRoutes.Parse(Encoding.UTF8.GetBytes(sample.ToJsonString())));
        }
        foreach (var media in mediaValues.EnumerateArray())
        {
            var sample = Body(); sample["inputs"]![0]!["mediaType"] = media.GetString();
            if (CsvDocumentPolicy.AdmitsMediaType(media.GetString()!))
                sample["inputs"]![0]!["csvDialect"] = JsonSerializer.SerializeToNode(BuildCsvContractTests.Dialect(
                    media.GetString() == "text/csv" ? "," : "\t"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(media.GetString(), Assert.Single(MappingEndpointRoutes.Parse(Encoding.UTF8.GetBytes(sample.ToJsonString())).InputMedia!).MediaType);
        }
    }

    [Theory]
    [InlineData(",", "text/csv")]
    [InlineData("\t", "text/tab-separated-values")]
    public void Explicit_empty_csv_request_requires_matching_dialect_and_keeps_its_request_identity(string delimiter, string mediaType)
    {
        var dialect = BuildCsvContractTests.Dialect(delimiter);
        var body = Body();
        body["inputs"]![0]!["content"] = "";
        body["inputs"]![0]!["mediaType"] = mediaType;
        body["inputs"]![0]!["csvDialect"] = JsonSerializer.SerializeToNode(dialect, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = MappingEndpointRoutes.Parse(Encoding.UTF8.GetBytes(body.ToJsonString()));
        Assert.Empty(request.Primary.CopyBytes());
        Assert.Equal(dialect, request.Primary.CsvDialect);
        var changed = new MachineInvocationRequest(new(null, [], dialect with { Utf8Bom = true }), [], [],
            [new(null, mediaType, dialect with { Utf8Bom = true })]);
        Assert.NotEqual(request.Digest, changed.Digest);
        body["inputs"]![0]!.AsObject().Remove("csvDialect");
        Assert.Throws<FormatException>(() => MappingEndpointRoutes.Parse(Encoding.UTF8.GetBytes(body.ToJsonString())));
        body["inputs"]![0]!.AsObject().Remove("content");
        Assert.Throws<FormatException>(() => MappingEndpointRoutes.Parse(Encoding.UTF8.GetBytes(body.ToJsonString())));
    }

    [Fact]
    public void Csv_response_keeps_dialect_and_empty_output_and_round_trips_without_relabelling()
    {
        var dialect = BuildCsvContractTests.Dialect();
        var outputs = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, [], mediaType: "text/csv", csvDialect: dialect)]);
        var provenance = new MachineInvocationProvenance(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Sha256Digest.FromBytes([1]),
            Sha256Digest.FromBytes([2]), Guid.NewGuid(), "sha256:" + new string('a', 64));
        var bytes = MachineInvocationService.Encode(Guid.NewGuid(), provenance, "2026-10-30T12:00:00Z", outputs, documents: true);
        var wire = JsonSerializer.Deserialize<MappingDocumentInvocationResponseV1>(bytes, MappingEndpointContract.Json)!;
        Assert.Equal(dialect, Assert.Single(wire.Outputs).CsvDialect);
        Assert.Empty(Assert.Single(wire.Outputs).Content);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(wire, MappingEndpointContract.Json));
    }

    private static JsonObject Body() => new()
    {
        ["schema"] = "weft.document-invocation-request.v1",
        ["inputs"] = new JsonArray(new JsonObject
        {
            ["name"] = null,
            ["mediaType"] = "application/edi-x12",
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(DocumentPreviewTestData.X12))
        }),
        ["parameters"] = new JsonArray()
    };

    [Fact]
    public void Explicit_document_request_keeps_bytes_media_and_a_separate_request_identity()
    {
        var value = MappingEndpointRoutes.Parse(Encoding.UTF8.GetBytes(Body().ToJsonString()));
        Assert.Equal("application/edi-x12", Assert.Single(value.InputMedia!).MediaType);
        Assert.Equal(Encoding.UTF8.GetBytes(DocumentPreviewTestData.X12), value.Primary.CopyBytes());
        var legacy = new MachineInvocationRequest(value.Primary, value.NamedInputs, value.Parameters);
        Assert.NotEqual(legacy.Digest, value.Digest);
        Assert.Equal(legacy.Digest, new MachineInvocationRequest(value.Primary, value.NamedInputs, value.Parameters).Digest);
        var relabelled = new MachineInvocationRequest(value.Primary, value.NamedInputs, value.Parameters, [new(null, "application/edifact")]);
        Assert.NotEqual(value.Digest, relabelled.Digest);
    }

    [Theory]
    [InlineData("missingMedia")]
    [InlineData("unsupportedMedia")]
    [InlineData("unknownSchema")]
    [InlineData("legacyMedia")]
    public void Ambiguous_or_unsupported_document_requests_are_refused(string mode)
    {
        var body = Body();
        if (mode == "missingMedia") body["inputs"]![0]!.AsObject().Remove("mediaType");
        if (mode == "unsupportedMedia") body["inputs"]![0]!["mediaType"] = "text/plain";
        if (mode == "unknownSchema") body["schema"] = "unknown";
        if (mode == "legacyMedia") body.Remove("schema");
        var error = Record.Exception(() => MappingEndpointRoutes.Parse(Encoding.UTF8.GetBytes(body.ToJsonString())));
        Assert.True(error is FormatException or ArgumentException);
    }

    [Fact]
    public void Document_response_keeps_the_output_media_and_cannot_use_the_legacy_encoder()
    {
        var outputs = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary,
            Encoding.UTF8.GetBytes(DocumentPreviewTestData.X12), mediaType: "application/edi-x12")]);
        var provenance = new MachineInvocationProvenance(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Sha256Digest.FromBytes([1]),
            Sha256Digest.FromBytes([2]), Guid.NewGuid(), "sha256:" + new string('a', 64));
        var id = Guid.NewGuid();
        var bytes = MachineInvocationService.Encode(id, provenance, "2026-10-24T12:00:00Z", outputs, documents: true);
        using var result = JsonDocument.Parse(bytes);
        Assert.Equal("weft.document-invocation-response.v1", result.RootElement.GetProperty("schema").GetString());
        var output = result.RootElement.GetProperty("outputs")[0];
        Assert.Equal("application/edi-x12", output.GetProperty("mediaType").GetString());
        Assert.Equal(Encoding.UTF8.GetBytes(DocumentPreviewTestData.X12), output.GetProperty("content").GetBytesFromBase64());
        var wire = JsonSerializer.Deserialize<MappingDocumentInvocationResponseV1>(bytes, MappingEndpointContract.Json)!;
        Assert.Equal(MappingEndpointContract.DocumentResponseSchema, wire.Schema);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(wire, MappingEndpointContract.Json));
        Assert.Throws<ArgumentException>(() => MachineInvocationService.Encode(id, provenance, "2026-10-24T12:00:00Z", outputs));
    }
}
