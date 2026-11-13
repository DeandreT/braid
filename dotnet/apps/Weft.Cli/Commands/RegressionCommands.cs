using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Regression;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class RegressionCommands
{
    private const int MaximumDefinitionBytes = 24 * 1024 * 1024;
    private sealed record Scope(Option<string> Project, Option<string> Mapping)
    {
        public async Task<ApiRequest> RequestAsync(CliInvocation invocation, ApiOperation operation) =>
            (await invocation.RequestAsync(operation)).Path("projectId", invocation.Id(Project)).Path("mappingId", invocation.Id(Mapping));
    }

    public static Command Regression(CliGlobals globals)
    {
        var scope = new Scope(Required("--project", "The project ID"), Required("--mapping", "The mapping ID"));
        var capabilities = new Command("capabilities", "Show the runtime, limits and what you may do with regression cases")
            .With(scope.Project, scope.Mapping);
        capabilities.Handle(globals, async invocation =>
        {
            var value = await invocation.Api.ReadAsync<RegressionCaseCapabilitiesV1>(
                await scope.RequestAsync(invocation, ApiOperations.GetRegressionCaseCapabilities), invocation.Cancellation);
            invocation.Output.Write(value, output =>
            {
                output.Fields(("Backend", value.Runtime.BackendId), ("Engine", value.Runtime.EngineRevision),
                    ("Profile", value.Runtime.Profile), ("Comparison", value.ComparisonPolicy), ("Read samples", value.ReadSamples),
                    ("Publish cases", value.PublishCases), ("Input bytes", value.Limits.MaximumInputBytes),
                    ("Output bytes", value.Limits.MaximumOutputBytes));
                if (value.DocumentRuntime is { } runtime)
                    output.Fields(("Document backend", runtime.BackendId), ("Document engine", runtime.EngineRevision), ("Document profile", runtime.Profile));
                if (value.CsvRuntime is { } csv)
                    output.Fields(("CSV backend", csv.BackendId), ("CSV engine", csv.EngineRevision), ("CSV profile", csv.Profile));
                if (value.XmlRuntime is { } xml)
                    output.Fields(("XML profile", xml.Profile), ("XML execution", "Native preview and exact-byte regression only; generated C# XML builds unavailable"));
                if (value.ComparisonPolicies is { } policies)
                    output.Fields(("Comparison policies", string.Join(", ", policies)));
            });
        });
        return new Command("regression", "Saved regression cases and their runs").With(capabilities, Cases(globals, scope), Runs(globals, scope));
    }

    private static Command Cases(CliGlobals globals, Scope scope)
    {
        var limit = Limit();
        var after = Optional("--after", "List cases after this case ID");
        var all = new Option<bool>("--all") { Description = "Read every page" };
        var list = new Command("list", "List the mapping's regression cases").With(scope.Project, scope.Mapping, limit, after, all);
        list.Handle(globals, async invocation =>
        {
            var cases = new List<RegressionCaseMetadataV1>();
            var cursor = invocation.OptionalId(after);
            RegressionCasePageV1 page;
            var traversal = new PageTraversal(cursor);
            do
            {
                var request = (await scope.RequestAsync(invocation, ApiOperations.GetRegressionCases)).Query("limit", LimitValue(invocation, limit))
                    .Query("afterCaseId", cursor);
                page = await invocation.Api.ReadAsync<RegressionCasePageV1>(request, invocation.Cancellation);
                cases.AddRange(page.Cases);
                cursor = page.NextCaseId;
            }
            while (traversal.Continue(invocation.Value(all), cursor));
            var document = invocation.Value(all) ? new RegressionCasePageV1(cases, null) : page;
            invocation.Output.Write(document, output =>
            {
                output.Table(["CASE", "VERSION", "NAME", "REVISION", "CREATED"], cases.Select(item =>
                    (IReadOnlyList<object?>)[item.CaseId, item.Version, item.Name, item.Revision.RevisionId, item.CreatedAtUtc]));
                if (document.NextCaseId is { } next)
                    output.Line($"More results: add --after {next} (or --all).");
            });
        });

        var caseId = Argument("caseId", "The case ID");
        var version = new Argument<long>("version") { Description = "The case version" };
        var get = new Command("get", "Show one version of a case, including its saved samples").With(scope.Project, scope.Mapping, caseId, version);
        get.Handle(globals, async invocation =>
        {
            var request = (await scope.RequestAsync(invocation, ApiOperations.GetRegressionCaseVersion)).Path("caseId", invocation.Id(caseId))
                .Path("version", CliInvocation.Positive(invocation.Value(version), "version"));
            var detail = await invocation.Api.ReadAsync<RegressionCaseDetailV1>(request, invocation.Cancellation);
            invocation.Output.Write(detail, output => output.Fields(("Case", detail.CaseId), ("Version", detail.Version),
                ("Name", detail.Definition.Name), ("Revision", detail.Revision.RevisionId), ("Target", detail.Definition.Target.Kind +
                    (detail.Definition.Target.Name is { } name ? " " + name : "")), ("Named inputs", detail.Definition.NamedInputs.Count),
                ("Parameters", detail.Definition.Parameters.Count), ("Expected outputs", detail.Definition.Expected.Count),
                ("Comparison", detail.Definition.ComparisonPolicy)));
        });

        var definition = Required("--definition", "The case definition JSON (application/vnd.weft.regression-case.v1+json), or - for standard input");
        var newCase = Optional("--case-id", "The case ID (default: a new case)");
        var newVersion = new Option<long>("--version") { Description = "The version to save; a new case starts at 1", DefaultValueFactory = _ => 1 };
        var create = new Command("create", "Save an immutable regression case version").With(scope.Project, scope.Mapping, definition, newCase, newVersion);
        create.Handle(globals, async invocation =>
        {
            var bytes = await ReadInputAsync(invocation, invocation.Value(definition)!, MaximumDefinitionBytes, "case definition");
            RegressionCasePutV1 body;
            try
            {
                body = JsonSerializer.Deserialize<RegressionCasePutV1>(bytes, WeftApiClient.Json)
                    ?? throw CliException.Usage("The case definition is empty.");
            }
            catch (JsonException)
            {
                throw CliException.Usage("The case definition is not a valid regression case document.");
            }
            var request = (await scope.RequestAsync(invocation, ApiOperations.PutRegressionCaseVersion))
                .Path("caseId", invocation.OptionalId(newCase) ?? CatalogCommands.NewId())
                .Path("version", CliInvocation.Positive(invocation.Value(newVersion), "--version"))
                .Body(CatalogCommands.JsonBody(body, ApiOperations.PutRegressionCaseVersion));
            var saved = await invocation.Api.ReadAsync<RegressionCaseSavedV1>(request, invocation.Cancellation);
            invocation.Output.Write(saved, output => output.Line($"Case {saved.CaseId} version {saved.Version}: {saved.Outcome}."));
        });

        return new Command("cases", "Saved regression cases").With(list, get, create);
    }

    private static Command Runs(CliGlobals globals, Scope scope)
    {
        var limit = Limit();
        var after = Optional("--after", "List runs after this run ID");
        var all = new Option<bool>("--all") { Description = "Read every page" };
        var list = new Command("list", "List your regression runs of the mapping").With(scope.Project, scope.Mapping, limit, after, all);
        list.Handle(globals, async invocation =>
        {
            var runs = new List<RegressionRunSummaryV1>();
            var cursor = invocation.OptionalId(after);
            RegressionRunPageV1 page;
            var traversal = new PageTraversal(cursor);
            do
            {
                var request = (await scope.RequestAsync(invocation, ApiOperations.GetRegressionRuns)).Query("limit", LimitValue(invocation, limit))
                    .Query("afterRunId", cursor);
                page = await invocation.Api.ReadAsync<RegressionRunPageV1>(request, invocation.Cancellation);
                runs.AddRange(page.Runs);
                cursor = page.NextRunId;
            }
            while (traversal.Continue(invocation.Value(all), cursor));
            var document = invocation.Value(all) ? new RegressionRunPageV1(runs, null) : page;
            invocation.Output.Write(document, output =>
            {
                output.Table(["RUN", "CASE", "VERSION", "STATE", "ATTEMPT", "CREATED"], runs.Select(run =>
                    (IReadOnlyList<object?>)[run.RunId, run.CaseId, run.Version, run.State, run.Attempt, run.CreatedAtUtc]));
                if (document.NextRunId is { } next)
                    output.Line($"More results: add --after {next} (or --all).");
            });
        });

        var caseId = Required("--case", "The case ID");
        var version = new Option<long>("--version") { Description = "The case version", Required = true };
        var runId = Optional("--run-id", "The run ID; reuse it to retry safely (default: new)");
        var wait = new Option<bool>("--wait") { Description = "Wait until the run finishes" };
        var timeout = Timeout("How long to wait for the run", TimeSpan.FromMinutes(15));
        var create = new Command("create", "Run one saved case version").With(scope.Project, scope.Mapping, caseId, version, runId, wait, timeout);
        create.Handle(globals, async invocation =>
        {
            var id = invocation.OptionalId(runId) ?? CatalogCommands.NewId();
            var request = (await scope.RequestAsync(invocation, ApiOperations.PutRegressionRun)).Path("runId", id)
                .Body(CatalogCommands.JsonBody(new RegressionRunPutV1(invocation.Id(caseId),
                    CliInvocation.Positive(invocation.Value(version), "--version")), ApiOperations.PutRegressionRun));
            var receipt = await invocation.Api.ReadAsync<RegressionRunReceiptV1>(request, invocation.Cancellation);
            if (!invocation.Value(wait))
            {
                invocation.Output.Write(receipt, output => output.Line($"Regression run {receipt.RunId}: {receipt.Outcome}."));
                return (int)CliExitCode.Success;
            }
            var run = await PollAsync(invocation, invocation.Value(timeout), "regression run", async () =>
            {
                var current = await ReadRunAsync(invocation, scope, receipt.RunId);
                return current.Run.State is "queued" or "running" ? null : current;
            });
            invocation.Output.Write(run, output => WriteRun(output, run));
            return Passed(run) ? (int)CliExitCode.Success : (int)CliExitCode.OperationFailed;
        });

        var id = Argument("runId", "The run ID");
        var get = new Command("get", "Show a regression run and its comparison").With(scope.Project, scope.Mapping, id);
        get.Handle(globals, async invocation =>
        {
            var run = await ReadRunAsync(invocation, scope, invocation.Id(id));
            invocation.Output.Write(run, output => WriteRun(output, run));
        });

        var cancel = new Command("cancel", "Ask a regression run to stop").With(scope.Project, scope.Mapping, id);
        cancel.Handle(globals, async invocation =>
        {
            var request = (await scope.RequestAsync(invocation, ApiOperations.CancelRegressionRun)).Path("runId", invocation.Id(id));
            var run = await invocation.Api.ReadAsync<RegressionRunV1>(request, invocation.Cancellation);
            invocation.Output.Write(run, output => WriteRun(output, run));
        });

        return new Command("runs", "Regression runs").With(list, create, get, cancel);
    }

    private static async Task<RegressionRunV1> ReadRunAsync(CliInvocation invocation, Scope scope, string runId)
    {
        var request = (await scope.RequestAsync(invocation, ApiOperations.GetRegressionRun)).Path("runId", runId);
        return await invocation.Api.ReadAsync<RegressionRunV1>(request, invocation.Cancellation);
    }

    private static bool Passed(RegressionRunV1 run) => run.Run.State == "completed" && run.Comparison?.Outcome == "passed";

    private static void WriteRun(CliOutput output, RegressionRunV1 run)
    {
        output.Fields(("Run", run.Run.RunId), ("Case", $"{run.Run.CaseId} v{run.Run.Version}"), ("Revision", run.Run.Revision.RevisionId),
            ("State", run.Run.State), ("Attempt", run.Run.Attempt), ("Execution", run.ExecutionOutcome), ("Preview status", run.PreviewStatus),
             ("Comparison", run.Comparison?.Outcome), ("Not evaluated", run.Comparison is { NotEvaluatedReason: not "none" } comparison
                 ? comparison.NotEvaluatedReason : null), ("Differences", run.Comparison?.TotalDifferences));
        if (run.Comparison?.DocumentDiagnostic is { } diagnostic)
            output.Fields(("Invalid document", diagnostic.Side), ("Output", diagnostic.Name ?? "(primary)"), ("Location", Location(diagnostic.Location)));
        if (run.Comparison?.CsvDiagnostic is { } csv)
            output.Fields(("Invalid CSV", csv.Side), ("Output", csv.Name ?? "(primary)"), ("Row", csv.Location.Row), ("Column", csv.Location.Column));
        if (run.Comparison is not { Differences.Count: > 0 } compared)
            return;
        output.Line();
        output.Table(["OUTPUT", "DIFFERENCE", "EXPECTED BYTES", "ACTUAL BYTES", "FIRST DIFFERENT BYTE"], compared.Differences.Select(difference =>
            (IReadOnlyList<object?>)[difference.Name ?? "(primary)", difference.Kind, difference.ExpectedBytes, difference.ActualBytes,
                difference.FirstDifferentByte]));
        foreach (var difference in compared.Differences.Where(value => value.Location is not null))
            output.Fields(("Output", difference.Name ?? "(primary)"), ("Location", Location(difference.Location!)));
        foreach (var difference in compared.Differences.Where(value => value.CsvLocation is not null))
            output.Fields(("Output", difference.Name ?? "(primary)"), ("Row", difference.CsvLocation!.Row), ("Column", difference.CsvLocation.Column));
    }

    private static string Location(RegressionDocumentLocationV1 value) => string.Create(CultureInfo.InvariantCulture,
        $"interchange {value.Interchange}, segment {value.Segment} ({value.SegmentId}), element {value.Element}, repetition {value.Repetition}, component {value.Component}");

    internal static Option<int?> Limit() => new("--limit") { Description = "Items per page, 1-100 (default 50)" };

    internal static string? LimitValue(CliInvocation invocation, Option<int?> limit) => invocation.Value(limit) switch
    {
        null => null,
        >= 1 and <= 100 and var value => value.ToString(CultureInfo.InvariantCulture),
        _ => throw CliException.Usage("--limit must be between 1 and 100.")
    };
}
