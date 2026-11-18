using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>
/// Admits the exact membership-incarnation triggers and state relation, after which source
/// captures project those triggers away so older table fingerprints remain meaningful.
/// </summary>
internal static class OrganizationSwitchingSchemaAdmission
{
    internal const string Migration = "0057_organization_switching.sql";
    internal const string StateTable = "organization_admission_state";
    internal const string StateFingerprint = "94e21aa1fafa9b19e5f5bca27cc2fcab748bbb1fbabdd78c6a23de013f44c591";
    private static readonly string[] Functions = ["organization_admission_state_guard", "organization_admission_incarnation"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions(Migration, 4));
    private static readonly (string Name, string Table, string Function, short Type)[] Triggers =
    [
        ("organization_admission_incarnation", "organization_memberships", "organization_admission_incarnation", 21),
        ("organization_admission_membership_retained", "organization_memberships", "organization_admission_state_guard", 11),
        ("organization_admission_state_guard", StateTable, "organization_admission_state_guard", 31),
        ("organization_admission_state_no_truncate", StateTable, "organization_admission_state_guard", 34)
    ];
    internal static string[] TriggerNames(string table) => Triggers.Where(t => t.Table == table).Select(t => t.Name).ToArray();
    internal static string[] AllTriggerNames => Triggers.Select(t => t.Name).ToArray();

    /// <summary>Admits any switching triggers on this table exactly and returns their names for exclusion.</summary>
    internal static async Task<string[]> AdmitTriggersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string table, CancellationToken token)
    {
        var names = TriggerNames(table);
        if (names.Length != 0) await ValidateAsync(connection, transaction, token);
        return names;
    }

    internal static async Task<bool> InstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        // Any owned-name object is a partial installation, never an older schema.
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.to_regclass('braid.organization_admission_state') IS NOT NULL
                OR EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='braid' AND p.proname=ANY(@functions))
                OR EXISTS(SELECT 1 FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
                    JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='braid' AND t.tgname=ANY(@triggers))
            """, connection, transaction);
        command.Parameters.AddWithValue("functions", Functions);
        command.Parameters.AddWithValue("triggers", AllTriggerNames);
        return await command.ExecuteScalarAsync(token) is true;
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await InstalledAsync(connection, transaction, token)) return;
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Functions, Definitions.Value, token);
        await using (var command = new NpgsqlCommand("""
            SELECT t.tgname,c.relname,p.proname,t.tgtype,t.tgenabled='O' AND t.tgnargs=0 AND t.tgattr::text=''
                AND t.tgqual IS NULL AND NOT t.tgisinternal AND NOT t.tgdeferrable AND NOT t.tginitdeferred
                AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL AND t.tgconstraint=0
                AND (t.tgfoid=pg_catalog.to_regprocedure(pg_catalog.format('braid.%I()',p.proname))) IS TRUE
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a
                    WHERE a.grantee<>p.proowner)
            FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid
            WHERE n.nspname='braid' AND (t.tgname=ANY(@names) OR (c.relname=@state AND NOT t.tgisinternal))
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("names", AllTriggerNames);
            command.Parameters.AddWithValue("state", StateTable);
            var expected = Triggers.ToDictionary(t => t.Name, StringComparer.Ordinal);
            await using var rows = await command.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
                if (!expected.Remove(rows.GetString(0), out var t) || rows.GetString(1) != t.Table || rows.GetString(2) != t.Function ||
                    rows.GetInt16(3) != t.Type || !rows.GetBoolean(4)) throw Invalid();
            if (expected.Count != 0) throw Invalid();
        }
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, [StateTable], token, validateAutomation: false, validateAudit: false, validateUsage: false, validateQuota: false) != StateFingerprint)
            throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Organization admission schema contract unavailable.");
}
