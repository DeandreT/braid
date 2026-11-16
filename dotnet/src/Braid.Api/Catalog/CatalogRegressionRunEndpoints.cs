using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Auth;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Contracts.Catalog;
using Braid.Contracts.Regression;
using Braid.Domain;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapBraidRegressionRuns(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var routes = endpoints.BusinessRoutes();
        const string route = "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/regression-runs";
        DescribeCase<RegressionRunPageV1>(routes.MapGet(route, GetRegressionRuns), "GetRegressionRuns", "Read the caller's bounded regression run history.")
            .DescribePage("afterRunId", "Return runs after this run: the nextRunId of the previous page.");
        DescribeCase<RegressionRunV1>(routes.MapGet(route + "/{runId}", GetRegressionRun), "GetRegressionRun", "Read a retained exact-case result under fresh sample authority.");
        DescribeCase<RegressionRunReceiptV1>(routes.MapPut(route + "/{runId}", PutRegressionRun), "PutRegressionRun", "Queue one exact saved-case version or recover the original request.")
            .Accepts<RegressionRunPutV1>(CatalogRegressionRunRequest.MediaType).Produces<RegressionRunReceiptV1>(StatusCodes.Status201Created)
            .Produces<CatalogErrorV1>(StatusCodes.Status409Conflict).Produces<CatalogErrorV1>(StatusCodes.Status412PreconditionFailed)
            .Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge).Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType)
            .Produces<CatalogErrorV1>(StatusCodes.Status429TooManyRequests);
        DescribeCase<RegressionRunV1>(routes.MapPost(route + "/{runId}/cancel", CancelRegressionRun), "CancelRegressionRun", "Request cancellation without replacing a retained terminal result.");
        return endpoints;
    }

    private static Task<IResult> GetRegressionRuns(string organizationId, string projectId, string mappingId, HttpContext context,
        CatalogRegressionCaseOptions cases, CatalogRegressionRunOptions runs) => WithRunAdmission(context, cases, runs, async admission =>
    {
        RequireQuery(context, "limit", "afterRunId");
        var mapping = CaseMapping(organizationId, projectId, mappingId, admission);
        if (mapping is null || !await RunAccess(context, admission, mapping)) return NotFound();
        var limit = 50;
        if (context.Request.Query.TryGetValue("limit", out var raw))
        {
            var parsed = CatalogRegressionCaseRequest.Version(raw[0]!);
            if (parsed > 100) throw new CatalogInvalidRequestException();
            limit = (int)parsed;
        }
        var after = context.Request.Query.TryGetValue("afterRunId", out var cursor) ? CatalogRegressionCaseRequest.Id(cursor[0]!) : (Guid?)null;
        var page = await RegressionRunPageAsync(context.RequestServices, admission.Actor!, mapping, limit, after, context.RequestAborted);
        if (page is null) return NotFound();
        var bytes = PrepareCase(new RegressionRunPageV1(page.Runs.Select(RunSummary).ToArray(), page.NextRunId?.ToString("D")), context.RequestAborted);
        return CaseResponse(bytes);
    });

    private static Task<IResult> GetRegressionRun(string organizationId, string projectId, string mappingId, string runId,
        HttpContext context, CatalogRegressionCaseOptions cases, CatalogRegressionRunOptions runs) => WithRunAdmission(context, cases, runs, async admission =>
    {
        RequireQuery(context);
        var mapping = CaseMapping(organizationId, projectId, mappingId, admission);
        var id = CatalogRegressionCaseRequest.Id(runId);
        if (mapping is null || !await RunAccess(context, admission, mapping)) return NotFound();
        return await RunResponse(context, admission, mapping, id);
    });

    private static Task<IResult> PutRegressionRun(string organizationId, string projectId, string mappingId, string runId,
        HttpContext context, CatalogRegressionCaseOptions cases, CatalogRegressionRunOptions runs) => WithRunAdmission(context, cases, runs, async admission =>
    {
        RequireQuery(context);
        var mapping = CaseMapping(organizationId, projectId, mappingId, admission);
        var id = CatalogRegressionCaseRequest.Id(runId);
        if (mapping is null || !await RunAccess(context, admission, mapping)) return NotFound();
        var requested = await CatalogRegressionRunRequest.ReadAsync(context.Request, context.RequestAborted);
        var value = await context.RequestServices.GetRequiredService<IRegressionCaseReader>().ReadAsync(admission.Actor!, mapping,
            requested.CaseId, requested.Version, context.RequestAborted);
        if (value is null) return NotFound();
        if (value.CaseId != requested.CaseId || value.Version != requested.Version || !Belongs(value.Revision, mapping))
            throw new InvalidOperationException("Run case scope mismatch.");
        var reference = new RegressionCaseEnvelopeCodec().Encode(value, context.RequestAborted).Reference;
        var existing = await context.RequestServices.GetRequiredService<IRegressionRunReader>().ReadAsync(admission.Actor!, mapping.OrganizationId, id, context.RequestAborted);
        if (existing is not null && (existing.Case != reference || existing.Submitter != admission.Actor!.PrincipalId))
            return Error(CatalogErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
        if (existing is null && cases.SelectRuntime(value.Runtime) is null)
            return Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
        if (!await RunAccess(context, admission, mapping)) return NotFound();
        var result = await context.RequestServices.GetRequiredService<IRegressionRunStore>().SubmitAsync(new(admission.Actor!, id, reference), context.RequestAborted);
        if (result == RegressionRunSubmitOutcome.NotAuthorizedOrNotFound) return NotFound();
        if (result == RegressionRunSubmitOutcome.Conflict) return Error(CatalogErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
        if (result == RegressionRunSubmitOutcome.Busy) return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status429TooManyRequests);
        if (result is not (RegressionRunSubmitOutcome.Created or RegressionRunSubmitOutcome.Replayed)) throw new InvalidOperationException("Run receipt refused.");
        if (!await RunAccess(context, admission, mapping)) return NotFound();
        var bytes = PrepareCase(new RegressionRunReceiptV1(runId, result == RegressionRunSubmitOutcome.Created ? "created" : "replayed"), context.RequestAborted);
        return CaseResponse(bytes, result == RegressionRunSubmitOutcome.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }, unsafeRequest: true);

    private static Task<IResult> CancelRegressionRun(string organizationId, string projectId, string mappingId, string runId,
        HttpContext context, CatalogRegressionCaseOptions cases, CatalogRegressionRunOptions runs) => WithRunAdmission(context, cases, runs, async admission =>
    {
        RequireQuery(context);
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")) throw new CatalogInvalidRequestException();
        var mapping = CaseMapping(organizationId, projectId, mappingId, admission);
        var id = CatalogRegressionCaseRequest.Id(runId);
        if (mapping is null || !await RunAccess(context, admission, mapping)) return NotFound();
        var current = await context.RequestServices.GetRequiredService<IRegressionRunReader>().ReadAsync(admission.Actor!, mapping.OrganizationId, id, context.RequestAborted);
        if (current is null || !Belongs(current.Case.Revision, mapping)) return NotFound();
        if (!await context.RequestServices.GetRequiredService<IRegressionRunStore>().CancelAsync(admission.Actor!, mapping.OrganizationId, id, context.RequestAborted)) return NotFound();
        return await RunResponse(context, admission, mapping, id);
    }, unsafeRequest: true);

    private static Task<IResult> WithRunAdmission(HttpContext context, CatalogRegressionCaseOptions cases, CatalogRegressionRunOptions runs,
        Func<CatalogRequestAdmission, Task<IResult>> action, bool unsafeRequest = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        return runs.Enabled ? WithCaseAdmission(context, cases, action, unsafeRequest) : Task.FromResult(Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status404NotFound));
    }

    private static Task<bool> RunAccess(HttpContext context, CatalogRequestAdmission admission, MappingScope mapping) =>
        RegressionRunAccessAsync(context.RequestServices, admission.Actor!, mapping, context.RequestAborted);

    private static async Task<IResult> RunResponse(HttpContext context, CatalogRequestAdmission admission, MappingScope mapping, Guid id)
    {
        var view = await RegressionRunViewAsync(context.RequestServices, admission.Actor!, mapping, id, context.RequestAborted);
        if (view is null) return NotFound();
        var result = view.Result;
        var comparison = result?.Comparison;
        var response = new RegressionRunV1(RunSummary(new(view.Run.RunId, view.Run.Case.CaseId, view.Run.Case.Version,
            view.Run.Case.Revision, view.Run.State, view.Run.Attempt, view.Run.CreatedAt)),
            result is null ? null : Name(result.Outcome), result?.PreviewStatus is { } status ? Name(status) : null,
            comparison is null ? null : new(comparison.Policy, Name(comparison.Outcome), Name(comparison.NotEvaluatedReason),
                comparison.ExpectedOutputCount, comparison.ActualOutputCount, comparison.TotalDifferenceCount, comparison.Truncated,
                comparison.Differences.Select(difference => new RegressionDifferenceV1(difference.Key.Name, Name(difference.Kind),
                    difference.ExpectedByteCount, difference.ActualByteCount, difference.ExpectedDigest?.Value,
                    difference.ActualDigest?.Value, difference.FirstDifferentByte, DocumentLocation(difference.Location), CsvLocation(difference.CsvLocation))).ToArray(),
                comparison.DocumentDiagnostic is { } diagnostic ? new(diagnostic.Key.Name, diagnostic.Side, DocumentLocation(diagnostic.Location)!) : null,
                comparison.CsvDiagnostic is { } csv ? new(csv.Key.Name, csv.Side, CsvLocation(csv.Location)!) : null),
            result?.Outputs?.Outputs.Select(output => new RegressionDocumentV1(output.Key.Name, WebEncoders.Base64UrlEncode(output.CopyBytes()),
                view.Runtime?.Profile is DocumentPreviewPolicy.Profile or CsvDocumentPolicy.Profile or XmlPreviewAdmission.Profile ? output.MediaType : null,
                CsvDialect(output.CsvDialect), XmlMetadata(output.XmlMetadata))).ToArray());
        var bytes = PrepareCase(response, context.RequestAborted);
        return CaseResponse(bytes);
    }

    private static RegressionRunSummaryV1 RunSummary(RegressionRunSummary value) => new(value.RunId.ToString("D"), value.CaseId.ToString("D"),
        value.Version.ToString(CultureInfo.InvariantCulture), Scope(value.Revision), Name(value.State), value.Attempt, value.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
    private static string Name<T>(T value) where T : Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private static RegressionDocumentLocationV1? DocumentLocation(RegressionDocumentLocation? location) => location is null ? null :
        new(location.Interchange, location.Segment, location.SegmentId, location.Element, location.Repetition, location.Component);
    private static RegressionCsvLocationV1? CsvLocation(RegressionCsvLocation? location) => location is null ? null : new(location.Row, location.Column);
}
