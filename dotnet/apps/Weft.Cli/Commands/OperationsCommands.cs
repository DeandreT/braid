using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Weft.Cli.Http;
using Weft.Contracts.Operations;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class OperationsCommands
{
    internal static Command Operations(CliGlobals globals)
    {
        var root = new Command("operations", "Release targets, invocation monitoring and machine caller grants");
        foreach (var section in new[] { "targets", "releases", "history", "actions", "invocations", "callers", "summary" })
        {
            var project = Required("--project", "Project ID");
            var mapping = Required("--mapping", "Mapping ID");
            var target = new Option<string?>("--target") { Required = section != "targets", Description = "Target ID" };
            var offset = new Option<int>("--offset") { Description = "Page offset from the preceding response" };
            var limit = RegressionCommands.Limit();
            var since = Optional("--since", "Window start, ISO 8601 round-trip timestamp");
            var until = Optional("--until", "Exclusive window end, ISO 8601 round-trip timestamp");
            var caller = Optional("--caller", "Filter by caller client application ID");
            var outcome = Optional("--outcome", "Filter by recorded outcome");
            var command = new Command(section, "Read " + section).With(project, mapping, target, offset, limit, since, until, caller, outcome);
            command.Handle(globals, async invocation =>
            {
                var id = invocation.Value(target);
                if (id is not null) id = CliInvocation.Id(id, "--target");
                var selected = section == "summary" ? ApiOperations.GetReleaseSummary : section == "targets" && id is null ? ApiOperations.GetReleaseTargets : ApiOperations.GetReleaseOperations;
                var request = (await invocation.RequestAsync(selected)).Path("projectId", invocation.Id(project)).Path("mappingId", invocation.Id(mapping));
                if (id is not null) request.Path("targetId", id);
                if (selected == ApiOperations.GetReleaseOperations) request.Path("section", section);
                if (invocation.Value(offset) is < 0 or > 1000000) throw CliException.Usage("Offset must be between 0 and 1000000.");
                request.Query("offset", invocation.Value(offset).ToString(CultureInfo.InvariantCulture))
                    .Query("limit", RegressionCommands.LimitValue(invocation, limit))
                    .Query("since", invocation.Value(since)).Query("until", invocation.Value(until))
                    .Query("caller", invocation.OptionalId(caller)).Query("outcome", invocation.Value(outcome));
                if (section == "summary")
                {
                    var value = await invocation.Api.ReadAsync<ReleaseOperationsSummaryV1>(request, invocation.Cancellation);
                    invocation.Output.Write(value, output => output.Fields(("Invocations", value.Count), ("Failed", value.Failed), ("Mean latency (ms)", value.MeanLatencyMs), ("P95 latency (ms)", value.P95LatencyMs)));
                }
                else
                {
                    var value = await invocation.Api.ReadAsync<ReleaseOperationsPageV1>(request, invocation.Cancellation);
                    invocation.Output.Write(value, output =>
                    {
                        foreach (var item in value.Items) output.Line(JsonSerializer.Serialize(item));
                        if (value.NextOffset is { } next) output.Line($"More results: --offset {next}");
                    });
                }
            });
            root.Subcommands.Add(command);
        }
        foreach (var action in new[] { "activate", "rollback", "enable", "disable" })
        {
            var project = Required("--project", "Project ID"); var mapping = Required("--mapping", "Mapping ID");
            var target = Required("--target", "Target ID"); var environment = Required("--environment", "Environment ID");
            var environmentVersion = new Option<long>("--environment-version") { Required = true, Description = "Exact environment version" };
            var version = new Option<long>("--version") { Required = true, Description = "Reviewed target version (If-Match)" };
            var operation = Required("--operation", "Original operation ID; reuse unchanged after an uncertain response");
            var qualification = Optional("--qualification", "Your stored qualification ID (activate/rollback)");
            var source = Optional("--source-release", "Your previous activated release ID (rollback)");
            var command = new Command(action, "Explicitly " + action + " a release target").With(project, mapping, target, environment, environmentVersion, version, operation, qualification, source);
            command.Handle(globals, async invocation =>
            {
                var body = new ReleaseOperationV1(action, invocation.Id(environment), CliInvocation.Positive(invocation.Value(environmentVersion), "--environment-version"),
                    invocation.OptionalId(qualification), invocation.OptionalId(source));
                if ((action is "activate" or "rollback") != (body.QualificationId is not null) || (action == "rollback") != (body.SourceReleaseId is not null))
                    throw CliException.Usage("Activation requires --qualification; rollback also requires --source-release. Enable and disable accept neither.");
                var request = (await invocation.RequestAsync(ApiOperations.ApplyReleaseOperation)).Path("projectId", invocation.Id(project)).Path("mappingId", invocation.Id(mapping))
                    .Path("targetId", invocation.Id(target)).Path("operationId", invocation.Id(operation))
                    .Header("If-Match", "\"" + CliInvocation.Positive(invocation.Value(version), "--version") + "\"")
                    .Body(CatalogCommands.JsonBody(body, ApiOperations.ApplyReleaseOperation));
                var value = await invocation.Api.ReadAsync<ReleaseOperationReceiptV1>(request, invocation.Cancellation);
                invocation.Output.Write(value, output => output.Line($"{value.OperationId}: {value.Outcome}"));
            });
            root.Subcommands.Add(command);
        }
        return root;
    }
}
