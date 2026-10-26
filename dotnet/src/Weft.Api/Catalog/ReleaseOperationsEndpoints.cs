using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Weft.Api.Auth;
using Weft.Contracts.Catalog;
using Weft.Contracts.Operations;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Api.Catalog;

public sealed record ReleaseOperationsOptions(bool Enabled = false);

public static partial class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapWeftReleaseOperations(this IEndpointRouteBuilder endpoints)
    {
        const string root = "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/operations";
        var routes = endpoints.BusinessRoutes();
        DescribeOperations<ReleaseOperationsPageV1>(routes.MapGet(root, GetReleaseTargets), "GetReleaseTargets", "List endpoint targets and active release identities.")
            .AddOpenApiOperationTransformer((operation, _, _) => { OperationsParameters(operation); return Task.CompletedTask; });
        DescribeOperations<ReleaseOperationsPageV1>(routes.MapGet(root + "/{targetId}/{section}", GetReleaseOperations), "GetReleaseOperations", "Read paged release, history, actions, callers or invocation metadata. No payload bytes.")
            .AddOpenApiOperationTransformer((operation, _, _) => { OperationsParameters(operation); return Task.CompletedTask; });
        DescribeOperations<ReleaseOperationsSummaryV1>(routes.MapGet(root + "/{targetId}/summary", GetReleaseSummary), "GetReleaseSummary", "Read failure and latency summaries over a selected window.")
            .AddOpenApiOperationTransformer((operation, _, _) => { OperationsParameters(operation); return Task.CompletedTask; });
        DescribeOperations<ReleaseOperationReceiptV1>(routes.MapPost(root + "/{targetId}/actions/{operationId}", ApplyReleaseOperation), "ApplyReleaseOperation", "Apply a version-conditional idempotent release operation.")
            .Accepts<ReleaseOperationV1>("application/json").Produces<CatalogErrorV1>(412).Produces<CatalogErrorV1>(428)
            .Produces<CatalogErrorV1>(409).Produces<CatalogErrorV1>(408).Produces<CatalogErrorV1>(413).Produces<CatalogErrorV1>(415)
            .AddOpenApiOperationTransformer((operation, _, _) => { AddReportParameter(operation, "If-Match", ParameterLocation.Header, true, "Quoted target version."); return Task.CompletedTask; });
        return endpoints;
    }

    private static RouteHandlerBuilder DescribeOperations<T>(RouteHandlerBuilder route, string name, string summary) =>
        DescribeBuild<T>(route, name, summary).WithTags("Operations")
            .Produces<CatalogErrorV1>(401).Produces<CatalogErrorV1>(403).Produces<CatalogErrorV1>(500);

    private static void OperationsParameters(OpenApiOperation operation)
    {
        foreach (var name in new[] { "offset", "limit", "since", "until", "caller", "outcome" })
            AddReportParameter(operation, name, ParameterLocation.Query, false, "Operations page or window filter.");
    }

    private static Task<IResult> WithOperations(HttpContext context, Func<CatalogRequestAdmission, Task<IResult>> action, bool write = false)
    {
        var enabled = context.RequestServices.GetRequiredService<ReleaseOperationsOptions>().Enabled;
        context.Response.Headers.CacheControl = "no-store";
        if (!enabled) return Task.FromResult(NotFound());
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(), async admission =>
        {
            try { return await action(admission); }
            catch (CatalogBuildRequestException error) { return Error(error.Code, error.Status); }
            catch (ArgumentException) { return Error(CatalogErrorCodeV1.InvalidRequest, 400); }
            catch (OverflowException) { return Error(CatalogErrorCodeV1.InvalidRequest, 400); }
        }, write);
    }

    private static Task<IResult> GetReleaseTargets(string organizationId, string projectId, string mappingId, HttpContext context) =>
        ReadReleaseOperations(organizationId, projectId, mappingId, null, "targets", context);
    private static Task<IResult> GetReleaseOperations(string organizationId, string projectId, string mappingId, string targetId, string section, HttpContext context) =>
        ReadReleaseOperations(organizationId, projectId, mappingId, targetId, section, context);
    private static Task<IResult> GetReleaseSummary(string organizationId, string projectId, string mappingId, string targetId, HttpContext context) =>
        ReadReleaseOperations(organizationId, projectId, mappingId, targetId, "summary", context);

    private static Task<IResult> ReadReleaseOperations(string organizationId, string projectId, string mappingId, string? targetId, string section, HttpContext context) =>
        WithOperations(context, async admission =>
        {
            RequireQuery(context, "offset", "limit", "since", "until", "caller", "outcome");
            if (section is not ("targets" or "releases" or "history" or "actions" or "callers" or "invocations" or "summary")) return NotFound();
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            if (mapping is null || !await BuildProjectAccess(context, admission, new(mapping.OrganizationId, mapping.ProjectId))) return NotFound();
            var offset = context.Request.Query.TryGetValue("offset", out var raw) && raw[0] != "0" ? checked((int)CatalogBuildRequest.Version(raw[0]!)) : 0;
            DateTimeOffset? Date(string name) => context.Request.Query.TryGetValue(name, out var value)
                ? DateTimeOffset.TryParseExact(value[0], ["O", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : throw new CatalogInvalidRequestException() : null;
            var query = new ReleaseOperationsQuery(section, offset, BuildPageLimit(context), Date("since"), Date("until"),
                context.Request.Query.TryGetValue("caller", out var caller) ? CatalogBuildRequest.Id(caller[0]!) : null,
                context.Request.Query.TryGetValue("outcome", out var outcome) ? outcome[0] : null);
            var value = await context.RequestServices.GetRequiredService<IReleaseOperationsStore>().ReadAsync(admission.Actor!, mapping,
                targetId is null ? null : CatalogBuildRequest.Id(targetId), query, context.RequestAborted);
            if (value is null || !await BuildProjectAccess(context, admission, new(mapping.OrganizationId, mapping.ProjectId))) return NotFound();
            if (section == "summary")
            {
                var summary = value.Value.Deserialize<ReleaseOperationsSummaryV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Invalid operations summary.");
                return CaseResponse(PrepareBuild(summary, context.RequestAborted));
            }
            var items = value.Value.EnumerateArray().ToArray();
            var page = new ReleaseOperationsPageV1(items.Take(query.Limit).Select(item => OperationsItem(section, item)).ToArray(), items.Length > query.Limit ? offset + query.Limit : null);
            if (section == "targets" && targetId is not null && items.Length == 1) context.Response.Headers.ETag = $"\"{items[0].GetProperty("version").GetString()}\"";
            return CaseResponse(PrepareBuild(page, context.RequestAborted));
        });

    private static ReleaseOperationsItemV1 OperationsItem(string section, JsonElement item)
    {
        var type = section switch
        {
            "targets" => typeof(ReleaseTargetMetadataV1),
            "releases" => typeof(ReleaseMetadataV1),
            "history" => typeof(ReleaseTargetEventV1),
            "actions" => typeof(ReleaseActionAuditV1),
            "callers" => typeof(ReleaseCallerGrantV1),
            "invocations" => typeof(ReleaseInvocationMetadataV1),
            _ => throw new InvalidOperationException("Invalid metadata section.")
        };
        return (ReleaseOperationsItemV1)(JsonSerializer.Deserialize(item, type, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Invalid operations metadata."));
    }

    private static Task<IResult> ApplyReleaseOperation(string organizationId, string projectId, string mappingId, string targetId, string operationId, HttpContext context) =>
        WithOperations(context, async admission =>
        {
            RequireQuery(context);
            var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
            if (mapping is null || !await BuildProjectAccess(context, admission, new(mapping.OrganizationId, mapping.ProjectId))) return NotFound();
            if (!context.Request.Headers.TryGetValue("If-Match", out var match)) return Error(CatalogErrorCodeV1.PreconditionFailed, 428);
            if (match.Count != 1 || match[0] is not { Length: > 2 } etag || etag[0] != '"' || etag[^1] != '"') throw new CatalogInvalidRequestException();
            var expected = CatalogBuildRequest.Version(etag[1..^1]);
            var body = await CatalogBuildRequest.ReleaseOperationAsync(context.Request, context.RequestAborted);
            var target = new ReleaseTargetReference(mapping, new(new(mapping.OrganizationId, mapping.ProjectId), CatalogBuildRequest.Id(body.EnvironmentId), CatalogBuildRequest.Version(body.EnvironmentVersion)), CatalogBuildRequest.Id(targetId));
            var operation = new ReleaseOperation(CatalogBuildRequest.Id(operationId), body.Action, expected,
                body.QualificationId is null ? null : CatalogBuildRequest.Id(body.QualificationId), body.SourceReleaseId is null ? null : CatalogBuildRequest.Id(body.SourceReleaseId));
            var result = await context.RequestServices.GetRequiredService<ReleaseOperationsService>().OperateAsync(admission.Actor!, target, operation, context.RequestAborted);
            if (result == ReleaseTransitionOutcome.IdempotencyConflict) return Error(CatalogErrorCodeV1.IdempotencyConflict, 409);
            if (result == ReleaseTransitionOutcome.Conflict) return Error(CatalogErrorCodeV1.PreconditionFailed, 412);
            if (result == ReleaseTransitionOutcome.Refused) return NotFound();
            return CaseResponse(PrepareBuild(new ReleaseOperationReceiptV1(operationId, result == ReleaseTransitionOutcome.Activated ? "applied" : result.ToString().ToLowerInvariant()), context.RequestAborted));
        }, true);
}
