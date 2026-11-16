using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Domain;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static Task<IResult> CreateCatalogProject(string organizationId, HttpContext context,
        CatalogProjectCreationOptions creationOptions)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!creationOptions.Enabled)
            return Task.FromResult(Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status404NotFound));
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(), async admission =>
        {
            var organization = Id(organizationId);
            if (organization != admission.AllowedOrganizationId)
                return NotFound();
            CatalogProjectCreationParser.RequireNoQuery(context.Request);
            var observed = await ReadProjectCreationAdmission(context, admission.Actor!, organization);
            if (observed is null)
                return NotFound();
            if (!observed.CanCreateProjects)
                return Error(CatalogErrorCodeV1.PermissionDenied, StatusCodes.Status403Forbidden);

            // The advisory read has finished its transaction. No database lease
            // or authority lock is held while accepting the complete document.
            var key = CatalogProjectCreationParser.Key(context.Request);
            CatalogProjectCreationParser.RequireContent(context.Request);
            context.RequestAborted.ThrowIfCancellationRequested();
            using var permit = context.RequestServices.GetRequiredService<CatalogProjectCreationGate>().TryEnter();
            if (permit is null)
                return Error(CatalogErrorCodeV1.ProjectCreationAdmissionBusy, StatusCodes.Status503ServiceUnavailable);
            var metadata = await CatalogProjectCreationParser.ReadAsync(context.Request, context.RequestAborted);
            var result = await ProjectCreationPersistence(context, () => context.RequestServices.GetRequiredService<IProjectCreator>()
                .CreateAsync(admission.Actor!, organization, key, metadata, context.RequestAborted));
            switch (result.Outcome)
            {
                case ProjectCreationOutcome.NotAdmitted:
                    RequireScope(result.Receipt is null);
                    return NotFound();
                case ProjectCreationOutcome.CapabilityDenied:
                    RequireScope(result.Receipt is null);
                    return Error(CatalogErrorCodeV1.PermissionDenied, StatusCodes.Status403Forbidden);
                case ProjectCreationOutcome.IdempotencyConflict:
                    RequireScope(result.Receipt is null);
                    return Error(CatalogErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
                case ProjectCreationOutcome.Created:
                case ProjectCreationOutcome.Replayed:
                    var mapped = CatalogProjectCreationMapper.Result(result, admission.Actor!, organization, key, metadata);
                    context.Response.Headers.Location = mapped.ProjectUrl;
                    return Results.Json(mapped, statusCode: result.Outcome == ProjectCreationOutcome.Created
                        ? StatusCodes.Status201Created : StatusCodes.Status200OK);
                default:
                    throw new InvalidOperationException("The project creation outcome is unknown.");
            }
        }, unsafeRequest: true);
    }

    private static async Task<OrganizationProjectCreationAdmission?> ReadProjectCreationAdmission(HttpContext context,
        ActorContext actor, Guid organizationId)
    {
        var observed = await ProjectCreationPersistence(context, () =>
            context.RequestServices.GetRequiredService<IOrganizationProjectCreationReader>()
                .GetAdmissionAsync(actor, organizationId, context.RequestAborted));
        if (observed is not null)
            RequireScope(observed.OrganizationId == organizationId && observed.PrincipalId == actor.PrincipalId);
        return observed;
    }

    private static async Task<T> ProjectCreationPersistence<T>(HttpContext context, Func<Task<T>> operation)
    {
        try
        {
            var result = await operation();
            context.RequestAborted.ThrowIfCancellationRequested();
            return result;
        }
        catch (InvalidOperationException)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            throw new CatalogProjectCreationPersistenceException();
        }
    }
}

internal sealed class CatalogProjectCreationPersistenceException : Exception { }
