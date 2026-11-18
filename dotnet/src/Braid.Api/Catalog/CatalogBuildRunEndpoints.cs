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
    private static void MapBuildRuns(IEndpointRouteBuilder endpoints, string route)
    {
        BuildPageParameters(DescribeBuild<BuildRunPageV1>(endpoints.MapGet(route + "/build-runs", GetBuildRuns), "GetBuildRuns", "Read bounded own build history without artifact bodies."), "afterBuildId");
        DescribeBuild<BuildRunV1>(endpoints.MapGet(route + "/build-runs/{buildId}", GetBuildRun), "GetBuildRun", "Read one exact own retained build result.");
        DescribeBuild<MappingPackageManifestV1>(endpoints.MapGet(route + "/build-runs/{buildId}/package-manifest", GetBuildPackageManifest),
            "GetBuildPackageManifest", "Read the canonical manifest of an own successful immutable build; this does not authorize execution.");
        DescribeBuild<BuildRunReceiptV1>(endpoints.MapPut(route + "/build-runs/{buildId}", PutBuildRun), "PutBuildRun", "Queue an exact stored approval or recover the original submission.")
            .Accepts<BuildRunPutV1>(CatalogBuildRequest.RunMediaType).Produces<BuildRunReceiptV1>(StatusCodes.Status201Created)
            .Produces<CatalogErrorV1>(StatusCodes.Status409Conflict).Produces<CatalogErrorV1>(StatusCodes.Status412PreconditionFailed)
            .Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge).Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType)
            .Produces<CatalogErrorV1>(StatusCodes.Status429TooManyRequests);
        DescribeBuild<BuildRunV1>(endpoints.MapPost(route + "/build-runs/{buildId}/cancel", CancelBuildRun), "CancelBuildRun", "Request cancellation without replacing a retained terminal result.");
    }
    private static Task<IResult> PutBuildRun(string organizationId, string projectId, string mappingId, string buildId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            var id = CatalogBuildRequest.Id(buildId);
            if (mapping is null || !await BuildMappingAccess(context, admission, mapping)) return NotFound();
            var approval = await CatalogBuildRequest.RunAsync(context.Request, context.RequestAborted);
            var result = await context.RequestServices.GetRequiredService<BuildSubmissionService>().PutAsync(admission.Actor!, mapping, id, approval, context.RequestAborted);
            if (result.Outcome == BuildSubmissionRequestOutcome.Refused) return NotFound();
            if (result.Outcome == BuildSubmissionRequestOutcome.Conflict) return Error(CatalogErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
            if (result.Outcome == BuildSubmissionRequestOutcome.PreconditionFailed) return Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
            if (result.Outcome == BuildSubmissionRequestOutcome.Busy) return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status429TooManyRequests);
            if (result.Outcome == BuildSubmissionRequestOutcome.Unavailable) return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
            if (result.Outcome is not (BuildSubmissionRequestOutcome.Created or BuildSubmissionRequestOutcome.Replayed) || result.Run is not { } run) throw new InvalidOperationException("Build submission receipt refused.");
            CheckBuildRun(run, admission.Actor!.PrincipalId, mapping, id);
            var bytes = PrepareBuild(new BuildRunReceiptV1(result.Outcome == BuildSubmissionRequestOutcome.Created ? "created" : "replayed", CatalogBuildMapping.Run(run)), context.RequestAborted);
            return await BuildMappingAccess(context, admission, mapping) ? CaseResponse(bytes,
                result.Outcome == BuildSubmissionRequestOutcome.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK) : NotFound();
        }, unsafeRequest: true);
    private static Task<IResult> GetBuildRun(string organizationId, string projectId, string mappingId, string buildId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            var id = CatalogBuildRequest.Id(buildId);
            if (mapping is null || !await BuildMappingAccess(context, admission, mapping)) return NotFound();
            return await BuildRunResponse(context, admission, mapping, id);
        });
    private static Task<IResult> GetBuildPackageManifest(string organizationId, string projectId, string mappingId, string buildId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            var id = CatalogBuildRequest.Id(buildId);
            if (mapping is null) return NotFound();
            var value = await BuildRunAsync(context.RequestServices, admission.Actor!, mapping, id, context.RequestAborted);
            if (value is not { State: BuildRunState.Succeeded, Artifact: { } artifact }) return NotFound();
            if (!artifact.MatchesIntent(value.Intent)) throw new InvalidOperationException("Build artifact intent mismatch.");
            var bytes = MappingPackageManifest.Create(artifact).CopyCanonicalBytes();
            context.RequestAborted.ThrowIfCancellationRequested();
            return await BuildMappingAccess(context, admission, mapping) ? CaseResponse(bytes) : NotFound();
        });
    private static Task<IResult> CancelBuildRun(string organizationId, string projectId, string mappingId, string buildId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding") || context.Request.Headers.ContainsKey("Content-Type")) throw new CatalogInvalidRequestException();
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            var id = CatalogBuildRequest.Id(buildId);
            if (mapping is null || !await BuildMappingAccess(context, admission, mapping)) return NotFound();
            var value = await context.RequestServices.GetRequiredService<IBuildRunReader>().ReadAsync(admission.Actor!, mapping.OrganizationId, id, context.RequestAborted);
            if (value is null) return NotFound();
            if (!Belongs(value.Intent.Revision, mapping)) return NotFound();
            CheckBuildRun(value, admission.Actor!.PrincipalId, mapping, id);
            if (!await context.RequestServices.GetRequiredService<IBuildRunCommands>().CancelAsync(admission.Actor!, mapping.OrganizationId, id, context.RequestAborted)) return NotFound();
            return await BuildRunResponse(context, admission, mapping, id);
        }, unsafeRequest: true);
    private static async Task<IResult> BuildRunResponse(HttpContext context, CatalogRequestAdmission admission, MappingScope mapping, Guid id)
    {
        var value = await BuildRunAsync(context.RequestServices, admission.Actor!, mapping, id, context.RequestAborted);
        return value is null ? NotFound() : CaseResponse(PrepareBuild(CatalogBuildMapping.Run(value), context.RequestAborted));
    }
    private static Task<IResult> GetBuildRuns(string organizationId, string projectId, string mappingId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context, "limit", "afterBuildId");
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            if (mapping is null || !await BuildMappingAccess(context, admission, mapping)) return NotFound();
            var maximum = BuildPageLimit(context);
            var after = context.Request.Query.TryGetValue("afterBuildId", out var cursor) ? CatalogBuildRequest.Id(cursor[0]!) : (Guid?)null;
            var page = await BuildRunPageAsync(context.RequestServices, admission.Actor!, mapping, maximum, after, context.RequestAborted);
            if (page is null) return NotFound();
            return CaseResponse(PrepareBuild(new BuildRunPageV1(page.Runs.Select(CatalogBuildMapping.Run).ToArray(), page.NextBuildId?.ToString("D")), context.RequestAborted));
        });
    private static void CheckBuildRun(BuildRunSnapshot value, PrincipalId actor, MappingScope mapping, Guid id)
    {
        if (value.Actor != actor || value.Intent.BuildId != id || !Belongs(value.Intent.Revision, mapping)) throw new InvalidOperationException("Build run scope mismatch.");
    }
}
