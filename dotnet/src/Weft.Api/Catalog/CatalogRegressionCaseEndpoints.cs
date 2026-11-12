using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Weft.Api.Auth;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Catalog;
using Weft.Contracts.Regression;
using Weft.Domain;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static readonly JsonSerializerOptions CaseJson = new(JsonSerializerDefaults.Web);

    /// <summary>Explicit, default-off case routes; no preview dispatch or implicit host activation.</summary>
    public static IEndpointRouteBuilder MapWeftRegressionCases(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var routes = endpoints.BusinessRoutes();
        const string route = "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/regression-cases";
        DescribeCase<RegressionCaseCapabilitiesV1>(routes.MapGet(route + "/capabilities", GetRegressionCapabilities),
            "GetRegressionCaseCapabilities", "Read the active regression-case profile and current sample permissions.");
        DescribeCase<RegressionCasePageV1>(routes.MapGet(route, GetRegressionCases),
            "GetRegressionCases", "Read a bounded metadata page of saved regression cases.")
            .DescribePage("afterCaseId", "Return cases after this case: the nextCaseId of the previous page.");
        DescribeCase<RegressionCaseDetailV1>(routes.MapGet(route + "/{caseId}/versions/{version}", GetRegressionCaseVersion),
            "GetRegressionCaseVersion", "Read an exact saved case version under fresh sample permission.");
        DescribeCase<RegressionCaseSavedV1>(routes.MapPut(route + "/{caseId}/versions/{version}", PutRegressionCaseVersion),
            "PutRegressionCaseVersion", "Publish an immutable case version or replay the identical original request.")
            .Accepts<RegressionCasePutV1>(CatalogRegressionCaseRequest.MediaType)
            .Produces<RegressionCaseSavedV1>(StatusCodes.Status201Created)
            .Produces<CatalogErrorV1>(StatusCodes.Status409Conflict)
            .Produces<CatalogErrorV1>(StatusCodes.Status412PreconditionFailed)
            .Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge)
            .Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType);
        return endpoints;
    }

    // The paging query a history read accepts: a page size and the previous page's cursor; nothing else.
    private static RouteHandlerBuilder DescribePage(this RouteHandlerBuilder route, string cursor, string description) =>
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "limit",
                In = ParameterLocation.Query,
                Description = "Page size: a canonical decimal from 1 to 100, 50 when absent.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100", Default = JsonValue.Create(50) }
            });
            AddReportParameter(operation, cursor, ParameterLocation.Query, false, description, uuid: true);
            return Task.CompletedTask;
        });

    private static RouteHandlerBuilder DescribeCase<T>(RouteHandlerBuilder route, string name, string summary) =>
        route.WithName(name).WithTags("Regression cases").WithSummary(summary)
            .Produces<T>(StatusCodes.Status200OK)
            .Produces<CatalogErrorV1>(StatusCodes.Status400BadRequest)
            .Produces<CatalogErrorV1>(StatusCodes.Status401Unauthorized)
            .Produces<CatalogErrorV1>(StatusCodes.Status403Forbidden)
            .Produces<CatalogErrorV1>(StatusCodes.Status404NotFound)
            .Produces<CatalogErrorV1>(StatusCodes.Status500InternalServerError)
            .Produces<CatalogErrorV1>(StatusCodes.Status503ServiceUnavailable);

    private static Task<IResult> GetRegressionCapabilities(string organizationId, string projectId, string mappingId,
        HttpContext context, CatalogRegressionCaseOptions options) => WithCaseAdmission(context, options, async admission =>
    {
        RequireQuery(context);
        var mapping = CaseMapping(organizationId, projectId, mappingId, admission);
        if (mapping is null) return NotFound();
        var response = await RegressionCapabilitiesAsync(context.RequestServices, admission.Actor!, mapping, options, context.RequestAborted);
        if (response is null) return NotFound();
        var bytes = PrepareCase(response, context.RequestAborted);
        return CaseResponse(bytes);
    });

    private static Task<IResult> GetRegressionCases(string organizationId, string projectId, string mappingId,
        HttpContext context, CatalogRegressionCaseOptions options) => WithCaseAdmission(context, options, async admission =>
    {
        RequireQuery(context, "limit", "afterCaseId");
        var mapping = CaseMapping(organizationId, projectId, mappingId, admission);
        if (mapping is null || await CaseCatalogMapping(context, admission.Actor!, mapping) is null) return NotFound();
        var limit = 50;
        if (context.Request.Query.TryGetValue("limit", out var text))
        {
            var value = text[0]!;
            if (value.Length > 3 || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
                limit is < 1 or > 100 || value != limit.ToString(CultureInfo.InvariantCulture)) throw new CatalogInvalidRequestException();
        }
        var cursor = context.Request.Query.TryGetValue("afterCaseId", out var after) ? CatalogRegressionCaseRequest.Id(after[0]!) : (Guid?)null;
        var page = await RegressionCasePageAsync(context.RequestServices, admission.Actor!, mapping, limit, cursor, context.RequestAborted);
        if (page is null) return NotFound();
        var response = new RegressionCasePageV1(page.Cases.Select(item => new RegressionCaseMetadataV1(item.CaseId.ToString("D"),
            item.Version.ToString(CultureInfo.InvariantCulture), Scope(item.Revision), item.Name,
            item.CreatedAt.ToString("O", CultureInfo.InvariantCulture))).ToArray(), page.NextCaseId?.ToString("D"));
        var bytes = PrepareCase(response, context.RequestAborted);
        return CaseResponse(bytes);
    });

    private static Task<IResult> GetRegressionCaseVersion(string organizationId, string projectId, string mappingId, string caseId,
        string version, HttpContext context, CatalogRegressionCaseOptions options) => WithCaseAdmission(context, options, async admission =>
    {
        RequireQuery(context);
        var mapping = CaseMapping(organizationId, projectId, mappingId, admission);
        var id = CatalogRegressionCaseRequest.Id(caseId);
        var number = CatalogRegressionCaseRequest.Version(version);
        if (mapping is null) return NotFound();
        var value = await RegressionCaseVersionAsync(context.RequestServices, admission.Actor!, mapping, id, number, context.RequestAborted);
        if (value is null) return NotFound();
        var bytes = PrepareCase(new RegressionCaseDetailV1(caseId, version, Scope(value.Revision), Definition(value)), context.RequestAborted);
        return CaseResponse(bytes);
    });

    private static Task<IResult> PutRegressionCaseVersion(string organizationId, string projectId, string mappingId, string caseId,
        string version, HttpContext context, CatalogRegressionCaseOptions options) => WithCaseAdmission(context, options, async admission =>
    {
        RequireQuery(context);
        var mapping = CaseMapping(organizationId, projectId, mappingId, admission);
        var id = CatalogRegressionCaseRequest.Id(caseId);
        var number = CatalogRegressionCaseRequest.Version(version);
        if (mapping is null) return NotFound();
        var current = await CaseCatalogMapping(context, admission.Actor!, mapping);
        if (current is null) return NotFound();
        if (!CanPublishCase(current.Access)) return Error(CatalogErrorCodeV1.PermissionDenied, StatusCodes.Status403Forbidden);
        // No body or private store is touched before session/CSRF and current project publication admission.
        var incoming = await CatalogRegressionCaseRequest.ReadAsync(context.Request, context.RequestAborted);
        var selectedRuntime = options.SelectRuntime(incoming.Runtime);
        if (selectedRuntime is null || current.Summary.BackendId != options.Runtime!.BackendId)
            return Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
        var revisionScope = new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, incoming.RevisionId);
        var revision = await CaseCatalogRevision(context, admission.Actor!, revisionScope);
        if (revision is null) return NotFound();
        if (!CanPublishCase(revision.Access)) return Error(CatalogErrorCodeV1.PermissionDenied, StatusCodes.Status403Forbidden);
        if (revision.Summary.BackendId != options.Runtime.BackendId)
            throw new InvalidOperationException("Invalid regression revision backend.");
        var owner = admission.Actor!.PrincipalId;
        if (number > 1)
        {
            var previous = await context.RequestServices.GetRequiredService<IRegressionCaseReader>().ReadAsync(admission.Actor!, mapping,
                id, number - 1, context.RequestAborted);
            if (previous is null) return NotFound();
            if (previous.CaseId != id || previous.Version != number - 1 || !Belongs(previous.Revision, mapping))
                throw new InvalidOperationException("Invalid previous case identity.");
            owner = previous.Owner;
        }
        RegressionCaseVersion value;
        RegressionCaseReference expected;
        try
        {
            var replay = incoming.Replay;
            if (selectedRuntime.Profile is CsvDocumentPolicy.Profile or XmlPreviewAdmission.Profile)
            {
                var source = await context.RequestServices.GetRequiredService<IRevisionReportMetadataReader>()
                    .GetSourceAsync(admission.Actor!, revisionScope, context.RequestAborted);
                if (source is null) return NotFound();
                if (source.Actor != admission.Actor!.PrincipalId || source.Source.Scope != revisionScope || source.Source.NativeObject.Digest != revision.Summary.NativeDigest ||
                    source.Source.SourceBackendId != revision.Summary.BackendId || source.Source.LogicalDocument != IngestionIntent.EntryDocument ||
                    source.Source.DependencyIdentity != IngestionIntent.EmptyDependencyIdentity)
                    throw new InvalidOperationException("CSV source identity mismatch.");
                if (!await CsvNativeAuthorityAsync(context, admission.Actor!, revision, ProjectAction.UploadRevision)) return NotFound();
                var native = await context.RequestServices.GetRequiredService<IPrivateObjectStore>()
                    .ReadVerifiedAsync(revisionScope, source.Source.NativeObject, context.RequestAborted);
                if (!await CsvNativeAuthorityAsync(context, admission.Actor!, revision, ProjectAction.UploadRevision)) return NotFound();
                try
                {
                    var artifact = NativeArtifact.Admit(revisionScope, native, PreviewPolicy.MaximumNativeBytes);
                    if (artifact.Digest != source.Source.NativeObject.Digest || artifact.ByteCount != source.Source.NativeObject.ByteCount)
                        throw new InvalidOperationException("CSV source integrity mismatch.");
                    if (selectedRuntime.Profile == XmlPreviewAdmission.Profile)
                    {
                        if (!XmlPreviewAdmission.MatchesCase(artifact, replay, incoming.Expected))
                            return Error(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
                    }
                    else
                    {
                        if (incoming.Expected.Outputs.Count != 1)
                            return Error(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
                        var primary = new PreviewInputDocument(null, replay.Primary.CopyBytes(), CsvNativeDialect.Read(artifact).Input);
                        if (CsvNativeDialect.Read(artifact).Output != incoming.Expected.Outputs[0].CsvDialect)
                            return Error(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
                        replay = new(replay.Target, replay.CurrentDateTime, replay.Limits,
                            primary, replay.NamedInputs, replay.Parameters, context.RequestAborted,
                            PreviewExecutionProfile.CsvDocument);
                    }
                }
                catch (PreviewRunException) { return Error(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest); }
                catch (XmlDocumentException error)
                {
                    return error.IsResourceLimit ? Error(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge)
                        : Error(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
                }
            }
            value = new(id, number, owner, incoming.Name, revisionScope, revision.Summary.NativeDigest, selectedRuntime,
                replay, incoming.Policy, incoming.Expected, incoming.EdiOptions);
            expected = new RegressionCaseEnvelopeCodec().Encode(value, context.RequestAborted).Reference;
        }
        catch (RegressionCaseException error)
        {
            return Error(error.Failure == RegressionCaseFailure.ResourceLimit ? CatalogErrorCodeV1.BodyLimit : CatalogErrorCodeV1.InvalidRequest,
                error.Failure == RegressionCaseFailure.ResourceLimit ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest);
        }
        var result = await context.RequestServices.GetRequiredService<IRegressionCaseWriter>().SaveAsync(admission.Actor!, value, context.RequestAborted);
        if (result.Outcome == RegressionCaseSaveOutcome.Unavailable) return NotFound();
        if (result.Outcome == RegressionCaseSaveOutcome.Conflict) return Error(CatalogErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
        if (result.Reference != expected || result.Outcome == RegressionCaseSaveOutcome.Created && number != 1 ||
            result.Outcome == RegressionCaseSaveOutcome.Appended && number == 1)
            throw new InvalidOperationException("Invalid saved case receipt.");
        var fresh = await CaseCatalogRevision(context, admission.Actor!, revisionScope);
        if (fresh is null || !CanPublishCase(fresh.Access)) return NotFound();
        RequireScope(fresh.Summary == revision.Summary);
        var outcome = result.Outcome switch
        {
            RegressionCaseSaveOutcome.Created => "created",
            RegressionCaseSaveOutcome.Appended => "appended",
            RegressionCaseSaveOutcome.Replayed => "replayed",
            _ => throw new InvalidOperationException("Invalid case outcome.")
        };
        var response = new RegressionCaseSavedV1(caseId, version, Scope(revisionScope), outcome);
        var bytes = PrepareCase(response, context.RequestAborted);
        return CaseResponse(bytes, result.Outcome == RegressionCaseSaveOutcome.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }, unsafeRequest: true);

    private static Task<IResult> WithCaseAdmission(HttpContext context, CatalogRegressionCaseOptions options,
        Func<CatalogRequestAdmission, Task<IResult>> action, bool unsafeRequest = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Remove("ETag");
        if (!options.Enabled) return Task.FromResult(Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status404NotFound));
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(), async admission =>
        {
            try { return await action(admission); }
            catch (CatalogRegressionCaseRequestException error) { return Error(error.Code, error.Status); }
            catch (RegressionCaseException) { return Error(CatalogErrorCodeV1.PersistenceFailure, StatusCodes.Status500InternalServerError); }
        }, unsafeRequest);
    }

    private static MappingScope? CaseMapping(string organization, string project, string mapping, CatalogRequestAdmission admission)
    {
        var result = new MappingScope(CatalogRegressionCaseRequest.Id(organization), CatalogRegressionCaseRequest.Id(project), CatalogRegressionCaseRequest.Id(mapping));
        return result.OrganizationId == admission.AllowedOrganizationId ? result : null;
    }
    private static Task<CatalogMapping?> CaseCatalogMapping(HttpContext context, ActorContext actor, MappingScope scope) =>
        ReadableMappingAsync(context.RequestServices, actor, scope, context.RequestAborted);
    private static Task<CatalogRevision?> CaseCatalogRevision(HttpContext context, ActorContext actor, RevisionScope scope) =>
        ReadableRevisionAsync(context.RequestServices, actor, scope, context.RequestAborted);
    private static bool CanReadCase(ProjectAccess access) => access.Can(ProjectAction.SubmitPreview);
    private static bool CanPublishCase(ProjectAccess access) => access.Can(ProjectAction.UploadRevision);
    private static bool Belongs(RevisionScope revision, MappingScope mapping) => revision.OrganizationId == mapping.OrganizationId &&
        revision.ProjectId == mapping.ProjectId && revision.MappingId == mapping.MappingId;
    private static CatalogRevisionScopeV1 Scope(RevisionScope value) => new(value.OrganizationId.ToString("D"), value.ProjectId.ToString("D"), value.MappingId.ToString("D"), value.RevisionId.ToString("D"));
    private static RegressionRuntimeV1 Runtime(RegressionRuntimeIdentity value) => new(value.BackendId, value.EngineRevision, value.BackendBuild, value.AdapterBuild, value.Profile);
    private static RegressionLimitsV1 Limits(PreviewLimits value) => new(value.MaximumInputBytes, value.MaximumOutputBytes, value.MaximumArtifacts, value.MaximumTextBytes);
    private static byte[] PrepareCase<T>(T value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CaseJson);
        if (bytes.Length > CatalogRegressionCaseRequest.MaximumBodyBytes) throw new InvalidOperationException("Regression response exceeds its bound.");
        token.ThrowIfCancellationRequested();
        return bytes;
    }
    private static IResult CaseResponse(byte[] bytes, int status = StatusCodes.Status200OK) => new RegressionCaseBytes(bytes, status);
    private sealed class RegressionCaseBytes(byte[] bytes, int status) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
    }
    private static RegressionCasePutV1 Definition(RegressionCaseVersion value) => new(value.Name, value.Revision.RevisionId.ToString("D"), Runtime(value.Runtime),
        value.Replay.CurrentDateTime, new(value.Replay.Target.Kind switch { PreviewTargetKind.All => "all", PreviewTargetKind.Primary => "primary", _ => "named" }, value.Replay.Target.Name),
        Limits(value.Replay.Limits), WebEncoders.Base64UrlEncode(value.Replay.Primary.CopyBytes()),
        value.Replay.NamedInputs.Select(input => new RegressionInputDocumentV1(input.Name, WebEncoders.Base64UrlEncode(input.CopyBytes()))).ToArray(),
        value.Replay.Parameters.Select(parameter => new RegressionParameterV1(parameter.Name, parameter.Kind switch
        {
            PreviewParameterKind.String => "string",
            PreviewParameterKind.Integer => "integer",
            PreviewParameterKind.Float => "float",
            _ => "boolean"
        }, parameter.Value)).ToArray(), value.ComparisonPolicy,
        value.Expected.Outputs.Select(output => new RegressionDocumentV1(output.Key.Name, WebEncoders.Base64UrlEncode(output.CopyBytes()),
            value.Runtime.Profile is DocumentPreviewPolicy.Profile or CsvDocumentPolicy.Profile or XmlPreviewAdmission.Profile ? output.MediaType : null,
            CsvDialect(output.CsvDialect), XmlMetadata(output.XmlMetadata))).ToArray(),
        value.EdiOptions is { } options ? new(options.CompareSeparators, options.IgnoreControlNumbers, options.IgnoreDates) : null);
}
