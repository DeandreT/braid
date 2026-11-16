using System.Text;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// Immutable facts retained independently of the private encoding by the restricted store. Expected facts must
/// come from that authorized store, never from client input or the encoding being restored. Hashes are not authentication.
/// </summary>
public sealed record ChatMappingEditStorageIdentity(ChatConversationKey Conversation, Guid TurnId, Guid RoundId, string CallId,
    Guid ProposalId, Guid RequestKey, Sha256Digest Fingerprint, RevisionScope Base, long BaseSequence,
    Sha256Digest OperationsDigest, int OperationsByteCount, int OperationCount, Sha256Digest NativeDigest, int NativeByteCount,
    Sha256Digest ChangesDigest, int ChangesByteCount, int ChangeCount, bool ChangesTruncated)
{
    public static ChatMappingEditStorageIdentity FromProposal(ChatMappingEditProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var content = proposal.Content;
        var summaries = content.CopyChangesUtf8();
        return new(proposal.Conversation, proposal.TurnId, proposal.RoundId, proposal.CallId, proposal.ProposalId,
            proposal.RequestKey, proposal.Fingerprint, content.Base, proposal.BaseSequence, content.Operations.Digest,
            content.Operations.CopyUtf8().Length, content.Operations.Count, content.Checkpoint.Digest, content.Checkpoint.ByteCount,
            Sha256Digest.FromBytes(summaries), summaries.Length, content.Changes.Length, content.ChangesTruncated);
    }

    public override string ToString() => nameof(ChatMappingEditStorageIdentity);
}

/// <summary>
/// Private persistence encoding v1 of content-fingerprint v2. This is not an HTTP or model-input codec.
/// A restricted store populated only by verified generation is the authenticity boundary: restoration verifies
/// integrity against independently retained immutable facts, NOT worker provenance or approval. Never admit
/// client-supplied bytes/facts through this path. Store reads must first enforce creator, project and retention authority.
/// </summary>
public static class ChatMappingEditProposalStorage
{
    public const string Format = "braid.chat-edit-storage.v1";
    public const int MaximumEncodedBytes = 8 * 1024 * 1024;

    /// <summary>Returns a new copy, retaining the complete canonical operation log and exact native bytes as base64.</summary>
    public static byte[] Encode(ChatMappingEditProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return Encoding.UTF8.GetBytes(ChatCanonicalJson.Write(w =>
        {
            w.WriteStartArray(); w.WriteStringValue(Format);
            w.WriteStringValue(proposal.Conversation.OrganizationId); w.WriteStringValue(proposal.Conversation.Creator.Value);
            w.WriteStringValue(proposal.Conversation.ConversationId); w.WriteStringValue(proposal.TurnId);
            w.WriteStringValue(proposal.RoundId); w.WriteStringValue(proposal.CallId);
            w.WriteStringValue(proposal.Content.Base.ProjectId); w.WriteStringValue(proposal.Content.Base.MappingId);
            w.WriteStringValue(proposal.Content.Base.RevisionId); w.WriteNumberValue(proposal.BaseSequence);
            w.WriteBase64StringValue(proposal.Content.Operations.CopyUtf8()); w.WriteBase64StringValue(proposal.Content.Checkpoint.CopyBytes());
            w.WriteRawValue(proposal.Content.CopyChangesUtf8()); w.WriteBooleanValue(proposal.Content.ChangesTruncated);
            w.WriteEndArray();
        }, MaximumEncodedBytes));
    }

    /// <summary>
    /// Trusted persistence only; restores copy-owned content after mandatory full identity, scope, digest, length,
    /// count and canonical-byte checks. Does not establish worker authenticity. Refuses unknown versions and never repairs bytes.
    /// </summary>
    public static ChatMappingEditProposal RestoreFromTrustedStorage(ReadOnlySpan<byte> encoding, ChatMappingEditStorageIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (encoding.Length is < 1 or > MaximumEncodedBytes) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(encoding.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 15 || root[0].GetString() != Format) throw Invalid();
            var conversation = ChatConversationKey.ForWorkspace(root[1].GetGuid(), new PrincipalId(root[2].GetGuid()), root[3].GetGuid());
            var scope = new RevisionScope(conversation.OrganizationId, root[7].GetGuid(), root[8].GetGuid(), root[9].GetGuid());
            var operationBytes = Decode(root[11], MappingEditPolicy.MaximumOperationsBytes);
            var operations = MappingEditScript.Parse(operationBytes);
            if (!operations.CopyUtf8().AsSpan().SequenceEqual(operationBytes)) throw Invalid();
            var native = NativeArtifact.Admit(scope, Decode(root[12], MappingEditPolicy.MaximumNativeBytes), MappingEditPolicy.MaximumNativeBytes);
            var summaries = root[13];
            if (summaries.ValueKind != JsonValueKind.Array || summaries.GetArrayLength() > MappingEditProposal.MaximumChanges ||
                Encoding.UTF8.GetByteCount(summaries.GetRawText()) > MappingEditProposal.MaximumChangesBytes) throw Invalid();
            var changes = new List<MappingEditChange>();
            foreach (var item in summaries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3) throw Invalid();
                changes.Add(new(item[0].GetString()!, item[1].GetString()!, item[2].GetString()!));
            }
            var content = new MappingEditProposal(native, operations, changes, root[14].GetBoolean());
            var proposal = new ChatMappingEditProposal(conversation, root[4].GetGuid(), root[5].GetGuid(), root[6].GetString()!, root[10].GetInt64(), content);
            if (ChatMappingEditStorageIdentity.FromProposal(proposal) != expected || !Encode(proposal).AsSpan().SequenceEqual(encoding)) throw Invalid();
            return proposal;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            // Never include private source bytes or parser diagnostics in a persistence refusal.
            throw Invalid();
        }
    }

    private static byte[] Decode(JsonElement element, int maximum)
    {
        var text = element.GetString();
        if (text is null || text.Length > 4 * ((maximum + 2) / 3)) throw Invalid();
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length is 0 || bytes.Length > maximum) throw Invalid();
        return bytes;
    }

    private static ArgumentException Invalid() => new("Stored chat edit proposal does not match its immutable storage contract.");
}
