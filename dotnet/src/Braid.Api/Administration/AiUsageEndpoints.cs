using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Administration;

namespace Braid.Api.Administration;

internal static class AiUsageEndpoints
{
    internal static void MapAiUsage(this IEndpointRouteBuilder endpoints)
    {
        var route = endpoints.MapGet("/api/v1/organizations/{organizationId}/usage",
            (string organizationId, HttpContext context) => Read(context, organizationId));
        route.WithName("GetAiUsage").WithTags("Administration")
            .WithSummary("Read administrator-scoped AI usage; unknown tokens remain unknown and historical coverage is explicit.")
            .Produces<AiUsagePageV1>();
        foreach (var status in new[] { 400, 401, 403, 404, 429, 503 }) route.Produces<AdministrationErrorV1>(status);
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            foreach (var (name, description) in new[]
            {
                ("projectId", "Optional current project scope."), ("principalId", "Optional principal UUID."),
                ("profileId", "Optional exact model profile identifier."), ("profileVersion", "Optional profile version; requires profileId."),
                ("groupBy", "organization (default), project, principal or modelProfile."),
                ("since", "Inclusive UTC admission time. Defaults to 30 days before until."),
                ("until", "Exclusive UTC admission time. Defaults to now; reuse the returned window when paging."),
                ("limit", "Groups per page, 1–100; default 50."), ("cursor", "Opaque continuation bound to scope, filters, grouping and time window.")
            }) operation.Parameters.Add(new OpenApiParameter
            {
                Name = name,
                In = ParameterLocation.Query,
                Description = description,
                Schema = name is "limit" or "profileVersion" ? new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" }
                    : new OpenApiSchema { Type = JsonSchemaType.String, Format = name is "since" or "until" ? "date-time" : name is "projectId" or "principalId" ? "uuid" : null }
            });
            return Task.CompletedTask;
        });
    }

    private static async Task<IResult> Read(HttpContext context, string organizationId)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            var admission = await context.RequestServices.GetRequiredService<CatalogRequestBoundary>().AuthenticateAsync(context, false, context.RequestAborted);
            if (!admission.IsAdmitted) return Error("identityRejected", admission.StatusCode);
            var org = Id(organizationId);
            if (org != admission.AllowedOrganizationId) return Error("notFound", 404);
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Content-Type") ||
                context.Request.Headers.ContainsKey("Content-Encoding") || context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true) throw new ArgumentException();
            string[] allowed = ["projectId", "principalId", "profileId", "profileVersion", "groupBy", "since", "until", "limit", "cursor"];
            if (context.Request.Query.Any(item => !allowed.Contains(item.Key, StringComparer.Ordinal) || item.Value.Count != 1)) throw new ArgumentException();
            string? Value(string name) => context.Request.Query.TryGetValue(name, out var value) ? value[0] : null;
            Guid? OptionalId(string name) => Value(name) is { } value ? Id(value) : null;
            int? Number(string name) => Value(name) is { } value ? int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
                value == number.ToString(CultureInfo.InvariantCulture) ? number : throw new ArgumentException() : null;
            var until = Value("until") is { } end ? Date(end) : AuditLogQuery.Microseconds((context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow());
            var since = Value("since") is { } start ? Date(start) : until.AddDays(-30);
            var grouping = Value("groupBy") switch
            {
                null or "organization" => AiUsageGrouping.Organization,
                "project" => AiUsageGrouping.Project,
                "principal" => AiUsageGrouping.Principal,
                "modelProfile" => AiUsageGrouping.ModelProfile,
                _ => throw new ArgumentException()
            };
            var cursor = Value("cursor");
            if (cursor is not null && (Value("since") is null || Value("until") is null)) throw new ArgumentException();
            var query = new AiUsageQuery(org, since, until, grouping, OptionalId("projectId"), OptionalId("principalId"),
                Value("profileId"), Number("profileVersion"), Number("limit") ?? 50, cursor);
            if (context.RequestServices.GetService<IAiUsageReader>() is not { } reader) return Error("unavailable", 404);
            var page = await reader.ReadUsageAsync(admission.Actor!, query, context.RequestAborted);
            return page is null ? Error("notFound", 404) : Results.Ok(page);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { return Error("invalidRequest", 400); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return Error("unavailable", 503); }
    }

    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D") ? id : throw new ArgumentException();
    private static DateTimeOffset Date(string value) => value.Length <= 40 && value.Contains('T') &&
        (value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal)) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Offset == TimeSpan.Zero ? date : throw new ArgumentException();
    private static IResult Error(string code, int status) => Results.Json(new AdministrationErrorV1(code), statusCode: status);
}
