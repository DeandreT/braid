using System.CommandLine;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Catalog;
using Weft.Contracts.Comparisons;
using Weft.Contracts.Projections;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class ReportCommands
{
    private sealed record Scope(Option<string> Project, Option<string> Mapping, Option<string> Revision)
    {
        public Symbol[] Symbols => [Project, Mapping, Revision];

        public async Task<ApiRequest> RequestAsync(CliInvocation invocation, ApiOperation operation) =>
            (await invocation.RequestAsync(operation)).Path("projectId", invocation.Id(Project)).Path("mappingId", invocation.Id(Mapping))
                .Path("revisionId", invocation.Id(Revision));
    }

    public static Command Reports(CliGlobals globals)
    {
        var scope = new Scope(Required("--project", "The project ID"), Required("--mapping", "The mapping ID"),
            Required("--revision", "The revision ID"));
        return new Command("reports", "Projection and validation reports of a revision").With(
            Kind(globals, scope, "projection", ApiOperations.GetCatalogProjectionReport, ApiOperations.GetRetainedCatalogProjectionReport,
                ApiOperations.EnsureCatalogProjectionReport, Write<CatalogProjectionReportV1>(WriteProjection)),
            Kind(globals, scope, "validation", ApiOperations.GetCatalogValidationReport, ApiOperations.GetRetainedCatalogValidationReport,
                ApiOperations.EnsureCatalogValidationReport, Write<CatalogValidationReportV1>(WriteValidation)));
    }

    private sealed record Report(CatalogRevisionReportStateV1 State, Action<CliOutput> Write);
    private sealed record Writer(Func<CliInvocation, ApiResponse, Task<Report>> ReadAsync);

    private static Writer Write<T>(Action<CliOutput, T> human) where T : class => new(async (invocation, response) =>
    {
        var report = await response.ReadJsonAsync<T>(invocation.Cancellation);
        var state = report switch
        {
            CatalogProjectionReportV1 projection => projection.State,
            CatalogValidationReportV1 validation => validation.State,
            _ => throw new InvalidOperationException("Unknown report type.")
        };
        return new Report(state, output => output.Write(report, humanOutput => human(humanOutput, report)));
    });

    private static Command Kind(CliGlobals globals, Scope scope, string kind, ApiOperation current, ApiOperation retained,
        ApiOperation ensure, Writer writer)
    {
        var derivation = Optional("--derivation", "Read this exact retained derivation instead of the current report");
        var projectionLink = Optional("--projection-derivation", "Link validation findings to this verified projection derivation");
        var getSymbols = kind == "validation" ? [.. scope.Symbols, derivation, projectionLink] : new Symbol[] { scope.Project, scope.Mapping, scope.Revision, derivation };
        var get = new Command("get", $"Read the {kind} report").With(getSymbols);
        get.Handle(globals, async invocation =>
        {
            var selected = invocation.OptionalId(derivation);
            var request = await scope.RequestAsync(invocation, selected is null ? current : retained);
            if (selected is not null)
                request.Path("derivationId", selected);
            if (kind == "validation")
                request.Query("projectionDerivationId", invocation.OptionalId(projectionLink));
            using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
            await response.RequireSuccessAsync(invocation.Cancellation);
            (await writer.ReadAsync(invocation, response)).Write(invocation.Output);
        });

        var repair = Optional("--repair", "Repair this currently selected derivation whose stored object is missing or damaged");
        var noWait = new Option<bool>("--no-wait") { Description = "Return while derivation is still pending" };
        var timeout = Timeout("How long to wait for the report", TimeSpan.FromMinutes(10));
        var ensureCommand = new Command("ensure", $"Derive the current {kind} report if it is missing, and wait for it")
            .With([.. scope.Symbols, repair, noWait, timeout]);
        ensureCommand.Handle(globals, async invocation =>
        {
            var request = (await scope.RequestAsync(invocation, ensure)).Query("repairDerivationId", invocation.OptionalId(repair));
            var api = invocation.Api;
            TimeSpan? advised = null;
            var settled = await PollAsync(invocation, invocation.Value(timeout), $"{kind} report", async () =>
            {
                using var response = await api.SendAsync(request, invocation.Cancellation);
                if (!response.IsSuccess)
                {
                    throw await response.FailureAsync(invocation.Cancellation);
                }
                if (response.Status == 202 && !invocation.Value(noWait))
                {
                    advised = api.RetryAfter(response.Message);
                    return null;
                }
                // Consume the terminal body's JSON while the poll budget is still active; no owned response escapes it.
                return await writer.ReadAsync(invocation, response);
            }, () => advised);
            settled.Write(invocation.Output);
            return settled.State == CatalogRevisionReportStateV1.Failed ? (int)CliExitCode.OperationFailed : (int)CliExitCode.Success;
        });

        return new Command(kind, $"The {kind} report").With(get, ensureCommand);
    }

    private static void WriteLifecycle(CliOutput output, CatalogRevisionReportStateV1 state, CatalogRevisionReportReadOutcomeV1 read,
        object? status, CatalogRevisionReportResultV1? result, CatalogRevisionReportAttemptV1? attempt, DateTimeOffset? next,
        CatalogRevisionReportFailureV1? failure) => output.Fields(("State", state), ("Read", read), ("Status", status),
        ("Derivation", result?.DerivationId ?? attempt?.DerivationId), ("Generation", result?.Generation ?? attempt?.Generation),
        ("Started", result?.StartedAtUtc ?? attempt?.StartedAtUtc), ("Finished", result?.FinishedAtUtc ?? attempt?.FinishedAtUtc),
        ("Next attempt", next), ("Failure", failure));

    private static void WriteProjection(CliOutput output, CatalogProjectionReportV1 report)
    {
        WriteLifecycle(output, report.State, report.ReadOutcome, report.Status, report.Result, report.CurrentAttempt,
            report.NextAttemptAtUtc, report.Failure);
        if (report.Report?.Document is not { } document)
            return;
        output.Line();
        output.Fields(("Coverage", document.Coverage.Status), ("Coverage gaps", document.Coverage.Gaps.Count),
            ("Boundaries", document.Boundaries.Count), ("Fields", document.Fields.Count), ("Parameters", document.Parameters.Count),
            ("Rules", document.Rules.Count), ("Controls", document.Controls.Count), ("Relationships", document.Relationships.Count),
            ("Diagnostics", document.Diagnostics.Count));
    }

    private static void WriteValidation(CliOutput output, CatalogValidationReportV1 report)
    {
        WriteLifecycle(output, report.State, report.ReadOutcome, report.Status, report.Result, report.CurrentAttempt,
            report.NextAttemptAtUtc, report.Failure);
        if (report.Report is not { } validation)
            return;
        output.Line();
        output.Fields(("Valid", validation.IsValid), ("Issues", validation.TotalIssueCount), ("Shown", validation.EmittedIssueCount),
            ("Truncated", validation.Truncated), ("Linked projection", report.ProjectionDerivationId));
        if (validation.Diagnostics.Count == 0)
            return;
        output.Line();
        output.Table(["SEVERITY", "CODE", "MESSAGE"], validation.Diagnostics.Select(diagnostic =>
            (IReadOnlyList<object?>)[diagnostic.Severity, diagnostic.Code, diagnostic.Message + (diagnostic.MessageTruncated ? "…" : "")]));
    }

    public static Command Compare(CliGlobals globals)
    {
        var project = Required("--project", "The project ID");
        var mapping = Required("--mapping", "The mapping ID");
        var leftRevision = Required("--left-revision", "The left revision ID");
        var rightRevision = Required("--right-revision", "The right revision ID");
        var leftProjection = Optional("--left-projection", "The left projection derivation (default: the current available one)");
        var rightProjection = Optional("--right-projection", "The right projection derivation (default: the current available one)");
        var leftValidation = Optional("--left-validation", "The left validation derivation to include");
        var rightValidation = Optional("--right-validation", "The right validation derivation to include");
        var withValidation = new Option<bool>("--validation") { Description = "Include each side's current available validation" };
        var compare = new Command("compare", "Compare the observed projection facts of two revisions of a mapping")
            .With(project, mapping, leftRevision, rightRevision, leftProjection, rightProjection, leftValidation, rightValidation, withValidation);
        compare.Handle(globals, async invocation =>
        {
            var projectId = invocation.Id(project);
            var mappingId = invocation.Id(mapping);
            async Task<string?> Current(string revision, ApiOperation operation)
            {
                var request = (await invocation.RequestAsync(operation)).Path("projectId", projectId).Path("mappingId", mappingId)
                    .Path("revisionId", revision);
                using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
                await response.RequireSuccessAsync(invocation.Cancellation);
                var result = operation == ApiOperations.GetCatalogProjectionReport
                    ? (await response.ReadJsonAsync<CatalogProjectionReportV1>(invocation.Cancellation)).Result
                    : (await response.ReadJsonAsync<CatalogValidationReportV1>(invocation.Cancellation)).Result;
                return result?.DerivationId ?? throw new CliException(CliExitCode.Conflict,
                    $"Revision {revision} has no available {(operation == ApiOperations.GetCatalogProjectionReport ? "projection" : "validation")} report. " +
                    "Derive it with: weft reports ... ensure", "reportUnavailable");
            }
            var left = invocation.Id(leftRevision);
            var right = invocation.Id(rightRevision);
            var request = (await invocation.RequestAsync(ApiOperations.GetCatalogRevisionComparison)).Path("projectId", projectId)
                .Path("mappingId", mappingId)
                .Query("leftRevision", left)
                .Query("leftProjection", invocation.OptionalId(leftProjection) ?? await Current(left, ApiOperations.GetCatalogProjectionReport))
                .Query("leftValidation", invocation.OptionalId(leftValidation) ??
                    (invocation.Value(withValidation) ? await Current(left, ApiOperations.GetCatalogValidationReport) : null))
                .Query("rightRevision", right)
                .Query("rightProjection", invocation.OptionalId(rightProjection) ?? await Current(right, ApiOperations.GetCatalogProjectionReport))
                .Query("rightValidation", invocation.OptionalId(rightValidation) ??
                    (invocation.Value(withValidation) ? await Current(right, ApiOperations.GetCatalogValidationReport) : null));
            var comparison = await invocation.Api.ReadAsync<RevisionComparisonV1>(request, invocation.Cancellation);
            invocation.Output.Write(comparison, output => WriteComparison(output, comparison));
        });
        return compare;
    }

    internal static void WriteComparison(CliOutput output, RevisionComparisonV1 comparison)
    {
        output.Fields(("Observed differences", comparison.HasObservedDifferences), ("Uncertainty", comparison.HasUncertainty),
            ("Items", comparison.TotalItemCount), ("Shown", comparison.EmittedItemCount), ("Truncated", comparison.Truncated),
            ("Left coverage", comparison.LeftCoverage.Status), ("Right coverage", comparison.RightCoverage.Status),
            ("Left valid", comparison.LeftValidation?.IsValid), ("Right valid", comparison.RightValidation?.IsValid));
        var items = comparison.Items.Where(item => item.Match != ComparisonMatchV1.Matched ||
            item.Facets.Any(facet => facet.HasObservedDifference || facet.HasUncertainty)).ToArray();
        if (items.Length == 0)
            return;
        output.Line();
        output.Table(["ENTITY", "MATCH", "LEFT", "RIGHT", "DIFFERENT", "UNCERTAIN"], items.Select(item => (IReadOnlyList<object?>)[
            item.Entity, item.Match, Candidate(item.Left), Candidate(item.Right),
            Aspects(item.Facets.Where(facet => facet.HasObservedDifference)), Aspects(item.Facets.Where(facet => facet.HasUncertainty))]));
    }

    private static string Candidate(IReadOnlyList<ComparisonCandidateV1> candidates) => candidates.Count == 0 ? "-" : string.Join(", ",
        candidates.Select(candidate => candidate.RelationshipId ?? candidate.Node switch
        {
            BoundaryNodeReference node => node.BoundaryId,
            FieldNodeReference node => node.FieldId,
            ParameterNodeReference node => node.ParameterId,
            RuleNodeReference node => node.RuleId,
            ControlNodeReference node => node.ControlId,
            _ => "?"
        }));

    private static string Aspects(IEnumerable<ComparisonFacetV1> facets)
    {
        var names = facets.Select(facet => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(facet.Aspect.ToString())).ToArray();
        return names.Length == 0 ? "-" : string.Join(", ", names);
    }
}
