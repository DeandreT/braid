using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weft.Ai;
using Weft.Application;
using Weft.Domain;

namespace Weft.Api.Chat.Workspace;

public sealed partial class WorkspaceToolService
{
    public async Task<bool> AllowsRetainedProposalsAsync(ActorContext actor, ChatConversationIntent conversation, ChatTurnIntent turn,
        AiProfileSpec profile, IReadOnlyList<ChatRetainedProposalRead> proposals, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!features.Creation || conversation.Key.Kind != ChatConversationKind.Workspace || conversation.Key.Creator != actor.PrincipalId ||
            !profile.PayloadsAllowed || !profile.MappingMetadataAllowed || ManifestOf(turn) is not { Payloads: true } || proposals.Count > 8)
            return false;
        var store = services.GetRequiredService<IMappingCreationProposalStore>();
        foreach (var pin in proposals.Distinct())
        {
            if (pin.Project.OrganizationId != conversation.Key.OrganizationId) return false;
            // The authorized reader checks current creator/content access without reopening physical sources.
            var state = await store.ReadAsync(actor, pin.Project, pin.ProposalId, ct);
            ct.ThrowIfCancellationRequested();
            if (state is null || state.Proposal.Author != actor.PrincipalId || state.Proposal.Project != pin.Project ||
                state.Proposal.Id != pin.ProposalId || state.Proposal.Fingerprint != pin.Fingerprint) return false;
        }
        return true;
    }

    private async Task<ChatReadOnlyToolResult> ReadCreationFeedbackAsync(CallScope scope, WorkspaceToolCall call,
        ProjectScope project, CancellationToken ct)
    {
        var id = call.Id("proposalId");
        Sha256Digest fingerprint;
        try
        {
            var text = call.Text("fingerprint", 64);
            fingerprint = Sha256Digest.Parse(text);
            if (fingerprint.Value != text) throw new ArgumentException();
        }
        catch (ArgumentException) { throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall); }
        var store = services.GetRequiredService<IMappingCreationProposalStore>();
        async Task<MappingCreationProposal> Read()
        {
            await CreationGrantAsync(scope, project, ct);
            var state = await store.ReadAsync(scope.Actor, project, id, ct);
            await CreationGrantAsync(scope, project, ct);
            if (state is null || state.Proposal.Id != id || state.Proposal.Author != scope.Actor.PrincipalId || state.Proposal.Project != project)
                throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
            if (state.Proposal.Fingerprint != fingerprint) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
            return state.Proposal;
        }
        // Retained content is authorized by the proposal reader, independently of
        // the original source store. Fresh sources are required for a subsequent generation.
        var proposal = await Read();
        using var plan = JsonDocument.Parse(proposal.Plan.CopyBytes());
        var snapshot = proposal.Specification.Snapshot;
        var data = new
        {
            projectId = Id(project.ProjectId),
            proposalId = Id(id),
            fingerprint = fingerprint.Value,
            snapshotDigest = snapshot.Digest.Value,
            planDigest = proposal.Plan.Digest.Value,
            plan = plan.RootElement,
            assumptions = proposal.Notes.Assumptions,
            questions = proposal.Notes.Questions,
            evidence = proposal.Notes.Evidence.Select(e => new
            {
                sourceId = Id(e.Source.SourceId), sourceVersion = e.Source.Version, chunkOrdinal = e.ChunkOrdinal
            }).ToArray(),
            bundleId = Id(snapshot.Bundle.BundleId),
            sources = snapshot.Extractions.Select(e => new
            {
                sourceId = Id(e.Source.SourceId), sourceVersion = e.Source.Version, sourceDigest = e.SourceDigest.Value,
                format = e.Format.ToString(), extractorVersion = e.ExtractorVersion, profileDigest = e.ProfileDigest.Value,
                outcome = e.Outcome.ToString(), evidenceDigest = e.EvidenceDigest.Value
            }).ToArray(),
            requiresNewApproval = true
        };
        var bytes = Serialize(new Envelope(Schema, call.Name, WorkspaceToolOutcome.Available, false, data));
        if (bytes.Length > scope.Budget.Bytes) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit);
        using var content = JsonDocument.Parse(bytes);
        var nodes = CountFeedbackNodes(content.RootElement.GetProperty("data"));
        if (nodes > scope.Budget.Nodes) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit);
        // Re-observe current retained content authority after preparing the bounded response.
        _ = await Read();
        ct.ThrowIfCancellationRequested();
        return new(AiJsonObject.CopyFrom(Encoding.UTF8.GetString(bytes)), nodes,
            workspaceCitations: [ChatWorkspaceCitationFact.CreationProposal(project, id, fingerprint)],
            dataShared: new(project.ProjectId, ChatPayloadSource.Specification, id, null, bytes.Length),
            retainedProposal: new(project, id, fingerprint));
    }

    private static int CountFeedbackNodes(JsonElement value) => 1 + (value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Sum(p => CountFeedbackNodes(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Sum(CountFeedbackNodes),
        _ => 0
    });
}
