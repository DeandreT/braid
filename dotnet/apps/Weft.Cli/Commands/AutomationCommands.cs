using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Cli.Http;
using Weft.Contracts.Automation;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class AutomationCommands
{
    public static Command Automation(CliGlobals globals) => new Command("automation", "Automatic regression, exact-revision build policy and retained suite results").With(
        Read<AutomationPolicyV1>(globals, "policy", ApiOperations.GetAutomationPolicy),
        Write<AutomationPolicyV1>(globals, "set-policy", ApiOperations.PutAutomationPolicy),
        Read<MappingAutomationV1>(globals, "mapping", ApiOperations.GetMappingAutomation, mapping: true),
        Write<MappingAutomationV1>(globals, "set-mapping", ApiOperations.PutMappingAutomation, resource: true),
        Read<RegressionSuitesV1>(globals, "suites", ApiOperations.GetRegressionSuites, mapping: true, page: true),
        Read<RegressionSuiteV1>(globals, "suite", ApiOperations.GetRegressionSuite, mapping: true),
        Write<RegressionRecheckV1>(globals, "recheck", ApiOperations.PutRegressionRecheck, resource: true),
        Read<RegressionEvaluationsV1>(globals, "cases", ApiOperations.GetRegressionEvaluations, mapping: true, page: true));

    public static Command Notifications(CliGlobals globals) => new Command("notifications", "Project notification centre, subscriptions and delivery history").With(
        Read<NotificationsV1>(globals, "inbox", ApiOperations.GetNotificationInbox, page: true, projectScope: false),
        Write<NotificationReadV1>(globals, "inbox-read", ApiOperations.ReadInboxNotification, resource: true, empty: true, projectScope: false, resourceName: "notificationId"),
        Read<NotificationsV1>(globals, "list", ApiOperations.GetNotifications, page: true),
        Read<NotificationSubscriptionV1>(globals, "subscription", ApiOperations.GetNotificationSubscription),
        Write<NotificationSubscriptionV1>(globals, "subscribe", ApiOperations.PutNotificationSubscription),
        Read<NotificationOptionsV1>(globals, "options", ApiOperations.GetNotificationOptions),
        Read<NotificationDeliveriesV1>(globals, "deliveries", ApiOperations.GetNotificationDeliveries, page: true),
        Read<NotificationDestinationsV1>(globals, "destinations", ApiOperations.GetNotificationDestinations),
        Write<NotificationDestinationEnableV1>(globals, "enable-destination", ApiOperations.EnableNotificationDestination),
        Write<NotificationReadV1>(globals, "mark-read", ApiOperations.ReadNotification, resource: true, empty: true));

    private static Command Read<T>(CliGlobals globals, string name, ApiOperation operation, bool mapping = false, bool page = false, bool projectScope = true)
    {
        var project = Required("--project", "Project ID");
        var resource = Required(operation == ApiOperations.GetRegressionEvaluations ? "--suite" :
            operation == ApiOperations.GetRegressionSuites || operation == ApiOperations.GetMappingAutomation ? "--mapping" : "--revision", "Mapping, suite or exact revision ID");
        var after = Optional("--after", "Continue after the previous page's ID");
        var limit = RegressionCommands.Limit();
        var command = new Command(name, operation.OperationId);
        if (projectScope) command.Options.Add(project);
        if (mapping) command.Options.Add(resource);
        if (page) command.With(after, limit);
        command.Handle(globals, async invocation =>
        {
            var request = await invocation.RequestAsync(operation);
            if (projectScope) request.Path("projectId", invocation.Id(project));
            if (mapping) request.Path("resourceId", invocation.Id(resource));
            if (page) request.Query("afterId", invocation.OptionalId(after)).Query("limit", RegressionCommands.LimitValue(invocation, limit));
            var result = await invocation.Api.ReadAsync<T>(request, invocation.Cancellation);
            invocation.Output.Write(result, output => output.Line(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })));
        });
        return command;
    }

    private static Command Write<T>(CliGlobals globals, string name, ApiOperation operation, bool resource = false, bool empty = false,
        bool projectScope = true, string resourceName = "resourceId")
    {
        var project = Required("--project", "Project ID");
        var id = Required("--id", "Mapping or notification ID");
        var definition = Required("--definition", "Bounded JSON definition file, or - for stdin; never include secrets");
        var command = new Command(name, operation.OperationId);
        if (projectScope) command.Options.Add(project);
        if (resource) command.Options.Add(id);
        if (!empty) command.Options.Add(definition);
        command.Handle(globals, async invocation =>
        {
            var bytes = empty ? "{}"u8.ToArray() : await ReadInputAsync(invocation, invocation.Value(definition)!, 8192, "automation definition");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            var expected = typeof(T).GetProperties().Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Order().ToArray();
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(expected))
                throw new ArgumentException("Automation definition must contain exactly the declared fields.");
            var body = JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 });
            if (body is null) throw new ArgumentException("Automation definition is required.");
            var request = await invocation.RequestAsync(operation);
            if (projectScope) request.Path("projectId", invocation.Id(project));
            if (resource) request.Path(resourceName, invocation.Id(id));
            request.Body(CatalogCommands.JsonBody(body, operation));
            var result = await invocation.Api.ReadAsync<AutomationSavedV1>(request, invocation.Cancellation);
            invocation.Output.Write(result, output => output.Line(result.Saved ? "Saved." : "Not saved."));
        });
        return command;
    }
}
