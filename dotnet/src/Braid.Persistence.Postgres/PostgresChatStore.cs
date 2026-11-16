using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Short creator-private ledger transactions; no model, tool, job, API or host registration.</summary>
public sealed partial class PostgresChatStore : IChatConversationStore, IChatTurnStore
{
    private readonly PostgresChatDatabase _database;
    private const string Scope = "organization_id=@org AND creator_id=@creator AND conversation_id=@conversation";
    private const string TurnScope = Scope + " AND turn_id=@turn";
    // The conversation's complete identity: a revision conversation's exact scope, or a workspace conversation's none.
    private const string ExactScope = "conversation_kind=@conversationKind AND project_id IS NOT DISTINCT FROM @project AND mapping_id IS NOT DISTINCT FROM @mapping AND revision_id IS NOT DISTINCT FROM @revision";
    public PostgresChatStore(PostgresChatDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<ChatStoreOutcome> CreateAsync(ActorContext actor, ChatConversationIntent intent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return ExecuteAsync(actor, intent.Key, true, false, async tx =>
        {
            var now = await NowAsync(tx, ct);
            if (intent.RetainUntilUtc <= now) return ChatStoreOutcome.Expired;
            if (!await AuthorizedAsync(tx, intent.Key, ct)) return ChatStoreOutcome.NotAuthorizedOrNotFound;
            const string admission = "chat_create_admission";
            await tx.Transaction.SaveAsync(admission, ct);
            var identity = await ReserveIdentityAsync(tx, intent.Key, 1, intent.Key.ConversationId, null, ct);
            if (!await AuthorizedAsync(tx, intent.Key, ct))
            { await tx.Transaction.RollbackAsync(admission, ct); return ChatStoreOutcome.NotAuthorizedOrNotFound; }
            if (identity is not (ChatStoreOutcome.Applied or ChatStoreOutcome.Replayed)) return identity;
            if (identity == ChatStoreOutcome.Replayed)
            {
                await LockAsync(tx, intent.Key, ct);
                var original = await ConversationAsync(tx, intent.Key, ct);
                // A reservation without content is not permission to reconstruct deleted/crashed work.
                if (original is null) return await AuthorizedAsync(tx, intent.Key, ct) ? ChatStoreOutcome.Conflict : ChatStoreOutcome.NotAuthorizedOrNotFound;
                return original == intent ? ChatStoreOutcome.Replayed : ChatStoreOutcome.Conflict;
            }
            await using (var insert = Command(tx, """
                INSERT INTO braid.chat_conversations(organization_id,creator_id,conversation_id,project_id,mapping_id,revision_id,
                    projection_entry_id,projection_id,projection_key,projection_digest,validation_entry_id,validation_id,validation_key,validation_digest,retain_until,
                    conversation_kind,focus_project_id,focus_mapping_id,focus_revision_id)
                VALUES(@org,@creator,@conversation,@project,@mapping,@revision,@pe,@pi,@pk,@pd,@ve,@vi,@vk,@vd,@retain,@conversationKind,@focusProject,@focusMapping,@focusRevision)
                ON CONFLICT(organization_id,creator_id,conversation_id) DO NOTHING
                """, intent.Key))
            {
                Pin(insert, "p", intent.Projection); Pin(insert, "v", intent.Validation);
                Add(insert, "retain", NpgsqlDbType.TimestampTz, intent.RetainUntilUtc);
                Add(insert, "focusProject", NpgsqlDbType.Uuid, intent.Focus?.Mapping.ProjectId);
                Add(insert, "focusMapping", NpgsqlDbType.Uuid, intent.Focus?.Mapping.MappingId);
                Add(insert, "focusRevision", NpgsqlDbType.Uuid, intent.Focus?.RevisionId);
                var changed = await insert.ExecuteNonQueryAsync(ct);
                if (changed is < 0 or > 1) throw Invalid();
                // Unique-index waits are followed by a lock and a NEW statement authority read.
                await LockAsync(tx, intent.Key, ct);
                var stored = await ConversationAsync(tx, intent.Key, ct);
                if (stored is null) { await tx.Transaction.RollbackAsync(admission, ct); return ChatStoreOutcome.NotAuthorizedOrNotFound; }
                if (stored != intent) { await tx.Transaction.RollbackAsync(admission, ct); return ChatStoreOutcome.Conflict; }
                await tx.Transaction.ReleaseAsync(admission, ct);
                return changed == 1 ? ChatStoreOutcome.Applied : ChatStoreOutcome.Replayed;
            }
        }, ChatStoreOutcome.NotAuthorizedOrNotFound, ct);
    }

    public Task<ChatConversationIntent?> ReadAsync(ActorContext actor, ChatConversationKey key, CancellationToken ct = default) =>
        ExecuteAsync<ChatConversationIntent?>(actor, key, false, false, async tx =>
        {
            var conversation = await ConversationAsync(tx, key, ct);
            return conversation is not null && conversation.RetainUntilUtc > await NowAsync(tx, ct) ? conversation : null;
        }, null, ct);

    public Task<ChatStoreOutcome> DeleteExpiredAsync(ActorContext actor, ChatConversationKey key, ChatErasurePolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return ExecuteAsync(actor, key, true, true, async tx =>
        {
            var conversation = await ConversationAsync(tx, key, ct);
            if (conversation is null) return ChatStoreOutcome.NotAuthorizedOrNotFound;
            await using var erase = Command(tx, "SELECT braid.chat_erase_expired(@org,@creator,@conversation,@project,@mapping,@revision,@policy,@policyVersion,@retirement)", key);
            Add(erase, "policy", NpgsqlDbType.Text, policy.PolicyId); Add(erase, "policyVersion", NpgsqlDbType.Integer, policy.Version);
            Add(erase, "retirement", NpgsqlDbType.TimestampTz, policy.IdentityRetirementNotBeforeUtc);
            return ReadOutcome(await erase.ExecuteScalarAsync(ct));
        }, ChatStoreOutcome.NotAuthorizedOrNotFound, ct);
    }

    public Task<ChatTurnWriteResult> StartAsync(ActorContext actor, ChatConversationKey key, ChatTurnIntent intent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return ExecuteAsync(actor, key, true, true, async tx =>
        {
            var conversation = await ConversationAsync(tx, key, ct);
            if (conversation is null) return Denied();
            var now = await NowAsync(tx, ct);
            if (conversation.RetainUntilUtc <= now) return new(ChatStoreOutcome.Expired);
            var existing = await TurnAsync(tx, key, intent.TurnId, ct);
            if (existing is not null) return new(existing.Intent == intent ? ChatStoreOutcome.Replayed : ChatStoreOutcome.Conflict, existing.Intent == intent ? existing : null);
            if (intent.DeadlineUtc <= now || intent.DeadlineUtc > conversation.RetainUntilUtc) return new(ChatStoreOutcome.Expired);
            await using (var active = Command(tx, $"SELECT EXISTS(SELECT 1 FROM braid.chat_turns WHERE {Scope} AND phase=1)", key))
                if (await active.ExecuteScalarAsync(ct) is true) return new(ChatStoreOutcome.Conflict);
            const string admission = "chat_turn_identity_admission";
            await tx.Transaction.SaveAsync(admission, ct);
            foreach (var (kind, id) in new[] { (2, intent.TurnId), (3, intent.Envelope.RequestId) })
            {
                var identity = await ReserveIdentityAsync(tx, key, kind, id, intent.TurnId, ct);
                if (!await AuthorizedAsync(tx, key, ct)) { await tx.Transaction.RollbackAsync(admission, ct); return Denied(); }
                if (identity != ChatStoreOutcome.Applied)
                { await tx.Transaction.RollbackAsync(admission, ct); return new(identity == ChatStoreOutcome.Replayed ? ChatStoreOutcome.Conflict : identity); }
            }
            await tx.Transaction.ReleaseAsync(admission, ct);
            var canonical = ChatCanonicalEnvelope.Turn(conversation, intent);
            await using (var insert = Command(tx, """
                INSERT INTO braid.chat_turns(organization_id,creator_id,conversation_id,turn_id,original_request,request_id,original_envelope,canonical_envelope,envelope_fingerprint,profile_id,profile_version,
                    provider_id,model_id,metadata_policy_id,metadata_policy_version,profile_fingerprint,max_events,max_event_bytes,max_rounds,deadline)
                VALUES(@org,@creator,@conversation,@turn,@request,@requestId,@originalEnvelope,@canonicalEnvelope,@envelopeFingerprint,@profile,@pv,@provider,@model,@policy,@mpv,@fingerprint,@events,@bytes,@rounds,@deadline)
                """, key, intent.TurnId))
            {
                Add(insert, "request", NpgsqlDbType.Text, intent.OriginalRequest);
                Add(insert, "requestId", NpgsqlDbType.Uuid, intent.Envelope.RequestId); Add(insert, "originalEnvelope", NpgsqlDbType.Text, intent.Envelope.CanonicalJson);
                Add(insert, "canonicalEnvelope", NpgsqlDbType.Text, canonical); Add(insert, "envelopeFingerprint", NpgsqlDbType.Text, ChatCanonicalEnvelope.Fingerprint(canonical).Value);
                Add(insert, "profile", NpgsqlDbType.Text, intent.Profile.ProfileId); Add(insert, "pv", NpgsqlDbType.Integer, intent.Profile.Version);
                Add(insert, "provider", NpgsqlDbType.Text, intent.Profile.ProviderId); Add(insert, "model", NpgsqlDbType.Text, intent.Profile.ModelId);
                Add(insert, "policy", NpgsqlDbType.Text, intent.Profile.MetadataPolicyId); Add(insert, "mpv", NpgsqlDbType.Integer, intent.Profile.MetadataPolicyVersion);
                Add(insert, "fingerprint", NpgsqlDbType.Text, intent.Profile.ConfigurationFingerprint.Value);
                Add(insert, "events", NpgsqlDbType.Integer, intent.Budget.MaxEvents); Add(insert, "bytes", NpgsqlDbType.Integer, intent.Budget.MaxEventBytes);
                Add(insert, "rounds", NpgsqlDbType.Integer, intent.Budget.MaxRounds); Add(insert, "deadline", NpgsqlDbType.TimestampTz, intent.DeadlineUtc);
                RequireOne(await insert.ExecuteNonQueryAsync(ct));
            }
            await InsertEventAsync(tx, key, intent.TurnId, 1, Guid.NewGuid(), ChatEventKind.Started, "", null, null, ct);
            return new(ChatStoreOutcome.Applied, await RequireTurnAsync(tx, key, intent.TurnId, ct));
        }, Denied(), ct);
    }

    public Task<ChatTurnFacts?> ReadTurnAsync(ActorContext actor, ChatConversationKey key, Guid turnId, CancellationToken ct = default)
    {
        ChatBounds.Id(turnId);
        return ExecuteAsync<ChatTurnFacts?>(actor, key, false, false, async tx =>
            await TurnAsync(tx, key, turnId, ct, visibleOnly: true), null, ct);
    }

    public Task<ChatRoundFacts?> ReadRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId, Guid roundId, CancellationToken ct = default)
    {
        ChatBounds.Id(turnId); ChatBounds.Id(roundId);
        return ExecuteAsync<ChatRoundFacts?>(actor, key, false, false, async tx =>
            await RoundAsync(tx, key, turnId, roundId, ct, visibleOnly: true), null, ct);
    }

    public Task<IReadOnlyList<ChatStoredEvent>> ReadEventsAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long afterSequence, int limit, CancellationToken ct = default)
    {
        ChatBounds.Id(turnId);
        if (afterSequence is < 0 or > 4096 || limit is < 1 or > 100) throw ChatBounds.Invalid();
        return ExecuteAsync<IReadOnlyList<ChatStoredEvent>>(actor, key, false, false, async tx =>
        {
            await using var command = Command(tx, $"SELECT sequence,event_id,kind,private_text,round_id,metadata_json FROM braid.chat_events WHERE {TurnScope} AND {AuthorizedParent} AND sequence>@after ORDER BY sequence LIMIT @limit", key, turnId);
            Add(command, "visibleOnly", NpgsqlDbType.Boolean, true);
            Add(command, "after", NpgsqlDbType.Bigint, afterSequence); Add(command, "limit", NpgsqlDbType.Integer, limit);
            var result = new List<ChatStoredEvent>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            long sequence = afterSequence;
            while (await reader.ReadAsync(ct))
            {
                var next = reader.GetInt32(0);
                if (next != sequence + 1 || !Enum.IsDefined((ChatEventKind)reader.GetInt32(2))) throw Invalid();
                var item = new ChatStoredEvent(next, reader.GetGuid(1), (ChatEventKind)reader.GetInt32(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5));
                _ = item.Usage; _ = item.ProviderFailure; _ = item.Citation; _ = item.WorkspaceCitation; _ = item.DataShared; _ = item.MappingWrite; _ = item.TerminalMetadata;
                result.Add(item);
                sequence = next;
            }
            return result.AsReadOnly();
        }, Array.Empty<ChatStoredEvent>(), ct);
    }

    private async Task<T> ExecuteAsync<T>(ActorContext actor, ChatConversationKey key, bool write, bool lockExisting,
        Func<PostgresTransaction, Task<T>> operation, T denied, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        if (actor.PrincipalId != key.Creator) return denied;
        try
        {
            await using var tx = await _database.BeginAsync(actor, key, write, ct);
            if (lockExisting) await LockAsync(tx, key, ct);
            var result = await operation(tx);
            // No success/no-op/refusal escapes before COMMIT acknowledgement.
            await tx.CommitAsync(ct);
            return result;
        }
        catch (NpgsqlException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (NpgsqlException) { throw new InvalidOperationException("Private chat persistence failed."); }
        catch (ArgumentException) { throw new InvalidOperationException("Private chat stored facts are invalid."); }
        catch (InvalidCastException) { throw new InvalidOperationException("Private chat stored facts are invalid."); }
    }

    private static async Task LockAsync(PostgresTransaction tx, ChatConversationKey key, CancellationToken ct)
    {
        // This statement deliberately chooses no authority decision or policy clock.
        await using var command = Command(tx, $"SELECT conversation_id FROM ONLY braid.chat_conversations WHERE {Scope} FOR UPDATE", key);
        await command.ExecuteScalarAsync(ct);
    }
    private static async Task<bool> AuthorizedAsync(PostgresTransaction tx, ChatConversationKey key, CancellationToken ct)
    {
        await using var command = Command(tx, "SELECT braid.chat_authorized(@org,@project,@creator)", key);
        return await command.ExecuteScalarAsync(ct) is true;
    }
    private static async Task<bool> VisibleAsync(PostgresTransaction tx, ChatConversationKey key, CancellationToken ct)
    {
        await using var command = Command(tx, $"SELECT EXISTS(SELECT 1 FROM braid.chat_conversations WHERE {Scope} AND {ExactScope} AND retain_until>statement_timestamp() AND braid.chat_authorized(organization_id,project_id,creator_id))", key);
        return await command.ExecuteScalarAsync(ct) is true;
    }
    private static async Task<DateTimeOffset> NowAsync(PostgresTransaction tx, CancellationToken ct)
    {
        await using var command = tx.CreateCommand("SELECT statement_timestamp()");
        var result = await command.ExecuteScalarAsync(ct);
        if (result is not DateTime time) throw Invalid();
        var now = new DateTimeOffset(time); ChatBounds.Instant(now); return now;
    }
    private static NpgsqlCommand Command(PostgresTransaction tx, string sql, ChatConversationKey key, Guid? turn = null)
    {
        var command = tx.CreateCommand(sql);
        Add(command, "org", NpgsqlDbType.Uuid, key.OrganizationId); Add(command, "creator", NpgsqlDbType.Uuid, key.Creator.Value);
        Add(command, "conversation", NpgsqlDbType.Uuid, key.ConversationId); Add(command, "project", NpgsqlDbType.Uuid, key.Revision?.ProjectId);
        Add(command, "mapping", NpgsqlDbType.Uuid, key.Revision?.MappingId); Add(command, "revision", NpgsqlDbType.Uuid, key.Revision?.RevisionId);
        Add(command, "conversationKind", NpgsqlDbType.Integer, (int)key.Kind);
        if (turn is { } id) Add(command, "turn", NpgsqlDbType.Uuid, id);
        return command;
    }
    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
    private static void Pin(NpgsqlCommand command, string prefix, ChatReportPin? pin)
    {
        Add(command, prefix + "e", NpgsqlDbType.Uuid, pin?.CacheEntryId); Add(command, prefix + "i", NpgsqlDbType.Uuid, pin?.DerivationId);
        Add(command, prefix + "k", NpgsqlDbType.Text, pin?.KeyFingerprint.Value); Add(command, prefix + "d", NpgsqlDbType.Text, pin?.ObjectDigest.Value);
    }
    private static ChatTurnWriteResult Denied() => new(ChatStoreOutcome.NotAuthorizedOrNotFound);
    private static InvalidOperationException Invalid() => new("Private chat stored facts are invalid.");
    private static void RequireOne(int count) { if (count != 1) throw Invalid(); }
    private static ChatStoreOutcome ReadOutcome(object? value) => value is string text && Enum.TryParse<ChatStoreOutcome>(text, out var outcome) && Enum.IsDefined(outcome)
        ? outcome : throw Invalid();
}
