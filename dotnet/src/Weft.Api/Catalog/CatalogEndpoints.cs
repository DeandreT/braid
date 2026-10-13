using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapWeftCatalog(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Describe<CatalogContextV1>(endpoints.MapGet("/api/v1/catalog/context", GetContext),
            "GetCatalogContext", "Read the explicitly configured development catalog context and request token.");
        const string organization = "/api/v1/organizations/{organizationId}";
        const string project = organization + "/projects/{projectId}";
        const string mapping = project + "/mappings/{mappingId}";
        Describe<CatalogProjectPageV1>(endpoints.MapGet(organization + "/projects", GetProjects),
            "GetCatalogProjects", "Read a bounded page of currently visible projects.");
        Describe<CatalogProjectV1>(endpoints.MapGet(project, GetProject),
            "GetCatalogProject", "Read project metadata and current supported actions.");
        Describe<CatalogMappingPageV1>(endpoints.MapGet(project + "/mappings", GetMappings),
            "GetCatalogMappings", "Read a bounded page of mappings and committed heads.");
        Describe<CatalogMappingV1>(endpoints.MapGet(mapping, GetMapping),
            "GetCatalogMapping", "Read mapping metadata and its current committed head.");
        Describe<CatalogMappingMetadataV1>(endpoints.MapGet(mapping + "/metadata", GetMetadata),
            "GetCatalogMappingMetadata", "Read the exact mapping metadata representation.");
        Describe<CatalogRevisionPageV1>(endpoints.MapGet(mapping + "/revisions", GetRevisions),
            "GetCatalogRevisions", "Read immutable revision history in descending committed sequence.");
        Describe<CatalogRevisionV1>(endpoints.MapGet(mapping + "/revisions/{revisionId}", GetRevision),
            "GetCatalogRevision", "Read one scoped immutable revision's catalog metadata.");
        return endpoints;
    }

    private static void Describe<T>(RouteHandlerBuilder route, string name, string summary) =>
        route.WithName(name).WithTags("Catalog").WithSummary(summary)
            .Produces<T>(StatusCodes.Status200OK)
            .Produces<CatalogErrorV1>(StatusCodes.Status400BadRequest)
            .Produces<CatalogErrorV1>(StatusCodes.Status403Forbidden)
            .Produces<CatalogErrorV1>(StatusCodes.Status404NotFound)
            .Produces<CatalogErrorV1>(StatusCodes.Status500InternalServerError);

    private static Task<IResult> GetContext(HttpContext context, CatalogRequestBoundary boundary) =>
        WithAdmission(context, boundary, async admission =>
        {
            RequireQuery(context);
            return await Read(context, reader => reader.GetOrganizationAdmissionAsync(admission.Actor!,
                admission.AllowedOrganizationId!.Value, context.RequestAborted), organization =>
            {
                RequireOrganization(organization, admission);
                var token = boundary.IssueRequestToken(context, admission.Actor!);
                return Results.Ok(CatalogPublicMapper.Context(organization, token));
            });
        });

    private static Task<IResult> GetProjects(string organizationId, HttpContext context,
        CatalogRequestBoundary boundary, string? limit = null, string? cursor = null) =>
        WithAdmission(context, boundary, async admission =>
        {
            var organization = Id(organizationId);
            if (organization != admission.AllowedOrganizationId)
                return NotFound();
            var page = PageRequest(context);
            var continuation = page.Cursor is { } encoded ? Decode(() => CatalogCursorCodec.DecodeProjects(encoded, organization)) : null;
            return await Read(context, reader => reader.ListProjectsAsync(admission.Actor!, organization,
                page.Limit, continuation, context.RequestAborted), projects =>
            {
                RequireOrganization(projects.Admission, admission);
                foreach (var item in projects.Projects)
                    RequireAccess(item.Access, admission.Actor!, item.Summary.Scope);
                return Results.Ok(CatalogPublicMapper.Projects(projects));
            });
        });

    private static Task<IResult> GetProject(string organizationId, string projectId,
        HttpContext context, CatalogRequestBoundary boundary) =>
        WithAdmission(context, boundary, async admission =>
        {
            RequireQuery(context);
            var scope = Project(organizationId, projectId);
            if (scope.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            return await Read(context, reader => reader.GetProjectAsync(admission.Actor!, scope, context.RequestAborted), project =>
            {
                RequireScope(project.Summary.Scope == scope);
                RequireAccess(project.Access, admission.Actor!, scope);
                return Results.Ok(CatalogPublicMapper.Project(project));
            });
        });

    private static Task<IResult> GetMappings(string organizationId, string projectId,
        HttpContext context, CatalogRequestBoundary boundary, string? limit = null, string? cursor = null) =>
        WithAdmission(context, boundary, async admission =>
        {
            var scope = Project(organizationId, projectId);
            if (scope.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            var page = PageRequest(context);
            var continuation = page.Cursor is { } encoded ? Decode(() => CatalogCursorCodec.DecodeMappings(encoded, scope)) : null;
            return await Read(context, reader => reader.ListMappingsAsync(admission.Actor!, scope,
                page.Limit, continuation, context.RequestAborted), mappings =>
            {
                RequireAccess(mappings.Access, admission.Actor!, scope);
                return Results.Ok(CatalogPublicMapper.Mappings(mappings));
            });
        });

    private static Task<IResult> GetMapping(string organizationId, string projectId, string mappingId,
        HttpContext context, CatalogRequestBoundary boundary) =>
        WithAdmission(context, boundary, async admission =>
        {
            RequireQuery(context);
            var scope = Mapping(organizationId, projectId, mappingId);
            if (scope.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            return await Read(context, reader => reader.GetMappingAsync(admission.Actor!, scope, context.RequestAborted), mapping =>
            {
                RequireScope(mapping.Summary.Scope == scope);
                RequireAccess(mapping.Access, admission.Actor!, Project(scope));
                return Results.Ok(CatalogPublicMapper.Mapping(mapping));
            });
        });

    private static Task<IResult> GetMetadata(string organizationId, string projectId, string mappingId,
        HttpContext context, CatalogRequestBoundary boundary) =>
        WithAdmission(context, boundary, async admission =>
        {
            RequireQuery(context);
            var scope = Mapping(organizationId, projectId, mappingId);
            if (scope.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            return await Read(context, reader => reader.GetMappingMetadataAsync(admission.Actor!, scope, context.RequestAborted), metadata =>
            {
                RequireScope(metadata.Scope == scope);
                return Results.Ok(CatalogPublicMapper.Metadata(metadata));
            });
        });

    private static Task<IResult> GetRevisions(string organizationId, string projectId, string mappingId,
        HttpContext context, CatalogRequestBoundary boundary, string? limit = null, string? cursor = null) =>
        WithAdmission(context, boundary, async admission =>
        {
            var scope = Mapping(organizationId, projectId, mappingId);
            if (scope.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            var page = PageRequest(context);
            var continuation = page.Cursor is { } encoded ? Decode(() => CatalogCursorCodec.DecodeRevisions(encoded, scope)) : null;
            return await Read(context, reader => reader.ListRevisionsAsync(admission.Actor!, scope,
                page.Limit, continuation, context.RequestAborted), revisions =>
            {
                RequireScope(revisions.Mapping == scope);
                RequireAccess(revisions.Access, admission.Actor!, Project(scope));
                return Results.Ok(CatalogPublicMapper.Revisions(revisions));
            });
        });

    private static Task<IResult> GetRevision(string organizationId, string projectId, string mappingId,
        string revisionId, HttpContext context, CatalogRequestBoundary boundary) =>
        WithAdmission(context, boundary, async admission =>
        {
            RequireQuery(context);
            var mapping = Mapping(organizationId, projectId, mappingId);
            var scope = new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, Id(revisionId));
            if (scope.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            return await Read(context, reader => reader.GetRevisionAsync(admission.Actor!, scope, context.RequestAborted), revision =>
            {
                RequireScope(revision.Summary.Scope == scope);
                RequireAccess(revision.Access, admission.Actor!, Project(mapping));
                return Results.Ok(CatalogPublicMapper.Revision(revision));
            });
        });

    private static async Task<IResult> WithAdmission(HttpContext context, CatalogRequestBoundary boundary,
        Func<CatalogRequestAdmission, Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var admission = await boundary.AuthenticateAsync(context, unsafeRequest: false, context.RequestAborted);
            if (!admission.IsAdmitted)
                return Error(admission.Error!.Value, admission.StatusCode);
            return await action(admission);
        }
        catch (CatalogInvalidRequestException)
        {
            return Error(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            throw;
        }
        catch (Exception)
        {
            return Error(CatalogErrorCodeV1.InternalFailure, StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> Read<T>(HttpContext context,
        Func<ICatalogReader, Task<T?>> read, Func<T, IResult> map) where T : class
    {
        T? value;
        try
        {
            // Deferred resolution keeps disabled and malformed requests away from the catalog pool.
            value = await read(context.RequestServices.GetRequiredService<ICatalogReader>());
            context.RequestAborted.ThrowIfCancellationRequested();
        }
        catch (InvalidOperationException)
        {
            return Error(CatalogErrorCodeV1.PersistenceFailure, StatusCodes.Status500InternalServerError);
        }
        return value is null ? NotFound() : map(value);
    }

    private static (int Limit, string? Cursor) PageRequest(HttpContext context)
    {
        RequireQuery(context, "limit", "cursor");
        var limit = 50;
        if (context.Request.Query.TryGetValue("limit", out var specified))
        {
            var text = specified[0];
            if (text is null || text.Length > 3 || !int.TryParse(text, NumberStyles.None,
                CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100 ||
                text != limit.ToString(CultureInfo.InvariantCulture))
                throw new CatalogInvalidRequestException();
        }
        return (limit, context.Request.Query.TryGetValue("cursor", out var cursor) ? cursor[0] : null);
    }

    private static void RequireQuery(HttpContext context, params string[] allowed)
    {
        foreach (var query in context.Request.Query)
            if (!allowed.Contains(query.Key, StringComparer.Ordinal) || query.Value.Count != 1)
                throw new CatalogInvalidRequestException();
    }

    private static T Decode<T>(Func<T> decode)
    {
        try { return decode(); }
        catch (ArgumentException) { throw new CatalogInvalidRequestException(); }
    }

    private static Guid Id(string text)
    {
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D"))
            throw new CatalogInvalidRequestException();
        return id;
    }

    private static ProjectScope Project(string organization, string project) => new(Id(organization), Id(project));
    private static ProjectScope Project(MappingScope mapping) => new(mapping.OrganizationId, mapping.ProjectId);
    private static MappingScope Mapping(string organization, string project, string mapping) => new(Id(organization), Id(project), Id(mapping));
    private static void RequireOrganization(CatalogOrganizationAdmission organization, CatalogRequestAdmission admission) =>
        RequireScope(organization.OrganizationId == admission.AllowedOrganizationId && organization.PrincipalId == admission.Actor!.PrincipalId);
    private static void RequireAccess(ProjectAccess access, ActorContext actor, ProjectScope project) =>
        RequireScope(access.Scope == project && access.PrincipalId == actor.PrincipalId && access.Can(ProjectAction.ReadMetadata));
    private static void RequireScope(bool matches)
    {
        if (!matches) throw new InvalidOperationException("The catalog read scope is inconsistent.");
    }
    private static IResult NotFound() => Error(CatalogErrorCodeV1.NotFound, StatusCodes.Status404NotFound);
    private static IResult Error(CatalogErrorCodeV1 error, int status) => Results.Json(new CatalogErrorV1(error), statusCode: status);
    private sealed class CatalogInvalidRequestException : Exception { }
}
