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
    private static void MapResume(IEndpointRouteBuilder routes) =>
        Describe<DeletionJobV1>(routes.MapPost("/api/v1/organizations/{organizationId}/deletion-jobs/{jobId}/resume",
            (string organizationId, string jobId, HttpContext context) => ResumeDeletion(context, organizationId, jobId)), "ResumeDeletionJob")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                // Identified by its operation ID and exact body, not by an administration ETag.
                foreach (var condition in operation.Parameters.Where(item => item.Name == "If-Match").ToArray()) operation.Parameters.Remove(condition);
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = OperationHeader,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Client-chosen operation UUID. Reusing it for the same job returns the job; any other job conflicts.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                });
                if (operation.Responses?["200"] is OpenApiResponse response) response.Headers?.Remove("ETag");
                return Task.CompletedTask;
            });

    private static Task<IResult> ResumeDeletion(HttpContext context, string organizationId, string jobId) =>
        Handle(context, organizationId, null, true, async (_, actor, org, _) =>
        {
            RequireEmpty(context);
            var job = Id(jobId);
            var operations = context.Request.Headers[OperationHeader];
            if (operations.Count != 1 || operations[0] is not { } operationText) throw new InvalidInput();
            var operation = Id(operationText);
            if (context.RequestServices.GetService<IResourcePurgeStore>() is not { } store) return Error("unavailable", 404);
            var (outcome, status) = await store.ResumeAsync(actor, org, job, operation, context.RequestAborted);
            return outcome switch
            {
                DeletionResumeOutcome.Resumed or DeletionResumeOutcome.Replayed when status is not null => Results.Ok(Job(status)),
                DeletionResumeOutcome.NotAuthorizedOrNotFound => Error("notFound", 404),
                DeletionResumeOutcome.StillBlocked => Error("activeRelease", 409),
                DeletionResumeOutcome.Conflict => Error("conflict", 409),
                DeletionResumeOutcome.InvalidRequest => Error("invalidRequest", 400),
                _ => throw new InvalidOperationException()
            };
        });
}
