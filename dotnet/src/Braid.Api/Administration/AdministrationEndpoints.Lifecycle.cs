using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Braid.Application.Lifecycle;
using Braid.Contracts.Administration;
using Braid.Domain;

namespace Braid.Api.Administration;

public static partial class AdministrationEndpoints
{
    private static void MapLifecycle(IEndpointRouteBuilder routes)
    {
        const string org = "/api/v1/organizations/{organizationId}";
        const string project = org + "/projects/{projectId}";
        const string mapping = project + "/mappings/{mappingId}";
        LifecycleDescription<ResourceLifecycleV1>(routes.MapGet(project + "/lifecycle",
            (string organizationId, string projectId, HttpContext context) => ReadLifecycle(context, organizationId, projectId, null)), "GetProjectLifecycle");
        LifecycleDescription<ResourceLifecycleV1>(routes.MapGet(mapping + "/lifecycle",
            (string organizationId, string projectId, string mappingId, HttpContext context) => ReadLifecycle(context, organizationId, projectId, mappingId)), "GetMappingLifecycle");
        LifecycleDescription<ResourceLifecycleReceiptV1>(routes.MapPost(project + "/lifecycle/actions/{operationId}",
            (string organizationId, string projectId, string operationId, HttpContext context) => ChangeLifecycle(context, organizationId, projectId, null, operationId)), "ChangeProjectLifecycle")
            .Accepts<ResourceLifecycleActionV1>("application/json");
        LifecycleDescription<ResourceLifecycleReceiptV1>(routes.MapPost(mapping + "/lifecycle/actions/{operationId}",
            (string organizationId, string projectId, string mappingId, string operationId, HttpContext context) => ChangeLifecycle(context, organizationId, projectId, mappingId, operationId)), "ChangeMappingLifecycle")
            .Accepts<ResourceLifecycleActionV1>("application/json");
        LifecycleDescription<ResourceLifecycleReceiptV1>(routes.MapGet(org + "/lifecycle/operations/{operationId}",
            (string organizationId, string operationId, HttpContext context) => ReadLifecycleOperation(context, organizationId, operationId)), "GetLifecycleOperation");
    }

    private static Task<IResult> ReadLifecycle(HttpContext context, string organizationId, string projectId, string? mappingId) =>
        Handle(context, organizationId, projectId, false, async (_, actor, org, project) =>
        {
            RequireEmpty(context);
            var resource = new ResourceKey(new(org, project!.Value), mappingId is null ? null : Id(mappingId));
            if (context.RequestServices.GetService<IResourceLifecycleStore>() is not { } store) return Error("unavailable", 404);
            var state = await store.ReadAsync(actor, resource, context.RequestAborted);
            if (state is null) return Error("notFound", 404);
            if (state.Resource != resource) throw new InvalidOperationException();
            context.Response.Headers.ETag = ResourceLifecycleVersion.ETag(resource, state.Version);
            return Results.Ok(new ResourceLifecycleV1(resource.IsMapping ? "mapping" : "project", project.Value, resource.MappingId,
                state.DeletionJobId is not null ? "deleting" : state.Archived ? "archived" : "active",
                state.DeletionJobId is not null ? "deleting" : state.EffectivelyArchived ? "archived" : "active",
                Decimal(state.Version.Project), state.Version.Mapping is { } version ? Decimal(version) : null,
                state.ArchivedAtUtc, state.CanArchive, state.CanRestore, state.DeletionJobId));
        });

    private static Task<IResult> ChangeLifecycle(HttpContext context, string organizationId, string projectId, string? mappingId, string operationId) =>
        Handle(context, organizationId, projectId, true, async (_, actor, org, project) =>
        {
            var resource = new ResourceKey(new(org, project!.Value), mappingId is null ? null : Id(mappingId));
            var operation = Id(operationId);
            var values = context.Request.Headers.IfMatch;
            if (values.Count == 0) throw new MissingCondition();
            var expected = values.Count == 1 ? ResourceLifecycleVersion.ParseETag(resource, values[0]) : null;
            if (expected is null) throw new InvalidInput();
            var body = await Body<ResourceLifecycleActionV1>(context);
            var action = body.Action switch
            {
                "archive" => ResourceLifecycleAction.Archive,
                "restore" => ResourceLifecycleAction.Restore,
                _ => throw new InvalidInput()
            };
            if (context.RequestServices.GetService<IResourceLifecycleStore>() is not { } store) return Error("unavailable", 404);
            // One mutation per request; an unknown commit is resolved through the operation read, never by resending.
            var (outcome, receipt) = await store.ChangeAsync(actor, new(resource, action, operation, expected), context.RequestAborted);
            return outcome switch
            {
                ResourceLifecycleOutcome.Archived or ResourceLifecycleOutcome.Restored or ResourceLifecycleOutcome.Replayed when receipt is not null =>
                    Results.Ok(Receipt(receipt)),
                ResourceLifecycleOutcome.NotAuthorizedOrNotFound => Error("notFound", 404),
                ResourceLifecycleOutcome.PreconditionFailed => Error("preconditionFailed", 412),
                ResourceLifecycleOutcome.Conflict => Error("conflict", 409),
                ResourceLifecycleOutcome.InvalidRequest => Error("invalidRequest", 400),
                _ => throw new InvalidOperationException()
            };
        });

    private static Task<IResult> ReadLifecycleOperation(HttpContext context, string organizationId, string operationId) =>
        Handle(context, organizationId, null, false, async (_, actor, org, _) =>
        {
            RequireEmpty(context);
            var operation = Id(operationId);
            if (context.RequestServices.GetService<IResourceLifecycleStore>() is not { } store) return Error("unavailable", 404);
            var receipt = await store.ReadOperationAsync(actor, org, operation, context.RequestAborted);
            return receipt is null ? Error("notFound", 404) : Results.Ok(Receipt(receipt));
        });

    private static ResourceLifecycleReceiptV1 Receipt(ResourceLifecycleReceipt receipt) => new(
        receipt.Outcome switch
        {
            ResourceLifecycleOutcome.Archived => "archived",
            ResourceLifecycleOutcome.Restored => "restored",
            _ => "replayed"
        },
        receipt.OperationId, receipt.Resource.IsMapping ? "mapping" : "project", receipt.Resource.Project.ProjectId, receipt.Resource.MappingId,
        receipt.Action == ResourceLifecycleAction.Archive ? "archive" : "restore",
        receipt.Action == ResourceLifecycleAction.Archive ? "archived" : "active",
        Decimal(receipt.Version.Project), receipt.Version.Mapping is { } version ? Decimal(version) : null, receipt.RecordedAtUtc);

    private static void RequireEmpty(HttpContext context)
    {
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Content-Type") ||
            context.Request.Headers.ContainsKey("Transfer-Encoding") ||
            context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true) throw new InvalidInput();
    }

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static RouteHandlerBuilder LifecycleDescription<T>(RouteHandlerBuilder route, string name) =>
        Describe<T>(route, name).AddOpenApiOperationTransformer((operation, _, _) =>
        {
            foreach (var parameter in operation.Parameters ?? [])
                if (parameter.Name == "If-Match" && parameter is OpenApiParameter condition)
                    condition.Description = "Strong lifecycle ETag from the latest authorized lifecycle read of this exact project or mapping.";
            // Recovery reads are not lifecycle changes and take no precondition.
            if (name == "GetLifecycleOperation" && operation.Responses?["200"] is OpenApiResponse response) response.Headers?.Remove("ETag");
            return Task.CompletedTask;
        });
}
