using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresChatStore : IChatConversationReader
{
    private const string ConversationFacts = """
        (SELECT left(t.original_request,160) FROM ONLY weft.chat_turns t WHERE t.organization_id=c.organization_id AND t.creator_id=c.creator_id
            AND t.conversation_id=c.conversation_id ORDER BY t.created_at,t.turn_id LIMIT 1) AS title,
        (SELECT count(*)::integer FROM ONLY weft.chat_turns t WHERE t.organization_id=c.organization_id AND t.creator_id=c.creator_id
            AND t.conversation_id=c.conversation_id) AS turn_count,
        (SELECT max(t.created_at) FROM ONLY weft.chat_turns t WHERE t.organization_id=c.organization_id AND t.creator_id=c.creator_id
            AND t.conversation_id=c.conversation_id) AS last_turn_at
        """;

    public Task<ChatConversationPage> ListConversationsAsync(ActorContext actor, RevisionScope revision, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(revision);
        if (limit is < 1 or > 100) throw ChatBounds.Invalid();
        return ListAsync(actor, revision.OrganizationId, async tx =>
        {
            await using var command = tx.CreateCommand($"""
                SELECT c.*, {ConversationFacts} FROM ONLY weft.chat_conversations c
                WHERE c.organization_id=@org AND c.creator_id=@creator AND c.project_id=@project AND c.mapping_id=@mapping AND c.revision_id=@revision
                    AND c.retain_until>statement_timestamp() AND weft.chat_authorized(c.organization_id,c.project_id,c.creator_id)
                ORDER BY c.created_at DESC,c.conversation_id DESC LIMIT @limit
                """);
            Add(command, "org", NpgsqlDbType.Uuid, revision.OrganizationId); Add(command, "creator", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
            Add(command, "project", NpgsqlDbType.Uuid, revision.ProjectId); Add(command, "mapping", NpgsqlDbType.Uuid, revision.MappingId);
            Add(command, "revision", NpgsqlDbType.Uuid, revision.RevisionId); Add(command, "limit", NpgsqlDbType.Integer, limit + 1);
            var result = new List<ChatConversationSummary>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = new ChatConversationKey(revision, actor.PrincipalId, Id(reader, "conversation_id"));
                if (Id(reader, "creator_id") != actor.PrincipalId.Value) throw Invalid();
                result.Add(Summary(reader, key));
            }
            var more = result.Count > limit;
            if (more) result.RemoveAt(limit);
            return new ChatConversationPage(result.AsReadOnly(), more);
        }, ct);
    }

    public Task<ChatConversationSummary?> GetConversationAsync(ActorContext actor, ChatConversationKey key, CancellationToken ct = default) =>
        ExecuteAsync<ChatConversationSummary?>(actor, key, false, false, async tx =>
        {
            await using var command = Command(tx, $"""
                SELECT c.*, {ConversationFacts} FROM ONLY weft.chat_conversations c
                WHERE c.organization_id=@org AND c.creator_id=@creator AND c.conversation_id=@conversation AND c.project_id=@project
                    AND c.mapping_id=@mapping AND c.revision_id=@revision AND c.retain_until>statement_timestamp()
                    AND weft.chat_authorized(c.organization_id,c.project_id,c.creator_id)
                """, key);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var summary = Summary(reader, key);
            if (await reader.ReadAsync(ct)) throw Invalid();
            return summary;
        }, null, ct);

    public Task<ChatTurnPage?> ListTurnsAsync(ActorContext actor, ChatConversationKey key, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw ChatBounds.Invalid();
        return ExecuteAsync<ChatTurnPage?>(actor, key, false, false, async tx =>
        {
            if (!await VisibleAsync(tx, key, ct)) return null;
            var ids = new List<Guid>();
            await using (var command = Command(tx, $"SELECT turn_id FROM ONLY weft.chat_turns WHERE {Scope} AND {AuthorizedParent} ORDER BY created_at DESC,turn_id DESC LIMIT @limit", key))
            {
                Add(command, "visibleOnly", NpgsqlDbType.Boolean, true); Add(command, "limit", NpgsqlDbType.Integer, limit + 1);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
            }
            var earlier = ids.Count > limit;
            if (earlier) ids.RemoveAt(limit);
            var turns = new List<ChatTurnFacts>(ids.Count);
            for (var i = ids.Count - 1; i >= 0; i--)
                turns.Add(await TurnAsync(tx, key, ids[i], ct, visibleOnly: true) ?? throw Invalid());
            return new ChatTurnPage(turns.AsReadOnly(), earlier);
        }, null, ct);
    }

    private static ChatConversationSummary Summary(NpgsqlDataReader reader, ChatConversationKey key)
    {
        if (Id(reader, "organization_id") != key.Revision.OrganizationId || Id(reader, "project_id") != key.Revision.ProjectId ||
            Id(reader, "mapping_id") != key.Revision.MappingId || Id(reader, "revision_id") != key.Revision.RevisionId ||
            Id(reader, "conversation_id") != key.ConversationId) throw Invalid();
        var conversation = new ChatConversationIntent(key,
            new(Id(reader, "projection_entry_id"), Id(reader, "projection_id"), Digest(reader, "projection_key"), Digest(reader, "projection_digest")),
            new(Id(reader, "validation_entry_id"), Id(reader, "validation_id"), Digest(reader, "validation_key"), Digest(reader, "validation_digest")),
            Instant(reader, "retain_until"));
        var title = reader.IsDBNull(reader.GetOrdinal("title")) ? null : Text(reader, "title");
        var last = reader.IsDBNull(reader.GetOrdinal("last_turn_at")) ? (DateTimeOffset?)null : Instant(reader, "last_turn_at");
        return new(conversation, Instant(reader, "created_at"), title, Int(reader, "turn_count"), last);
    }

    private async Task<T> ListAsync<T>(ActorContext actor, Guid organizationId, Func<PostgresTransaction, Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var tx = await _database.BeginAsync(actor, organizationId, false, ct);
            var result = await operation(tx);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (NpgsqlException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (NpgsqlException) { throw new InvalidOperationException("Private chat persistence failed."); }
        catch (ArgumentException) { throw new InvalidOperationException("Private chat stored facts are invalid."); }
        catch (InvalidCastException) { throw new InvalidOperationException("Private chat stored facts are invalid."); }
    }
}
