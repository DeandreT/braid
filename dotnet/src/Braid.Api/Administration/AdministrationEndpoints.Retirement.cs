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
    private static void MapRetirement(IEndpointRouteBuilder routes)
    {
        const string org = "/api/v1/organizations/{organizationId}";
        Describe<TargetRetirementReceiptV1>(routes.MapPost(org + "/projects/{projectId}/release-targets/{targetId}/retire",
            (string organizationId, string projectId, string targetId, HttpContext context) => Retire(context, organizationId, projectId, targetId)),
            "RetireReleaseTarget").Accepts<TargetRetirementRequestV1>("application/json")
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
                    Description = "Client-chosen operation UUID. Reusing it with the same request returns the original receipt; any change conflicts.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                });
                if (operation.Responses?["200"] is OpenApiResponse response) response.Headers?.Remove("ETag");
                return Task.CompletedTask;
            });
        Describe<TargetRetirementReceiptV1>(routes.MapGet(org + "/release-target-retirements/{operationId}",
            (string organizationId, string operationId, HttpContext context) => ReadRetirement(context, organizationId, operationId)), "GetReleaseTargetRetirement")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                if (operation.Responses?["200"] is OpenApiResponse response) response.Headers?.Remove("ETag");
                return Task.CompletedTask;
            });
    }

    private static Task<IResult> Retire(HttpContext context, string organizationId, string projectId, string targetId) =>
        Handle(context, organizationId, projectId, true, async (_, actor, org, project) =>
        {
            var target = Id(targetId);
            var operations = context.Request.Headers[OperationHeader];
            if (operations.Count != 1 || operations[0] is not { } operationText) throw new InvalidInput();
            var operation = Id(operationText);
            var body = await Body<TargetRetirementRequestV1>(context);
            if (!long.TryParse(body.ExpectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) || expected < 1 ||
                expected.ToString(CultureInfo.InvariantCulture) != body.ExpectedVersion || body.ActiveReleaseId == Guid.Empty) throw new InvalidInput();
            if (context.RequestServices.GetService<ITargetRetirementStore>() is not { } store) return Error("unavailable", 404);
            // One mutation per request; an unknown commit is resolved through the operation read, never by resending.
            var (outcome, receipt) = await store.RetireAsync(actor, new(org, project!.Value, target, operation, expected, body.ActiveReleaseId), context.RequestAborted);
            return outcome switch
            {
                TargetRetirementOutcome.Retired or TargetRetirementOutcome.Replayed when receipt is not null => Results.Ok(RetirementReceipt(receipt)),
                TargetRetirementOutcome.NotAuthorizedOrNotFound => Error("notFound", 404),
                TargetRetirementOutcome.PreconditionFailed => Error("preconditionFailed", 412),
                TargetRetirementOutcome.Conflict => Error("conflict", 409),
                TargetRetirementOutcome.InvalidRequest => Error("invalidRequest", 400),
                _ => throw new InvalidOperationException()
            };
        });

    private static Task<IResult> ReadRetirement(HttpContext context, string organizationId, string operationId) =>
        Handle(context, organizationId, null, false, async (_, actor, org, _) =>
        {
            RequireEmpty(context);
            var operation = Id(operationId);
            if (context.RequestServices.GetService<ITargetRetirementStore>() is not { } store) return Error("unavailable", 404);
            var receipt = await store.ReadRetirementAsync(actor, org, operation, context.RequestAborted);
            return receipt is null ? Error("notFound", 404) : Results.Ok(RetirementReceipt(receipt));
        });

    private static TargetRetirementReceiptV1 RetirementReceipt(TargetRetirementReceipt receipt) => new(
        receipt.Outcome == TargetRetirementOutcome.Retired ? "retired" : "replayed", receipt.OperationId, receipt.ProjectId, receipt.MappingId,
        receipt.TargetId, receipt.RetiredReleaseId, Decimal(receipt.TargetVersion), receipt.RecordedAtUtc);
}
