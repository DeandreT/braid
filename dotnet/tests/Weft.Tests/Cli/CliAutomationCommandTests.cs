using System.Text.Json;
using Weft.Cli;
using Weft.Contracts.Automation;
using Weft.Contracts.Catalog;

namespace Weft.Tests.Cli;

public sealed class CliAutomationCommandTests : IAsyncLifetime
{
    private CliTestIssuer issuer = null!;
    private CliApiStub stub = null!;
    private CliHarness cli = null!;
    private readonly string project = Guid.NewGuid().ToString("D");
    private readonly string mapping = Guid.NewGuid().ToString("D");
    private readonly string revision = Guid.NewGuid().ToString("D");
    private readonly string suite = Guid.NewGuid().ToString("D");

    public async Task InitializeAsync()
    {
        issuer = await CliTestIssuer.StartAsync(); stub = await CliApiStub.StartAsync(issuer);
        cli = new(issuer, stub.Handler); await cli.ConfigureAsync(); await cli.LoginAsync();
    }
    public async Task DisposeAsync()
    {
        stub.AssertClean(); cli.Dispose(); await stub.DisposeAsync(); await issuer.DisposeAsync();
    }

    [Fact]
    public async Task Incomplete_definitions_never_send_defaulted_policy_or_mapping_updates()
    {
        var invalid = new[]
        {
            ("set-mapping", "{}"),
            ("set-policy", "{}"),
            ("set-policy", "{\"errorRatePercent\":20,\"minimumRequests\":10,\"windowSeconds\":300}"),
            ("set-policy", "{\"requirePassingRegression\":true,\"minimumRequests\":10,\"windowSeconds\":300}"),
            ("set-policy", "{\"requirePassingRegression\":true,\"errorRatePercent\":20,\"windowSeconds\":300}"),
            ("set-policy", "{\"requirePassingRegression\":true,\"errorRatePercent\":20,\"minimumRequests\":10}")
        };
        foreach (var (command, body) in invalid)
        {
            var path = Path.Combine(cli.ConfigurationDirectory, "incomplete.json");
            await File.WriteAllTextAsync(path, body);
            var args = new List<string> { "automation", command, "--project", project, "--definition", path };
            if (command == "set-mapping") args.AddRange(["--id", mapping]);
            Assert.NotEqual(0, (await cli.RunAsync(args.ToArray())).Exit);
        }
        Assert.Empty(stub.Calls("PutAutomationPolicy"));
        Assert.Empty(stub.Calls("PutMappingAutomation"));
    }

    [Fact]
    public async Task Every_automation_and_notification_read_uses_the_documented_scope_and_response()
    {
        var value = new RegressionSuiteV1(suite, revision, "passed", "2026-10-25T12:00:00Z", 1, 1, 0, 0);
        var reads = new (string Operation, string[] Command, object Reply)[]
        {
            ("GetAutomationPolicy",["automation","policy"],new AutomationPolicyV1(true,20,10,300)),
            ("GetMappingAutomation",["automation","mapping","--mapping",mapping],new MappingAutomationV1(true)),
            ("GetRegressionSuites",["automation","suites","--mapping",mapping],new RegressionSuitesV1([value],null)),
            ("GetRegressionSuite",["automation","suite","--revision",revision],value),
            ("GetRegressionEvaluations",["automation","cases","--suite",suite],new RegressionEvaluationsV1([],null)),
            ("GetNotifications",["notifications","list"],new NotificationsV1([],null)),
            ("GetNotificationSubscription",["notifications","subscription"],new NotificationSubscriptionV1(["revisionUploaded"],true,false,[])),
            ("GetNotificationOptions",["notifications","options"],new NotificationOptionsV1(["revisionUploaded"],false,[])),
            ("GetNotificationDeliveries",["notifications","deliveries"],new NotificationDeliveriesV1([],null)),
            ("GetNotificationDestinations",["notifications","destinations"],new NotificationDestinationsV1([]))
        };
        foreach (var item in reads)
        {
            stub.Reply(item.Operation, _ => CliStubReply.Of(200, item.Reply));
            var result = await cli.RunAsync([.. item.Command, "--project", project, "--json"]);
            Assert.True(result.Exit == 0, result.ToString());
            using var body = JsonDocument.Parse(result.Out); Assert.Equal(JsonValueKind.Object, body.RootElement.ValueKind);
            Assert.Equal(project, Assert.Single(stub.Calls(item.Operation)).Path["projectId"]);
        }
    }

    [Fact]
    public async Task Every_mutation_and_recheck_retry_keeps_only_the_original_metadata_and_resource_id()
    {
        var writes = new (string Operation, string[] Command, object Body)[]
        {
            ("PutAutomationPolicy",["automation","set-policy"],new AutomationPolicyV1(true,20,10,300)),
            ("PutMappingAutomation",["automation","set-mapping","--id",mapping],new MappingAutomationV1(false)),
            ("PutRegressionRecheck",["automation","recheck","--id",suite],new RegressionRecheckV1(mapping,revision)),
            ("PutNotificationSubscription",["notifications","subscribe"],new NotificationSubscriptionV1(["revisionUploaded"],true,false,[])),
            ("EnableNotificationDestination",["notifications","enable-destination"],new NotificationDestinationEnableV1("webhook:operations"))
        };
        foreach (var item in writes)
        {
            stub.Enqueue(item.Operation, CliStubReply.Error(503, CatalogErrorCodeV1.Unavailable, ("Retry-After", "1")), CliStubReply.Of(200, new AutomationSavedV1(true)));
            var file = Path.Combine(cli.ConfigurationDirectory, item.Operation + ".json");
            await File.WriteAllBytesAsync(file, JsonSerializer.SerializeToUtf8Bytes(item.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var result = await cli.RunAsync([.. item.Command, "--project", project, "--definition", file, "--json"]);
            Assert.True(result.Exit == 0, result.ToString());
            var calls = stub.Calls(item.Operation); Assert.Equal(2, calls.Count);
            Assert.Equal(calls[0].Body, calls[1].Body); Assert.Equal(calls[0].Path, calls[1].Path);
            Assert.False(calls[0].Headers.ContainsKey("X-XSRF-TOKEN"));
        }
        stub.Reply("ReadNotification", _ => CliStubReply.Of(200, new AutomationSavedV1(true)));
        var marked = await cli.RunAsync("notifications", "mark-read", "--project", project, "--id", Guid.NewGuid().ToString("D"));
        Assert.True(marked.Exit == 0, marked.ToString());
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(Assert.Single(stub.Calls("ReadNotification")).Body));
    }

    [Fact]
    public async Task Organization_inbox_and_read_marker_do_not_require_or_invent_a_project()
    {
        stub.Reply("GetNotificationInbox", _ => CliStubReply.Of(200, new NotificationsV1([], null)));
        var read = await cli.RunAsync("notifications", "inbox", "--json");
        Assert.True(read.Exit == 0, read.ToString());
        Assert.False(Assert.Single(stub.Calls("GetNotificationInbox")).Path.ContainsKey("projectId"));
        stub.Reply("ReadInboxNotification", _ => CliStubReply.Of(200, new AutomationSavedV1(true)));
        var id = Guid.NewGuid().ToString("D");
        var marked = await cli.RunAsync("notifications", "inbox-read", "--id", id);
        Assert.True(marked.Exit == 0, marked.ToString());
        Assert.Equal(id, Assert.Single(stub.Calls("ReadInboxNotification")).Path["notificationId"]);
    }
}
