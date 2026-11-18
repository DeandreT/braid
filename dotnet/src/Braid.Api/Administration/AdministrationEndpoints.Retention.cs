using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Braid.Application.Retention;
using Braid.Contracts.Administration;

namespace Braid.Api.Administration;

public static partial class AdministrationEndpoints
{
    private static void MapRetention(IEndpointRouteBuilder routes)
    {
        const string path = "/api/v1/organizations/{organizationId}/administration/retention";
        RetentionDescription<RetentionPoliciesV1>(routes.MapGet(path, (string organizationId, HttpContext context) =>
            Handle(context, organizationId, null, false, async (_, actor, org, _) =>
            {
                if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Content-Type") ||
                    context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                    context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true) throw new InvalidInput();
                if (context.RequestServices.GetService<IRetentionPolicyStore>() is not { } store) return Error("unavailable", 404);
                var result = await store.ReadRetentionAsync(actor, org, context.RequestAborted);
                if (result is null) return Error("notFound", 404);
                if (result.OrganizationId != org) throw new InvalidOperationException();
                context.Response.Headers.ETag = RetentionFamilyPolicy.ETag(org, RetentionFamilyPolicy.ParseCount(result.Version)!.Value);
                return Results.Ok(result);
            })), "GetOrganizationRetention");
        RetentionDescription<RetentionPoliciesWriteResultV1>(routes.MapPut(path, (string organizationId, HttpContext context) =>
            Handle(context, organizationId, null, true, async (_, actor, org, _) =>
            {
                var values = context.Request.Headers.IfMatch;
                if (values.Count == 0) throw new MissingCondition();
                var version = values.Count == 1 ? RetentionFamilyPolicy.ParseETag(org, values[0]) : null;
                if (version is null) throw new InvalidInput();
                var body = await Body<RetentionPoliciesWriteV1>(context);
                IReadOnlyList<RetentionFamilyPolicy> policies;
                try
                {
                    if (body.Policies is null || body.Policies.Count != RetentionFamilyPolicy.Families.Length) throw new InvalidInput();
                    policies = RetentionFamilyPolicy.Complete(body.Policies.Select(item => new RetentionFamilyPolicy(
                        RetentionFamilyPolicy.ParseFamily(item.Family), RetentionFamilyPolicy.ParseMode(item.Mode),
                        RetentionFamilyPolicy.ParseCount(item.DurationSeconds), item.ApplyToExisting)));
                }
                catch (ArgumentException) { throw new InvalidInput(); }
                if (context.RequestServices.GetService<IRetentionPolicyStore>() is not { } store) return Error("unavailable", 404);
                return await store.ReplaceRetentionAsync(actor, org, version.Value, policies, context.RequestAborted) switch
                {
                    RetentionPolicyOutcome.Updated => Results.Ok(new RetentionPoliciesWriteResultV1("updated")),
                    RetentionPolicyOutcome.Unchanged => Results.Ok(new RetentionPoliciesWriteResultV1("unchanged")),
                    RetentionPolicyOutcome.NotAuthorizedOrNotFound => Error("notFound", 404),
                    RetentionPolicyOutcome.PreconditionFailed => Error("preconditionFailed", 412),
                    RetentionPolicyOutcome.Conflict => Error("conflict", 409),
                    _ => Error("invalidRequest", 400)
                };
            })), "PutOrganizationRetention").Accepts<RetentionPoliciesWriteV1>("application/json");
    }

    private static RouteHandlerBuilder RetentionDescription<T>(RouteHandlerBuilder route, string name) =>
        Describe<T>(route, name).AddOpenApiOperationTransformer((operation, _, _) =>
        {
            foreach (var parameter in operation.Parameters ?? [])
                if (parameter.Name == "If-Match" && parameter is OpenApiParameter condition)
                    condition.Description = "Strong retention ETag from the latest authorized read of this organization's policy.";
            return Task.CompletedTask;
        });
}
