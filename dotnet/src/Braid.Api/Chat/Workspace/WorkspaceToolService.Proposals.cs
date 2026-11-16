using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Domain;

namespace Braid.Api.Chat.Workspace;

public sealed partial class WorkspaceToolService
{
    private async Task<ChatReadOnlyToolResult> ProposeEditAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        if (!features.Edits || ChatEndpoints.ProposalService(services) is not { } proposals ||
            services.GetService<MappingDirectEditService>() is not { } editor ||
            scope.Turn is null || scope.CallId is null || scope.RoundId is null)
            throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Unavailable);
        var mapping = await LocateMappingAsync(scope, call.Id("mappingId"), token);
        var baseRevision = new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, call.Id("baseRevisionId"));
        var readable = await CatalogEndpoints.ReadableMappingAsync(services, scope.Actor, mapping, token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        if (readable.Summary.BackendId != "ferrule") throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Unavailable);
        var revision = await CatalogEndpoints.ReadableRevisionAsync(services, scope.Actor, baseRevision, token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        MappingEditScript operations;
        try { operations = MappingEditScript.Parse(call.Object("edit")); }
        catch (ArgumentException) { throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall); }
        if (operations.Count == 0) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
        var generated = await editor.ProposeAsync(scope.Actor, baseRevision, operations, token);
        if (generated.Proposal is not { } content)
            throw new WorkspaceToolRefusal(generated.Outcome switch
            {
                MappingDirectEditOutcome.NotAuthorizedOrNotFound => WorkspaceToolOutcome.NotFound,
                MappingDirectEditOutcome.Busy => WorkspaceToolOutcome.Busy,
                MappingDirectEditOutcome.Rejected => WorkspaceToolOutcome.InvalidCall,
                _ => WorkspaceToolOutcome.Unavailable
            });
        // Only generated content enters Create. Storage restoration and client bytes are never admission.
        // The production store additionally requires the verified-generation capability minted by ProposeAsync.
        var proposal = new ChatMappingEditProposal(scope.Conversation.Key, scope.Turn.TurnId, scope.RoundId.Value,
            scope.CallId, revision.Summary.Sequence, content);
        var created = await proposals.CreateAsync(scope.Actor, proposal, token);
        if (created.State is not { } state)
            throw new WorkspaceToolRefusal(created.Outcome == ChatMappingEditCreateOutcome.Conflict
                ? WorkspaceToolOutcome.InvalidCall : WorkspaceToolOutcome.NotFound);
        // Full review is read separately; model output stays within its existing per-call budget.
        return Single(call.Name, scope.Budget, new
        {
            outcome = "proposed",
            proposalId = Id(proposal.ProposalId),
            fingerprint = proposal.Fingerprint.Value,
            status = ChatEndpoints.ProposalReview(state).Status,
            mappingId = Id(mapping.MappingId),
            baseRevisionId = Id(baseRevision.RevisionId),
            baseSequence = proposal.BaseSequence,
            operationCount = operations.Count,
            approvalRequired = true,
            review = "Read the exact stored proposal and its operations before approving. This call grants no save authority."
        }, [ChatWorkspaceCitationFact.Revision(baseRevision)]);
    }
}
