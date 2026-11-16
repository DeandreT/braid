using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Domain;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void MapRevisionReports(IEndpointRouteBuilder endpoints, string revisionRoute)
    {
        DescribeRevisionReport<CatalogProjectionReportV1>(
            endpoints.MapGet(revisionRoute + "/reports/projection", GetCatalogProjectionReport),
            "GetCatalogProjectionReport", RevisionReportOperation.Projection, retained: false, ensure: false);
        DescribeRevisionReport<CatalogValidationReportV1>(
            endpoints.MapGet(revisionRoute + "/reports/validation", GetCatalogValidationReport),
            "GetCatalogValidationReport", RevisionReportOperation.Validation, retained: false, ensure: false);
        DescribeRevisionReport<CatalogProjectionReportV1>(
            endpoints.MapGet(revisionRoute + "/reports/projection/{derivationId}", GetRetainedCatalogProjectionReport),
            "GetRetainedCatalogProjectionReport", RevisionReportOperation.Projection, retained: true, ensure: false);
        DescribeRevisionReport<CatalogValidationReportV1>(
            endpoints.MapGet(revisionRoute + "/reports/validation/{derivationId}", GetRetainedCatalogValidationReport),
            "GetRetainedCatalogValidationReport", RevisionReportOperation.Validation, retained: true, ensure: false);
        DescribeRevisionReport<CatalogProjectionReportV1>(
            endpoints.MapPost(revisionRoute + "/reports/projection/ensure", EnsureCatalogProjectionReport),
            "EnsureCatalogProjectionReport", RevisionReportOperation.Projection, retained: false, ensure: true);
        DescribeRevisionReport<CatalogValidationReportV1>(
            endpoints.MapPost(revisionRoute + "/reports/validation/ensure", EnsureCatalogValidationReport),
            "EnsureCatalogValidationReport", RevisionReportOperation.Validation, retained: false, ensure: true);
    }

    private static Task<IResult> GetCatalogProjectionReport(string organizationId, string projectId,
        string mappingId, string revisionId, HttpContext context, CatalogRevisionReportOptions revisionReportOptions) =>
        ReadCatalogRevisionReport(organizationId, projectId, mappingId, revisionId, null, context,
            revisionReportOptions, RevisionReportOperation.Projection);

    private static Task<IResult> GetCatalogValidationReport(string organizationId, string projectId,
        string mappingId, string revisionId, HttpContext context, CatalogRevisionReportOptions revisionReportOptions) =>
        ReadCatalogRevisionReport(organizationId, projectId, mappingId, revisionId, null, context,
            revisionReportOptions, RevisionReportOperation.Validation);

    private static Task<IResult> GetRetainedCatalogProjectionReport(string organizationId, string projectId,
        string mappingId, string revisionId, string derivationId, HttpContext context,
        CatalogRevisionReportOptions revisionReportOptions) =>
        ReadCatalogRevisionReport(organizationId, projectId, mappingId, revisionId, derivationId, context,
            revisionReportOptions, RevisionReportOperation.Projection);

    private static Task<IResult> GetRetainedCatalogValidationReport(string organizationId, string projectId,
        string mappingId, string revisionId, string derivationId, HttpContext context,
        CatalogRevisionReportOptions revisionReportOptions) =>
        ReadCatalogRevisionReport(organizationId, projectId, mappingId, revisionId, derivationId, context,
            revisionReportOptions, RevisionReportOperation.Validation);

    private static Task<IResult> EnsureCatalogProjectionReport(string organizationId, string projectId,
        string mappingId, string revisionId, HttpContext context, CatalogRevisionReportOptions revisionReportOptions) =>
        EnsureCatalogRevisionReport(organizationId, projectId, mappingId, revisionId, context,
            revisionReportOptions, RevisionReportOperation.Projection);

    private static Task<IResult> EnsureCatalogValidationReport(string organizationId, string projectId,
        string mappingId, string revisionId, HttpContext context, CatalogRevisionReportOptions revisionReportOptions) =>
        EnsureCatalogRevisionReport(organizationId, projectId, mappingId, revisionId, context,
            revisionReportOptions, RevisionReportOperation.Validation);

    private static Task<IResult> ReadCatalogRevisionReport(string organizationId, string projectId,
        string mappingId, string revisionId, string? derivationId, HttpContext context,
        CatalogRevisionReportOptions options, RevisionReportOperation operation) =>
        WithRevisionReportAdmission(context, options, async admission =>
        {
            var scope = CatalogRevisionReportRequestParser.Scope(organizationId, projectId, mappingId, revisionId);
            if (scope.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            var retainedId = derivationId is null ? (Guid?)null : CatalogRevisionReportRequestParser.RetainedId(derivationId);
            var selector = CatalogRevisionReportRequestParser.ParseRead(context.Request, operation);
            var revision = await ReadReportCatalogRevisionAsync(context, admission.Actor!, scope);
            if (revision is null)
                return NotFound();
            var result = await RevisionReportOperationAsync<IRevisionReportReader, RevisionReportReadResult>(context,
                reader => retainedId is { } retained
                    ? reader.GetRetainedAsync(admission.Actor!, scope, operation, retained,
                        selector.ProjectionDerivationId, context.RequestAborted)
                    : reader.GetCurrentAsync(admission.Actor!, scope, operation,
                        selector.ProjectionDerivationId, context.RequestAborted));
            // Bound facts and response admission are deliberately outside the
            // dependency boundary: inconsistent data is an internal failure.
            var mapped = CatalogRevisionReportMapper.Read(admission.Actor!, revision, operation,
                retainedId, selector.ProjectionDerivationId, result, options, context.RequestAborted);
            return await PrepareAndRecheckReportAsync(context, admission.Actor!, revision, mapped);
        });

    private static Task<IResult> EnsureCatalogRevisionReport(string organizationId, string projectId,
        string mappingId, string revisionId, HttpContext context, CatalogRevisionReportOptions options,
        RevisionReportOperation operation) =>
        WithRevisionReportAdmission(context, options, async admission =>
        {
            var scope = CatalogRevisionReportRequestParser.Scope(organizationId, projectId, mappingId, revisionId);
            if (scope.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            var selector = CatalogRevisionReportRequestParser.ParseEnsure(context.Request);
            var revision = await ReadReportCatalogRevisionAsync(context, admission.Actor!, scope);
            if (revision is null)
                return NotFound();
            var result = await RevisionReportOperationAsync<IRevisionReportDeriver, RevisionReportEnsureResult>(context,
                deriver => deriver.EnsureAsync(admission.Actor!, scope, operation,
                    selector.RepairDerivationId, context.RequestAborted));
            var mapped = CatalogRevisionReportMapper.Ensure(admission.Actor!, revision, operation,
                selector.RepairDerivationId, result, options, context.RequestAborted);
            return await PrepareAndRecheckReportAsync(context, admission.Actor!, revision, mapped);
        }, unsafeRequest: true);

    private static Task<IResult> WithRevisionReportAdmission(HttpContext context, CatalogRevisionReportOptions options,
        Func<CatalogRequestAdmission, Task<IResult>> action, bool unsafeRequest = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Remove("ETag");
        if (!options.Enabled || unsafeRequest && !options.DerivationEnabled)
            return Task.FromResult(Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status404NotFound));
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(), action, unsafeRequest);
    }

    private static async Task<CatalogRevision?> ReadReportCatalogRevisionAsync(HttpContext context,
        ActorContext actor, RevisionScope scope)
    {
        CatalogRevision? revision;
        try
        {
            revision = await context.RequestServices.GetRequiredService<ICatalogReader>()
                .GetRevisionAsync(actor, scope, context.RequestAborted);
            context.RequestAborted.ThrowIfCancellationRequested();
        }
        catch (InvalidOperationException)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            throw new CatalogRevisionPersistenceException();
        }
        if (revision is not null)
        {
            RequireScope(revision.Summary.Scope == scope);
            RequireAccess(revision.Access, actor, new(scope.OrganizationId, scope.ProjectId));
        }
        return revision;
    }

    private static async Task<T> RevisionReportOperationAsync<TPort, T>(HttpContext context,
        Func<TPort, Task<T>> action) where TPort : class
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        TPort port;
        try { port = context.RequestServices.GetRequiredService<TPort>(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException ||
            exception is RevisionReportServiceException { Failure: not RevisionReportFailureCategory.ProtocolFailure })
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            throw new CatalogRevisionReportUnavailableException();
        }
        try
        {
            var result = await action(port);
            context.RequestAborted.ThrowIfCancellationRequested();
            return result;
        }
        catch (RevisionReportServiceException exception) when (exception.Failure != RevisionReportFailureCategory.ProtocolFailure)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            throw new CatalogRevisionReportUnavailableException();
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            throw new CatalogRevisionReportUnavailableException();
        }
    }

    private static async Task<IResult> PrepareAndRecheckReportAsync(HttpContext context, ActorContext actor,
        CatalogRevision admittedRevision, CatalogRevisionReportMappedResponse mapped)
    {
        var prepared = CatalogRevisionReportResponsePreparer.Prepare(mapped, context.RequestAborted);
        var fresh = await ReadReportCatalogRevisionAsync(context, actor, admittedRevision.Summary.Scope);
        if (fresh is null)
            return NotFound();
        RequireScope(fresh.Summary == admittedRevision.Summary);
        if (mapped.Source is { } source)
        {
            var summary = fresh.Summary;
            RequireScope(source.Actor == actor.PrincipalId && source.Source.Scope == summary.Scope &&
                source.Source.NativeObject.Digest == summary.NativeDigest &&
                source.Source.NativeObject.ByteCount == summary.NativeByteCount &&
                source.Source.LogicalDocument == summary.LogicalDocument &&
                source.Source.SourceBackendId == summary.BackendId);
        }
        context.RequestAborted.ThrowIfCancellationRequested();
        context.Response.Headers.Remove("ETag");
        return prepared;
    }
}
