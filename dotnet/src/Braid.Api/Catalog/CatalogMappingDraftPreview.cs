using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Contracts.Catalog;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static Task<IResult> PreviewMappingDraft(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, true, async (admission, mapping, _, draft) =>
        {
            if (context.RequestServices.GetService<CatalogPreviewOptions>() is not { Enabled: true } options) return NotFound();
            var key = CatalogRevisionRequestParser.Key(context.Request);
            RequireJsonContent(context.Request);
            var body = await ReadPreviewBodyAsync(context.Request, options.BodyByteLimit, context.RequestAborted);
            var service = context.RequestServices.GetRequiredService<PreviewRunService>();
            var retained = await service.ReadDraftSubjectAsync(admission.Actor!, mapping, key, context.RequestAborted);
            if (retained is not null && retained.DraftId != draft!.Id) return Error(CatalogErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
            if (retained is null && (draft!.Checkpoint is not { } current || current.Version != draft.Version))
                return DraftResult(context, new(MappingDraftOutcome.CheckpointRequired, draft));
            var checkpoint = draft!.Checkpoint;
            var subject = retained ?? new DraftPreviewSubject(draft.Base.Revision, draft.Id, draft.Version, checkpoint!.NativeDigest, checkpoint.NativeByteCount);
            PreviewSubmitResult submitted;
            try { submitted = await service.SubmitAsync(admission.Actor!, PreviewSubmission(subject, key, body), context.RequestAborted); }
            catch (PreviewRunException) { throw new CatalogInvalidRequestException(); }
            if (submitted.Outcome == PreviewSubmitOutcome.NotAuthorizedOrNotFound) return NotFound();
            if (submitted.Outcome == PreviewSubmitOutcome.Conflict) return Error(CatalogErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
            var run = submitted.RunId ?? throw new InvalidOperationException("A stored preview has no run identity.");
            var snapshot = await service.ReadAsync(admission.Actor!, mapping.OrganizationId, run, context.RequestAborted);
            if (snapshot is null) return NotFound();
            RequireScope(snapshot.Subject == subject && snapshot.RequestKey == key);
            var created = submitted.Outcome == PreviewSubmitOutcome.Created;
            var result = new CatalogPreviewSubmitResultV1(created ? "created" : "replayed", PreviewRun(snapshot));
            return created ? Results.Created($"/api/v1/organizations/{mapping.OrganizationId:D}/previews/{run:D}", result) : Results.Ok(result);
        });
}
