using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Domain;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static Task<IResult> CreateCatalogMapping(string organizationId, string projectId,
        HttpContext context, CatalogRevisionIngestionOptions ingestionOptions) =>
        WithIngestionAdmission(context, ingestionOptions, async admission =>
        {
            var project = Project(organizationId, projectId);
            if (project.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            var request = CatalogRevisionRequestParser.Create(context.Request);
            var key = CatalogRevisionRequestParser.Key(context.Request);
            var visible = await IngestionPersistence(context, () => context.RequestServices.GetRequiredService<ICatalogReader>()
                .GetProjectAsync(admission.Actor!, project, context.RequestAborted));
            if (visible is null)
                return NotFound();
            RequireScope(visible.Summary.Scope == project);
            RequireAccess(visible.Access, admission.Actor!, project);
            if (!visible.Access.Can(ProjectAction.UploadRevision))
                return NotFound();
            CatalogRevisionRequestParser.RequireContent(context.Request);
            return await IngestionWork(context, service => service.CreateAsync(admission.Actor!, project, key, request,
                new CatalogRevisionRequestStream(context.Request.Body, context.RequestAborted), context.RequestAborted), project);
        }, unsafeRequest: true);

    private static Task<IResult> AppendCatalogRevision(string organizationId, string projectId, string mappingId,
        HttpContext context, CatalogRevisionIngestionOptions ingestionOptions) =>
        WithIngestionAdmission(context, ingestionOptions, async admission =>
        {
            var mapping = Mapping(organizationId, projectId, mappingId);
            if (mapping.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            var request = CatalogRevisionRequestParser.Append(context.Request);
            var key = CatalogRevisionRequestParser.Key(context.Request);
            var project = Project(mapping);
            var visible = await IngestionPersistence(context, () => context.RequestServices.GetRequiredService<ICatalogReader>()
                .GetMappingAsync(admission.Actor!, mapping, context.RequestAborted));
            if (visible is null)
                return NotFound();
            RequireScope(visible.Summary.Scope == mapping);
            RequireAccess(visible.Access, admission.Actor!, project);
            if (!visible.Access.Can(ProjectAction.UploadRevision))
                return NotFound();
            CatalogRevisionRequestParser.RequireContent(context.Request);
            // Persisted backend resolution and original-key comparison belong exclusively to C2.
            return await IngestionWork(context, service => service.AppendAsync(admission.Actor!, mapping, key, request,
                new CatalogRevisionRequestStream(context.Request.Body, context.RequestAborted), context.RequestAborted), project, mapping);
        }, unsafeRequest: true);

    private static Task<IResult> GetCatalogIngestionReceipt(string organizationId, string projectId, string operationId,
        HttpContext context, CatalogRevisionIngestionOptions ingestionOptions) =>
        WithIngestionAdmission(context, ingestionOptions, async admission =>
        {
            var project = Project(organizationId, projectId);
            var operation = Id(operationId);
            if (project.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            CatalogRevisionRequestParser.NoQuery(context.Request);
            var receipt = await IngestionPersistence(context, () => context.RequestServices.GetRequiredService<IRevisionIngestionService>()
                .GetOwnReceiptAsync(admission.Actor!, project, operation, context.RequestAborted));
            if (receipt is null)
                return NotFound();
            RequireIngestionReceipt(receipt, project, operation);
            return Results.Ok(CatalogRevisionIngestionMapper.Receipt(receipt));
        });

    private static Task<IResult> ResumeCatalogRevisionIngestion(string organizationId, string projectId, string operationId,
        HttpContext context, CatalogRevisionIngestionOptions ingestionOptions) =>
        WithIngestionAdmission(context, ingestionOptions, async admission =>
        {
            var project = Project(organizationId, projectId);
            var operation = Id(operationId);
            if (project.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            CatalogRevisionRequestParser.NoQuery(context.Request);
            CatalogRevisionRequestParser.ResumeHeaders(context.Request);
            var receipt = await IngestionPersistence(context, () => context.RequestServices.GetRequiredService<IRevisionIngestionService>()
                .GetOwnReceiptAsync(admission.Actor!, project, operation, context.RequestAborted));
            if (receipt is null)
                return NotFound();
            RequireIngestionReceipt(receipt, project, operation);
            await CatalogRevisionRequestParser.RequireEmptyBodyAsync(context.Request, context.RequestAborted);
            return await IngestionWork(context, current => current.ResumeAsync(admission.Actor!, project,
                operation, context.RequestAborted), project, expectedOperation: operation);
        }, unsafeRequest: true);

    private static Task<IResult> WithIngestionAdmission(HttpContext context, CatalogRevisionIngestionOptions options,
        Func<CatalogRequestAdmission, Task<IResult>> action, bool unsafeRequest = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!options.Enabled)
            return Task.FromResult(Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status404NotFound));
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(), action, unsafeRequest);
    }

    private static async Task<T> IngestionPersistence<T>(HttpContext context, Func<Task<T>> action)
    {
        try
        {
            var value = await action();
            context.RequestAborted.ThrowIfCancellationRequested();
            return value;
        }
        catch (InvalidOperationException)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            throw new CatalogRevisionPersistenceException();
        }
    }

    private static async Task<IResult> IngestionWork(HttpContext context,
        Func<IRevisionIngestionService, Task<RevisionIngestionResult>> action, ProjectScope project,
        MappingScope? expectedMapping = null, Guid? expectedOperation = null)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        using var permit = context.RequestServices.GetRequiredService<CatalogRevisionIngestionGate>().TryEnter();
        if (permit is null)
            return Error(CatalogErrorCodeV1.IngestionAdmissionBusy, StatusCodes.Status503ServiceUnavailable);
        var result = await IngestionPersistence(context, () => action(context.RequestServices.GetRequiredService<IRevisionIngestionService>()));
        if (result.Receipt is { } receipt)
        {
            RequireIngestionReceipt(receipt, project, expectedOperation);
            RequireScope(expectedMapping is null || receipt.FutureMapping == expectedMapping);
        }
        var mapped = CatalogRevisionIngestionMapper.Result(result);
        var status = CatalogRevisionIngestionMapper.Status(result);
        if (result.Outcome == RevisionIngestionOutcome.InProgress && result.Receipt is { } pending)
            context.Response.Headers.Location = CatalogRevisionIngestionMapper.ReceiptUrl(project, pending.OperationId);
        return Results.Json(mapped, statusCode: status);
    }

    private static void RequireIngestionReceipt(RevisionIngestionReceipt receipt, ProjectScope project, Guid? operation = null) =>
        RequireScope(receipt.Project == project && (operation is null || receipt.OperationId == operation) &&
            receipt.FutureMapping.OrganizationId == project.OrganizationId && receipt.FutureMapping.ProjectId == project.ProjectId &&
            receipt.FutureRevision.OrganizationId == project.OrganizationId && receipt.FutureRevision.ProjectId == project.ProjectId &&
            receipt.FutureRevision.MappingId == receipt.FutureMapping.MappingId);
}

internal sealed class CatalogRevisionPersistenceException : Exception { }
