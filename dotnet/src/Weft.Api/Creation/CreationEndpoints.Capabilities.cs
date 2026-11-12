using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Contracts.Creation;
using Weft.Domain;

namespace Weft.Api.Creation;

public static partial class CreationEndpoints
{
    private static async Task<IResult> Capabilities(HttpContext c, string organizationId, string projectId)
    {
        c.Response.Headers.CacheControl = "no-store"; c.Response.Headers.XContentTypeOptions = "nosniff";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        try
        {
            var boundary = c.RequestServices.GetService<CatalogRequestBoundary>();
            if (boundary is null) return Error(503, "Unavailable");
            var auth = await boundary.AuthenticateAsync(c, false, ct);
            if (!auth.IsAdmitted) return Error(auth.StatusCode, "IdentityRejected");
            if (auth.ContextMode is not ("session" or "bearer")) return Error(403, "IdentityRejected");
            var project = new ProjectScope(Id(organizationId), Id(projectId));
            if (auth.AllowedOrganizationId != project.OrganizationId) return Error(404, "NotFound");
            if (c.Request.QueryString.HasValue || c.Request.Headers.ContentEncoding.Count != 0) return Error(400, "InvalidRequest");
            var catalog = c.RequestServices.GetRequiredService<ICatalogReader>();
            async Task<bool> CanRead()
            {
                var current = await catalog.GetProjectAsync(auth.Actor!, project, ct);
                return current is not null && current.Summary.Scope == project && current.Access.Scope == project &&
                    current.Access.PrincipalId == auth.Actor!.PrincipalId && current.Access.Can(ProjectAction.ReadMetadata);
            }
            if (!await CanRead()) return Error(404, "NotFound");
            // Host admission describes the durable runtime, including retained reads
            // after source retirement. Availability is independent of this actor's payload grant.
            var available = await CreationRuntime.AvailableAsync(c.RequestServices, ct);
            if (!await CanRead()) return Error(404, "NotFound");
            ct.ThrowIfCancellationRequested();
            return Results.Ok(new CreationCapabilitiesV1(available));
        }
        catch (OperationCanceledException) when (c.RequestAborted.IsCancellationRequested) { throw; }
        catch (ArgumentException) { return Error(400, "InvalidRequest"); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { return Error(503, "Unavailable"); }
    }
}
