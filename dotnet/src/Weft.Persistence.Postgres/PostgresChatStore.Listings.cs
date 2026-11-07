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
                WHERE c.organization_id=@org AND c.creator_id=@creator AND c.conversation_kind=1 AND c.project_id=@project AND c.mapping_id=@mapping
                    AND c.revision_id=@revision AND c.retain_until>statement_timestamp() AND weft.chat_authorized(c.organization_id,c.project_id,c.creator_id)
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
                WHERE c.organization_id=@org AND c.creator_id=@creator AND c.conversation_id=@conversation AND c.conversation_kind=@conversationKind
                    AND c.project_id IS NOT DISTINCT FROM @project AND c.mapping_id IS NOT DISTINCT FROM @mapping
                    AND c.revision_id IS NOT DISTINCT FROM @revision AND c.retain_until>statement_timestamp()
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

    public Task<ChatConversationPage> ListOrganizationConversationsAsync(ActorContext actor, Guid organizationId, RevisionScope? revision,
        int limit, Guid? afterConversationId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ChatBounds.Id(organizationId);
        if (limit is < 1 or > 100 || revision is not null && revision.OrganizationId != organizationId) throw ChatBounds.Invalid();
        if (afterConversationId is { } after) ChatBounds.Id(after);
        return ListAsync(actor, organizationId, async tx =>
        {
            // A cursor names a conversation of this same listing; an unknown or foreign cursor yields an empty page.
            await using var command = tx.CreateCommand($"""
                WITH visible AS (SELECT c.* FROM ONLY weft.chat_conversations c
                    WHERE c.organization_id=@org AND c.creator_id=@creator AND c.retain_until>statement_timestamp()
                        AND weft.chat_authorized(c.organization_id,c.project_id,c.creator_id)
                        AND (@revision::uuid IS NULL OR (c.conversation_kind=1 AND c.project_id=@project AND c.mapping_id=@mapping AND c.revision_id=@revision)
                            OR (c.conversation_kind=2 AND c.focus_project_id=@project AND c.focus_mapping_id=@mapping AND c.focus_revision_id=@revision)))
                SELECT c.*, {ConversationFacts} FROM visible c
                WHERE @after::uuid IS NULL OR (c.created_at,c.conversation_id)<(SELECT x.created_at,x.conversation_id FROM visible x WHERE x.conversation_id=@after)
                ORDER BY c.created_at DESC,c.conversation_id DESC LIMIT @limit
                """);
            Add(command, "org", NpgsqlDbType.Uuid, organizationId); Add(command, "creator", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
            Add(command, "project", NpgsqlDbType.Uuid, revision?.ProjectId); Add(command, "mapping", NpgsqlDbType.Uuid, revision?.MappingId);
            Add(command, "revision", NpgsqlDbType.Uuid, revision?.RevisionId); Add(command, "after", NpgsqlDbType.Uuid, afterConversationId);
            Add(command, "limit", NpgsqlDbType.Integer, limit + 1);
            var result = new List<ChatConversationSummary>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (Id(reader, "creator_id") != actor.PrincipalId.Value) throw Invalid();
                result.Add(Summary(reader, Key(reader, actor.PrincipalId)));
            }
            var more = result.Count > limit;
            if (more) result.RemoveAt(limit);
            return new ChatConversationPage(result.AsReadOnly(), more);
        }, ct);
    }

    public Task<ChatConversationSummary?> FindConversationAsync(ActorContext actor, Guid organizationId, Guid conversationId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ChatBounds.Id(organizationId); ChatBounds.Id(conversationId);
        return ListAsync<ChatConversationSummary?>(actor, organizationId, async tx =>
        {
            await using var command = tx.CreateCommand($"""
                SELECT c.*, {ConversationFacts} FROM ONLY weft.chat_conversations c
                WHERE c.organization_id=@org AND c.creator_id=@creator AND c.conversation_id=@conversation AND c.retain_until>statement_timestamp()
                    AND weft.chat_authorized(c.organization_id,c.project_id,c.creator_id)
                """);
            Add(command, "org", NpgsqlDbType.Uuid, organizationId); Add(command, "creator", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
            Add(command, "conversation", NpgsqlDbType.Uuid, conversationId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var summary = Summary(reader, Key(reader, actor.PrincipalId));
            if (await reader.ReadAsync(ct)) throw Invalid();
            return summary;
        }, ct);
    }

    private static ChatConversationSummary Summary(NpgsqlDataReader reader, ChatConversationKey key)
    {
        var conversation = Conversation(reader, key);
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
