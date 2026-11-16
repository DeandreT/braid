using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Braid.Contracts.Builds;
using Braid.Contracts.Catalog;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void MapBuildApprovals(IEndpointRouteBuilder endpoints, string route)
    {
        BuildPageParameters(DescribeBuild<BuildApprovalPageV1>(endpoints.MapGet(route + "/build-approvals", GetBuildApprovals), "GetBuildApprovals", "Read bounded own retained approvals without renewing admission."), "afterApprovalId");
        DescribeBuild<BuildApprovalV1>(endpoints.MapGet(route + "/build-approvals/{approvalId}", GetBuildApproval), "GetBuildApproval", "Recover an exact own approval, including revoked/inactive metadata.");
        DescribeBuild<BuildApprovalReceiptV1>(endpoints.MapPut(route + "/build-approvals/{approvalId}", PutBuildApproval), "PutBuildApproval", "Create or recover one original immutable build approval.")
            .Accepts<BuildApprovalPutV1>(CatalogBuildRequest.ApprovalMediaType).Produces<BuildApprovalReceiptV1>(StatusCodes.Status201Created)
            .Produces<CatalogErrorV1>(StatusCodes.Status409Conflict).Produces<CatalogErrorV1>(StatusCodes.Status412PreconditionFailed)
            .Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge).Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType);
    }
    private static Task<IResult> PutBuildApproval(string organizationId, string projectId, string mappingId, string approvalId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            var id = CatalogBuildRequest.Id(approvalId);
            if (mapping is null || !await BuildMappingAccess(context, admission, mapping)) return NotFound();
            var selection = await CatalogBuildRequest.ApprovalAsync(context.Request, context.RequestAborted);
            var result = await context.RequestServices.GetRequiredService<BuildApprovalRecoveryService>()
                .PutAsync(admission.Actor!, mapping, id, selection, context.RequestAborted);
            if (result.Outcome == BuildApprovalRequestOutcome.Refused) return NotFound();
            if (result.Outcome == BuildApprovalRequestOutcome.Conflict) return Error(CatalogErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
            if (result.Outcome == BuildApprovalRequestOutcome.PreconditionFailed) return Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
            if (result.Outcome == BuildApprovalRequestOutcome.Unavailable) return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
            if (result.Outcome is not (BuildApprovalRequestOutcome.Created or BuildApprovalRequestOutcome.Replayed) || result.Approval is not { } approval) throw new InvalidOperationException("Build approval receipt refused.");
            CheckBuildApproval(approval, admission.Actor!.PrincipalId, mapping, id);
            var response = new BuildApprovalReceiptV1(result.Outcome == BuildApprovalRequestOutcome.Created ? "created" : "replayed", CatalogBuildMapping.Approval(approval));
            var bytes = PrepareBuild(response, context.RequestAborted);
            return await BuildMappingAccess(context, admission, mapping) ? CaseResponse(bytes,
                result.Outcome == BuildApprovalRequestOutcome.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK) : NotFound();
        }, unsafeRequest: true);
    private static Task<IResult> GetBuildApproval(string organizationId, string projectId, string mappingId, string approvalId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            var id = CatalogBuildRequest.Id(approvalId);
            if (mapping is null || !await BuildMappingAccess(context, admission, mapping)) return NotFound();
            var value = await BuildApprovalAsync(context.RequestServices, admission.Actor!, mapping, id, context.RequestAborted);
            return value is null ? NotFound() : CaseResponse(PrepareBuild(CatalogBuildMapping.Approval(value), context.RequestAborted));
        });
    private static Task<IResult> GetBuildApprovals(string organizationId, string projectId, string mappingId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context, "limit", "afterApprovalId");
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            if (mapping is null || !await BuildMappingAccess(context, admission, mapping)) return NotFound();
            var maximum = BuildPageLimit(context);
            var after = context.Request.Query.TryGetValue("afterApprovalId", out var cursor) ? CatalogBuildRequest.Id(cursor[0]!) : (Guid?)null;
            var page = await BuildApprovalPageAsync(context.RequestServices, admission.Actor!, mapping, maximum, after, context.RequestAborted);
            if (page is null) return NotFound();
            return CaseResponse(PrepareBuild(new BuildApprovalPageV1(page.Approvals.Select(CatalogBuildMapping.Approval).ToArray(), page.NextApprovalId?.ToString("D")), context.RequestAborted));
        });
    private static void CheckBuildApproval(RetainedBuildApproval value, PrincipalId actor, MappingScope mapping, Guid id)
    {
        if (value.Actor != actor || value.Intent.ApprovalId != id || !Belongs(value.Intent.Revision, mapping)) throw new InvalidOperationException("Build approval scope mismatch.");
    }
}
