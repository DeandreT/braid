using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Braid.Api.Auth;
using Braid.Api.Help;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Contracts.Help;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    /// <summary>
    /// The embedded user guide requires authenticated identity but no business membership.
    /// Mapping parses the guide, so an invalid guide fails startup.
    /// </summary>
    public static IEndpointRouteBuilder MapBraidHelp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var guide = UserGuide.Embedded;
        var routes = endpoints.IdentityRoutes();
        DescribeHelp<HelpSectionListV1>(routes.MapGet("/api/v1/help/sections",
            (HttpContext context, CatalogRequestBoundary boundary) => GetHelpSections(context, boundary, guide)),
            "GetHelpSections", "Read the user guide's sections in reading order.");
        DescribeHelp<HelpSectionV1>(routes.MapGet("/api/v1/help/sections/{sectionId}",
            (string sectionId, HttpContext context, CatalogRequestBoundary boundary) => GetHelpSection(sectionId, context, boundary, guide)),
            "GetHelpSection", "Read one user guide section as structured text.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                if (operation.Parameters?.SingleOrDefault(parameter => parameter.Name == "sectionId") is OpenApiParameter { Schema: OpenApiSchema schema })
                {
                    schema.Pattern = "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$";
                    schema.MaxLength = UserGuide.MaximumSectionIdCharacters;
                }
                return Task.CompletedTask;
            });
        return endpoints;
    }

    private static RouteHandlerBuilder DescribeHelp<T>(RouteHandlerBuilder route, string name, string summary) =>
        route.WithName(name).WithTags("Help").WithSummary(summary)
            .Produces<T>(StatusCodes.Status200OK)
            .Produces<CatalogErrorV1>(StatusCodes.Status400BadRequest)
            .Produces<CatalogErrorV1>(StatusCodes.Status401Unauthorized)
            .Produces<CatalogErrorV1>(StatusCodes.Status403Forbidden)
            .Produces<CatalogErrorV1>(StatusCodes.Status404NotFound)
            .Produces<CatalogErrorV1>(StatusCodes.Status500InternalServerError)
            .Produces<CatalogErrorV1>(StatusCodes.Status503ServiceUnavailable);

    private static Task<IResult> GetHelpSections(HttpContext context, CatalogRequestBoundary boundary, IUserGuideReader guide) =>
        WithAdmission(context, boundary, _ =>
        {
            RequireQuery(context);
            return Task.FromResult<IResult>(Results.Ok(guide.Contents));
        }, identityOnly: true);

    private static Task<IResult> GetHelpSection(string sectionId, HttpContext context, CatalogRequestBoundary boundary,
        IUserGuideReader guide) =>
        WithAdmission(context, boundary, _ =>
        {
            RequireQuery(context);
            if (!UserGuide.IsSectionId(sectionId))
                throw new CatalogInvalidRequestException();
            return Task.FromResult(guide.TryGetSection(sectionId, out var section) ? Results.Ok(section) : NotFound());
        }, identityOnly: true);
}
