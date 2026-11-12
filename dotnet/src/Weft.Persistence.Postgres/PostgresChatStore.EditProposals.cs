using System.Text.Json;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresChatStore : IChatMappingEditProposalStore
{
    public Task<ChatMappingEditCreateResult> CreateAsync(ActorContext actor, ChatMappingEditProposal proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(proposal);
        ct.ThrowIfCancellationRequested();
        var generation = proposal.Content.VerifiedGeneration;
        // RestoreFromTrustedStorage deliberately cannot recreate this capability, even with correct hashes.
        if (generation is null || generation.Creator != actor.PrincipalId || generation.Creator != proposal.Conversation.Creator ||
            !generation.Provenance.Confirmed || generation.Provenance.Scope != proposal.Content.Base ||
            generation.Provenance.NativeDigest != proposal.Content.Checkpoint.Digest)
            return Task.FromResult(new ChatMappingEditCreateResult(ChatMappingEditCreateOutcome.Refused));
        return ExecuteAsync(actor, proposal.Conversation, true, true, async tx =>
        {
            var encoding = ChatMappingEditProposalStorage.Encode(proposal);
            await using var command = Command(tx, "SELECT weft.chat_edit_create(@org,@creator,@conversation,@turn,@facts,@generation,@provenance,@encoding,@native,@journal)", proposal.Conversation, proposal.TurnId);
            Add(command, "facts", NpgsqlDbType.Text, ProposalFacts(proposal, encoding));
            Add(command, "generation", NpgsqlDbType.Uuid, generation.GenerationId);
            Add(command, "provenance", NpgsqlDbType.Text, JsonSerializer.Serialize(generation.Provenance));
            Add(command, "encoding", NpgsqlDbType.Bytea, encoding);
            Add(command, "native", NpgsqlDbType.Bytea, proposal.Content.Checkpoint.CopyBytes());
            Add(command, "journal", NpgsqlDbType.Text, ChatMetadataCodec.MappingWrite(ProposalWriteFact(proposal)));
            var outcome = await command.ExecuteScalarAsync(ct) is string value && Enum.TryParse<ChatMappingEditCreateOutcome>(value, out var parsed)
                && Enum.IsDefined(parsed) ? parsed : throw Invalid();
            var state = outcome is ChatMappingEditCreateOutcome.Created or ChatMappingEditCreateOutcome.Existing
                ? await ReadProposalAsync(tx, proposal.Conversation, proposal.TurnId, proposal.ProposalId, ct) : null;
            if (outcome is ChatMappingEditCreateOutcome.Created or ChatMappingEditCreateOutcome.Existing && state is null) throw Invalid();
            return new ChatMappingEditCreateResult(outcome, state);
        }, new(ChatMappingEditCreateOutcome.Refused), ct);
    }

    public Task<ChatMappingEditState?> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, Guid proposalId, CancellationToken ct) =>
        ExecuteAsync<ChatMappingEditState?>(actor, conversation, false, false,
            tx => ReadProposalAsync(tx, conversation, turnId, proposalId, ct), null, ct);

    public Task<IReadOnlyList<ChatMappingEditState>> ListAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, CancellationToken ct) =>
        ExecuteAsync<IReadOnlyList<ChatMappingEditState>>(actor, conversation, false, false, async tx =>
        {
            await using var command = Command(tx, "SELECT weft.chat_edit_list(@org,@creator,@conversation,@turn)", conversation, turnId);
            if (await command.ExecuteScalarAsync(ct) is not string json) throw Invalid();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.GetArrayLength() > 8) throw Invalid();
            var result = new List<ChatMappingEditState>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = item.GetProperty("id").GetGuid();
                result.Add(DecodeProposal(item.GetProperty("state").GetString(), conversation, turnId, id) ?? throw Invalid());
            }
            if (result.Select(s => s.Proposal.ProposalId).Distinct().Count() != result.Count) throw Invalid();
            return result.AsReadOnly();
        }, [], ct);

    public Task<ChatMappingEditState?> ApproveAndPrepareAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId,
        Guid proposalId, Sha256Digest expectedFingerprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        if (_database.Profile != ChatDatabaseProfile.Approver)
            throw new InvalidOperationException("Exact approval requires its isolated approval profile.");
        return ExecuteAsync<ChatMappingEditState?>(actor, conversation, true, false, async tx =>
        {
            await using var command = Command(tx, "SELECT weft.chat_edit_approve(@org,@creator,@conversation,@turn,@proposal,@fingerprint)", conversation, turnId);
            Add(command, "proposal", NpgsqlDbType.Uuid, proposalId); Add(command, "fingerprint", NpgsqlDbType.Text, expectedFingerprint.Value);
            var state = DecodeProposal(await command.ExecuteScalarAsync(ct), conversation, turnId, proposalId);
            if (state is not null && state.Proposal.Fingerprint != expectedFingerprint) throw Invalid();
            return state;
        }, null, ct);
    }

    private static async Task<ChatMappingEditState?> ReadProposalAsync(PostgresTransaction tx, ChatConversationKey conversation,
        Guid turnId, Guid proposalId, CancellationToken ct)
    {
        await using var command = Command(tx, "SELECT weft.chat_edit_read(@org,@creator,@conversation,@turn,@proposal)", conversation, turnId);
        Add(command, "proposal", NpgsqlDbType.Uuid, proposalId);
        return DecodeProposal(await command.ExecuteScalarAsync(ct), conversation, turnId, proposalId);
    }

    internal static string ProposalFacts(ChatMappingEditProposal p, byte[] encoding)
    {
        var f = ChatMappingEditStorageIdentity.FromProposal(p);
        return JsonSerializer.Serialize(new
        {
            organization = f.Conversation.OrganizationId,
            creator = f.Conversation.Creator.Value,
            conversation = f.Conversation.ConversationId,
            turn = f.TurnId,
            round = f.RoundId,
            call = f.CallId,
            proposal = f.ProposalId,
            key = f.RequestKey,
            fingerprint = f.Fingerprint.Value,
            canonicalIdentity = p.CanonicalIdentity,
            project = f.Base.ProjectId,
            mapping = f.Base.MappingId,
            @base = f.Base.RevisionId,
            baseSequence = f.BaseSequence,
            operationsDigest = f.OperationsDigest.Value,
            operationsBytes = f.OperationsByteCount,
            operationCount = f.OperationCount,
            nativeDigest = f.NativeDigest.Value,
            nativeBytes = f.NativeByteCount,
            changesDigest = f.ChangesDigest.Value,
            changesBytes = f.ChangesByteCount,
            changeCount = f.ChangeCount,
            changesTruncated = f.ChangesTruncated,
            encodingDigest = Sha256Digest.FromBytes(encoding).Value
        });
    }

    internal static ChatMappingEditState? DecodeProposal(object? value, ChatConversationKey conversation, Guid turnId, Guid proposalId)
    {
        if (value is null or DBNull) return null;
        try
        {
            if (value is not string json) throw Invalid();
            using var response = JsonDocument.Parse(json);
            var root = response.RootElement;
            using var facts = JsonDocument.Parse(root.GetProperty("facts").GetString()!);
            var f = facts.RootElement;
            Guid Id(string name) => f.GetProperty(name).GetGuid();
            int Count(string name) => f.GetProperty(name).GetInt32();
            Sha256Digest Digest(string name) => Sha256Digest.Parse(f.GetProperty(name).GetString()!);
            var expected = new ChatMappingEditStorageIdentity(ChatConversationKey.ForWorkspace(Id("organization"), new(Id("creator")), Id("conversation")),
                Id("turn"), Id("round"), f.GetProperty("call").GetString()!, Id("proposal"), Id("key"), Digest("fingerprint"),
                new(Id("organization"), Id("project"), Id("mapping"), Id("base")), f.GetProperty("baseSequence").GetInt64(),
                Digest("operationsDigest"), Count("operationsBytes"), Count("operationCount"), Digest("nativeDigest"), Count("nativeBytes"),
                Digest("changesDigest"), Count("changesBytes"), Count("changeCount"), f.GetProperty("changesTruncated").GetBoolean());
            if (expected.Conversation != conversation || expected.TurnId != turnId || expected.ProposalId != proposalId) throw Invalid();
            var text = root.GetProperty("encoding").GetString()!;
            // PostgreSQL's base64 encoder inserts line breaks; allow those, with a bounded allocation.
            if (text.Length > 12 * 1024 * 1024) throw Invalid();
            var encoding = Convert.FromBase64String(text);
            if (Sha256Digest.FromBytes(encoding) != Digest("encodingDigest")) throw Invalid();
            var proposal = ChatMappingEditProposalStorage.RestoreFromTrustedStorage(encoding, expected);
            if (f.GetProperty("canonicalIdentity").GetString() != proposal.CanonicalIdentity) throw Invalid();
            if (!Enum.TryParse<ChatMappingEditStatus>(root.GetProperty("status").GetString(), out var status) || !Enum.IsDefined(status)) throw Invalid();
            Guid? ReceiptId(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetGuid();
            var operation = ReceiptId("operation"); var revision = ReceiptId("revision");
            long? sequence = root.GetProperty("sequence").ValueKind == JsonValueKind.Null ? null : root.GetProperty("sequence").GetInt64();
            if (operation == Guid.Empty || status == ChatMappingEditStatus.Pending && operation is null ||
                status == ChatMappingEditStatus.Approved && operation is not null ||
                status == ChatMappingEditStatus.Saved && (operation is null || revision is null || sequence <= proposal.BaseSequence || sequence is null) ||
                status != ChatMappingEditStatus.Saved && (revision is not null || sequence is not null) ||
                status == ChatMappingEditStatus.AwaitingApproval && operation is not null) throw Invalid();
            var receipt = status == ChatMappingEditStatus.AwaitingApproval || status is ChatMappingEditStatus.NotPermitted or ChatMappingEditStatus.OutOfDate && operation is null
                ? null : new ChatMappingWriteReceipt(proposal.RequestKey, proposal.RoundId,
                    status == ChatMappingEditStatus.Saved ? "saved" : status == ChatMappingEditStatus.NotSaved ? "notSaved" : "pending",
                    ProposalWriteFact(proposal), operation, revision, sequence, proposal.Content.Checkpoint);
            return new(proposal, status, receipt);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        { throw Invalid(); }
    }

    private static ChatMappingWriteFact ProposalWriteFact(ChatMappingEditProposal p) => new ChatMappingWriteFact(ChatMappingWriteOutcome.Proposed,
        ChatMappingWriteReason.NotPermitted, p.Content.Base, p.BaseSequence, null, null, null, null,
        p.Content.Operations.Count, p.Content.Operations.Digest,
        p.Content.Changes.Take(ChatMappingWriteFact.MaximumChanges).Select(c => new ChatMappingChange(c.Field, c.Before, c.After)).ToArray(),
        p.Content.ChangesTruncated || p.Content.Changes.Length > ChatMappingWriteFact.MaximumChanges).Fitted();
}
