using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void MapInterchange(IEndpointRouteBuilder endpoints, string project, string mapping)
    {
        DescribeInterchangeRead<CatalogInterchangeCapabilitiesV1>(endpoints.MapGet("/api/v1/catalog/interchange", InterchangeCapabilities),
            "GetCatalogInterchangeCapabilities", "Read bounded interchange availability; no backend is enabled by default.");
        DescribeInterchangeReview(endpoints.MapPost(project + "/mfd-imports/review", ReviewMfdCreate), "ReviewMfdCreate", import: true, append: false)
            .WithMetadata(new RequestSizeLimitAttribute(InterchangePackage.MaximumEncodedBytes));
        DescribeInterchangeReview(endpoints.MapPost(mapping + "/mfd-imports/review", ReviewMfdAppend), "ReviewMfdAppend", import: true, append: true)
            .WithMetadata(new RequestSizeLimitAttribute(InterchangePackage.MaximumEncodedBytes));
        DescribeInterchangeConfirmation(endpoints.MapPost(project + "/mfd-imports/{reviewId}/confirm", ConfirmMfdImport), export: false);
        DescribeInterchangeRead<CatalogInterchangeReviewV1>(endpoints.MapGet(project + "/mfd-imports/{reviewId}", ReadMfdImportReview),
            "GetMfdImportReview", "Restore an original-actor import review after fresh project and target authorization.");
        var revision = mapping + "/revisions/{revisionId}";
        DescribeInterchangeReview(endpoints.MapPost(revision + "/mfd-exports/review", ReviewMfdExport), "ReviewMfdExport", import: false, append: false);
        DescribeInterchangeConfirmation(endpoints.MapPost(revision + "/mfd-exports/{reviewId}/download", DownloadMfdExport), export: true);
    }

    private static Task<IResult> InterchangeCapabilities(HttpContext context, CatalogRequestBoundary boundary) =>
        WithAdmission(context, boundary, _ =>
        {
            CatalogRevisionRequestParser.NoQuery(context.Request);
            return Task.FromResult<IResult>(Results.Ok(new CatalogInterchangeCapabilitiesV1(InterchangeEnabled(context),
                InterchangePackage.MaximumFiles, InterchangePackage.MaximumFileBytes, InterchangePackage.MaximumTotalBytes,
                InterchangePackage.MaximumPathBytes)));
        });

    private static bool InterchangeEnabled(HttpContext context)
    {
        // Inspect registrations without constructing a lazy storage/worker dependency
        // before the identity boundary admits this request.
        var services = context.RequestServices.GetService<IServiceProviderIsService>();
        return context.RequestServices.GetService<CatalogInterchangeOptions>()?.Enabled == true &&
            services?.IsService(typeof(IInterchangeBackendRegistry)) == true &&
            services.IsService(typeof(InterchangeImportService)) && services.IsService(typeof(InterchangeExportService));
    }

    private static Task<IResult> InterchangeWork(HttpContext context, Func<CatalogRequestAdmission, Task<IResult>> action, bool unsafeRequest = true)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!InterchangeEnabled(context)) return Task.FromResult(Error(CatalogErrorCodeV1.Unavailable, 404));
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(), async admission =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var gate = context.RequestServices.GetRequiredService<CatalogInterchangeGate>();
            if (!gate.TryEnter()) return Error(CatalogErrorCodeV1.IngestionAdmissionBusy, 503);
            var caller = context.RequestAborted;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            context.RequestAborted = deadline.Token;
            try { return await action(admission); }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested && deadline.IsCancellationRequested)
            { return Error(CatalogErrorCodeV1.Unavailable, 503); }
            catch (UnauthorizedAccessException) { return NotFound(); }
            catch (PrivateObjectStorageException) { return Error(CatalogErrorCodeV1.Unavailable, 503); }
            catch (InvalidOperationException) { return Error(CatalogErrorCodeV1.Unavailable, 503); }
            finally { context.RequestAborted = caller; gate.Exit(); }
        }, unsafeRequest);
    }

    private static Task<IResult> ReadMfdImportReview(string organizationId, string projectId, string reviewId, HttpContext context) =>
        InterchangeWork(context, async admission =>
        {
            var project = Project(organizationId, projectId);
            if (project.OrganizationId != admission.AllowedOrganizationId) return NotFound();
            CatalogRevisionRequestParser.NoQuery(context.Request);
            var review = await context.RequestServices.GetRequiredService<InterchangeImportService>()
                .GetReviewAsync(admission.Actor!, project, CatalogRevisionRequestParser.CanonicalId(reviewId), context.RequestAborted);
            return review is null ? NotFound() : Results.Ok(ImportReviewContract(review));
        }, unsafeRequest: false);

    private static Task<IResult> ReviewMfdCreate(string organizationId, string projectId, HttpContext context) =>
        InterchangeWork(context, async admission =>
        {
            var project = Project(organizationId, projectId);
            if (project.OrganizationId != admission.AllowedOrganizationId) return NotFound();
            var destination = CatalogInterchangeParser.Create(context.Request, project);
            // Do not consume the large package until exact project upload authority has been checked.
            var access = await context.RequestServices.GetRequiredService<IProjectReader>().GetAccessAsync(admission.Actor!, project, context.RequestAborted);
            if (access is null || access.Scope != project || access.PrincipalId != admission.Actor!.PrincipalId || !access.Can(ProjectAction.UploadRevision)) return NotFound();
            SetPackageLimit(context);
            var package = await CatalogInterchangeParser.PackageAsync(context.Request, context.RequestAborted);
            var review = await context.RequestServices.GetRequiredService<InterchangeImportService>()
                .ReviewAsync(admission.Actor!, destination, package, context.RequestAborted);
            return review is null ? NotFound() : Results.Ok(ImportReviewContract(review));
        });

    private static Task<IResult> ReviewMfdAppend(string organizationId, string projectId, string mappingId, HttpContext context) =>
        InterchangeWork(context, async admission =>
        {
            var mapping = Mapping(organizationId, projectId, mappingId);
            if (mapping.OrganizationId != admission.AllowedOrganizationId) return NotFound();
            var head = CatalogInterchangeParser.AppendHead(context.Request);
            var project = Project(mapping);
            var access = await context.RequestServices.GetRequiredService<IProjectReader>().GetAccessAsync(admission.Actor!, project, context.RequestAborted);
            if (access is null || access.Scope != project || access.PrincipalId != admission.Actor!.PrincipalId || !access.Can(ProjectAction.UploadRevision)) return NotFound();
            var target = await context.RequestServices.GetRequiredService<IIngestionWorkflowReader>().GetAppendTargetAsync(admission.Actor!, mapping, context.RequestAborted);
            if (target is null) return NotFound();
            if (target.Scope != mapping) throw new InvalidOperationException("Inconsistent append target.");
            SetPackageLimit(context);
            var package = await CatalogInterchangeParser.PackageAsync(context.Request, context.RequestAborted);
            var review = await context.RequestServices.GetRequiredService<InterchangeImportService>()
                .ReviewAsync(admission.Actor!, ImportDestination.Append(mapping, target.BackendId, head), package, context.RequestAborted);
            return review is null ? NotFound() : Results.Ok(ImportReviewContract(review));
        });

    private static Task<IResult> ConfirmMfdImport(string organizationId, string projectId, string reviewId, HttpContext context) =>
        InterchangeWork(context, async admission =>
        {
            var project = Project(organizationId, projectId);
            if (project.OrganizationId != admission.AllowedOrganizationId) return NotFound();
            var id = CatalogRevisionRequestParser.CanonicalId(reviewId);
            var key = CatalogRevisionRequestParser.Key(context.Request);
            var command = await CatalogInterchangeParser.ConfirmationAsync(context.Request, context.RequestAborted);
            var result = await context.RequestServices.GetRequiredService<InterchangeImportService>().ConfirmAsync(admission.Actor!, project,
                new(id, Sha256Digest.Parse(command.ReviewDigest), key, command.AcknowledgeWarnings), context.RequestAborted);
            if (result.Outcome == ImportConfirmationOutcome.NotAuthorizedOrNotFound) return NotFound();
            if (result.Outcome != ImportConfirmationOutcome.Submitted) return Error(CatalogErrorCodeV1.PreconditionFailed, 412);
            var ingestion = result.Ingestion ?? throw new InvalidOperationException("Missing import receipt.");
            if (ingestion.Receipt is { } receipt)
            {
                RequireIngestionReceipt(receipt, project);
                if (ingestion.Outcome == RevisionIngestionOutcome.InProgress)
                    context.Response.Headers.Location = CatalogRevisionIngestionMapper.ReceiptUrl(project, receipt.OperationId);
            }
            return Results.Json(CatalogRevisionIngestionMapper.Result(ingestion), statusCode: CatalogRevisionIngestionMapper.Status(ingestion));
        });

    private static Task<IResult> ReviewMfdExport(string organizationId, string projectId, string mappingId, string revisionId, HttpContext context) =>
        InterchangeWork(context, async admission =>
        {
            var scope = new RevisionScope(Id(organizationId), Id(projectId), Id(mappingId), Id(revisionId));
            if (scope.OrganizationId != admission.AllowedOrganizationId) return NotFound();
            CatalogRevisionRequestParser.NoQuery(context.Request);
            CatalogRevisionRequestParser.ResumeHeaders(context.Request);
            await CatalogRevisionRequestParser.RequireEmptyBodyAsync(context.Request, context.RequestAborted);
            var review = await context.RequestServices.GetRequiredService<InterchangeExportService>().ReviewAsync(admission.Actor!, scope, context.RequestAborted);
            return review is null ? NotFound() : Results.Ok(ExportReviewContract(review));
        });

    private static Task<IResult> DownloadMfdExport(string organizationId, string projectId, string mappingId, string revisionId, string reviewId, HttpContext context) =>
        InterchangeWork(context, async admission =>
        {
            var scope = new RevisionScope(Id(organizationId), Id(projectId), Id(mappingId), Id(revisionId));
            if (scope.OrganizationId != admission.AllowedOrganizationId) return NotFound();
            CatalogRevisionRequestParser.ResumeHeaders(context.Request);
            var command = await CatalogInterchangeParser.ConfirmationAsync(context.Request, context.RequestAborted);
            var result = await context.RequestServices.GetRequiredService<InterchangeExportService>().DownloadAsync(admission.Actor!, scope,
                CatalogRevisionRequestParser.CanonicalId(reviewId), Sha256Digest.Parse(command.ReviewDigest), command.AcknowledgeWarnings, context.RequestAborted);
            if (result.Outcome == ExportDownloadOutcome.NotAuthorizedOrNotFound) return NotFound();
            if (result.Outcome != ExportDownloadOutcome.Available) return Error(CatalogErrorCodeV1.PreconditionFailed, 412);
            var download = result.Download ?? throw new InvalidOperationException("Missing export bytes.");
            context.Response.Headers["X-Content-Sha256"] = download.Digest.Value;
            return Results.File(download.CopyBytes(), download.ContentType, download.Filename, enableRangeProcessing: false);
        });

    private static void SetPackageLimit(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = InterchangePackage.MaximumEncodedBytes;
    }
    private static CatalogInterchangeReviewV1 ImportReviewContract(ImportReviewSummary review) => ReviewContract(review.Id,
        review.Digest, review.PackageDigest, review.NativeDigest, review.Profile, review.Report, review.ExpiresAt,
        review.Destination ?? throw new InvalidOperationException("Missing reviewed destination."));
    private static CatalogInterchangeReviewV1 ExportReviewContract(ExportReviewSummary review) => ReviewContract(review.Id,
        review.Digest, review.PackageDigest, review.NativeDigest, review.Profile, review.Report, review.ExpiresAt);
    private static CatalogInterchangeReviewV1 ReviewContract(Guid id, Sha256Digest digest, Sha256Digest package, Sha256Digest native,
        IngestionBackendProfile profile, InterchangeReport report, DateTimeOffset expires, ImportDestination? destination = null) => new(id.ToString("D"), digest.Value,
        package.Value, native.Value, new(profile.BackendId, profile.EngineRevision, profile.BackendBuild, profile.AdapterBuild),
        report.Summary, report.Warnings.Select(w => new CatalogInterchangeWarningV1(w.Code, w.Message)).ToArray(), expires,
        destination?.Target?.MappingId.ToString("D"), destination?.Head?.RevisionId.ToString("D"), destination?.Metadata?.Name);
}
