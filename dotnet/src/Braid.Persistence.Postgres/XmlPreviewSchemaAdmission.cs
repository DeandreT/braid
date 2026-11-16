using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>An additive private envelope; no old function body or table contract is projected away.</summary>
internal static class XmlPreviewSchemaAdmission
{
    internal const string Table = "xml_preview_documents";
    internal const string MigrationChecksum = "0697bf8366525f6728f2149b009afa99efa1a5c690feb2e851b4f953472af8d8";
    internal static readonly string[] Functions = ["xml_metadata_valid", "guard_xml_preview_document", "preview_submit_xml", "preview_complete_xml", "preview_xml_inputs", "preview_xml_documents"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0042_xml_documents.sql", Functions.Length));

    internal static async Task<bool> InstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass('braid.xml_preview_documents') IS NOT NULL OR EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid' AND p.proname=ANY($1))", connection, transaction);
        command.Parameters.AddWithValue(Functions);
        if (await command.ExecuteScalarAsync(token) is true) return true;
        // Absence cannot distinguish an old prefix from a removed current suffix.
        // Ordinary preview roles cannot read the journal and therefore require the
        // current XML schema, even for legacy JSON operations. No request setting,
        // role escalation or caller-supplied version can select an older contract.
        await using var readable = new NpgsqlCommand("""
            SELECT has_column_privilege('braid.schema_migrations','version','SELECT')
                AND has_column_privilege('braid.schema_migrations','checksum','SELECT')
            """, connection, transaction);
        if (await readable.ExecuteScalarAsync(token) is not true) return true;
        // Existing owner-side historical fixture ports already hold the migration
        // lock. A readable journal is a discriminator only when its complete,
        // consecutive history matches the embedded source, not merely max(version).
        var expected = MigrationRunner.EmbeddedMigrations;
        var count = 0;
        await using var history = new NpgsqlCommand("SELECT version,checksum FROM ONLY braid.schema_migrations ORDER BY version", connection, transaction);
        await using var rows = await history.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
        {
            if (count >= expected.Count || rows.GetInt32(0) != count + 1 || rows.GetString(1) != expected[count].Checksum) throw Invalid();
            count++;
        }
        if (count == 0) throw Invalid();
        return count >= 42;
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await InstalledAsync(connection, transaction, token)) return;
        await DocumentJsonSchemaAdmission.ValidateAsync(connection, transaction, token);
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Functions, Definitions.Value, token);
        var grants = Functions.Select(name => (name, "braid_migrator")).ToHashSet();
        foreach (var name in new[] { "preview_submit_xml", "preview_complete_xml", "preview_xml_inputs", "preview_xml_documents" }) grants.Add((name, "braid_preview_writer"));
        grants.Add(("preview_xml_documents", "braid_preview_reader"));
        await using (var command = new NpgsqlCommand("SELECT p.proname,CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee)::text END,a.privilege_type,a.is_grantable,a.grantor=p.proowner FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE n.nspname='braid' AND p.proname=ANY($1)", connection, transaction))
        {
            command.Parameters.AddWithValue(Functions);
            await using var rows = await command.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
                if (rows.GetString(2) != "EXECUTE" || rows.GetBoolean(3) || !rows.GetBoolean(4) || !grants.Remove((rows.GetString(0), rows.GetString(1)))) throw Invalid();
            if (grants.Count != 0) throw Invalid();
        }
        await using (var command = new NpgsqlCommand("""
            SELECT c.relkind='r' AND c.relpersistence='p' AND NOT c.relispartition AND c.reloptions IS NULL
                AND c.relowner='braid_migrator'::regrole AND n.nspowner=c.relowner AND c.relrowsecurity AND NOT c.relforcerowsecurity
                AND NOT EXISTS(SELECT 1 FROM pg_inherits WHERE inhrelid=c.oid OR inhparent=c.oid)
                AND NOT EXISTS(SELECT 1 FROM pg_rewrite WHERE ev_class=c.oid)
                AND (SELECT count(*)=7 AND bool_and(a.grantee=c.relowner AND a.grantor=c.relowner AND NOT a.is_grantable)
                    FROM aclexplode(coalesce(c.relacl,acldefault('r',c.relowner))) a)
                AND NOT EXISTS(SELECT 1 FROM pg_attribute WHERE attrelid=c.oid AND attnum>0 AND attacl IS NOT NULL)
                AND (SELECT count(*)=1 AND bool_and(i.indisunique AND i.indisprimary AND i.indisvalid AND i.indisready AND i.indislive
                    AND i.indimmediate AND NOT i.indisexclusion AND NOT i.indnullsnotdistinct AND NOT i.indisreplident
                    AND i.indnkeyatts=4 AND i.indnatts=4 AND i.indkey::text='1 2 3 4' AND i.indexprs IS NULL AND i.indpred IS NULL
                    AND x.relname='xml_preview_documents_pkey' AND x.relkind='i' AND x.relpersistence='p' AND x.reloptions IS NULL
                    AND x.relowner=c.relowner AND x.relnamespace=c.relnamespace AND a.amname='btree'
                    AND NOT EXISTS(SELECT 1 FROM unnest(i.indoption::smallint[]) o WHERE o<>0))
                    FROM pg_index i JOIN pg_class x ON x.oid=i.indexrelid JOIN pg_am a ON a.oid=x.relam WHERE i.indrelid=c.oid)
                AND (SELECT count(*)=1 AND bool_and(t.tgname='guard_xml_preview_document' AND t.tgtype=23 AND t.tgenabled='O'
                    AND t.tgfoid='braid.guard_xml_preview_document()'::regprocedure AND t.tgnargs=0 AND t.tgattr::text=''
                    AND t.tgqual IS NULL AND NOT t.tgdeferrable AND NOT t.tginitdeferred AND t.tgconstraint=0
                    AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL) FROM pg_trigger t WHERE t.tgrelid=c.oid AND NOT t.tgisinternal)
                AND (SELECT count(*)=1 AND bool_and(p.polname='xml_preview_documents_organization_scope' AND p.polcmd='*'
                    AND p.polpermissive AND p.polroles=ARRAY[0::oid]
                    AND pg_get_expr(p.polqual,p.polrelid)='(organization_id = (NULLIF(current_setting(''braid.organization_id''::text, true), ''''::text))::uuid)'
                    AND pg_get_expr(p.polwithcheck,p.polrelid)=pg_get_expr(p.polqual,p.polrelid)) FROM pg_policy p WHERE p.polrelid=c.oid)
                AND (SELECT count(*)=2 AND bool_and(k.convalidated AND NOT k.condeferrable AND NOT k.condeferred AND k.conislocal AND k.coninhcount=0
                    AND ((k.conname='xml_preview_documents_pkey' AND k.contype='p' AND k.conkey=ARRAY[1,2,3,4]::smallint[])
                        OR (k.conname='xml_preview_documents_run_fkey' AND k.contype='f' AND k.conkey=ARRAY[1,2]::smallint[]
                            AND k.confrelid='braid.preview_runs'::regclass AND k.confkey=ARRAY[1,2]::smallint[]
                            AND k.confupdtype='a' AND k.confdeltype='c' AND k.confmatchtype='s')))
                    FROM pg_constraint k WHERE k.conrelid=c.oid AND k.contype<>'n')
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='braid' AND c.relname='xml_preview_documents'
            """, connection, transaction))
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        var expected = new[] { ("organization_id", "uuid", true), ("run_id", "uuid", true), ("direction", "integer", true), ("ordinal", "integer", true),
            ("name", "text", false), ("target_name", "text", false), ("target_name_truncated", "boolean", false), ("records_written", "bigint", false), ("metadata", "text", true), ("content", "bytea", true),
            ("digest", "text", true), ("byte_count", "integer", true), ("metadata_digest", "text", true), ("envelope_digest", "text", true) };
        await using var columns = new NpgsqlCommand("SELECT attname,format_type(atttypid,atttypmod),attnotnull,attidentity='',attgenerated='',attisdropped,atthasdef,attinhcount FROM pg_attribute WHERE attrelid='braid.xml_preview_documents'::regclass AND attnum>0 ORDER BY attnum", connection, transaction);
        await using var reader = await columns.ExecuteReaderAsync(token);
        var index = 0;
        while (await reader.ReadAsync(token))
        {
            if (index >= expected.Length || (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)) != expected[index++] || !reader.GetBoolean(3) || !reader.GetBoolean(4)
                || reader.GetBoolean(5) || reader.GetBoolean(6) || reader.GetInt32(7) != 0) throw Invalid();
        }
        if (index != expected.Length) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("XML preview schema is unavailable.");
}
