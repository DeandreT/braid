using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Contracts.Comparisons;
using Weft.Contracts.Editing;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static bool MappingEditingEnabled(HttpContext context) => context.RequestServices.GetService<CatalogMappingEditOptions>()?.Enabled == true;
    private static readonly JsonSerializerOptions DraftRequestJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    private static void MapMappingDrafts(IEndpointRouteBuilder endpoints, string mapping)
    {
        var drafts = mapping + "/drafts";
        var draft = drafts + "/{draftId}";
        DraftBody<MappingDraftCreateV1>(DraftRoute<MappingDraftResultV1>(endpoints.MapPost(drafts, CreateMappingDraft), "CreateMappingDraft", "Create an author-private exact-base draft and version-zero checkpoint."), key: true)
            .Produces<MappingDraftResultV1>(StatusCodes.Status201Created);
        DraftRoute<MappingDraftListV1>(endpoints.MapGet(drafts, ListMappingDrafts), "ListMappingDrafts", "List the caller's own open drafts for this mapping.");
        DraftRoute<MappingDraftV1>(endpoints.MapGet(draft, GetMappingDraft), "GetMappingDraft", "Read author-private draft metadata and its strong version ETag.");
        DraftRoute(endpoints.MapGet(draft + "/base", GetMappingDraftBase), "GetMappingDraftBase", "Read the exact immutable base bytes, never an editable native upload.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");
        DraftRoute<EditScriptV1>(endpoints.MapGet(draft + "/operations", GetMappingDraftOperations), "GetMappingDraftOperations", "Read the closed ferrule.edit.v1 operation log.");
        DraftBody<EditScriptV1>(DraftRoute<MappingDraftResultV1>(endpoints.MapPut(draft + "/operations", PutMappingDraftOperations), "PutMappingDraftOperations", "Autosave a complete bounded operation log without starting a worker."), match: true);
        DraftBody<MappingDraftVersionV1>(DraftRoute<MappingDraftResultV1>(endpoints.MapPost(draft + "/checkpoints", CheckpointMappingDraft), "CheckpointMappingDraft", "Replay, encode, validate and project the exact draft version."));
        DraftRoute<ValidationReportV1>(endpoints.MapGet(draft + "/checkpoints/current/validation", GetMappingDraftValidation), "GetMappingDraftValidation", "Read the current authoritative checkpoint validation.");
        DraftRoute<ProjectionReportV1>(endpoints.MapGet(draft + "/checkpoints/current/projection", GetMappingDraftProjection), "GetMappingDraftProjection", "Read the current authoritative checkpoint projection.");
        DraftRoute<RevisionComparisonV1>(endpoints.MapGet(draft + "/comparison", CompareMappingDraft), "CompareMappingDraft", "Compare the checkpoint's semantic facts and typed intent with the immutable base.");
        DraftBody<CatalogPreviewSubmissionV1>(DraftRoute<CatalogPreviewSubmitResultV1>(endpoints.MapPost(draft + "/previews", PreviewMappingDraft), "PreviewMappingDraft", "Preview an immutable copy of the current checkpoint, retained independently of this draft."), key: true)
            .Produces<CatalogPreviewSubmitResultV1>(StatusCodes.Status201Created);
        DraftBody<MappingDraftSaveV1>(DraftRoute<CatalogRevisionIngestionResultV1>(endpoints.MapPost(draft + "/save", SaveMappingDraft), "SaveMappingDraft", "Save a nonempty current checkpoint through original-actor conditional ingestion."), key: true)
            .Produces<CatalogRevisionIngestionResultV1>(StatusCodes.Status202Accepted);
        DraftBody<MappingDraftRebaseV1>(DraftRoute<MappingDraftResultV1>(endpoints.MapPost(draft + "/rebase", RebaseMappingDraft), "RebaseMappingDraft", "Explicitly replay all operations on the current head, or change nothing."), match: true);
        DraftCondition(DraftRoute<MappingDraftResultV1>(endpoints.MapDelete(draft, DiscardMappingDraft), "DiscardMappingDraft", "Discard the author's operation log and checkpoint content."));
    }

    private static RouteHandlerBuilder DraftRoute<T>(RouteHandlerBuilder route, string name, string summary) =>
        DraftRoute(route, name, summary).Produces<T>(StatusCodes.Status200OK);

    private static RouteHandlerBuilder DraftRoute(RouteHandlerBuilder route, string name, string summary) =>
        route.WithName(name).WithTags("Mapping drafts").WithSummary(summary)
            .Produces<CatalogErrorV1>(StatusCodes.Status400BadRequest).Produces<CatalogErrorV1>(StatusCodes.Status401Unauthorized)
            .Produces<CatalogErrorV1>(StatusCodes.Status403Forbidden).Produces<CatalogErrorV1>(StatusCodes.Status404NotFound)
            .Produces<MappingDraftResultV1>(StatusCodes.Status409Conflict).Produces<CatalogErrorV1>(StatusCodes.Status412PreconditionFailed)
            .Produces<MappingDraftResultV1>(StatusCodes.Status422UnprocessableEntity).Produces<CatalogErrorV1>(StatusCodes.Status428PreconditionRequired)
            .Produces<CatalogErrorV1>(StatusCodes.Status500InternalServerError).Produces<CatalogErrorV1>(StatusCodes.Status503ServiceUnavailable)
            .AddOpenApiOperationTransformer(async (operation, context, token) =>
            {
                await DraftErrorSchemas(operation, context, token, name == "SaveMappingDraft");
            });

    private static Task<IResult> CreateMappingDraft(string organizationId, string projectId, string mappingId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, null, true, async (admission, mapping, service, _) =>
        {
            var key = CatalogRevisionRequestParser.Key(context.Request);
            var body = await DraftBodyAsync<MappingDraftCreateV1>(context);
            return DraftResult(context, await service.CreateAsync(admission.Actor!, mapping, key, Id(body.BaseRevisionId), context.RequestAborted));
        });

    private static Task<IResult> ListMappingDrafts(string organizationId, string projectId, string mappingId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, null, false, async (admission, mapping, service, _) =>
        {
            var drafts = await service.ListAsync(admission.Actor!, mapping, context.RequestAborted);
            return drafts is null ? NotFound() : Results.Ok(new MappingDraftListV1(drafts.Select(DraftDocument).ToArray()));
        });

    private static Task<IResult> GetMappingDraft(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, false, (_, _, _, draft) =>
        {
            context.Response.Headers.ETag = DraftTag(draft!);
            return Task.FromResult<IResult>(Results.Ok(DraftDocument(draft!)));
        });

    private static Task<IResult> GetMappingDraftBase(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, false, async (admission, mapping, service, draft) =>
        {
            var artifact = await service.ReadBaseAsync(admission.Actor!, mapping, draft!.Id, context.RequestAborted);
            if (artifact is null) return NotFound();
            context.Response.Headers["Content-Digest"] = "sha-256=:" + Convert.ToBase64String(Convert.FromHexString(artifact.Digest.Value)) + ":";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(artifact.CopyBytes(), "application/octet-stream");
        });

    private static Task<IResult> GetMappingDraftOperations(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, false, async (admission, mapping, service, draft) =>
        {
            var content = await service.ReadOperationsAsync(admission.Actor!, mapping, draft!.Id, context.RequestAborted);
            if (content is null) return NotFound();
            context.Response.Headers.ETag = DraftTag(content.Draft);
            return Results.Ok(content.Operations.CopyDocument());
        });

    private static Task<IResult> PutMappingDraftOperations(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, true, async (admission, mapping, service, draft) =>
        {
            var condition = DraftMatch(context.Request, draft!);
            if (condition.Error is { } error) return error;
            RequireJsonContent(context.Request);
            var bytes = await DraftBytesAsync(context, MappingEditPolicy.MaximumOperationsBytes);
            MappingEditScript operations;
            try { operations = MappingEditScript.Parse(bytes); }
            catch (ArgumentException) { throw new CatalogInvalidRequestException(); }
            return DraftResult(context, await service.PutOperationsAsync(admission.Actor!, mapping, draft!.Id, condition.Version, operations, context.RequestAborted));
        });

    private static Task<IResult> CheckpointMappingDraft(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, true, async (admission, mapping, service, draft) =>
        {
            var body = await DraftBodyAsync<MappingDraftVersionV1>(context);
            return DraftResult(context, await service.CheckpointAsync(admission.Actor!, mapping, draft!.Id, DraftVersion(body.Version), context.RequestAborted));
        });

    private static Task<IResult> GetMappingDraftValidation(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, false, async (admission, mapping, service, draft) =>
            await service.ReadCheckpointAsync(admission.Actor!, mapping, draft!.Id, context.RequestAborted) is { } checkpoint ? Results.Ok(checkpoint.Validation) : NotFound());

    private static Task<IResult> GetMappingDraftProjection(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, false, async (admission, mapping, service, draft) =>
            await service.ReadCheckpointAsync(admission.Actor!, mapping, draft!.Id, context.RequestAborted) is { } checkpoint ? Results.Ok(checkpoint.Projection) : NotFound());

    private static Task<IResult> SaveMappingDraft(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, true, async (admission, mapping, service, draft) =>
        {
            var key = CatalogRevisionRequestParser.Key(context.Request);
            var body = await DraftBodyAsync<MappingDraftSaveV1>(context);
            try { _ = new AppendRevisionRequest(originalFilename: body.OriginalFilename); }
            catch (ArgumentException) { throw new CatalogInvalidRequestException(); }
            return DraftResult(context, await service.SaveAsync(admission.Actor!, mapping, draft!.Id, DraftVersion(body.Version), key, body.OriginalFilename, context.RequestAborted));
        });

    private static Task<IResult> RebaseMappingDraft(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, true, async (admission, mapping, service, draft) =>
        {
            var condition = DraftMatch(context.Request, draft!);
            if (condition.Error is { } error) return error;
            var body = await DraftBodyAsync<MappingDraftRebaseV1>(context);
            return DraftResult(context, await service.RebaseAsync(admission.Actor!, mapping, draft!.Id, condition.Version, Id(body.TargetRevisionId), context.RequestAborted));
        });

    private static Task<IResult> DiscardMappingDraft(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, true, async (admission, mapping, service, draft) =>
        {
            var condition = DraftMatch(context.Request, draft!);
            if (condition.Error is { } error) return error;
            await CatalogRevisionRequestParser.RequireEmptyBodyAsync(context.Request, context.RequestAborted);
            return DraftResult(context, await service.DiscardAsync(admission.Actor!, mapping, draft!.Id, condition.Version, context.RequestAborted));
        });

    private static Task<IResult> WithMappingDraft(HttpContext context, string organizationId, string projectId, string mappingId,
        string? draftId, bool unsafeRequest, Func<CatalogRequestAdmission, MappingScope, MappingDraftService, MappingDraftSnapshot?, Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MappingEditingEnabled(context)) return Task.FromResult(NotFound());
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(), async admission =>
        {
            var mapping = Mapping(organizationId, projectId, mappingId);
            if (mapping.OrganizationId != admission.AllowedOrganizationId) return NotFound();
            RequireQuery(context);
            try
            {
                var observed = await context.RequestServices.GetRequiredService<ICatalogReader>().GetMappingAsync(admission.Actor!, mapping, context.RequestAborted);
                if (observed is null || !observed.Access.Can(ProjectAction.EditMapping) || observed.Summary.BackendId != "ferrule") return NotFound();
                RequireAccess(observed.Access, admission.Actor!, Project(mapping)); RequireScope(observed.Summary.Scope == mapping);
                var service = context.RequestServices.GetRequiredService<MappingDraftService>();
                var draft = draftId is null ? null : await service.ReadAsync(admission.Actor!, mapping, Id(draftId), context.RequestAborted);
                if (draftId is not null && draft is null) return NotFound();
                return await action(admission, mapping, service, draft);
            }
            catch (DbException) { return Error(CatalogErrorCodeV1.PersistenceFailure, StatusCodes.Status503ServiceUnavailable); }
            catch (PrivateObjectStorageException) { return Error(CatalogErrorCodeV1.InternalFailure, StatusCodes.Status503ServiceUnavailable); }
        }, unsafeRequest);
    }

    private static MappingDraftV1 DraftDocument(MappingDraftSnapshot draft) => new(draft.Id.ToString("D"), CatalogPublicMapper.MappingScope(draft.Mapping),
        draft.Author.Value.ToString("D"), new(CatalogPublicMapper.RevisionScope(draft.Base.Revision), draft.Base.Sequence.ToString(CultureInfo.InvariantCulture), new("sha256", draft.Base.NativeObject.Digest.Value), checked((int)draft.Base.NativeObject.ByteCount)),
        draft.State.ToString().ToLowerInvariant(), draft.Version.ToString(CultureInfo.InvariantCulture), draft.OperationCount,
        draft.OperationsDigest is { } digest ? new("sha256", digest.Value) : null, draft.CreatedAtUtc, draft.ChangedAtUtc, draft.ExpiresAtUtc,
        draft.Checkpoint is { } checkpoint ? new(checkpoint.Version.ToString(CultureInfo.InvariantCulture), new("sha256", checkpoint.NativeDigest.Value), checkpoint.NativeByteCount, checkpoint.IsValid, checkpoint.TotalIssueCount) : null,
        draft.SavingOperationId?.ToString("D"), draft.SavedRevisionId?.ToString("D"), draft.SavingKey?.ToString("D"), draft.SavingFilename);

    private static IResult DraftResult(HttpContext context, MappingDraftResult result)
    {
        if (result.Draft is { } draft) context.Response.Headers.ETag = DraftTag(draft);
        if (result.Ingestion is { } ingestion) return Results.Json(CatalogRevisionIngestionMapper.Result(ingestion), statusCode: CatalogRevisionIngestionMapper.Status(ingestion));
        if (result.Outcome == MappingDraftOutcome.NotAuthorizedOrNotFound) return NotFound();
        if (result.Outcome == MappingDraftOutcome.PreconditionFailed) return Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
        if (result.Outcome == MappingDraftOutcome.Busy) return Error(CatalogErrorCodeV1.IngestionAdmissionBusy, StatusCodes.Status503ServiceUnavailable);
        var document = new MappingDraftResultV1(JsonNamingPolicy.CamelCase.ConvertName(result.Outcome.ToString()), result.Draft is { } value ? DraftDocument(value) : null,
            result.Failure is { } failure ? new(failure.Code, failure.Message, failure.MessageTruncated, failure.OperationIndex, JsonNamingPolicy.CamelCase.ConvertName(failure.Status.ToString())) : null);
        var status = result.Outcome switch
        {
            MappingDraftOutcome.Created => StatusCodes.Status201Created,
            MappingDraftOutcome.Succeeded or MappingDraftOutcome.Replayed => StatusCodes.Status200OK,
            MappingDraftOutcome.ReplayFailed => result.Failure?.Status is IngestionBackendStatus.BackendUnavailable or IngestionBackendStatus.BackendFailure or IngestionBackendStatus.TimedOut or IngestionBackendStatus.IsolationFailure or IngestionBackendStatus.ProtocolFailure
                ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status409Conflict
        };
        return Results.Json(document, statusCode: status);
    }

    private static string DraftTag(MappingDraftSnapshot draft) => $"\"draft-{draft.Id:D}-{draft.Version.ToString(CultureInfo.InvariantCulture)}\"";
    private static long DraftVersion(string version) => long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= 0 && value.ToString(CultureInfo.InvariantCulture) == version ? value : throw new CatalogInvalidRequestException();

    private static (long Version, IResult? Error) DraftMatch(HttpRequest request, MappingDraftSnapshot draft)
    {
        if (request.Headers.ContainsKey("If-None-Match")) throw new CatalogInvalidRequestException();
        var matches = request.Headers.IfMatch;
        if (matches.Count == 0) return (0, Error(CatalogErrorCodeV1.PreconditionRequired, StatusCodes.Status428PreconditionRequired));
        if (matches.Count != 1 || matches[0] is not { } raw) throw new CatalogInvalidRequestException();
        var tag = raw.Trim(' ', '\t');
        var prefix = $"\"draft-{draft.Id:D}-";
        if (!tag.StartsWith(prefix, StringComparison.Ordinal) || !tag.EndsWith('"')) throw new CatalogInvalidRequestException();
        var version = DraftVersion(tag[prefix.Length..^1]);
        return version != draft.Version ? (version, Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed)) : (version, null);
    }

    private static async Task<T> DraftBodyAsync<T>(HttpContext context)
    {
        RequireJsonContent(context.Request);
        var bytes = await DraftBytesAsync(context, 4096);
        try { return JsonSerializer.Deserialize<T>(bytes, DraftRequestJson) ?? throw new CatalogInvalidRequestException(); }
        catch (JsonException) { throw new CatalogInvalidRequestException(); }
    }

    private static async Task<byte[]> DraftBytesAsync(HttpContext context, int maximum)
    {
        if (context.Request.ContentLength > maximum) throw new CatalogRevisionRequestException(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximum + 1 - checked((int)buffer.Length))), context.RequestAborted);
            if (count == 0) break;
            buffer.Write(chunk, 0, count);
            if (buffer.Length > maximum) throw new CatalogRevisionRequestException(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
        }
        return buffer.ToArray();
    }
}
