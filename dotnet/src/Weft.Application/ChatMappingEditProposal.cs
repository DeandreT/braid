using System.Security.Cryptography;
using System.Text;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Immutable review content and complete identity. Construction is not approval.</summary>
public sealed class ChatMappingEditProposal
{
    public ChatMappingEditProposal(ChatConversationKey conversation, Guid turnId, Guid roundId, string callId,
        long baseSequence, MappingEditProposal content)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        if (conversation.Kind != ChatConversationKind.Workspace || conversation.OrganizationId != content.Base.OrganizationId ||
            turnId == Guid.Empty || roundId == Guid.Empty || baseSequence < 1 || new UTF8Encoding(false, true).GetByteCount(callId) > 256 || callId.Any(char.IsControl) ||
            content.Operations.Count == 0 || content.Checkpoint.ByteCount > MappingEditPolicy.MaximumNativeBytes)
            throw new ArgumentException("Invalid proposal identity or content.");
        Conversation = conversation; TurnId = turnId; RoundId = roundId; CallId = callId; BaseSequence = baseSequence; Content = content;
        var identity = ChatCanonicalJson.Write(w =>
        {
            w.WriteStartArray(); w.WriteStringValue(conversation.OrganizationId); w.WriteStringValue(conversation.Creator.Value);
            w.WriteStringValue(conversation.ConversationId); w.WriteStringValue(turnId); w.WriteStringValue(roundId); w.WriteStringValue(callId); w.WriteEndArray();
        }, 4096);
        CanonicalIdentity = identity;
        ProposalId = Key("weft.chat-edit-proposal.v1\n" + identity);
        RequestKey = Key("weft.chat-approved-edit.v1\n" + identity);
        Fingerprint = Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(ChatCanonicalJson.Write(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("weft.chat-edit-proposal-content.v2"); w.WriteStringValue(identity);
            w.WriteStringValue(ProposalId); w.WriteStringValue(RequestKey); w.WriteStringValue(content.Base.ProjectId);
            w.WriteStringValue(content.Base.MappingId); w.WriteStringValue(content.Base.RevisionId); w.WriteNumberValue(baseSequence);
            w.WriteStringValue(content.Operations.Digest.Value); w.WriteNumberValue(content.Operations.Count);
            w.WriteNumberValue(content.Operations.CopyUtf8().Length); w.WriteStringValue(content.Checkpoint.Digest.Value);
            w.WriteNumberValue(content.Checkpoint.ByteCount);
            var summaries = content.CopyChangesUtf8();
            w.WriteStringValue(Sha256Digest.FromBytes(summaries).Value); w.WriteNumberValue(content.Changes.Length);
            w.WriteNumberValue(summaries.Length); w.WriteBooleanValue(content.ChangesTruncated); w.WriteEndArray();
        }, 8192)));
    }

    public ChatConversationKey Conversation { get; }
    public Guid TurnId { get; }
    public Guid RoundId { get; }
    public string CallId { get; }
    public long BaseSequence { get; }
    public MappingEditProposal Content { get; }
    /// <summary>Exact v1 call identity JSON used by both ID domains; default JSON escaping, no whitespace or Unicode normalization.</summary>
    public string CanonicalIdentity { get; }
    public Guid ProposalId { get; }
    public Guid RequestKey { get; }
    /// <summary>Content v2 binds exact script/checkpoint and retained summaries including truncation. Call-derived IDs keep their v1 identity domains.</summary>
    public Sha256Digest Fingerprint { get; }
    public override string ToString() => nameof(ChatMappingEditProposal);

    private static Guid Key(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        bytes[6] = (byte)(bytes[6] & 15 | 128); bytes[8] = (byte)(bytes[8] & 63 | 128);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
}

public enum ChatMappingEditStatus { AwaitingApproval, Approved, Pending, Saved, NotSaved, NotPermitted, OutOfDate }

public enum ChatMappingEditCreateOutcome { Created, Existing, Conflict, Refused }

/// <summary>Created/Existing carry exact requested content; Conflict/Refused carry no private state. Creation never grants approval.</summary>
public sealed record ChatMappingEditCreateResult(ChatMappingEditCreateOutcome Outcome, ChatMappingEditState? State = null)
{
    public override string ToString() => nameof(ChatMappingEditCreateResult);
}

/// <summary>Store-owned state. Approved means approval AND journal preparation committed atomically, not merely a model proposal.</summary>
public sealed record ChatMappingEditState(ChatMappingEditProposal Proposal, ChatMappingEditStatus Status,
    ChatMappingWriteReceipt? Receipt = null)
{
    public override string ToString() => nameof(ChatMappingEditState);
}

/// <summary>
/// Restricted persistence boundary; implementations must enforce creator/organization/project authority and retention.
/// Creation is immutable and idempotent by call identity, conflicting on changed fingerprint. It never prepares ingestion.
/// Only verified generation may populate new content; trusted storage restoration verifies integrity, not worker authenticity.
/// Reads never approve, prepare or reserve. No implementation is supplied by this application slice.
/// </summary>
public interface IChatMappingEditProposalStore
{
    /// <summary>Created is AwaitingApproval; Existing returns the exact same immutable content in its current state.
    /// Conflict means the call identity already owns different content. Conflict and Refused return no state.</summary>
    Task<ChatMappingEditCreateResult> CreateAsync(ActorContext actor, ChatMappingEditProposal proposal, CancellationToken ct);
    Task<ChatMappingEditState?> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, Guid proposalId, CancellationToken ct);
    /// <summary>
    /// Only the dedicated authenticated approval action may call this. Lock and check the stored fingerprint,
    /// creator, retention, fresh edit permission and exact head; atomically persist approval plus journal preparation
    /// using ONLY stored content and its stable request key. Return a prior committed receipt before checking a later
    /// head. Lost acknowledgement must not be interpreted as permission to ingest. Model/tool calls are not approval.
    /// </summary>
    Task<ChatMappingEditState?> ApproveAndPrepareAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId,
        Guid proposalId, Sha256Digest expectedFingerprint, CancellationToken ct);
}
