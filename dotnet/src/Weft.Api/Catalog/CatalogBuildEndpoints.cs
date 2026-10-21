using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Weft.Application;
using Weft.Contracts.Builds;
using Weft.Contracts.Catalog;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapWeftBuilds(this IEndpointRouteBuilder endpoints)
    {
        const string project = "/api/v1/organizations/{organizationId}/projects/{projectId}";
        const string mapping = project + "/mappings/{mappingId}";
        DescribeBuild<BuildCapabilitiesV1>(endpoints.MapGet(mapping + "/build-capabilities", GetBuildCapabilities), "GetBuildCapabilities", "Read current build-control capabilities without starting a worker.");
        BuildPageParameters(DescribeBuild<BuildEnvironmentPageV1>(endpoints.MapGet(project + "/build-environments", GetBuildEnvironments), "GetBuildEnvironments", "Read bounded retained environment versions."), "afterEnvironmentId", environmentVersion: true);
        DescribeBuild<BuildEnvironmentV1>(endpoints.MapGet(project + "/build-environments/{environmentId}/versions/{version}", GetBuildEnvironment), "GetBuildEnvironment", "Read one exact retained environment version.");
        MapBuildApprovals(endpoints, mapping);
        MapBuildRuns(endpoints, mapping);
        return endpoints;
    }

    private static RouteHandlerBuilder DescribeBuild<T>(RouteHandlerBuilder route, string name, string summary) =>
        route.WithName(name).WithTags("Builds").WithSummary(summary).Produces<T>(StatusCodes.Status200OK)
            .Produces<CatalogErrorV1>(StatusCodes.Status400BadRequest).Produces<CatalogErrorV1>(StatusCodes.Status404NotFound)
            .Produces<CatalogErrorV1>(StatusCodes.Status503ServiceUnavailable);

    private static RouteHandlerBuilder BuildPageParameters(RouteHandlerBuilder route, string cursor, bool environmentVersion = false) =>
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            AddReportParameter(operation, "limit", ParameterLocation.Query, false, "Canonical decimal page limit, default 50 and maximum 100.", maximumLength: 3);
            AddReportParameter(operation, cursor, ParameterLocation.Query, false, "Exact cursor from the preceding actor/scope-bound page.", uuid: true);
            if (environmentVersion) AddReportParameter(operation, "afterVersion", ParameterLocation.Query, false,
                "Canonical positive version paired with afterEnvironmentId.", maximumLength: 19);
            return Task.CompletedTask;
        });

    private static Task<IResult> WithBuildAdmission(HttpContext context, CatalogBuildOptions options,
        Func<CatalogRequestAdmission, Task<IResult>> action, bool unsafeRequest = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Remove("ETag");
        if (!options.Enabled) return Task.FromResult(Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status404NotFound));
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(), async admission =>
        {
            try { return await action(admission); }
            catch (CatalogBuildRequestException error) { return Error(error.Code, error.Status); }
            catch (BuildContractException) { return Error(CatalogErrorCodeV1.PersistenceFailure, StatusCodes.Status500InternalServerError); }
            catch (RevisionReportServiceException) { return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable); }
        }, unsafeRequest);
    }

    private static MappingScope? BuildMapping(string organization, string project, string mapping, CatalogRequestAdmission admission)
    {
        var value = new MappingScope(CatalogBuildRequest.Id(organization), CatalogBuildRequest.Id(project), CatalogBuildRequest.Id(mapping));
        return value.OrganizationId == admission.AllowedOrganizationId ? value : null;
    }
    private static async Task<bool> BuildMappingAccess(HttpContext context, CatalogRequestAdmission admission, MappingScope mapping) =>
        await CaseCatalogMapping(context, admission.Actor!, mapping) is { } current && current.Access.Can(ProjectAction.UploadRevision);
    private static async Task<bool> BuildProjectAccess(HttpContext context, CatalogRequestAdmission admission, ProjectScope project)
    {
        if (project.OrganizationId != admission.AllowedOrganizationId) return false;
        var current = await context.RequestServices.GetRequiredService<ICatalogReader>().GetProjectAsync(admission.Actor!, project, context.RequestAborted);
        if (current is null) return false;
        RequireScope(current.Summary.Scope == project);
        RequireAccess(current.Access, admission.Actor!, project);
        return current.Access.Can(ProjectAction.UploadRevision);
    }
    private static byte[] PrepareBuild<T>(T value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Build metadata response exceeds its bound.");
        token.ThrowIfCancellationRequested();
        return bytes;
    }
    private static int BuildPageLimit(HttpContext context)
    {
        if (!context.Request.Query.TryGetValue("limit", out var raw)) return 50;
        var value = CatalogBuildRequest.Version(raw[0]!);
        return value <= 100 ? (int)value : throw new CatalogInvalidRequestException();
    }
    private static Task<IResult> GetBuildCapabilities(string organizationId, string projectId, string mappingId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            if (mapping is null) return NotFound();
            var current = await CaseCatalogMapping(context, admission.Actor!, mapping);
            if (current is null || !current.Access.Can(ProjectAction.UploadRevision)) return NotFound();
            var supported = current.Summary.BackendId == options.Backend!.BackendId;
            var response = new BuildCapabilitiesV1(CatalogBuildMapping.Backend(options.Backend), CatalogBuildMapping.Profile(options.Profile!), true, supported, supported);
            var bytes = PrepareBuild(response, context.RequestAborted);
            return await BuildMappingAccess(context, admission, mapping) ? CaseResponse(bytes) : NotFound();
        });
    private static Task<IResult> GetBuildEnvironment(string organizationId, string projectId, string environmentId, string version, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context);
            var project = new ProjectScope(CatalogBuildRequest.Id(organizationId), CatalogBuildRequest.Id(projectId));
            var reference = new BuildEnvironmentReference(project, CatalogBuildRequest.Id(environmentId), CatalogBuildRequest.Version(version));
            if (!await BuildProjectAccess(context, admission, project)) return NotFound();
            var value = await context.RequestServices.GetRequiredService<IBuildMetadataReader>().ReadEnvironmentAsync(admission.Actor!, reference, context.RequestAborted);
            if (value is null) return NotFound();
            if (value.Environment.Reference != reference) throw new InvalidOperationException("Build environment scope mismatch.");
            var bytes = PrepareBuild(CatalogBuildMapping.Environment(value), context.RequestAborted);
            return await BuildProjectAccess(context, admission, project) ? CaseResponse(bytes) : NotFound();
        });
    private static Task<IResult> GetBuildEnvironments(string organizationId, string projectId, HttpContext context, CatalogBuildOptions options) =>
        WithBuildAdmission(context, options, async admission =>
        {
            RequireQuery(context, "limit", "afterEnvironmentId", "afterVersion");
            var project = new ProjectScope(CatalogBuildRequest.Id(organizationId), CatalogBuildRequest.Id(projectId));
            if (!await BuildProjectAccess(context, admission, project)) return NotFound();
            var maximum = BuildPageLimit(context);
            var hasId = context.Request.Query.TryGetValue("afterEnvironmentId", out var rawId);
            var hasVersion = context.Request.Query.TryGetValue("afterVersion", out var rawVersion);
            if (hasId != hasVersion) throw new CatalogInvalidRequestException();
            var cursor = hasId ? new BuildEnvironmentCursor(CatalogBuildRequest.Id(rawId[0]!), CatalogBuildRequest.Version(rawVersion[0]!)) : null;
            var page = await context.RequestServices.GetRequiredService<IBuildMetadataReader>().ListEnvironmentsAsync(admission.Actor!, project, maximum, cursor, context.RequestAborted);
            if (page is null) return NotFound();
            if (page.Environments.Count > maximum || page.Environments.Any(value => value.Environment.Reference.Project != project)) throw new InvalidOperationException("Build environment page scope mismatch.");
            var response = new BuildEnvironmentPageV1(page.Environments.Select(CatalogBuildMapping.Environment).ToArray(),
                page.Next?.EnvironmentId.ToString("D"), page.Next is null ? null : CatalogBuildMapping.Number(page.Next.Version));
            var bytes = PrepareBuild(response, context.RequestAborted);
            return await BuildProjectAccess(context, admission, project) ? CaseResponse(bytes) : NotFound();
        });
}
