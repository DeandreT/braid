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
    private static void MapBuildAdministration(IEndpointRouteBuilder endpoints, string project, string mapping)
    {
        DescribeBuild<BuildEnvironmentCapabilitiesV1>(endpoints.MapGet(project + "/build-environment-capabilities", GetBuildEnvironmentCapabilities),
            "GetBuildEnvironmentCapabilities", "Read the trusted selectable runtime profile and current environment-administrator capability.");
        DescribeBuild<BuildEnvironmentReceiptV1>(endpoints.MapPut(project + "/build-environments/{environmentId}/versions/{version}", PutBuildEnvironment),
            "PutBuildEnvironment", "Create a sequential environment version or recover its retained original profile without reactivation.")
            .Accepts<BuildEnvironmentPutV1>(CatalogBuildRequest.EnvironmentMediaType).Produces<BuildEnvironmentReceiptV1>(StatusCodes.Status201Created)
            .Produces<CatalogErrorV1>(StatusCodes.Status409Conflict).Produces<CatalogErrorV1>(StatusCodes.Status412PreconditionFailed)
            .Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge).Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType);
        DescribeBuild<BuildApprovalV1>(endpoints.MapPost(mapping + "/build-approvals/{approvalId}/revoke", RevokeBuildApproval),
            "RevokeBuildApproval", "Explicitly revoke an own approval or recover its retained revoked status without renewing admission.");
    }

    private static Task<IResult> GetBuildEnvironmentCapabilities(string organizationId, string projectId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            var project = new ProjectScope(CatalogBuildRequest.Id(organizationId), CatalogBuildRequest.Id(projectId));
            if (!await BuildProjectAccess(context, admission, project)) return NotFound();
            var administrator = await BuildProjectAccess(context, admission, project, administrator: true);
            var bytes = PrepareBuild(new BuildEnvironmentCapabilitiesV1(CatalogBuildMapping.Profile(options.Profile!), administrator,
                options.DocumentProfile is { } documentProfile ? CatalogBuildMapping.Profile(documentProfile) : null,
                options.InterchangeProfile is { } interchangeProfile ? CatalogBuildMapping.Profile(interchangeProfile) : null,
                context.RequestServices.GetService<ICsvBuildBoundaryReader>() is not null && options.CsvProfile is { } csvProfile
                    ? CatalogBuildMapping.Profile(csvProfile) : null,
                options.XmlProfile is { } xml ? CatalogBuildMapping.Profile(xml) : null, options.ByteProfiles?.Select(CatalogBuildMapping.Profile).ToArray()), context.RequestAborted);
            return await BuildProjectAccess(context, admission, project, administrator) ? CaseResponse(bytes) : NotFound();
        });

    private static Task<IResult> PutBuildEnvironment(string organizationId, string projectId, string environmentId, string version, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            var project = new ProjectScope(CatalogBuildRequest.Id(organizationId), CatalogBuildRequest.Id(projectId));
            var reference = new BuildEnvironmentReference(project, CatalogBuildRequest.Id(environmentId), CatalogBuildRequest.Version(version));
            if (!await BuildProjectAccess(context, admission, project, administrator: true)) return NotFound();
            var selection = await CatalogBuildRequest.EnvironmentAsync(context.Request, context.RequestAborted);
            if (!await BuildProjectAccess(context, admission, project, administrator: true)) return NotFound();
            var result = await context.RequestServices.GetRequiredService<BuildAdministrationService>()
                .PutEnvironmentAsync(admission.Actor!, reference, selection, context.RequestAborted);
            if (result.Outcome == BuildEnvironmentRequestOutcome.Refused) return NotFound();
            if (result.Outcome == BuildEnvironmentRequestOutcome.Conflict) return Error(CatalogErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
            if (result.Outcome == BuildEnvironmentRequestOutcome.PreconditionFailed) return Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
            if (result.Outcome == BuildEnvironmentRequestOutcome.Unavailable) return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
            if (result.Outcome is not (BuildEnvironmentRequestOutcome.Created or BuildEnvironmentRequestOutcome.Replayed) || result.Environment is not { } saved ||
                saved.Environment.Reference != reference || !selection.Matches(saved.Environment.Profile)) throw new InvalidOperationException("Build environment receipt refused.");
            var bytes = PrepareBuild(new BuildEnvironmentReceiptV1(result.Outcome == BuildEnvironmentRequestOutcome.Created ? "created" : "replayed",
                CatalogBuildMapping.Environment(saved)), context.RequestAborted);
            return await BuildProjectAccess(context, admission, project, administrator: true) ? CaseResponse(bytes,
                result.Outcome == BuildEnvironmentRequestOutcome.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK) : NotFound();
        }, unsafeRequest: true);

    private static Task<IResult> RevokeBuildApproval(string organizationId, string projectId, string mappingId, string approvalId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                context.Request.Headers.ContainsKey("Content-Type") || context.Request.Headers.ContainsKey("Content-Encoding")) throw new CatalogInvalidRequestException();
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            var id = CatalogBuildRequest.Id(approvalId);
            if (mapping is null || !await BuildMappingAccess(context, admission, mapping)) return NotFound();
            var result = await context.RequestServices.GetRequiredService<BuildAdministrationService>()
                .RevokeOwnApprovalAsync(admission.Actor!, mapping, id, context.RequestAborted);
            if (result.Outcome == BuildApprovalRevocationOutcome.Refused) return NotFound();
            if (result.Outcome == BuildApprovalRevocationOutcome.Unavailable) return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
            if (result.Outcome != BuildApprovalRevocationOutcome.Observed || result.Approval is not { Revoked: true } saved) throw new InvalidOperationException("Build approval revocation receipt refused.");
            CheckBuildApproval(saved, admission.Actor!.PrincipalId, mapping, id);
            var bytes = PrepareBuild(CatalogBuildMapping.Approval(saved), context.RequestAborted);
            return await BuildMappingAccess(context, admission, mapping) ? CaseResponse(bytes) : NotFound();
        }, unsafeRequest: true);
}
