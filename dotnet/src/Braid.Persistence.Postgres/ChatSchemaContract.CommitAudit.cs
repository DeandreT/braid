using Npgsql;

namespace Braid.Persistence.Postgres;

internal static partial class ChatSchemaContract
{
    internal static string MappingCommitTriggerBody => Body("audit_chat_mapping_write_commit");
    /// <summary>Catalog-only admission before any existing source guard omits the one additive commit trigger.
    /// No chat data, new grants, generic trigger exception or target-captured fingerprint is accepted.</summary>
    /// <returns>Whether the chat mapping-write supplement is installed. It is absent only before migration 0039, and
    /// then nothing is validated; any owned object makes it installed, so a partial supplement is refused in full.</returns>
    internal static async Task<bool> ValidateMappingCommitAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        _ = WriteJournalSource.Value; _ = EditProposalSource.Value;
        if (!await MappingWritesInstalledAsync(connection, transaction, ct)) return false;
        foreach (var table in new[] { WriteIntents, WriteCommits, EditProposals, EditApprovals })
        {
            await ValidateRelationAsync(connection, transaction, table, ct);
            await ValidateColumnsAsync(connection, transaction, table, ct);
            await ValidateConstraintsAsync(connection, transaction, table, ct);
            await ValidateIndexesAsync(connection, transaction, table, ct);
            await ValidatePolicyAsync(connection, transaction, table, ct);
        }
        string[] tables = [WriteIntents.Name, WriteCommits.Name, EditProposals.Name, EditApprovals.Name];
        await ValidateFunctionsAsync(connection, transaction, ct, Functions.Where(f => f.Name is "audit_chat_mapping_write_commit" or "guard_chat_mapping_write_audit"));
        await ValidateTriggersAsync(connection, transaction, ct, tables);
        await ValidateGrantsAsync(connection, transaction, ct, tables);
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=1 AND coalesce(bool_and(c.relname='revision_ingestions' AND n.nspname='braid'
                AND p.oid='braid.audit_chat_mapping_write_commit()'::regprocedure AND t.tgtype=17 AND t.tgenabled='O'
                AND NOT t.tgisinternal AND NOT t.tgdeferrable AND NOT t.tginitdeferred AND t.tgconstraint=0
                AND t.tgparentid=0 AND t.tgconstrrelid=0 AND t.tgnargs=0 AND octet_length(t.tgargs)=0
                AND t.tgattr::text='' AND t.tgqual IS NULL AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL),false)
            FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid
            WHERE t.tgname='chat_mapping_write_commit'
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(ct) is not true) throw Refusal();
        return true;
    }

    // Every object 0039, 0040 and 0043 create under its own name. Any one of them is a (possibly partial)
    // installation, never an old schema; only an older prefix with none of them skips this supplement.
    internal static readonly string[] MappingWriteTables = ["chat_mapping_write_audit", "chat_mapping_write_intents",
        "chat_mapping_write_content", "chat_mapping_write_commits", "chat_edit_proposals", "chat_edit_content", "chat_edit_approvals"];
    internal static readonly string[] MappingWriteFunctions = ["audit_chat_mapping_write", "audit_chat_mapping_write_commit",
        "guard_chat_mapping_write_audit", "guard_chat_mapping_write_content", "chat_mapping_write_prepare", "chat_mapping_write_read",
        "chat_edit_create", "chat_edit_read", "chat_edit_list", "chat_edit_approve"];
    internal static readonly string[] MappingWriteTriggers = ["chat_mapping_write_audit", "chat_mapping_write_commit"];

    internal static async Task<bool> MappingWritesInstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='braid' AND c.relname=ANY($1::text[]))
                OR EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='braid' AND p.proname=ANY($2::text[]))
                OR EXISTS(SELECT 1 FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
                    JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='braid' AND t.tgname=ANY($3::text[]))
            """, connection, transaction);
        command.Parameters.AddWithValue(MappingWriteTables);
        command.Parameters.AddWithValue(MappingWriteFunctions);
        command.Parameters.AddWithValue(MappingWriteTriggers);
        return await command.ExecuteScalarAsync(ct) is true;
    }
}
