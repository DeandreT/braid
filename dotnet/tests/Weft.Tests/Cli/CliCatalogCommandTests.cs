using System.Text;
using System.Text.Json.Nodes;
using Weft.Cli;
using Weft.Contracts.Catalog;
using Weft.Contracts.Help;

namespace Weft.Tests.Cli;

/// <summary>Catalog, upload and metadata commands end to end against the OpenAPI stub, signed in through the owned issuer.</summary>
public sealed class CliCatalogCommandTests : IAsyncLifetime
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

    [Fact]
    public async Task ProjectListsFollowCursorsAndWriteTablesOrTheExactDocument()
    {
        var second = CliStubData.Id();
        stub.Reply("GetCatalogProjects", request => request.Query.ContainsKey("cursor")
            ? CliStubReply.Of(200, new CatalogProjectPageV1([data.ProjectValue("Beta \u001b]0;title\u0007", second)]))
            : CliStubReply.Of(200, new CatalogProjectPageV1([data.ProjectValue("Alpha")], "bmV4dA")));

        var first = await cli.RunAsync("projects", "list", "--limit", "1");
        Assert.True(first.Exit == 0, first.ToString());
        Assert.Contains("Alpha", first.Out, StringComparison.Ordinal);
        Assert.Contains("--cursor bmV4dA", first.Out, StringComparison.Ordinal);
        Assert.Equal("1", stub.Calls("GetCatalogProjects")[0].Query["limit"]);
        Assert.Equal(stub.OrganizationId, stub.Calls("GetCatalogProjects")[0].Path["organizationId"]);

        var all = await cli.RunAsync("projects", "list", "--all");
        Assert.True(all.Exit == 0, all.ToString());
        Assert.Contains("Alpha", all.Out, StringComparison.Ordinal);
        Assert.Contains(second, all.Out, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', all.Out);
        Assert.DoesNotContain('\u0007', all.Out);
        Assert.Equal("bmV4dA", stub.Calls("GetCatalogProjects")[^1].Query["cursor"]);

        var json = await cli.RunAsync("projects", "list", "--json");
        Assert.Equal("bmV4dA", JsonNode.Parse(json.Out)!["nextCursor"]!.GetValue<string>());
        var allJson = JsonNode.Parse((await cli.RunAsync("projects", "list", "--all", "--json")).Out)!;
        Assert.Equal(2, allJson["projects"]!.AsArray().Count);
        Assert.Null(allJson["nextCursor"]);
        var explicitOrganization = await cli.RunAsync("projects", "get", data.Project, "--organization", stub.OrganizationId.ToUpperInvariant());
        Assert.Equal(CliExitCode.ServerFailure, explicitOrganization.Code);
        Assert.Equal(stub.OrganizationId, stub.Calls("GetCatalogProject")[0].Path["organizationId"]);
    }

    [Fact]
    public async Task TheAuthenticatedGuideRendersStructuredBlocksAndExactJsonWithoutTerminalControls()
    {
        var section = new HelpSectionV1("getting-started", "Start", "The guide", [
            new HelpHeadingV1(2, "intro", "Introduction"),
            new HelpParagraphV1([new HelpTextV1("Text "), new HelpStrongV1("strong "), new HelpEmphasisV1("emphasis "),
                new HelpCodeV1("code "), new HelpLinkV1("More", "workflows", "builds")]),
            new HelpListV1(true, [new HelpListItemV1([new HelpTextV1("First")])]),
            new HelpListV1(false, [new HelpListItemV1([new HelpTextV1("Item")])]),
            new HelpCodeBlockV1("weft context\u001b[31m"),
            new HelpTableV1([new HelpTableCellV1([new HelpTextV1("Column")])],
                [new HelpTableRowV1([new HelpTableCellV1([new HelpTextV1("Cell")])])])]);
        stub.Reply("GetHelpSections", _ => CliStubReply.Of(200, new HelpSectionListV1([new(section.Id, section.Title, section.Summary)])));
        stub.Reply("GetHelpSection", _ => CliStubReply.Of(200, section));
        var list = await cli.RunAsync("guide", "list");
        Assert.True(list.Exit == 0, list.ToString());
        Assert.Contains(section.Id, list.Out, StringComparison.Ordinal);
        var read = await cli.RunAsync("guide", "get", section.Id);
        Assert.True(read.Exit == 0, read.ToString());
        Assert.Contains("Text strong emphasis code More (weft guide get workflows; heading builds)", read.Out, StringComparison.Ordinal);
        Assert.Contains("1. First", read.Out, StringComparison.Ordinal);
        Assert.Contains("- Item", read.Out, StringComparison.Ordinal);
        Assert.Contains("Cell", read.Out, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', read.Out);
        var json = await cli.RunAsync("guide", "get", section.Id, "--json");
        Assert.True(json.Exit == 0, json.ToString());
        Assert.Equal(6, JsonNode.Parse(json.Out)!["blocks"]!.AsArray().Count);
        var invalid = await cli.RunAsync("guide", "get", "../private");
        Assert.Equal(CliExitCode.Usage, invalid.Code);
        Assert.Equal(2, stub.Calls("GetHelpSection").Count);
    }

    [Fact]
    public async Task ProjectCreationRetriesABusyServerWithTheSameIdempotencyKey()
    {
        var created = new CatalogProjectCreationResultV1(CatalogProjectCreationOutcomeV1.Created,
            new CatalogProjectCreationReceiptV1(data.ProjectScope, "Orders", "All orders", data.Actor, CliStubData.Time, "1",
                CatalogRoleV1.Administrator, true, "1"), $"/api/v1/organizations/{data.Organization}/projects/{data.Project}");
        stub.Enqueue("CreateCatalogProject", CliStubReply.Error(503, CatalogErrorCodeV1.ProjectCreationAdmissionBusy),
            CliStubReply.Of(201, created));

        var result = await cli.RunAsync("projects", "create", "--name", "Orders", "--description", "All orders");
        Assert.True(result.Exit == 0, result.ToString());
        Assert.Contains($"Created project {data.Project} (Orders).", result.Out, StringComparison.Ordinal);
        var calls = stub.Calls("CreateCatalogProject");
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Headers["Idempotency-Key"], calls[1].Headers["Idempotency-Key"]);
        Assert.Equal(calls[0].Body, calls[1].Body);
        Assert.Equal("application/vnd.weft.project-create.v1+json", calls[0].Headers["Content-Type"]);
        Assert.Equal("Orders", calls[0].Json!["name"]!.GetValue<string>());
        Assert.Contains(TimeSpan.FromSeconds(2), cli.Time.Delays);
    }

    [Fact]
    public async Task RateLimitedRequestsWaitForRetryAfterAndStopAfterTheirBudget()
    {
        stub.Enqueue("GetCatalogMapping", CliStubReply.Error(429, CatalogErrorCodeV1.Unavailable, ("Retry-After", "7")),
            CliStubReply.Of(200, data.MappingValue()));
        var read = await cli.RunAsync("mappings", "get", "--project", data.Project, data.Mapping);
        Assert.True(read.Exit == 0, read.ToString());
        Assert.Contains(TimeSpan.FromSeconds(7), cli.Time.Delays);
        Assert.Contains("rate limiting requests; retrying in 7 s", read.Error, StringComparison.Ordinal);

        stub.Reply("GetCatalogMappings", _ => CliStubReply.Error(429, CatalogErrorCodeV1.Unavailable, ("Retry-After", "600")));
        var limited = await cli.RunAsync("mappings", "list", "--project", data.Project);
        Assert.Equal(CliExitCode.RateLimited, limited.Code);
        Assert.Equal(5, stub.Calls("GetCatalogMappings").Count);
        Assert.Contains(TimeSpan.FromSeconds(60), cli.Time.Delays);
        Assert.DoesNotContain(TimeSpan.FromSeconds(600), cli.Time.Delays);
    }

    [Fact]
    public async Task MappingCreationUploadsTheExactBytesAndResendsThemWhenTheSourceIsNeeded()
    {
        var file = Path.Combine(cli.ConfigurationDirectory, "orders.native.json");
        var bytes = Encoding.UTF8.GetBytes("{\"native\":true}");
        await File.WriteAllBytesAsync(file, bytes);
        stub.Reply("CreateCatalogMapping", request => stub.Calls("CreateCatalogMapping").Count == 1
            ? CliStubReply.Of(409, data.NeedsSource(request.Headers["Idempotency-Key"]))
            : CliStubReply.Of(200, data.Committed(request.Headers["Idempotency-Key"])));

        var result = await cli.RunAsync("mappings", "create", "--project", data.Project, "--name", "Orders + returns", "--backend", "ferrule",
            "--description", "Native orders", file);
        Assert.True(result.Exit == 0, result.ToString());
        Assert.Contains($"Committed revision {data.Revision} (sequence 2, valid) of mapping {data.Mapping}.", result.Out, StringComparison.Ordinal);
        var calls = stub.Calls("CreateCatalogMapping");
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal(bytes, call.Body));
        Assert.Equal(calls[0].Headers["Idempotency-Key"], calls[1].Headers["Idempotency-Key"]);
        Assert.Contains($"Upload operation {calls[0].Headers["Idempotency-Key"]}.", result.Error, StringComparison.Ordinal);
        Assert.Equal("Orders + returns", calls[0].Query["name"]);
        Assert.Equal("ferrule", calls[0].Query["backendId"]);
        Assert.Equal("orders.native.json", calls[0].Query["originalFilename"]);
        Assert.Equal("application/octet-stream", calls[0].Headers["Content-Type"]);
    }

    [Fact]
    public async Task AllPagesRejectsARepeatedServerCursorRatherThanPrintingAnIncompleteDocument()
    {
        stub.Reply("GetCatalogMappings", _ => CliStubReply.Of(200, new CatalogMappingPageV1([data.MappingValue()], "repeated")));
        var result = await cli.RunAsync("mappings", "list", "--project", data.Project, "--all", "--json");
        Assert.Equal(CliExitCode.Transport, result.Code);
        Assert.Empty(result.Out);
        Assert.Equal(2, stub.Calls("GetCatalogMappings").Count);
    }

    [Fact]
    public async Task AnUploadInProgressIsResumedUntilItCommits()
    {
        var operation = CliStubData.Id();
        stub.Enqueue("AppendCatalogRevision", CliStubReply.Of(202, data.InProgress(operation),
            ("Location", $"/api/v1/organizations/{data.Organization}/projects/{data.Project}/revision-ingestions/{operation}")));
        stub.Enqueue("ResumeCatalogRevisionIngestion", CliStubReply.Of(202, data.InProgress(operation)), CliStubReply.Of(200, data.Committed(operation)));

        var result = await cli.RunWithInputAsync(Encoding.UTF8.GetBytes("{}"), "revisions", "append", "--project", data.Project,
            "--mapping", data.Mapping, "--operation-id", operation, "--expected-head", data.Revision, "-");
        Assert.True(result.Exit == 0, result.ToString());
        var append = Assert.Single(stub.Calls("AppendCatalogRevision"));
        Assert.Equal(operation, append.Headers["Idempotency-Key"]);
        Assert.Equal("equal", append.Query["head"]);
        Assert.Equal(data.Revision, append.Query["expectedHeadRevisionId"]);
        Assert.False(append.Query.ContainsKey("originalFilename"));
        var resumes = stub.Calls("ResumeCatalogRevisionIngestion");
        Assert.Equal(2, resumes.Count);
        Assert.All(resumes, resume => Assert.Equal(operation, resume.Path["operationId"]));
        Assert.All(resumes, resume => Assert.Empty(resume.Body));
    }

    [Fact]
    public async Task UploadOutcomesMapToStableExitCodes()
    {
        var file = Path.Combine(cli.ConfigurationDirectory, "bad.json");
        await File.WriteAllTextAsync(file, "not native");
        stub.Enqueue("AppendCatalogRevision", CliStubReply.Of(422, CliStubData.Rejected(CatalogRevisionIngestionRejectionV1.UnregisteredBackend)));
        var rejected = await cli.RunAsync("revisions", "append", "--project", data.Project, "--mapping", data.Mapping, file);
        Assert.Equal(CliExitCode.InvalidRequest, rejected.Code);
        Assert.Contains("rejected the native file (unregisteredBackend)", rejected.Error, StringComparison.Ordinal);

        stub.Reply("AppendCatalogRevision", request => CliStubReply.Of(409, data.HeadConflict(request.Headers["Idempotency-Key"])));
        var conflict = await cli.RunAsync("revisions", "append", "--project", data.Project, "--mapping", data.Mapping, file);
        Assert.Equal(CliExitCode.Conflict, conflict.Code);

        var empty = await cli.RunWithInputAsync([], "revisions", "append", "--project", data.Project, "--mapping", data.Mapping, "-");
        Assert.Equal(CliExitCode.Usage, empty.Code);
        var missing = await cli.RunAsync("revisions", "append", "--project", data.Project, "--mapping", data.Mapping, file + ".missing");
        Assert.Equal(CliExitCode.Usage, missing.Code);
        var tooLarge = await cli.RunWithInputAsync(new byte[4_194_305], "revisions", "append", "--project", data.Project, "--mapping", data.Mapping, "-");
        Assert.Equal(CliExitCode.Usage, tooLarge.Code);
        Assert.Equal(2, stub.Calls("AppendCatalogRevision").Count);
    }

    [Fact]
    public async Task MetadataEditsCarryTheEntityTagJustReadAndReportAConcurrentChange()
    {
        stub.Reply("GetCatalogMappingMetadata", _ => CliStubReply.Of(200, data.Metadata("Orders", "Old description", "3"), ("ETag", "\"metadata-3\"")));
        stub.Enqueue("EditCatalogMappingMetadata", CliStubReply.Of(200, data.Metadata("Orders", "New description", "4"), ("ETag", "\"metadata-4\"")));

        var edited = await cli.RunAsync("mappings", "metadata", "edit", "--project", data.Project, data.Mapping, "--description", "New description");
        Assert.True(edited.Exit == 0, edited.ToString());
        Assert.Contains("to version 4", edited.Out, StringComparison.Ordinal);
        var patch = Assert.Single(stub.Calls("EditCatalogMappingMetadata"));
        Assert.Equal("\"metadata-3\"", patch.Headers["If-Match"]);
        Assert.Equal("Orders", patch.Json!["name"]!.GetValue<string>());
        Assert.Equal("New description", patch.Json!["description"]!.GetValue<string>());

        stub.Enqueue("EditCatalogMappingMetadata", CliStubReply.Error(412, CatalogErrorCodeV1.PreconditionFailed));
        var stale = await cli.RunAsync("mappings", "metadata", "edit", "--project", data.Project, data.Mapping, "--name", "Renamed",
            "--if-match", "\"metadata-2\"");
        Assert.Equal(CliExitCode.PreconditionFailed, stale.Code);
        Assert.Equal("\"metadata-2\"", stub.Calls("EditCatalogMappingMetadata")[^1].Headers["If-Match"]);

        var shown = await cli.RunAsync("mappings", "metadata", "get", "--project", data.Project, data.Mapping, "--json");
        Assert.Equal("\"metadata-3\"", JsonNode.Parse(shown.Out)!["eTag"]!.GetValue<string>());
        var weak = await cli.RunAsync("mappings", "metadata", "edit", "--project", data.Project, data.Mapping, "--name", "x", "--if-match", "W/\"a\"");
        Assert.Equal(CliExitCode.Usage, weak.Code);
    }

    [Fact]
    public async Task IngestionReceiptsAndRevisionsAreReadable()
    {
        var operation = CliStubData.Id();
        stub.Reply("GetCatalogRevisionIngestionReceipt", _ => CliStubReply.Of(200, data.Receipt(operation, CatalogRevisionIngestionPhaseV1.Committed)));
        var status = await cli.RunAsync("ingestions", "status", "--project", data.Project, operation);
        Assert.True(status.Exit == 0, status.ToString());
        Assert.Contains("committed", status.Out, StringComparison.Ordinal);

        stub.Reply("GetCatalogRevisions", _ => CliStubReply.Of(200, new CatalogRevisionPageV1([data.RevisionValue()])));
        stub.Reply("GetCatalogRevision", _ => CliStubReply.Of(200, data.RevisionValue()));
        var list = await cli.RunAsync("revisions", "list", "--project", data.Project, "--mapping", data.Mapping);
        Assert.Contains(data.Revision, list.Out, StringComparison.Ordinal);
        var get = await cli.RunAsync("revisions", "get", "--project", data.Project, "--mapping", data.Mapping, data.Revision, "--json");
        Assert.Equal(data.Revision, JsonNode.Parse(get.Out)!["scope"]!["revisionId"]!.GetValue<string>());

        stub.Enqueue("GetCatalogRevision", CliStubReply.Error(404, CatalogErrorCodeV1.NotFound));
        var missing = await cli.RunAsync("revisions", "get", "--project", data.Project, "--mapping", data.Mapping, data.Revision);
        Assert.Equal(CliExitCode.NotFound, missing.Code);
        stub.Enqueue("GetCatalogRevision", CliStubReply.Error(404, CatalogErrorCodeV1.Unavailable));
        var disabled = await cli.RunAsync("revisions", "get", "--project", data.Project, "--mapping", data.Mapping, data.Revision);
        Assert.Equal(CliExitCode.Unavailable, disabled.Code);
        var invalid = await cli.RunAsync("revisions", "get", "--project", "not-a-uuid", "--mapping", data.Mapping, data.Revision);
        Assert.Equal(CliExitCode.Usage, invalid.Code);
    }
}
