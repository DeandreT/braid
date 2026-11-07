using System.Text;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresChatStore
{
    public Task<ChatRoundWriteResult> ReserveRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, ChatRoundIntent intent, CancellationToken ct = default)
    {
        Validate(turnId, expectedVersion); ArgumentNullException.ThrowIfNull(intent);
        return ExecuteAsync(actor, key, true, true, async tx =>
        {
            if (!await VisibleAsync(tx, key, ct)) return new(ChatStoreOutcome.NotAuthorizedOrNotFound);
            var turn = await TurnAsync(tx, key, turnId, ct);
            if (turn is null) return new(ChatStoreOutcome.NotAuthorizedOrNotFound);
            var existing = await RoundAsync(tx, key, turnId, intent.RoundId, ct);
            if (existing is not null) return existing.Intent == intent ? new(ChatStoreOutcome.Replayed, turn, existing) : new(ChatStoreOutcome.Conflict);
            var last = await RoundAsync(tx, key, turnId, null, ct);
            var decision = ChatTurnPolicy.Reserve(turn, last, expectedVersion, await NowAsync(tx, ct), intent.InstanceId);
            if (decision != ChatStoreOutcome.Applied) return new(decision);
            const string admission = "chat_round_identity_admission";
            await tx.Transaction.SaveAsync(admission, ct);
            var identity = await ReserveIdentityAsync(tx, key, 4, intent.RoundId, turnId, ct);
            if (!await AuthorizedAsync(tx, key, ct)) { await tx.Transaction.RollbackAsync(admission, ct); return new(ChatStoreOutcome.NotAuthorizedOrNotFound); }
            if (identity != ChatStoreOutcome.Applied)
            { await tx.Transaction.RollbackAsync(admission, ct); return new(identity == ChatStoreOutcome.Replayed ? ChatStoreOutcome.Conflict : identity); }
            await tx.Transaction.ReleaseAsync(admission, ct);
            await AdvanceAsync(tx, key, turnId, expectedVersion, "round_count=round_count+1", ct);
            await using (var insert = Command(tx, """
                INSERT INTO weft.chat_rounds(organization_id,creator_id,conversation_id,turn_id,round_id,ordinal,fence,instance_id,exact_request)
                VALUES(@org,@creator,@conversation,@turn,@round,@ordinal,@fence,@instance,@request)
                """, key, turnId))
            {
                Add(insert, "round", NpgsqlDbType.Uuid, intent.RoundId); Add(insert, "ordinal", NpgsqlDbType.Integer, turn.RoundCount + 1);
                Add(insert, "fence", NpgsqlDbType.Uuid, intent.Fence); Add(insert, "instance", NpgsqlDbType.Uuid, intent.InstanceId);
                Add(insert, "request", NpgsqlDbType.Text, intent.ExactRequest); RequireOne(await insert.ExecuteNonQueryAsync(ct));
            }
            return new(ChatStoreOutcome.Applied, await RequireTurnAsync(tx, key, turnId, ct), await RoundAsync(tx, key, turnId, intent.RoundId, ct) ?? throw Invalid());
        }, new ChatRoundWriteResult(ChatStoreOutcome.NotAuthorizedOrNotFound), ct);
    }

    public Task<ChatTurnWriteResult> CompleteRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, Guid roundId, Guid fence, ChatRoundOutcome outcome, CancellationToken ct = default) =>
        CompleteRoundAsync(actor, key, turnId, expectedVersion, roundId, fence, new ChatRoundCompletion(outcome), ct);

    public Task<ChatTurnWriteResult> CompleteRoundAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, Guid roundId, Guid fence, ChatRoundCompletion completion, CancellationToken ct = default)
    {
        Validate(turnId, expectedVersion); ChatBounds.Id(roundId); ChatBounds.Id(fence); ArgumentNullException.ThrowIfNull(completion);
        var metadata = completion.Metadata is null ? null : ChatMetadataCodec.Terminal(completion.Metadata);
        ValidateMetadataRound(completion.Metadata, roundId);
        return ExecuteAsync(actor, key, true, true, async tx =>
        {
            if (!await VisibleAsync(tx, key, ct)) return Denied();
            var turn = await TurnAsync(tx, key, turnId, ct); var round = await RoundAsync(tx, key, turnId, roundId, ct);
            if (turn is null || round is null) return Denied();
            if (round.Intent.Fence == fence && round.Outcome == completion.Outcome && round.Metadata == completion.Metadata) return new(ChatStoreOutcome.Replayed, turn);
            if (round.Ordinal != turn.RoundCount) return new(ChatStoreOutcome.InvalidState);
            var decision = ChatTurnPolicy.CompleteRound(turn, round, expectedVersion, fence, completion.Outcome, await NowAsync(tx, ct));
            if (decision != ChatStoreOutcome.Applied) return new(decision);
            if (metadata is not null)
            {
                var bytes = Encoding.UTF8.GetByteCount(metadata);
                decision = ChatTurnPolicy.AppendBytes(turn, round, expectedVersion, fence, bytes, await NowAsync(tx, ct));
                if (decision != ChatStoreOutcome.Applied) return new(decision);
                await AdvanceEventAsync(tx, key, turnId, expectedVersion, bytes, ct);
                await InsertEventAsync(tx, key, turnId, turn.EventCount + 1, Guid.NewGuid(), ChatEventKind.RoundCompleted, "", roundId, fence, ct, metadata);
            }
            else await AdvanceAsync(tx, key, turnId, expectedVersion, null, ct);
            await using var update = Command(tx, $"UPDATE weft.chat_rounds SET outcome=@outcome,finished_at=statement_timestamp(),result_metadata=@metadata WHERE {TurnScope} AND round_id=@round AND fence=@fence AND outcome=1", key, turnId);
            Add(update, "round", NpgsqlDbType.Uuid, roundId); Add(update, "fence", NpgsqlDbType.Uuid, fence);
            Add(update, "outcome", NpgsqlDbType.Integer, (int)completion.Outcome); Add(update, "metadata", NpgsqlDbType.Text, metadata);
            RequireOne(await update.ExecuteNonQueryAsync(ct));
            return new(ChatStoreOutcome.Applied, await RequireTurnAsync(tx, key, turnId, ct));
        }, Denied(), ct);
    }

    public Task<ChatTurnWriteResult> AppendAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, Guid roundId, Guid fence, ChatEventData data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Kind == ChatEventKind.Usage && ChatMetadataCodec.ReadUsage(data.MetadataJson!).RoundId != roundId ||
            data.Kind == ChatEventKind.ProviderFailure && ChatMetadataCodec.ReadFailure(data.MetadataJson!).RoundId != roundId ||
            data.Kind == ChatEventKind.DataShared && key.Kind != ChatConversationKind.Workspace) throw ChatBounds.Invalid();
        return AppendCoreAsync(actor, key, turnId, expectedVersion, roundId, fence, data.EventId, data.Kind, data.Text, data.MetadataJson, null, ct);
    }

    public Task<ChatTurnWriteResult> AppendCitationAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, Guid roundId, Guid fence, ChatCitationEventData data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Citation.Facts is { } facts ? facts.Revision != key.Revision || key.Kind != ChatConversationKind.Revision
            : data.Citation.Workspace!.OrganizationId != key.OrganizationId || key.Kind != ChatConversationKind.Workspace) throw ChatBounds.Invalid();
        return AppendCoreAsync(actor, key, turnId, expectedVersion, roundId, fence, data.EventId, data.Kind, "", data.MetadataJson, data.Citation, ct);
    }

    private Task<ChatTurnWriteResult> AppendCoreAsync(ActorContext actor, ChatConversationKey key, Guid turnId, long expectedVersion,
        Guid roundId, Guid fence, Guid eventId, ChatEventKind kind, string text, string? metadata, ChatValidatedCitation? citation, CancellationToken ct)
    {
        Validate(turnId, expectedVersion); ChatBounds.Id(roundId); ChatBounds.Id(fence);
        var bytes = Encoding.UTF8.GetByteCount(text) + (metadata is null ? 0 : Encoding.UTF8.GetByteCount(metadata));
        return ExecuteAsync(actor, key, true, true, async tx =>
        {
            if (!await VisibleAsync(tx, key, ct)) return Denied();
            if (citation?.Facts is { } facts)
            {
                var conversation = await ConversationAsync(tx, key, ct);
                if (conversation is null) return Denied();
                var expected = facts.Target.Kind is ChatCitationKind.ValidationDiagnostic or ChatCitationKind.ValidationReport ? conversation.Validation : conversation.Projection;
                if (facts.Report != expected) return new(ChatStoreOutcome.Conflict);
            }
            var turn = await TurnAsync(tx, key, turnId, ct); if (turn is null) return Denied();
            await using (var replay = Command(tx, $"SELECT kind,private_text,round_id,dispatch_fence,metadata_json FROM weft.chat_events WHERE {TurnScope} AND event_id=@event", key, turnId))
            {
                Add(replay, "event", NpgsqlDbType.Uuid, eventId); await using var reader = await replay.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var same = reader.GetInt32(0) == (int)kind && reader.GetString(1) == text && !reader.IsDBNull(2) && reader.GetGuid(2) == roundId &&
                        !reader.IsDBNull(3) && reader.GetGuid(3) == fence && (reader.IsDBNull(4) ? metadata is null : reader.GetString(4) == metadata);
                    if (await reader.ReadAsync(ct)) throw Invalid();
                    return new(same ? ChatStoreOutcome.Replayed : ChatStoreOutcome.Conflict, same ? turn : null);
                }
            }
            var round = await RoundAsync(tx, key, turnId, roundId, ct);
            if (round is null || round.Ordinal != turn.RoundCount) return new(ChatStoreOutcome.InvalidState);
            var decision = ChatTurnPolicy.AppendBytes(turn, round, expectedVersion, fence, bytes, await NowAsync(tx, ct));
            if (decision != ChatStoreOutcome.Applied) return new(decision);
            await AdvanceEventAsync(tx, key, turnId, expectedVersion, bytes, ct);
            await InsertEventAsync(tx, key, turnId, turn.EventCount + 1, eventId, kind, text, roundId, fence, ct, metadata);
            return new(ChatStoreOutcome.Applied, await RequireTurnAsync(tx, key, turnId, ct));
        }, Denied(), ct);
    }

    public Task<ChatTurnWriteResult> FinishAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, ChatTurnPhase phase, CancellationToken ct = default) =>
        FinishWithMetadataAsync(actor, key, turnId, expectedVersion, phase, null, ct);

    public Task<ChatTurnWriteResult> FinishWithMetadataAsync(ActorContext actor, ChatConversationKey key, Guid turnId,
        long expectedVersion, ChatTurnPhase phase, ChatTerminalMetadata? metadata, CancellationToken ct = default)
    {
        Validate(turnId, expectedVersion);
        if (phase is not (ChatTurnPhase.Completed or ChatTurnPhase.Failed or ChatTurnPhase.Cancelled or ChatTurnPhase.Interrupted)) throw ChatBounds.Invalid();
        return ExecuteAsync(actor, key, true, true, async tx =>
        {
            var conversation = await ConversationAsync(tx, key, ct);
            if (conversation is null) return Denied();
            var turn = await TurnAsync(tx, key, turnId, ct); if (turn is null) return Denied();
            var last = await RoundAsync(tx, key, turnId, null, ct);
            var decision = ChatTurnPolicy.Finish(turn, last, expectedVersion, phase, await NowAsync(tx, ct));
            if (decision != ChatStoreOutcome.Applied) return new(decision);
            var terminal = metadata ?? last?.Metadata;
            if (metadata is not null && last?.Metadata is { } committed && metadata != committed) return new(ChatStoreOutcome.Conflict);
            if (terminal is not null)
            {
                if (last is null || phase == ChatTurnPhase.Completed && terminal.Failure is not null) throw ChatBounds.Invalid();
                ValidateMetadataRound(terminal, last.Intent.RoundId);
            }
            var encoded = terminal is null ? null : ChatMetadataCodec.Terminal(terminal);
            if (encoded is not null && Encoding.UTF8.GetByteCount(encoded) > 2048) throw ChatBounds.Invalid();
            await using (var update = Command(tx, $"UPDATE weft.chat_turns SET version=version+1,phase=@phase,finished_at=statement_timestamp(),event_count=event_count+1 WHERE {TurnScope} AND version=@version AND phase=1", key, turnId))
            {
                Add(update, "phase", NpgsqlDbType.Integer, (int)phase); Add(update, "version", NpgsqlDbType.Bigint, expectedVersion);
                RequireOne(await update.ExecuteNonQueryAsync(ct));
            }
            // Terminal accounting has its own fixed 2 KiB reserve and cannot be stranded by content-byte exhaustion.
            await InsertEventAsync(tx, key, turnId, turn.EventCount + 1, Guid.NewGuid(), (ChatEventKind)((int)phase + 3), "", null, null, ct, encoded);
            return new(ChatStoreOutcome.Applied, conversation.RetainUntilUtc > await NowAsync(tx, ct) ? await RequireTurnAsync(tx, key, turnId, ct) : null);
        }, Denied(), ct);
    }

    private static async Task AdvanceEventAsync(PostgresTransaction tx, ChatConversationKey key, Guid turn, long version, int bytes, CancellationToken ct)
    {
        await using var command = Command(tx, $"UPDATE weft.chat_turns SET version=version+1,event_count=event_count+1,event_bytes=event_bytes+@bytes WHERE {TurnScope} AND version=@version AND phase=1", key, turn);
        Add(command, "version", NpgsqlDbType.Bigint, version); Add(command, "bytes", NpgsqlDbType.Integer, bytes); RequireOne(await command.ExecuteNonQueryAsync(ct));
    }
    private static async Task AdvanceAsync(PostgresTransaction tx, ChatConversationKey key, Guid turn, long version, string? extraSet, CancellationToken ct)
    {
        await using var command = Command(tx, $"UPDATE weft.chat_turns SET version=version+1{(extraSet is null ? "" : "," + extraSet)} WHERE {TurnScope} AND version=@version AND phase=1", key, turn);
        Add(command, "version", NpgsqlDbType.Bigint, version); RequireOne(await command.ExecuteNonQueryAsync(ct));
    }
    private static void ValidateMetadataRound(ChatTerminalMetadata? metadata, Guid round)
    {
        if (metadata?.Usage is { } usage && usage.RoundId != round || metadata?.Failure is { } failure && failure.RoundId != round) throw ChatBounds.Invalid();
    }
    private static void Validate(Guid turn, long version) { ChatBounds.Id(turn); if (version < 1) throw ChatBounds.Invalid(); }
}
