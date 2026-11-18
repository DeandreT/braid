using Npgsql;

namespace Braid.Persistence.Postgres;

internal static partial class ChatSchemaContract
{
    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ChatDatabaseProfile profile, CancellationToken ct)
    {
        _ = Source.Value; _ = WorkspaceSource.Value; _ = MappingWriteSource.Value; _ = WriteJournalSource.Value; _ = EditProposalSource.Value;
        var lifecycle = await LifecycleCatalogAdmission.ValidateIfInstalledAsync(connection, transaction, ct);
        // Reader also needs the tombstone relation protected without a tombstone
        // SELECT/listing grant. All profiles receive only this bounded lock capability.
        {
            // Validate exact source/owner/ACL before invoking this no-content lock
            // capability, then revalidate the complete schema with its locks held.
            if (!lifecycle) await ValidateFunctionsAsync(connection, transaction, ct);
            await using var protect = new NpgsqlCommand("SELECT braid.chat_schema_lock()", connection, transaction);
            await protect.ExecuteNonQueryAsync(ct);
        }
        // Allowed-column no-row SELECTs take transaction-held ACCESS SHARE locks
        // without table-wide SELECT, UPDATE on authority, or locks on membership rows.
        var locks = new List<(string Table, string Column)>
        {
            ("schema_migrations", "version"), ("principals", "id"), ("organization_memberships", "organization_id"),
            ("project_memberships", "organization_id"), ("chat_conversations", "conversation_id"),
            ("chat_turns", "turn_id"), ("chat_rounds", "round_id"), ("chat_events", "event_id")
        };
        if (profile == ChatDatabaseProfile.Writer)
        {
            locks.Add(("chat_identities", "identity_id"));
            locks.Add(("revision_derivations", "cache_entry_id"));
            locks.Add(("revision_derivation_attempts", "derivation_id"));
        }
        foreach (var (table, column) in profile is ChatDatabaseProfile.Lifecycle or ChatDatabaseProfile.Approver ? new List<(string Table, string Column)>() : locks)
        {
            await using var protect = new NpgsqlCommand($"SELECT {column} FROM ONLY braid.{table} WHERE false", connection, transaction);
            await protect.ExecuteNonQueryAsync(ct);
        }
        await ValidateRelationAsync(connection, transaction, History, ct);
        await ValidateColumnsAsync(connection, transaction, History, ct);
        await ValidateConstraintsAsync(connection, transaction, History, ct);
        await ValidateIndexesAsync(connection, transaction, History, ct);
        await using (var journal = new NpgsqlCommand("""
            SELECT NOT EXISTS(SELECT 1 FROM pg_catalog.pg_policy WHERE polrelid='braid.schema_migrations'::regclass)
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_trigger WHERE tgrelid='braid.schema_migrations'::regclass)
            """, connection, transaction))
            if (await journal.ExecuteScalarAsync(ct) is not true) throw Refusal();
        await ValidateHistoryAsync(connection, transaction, ct);
        if (lifecycle)
        {
            // Re-admit the exact upgraded schema after acquiring the source locks.
            await LifecycleCatalogAdmission.ValidateAsync(connection, transaction, ct);
            return;
        }
        // Reuse immutable old descriptors/admission, never broaden old guards/roles.
        // It is catalog-only and grants this credential no maintenance authority.
        await SupplementalMaintenanceSchemaAdmission.ValidateAsync(connection, transaction, ct);
        foreach (var table in NewTables.Concat(SourceTables()))
        {
            await ValidateRelationAsync(connection, transaction, table, ct);
            await ValidateColumnsAsync(connection, transaction, table, ct);
            await ValidateConstraintsAsync(connection, transaction, table, ct);
            if (ChatTables.Contains(table.Name, StringComparer.Ordinal))
            {
                await ValidatePolicyAsync(connection, transaction, table, ct);
                await ValidateIndexesAsync(connection, transaction, table, ct);
            }
        }
        foreach (var name in new[] { "mappings", "mapping_revisions", "revision_ingestions" })
            await CatalogRoleGuard.ValidateTransitionAsync(connection, transaction, CatalogSchemaContract.Tables[name], ct);
        await ValidateFunctionsAsync(connection, transaction, ct);
        await ValidateTriggersAsync(connection, transaction, ct);
        await ValidateGrantsAsync(connection, transaction, ct);
    }

    private static async Task ValidateHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var expected = MigrationRunner.EmbeddedMigrations;
        var workspace = WorkspaceMigrationVersion;
        var writes = MappingWriteMigrationVersion;
        var writeJournal = WriteJournalMigrationVersion;
        if (expected.Count < writes || expected[10].Checksum != MigrationChecksum || expected[workspace - 1].Checksum != WorkspaceMigrationChecksum ||
            expected[writes - 1].Checksum != MappingWriteMigrationChecksum || expected.Count < writeJournal || expected[writeJournal - 1].Checksum != WriteJournalMigrationChecksum ||
            expected.SingleOrDefault(script => script.Version == 43)?.Checksum != EditProposalMigrationChecksum ||
            expected.SingleOrDefault(script => script.Version == 44)?.Checksum != DocumentEvidenceSchemaAdmission.SpecChecksum ||
            expected.SingleOrDefault(script => script.Version == 45)?.Checksum != DocumentEvidenceSchemaAdmission.CreationChecksum ||
            expected.SingleOrDefault(script => script.Version == 46)?.Checksum != DocumentEvidenceSchemaAdmission.MigrationChecksum ||
            expected.SingleOrDefault(script => script.Version == 56)?.Checksum != AiUsageSchemaAdmission.MigrationChecksum ||
            !expected.Take(10).Select(script => script.Checksum).SequenceEqual(PrefixChecksums, StringComparer.Ordinal)) throw Refusal();
        await using var command = new NpgsqlCommand("SELECT version,checksum FROM ONLY braid.schema_migrations ORDER BY version", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var index = 0;
        while (await reader.ReadAsync(ct))
        {
            if (index >= expected.Count || reader.GetInt32(0) != expected[index].Version || reader.GetString(1) != expected[index].Checksum) throw Refusal();
            index++;
        }
        if (index != expected.Count) throw Refusal();
    }

    private static async Task ValidateRelationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Table table, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=1 AND COALESCE(bool_and(r.relkind='r' AND r.relpersistence='p' AND NOT r.relispartition
                AND r.reloptions IS NULL AND r.relreplident='d' AND r.relnatts=$2
                AND o.rolname='braid_migrator' AND so.rolname='braid_migrator'
                AND r.relrowsecurity=($1 NOT IN ('principals','schema_migrations')) AND NOT r.relforcerowsecurity
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=r.oid OR i.inhparent=r.oid)
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_rewrite w WHERE w.ev_class=r.oid)),false)
            FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            JOIN pg_catalog.pg_roles o ON o.oid=r.relowner JOIN pg_catalog.pg_roles so ON so.oid=n.nspowner
            WHERE n.nspname='braid' AND r.relname=$1
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name); command.Parameters.AddWithValue(table.Columns.Length);
        if (await command.ExecuteScalarAsync(ct) is not true) throw Refusal();
    }

    private static async Task ValidateColumnsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Table table, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),a.attnotnull,
                pg_catalog.pg_get_expr(d.adbin,d.adrelid),a.attisdropped,a.attidentity::text,a.attgenerated::text,
                a.attndims::integer,a.attinhcount::integer,a.attislocal,n.nspname,
                a.attcollation=t.typcollation,a.attnum::integer,cn.nspname,co.collname
            FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_type t ON t.oid=a.atttypid
            JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            LEFT JOIN pg_catalog.pg_collation co ON co.oid=a.attcollation LEFT JOIN pg_catalog.pg_namespace cn ON cn.oid=co.collnamespace
            WHERE a.attrelid=('braid.' || $1)::regclass AND a.attnum>0 ORDER BY a.attnum
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ordinal = 0;
        while (await reader.ReadAsync(ct))
        {
            if (ordinal >= table.Columns.Length) throw Refusal();
            var expected = table.Columns[ordinal++];
            if (reader.GetString(0) != expected.Name || reader.GetString(1) != expected.Type || reader.GetBoolean(2) != expected.NotNull ||
                (reader.IsDBNull(3) ? null : reader.GetString(3)) != expected.Default || reader.GetBoolean(4) || reader.GetString(5) != "" || reader.GetString(6) != "" ||
                reader.GetInt32(7) != 0 || reader.GetInt32(8) != 0 || !reader.GetBoolean(9) || reader.GetString(10) != "pg_catalog" || reader.GetInt32(12) != ordinal ||
                (expected.C ? reader.IsDBNull(13) || reader.GetString(13) != "pg_catalog" || reader.GetString(14) != "C" : !reader.GetBoolean(11))) throw Refusal();
        }
        if (ordinal != table.Columns.Length) throw Refusal();
    }

    private static async Task ValidateConstraintsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Table table, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.conname,c.contype::text,c.condeferrable,c.condeferred,c.convalidated,c.connoinherit,
                ARRAY(SELECT a.attname::text FROM unnest(c.conkey) WITH ORDINALITY k(attnum,ord)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum ORDER BY k.ord),
                n.nspname,r.relname,
                ARRAY(SELECT a.attname::text FROM unnest(c.confkey) WITH ORDINALITY k(attnum,ord)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.attnum ORDER BY k.ord),
                c.confdeltype::text,c.confupdtype::text,c.confmatchtype::text,pg_catalog.pg_get_constraintdef(c.oid,false),
                c.conislocal,c.coninhcount,
                (SELECT count(*)=4 AND count(DISTINCT p.proname)=4 AND bool_and(
                    t.tgisinternal AND t.tgenabled='O' AND pn.nspname='pg_catalog' AND t.tgparentid=0
                    AND t.tgnargs=0 AND octet_length(t.tgargs)=0 AND t.tgattr::text='' AND t.tgqual IS NULL
                    AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL
                    AND ((t.tgrelid=c.conrelid AND t.tgconstrrelid=c.confrelid
                        AND t.tgdeferrable=c.condeferrable AND t.tginitdeferred=c.condeferred
                        AND ((p.proname='RI_FKey_check_ins' AND t.tgtype=5) OR (p.proname='RI_FKey_check_upd' AND t.tgtype=17)))
                     OR (t.tgrelid=c.confrelid AND t.tgconstrrelid=c.conrelid AND
                        ((p.proname=CASE WHEN c.confdeltype='c' THEN 'RI_FKey_cascade_del' ELSE 'RI_FKey_restrict_del' END
                            AND t.tgtype=9 AND NOT t.tgdeferrable AND NOT t.tginitdeferred)
                         OR (p.proname=CASE WHEN c.confupdtype='a' THEN 'RI_FKey_noaction_upd' ELSE 'RI_FKey_restrict_upd' END
                            AND t.tgtype=17 AND t.tgdeferrable=CASE WHEN c.confupdtype='a' THEN c.condeferrable ELSE false END
                            AND t.tginitdeferred=CASE WHEN c.confupdtype='a' THEN c.condeferred ELSE false END)))))
                 FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid
                 JOIN pg_catalog.pg_namespace pn ON pn.oid=p.pronamespace WHERE t.tgconstraint=c.oid)
            FROM pg_catalog.pg_constraint c LEFT JOIN pg_catalog.pg_class r ON r.oid=c.confrelid
            LEFT JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            WHERE c.conrelid=('braid.' || $1)::regclass AND c.contype<>'t' ORDER BY c.conname
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        var remaining = table.Constraints.ToDictionary(c => c.Name, StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!remaining.Remove(reader.GetString(0), out var c) || reader.GetString(1) != c.Kind.ToString() || reader.GetBoolean(2) != c.Deferred ||
                reader.GetBoolean(3) != c.Deferred || !reader.GetBoolean(4) || !reader.GetBoolean(14) || reader.GetInt32(15) != 0) throw Refusal();
            if (c.Kind == 'c') { if (reader.GetBoolean(5) || c.Check is null || reader.GetString(13) != c.Check) throw Refusal(); }
            else if (!reader.GetFieldValue<string[]>(6).SequenceEqual(c.Columns)) throw Refusal();
            if (c.Kind == 'f' && (reader.IsDBNull(7) || reader.GetString(7) != "braid" || reader.GetString(8) != c.Target ||
                !reader.GetFieldValue<string[]>(9).SequenceEqual(c.TargetColumns!) || reader.GetString(10) != c.Delete || reader.GetString(11) != c.Update ||
                reader.GetString(12) != "s" || reader.IsDBNull(16) || !reader.GetBoolean(16))) throw Refusal();
        }
        if (remaining.Count != 0) throw Refusal();
    }

    private static async Task ValidatePolicyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Table table, CancellationToken ct)
    {
        const string expression = "((organization_id = (NULLIF(current_setting('braid.organization_id'::text, true), ''::text))::uuid) AND (creator_id = (NULLIF(current_setting('braid.chat_principal_id'::text, true), ''::text))::uuid))";
        await using var command = new NpgsqlCommand("""
            SELECT p.polname,p.polcmd::text,p.polpermissive,p.polroles,
                pg_catalog.pg_get_expr(p.polqual,p.polrelid,false),pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid,false)
            FROM pg_catalog.pg_policy p WHERE p.polrelid=('braid.' || $1)::regclass
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(0) != "creator_scope" || reader.GetString(1) != "*" || !reader.GetBoolean(2) ||
            !reader.GetFieldValue<uint[]>(3).SequenceEqual([0u]) || reader.IsDBNull(4) || reader.GetString(4) != expression ||
            reader.IsDBNull(5) || reader.GetString(5) != expression || await reader.ReadAsync(ct)) throw Refusal();
    }

    private sealed record Trigger(string Function, short Type, bool Deferred = false);
    private static async Task ValidateTriggersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct,
        IEnumerable<string>? selected = null)
    {
        var journal = await AuditLogSchemaAdmission.InstalledAsync(connection, transaction, ct);
        if (journal) await AuditLogSchemaAdmission.ValidateAsync(connection, transaction, ct);
        await AiUsageSchemaAdmission.ValidateAsync(connection, transaction, ct);
        await AiQuotaSchemaAdmission.ValidateAsync(connection, transaction, ct);
        foreach (var table in selected ?? ChatTables)
        {
            var conversation = table == "chat_conversations";
            var identity = table == "chat_identities";
            var audit = table is "chat_payload_audit" or "chat_mapping_write_audit";
            var function = audit ? "guard_" + table : identity ? "guard_chat_identity" : conversation ? "guard_chat_conversation" : "guard_chat_child";
            var expected = new Dictionary<string, Trigger>(StringComparer.Ordinal)
            {
                // The append-only audits refuse UPDATE/DELETE (27); the ledger guards also see INSERT (31).
                [audit ? table + "_guard" : identity ? "chat_identity_guard" : conversation ? "chat_conversation_guard" : "chat_child_guard"] = new(function, (short)(audit ? 27 : 31)),
                [audit ? table + "_truncate" : identity ? "chat_identity_truncate" : conversation ? "chat_conversation_truncate" : "chat_child_truncate"] = new(function, 34)
            };
            if (table is "chat_mapping_write_intents" or "chat_mapping_write_content" or "chat_mapping_write_commits" or "chat_edit_proposals" or "chat_edit_content" or "chat_edit_approvals")
            {
                expected.Clear();
                expected.Add(table + "_guard", new(table is "chat_mapping_write_content" or "chat_edit_content" ? "guard_chat_mapping_write_content" : "guard_chat_mapping_write_audit", 27));
                expected.Add(table + "_truncate", new(table is "chat_mapping_write_content" or "chat_edit_content" ? "guard_chat_mapping_write_content" : "guard_chat_mapping_write_audit", 34));
                audit = true;
            }
            if (journal && table == "chat_payload_audit") expected.Add("audit_capture_payload", new("audit_log_capture", 5));
            if (table == "chat_turns")
            {
                expected.Add("ai_usage_turn", new("ai_usage_capture", 21));
                expected.Add("ai_quota_turn", new("ai_quota_require_reservation", 7));
            }
            if (table == "chat_rounds") expected.Add("ai_usage_round", new("ai_usage_capture", 21));
            if (table == "chat_events") expected.Add("ai_usage_tool", new("ai_usage_capture", 5));
            if (identity) expected.Add("chat_identity_erasure", new("check_chat_identity_erasure", 17, true));
            else if (!conversation && !audit) expected.Add("chat_counters", new("check_chat_counters", 21, true));
            if (table == "chat_events")
            {
                expected.Add("chat_payload_audit", new("audit_chat_payload", 5));
                expected.Add("chat_mapping_write_audit", new("audit_chat_mapping_write", 5));
            }
            await using var command = new NpgsqlCommand("""
                SELECT t.tgname,t.tgtype,t.tgenabled::text,t.tgnargs,t.tgattr::text,t.tgqual IS NULL,
                    t.tgdeferrable,t.tginitdeferred,t.tgoldtable IS NULL AND t.tgnewtable IS NULL,
                    n.nspname,p.proname,t.tgparentid=0,t.tgconstrrelid=0,octet_length(t.tgargs)=0,
                    CASE WHEN t.tgconstraint=0 THEN false ELSE EXISTS(SELECT 1 FROM pg_catalog.pg_constraint c
                        WHERE c.oid=t.tgconstraint AND c.conname=t.tgname AND c.contype='t' AND c.conrelid=t.tgrelid
                        AND c.condeferrable AND c.condeferred AND c.convalidated AND c.conislocal AND c.coninhcount=0) END
                FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE t.tgrelid=('braid.' || $1)::regclass AND NOT t.tgisinternal
                """, connection, transaction);
            command.Parameters.AddWithValue(table);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!expected.Remove(reader.GetString(0), out var t) || reader.GetInt16(1) != t.Type || reader.GetString(2) != "O" ||
                    reader.GetInt16(3) != 0 || reader.GetString(4) != "" || !reader.GetBoolean(5) || reader.GetBoolean(6) != t.Deferred ||
                    reader.GetBoolean(7) != t.Deferred || !reader.GetBoolean(8) || reader.GetString(9) != "braid" || reader.GetString(10) != t.Function ||
                    !reader.GetBoolean(11) || !reader.GetBoolean(12) || !reader.GetBoolean(13) || reader.GetBoolean(14) != t.Deferred) throw Refusal();
            }
            if (expected.Count != 0) throw Refusal();
        }
    }

    private static async Task ValidateGrantsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct,
        string[]? selected = null)
    {
        var tables = selected ?? ChatTables;
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
            foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
                expected.Add(table + "||braid_migrator|" + privilege);
        if (tables.Contains("chat_conversations", StringComparer.Ordinal)) expected.Add("chat_conversations||braid_chat_writer|DELETE");
        foreach (var profile in new[] { ChatDatabaseProfile.Reader, ChatDatabaseProfile.Writer })
            foreach (var grant in ColumnGrants(profile))
            {
                var parts = grant.Split('|');
                if (tables.Contains(parts[0], StringComparer.Ordinal)) expected.Add(parts[0] + "|" + parts[1] + "|" + ChatRoleGuard.RoleName(profile) + "|" + parts[2]);
            }
        await using var command = new NpgsqlCommand("""
            SELECT r.relname::text,NULL::text,role.rolname,a.privilege_type,a.grantor=r.relowner AND NOT a.is_grantable
            FROM pg_catalog.pg_class r CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(r.relacl,pg_catalog.acldefault('r',r.relowner))) a
            LEFT JOIN pg_catalog.pg_roles role ON role.oid=a.grantee WHERE r.relnamespace='braid'::regnamespace AND r.relname=ANY($1::text[])
            UNION ALL SELECT r.relname::text,c.attname::text,role.rolname,a.privilege_type,a.grantor=r.relowner AND NOT a.is_grantable
            FROM pg_catalog.pg_class r JOIN pg_catalog.pg_attribute c ON c.attrelid=r.oid AND c.attnum>0
            CROSS JOIN LATERAL pg_catalog.aclexplode(c.attacl) a LEFT JOIN pg_catalog.pg_roles role ON role.oid=a.grantee
            WHERE r.relnamespace='braid'::regnamespace AND r.relname=ANY($1::text[])
            """, connection, transaction);
        command.Parameters.AddWithValue(tables);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (reader.IsDBNull(2) || !reader.GetBoolean(4) || !expected.Remove(reader.GetString(0) + "|" + (reader.IsDBNull(1) ? "" : reader.GetString(1)) + "|" + reader.GetString(2) + "|" + reader.GetString(3))) throw Refusal();
        if (expected.Count != 0) throw Refusal();
    }
}
