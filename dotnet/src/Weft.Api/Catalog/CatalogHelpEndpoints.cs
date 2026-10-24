using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Weft.Api.Auth;
using Weft.Api.Help;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Contracts.Help;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    /// <summary>
    /// The user guide embedded in this build, behind the same catalog admission and fresh organization
    /// authority as other business reads. Mapping parses the guide, so an invalid guide fails startup.
    /// </summary>
    public static IEndpointRouteBuilder MapWeftHelp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var guide = UserGuide.Embedded;
        var routes = endpoints.BusinessRoutes();
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
        WithAdmission(context, boundary, async admission =>
        {
            RequireQuery(context);
            return await HelpAdmittedAsync(context, admission) ? Results.Ok(guide.Contents) : NotFound();
        });

    private static Task<IResult> GetHelpSection(string sectionId, HttpContext context, CatalogRequestBoundary boundary,
        IUserGuideReader guide) =>
        WithAdmission(context, boundary, async admission =>
        {
            RequireQuery(context);
            if (!UserGuide.IsSectionId(sectionId))
                throw new CatalogInvalidRequestException();
            if (!await HelpAdmittedAsync(context, admission))
                return NotFound();
            return guide.TryGetSection(sectionId, out var section) ? Results.Ok(section) : NotFound();
        });

    private static async Task<bool> HelpAdmittedAsync(HttpContext context, CatalogRequestAdmission admission)
    {
        var organization = await ProjectCreationPersistence(context, () => context.RequestServices.GetRequiredService<ICatalogReader>()
            .GetOrganizationAdmissionAsync(admission.Actor!, admission.AllowedOrganizationId!.Value, context.RequestAborted));
        if (organization is null)
            return false;
        RequireOrganization(organization, admission);
        return true;
    }
}
