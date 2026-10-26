using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresChatStore
{
    private const string AuthorizedParent = "EXISTS(SELECT 1 FROM ONLY weft.chat_conversations c WHERE c.organization_id=@org AND c.creator_id=@creator AND c.conversation_id=@conversation AND c.conversation_kind=@conversationKind AND c.project_id IS NOT DISTINCT FROM @project AND c.mapping_id IS NOT DISTINCT FROM @mapping AND c.revision_id IS NOT DISTINCT FROM @revision AND (NOT @visibleOnly OR c.retain_until>statement_timestamp()) AND weft.chat_authorized(c.organization_id,c.project_id,c.creator_id))";
    private static async Task<ChatConversationIntent?> ConversationAsync(PostgresTransaction tx, ChatConversationKey key, CancellationToken ct)
    {
        await using var command = Command(tx, $"SELECT * FROM ONLY weft.chat_conversations WHERE {Scope} AND {ExactScope} AND weft.chat_authorized(organization_id,project_id,creator_id)", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var result = Conversation(reader, key);
        if (await reader.ReadAsync(ct)) throw Invalid();
        return result;
    }

    /// <summary>The stored conversation row as an intent of its own kind; any other column shape is invalid.</summary>
    private static ChatConversationIntent Conversation(NpgsqlDataReader reader, ChatConversationKey key)
    {
        if (Id(reader, "organization_id") != key.OrganizationId || Id(reader, "creator_id") != key.Creator.Value ||
            Id(reader, "conversation_id") != key.ConversationId || Int(reader, "conversation_kind") != (int)key.Kind) throw Invalid();
        if (key.Revision is { } revision)
        {
            if (Id(reader, "project_id") != revision.ProjectId || Id(reader, "mapping_id") != revision.MappingId ||
                Id(reader, "revision_id") != revision.RevisionId || !Null(reader, "focus_project_id") || !Null(reader, "focus_mapping_id") ||
                !Null(reader, "focus_revision_id")) throw Invalid();
            return new(key,
                new(Id(reader, "projection_entry_id"), Id(reader, "projection_id"), Digest(reader, "projection_key"), Digest(reader, "projection_digest")),
                new(Id(reader, "validation_entry_id"), Id(reader, "validation_id"), Digest(reader, "validation_key"), Digest(reader, "validation_digest")),
                Instant(reader, "retain_until"));
        }
        foreach (var column in new[] { "project_id", "mapping_id", "revision_id", "projection_entry_id", "projection_id", "projection_key",
            "projection_digest", "validation_entry_id", "validation_id", "validation_key", "validation_digest" })
            if (!Null(reader, column)) throw Invalid();
        ChatConversationFocus? focus = Null(reader, "focus_mapping_id") ? null
            : new(new(key.OrganizationId, Id(reader, "focus_project_id"), Id(reader, "focus_mapping_id")),
                Null(reader, "focus_revision_id") ? null : Id(reader, "focus_revision_id"));
        if (focus is null && (!Null(reader, "focus_project_id") || !Null(reader, "focus_revision_id"))) throw Invalid();
        return ChatConversationIntent.ForWorkspace(key, focus, Instant(reader, "retain_until"));
    }

    /// <summary>The complete key a stored row belongs to, for listings that do not yet know its kind.</summary>
    private static ChatConversationKey Key(NpgsqlDataReader reader, PrincipalId creator) =>
        (ChatConversationKind)Int(reader, "conversation_kind") switch
        {
            ChatConversationKind.Revision => new(new(Id(reader, "organization_id"), Id(reader, "project_id"), Id(reader, "mapping_id"),
                Id(reader, "revision_id")), creator, Id(reader, "conversation_id")),
            ChatConversationKind.Workspace => ChatConversationKey.ForWorkspace(Id(reader, "organization_id"), creator, Id(reader, "conversation_id")),
            _ => throw Invalid()
        };

    private static async Task<ChatTurnFacts?> TurnAsync(PostgresTransaction tx, ChatConversationKey key, Guid turn, CancellationToken ct, bool visibleOnly = false)
    {
        await using var command = Command(tx, $"SELECT * FROM ONLY weft.chat_turns WHERE {TurnScope} AND {AuthorizedParent}", key, turn);
        Add(command, "visibleOnly", NpgsqlDbType.Boolean, visibleOnly);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var original = ChatOriginalEnvelope.Restore(Text(reader, "original_envelope"));
        if (original.RequestId != Id(reader, "request_id") || original.UserText != Text(reader, "original_request")) throw Invalid();
        var canonical = Text(reader, "canonical_envelope"); var fingerprint = Digest(reader, "envelope_fingerprint");
        var intent = new ChatTurnIntent(Id(reader, "turn_id"), original,
            new(Text(reader, "profile_id"), Int(reader, "profile_version"), Text(reader, "provider_id"), Text(reader, "model_id"),
                Text(reader, "metadata_policy_id"), Int(reader, "metadata_policy_version"), Digest(reader, "profile_fingerprint")),
            new(Int(reader, "max_events"), Int(reader, "max_event_bytes"), Int(reader, "max_rounds")), Instant(reader, "deadline"));
        var result = new ChatTurnFacts(intent, (ChatTurnPhase)Int(reader, "phase"), reader.GetInt64(reader.GetOrdinal("version")),
            Int(reader, "event_count"), Int(reader, "event_bytes"), Int(reader, "round_count"), Instant(reader, "created_at"),
            reader.IsDBNull(reader.GetOrdinal("finished_at")) ? null : Instant(reader, "finished_at"));
        if (intent.TurnId != turn || await reader.ReadAsync(ct)) throw Invalid();
        await reader.DisposeAsync();
        var conversation = await ConversationAsync(tx, key, ct);
        if (conversation is null) return null;
        if (canonical != ChatCanonicalEnvelope.Turn(conversation, intent) || fingerprint != ChatCanonicalEnvelope.Fingerprint(canonical)) throw Invalid();
        return result;
    }
    private static async Task<ChatTurnFacts> RequireTurnAsync(PostgresTransaction tx, ChatConversationKey key, Guid turn, CancellationToken ct) =>
        await TurnAsync(tx, key, turn, ct) ?? throw Invalid();

    private static async Task<ChatRoundFacts?> RoundAsync(PostgresTransaction tx, ChatConversationKey key, Guid turn, Guid? round, CancellationToken ct, bool visibleOnly = false)
    {
        await using var command = Command(tx, $"SELECT * FROM ONLY weft.chat_rounds WHERE {TurnScope} AND {AuthorizedParent}" +
            (round is null ? " ORDER BY ordinal DESC LIMIT 1" : " AND round_id=@round"), key, turn);
        Add(command, "visibleOnly", NpgsqlDbType.Boolean, visibleOnly);
        if (round is { } id) Add(command, "round", NpgsqlDbType.Uuid, id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var result = new ChatRoundFacts(new(Id(reader, "round_id"), Id(reader, "fence"), Id(reader, "instance_id"), Text(reader, "exact_request")),
            Int(reader, "ordinal"), (ChatRoundOutcome)Int(reader, "outcome"), reader.IsDBNull(reader.GetOrdinal("result_metadata")) ? null : ChatMetadataCodec.ReadTerminal(Text(reader, "result_metadata")));
        if (round is { } expected && result.Intent.RoundId != expected || await reader.ReadAsync(ct)) throw Invalid();
        return result;
    }

    private static async Task InsertEventAsync(PostgresTransaction tx, ChatConversationKey key, Guid turn, int sequence,
        Guid eventId, ChatEventKind kind, string text, Guid? round, Guid? fence, CancellationToken ct, string? metadata = null)
    {
        await using var insert = Command(tx, """
            INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence,metadata_json)
            VALUES(@org,@creator,@conversation,@turn,@sequence,@event,@kind,@text,@round,@fence,@metadata)
            """, key, turn);
        Add(insert, "sequence", NpgsqlDbType.Integer, sequence); Add(insert, "event", NpgsqlDbType.Uuid, eventId);
        Add(insert, "kind", NpgsqlDbType.Integer, (int)kind); Add(insert, "text", NpgsqlDbType.Text, text);
        Add(insert, "round", NpgsqlDbType.Uuid, round); Add(insert, "fence", NpgsqlDbType.Uuid, fence);
        Add(insert, "metadata", NpgsqlDbType.Text, metadata);
        RequireOne(await insert.ExecuteNonQueryAsync(ct));
    }

    private static Guid Id(NpgsqlDataReader reader, string column) => reader.GetGuid(reader.GetOrdinal(column));
    private static bool Null(NpgsqlDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column));
    private static int Int(NpgsqlDataReader reader, string column) => reader.GetInt32(reader.GetOrdinal(column));
    private static string Text(NpgsqlDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));
    private static Sha256Digest Digest(NpgsqlDataReader reader, string column) => Sha256Digest.Parse(Text(reader, column));
    private static DateTimeOffset Instant(NpgsqlDataReader reader, string column) => reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(column));
}
