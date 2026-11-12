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
        Assert.Equal(new[] { "application/json", "application/edi-x12", "application/edifact", "text/csv", "text/tab-separated-values" },
            Assert.Single(stub.Calls("GetCatalogPreviewOutput")).Headers["Accept"].Split(',').Select(value => value.Trim()));
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
            : null);

    private static CliStubReply Reply(byte[] content, string media) => new()
    {
        Bytes = content,
        ContentType = media,
        Headers = { ["Content-Digest"] = "sha-256=:" + Convert.ToBase64String(SHA256.HashData(content)) + ":" }
    };
}
