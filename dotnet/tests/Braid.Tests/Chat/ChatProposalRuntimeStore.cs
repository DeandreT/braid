using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Recording test ports, not production admission or transaction evidence.</summary>
internal sealed class ChatProposalRuntimeStore : IChatMappingEditProposalStore, IProjectReader, IRevisionIngestionService
{
    internal ChatMappingEditState? State;
    internal bool Readable = true, Editable = true, HeadChanged, LosePrepareAck, Commit = true;
    internal int Creates, Approvals, Appends, Resumes;
    internal ChatMappingEditApprovalService Service() => new(this, this, this);
    internal ChatMappingEditProposal Proposal => State!.Proposal;
    internal readonly Guid Operation = Guid.NewGuid();

    public Task<ChatMappingEditCreateResult> CreateAsync(ActorContext actor, ChatMappingEditProposal proposal, CancellationToken ct)
    {
        Creates++;
        Assert.Equal(actor.PrincipalId, proposal.Conversation.Creator);
        if (State is not null)
            return Task.FromResult(State.Proposal.Fingerprint == proposal.Fingerprint
                ? new ChatMappingEditCreateResult(ChatMappingEditCreateOutcome.Existing, State)
                : new(ChatMappingEditCreateOutcome.Conflict));
        State = new(proposal, ChatMappingEditStatus.AwaitingApproval);
        return Task.FromResult(new ChatMappingEditCreateResult(ChatMappingEditCreateOutcome.Created, State));
    }

    public Task<ChatMappingEditState?> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, Guid proposalId, CancellationToken ct) =>
        Task.FromResult(Readable && State is not null && actor.PrincipalId == Proposal.Conversation.Creator &&
            conversation == Proposal.Conversation && turnId == Proposal.TurnId && proposalId == Proposal.ProposalId ? State : null);
    public async Task<IReadOnlyList<ChatMappingEditState>> ListAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, CancellationToken ct) =>
        State is not null && await ReadAsync(actor, conversation, turnId, Proposal.ProposalId, ct) is { } state ? [state] : [];
    public async Task<ChatMappingEditState?> ApproveAndPrepareAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId,
        Guid proposalId, Sha256Digest expectedFingerprint, CancellationToken ct)
    {
        Approvals++;
        var state = await ReadAsync(actor, conversation, turnId, proposalId, ct);
        if (state is null || state.Proposal.Fingerprint != expectedFingerprint) return null;
        if (state.Status == ChatMappingEditStatus.Saved) return state;
        if (!Editable) return state with { Status = ChatMappingEditStatus.NotPermitted };
        if (HeadChanged) return state with { Status = ChatMappingEditStatus.OutOfDate };
        State = state with { Status = ChatMappingEditStatus.Approved, Receipt = Receipt() };
        if (LosePrepareAck) throw new IOException("Lost preparation acknowledgement");
        return State;
    }
    internal ChatMappingWriteReceipt Receipt(Guid? operation = null)
    {
        var p = Proposal;
        return new(p.RequestKey, p.RoundId, "pending", new(ChatMappingWriteOutcome.Proposed, ChatMappingWriteReason.NotPermitted,
            p.Content.Base, p.BaseSequence, null, null, null, null, p.Content.Operations.Count, p.Content.Operations.Digest, [], false),
            operation, null, null, p.Content.Checkpoint);
    }
    public async Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target, Guid idempotencyKey,
        AppendRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default)
    {
        Appends++;
        Assert.Equal(Proposal.RequestKey, idempotencyKey);
        Assert.Equal(Proposal.Conversation.Creator, actor.PrincipalId);
        Assert.Equal(new EqualHead(Proposal.Content.Base.RevisionId), request.HeadCondition);
        using var bytes = new MemoryStream(); await nativeSource.CopyToAsync(bytes, cancellationToken);
        Assert.Equal(Proposal.Content.Checkpoint.CopyBytes(), bytes.ToArray());
        State = new(Proposal, Commit ? ChatMappingEditStatus.Saved : ChatMappingEditStatus.Pending,
            Receipt(Operation) with { Status = Commit ? "saved" : "pending", RevisionId = Commit ? Guid.NewGuid() : null, Sequence = Commit ? 3 : null });
        return RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.ClaimLost);
    }
    public Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default)
    {
        Resumes++;
        Assert.Equal(Operation, operationId);
        return Task.FromResult(RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.NeedsSource));
    }
    public Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<ProjectAccess?>(new(scope, actor.PrincipalId, Editable ? ProjectRole.Publisher : ProjectRole.Viewer));
    public Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit, Guid? afterProjectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey, CreateRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
