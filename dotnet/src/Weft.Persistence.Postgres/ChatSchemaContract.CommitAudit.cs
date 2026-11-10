using Npgsql;

namespace Weft.Persistence.Postgres;

internal static partial class ChatSchemaContract
{
    internal static string MappingCommitTriggerBody => Body("audit_chat_mapping_write_commit");
    /// <summary>Catalog-only admission before any existing source guard omits the one additive commit trigger.
    /// No chat data, new grants, generic trigger exception or target-captured fingerprint is accepted.</summary>
    internal static async Task ValidateMappingCommitAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        _ = WriteJournalSource.Value;
        foreach (var table in new[] { WriteIntents, WriteCommits })
        {
            await ValidateRelationAsync(connection, transaction, table, ct);
            await ValidateColumnsAsync(connection, transaction, table, ct);
            await ValidateConstraintsAsync(connection, transaction, table, ct);
            await ValidateIndexesAsync(connection, transaction, table, ct);
            await ValidatePolicyAsync(connection, transaction, table, ct);
        }
        string[] tables = [WriteIntents.Name, WriteCommits.Name];
        await ValidateFunctionsAsync(connection, transaction, ct, Functions.Where(f => f.Name is "audit_chat_mapping_write_commit" or "guard_chat_mapping_write_audit"));
        await ValidateTriggersAsync(connection, transaction, ct, tables);
        await ValidateGrantsAsync(connection, transaction, ct, tables);
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=1 AND coalesce(bool_and(c.relname='revision_ingestions' AND n.nspname='weft'
                AND p.oid='weft.audit_chat_mapping_write_commit()'::regprocedure AND t.tgtype=17 AND t.tgenabled='O'
                AND NOT t.tgisinternal AND NOT t.tgdeferrable AND NOT t.tginitdeferred AND t.tgconstraint=0
                AND t.tgparentid=0 AND t.tgconstrrelid=0 AND t.tgnargs=0 AND octet_length(t.tgargs)=0
                AND t.tgattr::text='' AND t.tgqual IS NULL AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL),false)
            FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid
            WHERE t.tgname='chat_mapping_write_commit'
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(ct) is not true) throw Refusal();
    }
}
