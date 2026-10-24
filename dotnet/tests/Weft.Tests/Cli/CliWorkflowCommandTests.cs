using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Weft.Cli;
using Weft.Contracts.Builds;
using Weft.Contracts.Catalog;
using Weft.Contracts.Regression;

namespace Weft.Tests.Cli;

/// <summary>Report, comparison, preview, regression and build commands end to end against the OpenAPI stub.</summary>
public sealed class CliWorkflowCommandTests : IAsyncLifetime
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

    private string[] Revision(params string[] args) => [.. args, "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision];

    [Theory]
    [InlineData("builds", "environments", "GetBuildEnvironments")]
    [InlineData("builds", "approvals", "GetBuildApprovals")]
    [InlineData("builds", "runs", "GetBuildRuns")]
    [InlineData("regression", "cases", "GetRegressionCases")]
    [InlineData("regression", "runs", "GetRegressionRuns")]
    public async Task FullWorkflowListsRefuseCyclicCursors(string group, string resource, string operation)
    {
        var cursor = CliStubData.Id();
        object page = operation switch
        {
            "GetBuildEnvironments" => new BuildEnvironmentPageV1([], cursor, "1"),
            "GetBuildApprovals" => new BuildApprovalPageV1([], cursor),
            "GetBuildRuns" => new BuildRunPageV1([], cursor),
            "GetRegressionCases" => new RegressionCasePageV1([], cursor),
            "GetRegressionRuns" => new RegressionRunPageV1([], cursor),
            _ => throw new InvalidOperationException()
        };
        stub.Reply(operation, _ => CliStubReply.Of(200, page));
        string[] ancestry = resource == "environments" ? ["--project", data.Project] : ["--project", data.Project, "--mapping", data.Mapping];
        var result = await cli.RunAsync([group, resource, "list", "--all", "--json", .. ancestry]);
        Assert.Equal(CliExitCode.Transport, result.Code);
        Assert.Contains("pagination cursor", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Out);
        Assert.Equal(2, stub.Calls(operation).Count);
    }

    [Fact]
    public async Task EnsureWaitsAsTheServerAdvisesUntilTheReportIsAvailable()
    {
        var derivation = CliStubData.Id();
        stub.Enqueue("EnsureCatalogProjectionReport", CliStubReply.Of(202, data.ProjectionPending(derivation), ("Retry-After", "4")),
            CliStubReply.Of(200, data.ProjectionAvailable(derivation)));

        var ensured = await cli.RunAsync(Revision("reports", "projection", "ensure"));
        Assert.True(ensured.Exit == 0, ensured.ToString());
        Assert.Contains("available", ensured.Out, StringComparison.Ordinal);
        Assert.Contains(derivation, ensured.Out, StringComparison.Ordinal);
        Assert.Contains(TimeSpan.FromSeconds(4), cli.Time.Delays);
        Assert.All(stub.Calls("EnsureCatalogProjectionReport"), call => Assert.Empty(call.Body));

        stub.Enqueue("EnsureCatalogProjectionReport", CliStubReply.Of(202, data.ProjectionPending(derivation)));
        var pending = await cli.RunAsync(Revision("reports", "projection", "ensure", "--no-wait"));
        Assert.True(pending.Exit == 0, pending.ToString());
        Assert.Contains("pending", pending.Out, StringComparison.Ordinal);

        var repair = CliStubData.Id();
        stub.Enqueue("EnsureCatalogValidationReport", CliStubReply.Of(200, data.ValidationFailed(repair)));
        var failed = await cli.RunAsync(Revision("reports", "validation", "ensure", "--repair", repair));
        Assert.Equal(CliExitCode.OperationFailed, failed.Code);
        Assert.Equal(repair, stub.Calls("EnsureCatalogValidationReport")[0].Query["repairDerivationId"]);
    }

    [Fact]
    public async Task BuildEnvironmentPagesCanBeContinuedWithTheirPairedCursor()
    {
        var cursor = CliStubData.Id();
        stub.Reply("GetBuildEnvironments", _ => CliStubReply.Of(200, new BuildEnvironmentPageV1([], null, null)));
        var result = await cli.RunAsync("builds", "environments", "list", "--project", data.Project,
            "--after-environment", cursor, "--after-version", "2", "--json");
        Assert.True(result.Exit == 0, result.ToString());
        var call = Assert.Single(stub.Calls("GetBuildEnvironments"));
        Assert.Equal(cursor, call.Query["afterEnvironmentId"]);
        Assert.Equal("2", call.Query["afterVersion"]);
        string[][] partialCursors = [["--after-environment", cursor], ["--after-version", "2"],
            ["--after-environment", cursor, "--after-version", "0"]];
        foreach (var partial in partialCursors)
        {
            var invalid = await cli.RunAsync(["builds", "environments", "list", "--project", data.Project, .. partial]);
            Assert.Equal(CliExitCode.Usage, invalid.Code);
        }
        Assert.Single(stub.Calls("GetBuildEnvironments"));
    }

    [Fact]
    public async Task ReportsAreReadCurrentOrRetainedAndValidationFindingsAreShownSafely()
    {
        var derivation = CliStubData.Id();
        var projection = CliStubData.Id();
        stub.Reply("GetRetainedCatalogValidationReport", _ => CliStubReply.Of(200, data.ValidationAvailable(derivation, false, projection)));
        var retained = await cli.RunAsync(Revision("reports", "validation", "get", "--derivation", derivation, "--projection-derivation", projection));
        Assert.True(retained.Exit == 0, retained.ToString());
        var call = Assert.Single(stub.Calls("GetRetainedCatalogValidationReport"));
        Assert.Equal(derivation, call.Path["derivationId"]);
        Assert.Equal(projection, call.Query["projectionDerivationId"]);
        Assert.Contains("unknown-field", retained.Out, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', retained.Out);

        stub.Reply("GetCatalogProjectionReport", _ => CliStubReply.Of(200, data.ProjectionAvailable(derivation)));
        var json = await cli.RunAsync(Revision("reports", "projection", "get", "--json"));
        Assert.Equal("available", JsonNode.Parse(json.Out)!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task ComparisonResolvesEachSidesCurrentProjection()
    {
        var left = CliStubData.Id();
        var right = CliStubData.Id();
        var leftProjection = CliStubData.Id();
        var rightProjection = CliStubData.Id();
        stub.Reply("GetCatalogProjectionReport", request => CliStubReply.Of(200,
            data.ProjectionAvailable(request.Path["revisionId"] == left ? leftProjection : rightProjection, request.Path["revisionId"])));
        stub.Reply("GetCatalogRevisionComparison", _ => CliStubReply.Of(200, data.Comparison(left, right, leftProjection, rightProjection)));

        var compared = await cli.RunAsync("compare", "--project", data.Project, "--mapping", data.Mapping, "--left-revision", left,
            "--right-revision", right);
        Assert.True(compared.Exit == 0, compared.ToString());
        var query = Assert.Single(stub.Calls("GetCatalogRevisionComparison")).Query;
        Assert.Equal(leftProjection, query["leftProjection"]);
        Assert.Equal(rightProjection, query["rightProjection"]);
        Assert.False(query.ContainsKey("leftValidation"));
        Assert.Contains("field-total", compared.Out, StringComparison.Ordinal);
        Assert.Contains("dataType", compared.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("field-id", compared.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewsAreSubmittedOnceWaitedForAndDownloadedWithDigestVerification()
    {
        var primary = Path.Combine(cli.ConfigurationDirectory, "order.json");
        var lookup = Path.Combine(cli.ConfigurationDirectory, "lookup.json");
        await File.WriteAllTextAsync(primary, "{\"id\":1}");
        await File.WriteAllTextAsync(lookup, "[1,2]");
        var run = CliStubData.Id();
        var content = Encoding.UTF8.GetBytes("{\"out\":true}");
        var output = new CatalogPreviewOutputV1(0, "primary", "Result", false, "application/json", "1",
            Convert.ToHexStringLower(SHA256.HashData(content)), content.Length);
        stub.Enqueue("SubmitCatalogPreview", CliStubReply.Of(201, new CatalogPreviewSubmitResultV1("created", data.PreviewRun(run, "queued"))));
        stub.Enqueue("GetCatalogPreview", CliStubReply.Of(200, data.PreviewRun(run, "running")));
        stub.Reply("GetCatalogPreview", _ => CliStubReply.Of(200, data.PreviewRun(run, "completed", "succeeded", [output])));

        var created = await cli.RunAsync(Revision("previews", "create", "--input", primary, "--named-input", "lookup=" + lookup,
            "--parameter", "limit:integer=5", "--parameter", "region=EU", "--target", "Result", "--wait"));
        Assert.True(created.Exit == 0, created.ToString());
        Assert.Contains("succeeded", created.Out, StringComparison.Ordinal);
        var submitted = Assert.Single(stub.Calls("SubmitCatalogPreview"));
        Assert.True(Guid.TryParse(submitted.Headers["Idempotency-Key"], out _));
        var body = submitted.Json!;
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"id\":1}")), body["inputs"]![0]!["content"]!.GetValue<string>());
        Assert.Null(body["inputs"]![0]!["name"]);
        Assert.Equal("lookup", body["inputs"]![1]!["name"]!.GetValue<string>());
        Assert.Equal("integer", body["parameters"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("string", body["parameters"]![1]!["kind"]!.GetValue<string>());
        Assert.Equal("named", body["target"]!["kind"]!.GetValue<string>());

        stub.Reply("GetCatalogPreviewOutput", _ => new CliStubReply
        {
            Bytes = content,
            Headers = { ["Content-Digest"] = "sha-256=:" + Convert.ToBase64String(SHA256.HashData(content)) + ":" }
        });
        var target = Path.Combine(cli.ConfigurationDirectory, "result.json");
        var download = await cli.RunAsync("previews", "download", run, "--output", target);
        Assert.True(download.Exit == 0, download.ToString());
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        var piped = await cli.RunAsync("previews", "download", run, "--output", "-");
        Assert.Equal(content, piped.Binary);

        var unwritable = await cli.RunAsync("previews", "download", run, "--output", Path.Combine(cli.ConfigurationDirectory, "missing", "x.json"));
        Assert.Equal(CliExitCode.Usage, unwritable.Code);

        stub.Reply("GetCatalogPreviewOutput", _ => new CliStubReply
        {
            Bytes = Encoding.UTF8.GetBytes("{\"out\":fals}"),
            Headers = { ["Content-Digest"] = "sha-256=:" + Convert.ToBase64String(SHA256.HashData(content)) + ":" }
        });
        var tampered = Path.Combine(cli.ConfigurationDirectory, "tampered.json");
        var mismatch = await cli.RunAsync("previews", "download", run, "--output", tampered);
        Assert.Equal(CliExitCode.Transport, mismatch.Code);
        Assert.False(File.Exists(tampered));
        Assert.Empty(Directory.GetFiles(cli.ConfigurationDirectory, "*.partial"));
        var corruptStdout = await cli.RunAsync("previews", "download", run, "--output", "-");
        Assert.Equal(CliExitCode.Transport, corruptStdout.Code);
        Assert.Empty(corruptStdout.Binary);

        stub.Reply("CancelCatalogPreview", _ => CliStubReply.Of(200, new CatalogPreviewCancelResultV1("finished", data.PreviewRun(run, "completed", "succeeded", [output]))));
        var cancel = await cli.RunAsync("previews", "cancel", run);
        Assert.Contains("cancellation finished", cancel.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedPreviewRunExitsWithTheOperationFailureCode()
    {
        var input = Path.Combine(cli.ConfigurationDirectory, "order.json");
        await File.WriteAllTextAsync(input, "{}");
        var run = CliStubData.Id();
        stub.Enqueue("SubmitCatalogPreview", CliStubReply.Of(200, new CatalogPreviewSubmitResultV1("replayed", data.PreviewRun(run, "queued"))));
        stub.Reply("GetCatalogPreview", _ => CliStubReply.Of(200, data.PreviewRun(run, "completed", "executionFailed")));
        var result = await cli.RunAsync(Revision("previews", "create", "--input", input, "--request-id", run, "--wait"));
        Assert.Equal(CliExitCode.OperationFailed, result.Code);
        Assert.Equal(run, stub.Calls("SubmitCatalogPreview")[0].Headers["Idempotency-Key"]);
        var badParameter = await cli.RunAsync(Revision("previews", "create", "--input", input, "--parameter", "x:date=1"));
        Assert.Equal(CliExitCode.Usage, badParameter.Code);
    }

    [Fact]
    public async Task RetryingAnApprovalCommandAfterALostResponseReusesItsExactDefaultBuild()
    {
        var approval = CliStubData.Id();
        var environment = CliStubData.Id();
        byte[]? original = null;
        stub.Reply("PutBuildApproval", request =>
        {
            if (original is null)
            {
                original = request.Body;
                // Model a committed approval whose acknowledgement never reached this CLI invocation.
                return CliStubReply.Error(500, CatalogErrorCodeV1.PersistenceFailure);
            }
            if (!original.AsSpan().SequenceEqual(request.Body))
                return CliStubReply.Error(409, CatalogErrorCodeV1.IdempotencyConflict);
            return CliStubReply.Of(200, new BuildApprovalReceiptV1("replayed",
                data.Approval(approval, request.Json!["buildId"]!.GetValue<string>(), environment)));
        });
        var args = new[] { "builds", "approvals", "create", "--project", data.Project, "--mapping", data.Mapping,
            "--revision", data.Revision, "--environment", environment, "--environment-version", "1", "--evidence", CliStubData.Id(),
            "--approval-id", approval };
        Assert.Equal(CliExitCode.ServerFailure, (await cli.RunAsync(args)).Code);
        var retried = await cli.RunAsync(args);
        Assert.True(retried.Exit == 0, retried.ToString());
        var calls = stub.Calls("PutBuildApproval");
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Body, calls[1].Body);
        Assert.Equal(calls[0].Path["approvalId"], calls[1].Path["approvalId"]);
        var derived = calls[0].Json!["buildId"]!.GetValue<string>();
        Assert.NotEqual(approval, derived);
        Assert.NotEqual(Weft.Cli.Commands.BuildCommands.BuildForApproval(CliStubData.Id()), derived);
        args[Array.IndexOf(args, "--evidence") + 1] = CliStubData.Id();
        Assert.Equal(CliExitCode.Conflict, (await cli.RunAsync(args)).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha-256=:invalid:")]
    [InlineData("sha-256=:YQ==:")]
    [InlineData("sha-256=:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=:")]
    [InlineData("sha-256=:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=:, sha-256=:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=:")]
    public async Task DownloadsRefuseMissingMalformedOrConflictingDigestHeadersWithoutReplacingTheDestination(string? header)
    {
        var content = Encoding.UTF8.GetBytes("verified output");
        var run = CliStubData.Id();
        var output = new CatalogPreviewOutputV1(0, "primary", "Result", false, "application/json", "1",
            Convert.ToHexStringLower(SHA256.HashData(content)), content.Length);
        stub.Reply("GetCatalogPreview", _ => CliStubReply.Of(200, data.PreviewRun(run, "completed", "succeeded", [output])));
        stub.Reply("GetCatalogPreviewOutput", _ =>
        {
            var reply = new CliStubReply { Bytes = content };
            if (header is not null)
                reply.Headers["Content-Digest"] = header;
            return reply;
        });
        var destination = Path.Combine(cli.ConfigurationDirectory, "existing.json");
        await File.WriteAllTextAsync(destination, "keep the existing file");
        var result = await cli.RunAsync("previews", "download", run, "--output", destination);
        Assert.Equal(CliExitCode.Transport, result.Code);
        Assert.Contains("SHA-256 digest", result.Error, StringComparison.Ordinal);
        Assert.Equal("keep the existing file", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(cli.ConfigurationDirectory, "*.partial"));
        var piped = await cli.RunAsync("previews", "download", run, "--output", "-");
        Assert.Equal(CliExitCode.Transport, piped.Code);
        Assert.Empty(piped.Binary);
    }

    [Fact]
    public async Task RegressionCasesAreSavedAndRunsAreWaitedForUntilTheirComparison()
    {
        var definition = Path.Combine(cli.ConfigurationDirectory, "case.json");
        await File.WriteAllBytesAsync(definition, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data.CaseDefinition, Weft.Cli.Http.WeftApiClient.Json));
        var caseId = CliStubData.Id();
        stub.Reply("PutRegressionCaseVersion", request => CliStubReply.Of(201,
            new RegressionCaseSavedV1(request.Path["caseId"], request.Path["version"], data.RevisionScope(), "created")));
        var saved = await cli.RunAsync("regression", "cases", "create", "--project", data.Project, "--mapping", data.Mapping,
            "--definition", definition, "--case-id", caseId);
        Assert.True(saved.Exit == 0, saved.ToString());
        Assert.Equal("1", Assert.Single(stub.Calls("PutRegressionCaseVersion")).Path["version"]);
        Assert.Equal("application/vnd.weft.regression-case.v1+json", stub.Calls("PutRegressionCaseVersion")[0].Headers["Content-Type"]);

        stub.Reply("PutRegressionRun", request => CliStubReply.Of(201, new RegressionRunReceiptV1(request.Path["runId"], "created")));
        stub.Enqueue("GetRegressionRun", CliStubReply.Of(200, data.RegressionRun("00000000-0000-0000-0000-000000000000", caseId, "running", null)));
        stub.Reply("GetRegressionRun", request => CliStubReply.Of(200, data.RegressionRun(request.Path["runId"], caseId, "completed", "passed")));
        var passed = await cli.RunAsync("regression", "runs", "create", "--project", data.Project, "--mapping", data.Mapping, "--case", caseId,
            "--version", "1", "--wait");
        Assert.True(passed.Exit == 0, passed.ToString());
        var put = Assert.Single(stub.Calls("PutRegressionRun"));
        Assert.Equal(caseId, put.Json!["caseId"]!.GetValue<string>());
        Assert.Equal("1", put.Json!["version"]!.GetValue<string>());

        stub.Reply("GetRegressionRun", request => CliStubReply.Of(200, data.RegressionRun(request.Path["runId"], caseId, "completed", "failed")));
        var failed = await cli.RunAsync("regression", "runs", "create", "--project", data.Project, "--mapping", data.Mapping, "--case", caseId,
            "--version", "1", "--wait");
        Assert.Equal(CliExitCode.OperationFailed, failed.Code);
        Assert.Contains("bytesDiffer", failed.Out, StringComparison.Ordinal);

        stub.Enqueue("GetRegressionCases", CliStubReply.Of(200, new RegressionCasePageV1(
            [new RegressionCaseMetadataV1(caseId, "1", data.RevisionScope(), "Orders smoke", "2026-10-20T12:00:00Z")], caseId)),
            CliStubReply.Of(200, new RegressionCasePageV1([], null)));
        var cases = await cli.RunAsync("regression", "cases", "list", "--project", data.Project, "--mapping", data.Mapping, "--all", "--limit", "1");
        Assert.True(cases.Exit == 0, cases.ToString());
        Assert.Equal(caseId, stub.Calls("GetRegressionCases")[^1].Query["afterCaseId"]);
        var invalid = await cli.RunWithInputAsync(Encoding.UTF8.GetBytes("{\"name\":1}"), "regression", "cases", "create", "--project", data.Project,
            "--mapping", data.Mapping, "--definition", "-");
        Assert.Equal(CliExitCode.Usage, invalid.Code);
    }

    [Fact]
    public async Task BuildsAreApprovedSubmittedFromTheApprovalAndWaitedFor()
    {
        var environment = CliStubData.Id();
        var approval = CliStubData.Id();
        var build = CliStubData.Id();
        var evidence = CliStubData.Id();
        stub.Reply("PutBuildApproval", request => CliStubReply.Of(201, new BuildApprovalReceiptV1("created",
            data.Approval(request.Path["approvalId"], request.Json!["buildId"]!.GetValue<string>(), environment))));
        var approved = await cli.RunAsync("builds", "approvals", "create", "--project", data.Project, "--mapping", data.Mapping,
            "--build", build, "--revision", data.Revision, "--environment", environment, "--environment-version", "1", "--evidence", evidence,
            "--approval-id", approval);
        Assert.True(approved.Exit == 0, approved.ToString());
        var put = Assert.Single(stub.Calls("PutBuildApproval")).Json!;
        Assert.Equal(evidence, put["regressionEvidenceId"]!.GetValue<string>());
        Assert.Equal("1", put["environmentVersion"]!.GetValue<string>());

        stub.Reply("GetBuildApproval", _ => CliStubReply.Of(200, data.Approval(approval, build, environment)));
        stub.Reply("PutBuildRun", request => CliStubReply.Of(201, new BuildRunReceiptV1("created", data.BuildRun(request.Path["buildId"], approval, "queued"))));
        stub.Enqueue("GetBuildRun", CliStubReply.Of(200, data.BuildRun(build, approval, "running")));
        stub.Reply("GetBuildRun", _ => CliStubReply.Of(200, data.BuildRun(build, approval, "succeeded")));
        var built = await cli.RunAsync("builds", "runs", "create", "--project", data.Project, "--mapping", data.Mapping, "--approval", approval, "--wait");
        Assert.True(built.Exit == 0, built.ToString());
        Assert.Equal(build, Assert.Single(stub.Calls("PutBuildRun")).Path["buildId"]);
        Assert.Contains(new string('f', 64), built.Out, StringComparison.Ordinal);

        stub.Enqueue("GetBuildEnvironments", CliStubReply.Of(200, new BuildEnvironmentPageV1([data.Environment(environment, "1")], environment, "1")),
            CliStubReply.Of(200, new BuildEnvironmentPageV1([data.Environment(environment, "2")], null, null)));
        var environments = await cli.RunAsync("builds", "environments", "list", "--project", data.Project, "--all");
        Assert.True(environments.Exit == 0, environments.ToString());
        var next = stub.Calls("GetBuildEnvironments")[^1].Query;
        Assert.Equal(environment, next["afterEnvironmentId"]);
        Assert.Equal("1", next["afterVersion"]);

        stub.Reply("RevokeBuildApproval", _ => CliStubReply.Of(200, data.Approval(approval, build, environment) with { Revoked = true }));
        var revoked = await cli.RunAsync("builds", "approvals", "revoke", "--project", data.Project, "--mapping", data.Mapping, approval);
        Assert.Contains("revoked: yes", revoked.Out, StringComparison.Ordinal);
        stub.Enqueue("PutBuildRun", CliStubReply.Error(412, CatalogErrorCodeV1.PreconditionFailed));
        var refused = await cli.RunAsync("builds", "runs", "create", "--project", data.Project, "--mapping", data.Mapping, "--approval", approval,
            "--build", build);
        Assert.Equal(CliExitCode.PreconditionFailed, refused.Code);
    }
}
