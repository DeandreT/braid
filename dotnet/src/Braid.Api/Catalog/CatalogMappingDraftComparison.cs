using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Application.Comparisons;
using Braid.Contracts.Catalog;
using Braid.Contracts.Comparisons;
using Braid.Contracts.Editing;
using Braid.Domain;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static Task<IResult> CompareMappingDraft(string organizationId, string projectId, string mappingId, string draftId, HttpContext context) =>
        WithMappingDraft(context, organizationId, projectId, mappingId, draftId, false, async (admission, mapping, service, draft) =>
        {
            var checkpoint = await service.ReadCheckpointAsync(admission.Actor!, mapping, draft!.Id, context.RequestAborted);
            if (checkpoint is null) return DraftResult(context, new(MappingDraftOutcome.CheckpointRequired, draft));
            var content = await service.ReadOperationsAsync(admission.Actor!, mapping, draft.Id, context.RequestAborted);
            if (content is null) return NotFound();
            if (content.Draft.Version != draft.Version) return Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
            var revision = await ReadReportCatalogRevisionAsync(context, admission.Actor!, draft.Base.Revision);
            if (revision is null) return NotFound();
            // Base reports stay ordinary retained derivations with their own identities.
            var projection = await RevisionReportOperationAsync<IRevisionReportDeriver, RevisionReportEnsureResult>(context,
                deriver => deriver.EnsureAsync(admission.Actor!, draft.Base.Revision, RevisionReportOperation.Projection, cancellationToken: context.RequestAborted));
            var validation = await RevisionReportOperationAsync<IRevisionReportDeriver, RevisionReportEnsureResult>(context,
                deriver => deriver.EnsureAsync(admission.Actor!, draft.Base.Revision, RevisionReportOperation.Validation, cancellationToken: context.RequestAborted));
            if (projection.Outcome == RevisionReportEnsureOutcome.NotAuthorizedOrNotFound || validation.Outcome == RevisionReportEnsureOutcome.NotAuthorizedOrNotFound) return NotFound();
            if (projection.Read?.Report is not VerifiedProjectionReport verifiedProjection || validation.Read?.Report is not VerifiedValidationReport verifiedValidation)
            {
                context.Response.Headers.RetryAfter = "2";
                return Error(CatalogErrorCodeV1.RevisionReportUnavailable, StatusCodes.Status503ServiceUnavailable);
            }
            var format = new RevisionReportFormat(checkpoint.Projection.Document!.Provenance.Backend.Id, RevisionReportOperation.Projection,
                verifiedProjection.Metadata.Key.SchemaVersion, checkpoint.Projection.Document.Provenance.ProjectionVersion, verifiedProjection.Metadata.Key.CodecVersion);
            byte[] bytes;
            try
            {
                var limits = new RevisionComparisonLimits();
                var result = new RevisionComparisonService().Compare(RevisionComparisonSide.Create(revision.Summary, verifiedProjection, verifiedValidation, context.RequestAborted),
                    RevisionComparisonSide.CreateDraft(draft, checkpoint, format, new(), context.RequestAborted), limits, context.RequestAborted)
                    with
                { Operations = DraftOperationDescriptions(content.Operations.CopyDocument()) };
                bytes = RevisionReportResponseSerializer.Serialize(result, limits.OutputUtf8Bytes, context.RequestAborted);
            }
            catch (RevisionComparisonException error) when (error.Failure == RevisionComparisonFailure.ResourceLimit)
            { return Error(CatalogErrorCodeV1.RevisionReportResourceLimit, StatusCodes.Status503ServiceUnavailable); }
            catch (RevisionReportValidationException)
            { return Error(CatalogErrorCodeV1.RevisionReportResourceLimit, StatusCodes.Status503ServiceUnavailable); }
            var fresh = await service.ReadAsync(admission.Actor!, mapping, draft.Id, context.RequestAborted);
            if (fresh is null) return NotFound();
            if (fresh.Version != draft.Version || fresh.Base != draft.Base || fresh.Checkpoint?.NativeDigest != checkpoint.Artifact.Digest)
                return Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
            return CatalogRevisionReportPreparedResponse.Create(StatusCodes.Status200OK, null, null, bytes, context.RequestAborted);
        });

    private static IReadOnlyList<ComparisonEditOperationV1> DraftOperationDescriptions(EditScriptV1 script) => script.Operations.Select((operation, index) =>
    {
        var text = operation switch
        {
            BindEditV1 op => $"Mapped {op.Binding.Field} ← {Expression(op.Expression)}",
            RetargetBindingEditV1 op => $"Retargeted {op.Binding.Field} → {op.To.Field}",
            RemoveBindingEditV1 op => $"Removed mapping for {op.Binding.Field}",
            ConnectEditV1 op => $"Connected node {op.Node} input {op.Input}",
            DisconnectEditV1 op => $"Disconnected node {op.Node} input {op.Input}",
            EditConstantV1 op => $"Edited constant {op.Node}",
            EditCallV1 op => $"Changed node {op.Node} to {op.Function}",
            EditValueMapV1 op => $"Edited value map {op.Node}",
            RemoveNodesEditV1 op => $"Removed {op.Nodes.Count} node(s)",
            AddScopeEditV1 op => $"Added child scope {op.Field}",
            RemoveScopeEditV1 => "Removed child scope",
            SetIterationEditV1 => "Changed scope iteration",
            SetFilterEditV1 => "Changed scope filter",
            SetSortEditV1 => "Changed scope sort",
            SetGroupingEditV1 => "Changed scope grouping",
            AddFailureRuleEditV1 => "Added failure rule",
            RemoveFailureRuleEditV1 op => $"Removed failure rule {op.Index}",
            MoveFailureRuleEditV1 op => $"Moved failure rule {op.From} → {op.To}",
            EditFailureRuleV1 op => $"Edited failure rule {op.Index}",
            _ => throw new InvalidOperationException("Unknown closed edit operation.")
        };
        var truncated = text.Length > 256;
        if (truncated) text = string.Concat(text.EnumerateRunes().Take(256).Select(rune => rune.ToString()));
        return new ComparisonEditOperationV1(index.ToString(CultureInfo.InvariantCulture), text, truncated);
    }).ToArray();

    private static string Expression(EditExpressionV1 expression) => expression switch
    {
        SourceEditExpressionV1 source => string.Join('/', source.Path),
        OwnedSourceEditExpressionV1 source => string.Join('/', source.Path),
        ConstantEditExpressionV1 => "constant",
        CallEditExpressionV1 call => call.Function,
        ExistingEditExpressionV1 node => "node " + node.Node,
        _ => throw new InvalidOperationException("Unknown closed edit expression.")
    };
}
