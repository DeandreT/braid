using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Weft.Application;
using Weft.Contracts.Catalog;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void DescribeSearch(RouteHandlerBuilder route)
    {
        Describe<CatalogSearchPageV1>(route, "SearchCatalog", "Search freshly authorized catalog metadata and retained report field/rule names and paths.");
        route.WithDescription("Literal substring search: ASCII case-insensitive, other Unicode exact. No query language, native derivation or sample payload reads. " +
            "Fresh principal, organization and project authority is checked for each page. Results retain exact revision/report/resource identities and plain text UTF-16 highlight spans. " +
            "Continuations bind the trimmed query and organization, not a permission grant or snapshot. A 503 response refuses incomplete report indexing; retry continues bounded backfill.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
                {
                    if (parameter.In != ParameterLocation.Query) continue;
                    if (parameter.Name == "q")
                    {
                        parameter.Required = true;
                        parameter.Description = "Trimmed literal text containing 1–256 UTF-8 bytes without controls. Unicode is preserved; %, _, quotes and backslashes are literal.";
                        parameter.Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 256 };
                    }
                    else if (parameter.Name == "limit")
                        parameter.Description = "Canonical decimal page size, 1–100; default 25.";
                    else if (parameter.Name == "cursor")
                        parameter.Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 4096, Pattern = "^[A-Za-z0-9_-]+$" };
                }
                if (operation.Responses?.TryGetValue("503", out var response) == true && response is OpenApiResponse pending)
                {
                    pending.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    pending.Headers["Retry-After"] = new OpenApiHeader
                    {
                        Description = "Two seconds when retained report indexing is incomplete or busy.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    };
                }
                return Task.CompletedTask;
            });
    }

    private static Task<IResult> SearchCatalog(string organizationId, HttpContext context,
        CatalogRequestBoundary boundary, string? q = null, string? limit = null, string? cursor = null) =>
        WithAdmission(context, boundary, async admission =>
        {
            var organization = Id(organizationId);
            if (organization != admission.AllowedOrganizationId) return NotFound();
            RequireQuery(context, "q", "limit", "cursor");
            var query = Decode(() => new CatalogSearchQuery(q ?? ""));
            var count = 25;
            if (limit is not null && (limit.Length > 3 || !int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
                count is < 1 or > 100 || limit != count.ToString(CultureInfo.InvariantCulture)))
                throw new CatalogInvalidRequestException();
            var continuation = cursor is null ? null : Decode(() => CatalogSearchCursorCodec.Decode(cursor, organization, query));
            try
            {
                var page = await context.RequestServices.GetRequiredService<ICatalogSearch>().SearchAsync(
                    admission.Actor!, organization, query, count, continuation, context.RequestAborted);
                if (page is null) return NotFound();
                RequireOrganization(page.Admission, admission);
                if (page.Query != query || page.Hits.Count > count) throw new InvalidOperationException("Inconsistent search page.");
                return Results.Ok(new CatalogSearchPageV1(query.Text, page.Hits.Select(hit => new CatalogSearchHitV1(
                    hit.Key.Kind switch
                    {
                        CatalogSearchKind.Project => CatalogSearchKindV1.Project,
                        CatalogSearchKind.Mapping => CatalogSearchKindV1.Mapping,
                        CatalogSearchKind.Revision => CatalogSearchKindV1.Revision,
                        CatalogSearchKind.Field => CatalogSearchKindV1.Field,
                        CatalogSearchKind.Rule => CatalogSearchKindV1.Rule,
                        _ => throw new InvalidOperationException("Unknown search resource.")
                    }, organization, hit.Key.Project.ProjectId, hit.Key.MappingId, hit.TargetRevisionId,
                    hit.Key.ProjectionDerivationId, hit.Key.ResourceId, SearchText(hit.Title), SearchText(hit.Description))).ToArray(),
                    page.NextCursor is { } next ? CatalogSearchCursorCodec.Encode(next) : null));
            }
            catch (CatalogSearchIndexPendingException)
            {
                context.Response.Headers.RetryAfter = "2";
                return Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
            }
            catch (InvalidOperationException) { return Error(CatalogErrorCodeV1.PersistenceFailure, StatusCodes.Status500InternalServerError); }
        });

    private static CatalogSearchTextV1 SearchText(CatalogSearchText value) =>
        new(value.Text, value.Highlights.Select(span => new CatalogSearchHighlightV1(span.Start, span.Length)).ToArray());
}
