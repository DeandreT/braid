using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Braid.Application;
using Braid.Contracts.Administration;

namespace Braid.Api.Administration;

public static partial class AdministrationEndpoints
{
    private static void MapQuotas(IEndpointRouteBuilder routes)
    {
        const string root = "/api/v1/organizations/{organizationId}/administration";
        QuotaDescription<AiQuotaSnapshotV1>(routes.MapGet(root + "/ai-quotas",
            (string organizationId, HttpContext context) => ReadQuota(context, organizationId, null)), "GetOrganizationAiQuotas");
        QuotaDescription<AiQuotaSnapshotV1>(routes.MapGet(root + "/principals/{principalId}/ai-quotas",
            (string organizationId, string principalId, HttpContext context) => ReadQuota(context, organizationId, principalId)), "GetPrincipalAiQuotas");
        QuotaDescription<AiQuotaWriteResultV1>(routes.MapPut(root + "/ai-quotas/{period}",
            (string organizationId, string period, HttpContext context) => WriteQuota(context, organizationId, null, period)), "PutOrganizationAiQuota")
            .Accepts<AiQuotaWriteV1>("application/json");
        QuotaDescription<AiQuotaWriteResultV1>(routes.MapPut(root + "/principals/{principalId}/ai-quotas/{period}",
            (string organizationId, string principalId, string period, HttpContext context) => WriteQuota(context, organizationId, principalId, period)), "PutPrincipalAiQuota")
            .Accepts<AiQuotaWriteV1>("application/json");
    }

    private static Task<IResult> ReadQuota(HttpContext context, string organizationId, string? principalId) =>
        Handle(context, organizationId, null, false, async (_, actor, org, _) =>
        {
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Content-Type") ||
                context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true) throw new InvalidInput();
            var principal = principalId is null ? (Guid?)null : Id(principalId);
            if (context.RequestServices.GetService<IAiQuotaStore>() is not { } store) return Error("unavailable", 404);
            var result = await store.ReadQuotasAsync(actor, org, principal, context.RequestAborted);
            if (result is null) return Error("notFound", 404);
            var version = AiQuotaPolicy.ParseLimit(result.Version) ?? throw new InvalidOperationException();
            if (result.OrganizationId != org || result.PrincipalId != principal) throw new InvalidOperationException();
            context.Response.Headers.ETag = QuotaETag(org, principal, version);
            return Results.Ok(result);
        });

    private static Task<IResult> WriteQuota(HttpContext context, string organizationId, string? principalId, string period) =>
        Handle(context, organizationId, null, true, async (_, actor, org, _) =>
        {
            var principal = principalId is null ? (Guid?)null : Id(principalId);
            var version = QuotaExpected(context, org, principal);
            var body = await Body<AiQuotaWriteV1>(context);
            AiQuotaPolicy policy;
            try { policy = new(principal, AiQuotaPolicy.ParsePeriod(period), AiQuotaPolicy.ParseLimit(body.MaximumTurns), AiQuotaPolicy.ParseLimit(body.MaximumTokens)); }
            catch (ArgumentException) { throw new InvalidInput(); }
            if (context.RequestServices.GetService<IAiQuotaStore>() is not { } store) return Error("unavailable", 404);
            var outcome = await store.WriteQuotaAsync(actor, org, version, policy, context.RequestAborted);
            return outcome switch
            {
                "updated" or "unchanged" => Results.Ok(new AiQuotaWriteResultV1(outcome)),
                "refused" => Error("notFound", 404),
                "preconditionFailed" => Error(outcome, 412),
                "invalidRequest" => Error(outcome, 400),
                _ => throw new InvalidOperationException()
            };
        });

    internal static string QuotaETag(Guid org, Guid? principal, long version) =>
        $"\"ai-quota-{org:D}-{(principal is null ? "organization" : principal.Value.ToString("D"))}-{version.ToString(CultureInfo.InvariantCulture)}\"";

    private static long QuotaExpected(HttpContext context, Guid org, Guid? principal)
    {
        var values = context.Request.Headers.IfMatch;
        if (values.Count == 0) throw new MissingCondition();
        var prefix = QuotaETag(org, principal, 1)[..^2];
        if (values.Count != 1 || values[0] is not { } text || !text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith('"') ||
            !long.TryParse(text.AsSpan(prefix.Length, text.Length - prefix.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version < 1 || QuotaETag(org, principal, version) != text) throw new InvalidInput();
        return version;
    }

    private static RouteHandlerBuilder QuotaDescription<T>(RouteHandlerBuilder route, string name) =>
        Describe<T>(route, name).AddOpenApiOperationTransformer((operation, _, _) =>
        {
            foreach (var parameter in operation.Parameters ?? [])
            {
                if (parameter.Name == "If-Match" && parameter is OpenApiParameter condition)
                    condition.Description = "Strong quota ETag from the latest authorized read of this organization and principal scope.";
                if (parameter.Name == "period" && parameter is OpenApiParameter period)
                    period.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^(hour|day|month)$" };
            }
            return Task.CompletedTask;
        });
}
