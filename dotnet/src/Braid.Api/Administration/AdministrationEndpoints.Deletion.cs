using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Braid.Application.Lifecycle;
using Braid.Contracts.Administration;

namespace Braid.Api.Administration;

public static partial class AdministrationEndpoints
{
    internal const string OperationHeader = "Braid-Operation-Id";

    private static void MapDeletion(IEndpointRouteBuilder routes)
    {
        const string org = "/api/v1/organizations/{organizationId}";
        const string project = org + "/projects/{projectId}";
        DescribeDeletion(routes.MapDelete(org + "/previews/{runId}", (string organizationId, string runId, HttpContext context) =>
            DeleteItem(context, organizationId, null, null, runId, ResourceItemKind.Preview)), "DeletePreview", versioned: false);
        DescribeDeletion(routes.MapDelete(org + "/chat/conversations/{conversationId}", (string organizationId, string conversationId, HttpContext context) =>
            DeleteItem(context, organizationId, null, null, conversationId, ResourceItemKind.Conversation)), "DeleteChatConversation", versioned: false);
        DescribeDeletion(routes.MapDelete(project + "/mappings/{mappingId}/regression-cases/{caseId}",
            (string organizationId, string projectId, string mappingId, string caseId, HttpContext context) =>
                DeleteItem(context, organizationId, projectId, mappingId, caseId, ResourceItemKind.RegressionCase)), "DeleteRegressionCase", versioned: true);
        DescribeDeletion(routes.MapDelete(project + "/build-environments/{environmentId}",
            (string organizationId, string projectId, string environmentId, HttpContext context) =>
                DeleteItem(context, organizationId, projectId, null, environmentId, ResourceItemKind.BuildEnvironment)), "DeleteBuildEnvironment", versioned: true);
        Describe<ResourceDeletionReceiptV1>(routes.MapGet(org + "/deletions/{operationId}",
            (string organizationId, string operationId, HttpContext context) => ReadDeletion(context, organizationId, operationId)), "GetResourceDeletion")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                // Recovery reads are not deletions and take no precondition.
                if (operation.Responses?["200"] is OpenApiResponse response) response.Headers?.Remove("ETag");
                return Task.CompletedTask;
            });
    }

    private static Task<IResult> DeleteItem(HttpContext context, string organizationId, string? projectId, string? mappingId, string resourceId,
        ResourceItemKind kind) =>
        Handle(context, organizationId, projectId, true, async (_, actor, org, project) =>
        {
            RequireEmpty(context);
            var resource = Id(resourceId);
            var operations = context.Request.Headers[OperationHeader];
            if (operations.Count != 1 || operations[0] is not { } operationText) throw new InvalidInput();
            var operation = Id(operationText);
            long? expected = null;
            var conditions = context.Request.Headers.IfMatch;
            if (kind is ResourceItemKind.RegressionCase or ResourceItemKind.BuildEnvironment)
            {
                if (conditions.Count == 0) throw new MissingCondition();
                expected = conditions.Count == 1 ? ItemVersion(conditions[0]) : throw new InvalidInput();
            }
            else if (conditions.Count != 0) throw new InvalidInput();
            if (context.RequestServices.GetService<IResourceDeletionStore>() is not { } store) return Error("unavailable", 404);
            // One mutation per request; an unknown commit is resolved through the operation read, never by resending.
            var result = await store.DeleteAsync(actor, new(org, kind, resource, operation, project, mappingId is null ? null : Id(mappingId), expected),
                context.RequestAborted);
            return result.Outcome switch
            {
                ResourceDeletionOutcome.Deleted or ResourceDeletionOutcome.Replayed when result.Receipt is not null => Results.Ok(DeletionReceipt(result.Receipt)),
                ResourceDeletionOutcome.NotAuthorizedOrNotFound => Error("notFound", 404),
                ResourceDeletionOutcome.PreconditionFailed => Error("preconditionFailed", 412),
                ResourceDeletionOutcome.ActiveWork => Error("activeWork", 409),
                ResourceDeletionOutcome.ReferencedResource when result.References is { } r => Results.Json(new ResourceDeletionErrorV1("referencedResource",
                    new(Count(r.RegressionRuns), Count(r.AutomationEvaluations), Count(r.BuildApprovals), Count(r.ReleaseTargets))), statusCode: 409),
                ResourceDeletionOutcome.Conflict => Error("conflict", 409),
                ResourceDeletionOutcome.InvalidRequest => Error("invalidRequest", 400),
                _ => throw new InvalidOperationException()
            };
        });

    private static Task<IResult> ReadDeletion(HttpContext context, string organizationId, string operationId) =>
        Handle(context, organizationId, null, false, async (_, actor, org, _) =>
        {
            RequireEmpty(context);
            var operation = Id(operationId);
            if (context.RequestServices.GetService<IResourceDeletionStore>() is not { } store) return Error("unavailable", 404);
            var receipt = await store.ReadOperationAsync(actor, org, operation, context.RequestAborted);
            return receipt is null ? Error("notFound", 404) : Results.Ok(DeletionReceipt(receipt));
        });

    /// <summary>The exact strong version tag of a case head or an environment's latest version, for example <c>"3"</c>.</summary>
    internal static string ItemETag(long version) => "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";

    private static long ItemVersion(string? text) =>
        text is { Length: > 2 and <= 22 } && long.TryParse(text.AsSpan(1, text.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version) &&
        version > 0 && ItemETag(version) == text ? version : throw new InvalidInput();

    private static string? Count(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static ResourceDeletionReceiptV1 DeletionReceipt(ResourceDeletionReceipt receipt) => new(
        receipt.Outcome == ResourceDeletionOutcome.Deleted ? "deleted" : "replayed", receipt.OperationId,
        receipt.Kind switch
        {
            ResourceItemKind.Preview => "preview",
            ResourceItemKind.RegressionCase => "regressionCase",
            ResourceItemKind.BuildEnvironment => "buildEnvironment",
            _ => "conversation"
        }, receipt.ResourceId, receipt.ProjectId, receipt.MappingId, receipt.RecordedAtUtc);

    private static void DescribeDeletion(RouteHandlerBuilder route, string name, bool versioned) =>
        route.WithName(name).WithTags("Administration").WithSummary(name)
            .Produces<ResourceDeletionReceiptV1>().Produces<ResourceDeletionErrorV1>(400).Produces<ResourceDeletionErrorV1>(404)
            .Produces<ResourceDeletionErrorV1>(409).Produces<ResourceDeletionErrorV1>(412).Produces<ResourceDeletionErrorV1>(428)
            .Produces<ResourceDeletionErrorV1>(503).AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = OperationHeader,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Client-chosen operation UUID. Reusing it with the same request returns the original receipt; any change conflicts.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                });
                if (versioned)
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "If-Match",
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "Strong tag of the exact version being deleted: the case head version or the environment's latest version as a quoted decimal.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    });
                return Task.CompletedTask;
            });
}
