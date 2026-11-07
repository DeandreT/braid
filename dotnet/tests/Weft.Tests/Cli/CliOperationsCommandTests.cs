using Weft.Cli;
using Weft.Contracts.Operations;

namespace Weft.Tests.Cli;

public sealed class CliOperationsCommandTests : IAsyncLifetime
{
    private CliTestIssuer issuer = null!;
    private CliApiStub stub = null!;
    private CliHarness cli = null!;
    private CliStubData data = null!;
    public async Task InitializeAsync()
    {
        issuer = await CliTestIssuer.StartAsync(); stub = await CliApiStub.StartAsync(issuer);
        cli = new CliHarness(issuer, stub.Handler); data = new CliStubData(stub.OrganizationId);
        await cli.ConfigureAsync(); await cli.LoginAsync();
    }
    public async Task DisposeAsync()
    {
        stub.AssertClean(); cli.Dispose(); await stub.DisposeAsync(); await issuer.DisposeAsync();
    }

    [Fact]
    public async Task Conditional_actions_send_the_original_id_body_and_version_without_browser_credentials()
    {
        var operation = CliStubData.Id(); var target = CliStubData.Id(); var environment = CliStubData.Id();
        stub.Reply("ApplyReleaseOperation", _ => CliStubReply.Of(200, new ReleaseOperationReceiptV1(operation, "applied")));
        string[] args = ["operations", "disable", "--project", data.Project, "--mapping", data.Mapping, "--target", target,
            "--environment", environment, "--environment-version", "1", "--version", "4", "--operation", operation];
        var first = await cli.RunAsync(args); var replay = await cli.RunAsync(args);
        Assert.Equal(CliExitCode.Success, first.Code); Assert.Equal(CliExitCode.Success, replay.Code);
        var calls = stub.Calls("ApplyReleaseOperation"); Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Body, calls[1].Body);
        Assert.All(calls, call =>
        {
            Assert.Equal(operation, call.Path["operationId"]); Assert.Equal(target, call.Path["targetId"]);
            Assert.Equal("\"4\"", call.Headers["If-Match"]); Assert.Equal("disable", call.Json!["action"]!.GetValue<string>());
            Assert.Null(call.Json!["qualificationId"]); Assert.Null(call.Json!["sourceReleaseId"]);
            Assert.DoesNotContain("Cookie", call.Headers.Keys); Assert.DoesNotContain("X-XSRF-TOKEN", call.Headers.Keys);
        });
    }

    [Fact]
    public async Task Summary_and_paged_history_pass_only_the_selected_filters()
    {
        var target = CliStubData.Id(); var caller = CliStubData.Id();
        stub.Reply("GetReleaseSummary", _ => CliStubReply.Of(200, new ReleaseOperationsSummaryV1(3, 1, 1, 1, new Dictionary<string, long> { ["executionFailed"] = 1 }, 20, 30)));
        var summary = await cli.RunAsync("operations", "summary", "--project", data.Project, "--mapping", data.Mapping, "--target", target, "--caller", caller, "--json");
        Assert.Equal(CliExitCode.Success, summary.Code); Assert.Contains("executionFailed", summary.Out);
        Assert.Equal(caller, Assert.Single(stub.Calls("GetReleaseSummary")).Query["caller"]);
        stub.Reply("GetReleaseOperations", _ => CliStubReply.Of(200, new ReleaseOperationsPageV1([], 10)));
        var history = await cli.RunAsync("operations", "history", "--project", data.Project, "--mapping", data.Mapping, "--target", target, "--offset", "5");
        Assert.Equal(CliExitCode.Success, history.Code); Assert.Contains("--offset 10", history.Out);
        Assert.Equal("history", Assert.Single(stub.Calls("GetReleaseOperations")).Path["section"]);
    }
}
