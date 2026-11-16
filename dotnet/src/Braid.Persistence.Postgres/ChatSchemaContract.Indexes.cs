using Npgsql;

namespace Braid.Persistence.Postgres;

internal static partial class ChatSchemaContract
{
    private sealed record Index(bool Primary, string[] Columns, string? Predicate = null, bool Unique = true);
    private static async Task ValidateIndexesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Table table, CancellationToken ct)
    {
        var expected = table.Constraints.Where(c => c.Kind is 'p' or 'u').ToDictionary(c => c.Name, c => new Index(c.Kind == 'p', c.Columns), StringComparer.Ordinal);
        if (table.Name == "chat_turns") expected.Add("chat_one_active_turn", new(false, ["organization_id", "creator_id", "conversation_id"], "(phase = 1)"));
        if (table.Name == "chat_identities") expected.Add("chat_identities_conversation", new(false, ["organization_id", "creator_id", "conversation_id"], Unique: false));
        if (table.Name == "chat_conversations") expected.Add("chat_conversations_recent", new(false, ["organization_id", "creator_id", "created_at", "conversation_id"], Unique: false));
        await using var command = new NpgsqlCommand("""
            SELECT r.relname,i.indisprimary,pg_catalog.pg_get_expr(i.indpred,i.indrelid,false),
                ARRAY(SELECT a.attname::text FROM unnest(i.indkey) WITH ORDINALITY k(attnum,ord)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum ORDER BY k.ord),
                NOT i.indisexclusion AND i.indimmediate AND i.indisvalid AND i.indisready AND i.indislive
                AND NOT i.indnullsnotdistinct AND NOT i.indisreplident AND i.indnkeyatts=i.indnatts
                AND i.indexprs IS NULL AND m.amname='btree' AND o.rolname='braid_migrator' AND n.nspname='braid'
                AND r.reloptions IS NULL AND r.relkind='i' AND r.relpersistence='p' AND NOT r.relispartition
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits h WHERE h.inhrelid=r.oid OR h.inhparent=r.oid)
                AND NOT EXISTS(SELECT 1 FROM unnest(i.indoption) v WHERE v<>0)
                AND NOT EXISTS(SELECT 1 FROM unnest(i.indkey,i.indcollation) k(attnum,collation_oid)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum WHERE a.attcollation<>k.collation_oid)
                AND NOT EXISTS(SELECT 1 FROM unnest(i.indclass) WITH ORDINALITY c(opclass,ord)
                    JOIN pg_catalog.pg_opclass op ON op.oid=c.opclass JOIN pg_catalog.pg_namespace ns ON ns.oid=op.opcnamespace
                    JOIN unnest(i.indkey) WITH ORDINALITY k(attnum,ord) ON k.ord=c.ord
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                    WHERE ns.nspname<>'pg_catalog' OR NOT op.opcdefault OR op.opcintype<>a.atttypid OR op.opcmethod<>m.oid),
                i.indnkeyatts::integer,c.conname,c.contype::text,i.indisunique
            FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class r ON r.oid=i.indexrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace JOIN pg_catalog.pg_roles o ON o.oid=r.relowner
            JOIN pg_catalog.pg_am m ON m.oid=r.relam LEFT JOIN pg_catalog.pg_constraint c ON c.conindid=r.oid AND c.conrelid=i.indrelid AND c.contype IN ('p','u','x')
            WHERE i.indrelid=('braid.' || $1)::regclass
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!expected.Remove(reader.GetString(0), out var index) || reader.GetBoolean(1) != index.Primary ||
                (reader.IsDBNull(2) ? null : reader.GetString(2)) != index.Predicate || !reader.GetFieldValue<string[]>(3).SequenceEqual(index.Columns) ||
                !reader.GetBoolean(4) || reader.GetInt32(5) != index.Columns.Length || reader.GetBoolean(8) != index.Unique ||
                (index.Predicate is not null || !index.Unique ? !reader.IsDBNull(6) : reader.IsDBNull(6) || reader.GetString(6) != reader.GetString(0) || reader.GetString(7) != (index.Primary ? "p" : "u"))) throw Refusal();
        }
        if (expected.Count != 0) throw Refusal();
    }
}
