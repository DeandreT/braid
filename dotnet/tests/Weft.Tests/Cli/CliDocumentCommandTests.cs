using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weft.Cli;
using Weft.Cli.Commands;
using Weft.Cli.Http;
using Weft.Contracts.Builds;
using Weft.Contracts.Catalog;
using Weft.Contracts.Regression;

namespace Weft.Tests.Cli;

/// <summary>Document commands against the strict generated-OpenAPI stub, not native engine qualification.</summary>
public sealed class CliDocumentCommandTests : IAsyncLifetime
{
    private CliTestIssuer issuer = null!;
    private CliApiStub stub = null!;
    private CliHarness cli = null!;
    private CliStubData data = null!;

    public async Task InitializeAsync()
    {
        issuer = await CliTestIssuer.StartAsync();
        stub = await CliApiStub.StartAsync(issuer);
        cli = new CliHarness(issuer, stub.Handler);
        data = new CliStubData(stub.OrganizationId);
        await cli.ConfigureAsync();
        await cli.LoginAsync();
    }

    public async Task DisposeAsync()
    {
        stub.AssertClean();
        cli.Dispose();
        await stub.DisposeAsync();
        await issuer.DisposeAsync();
    }

    [Theory]
    [InlineData("order.x12", true)]
    [InlineData("order.txt", true)]
    [InlineData("order.x12", false)]
    public async Task Preview_profile_is_explicit_and_never_inferred_from_the_filename(string filename, bool documents)
    {
        var input = Path.Combine(cli.ConfigurationDirectory, filename);
        var bytes = Encoding.UTF8.GetBytes("self-authored document bytes");
        await File.WriteAllBytesAsync(input, bytes);
        var run = CliStubData.Id();
        stub.Reply("SubmitCatalogPreview", _ => CliStubReply.Of(201,
            new CatalogPreviewSubmitResultV1("created", data.PreviewRun(run, "queued"))));
        var arguments = new List<string> { "previews", "create", "--input", input, "--project", data.Project,
            "--mapping", data.Mapping, "--revision", data.Revision };
        if (documents) arguments.Add("--document");
        var result = await cli.RunAsync(arguments.ToArray());
        Assert.True(result.Exit == 0, result.ToString());
        var body = Assert.Single(stub.Calls("SubmitCatalogPreview")).Json!;
        Assert.Equal(Convert.ToBase64String(bytes), body["inputs"]![0]!["content"]!.GetValue<string>());
        if (documents) Assert.Equal(PreviewCommands.DocumentProfile, body["profile"]!.GetValue<string>());
        else Assert.False(body.AsObject().ContainsKey("profile"));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/edi-x12")]
    [InlineData("application/edifact")]
    [InlineData("text/csv")]
    [InlineData("text/tab-separated-values")]
    [InlineData("application/xml")]
    public async Task Document_downloads_advertise_exact_media_and_verify_bytes_before_stdout(string media)
    {
        var content = Encoding.UTF8.GetBytes("owned exact output\r\n");
        var run = CliStubData.Id();
        var output = Output(media, content);
        stub.Reply("GetCatalogPreview", _ => CliStubReply.Of(200, data.PreviewRun(run, "completed", "succeeded", [output])));
        stub.Reply("GetCatalogPreviewOutput", _ => Reply(content, media));
        var result = await cli.RunAsync("previews", "download", run, "--output", "-");
        Assert.True(result.Exit == 0, result.ToString());
        Assert.Equal(content, result.Binary);
        Assert.Equal(new[] { "application/json", "application/edi-x12", "application/edifact", "text/csv", "text/tab-separated-values", "application/xml" },
            Assert.Single(stub.Calls("GetCatalogPreviewOutput")).Headers["Accept"].Split(',').Select(value => value.Trim()));
    }

    [Fact]
    public async Task Xml_preview_requires_the_explicit_option_and_never_adds_caller_metadata()
    {
        stub.Reply("GetCatalogContext", _ => CliStubReply.Of(200, XmlContext(true)));
        var path = Path.Combine(cli.ConfigurationDirectory, "xml-looking.json");
        var bytes = "<Order/>\r\n"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        stub.Reply("SubmitCatalogPreview", _ => CliStubReply.Of(201, new CatalogPreviewSubmitResultV1("created", data.PreviewRun(CliStubData.Id(), "queued"))));
        var result = await cli.RunAsync("previews", "create", "--xml", "--input", path, "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision);
        Assert.Equal(CliExitCode.Success, result.Code);
        var sent = Assert.Single(stub.Calls("SubmitCatalogPreview")).Json!;
        Assert.Equal(PreviewCommands.XmlProfile, sent["profile"]!.GetValue<string>());
        Assert.Equal(bytes, Convert.FromBase64String(sent["inputs"]![0]!["content"]!.GetValue<string>()));
        Assert.False(sent["inputs"]![0]!.AsObject().ContainsKey("xmlMetadata"));
        var mixed = await cli.RunAsync("previews", "create", "--xml", "--csv", "--input", path, "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision);
        Assert.NotEqual(CliExitCode.Success, mixed.Code);
        Assert.Single(stub.Calls("SubmitCatalogPreview"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Xml_unadvertised_context_refuses_before_any_stdin_or_file_io_and_never_uploads(bool explicitOrganization, bool file)
    {
        var opens = 0;
        cli.OpenStandardInput = () => { opens++; throw new InvalidOperationException("private-sentinel stdin was opened"); };
        cli.OpenInputFile = _ => { opens++; throw new InvalidOperationException("private-sentinel file was opened"); };
        foreach (var state in new[] { "disabled", "missing", "unsupported" })
        {
            var body = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(XmlContext(false), WeftApiClient.Json))!;
            if (state == "missing") body["features"]!.AsObject().Remove("xmlPreview");
            stub.Reply("GetCatalogContext", _ => state == "unsupported" ? CliStubReply.Error(404, CatalogErrorCodeV1.Unavailable) : CliStubReply.Of(200, body));
            var args = new List<string> { "previews", "create", "--xml", "--input", file ? "private-sentinel.xml" : "-", "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision };
            if (explicitOrganization) args.AddRange(["--organization", stub.OrganizationId]);
            var result = await cli.RunAsync(args.ToArray());
            Assert.Equal(CliExitCode.Unavailable, result.Code);
            Assert.Equal(0, opens);
            Assert.Empty(stub.Calls("SubmitCatalogPreview"));
            Assert.All(stub.Requests, request => Assert.Equal("GetCatalogContext", request.OperationId));
        }
        Assert.Equal(3, stub.Calls("GetCatalogContext").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Xml_enabled_context_is_fresh_and_submission_keeps_its_captured_scope_and_request_identity(bool explicitOrganization)
    {
        stub.Reply("GetCatalogContext", _ => CliStubReply.Of(200, XmlContext(true)));
        cli.OpenStandardInput = () =>
        {
            Assert.Single(stub.Calls("GetCatalogContext"));
            stub.Reply("GetCatalogContext", _ => CliStubReply.Of(200, XmlContext(false)));
            return new MemoryStream("<Order/>"u8.ToArray());
        };
        stub.Reply("SubmitCatalogPreview", _ => CliStubReply.Of(201, new CatalogPreviewSubmitResultV1("created", data.PreviewRun(CliStubData.Id(), "queued"))));
        var key = CliStubData.Id();
        var args = new List<string> { "previews", "create", "--xml", "--input", "-", "--request-id", key, "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision };
        if (explicitOrganization) args.AddRange(["--organization", stub.OrganizationId]);
        Assert.Equal(CliExitCode.Success, (await cli.RunAsync(args.ToArray())).Code);
        var sent = Assert.Single(stub.Calls("SubmitCatalogPreview"));
        Assert.Single(stub.Calls("GetCatalogContext"));
        Assert.Equal(stub.OrganizationId, sent.Path["organizationId"]);
        Assert.Equal(data.Project, sent.Path["projectId"]);
        Assert.Equal(data.Mapping, sent.Path["mappingId"]);
        Assert.Equal(data.Revision, sent.Path["revisionId"]);
        Assert.Equal(key, sent.Headers["Idempotency-Key"]);
        Assert.Equal(Convert.ToBase64String("<Order/>"u8), sent.Json!["inputs"]![0]!["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("invalidXml", "invalidInput", null, null)]
    [InlineData("depthLimit", "resourceLimit", "65", "64")]
    [InlineData("nodeLimit", "resourceLimit", "1000001", "1000000")]
    [InlineData("metadataTooLarge", "resourceLimit", "32769", "32768")]
    [InlineData("metadataTooLarge", "resourceLimit", "9223372036854775807", "32768")]
    public async Task Xml_preflight_refusals_show_safe_facts_without_a_run_or_retry(string reason, string kind, string? observed, string? limit)
    {
        stub.Reply("GetCatalogContext", _ => CliStubReply.Of(200, XmlContext(true)));
        stub.Reply("SubmitCatalogPreview", _ => CliStubReply.Of(422,
            new CatalogXmlPreviewProblemV1("xmlPreflight", kind, reason, new(2, 3), new(12, 3, 2, 0), observed, limit)));
        var result = await cli.RunWithInputAsync("<private-sentinel/>"u8.ToArray(), "previews", "create", "--xml", "--input", "-", "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision);
        Assert.Equal(CliExitCode.InvalidRequest, result.Code);
        Assert.Contains("Line 2, column 3", result.Error);
        Assert.Contains("No preview run was created", result.Error);
        Assert.DoesNotContain("private-sentinel", result.Error + result.Out);
        if (limit is not null) Assert.Contains("Limit " + limit, result.Error);
        Assert.Single(stub.Calls("SubmitCatalogPreview"));
        Assert.Empty(stub.Calls("GetCatalogPreview"));
        Assert.Empty(result.Binary);
    }

    [Fact]
    public async Task Xml_explicit_organization_must_match_fresh_admission_before_input_io()
    {
        stub.Reply("GetCatalogContext", _ => CliStubReply.Of(200, XmlContext(true)));
        cli.OpenStandardInput = () => throw new InvalidOperationException("Input must not be opened.");
        var result = await cli.RunAsync("previews", "create", "--xml", "--organization", CliStubData.Id(), "--input", "-", "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision);
        Assert.Equal(CliExitCode.Usage, result.Code);
        Assert.Single(stub.Calls("GetCatalogContext"));
        Assert.Empty(stub.Calls("SubmitCatalogPreview"));
    }

    [Theory]
    [InlineData("reason", "private-sentinel")]
    [InlineData("observed", "-1")]
    [InlineData("limit", "9223372036854775808")]
    [InlineData("message", "private-sentinel parser text")]
    public async Task Xml_cli_never_echoes_untrusted_problem_fields(string field, string text)
    {
        var body = JsonSerializer.SerializeToNode(new CatalogXmlPreviewProblemV1("xmlPreflight", "resourceLimit", "depthLimit", null, null, "65", "64"), WeftApiClient.Json)!;
        body[field] = text;
        using var message = new HttpResponseMessage(System.Net.HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var response = new ApiResponse(message, ApiOperations.SubmitCatalogPreview);
        var error = await response.FailureAsync(default);
        Assert.Equal(CliExitCode.InvalidRequest, error.Code);
        Assert.DoesNotContain("private-sentinel", error.Message);
        Assert.DoesNotContain("No preview run was created", error.Message);
    }

    private CatalogContextV1 XmlContext(bool enabled) => new("bearer", CliStubData.Id(), stub.OrganizationId, "Stub organization",
        new CatalogFeaturesV1(true, true, true, true, true, true, true, true, false, true, xmlPreview: enabled),
        new CatalogOrganizationActionsV1(true), requestToken: null, identityContext: null);

    [Theory]
    [InlineData("--csv")]
    [InlineData("--document")]
    public async Task Xml_profile_conflicts_never_submit(string other)
    {
        var result = await cli.RunWithInputAsync([], "previews", "create", "--xml", other, "--input", "-", "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision);
        Assert.Equal(CliExitCode.Usage, result.Code);
        Assert.Empty(stub.Calls("SubmitCatalogPreview"));
    }

    [Fact]
    public async Task Xml_regression_create_and_get_keep_metadata_and_exact_baseline()
    {
        var body = data.CaseDefinition with
        {
            Name = "XML CLI",
            Runtime = data.Runtime with { EngineRevision = "dddc4dce6c93f4137fb2aa2a011360d089e0a51b", BackendBuild = "sha256:" + new string('a', 64), Profile = PreviewCommands.XmlProfile, AdapterBuild = "weft.ferrule-xml-preview-adapter.v1" },
            PrimaryBase64Url = Convert.ToBase64String("<Order/>\n"u8).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            Parameters = [],
            ComparisonPolicy = "weft.regression.exact-bytes.v1",
            Expected = [new(null, Convert.ToBase64String("<Order/>\n"u8).TrimEnd('=').Replace('+', '-').Replace('/', '_'), "application/xml", XmlMetadata: new("weft.xml-boundary.v1", "weft.xml-safe.v1", null))],
        };
        var id = CliStubData.Id();
        stub.Reply("PutRegressionCaseVersion", _ => CliStubReply.Of(201, new RegressionCaseSavedV1(id, "1", data.RevisionScope(), "created")));
        var created = await cli.RunWithInputAsync(JsonSerializer.SerializeToUtf8Bytes(body, WeftApiClient.Json), "regression", "cases", "create", "--case-id", id, "--definition", "-", "--project", data.Project, "--mapping", data.Mapping);
        Assert.Equal(CliExitCode.Success, created.Code);
        var sent = Assert.Single(stub.Calls("PutRegressionCaseVersion")).Json!;
        Assert.Equal("application/xml", sent["expected"]![0]!["mediaType"]!.GetValue<string>());
        Assert.Equal("weft.xml-safe.v1", sent["expected"]![0]!["xmlMetadata"]!["safetyProfile"]!.GetValue<string>());
        stub.Reply("GetRegressionCaseVersion", _ => CliStubReply.Of(200, new RegressionCaseDetailV1(id, "1", data.RevisionScope(), body)));
        var read = await cli.RunAsync("regression", "cases", "get", id, "1", "--project", data.Project, "--mapping", data.Mapping, "--json");
        Assert.Equal(CliExitCode.Success, read.Code);
        using var document = JsonDocument.Parse(read.Out);
        var actual = document.RootElement.GetProperty("definition").GetProperty("expected")[0];
        Assert.Equal("application/xml", actual.GetProperty("mediaType").GetString());
        Assert.Equal("weft.xml-safe.v1", actual.GetProperty("xmlMetadata").GetProperty("safetyProfile").GetString());
        Assert.Equal(body.Expected[0].BytesBase64Url, actual.GetProperty("bytesBase64Url").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Matching_hashes_do_not_authorize_wrong_media_or_encoded_output(bool encoded)
    {
        var content = Encoding.UTF8.GetBytes("owned exact output");
        var run = CliStubData.Id();
        stub.Reply("GetCatalogPreview", _ => CliStubReply.Of(200,
            data.PreviewRun(run, "completed", "succeeded", [Output("application/edi-x12", content)])));
        stub.Reply("GetCatalogPreviewOutput", _ =>
        {
            var reply = Reply(content, encoded ? "application/edi-x12" : "application/json");
            if (encoded) reply.Headers["Content-Encoding"] = "gzip";
            return reply;
        });
        var path = Path.Combine(cli.ConfigurationDirectory, "existing.x12");
        await File.WriteAllTextAsync(path, "preserve this file");
        Assert.Equal(CliExitCode.Transport, (await cli.RunAsync("previews", "download", run, "--output", path)).Code);
        Assert.Equal("preserve this file", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(cli.ConfigurationDirectory, "*.partial"));
        var piped = await cli.RunAsync("previews", "download", run, "--output", "-");
        Assert.Equal(CliExitCode.Transport, piped.Code);
        Assert.Empty(piped.Binary);
    }

    [Fact]
    public async Task Regression_definitions_keep_document_media_and_explicit_comparison_policy()
    {
        var body = data.CaseDefinition with
        {
            Runtime = data.Runtime with { Profile = PreviewCommands.DocumentProfile },
            ComparisonPolicy = "weft.regression.edi-segments.v1",
            ComparisonOptions = new(true, false, false),
            Expected = [new(null, Convert.ToBase64String("UNH+1'"u8).TrimEnd('=').Replace('+', '-').Replace('/', '_'), "application/edifact")]
        };
        var path = Path.Combine(cli.ConfigurationDirectory, "document-case.json");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(body, WeftApiClient.Json));
        stub.Reply("PutRegressionCaseVersion", request => CliStubReply.Of(201,
            new RegressionCaseSavedV1(request.Path["caseId"], request.Path["version"], data.RevisionScope(), "created")));
        var result = await cli.RunAsync("regression", "cases", "create", "--project", data.Project, "--mapping", data.Mapping,
            "--definition", path);
        Assert.True(result.Exit == 0, result.ToString());
        var sent = Assert.Single(stub.Calls("PutRegressionCaseVersion")).Json!;
        Assert.Equal("application/edifact", sent["expected"]![0]!["mediaType"]!.GetValue<string>());
        Assert.True(sent["comparisonOptions"]!["compareSeparators"]!.GetValue<bool>());
        Assert.False(sent["comparisonOptions"]!["ignoreControlNumbers"]!.GetValue<bool>());
        Assert.False(sent["comparisonOptions"]!["ignoreDates"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Capability_tables_include_the_separately_admitted_document_profiles()
    {
        var documentRuntime = data.Runtime with { Profile = PreviewCommands.DocumentProfile };
        stub.Reply("GetRegressionCaseCapabilities", _ => CliStubReply.Of(200,
            new RegressionCaseCapabilitiesV1(data.Runtime, data.CaseDefinition.Limits, "weft.regression.exact-bytes.v1", true, true,
                documentRuntime, ["weft.regression.exact-bytes.v1", "weft.regression.edi-segments.v1"])));
        var regression = await cli.RunAsync("regression", "capabilities", "--project", data.Project, "--mapping", data.Mapping);
        Assert.True(regression.Exit == 0, regression.ToString());
        Assert.Contains(PreviewCommands.DocumentProfile, regression.Out, StringComparison.Ordinal);
        Assert.Contains("weft.regression.edi-segments.v1", regression.Out, StringComparison.Ordinal);

        var backend = new BuildBackendV1("ferrule", "revision", "build", "adapter");
        var document = data.BuildProfile with { Id = "document-runtime" };
        var interchange = data.BuildProfile with { Id = "weft.ferrule-csharp-x12-interchanges.v1" };
        stub.Reply("GetBuildCapabilities", _ => CliStubReply.Of(200,
            new BuildCapabilitiesV1(backend, data.BuildProfile, true, true, true, backend, document, interchange)));
        stub.Reply("GetBuildEnvironmentCapabilities", _ => CliStubReply.Of(200,
            new BuildEnvironmentCapabilitiesV1(data.BuildProfile, true, document, interchange)));
        foreach (var command in new[] { "capabilities", "environment-capabilities" })
        {
            var args = new List<string> { "builds", command, "--project", data.Project };
            if (command == "capabilities") args.AddRange(["--mapping", data.Mapping]);
            var result = await cli.RunAsync(args.ToArray());
            Assert.True(result.Exit == 0, result.ToString());
            Assert.Contains("document-runtime", result.Out, StringComparison.Ordinal);
            Assert.Contains(interchange.Id, result.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Interchange_environment_selection_keeps_the_exact_offered_profile_on_the_wire()
    {
        var id = CliStubData.Id();
        var profile = data.BuildProfile with { Id = "weft.ferrule-csharp-x12-interchanges.v1", Version = "1" };
        stub.Reply("PutBuildEnvironment", _ => CliStubReply.Of(201, new BuildEnvironmentReceiptV1("created", data.Environment(id, "1") with { Profile = profile })));
        var result = await cli.RunAsync("builds", "environments", "create", id, "1", "--project", data.Project,
            "--profile-id", profile.Id, "--profile-version", "1");
        Assert.Equal(CliExitCode.Success, result.Code);
        var sent = Assert.Single(stub.Calls("PutBuildEnvironment")).Json!;
        Assert.Equal(profile.Id, sent["profileId"]!.GetValue<string>());
        Assert.Equal("1", sent["profileVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Edi_failures_show_bounded_locations_instead_of_payload_contents(bool invalid)
    {
        var id = CliStubData.Id();
        var location = new RegressionDocumentLocationV1(1, 2, "BGM", 3, 1, 1);
        var run = data.RegressionRun(id, CliStubData.Id(), "completed", "failed") with
        {
            Comparison = new("weft.regression.edi-segments.v1", invalid ? "notEvaluated" : "failed",
                invalid ? "invalidDocument" : "none", 1, 1, invalid ? 0 : 1, false,
                invalid ? [] : [new(null, "content", 10, 10, null, null, null, location)],
                invalid ? new(null, "actual", location) : null)
        };
        stub.Reply("GetRegressionRun", _ => CliStubReply.Of(200, run));
        var result = await cli.RunAsync("regression", "runs", "get", id, "--project", data.Project, "--mapping", data.Mapping);
        Assert.True(result.Exit == 0, result.ToString());
        Assert.Contains("segment 2 (BGM)", result.Out, StringComparison.Ordinal);
        Assert.Contains("element 3", result.Out, StringComparison.Ordinal);
    }

    private static CatalogPreviewOutputV1 Output(string media, byte[] content) => new(0, "primary", "Result", false,
        media, "1", Convert.ToHexStringLower(SHA256.HashData(content)), content.Length,
        media is "text/csv" or "text/tab-separated-values"
            ? new("weft.csv-dialect.v1", media == "text/csv" ? "," : "\t", "\"", false, false, false, true)
            : null, media == "application/xml" ? new("weft.xml-boundary.v1", "weft.xml-safe.v1", null) : null);

    private static CliStubReply Reply(byte[] content, string media) => new()
    {
        Bytes = content,
        ContentType = media,
        Headers = { ["Content-Digest"] = "sha-256=:" + Convert.ToBase64String(SHA256.HashData(content)) + ":" }
    };
}
