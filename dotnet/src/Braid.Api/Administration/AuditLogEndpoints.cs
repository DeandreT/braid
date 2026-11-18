using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Braid.Api.Auth;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Administration;

namespace Braid.Api.Administration;

internal static class AuditLogEndpoints
{
    internal static void MapAuditLog(this IEndpointRouteBuilder endpoints)
    {
        const string path = "/api/v1/organizations/{organizationId}/audit";
        Describe(endpoints.MapGet(path, (string organizationId, HttpContext context) => Read(context, organizationId, false)), "GetAuditLog")
            .Produces<AuditLogPageV1>();
        Describe(endpoints.MapGet(path + "/export", (string organizationId, HttpContext context) => Read(context, organizationId, true)), "ExportAuditLog")
            .Produces<byte[]>(StatusCodes.Status200OK, "text/csv")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                if (operation.Responses?.TryGetValue("200", out var response) == true && response is OpenApiResponse success)
                {
                    success.Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["text/csv"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }
                    };
                    success.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    foreach (var name in new[] { "Since", "Until" })
                        success.Headers["X-Braid-Audit-" + name] = new OpenApiHeader
                        {
                            Description = name == "Since" ? "Inclusive UTC start of the fixed audit window." : "Exclusive UTC end of the fixed audit window.",
                            Required = true,
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" }
                        };
                    success.Headers["X-Braid-Audit-Next-Cursor"] = new OpenApiHeader
                    {
                        Description = "Continuation for the same filters and fixed time window; omitted on the final page.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 1024 }
                    };
                }
                return Task.CompletedTask;
            });
    }

    private static RouteHandlerBuilder Describe(RouteHandlerBuilder route, string name)
    {
        route.WithName(name).WithTags("Administration").WithSummary("Read retained metadata-only audit events under current administrator authority.");
        foreach (var status in new[] { 400, 401, 403, 404, 429, 503 }) route.Produces<AdministrationErrorV1>(status);
        return route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            foreach (var (parameter, description) in new[]
            {
                ("projectId", "Optional project UUID. Project administrators see only their current projects; organization events remain organization-administrator-only."),
                ("actorId", "Optional user or machine object UUID; unknown/system actors do not match."),
                ("kind", "administration, release, build, approval, request, payload, grant or invocation."),
                ("since", "Inclusive UTC ISO timestamp. Default: 30 days before until. Reuse the returned window when paging."),
                ("until", "Exclusive UTC ISO timestamp. Default: now. Required with since when using a cursor."),
                ("limit", "Maximum events on this page: 1–100, default 50."),
                ("cursor", "Opaque continuation from this exact scope, filters and time window.")
            }) operation.Parameters.Add(new OpenApiParameter
            {
                Name = parameter,
                In = ParameterLocation.Query,
                Description = description,
                Schema = parameter == "limit" ? new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100" }
                    : new OpenApiSchema { Type = JsonSchemaType.String, Format = parameter is "since" or "until" ? "date-time" : null }
            });
            return Task.CompletedTask;
        });
    }

    private static async Task<IResult> Read(HttpContext context, string organizationId, bool csv)
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
            var query = context.Request.Query;
            string[] allowed = ["projectId", "actorId", "kind", "since", "until", "limit", "cursor"];
            if (query.Any(p => !allowed.Contains(p.Key, StringComparer.Ordinal) || p.Value.Count != 1)) throw new ArgumentException();
            string? Value(string name) => query.TryGetValue(name, out var value) ? value[0] : null;
            Guid? OptionalId(string name) => Value(name) is { } value ? Id(value) : null;
            var until = Value("until") is { } end ? Date(end) : AuditLogQuery.Microseconds((context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow());
            var since = Value("since") is { } start ? Date(start) : until.AddDays(-30);
            var cursor = Value("cursor");
            if (cursor is not null && (Value("since") is null || Value("until") is null)) throw new ArgumentException();
            var limit = Value("limit") is { } count ? int.Parse(count, NumberStyles.None, CultureInfo.InvariantCulture) : 50;
            if (Value("limit") is { } literal && literal != limit.ToString(CultureInfo.InvariantCulture)) throw new ArgumentException();
            var request = new AuditLogQuery(org, OptionalId("projectId"), OptionalId("actorId"), Value("kind"), since, until, limit, cursor);
            if (context.RequestServices.GetService<IAuditLogReader>() is not { } reader) return Error("unavailable", 404);
            var page = await reader.ReadAuditAsync(admission.Actor!, org, request, context.RequestAborted);
            if (page is null) return Error("notFound", 404);
            if (!csv) return Results.Ok(page);
            context.Response.Headers["X-Braid-Audit-Since"] = page.Since.ToString("O", CultureInfo.InvariantCulture);
            context.Response.Headers["X-Braid-Audit-Until"] = page.Until.ToString("O", CultureInfo.InvariantCulture);
            if (page.NextCursor is { } next) context.Response.Headers["X-Braid-Audit-Next-Cursor"] = next;
            return Results.File(Encoding.UTF8.GetBytes(AuditLogCsv.Write(page)), "text/csv; charset=utf-8", "braid-audit.csv");
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { return Error("invalidRequest", 400); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return Error("unavailable", 503); }
    }

    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D")
        ? id : throw new ArgumentException();
    private static DateTimeOffset Date(string value) => value.Length <= 40 && value.Contains('T') &&
        (value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal)) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Offset == TimeSpan.Zero
        ? date : throw new ArgumentException();
    private static IResult Error(string code, int status) => Results.Json(new AdministrationErrorV1(code), statusCode: status);
}
