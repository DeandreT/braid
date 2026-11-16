using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Routes the proposal port through separately admitted read, generation and approval pools.
/// The host owns pool lifetimes. Register only when the dedicated authenticated approval route is installed;
/// never expose the approval method as a model tool.</summary>
public sealed class PostgresChatEditProposalStore : IChatMappingEditProposalStore
{
    private readonly PostgresChatStore _reader, _writer, _approver;

    public PostgresChatEditProposalStore(PostgresChatDatabase reader, PostgresChatDatabase writer, PostgresChatDatabase approver)
    {
        ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(writer); ArgumentNullException.ThrowIfNull(approver);
        if (reader.Profile != ChatDatabaseProfile.Reader || writer.Profile != ChatDatabaseProfile.Writer || approver.Profile != ChatDatabaseProfile.Approver)
            throw new ArgumentException("Proposal persistence requires distinct reader, writer and approver profiles.");
        _reader = new(reader); _writer = new(writer); _approver = new(approver);
    }

    public Task<ChatMappingEditCreateResult> CreateAsync(ActorContext actor, ChatMappingEditProposal proposal, CancellationToken ct) =>
        _writer.CreateAsync(actor, proposal, ct);
    public Task<ChatMappingEditState?> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, Guid proposalId, CancellationToken ct) =>
        _reader.ReadAsync(actor, conversation, turnId, proposalId, ct);
    public Task<IReadOnlyList<ChatMappingEditState>> ListAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, CancellationToken ct) =>
        _reader.ListAsync(actor, conversation, turnId, ct);
    public Task<ChatMappingEditState?> ApproveAndPrepareAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, Guid proposalId,
        Sha256Digest expectedFingerprint, CancellationToken ct) =>
        _approver.ApproveAndPrepareAsync(actor, conversation, turnId, proposalId, expectedFingerprint, ct);
}
