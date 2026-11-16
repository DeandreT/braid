using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// Application orchestration for a future dedicated authenticated approval endpoint, never a model tool.
/// This class cannot authenticate a UI click: the endpoint and restricted store must enforce that trust boundary.
/// </summary>
public sealed class ChatMappingEditApprovalService(IChatMappingEditProposalStore store, IProjectReader projects,
    IRevisionIngestionService ingestion)
{
    /// <summary>Retains review content only. Neither this operation nor the proposal itself grants save authority.</summary>
    public async Task<ChatMappingEditCreateResult> CreateAsync(ActorContext actor, ChatMappingEditProposal proposal, CancellationToken ct = default)
    {
        RequireOwner(actor, proposal.Conversation);
        var result = await store.CreateAsync(actor, proposal, ct).ConfigureAwait(false);
        if (result is null || !Enum.IsDefined(result.Outcome) ||
            (result.Outcome is ChatMappingEditCreateOutcome.Created or ChatMappingEditCreateOutcome.Existing) != (result.State is not null) ||
            result.Outcome == ChatMappingEditCreateOutcome.Created && result.State?.Status != ChatMappingEditStatus.AwaitingApproval)
            throw new InvalidOperationException("Proposal store returned an inconsistent creation result.");
        Validate(result.State, proposal.Conversation, proposal.TurnId, proposal.ProposalId, proposal.Fingerprint);
        return result;
    }

    public async Task<IReadOnlyList<ChatMappingEditState>> ListAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId,
        CancellationToken ct = default)
    {
        RequireOwner(actor, conversation);
        var states = await store.ListAsync(actor, conversation, turnId, ct).ConfigureAwait(false);
        if (states is null || states.Count > 8 || states.Select(s => s.Proposal.ProposalId).Distinct().Count() != states.Count)
            throw new InvalidOperationException("Proposal store returned an inconsistent listing.");
        foreach (var state in states)
            Validate(state, conversation, turnId, state.Proposal.ProposalId, state.Proposal.Fingerprint);
        return states;
    }

    /// <summary>Authorized discovery of one immutable proposal. Never approves or reserves ingestion.</summary>
    public async Task<ChatMappingEditState?> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId,
        Guid proposalId, CancellationToken ct = default)
    {
        RequireOwner(actor, conversation);
        var state = await store.ReadAsync(actor, conversation, turnId, proposalId, ct).ConfigureAwait(false);
        return state is null ? null : Validate(state, conversation, turnId, proposalId, state.Proposal.Fingerprint);
    }

    public async Task<ChatMappingEditState?> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId,
        Guid proposalId, Sha256Digest fingerprint, CancellationToken ct = default)
    {
        RequireOwner(actor, conversation);
        return Validate(await store.ReadAsync(actor, conversation, turnId, proposalId, ct).ConfigureAwait(false),
            conversation, turnId, proposalId, fingerprint);
    }

    /// <summary>Explicit repeat approval may bridge a committed approval whose ingestion was not yet reserved.</summary>
    public async Task<ChatMappingEditState?> ApproveAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId,
        Guid proposalId, Sha256Digest fingerprint, CancellationToken ct = default)
    {
        RequireOwner(actor, conversation);
        var state = Validate(await store.ApproveAndPrepareAsync(actor, conversation, turnId, proposalId, fingerprint, ct).ConfigureAwait(false),
            conversation, turnId, proposalId, fingerprint);
        if (state is null || state.Status is not (ChatMappingEditStatus.Approved or ChatMappingEditStatus.Pending)) return state;
        if (!await CanEditAsync(actor, state.Proposal, ct).ConfigureAwait(false))
            return state with { Status = ChatMappingEditStatus.NotPermitted };
        var proposal = state.Proposal;
        var revision = proposal.Content.Base;
        using var bytes = new MemoryStream(proposal.Content.Checkpoint.CopyBytes(), false);
        RevisionIngestionOutcome? outcome = null;
        try
        {
            // Ingestion rechecks permission and exact head at commit. Never replace the stored base or key.
            outcome = (await ingestion.AppendAsync(actor, new(revision.OrganizationId, revision.ProjectId, revision.MappingId), proposal.RequestKey,
                new(new EqualHead(revision.RevisionId)), bytes, ct).ConfigureAwait(false)).Outcome;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException) && !ct.IsCancellationRequested)
        {
            // An ambiguous append is not success or failure; the authority-checked durable state below is definitive.
        }
        var observed = await ReadAsync(actor, conversation, turnId, proposalId, fingerprint, ct).ConfigureAwait(false);
        return ObserveAttempt(observed, outcome);
    }

    /// <summary>Only resumes a stored reservation; cannot first-reserve, repair source bytes or approve.</summary>
    public async Task<ChatMappingEditState?> RecoverAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId,
        Guid proposalId, Sha256Digest fingerprint, CancellationToken ct = default)
    {
        var state = await ReadAsync(actor, conversation, turnId, proposalId, fingerprint, ct).ConfigureAwait(false);
        if (state?.Status != ChatMappingEditStatus.Pending || state.Receipt?.OperationId is not { } operation) return state;
        if (!await CanEditAsync(actor, state.Proposal, ct).ConfigureAwait(false)) return state with { Status = ChatMappingEditStatus.NotPermitted };
        var revision = state.Proposal.Content.Base;
        RevisionIngestionOutcome? outcome = null;
        try { outcome = (await ingestion.ResumeAsync(actor, new(revision.OrganizationId, revision.ProjectId), operation, ct).ConfigureAwait(false)).Outcome; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException) && !ct.IsCancellationRequested)
        {
            // A lost resume acknowledgement has the same durable-read resolution as a lost append acknowledgement.
        }
        var observed = await ReadAsync(actor, conversation, turnId, proposalId, fingerprint, ct).ConfigureAwait(false);
        return ObserveAttempt(observed, outcome);
    }

    // A durable success wins over any attempt refusal. Rejected includes admission before reservation:
    // report NotSaved for this attempt without inventing a reservation, commit or durable terminal state.
    // NeedsSource leaves the authoritative pending reservation intact; only explicit approval can resupply bytes.
    private static ChatMappingEditState? ObserveAttempt(ChatMappingEditState? observed, RevisionIngestionOutcome? outcome) =>
        observed is null || observed.Status == ChatMappingEditStatus.Saved ? observed : outcome switch
        {
            RevisionIngestionOutcome.NotAuthorizedOrNotFound => observed with { Status = ChatMappingEditStatus.NotPermitted },
            RevisionIngestionOutcome.HeadConflict => observed with { Status = ChatMappingEditStatus.OutOfDate },
            RevisionIngestionOutcome.Rejected => observed with { Status = ChatMappingEditStatus.NotSaved },
            _ => observed
        };

    private async Task<bool> CanEditAsync(ActorContext actor, ChatMappingEditProposal proposal, CancellationToken ct)
    {
        var revision = proposal.Content.Base;
        var project = new ProjectScope(revision.OrganizationId, revision.ProjectId);
        var access = await projects.GetAccessAsync(actor, project, ct).ConfigureAwait(false);
        return access is not null && access.Scope == project && access.PrincipalId == actor.PrincipalId && access.Can(ProjectAction.EditMapping);
    }

    private static void RequireOwner(ActorContext actor, ChatConversationKey conversation)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Kind != ChatConversationKind.Workspace || conversation.Creator != actor.PrincipalId)
            throw new ArgumentException("Approval requires the conversation creator.");
    }

    private static ChatMappingEditState? Validate(ChatMappingEditState? state, ChatConversationKey conversation, Guid turnId,
        Guid proposalId, Sha256Digest fingerprint)
    {
        if (state is null) return null;
        var p = state.Proposal;
        if (p.Conversation != conversation || p.TurnId != turnId || p.ProposalId != proposalId || p.Fingerprint != fingerprint || !Enum.IsDefined(state.Status))
            throw new InvalidOperationException("Proposal store returned a mismatched identity.");
        var receipt = state.Receipt;
        if (receipt is not null && (receipt.Key != p.RequestKey || receipt.RoundId != p.RoundId || receipt.Proposal.Base != p.Content.Base ||
            receipt.Proposal.BaseSequence != p.BaseSequence || receipt.Proposal.EditDigest != p.Content.Operations.Digest ||
            receipt.Proposal.OperationCount != p.Content.Operations.Count || receipt.Native is { } native &&
            (native.Scope != p.Content.Base || native.Digest != p.Content.Checkpoint.Digest || native.ByteCount != p.Content.Checkpoint.ByteCount)))
            throw new InvalidOperationException("Proposal store returned mismatched save content.");
        if ((state.Status is ChatMappingEditStatus.Approved or ChatMappingEditStatus.Pending or ChatMappingEditStatus.Saved) && receipt is null ||
            receipt?.OperationId == Guid.Empty ||
            state.Status == ChatMappingEditStatus.Approved && (receipt!.Status != "pending" || receipt.OperationId is not null) ||
            state.Status == ChatMappingEditStatus.Pending && (receipt!.Status != "pending" || receipt.OperationId is null) ||
            state.Status == ChatMappingEditStatus.Saved && (receipt!.Saved is null || receipt.OperationId is null) ||
            state.Status == ChatMappingEditStatus.AwaitingApproval && receipt is not null ||
            state.Status != ChatMappingEditStatus.Saved && receipt is not null &&
            (receipt.Status == "saved" || receipt.RevisionId is not null || receipt.Sequence is not null))
            throw new InvalidOperationException("Proposal store returned inconsistent approval state.");
        return state;
    }
}
