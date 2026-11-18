using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>Admits the complete metadata journal before its additive triggers are projected out of older contracts.</summary>
internal static class AuditLogSchemaAdmission
{
    internal const string Fingerprint = "59832f1282d8ff56079753ef14f46057e749acc314498cec9fbf6ec91e7758fb";
    internal const string MigrationChecksum = "e469d367b7c2ff36c7eaf309fc08ab68c8bdad879b4ad0bea2acbe9ccea7ff3c";
    internal static readonly string[] Functions = ["audit_log_guard", "audit_log_append", "audit_log_project", "audit_log_record", "audit_log_capture", "read_audit_log"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0055_audit_log.sql", Functions.Length));
    private static readonly (string Name, string Table, string Function, short Type)[] Triggers =
    [
        ("audit_log_guard", "audit_log_events", "audit_log_guard", 31),
        ("audit_log_no_truncate", "audit_log_events", "audit_log_guard", 34),
        ("audit_capture_administration", "administration_audit", "audit_log_capture", 5),
        ("audit_capture_release", "local_release_audit", "audit_log_capture", 5),
        ("audit_capture_grant", "invocation_grant_audit", "audit_log_capture", 5),
        ("audit_capture_invocation", "machine_invocation_audit", "audit_log_capture", 5),
        ("audit_capture_request", "api_request_audit", "audit_log_capture", 5),
        ("audit_capture_payload", "chat_payload_audit", "audit_log_capture", 5),
        ("audit_capture_qualification", "build_qualifications", "audit_log_capture", 5),
        ("audit_capture_approval", "build_approvals", "audit_log_capture", 21),
        ("audit_capture_build", "build_runs", "audit_log_capture", 21),
        ("audit_capture_publication", "build_publications", "audit_log_capture", 21)
    ];
    internal static string[] TriggerNames => Triggers.Select(t => t.Name).ToArray();

    internal static async Task<bool> InstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT to_regclass('braid.audit_log_events') IS NOT NULL OR EXISTS(
                SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid' AND p.proname=ANY($1))
                OR EXISTS(SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='braid' AND t.tgname=ANY($2))
            """, connection, transaction);
        command.Parameters.AddWithValue(Functions); command.Parameters.AddWithValue(TriggerNames);
        if (await command.ExecuteScalarAsync(token) is true) return true;
        // Object absence alone cannot distinguish a prefix from a removed journal.
        // Restricted profiles without history access must require the current schema.
        await using var readable = new NpgsqlCommand("SELECT has_column_privilege('braid.schema_migrations','version','SELECT')", connection, transaction);
        if (await readable.ExecuteScalarAsync(token) is not true) return true;
        await using var recorded = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM ONLY braid.schema_migrations WHERE version>=55)", connection, transaction);
        return await recorded.ExecuteScalarAsync(token) is true;
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await InstalledAsync(connection, transaction, token)) return;
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Functions, Definitions.Value, token);
        var grants = Functions.Select(name => (name, "braid_migrator")).ToHashSet();
        grants.Add(("read_audit_log", "braid_administration"));
        await using (var command = new NpgsqlCommand("""
            SELECT p.proname,CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee)::text END,
                a.privilege_type,a.is_grantable,a.grantor=p.proowner
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
            WHERE n.nspname='braid' AND p.proname=ANY($1)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(Functions);
            await using var rows = await command.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
                if (rows.GetString(2) != "EXECUTE" || rows.GetBoolean(3) || !rows.GetBoolean(4) || !grants.Remove((rows.GetString(0), rows.GetString(1)))) throw Invalid();
            if (grants.Count != 0) throw Invalid();
        }
        await using (var command = new NpgsqlCommand("""
            SELECT t.tgname,c.relname,p.proname,t.tgtype,t.tgenabled='O' AND t.tgnargs=0 AND t.tgattr::text=''
                AND t.tgqual IS NULL AND NOT t.tgisinternal AND NOT t.tgdeferrable AND NOT t.tginitdeferred
                AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL AND t.tgconstraint=0
                AND (t.tgfoid=to_regprocedure(format('braid.%I()',p.proname))) IS TRUE
            FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_proc p ON p.oid=t.tgfoid WHERE n.nspname='braid' AND t.tgname=ANY($1)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(TriggerNames);
            var expected = Triggers.ToDictionary(t => t.Name);
            await using var rows = await command.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
                if (!expected.Remove(rows.GetString(0), out var t) || rows.GetString(1) != t.Table || rows.GetString(2) != t.Function || rows.GetInt16(3) != t.Type || !rows.GetBoolean(4)) throw Invalid();
            if (expected.Count != 0) throw Invalid();
        }
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, ["audit_log_events"], token,
                validateAutomation: false, validateAudit: false, validateUsage: false, validateQuota: false) != Fingerprint) throw Invalid();
    }
    private static InvalidOperationException Invalid() => new("Audit journal schema is unavailable.");
}
