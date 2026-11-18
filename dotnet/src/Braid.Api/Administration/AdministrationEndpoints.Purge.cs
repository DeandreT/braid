using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Braid.Application;
using Braid.Application.Lifecycle;
using Braid.Contracts.Administration;

namespace Braid.Api.Administration;

public static partial class AdministrationEndpoints
{
    internal const string ConfirmationHeader = "Braid-Confirmation";
    private const string ConfirmationPurpose = "Braid.ResourcePurge.Confirmation.v1";
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(10);

    private static void MapPurge(IEndpointRouteBuilder routes)
    {
        const string org = "/api/v1/organizations/{organizationId}";
        const string project = org + "/projects/{projectId}";
        const string mapping = project + "/mappings/{mappingId}";
        DescribePurgeRead<ResourcePurgePreviewV1>(routes.MapGet(project + "/deletion-preview",
            (string organizationId, string projectId, HttpContext context) => PreviewPurge(context, organizationId, projectId, null)), "GetProjectDeletionPreview");
        DescribePurgeRead<ResourcePurgePreviewV1>(routes.MapGet(mapping + "/deletion-preview",
            (string organizationId, string projectId, string mappingId, HttpContext context) => PreviewPurge(context, organizationId, projectId, mappingId)), "GetMappingDeletionPreview");
        DescribePurge(routes.MapDelete(project,
            (string organizationId, string projectId, HttpContext context) => RequestPurge(context, organizationId, projectId, null)), "DeleteProject");
        DescribePurge(routes.MapDelete(mapping,
            (string organizationId, string projectId, string mappingId, HttpContext context) => RequestPurge(context, organizationId, projectId, mappingId)), "DeleteMapping");
        DescribePurgeRead<DeletionJobV1>(routes.MapGet(org + "/deletion-jobs/{jobId}",
            (string organizationId, string jobId, HttpContext context) => ReadPurgeJob(context, organizationId, jobId)), "GetDeletionJob");
        DescribePurgeRead<DeletionJobV1>(routes.MapGet(org + "/deletion-jobs/operations/{operationId}",
            (string organizationId, string operationId, HttpContext context) => ReadPurgeOperation(context, organizationId, operationId)), "GetDeletionJobOperation");
    }

    private static Task<IResult> PreviewPurge(HttpContext context, string organizationId, string projectId, string? mappingId) =>
        Handle(context, organizationId, projectId, false, async (_, actor, org, project) =>
        {
            RequireEmpty(context);
            var resource = new ResourceKey(new(org, project!.Value), mappingId is null ? null : Id(mappingId));
            if (context.RequestServices.GetService<IResourcePurgeStore>() is not { } store ||
                context.RequestServices.GetService<IDataProtectionProvider>() is not { } protection) return Error("unavailable", 404);
            var preview = await store.PreviewAsync(actor, resource, context.RequestAborted);
            if (preview is null) return Error("notFound", 404);
            if (preview.Resource != resource) throw new InvalidOperationException();
            // A confirmation is offered only for an archived resource with no blocker; it is never persisted or logged.
            string? confirmation = null;
            DateTimeOffset? expires = null;
            if (preview.EffectivelyArchived && preview.Deletion is null && preview.ActiveReleases == 0 && preview.PendingMappingDeletions == 0)
            {
                expires = DateTimeOffset.UtcNow.Add(ConfirmationLifetime);
                confirmation = Protector(protection).Protect(ConfirmationText(Guid.NewGuid(), actor, resource, preview.Version), expires.Value);
            }
            context.Response.Headers.ETag = ResourceLifecycleVersion.ETag(resource, preview.Version);
            var i = preview.Impact;
            return Results.Ok(new ResourcePurgePreviewV1(resource.IsMapping ? "mapping" : "project", resource.Project.ProjectId, resource.MappingId,
                preview.Deletion is not null ? "deleting" : preview.EffectivelyArchived ? "archived" : "active",
                Decimal(preview.Version.Project), preview.Version.Mapping is { } version ? Decimal(version) : null,
                preview.PlanVersion.ToString(CultureInfo.InvariantCulture), Decimal(preview.ActiveReleases), Decimal(preview.PendingMappingDeletions),
                new(Decimal(i.Mappings), Decimal(i.Revisions), Decimal(i.Conversations), Decimal(i.Previews), Decimal(i.RegressionCases),
                    Decimal(i.RegressionRuns), Decimal(i.Builds), Decimal(i.ReleaseTargets), Decimal(i.RetainedArtifacts)),
                preview.Deletion is { } job ? Job(job) : null, confirmation, expires));
        });

    private static Task<IResult> RequestPurge(HttpContext context, string organizationId, string projectId, string? mappingId) =>
        Handle(context, organizationId, projectId, true, async (_, actor, org, project) =>
        {
            RequireEmpty(context);
            var resource = new ResourceKey(new(org, project!.Value), mappingId is null ? null : Id(mappingId));
            var operations = context.Request.Headers[OperationHeader];
            if (operations.Count != 1 || operations[0] is not { } operationText) throw new InvalidInput();
            var operation = Id(operationText);
            var conditions = context.Request.Headers.IfMatch;
            var confirmations = context.Request.Headers[ConfirmationHeader];
            if (conditions.Count == 0 || confirmations.Count == 0) throw new MissingCondition();
            var expected = conditions.Count == 1 ? ResourceLifecycleVersion.ParseETag(resource, conditions[0]) : null;
            if (expected is null || confirmations.Count != 1 || confirmations[0] is not { Length: > 0 and <= 2048 } token) throw new InvalidInput();
            if (context.RequestServices.GetService<IResourcePurgeStore>() is not { } store ||
                context.RequestServices.GetService<IDataProtectionProvider>() is not { } protection) return Error("unavailable", 404);
            Guid confirmation;
            try
            {
                var text = Protector(protection).Unprotect(token, out DateTimeOffset _expiresAtUtc);
                if (!TryConfirmation(text, actor, resource, out confirmation, out var bound)) return Error("confirmationExpired", 409);
                if (bound != expected) return Error("preconditionFailed", 412);
            }
            catch (CryptographicException) { return Error("confirmationExpired", 409); }
            // One mutation per request; an unknown commit is resolved through the operation read, never by resending.
            var result = await store.RequestAsync(actor, new(resource, operation, confirmation, expected), context.RequestAborted);
            return result.Outcome switch
            {
                ResourcePurgeOutcome.Accepted or ResourcePurgeOutcome.Replayed when result.Job is { } job =>
                    Results.Json(Job(job), statusCode: 202),
                ResourcePurgeOutcome.NotAuthorizedOrNotFound => Error("notFound", 404),
                ResourcePurgeOutcome.PreconditionFailed => Error("preconditionFailed", 412),
                ResourcePurgeOutcome.ActiveRelease => Error("activeRelease", 409),
                ResourcePurgeOutcome.ActiveWork => Error("activeWork", 409),
                ResourcePurgeOutcome.Conflict => Error("conflict", 409),
                ResourcePurgeOutcome.InvalidRequest => Error("invalidRequest", 400),
                _ => throw new InvalidOperationException()
            };
        });

    private static Task<IResult> ReadPurgeJob(HttpContext context, string organizationId, string jobId) =>
        Handle(context, organizationId, null, false, async (_, actor, org, _) =>
        {
            RequireEmpty(context);
            var id = Id(jobId);
            if (context.RequestServices.GetService<IResourcePurgeStore>() is not { } store) return Error("unavailable", 404);
            var job = await store.ReadJobAsync(actor, org, id, context.RequestAborted);
            return job is null ? Error("notFound", 404) : Results.Ok(Job(job));
        });

    private static Task<IResult> ReadPurgeOperation(HttpContext context, string organizationId, string operationId) =>
        Handle(context, organizationId, null, false, async (_, actor, org, _) =>
        {
            RequireEmpty(context);
            var id = Id(operationId);
            if (context.RequestServices.GetService<IResourcePurgeStore>() is not { } store) return Error("unavailable", 404);
            var job = await store.ReadOperationAsync(actor, org, id, context.RequestAborted);
            return job is null ? Error("notFound", 404) : Results.Ok(Job(job));
        });

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider provider) =>
        provider.CreateProtector(ConfirmationPurpose).ToTimeLimitedDataProtector();

    private static string ConfirmationText(Guid confirmation, ActorContext actor, ResourceKey resource, ResourceLifecycleVersion version) =>
        string.Join('|', confirmation.ToString("D"), actor.PrincipalId.Value.ToString("D"), resource.Project.OrganizationId.ToString("D"),
            resource.Project.ProjectId.ToString("D"), resource.MappingId?.ToString("D") ?? "", Decimal(version.Project),
            version.Mapping is { } mapping ? Decimal(mapping) : "", "1");

    /// <summary>The confirmation binds the actor, exact resource and lifecycle versions it was issued for.</summary>
    private static bool TryConfirmation(string text, ActorContext actor, ResourceKey resource, out Guid confirmation, out ResourceLifecycleVersion? version)
    {
        confirmation = Guid.Empty; version = null;
        var parts = text.Split('|');
        if (parts.Length != 8 || !Guid.TryParseExact(parts[0], "D", out confirmation) || confirmation == Guid.Empty ||
            parts[1] != actor.PrincipalId.Value.ToString("D") || parts[2] != resource.Project.OrganizationId.ToString("D") ||
            parts[3] != resource.Project.ProjectId.ToString("D") || parts[4] != (resource.MappingId?.ToString("D") ?? "") || parts[7] != "1" ||
            !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var project) || project < 1) return false;
        long? mapping = null;
        if (resource.IsMapping)
        {
            if (!long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1) return false;
            mapping = value;
        }
        else if (parts[6].Length != 0) return false;
        version = new(project, mapping);
        return true;
    }

    private static DeletionJobV1 Job(DeletionJobStatus job) => new(job.JobId, job.Resource.IsMapping ? "mapping" : "project",
        job.Resource.Project.ProjectId, job.Resource.MappingId,
        job.State switch
        {
            DeletionJobState.Quiescing => "quiescing",
            DeletionJobState.Inventory => "inventory",
            DeletionJobState.CatalogPurging => "catalogPurging",
            DeletionJobState.ObjectsPending => "objectsPending",
            DeletionJobState.Completed => "completed",
            _ => "blocked"
        },
        job.Blocker switch
        {
            null => null,
            DeletionJobBlocker.ActiveRelease => "activeRelease",
            DeletionJobBlocker.Integrity => "integrity",
            DeletionJobBlocker.Storage => "storage",
            _ => "attemptsExhausted"
        },
        job.CatalogRemoved, Decimal(job.ObjectCount), Decimal(job.ObjectsPending), Decimal(job.ObjectsBlocked),
        Decimal(job.RetainedArtifacts), Decimal(job.RetainedAuditRecords), job.RequestedAtUtc, job.ChangedAtUtc, job.CatalogRemovedAtUtc, job.CompletedAtUtc);

    private static void DescribePurgeRead<T>(RouteHandlerBuilder route, string name) =>
        Describe<T>(route, name).AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (name != "GetProjectDeletionPreview" && name != "GetMappingDeletionPreview" && operation.Responses?["200"] is OpenApiResponse response)
                response.Headers?.Remove("ETag");
            return Task.CompletedTask;
        });

    private static void DescribePurge(RouteHandlerBuilder route, string name) =>
        route.WithName(name).WithTags("Administration").WithSummary(name)
            .Produces<DeletionJobV1>(202).Produces<ResourceDeletionErrorV1>(400).Produces<ResourceDeletionErrorV1>(404)
            .Produces<ResourceDeletionErrorV1>(409).Produces<ResourceDeletionErrorV1>(412).Produces<ResourceDeletionErrorV1>(428)
            .Produces<ResourceDeletionErrorV1>(503).AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = OperationHeader,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Client-chosen operation UUID. Reusing it with the same request returns the original job; any change conflicts.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                });
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "If-Match",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Strong lifecycle ETag of the exact archived project or mapping the confirmation was issued for.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = ConfirmationHeader,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Short-lived opaque confirmation from the latest deletion preview. It is bound to the actor, resource and versions and is consumed once.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
                return Task.CompletedTask;
            });
}
