using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Application.Regression;

namespace Braid.Tests;

public sealed class RegressionDocumentRequestTests
{
    [Fact]
    public void Runtime_selection_admits_only_the_exact_independent_json_and_document_profiles()
    {
        var json = new RegressionRuntimeIdentity("ferrule", "engine", "build", "json-adapter", PreviewPolicy.Profile);
        var document = new RegressionRuntimeIdentity("ferrule", "engine", "build", "document-adapter", DocumentPreviewPolicy.Profile);
        var options = new CatalogRegressionCaseOptions(true, json, document);
        Assert.Same(json, options.SelectRuntime(json));
        Assert.Same(document, options.SelectRuntime(document));
        Assert.Null(new CatalogRegressionCaseOptions(true, json).SelectRuntime(document));
        Assert.Null(new CatalogRegressionCaseOptions().SelectRuntime(json));
        Assert.Null(options.SelectRuntime(new("ferrule", "engine", "build", "other-adapter", DocumentPreviewPolicy.Profile)));
    }

    [Fact]
    public async Task Csv_request_requires_explicit_dialect_and_accepts_a_present_empty_primary()
    {
        var body = Body();
        body["runtime"]!["profile"] = CsvDocumentPolicy.Profile;
        body["runtime"]!["adapterBuild"] = "csv-adapter";
        body["primaryBase64Url"] = "";
        body["comparisonPolicy"] = RegressionComparisonService.CsvRowsPolicy;
        body.Remove("comparisonOptions");
        body["expected"]![0]!["mediaType"] = "text/tab-separated-values";
        body["expected"]![0]!["bytesBase64Url"] = "";
        body["expected"]![0]!["csvDialect"] = JsonNode.Parse(CsvDialectJson.Encode(CsvPreviewTestData.Dialect));
        var value = await Read(body);
        Assert.Equal(0, value.Replay.Primary.ByteCount);
        Assert.Equal(PreviewExecutionProfile.CsvDocument, value.Replay.Profile);
        Assert.Equal(CsvPreviewTestData.Dialect, Assert.Single(value.Expected.Outputs).CsvDialect);
        body["expected"]![0]!.AsObject().Remove("csvDialect");
        await Assert.ThrowsAsync<CatalogRegressionCaseRequestException>(() => Read(body));
    }

    [Fact]
    public void Csv_runtime_is_a_separate_exact_server_selection()
    {
        var json = new RegressionRuntimeIdentity("ferrule", "engine", "build", "json-adapter", PreviewPolicy.Profile);
        var csv = new RegressionRuntimeIdentity("ferrule", "engine", "build", "csv-adapter", CsvDocumentPolicy.Profile);
        var options = new CatalogRegressionCaseOptions(true, json, csvRuntime: csv);
        Assert.Same(csv, options.SelectRuntime(csv));
        Assert.Null(new CatalogRegressionCaseOptions(true, json).SelectRuntime(csv));
        Assert.Null(options.SelectRuntime(new("ferrule", "engine", "other-build", "csv-adapter", CsvDocumentPolicy.Profile)));
    }

    private static JsonObject Body() => new()
    {
        ["name"] = "EDI fixture",
        ["revisionId"] = "60000000-0000-4000-8000-000000000001",
        ["runtime"] = new JsonObject
        {
            ["backendId"] = "ferrule",
            ["engineRevision"] = "engine",
            ["backendBuild"] = "build",
            ["adapterBuild"] = "document-adapter",
            ["profile"] = DocumentPreviewPolicy.Profile
        },
        ["currentDateTime"] = "2026-10-24T12:00:00Z",
        ["target"] = new JsonObject { ["kind"] = "all", ["name"] = null },
        ["limits"] = new JsonObject
        {
            ["maximumInputBytes"] = 8388608,
            ["maximumOutputBytes"] = 8388608,
            ["maximumArtifacts"] = 64,
            ["maximumTextBytes"] = 2048
        },
        ["primaryBase64Url"] = "e30",
        ["namedInputs"] = new JsonArray(),
        ["parameters"] = new JsonArray(),
        ["comparisonPolicy"] = RegressionComparisonService.EdiSegmentsPolicy,
        ["comparisonOptions"] = new JsonObject { ["compareSeparators"] = true, ["ignoreControlNumbers"] = false, ["ignoreDates"] = false },
        ["expected"] = new JsonArray(new JsonObject { ["name"] = null, ["bytesBase64Url"] = "e30", ["mediaType"] = "application/edi-x12" })
    };

    private static Task<CatalogRegressionCaseDefinition> Read(JsonObject body)
    {
        var request = new DefaultHttpContext().Request;
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        request.ContentType = CatalogRegressionCaseRequest.MediaType;
        request.ContentLength = bytes.Length; request.Body = new MemoryStream(bytes);
        return CatalogRegressionCaseRequest.ReadAsync(request, default);
    }

    [Fact]
    public async Task Document_request_owns_explicit_media_and_options_without_decoding_samples()
    {
        var value = await Read(Body());
        Assert.Equal(DocumentPreviewPolicy.Profile, value.Runtime.Profile);
        Assert.Equal(new EdiComparisonOptions(true, false, false), value.EdiOptions);
        Assert.Equal("application/edi-x12", Assert.Single(value.Expected.Outputs).MediaType);
    }

    [Theory]
    [InlineData("missingMedia")]
    [InlineData("unknownMedia")]
    [InlineData("inputMedia")]
    [InlineData("missingOption")]
    [InlineData("stringOption")]
    [InlineData("legacyMedia")]
    public async Task Conditional_document_grammar_refuses_ambiguous_shapes(string mode)
    {
        var body = Body();
        if (mode == "missingMedia") body["expected"]![0]!.AsObject().Remove("mediaType");
        if (mode == "unknownMedia") body["expected"]![0]!["mediaType"] = "text/plain";
        if (mode == "inputMedia") body["namedInputs"]!.AsArray().Add(new JsonObject { ["name"] = "lookup", ["bytesBase64Url"] = "e30", ["mediaType"] = "application/json" });
        if (mode == "missingOption") body["comparisonOptions"]!.AsObject().Remove("ignoreDates");
        if (mode == "stringOption") body["comparisonOptions"]!["ignoreDates"] = "false";
        if (mode == "legacyMedia") body["runtime"]!["profile"] = PreviewPolicy.Profile;
        await Assert.ThrowsAsync<CatalogRegressionCaseRequestException>(() => Read(body));
    }
}
