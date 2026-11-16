using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;
using Braid.Application;
using Braid.Application.Comparisons;
using Braid.Contracts.Catalog;
using Braid.Contracts.Comparisons;
using Braid.Domain;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void MapRevisionComparisons(IEndpointRouteBuilder endpoints, string mappingRoute) =>
        DescribeRevisionComparison(endpoints.MapGet(mappingRoute + "/comparisons", GetCatalogRevisionComparison));

    private static Task<IResult> GetCatalogRevisionComparison(string organizationId, string projectId,
        string mappingId, HttpContext context, CatalogRevisionReportOptions revisionReportOptions) =>
        WithRevisionReportAdmission(context, revisionReportOptions, async admission =>
        {
            var mapping = CatalogRevisionReportRequestParser.Mapping(organizationId, projectId, mappingId);
            if (mapping.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            var selector = CatalogRevisionReportRequestParser.ParseComparison(context.Request);
            var actor = admission.Actor!;
            RevisionScope Side(Guid revision) => new(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, revision);
            var leftRevision = await ReadReportCatalogRevisionAsync(context, actor, Side(selector.Left.Revision));
            var rightRevision = await ReadReportCatalogRevisionAsync(context, actor, Side(selector.Right.Revision));
            if (leftRevision is null || rightRevision is null)
                return NotFound();
            var left = await ComparisonSideAsync(context, actor, leftRevision, selector.Left);
            if (left.Error is { } leftError)
                return leftError;
            var right = await ComparisonSideAsync(context, actor, rightRevision, selector.Right);
            if (right.Error is { } rightError)
                return rightError;

            byte[] bytes;
            try
            {
                var limits = new RevisionComparisonLimits();
                var comparison = new RevisionComparisonService().Compare(left.Side!, right.Side!, limits, context.RequestAborted);
                bytes = RevisionReportResponseSerializer.Serialize(comparison, limits.OutputUtf8Bytes, context.RequestAborted);
            }
            catch (RevisionComparisonException exception) when (exception.Failure == RevisionComparisonFailure.ResourceLimit)
            {
                return Error(CatalogErrorCodeV1.RevisionReportResourceLimit, StatusCodes.Status503ServiceUnavailable);
            }
            catch (RevisionReportValidationException)
            {
                return Error(CatalogErrorCodeV1.RevisionReportResourceLimit, StatusCodes.Status503ServiceUnavailable);
            }

            // Authority and both immutable sources are checked again immediately before delivery.
            var freshLeft = await ReadReportCatalogRevisionAsync(context, actor, leftRevision.Summary.Scope);
            var freshRight = await ReadReportCatalogRevisionAsync(context, actor, rightRevision.Summary.Scope);
            if (freshLeft is null || freshRight is null)
                return NotFound();
            RequireScope(freshLeft.Summary == leftRevision.Summary && freshRight.Summary == rightRevision.Summary);
            return CatalogRevisionReportPreparedResponse.Create(StatusCodes.Status200OK, null, null, bytes, context.RequestAborted);
        });

    private readonly record struct ComparisonSideResult(RevisionComparisonSide? Side, IResult? Error);

    private static async Task<ComparisonSideResult> ComparisonSideAsync(HttpContext context, ActorContext actor,
        CatalogRevision revision, CatalogRevisionComparisonSideSelector selector)
    {
        var scope = revision.Summary.Scope;
        var projection = await RevisionReportOperationAsync<IRevisionReportReader, RevisionReportReadResult>(context,
            reader => reader.GetRetainedAsync(actor, scope, RevisionReportOperation.Projection, selector.Projection,
                null, context.RequestAborted));
        if (Refusal(projection) is { } projectionRefusal)
            return new(null, projectionRefusal);
        if (projection.Report is not VerifiedProjectionReport verifiedProjection)
            return new(null, NotFound());
        VerifiedValidationReport? verifiedValidation = null;
        if (selector.Validation is { } validationId)
        {
            var validation = await RevisionReportOperationAsync<IRevisionReportReader, RevisionReportReadResult>(context,
                reader => reader.GetRetainedAsync(actor, scope, RevisionReportOperation.Validation, validationId,
                    selector.Projection, context.RequestAborted));
            if (Refusal(validation) is { } validationRefusal)
                return new(null, validationRefusal);
            if (validation.Report is not VerifiedValidationReport verified)
                return new(null, NotFound());
            verifiedValidation = verified;
        }
        try
        {
            return new(RevisionComparisonSide.Create(revision.Summary, verifiedProjection, verifiedValidation,
                context.RequestAborted), null);
        }
        catch (RevisionComparisonException)
        {
            // A verified report that does not describe its own revision is inconsistent stored data.
            return new(null, Error(CatalogErrorCodeV1.InternalFailure, StatusCodes.Status500InternalServerError));
        }
    }

    private static IResult? Refusal(RevisionReportReadResult result) => result.Outcome switch
    {
        RevisionReportReadOutcome.Observed => null,
        RevisionReportReadOutcome.AdmissionBusy =>
            Error(CatalogErrorCodeV1.RevisionReportAdmissionBusy, StatusCodes.Status429TooManyRequests),
        RevisionReportReadOutcome.ObjectUnavailable or RevisionReportReadOutcome.StorageIntegrity or
            RevisionReportReadOutcome.UnsupportedFormat or RevisionReportReadOutcome.ProfileUnavailable =>
            Error(CatalogErrorCodeV1.RevisionReportUnavailable, StatusCodes.Status503ServiceUnavailable),
        RevisionReportReadOutcome.ResourceLimit =>
            Error(CatalogErrorCodeV1.RevisionReportResourceLimit, StatusCodes.Status503ServiceUnavailable),
        _ => NotFound()
    };

    private static void DescribeRevisionComparison(RouteHandlerBuilder route)
    {
        Describe<RevisionComparisonV1>(route, "GetCatalogRevisionComparison",
            "Compare the retained explanations of two exact revisions of one mapping.");
        route.Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType)
            .Produces<CatalogErrorV1>(StatusCodes.Status429TooManyRequests);
        route.WithDescription("Requires the revision report read feature and fresh project ReadMetadata authority for both revisions. " +
            "leftRevision, leftProjection, rightRevision and rightProjection are required; leftValidation and rightValidation are optional. " +
            "Each names an exact retained successful derivation, never a pending or failed attempt; each name may appear once and no other " +
            "query is accepted. All UUIDs are canonical nonzero lowercase D strings. Reads never derive, claim, repair or start a worker. " +
            "Missing revisions or reports are opaque 404s; unavailable report storage is 503. Facts pair only through unique structural " +
            "anchors, never node IDs; partial or truncated evidence is reported as uncertainty, not equality. The comparison is bounded " +
            "before response bytes and followed by fresh authority and source validation. No body or Content-Type is supported. " +
            "Responses are no-store and carry no ETag.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.RequestBody = null;
                foreach (var side in new[] { "left", "right" })
                {
                    AddReportParameter(operation, side + "Revision", ParameterLocation.Query, true,
                        $"Exact {side} revision of this mapping.", uuid: true);
                    AddReportParameter(operation, side + "Projection", ParameterLocation.Query, true,
                        $"Exact retained successful projection derivation of the {side} revision.", uuid: true);
                    AddReportParameter(operation, side + "Validation", ParameterLocation.Query, false,
                        $"Exact retained validation derivation of the {side} revision, linked through its projection.", uuid: true);
                }
                AddReportParameter(operation, "Content-Encoding", ParameterLocation.Header, false,
                    "Absent or a single identity token; no other coding or request body is accepted.");
                return Task.CompletedTask;
            });
    }
}
