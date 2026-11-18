using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

internal static class AiQuotaSchemaAdmission
{
    internal const string Fingerprint = "782949166bdafa73adacdad30ba941f2e29ffead66539043c046428323c00104";
    internal const string MigrationChecksum = "48bf52fd22973280f4ca6d395e3c9b5ab6bc5723ae5ac2d2dffbe034fe973a77";
    internal static readonly string[] Tables = ["ai_quota_state", "ai_quota_limits", "ai_quota_reservations"];
    internal static readonly string[] Functions = ["ai_quota_guard", "read_ai_quotas", "write_ai_quota", "reserve_ai_quota", "ai_quota_require_reservation", "ai_quota_settle"];
    internal const string ReserveSignature = "braid.reserve_ai_quota(uuid,uuid,uuid,uuid,text,text,timestamp with time zone,bigint,text,integer)";
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0056_ai_usage_quotas.sql", 10));
    private static readonly (string Name, string Table, string Function, short Type)[] Triggers =
    [
        ("ai_quota_state_guard", "ai_quota_state", "ai_quota_guard", 31),
        ("ai_quota_state_no_truncate", "ai_quota_state", "ai_quota_guard", 34),
        ("ai_quota_limit_guard", "ai_quota_limits", "ai_quota_guard", 31),
        ("ai_quota_limit_no_truncate", "ai_quota_limits", "ai_quota_guard", 34),
        ("ai_quota_reservation_guard", "ai_quota_reservations", "ai_quota_guard", 31),
        ("ai_quota_reservation_no_truncate", "ai_quota_reservations", "ai_quota_guard", 34),
        ("ai_quota_turn", "chat_turns", "ai_quota_require_reservation", 7),
        ("ai_quota_usage", "ai_usage_turns", "ai_quota_settle", 21)
    ];
    internal static string[] TriggerNames => Triggers.Select(item => item.Name).ToArray();

    internal static async Task<bool> InstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var query = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='braid' AND c.relname=ANY($1))
                OR EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid' AND p.proname=ANY($2))
                OR EXISTS(SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='braid' AND t.tgname=ANY($3))
            """, connection, transaction);
        query.Parameters.AddWithValue(Tables); query.Parameters.AddWithValue(Functions); query.Parameters.AddWithValue(TriggerNames);
        if (await query.ExecuteScalarAsync(token) is true) return true;
        await using var readable = new NpgsqlCommand("SELECT has_column_privilege('braid.schema_migrations','version','SELECT')", connection, transaction);
        if (await readable.ExecuteScalarAsync(token) is not true) return true;
        await using var recorded = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM ONLY braid.schema_migrations WHERE version>=56)", connection, transaction);
        return await recorded.ExecuteScalarAsync(token) is true;
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await InstalledAsync(connection, transaction, token)) return;
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Functions, Definitions.Value, token);
        var grants = Functions.Select(name => (name, "braid_migrator")).ToHashSet();
        grants.Add(("read_ai_quotas", "braid_administration")); grants.Add(("write_ai_quota", "braid_administration"));
        grants.Add(("reserve_ai_quota", "braid_chat_writer"));
        await using (var query = new NpgsqlCommand("""
            SELECT p.proname,CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee)::text END,
                a.privilege_type,a.is_grantable,a.grantor=p.proowner
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
            WHERE n.nspname='braid' AND p.proname=ANY($1)
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(Functions);
            await using var rows = await query.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
                if (rows.GetString(2) != "EXECUTE" || rows.GetBoolean(3) || !rows.GetBoolean(4) || !grants.Remove((rows.GetString(0), rows.GetString(1)))) throw Invalid();
            if (grants.Count != 0) throw Invalid();
        }
        await using (var query = new NpgsqlCommand("""
            SELECT t.tgname,c.relname,p.proname,t.tgtype,t.tgenabled='O' AND t.tgnargs=0 AND t.tgattr::text=''
                AND t.tgqual IS NULL AND NOT t.tgisinternal AND NOT t.tgdeferrable AND NOT t.tginitdeferred
                AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL AND t.tgconstraint=0
                AND (t.tgfoid=to_regprocedure(format('braid.%I()',p.proname))) IS TRUE
            FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_proc p ON p.oid=t.tgfoid WHERE n.nspname='braid' AND t.tgname=ANY($1)
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(TriggerNames);
            var expected = Triggers.ToDictionary(item => item.Name);
            await using var rows = await query.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
                if (!expected.Remove(rows.GetString(0), out var value) || rows.GetString(1) != value.Table || rows.GetString(2) != value.Function ||
                    rows.GetInt16(3) != value.Type || !rows.GetBoolean(4)) throw Invalid();
            if (expected.Count != 0) throw Invalid();
        }
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token,
            validateAutomation: false, validateAudit: false, validateUsage: false, validateQuota: false) != Fingerprint) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("AI quota schema is unavailable.");
}
