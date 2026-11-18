using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>
/// Admits the trigger worker login and the exact 0043 trigger contract with its 0044 schedule and batch extension. Configuration
/// functions belong to the release writer; the worker can only read definitions, admit, claim and finish orders, record scheduled
/// runs and batch parents, and claim and observe deliveries.
/// </summary>
internal static class TriggerRoleGuard
{
    internal const string Role = "braid_trigger_worker";
    internal const string MigrationChecksum = "630bdfe240464d3858d2bb2288259cefaab66d0364ff920d6810344b5a1522f5";
    internal const string ScheduleMigrationChecksum = "5dd73c8c3b1852eccff2bb332ad94a1f764a769474cc0fddbebe0230ebbd38b9";
    internal const string OperationsMigrationChecksum = "71c119a14ec6432b1e2e0b9ee3814971acbc4b597e4a7bec8bd91302e16d4455";
    internal const string Fingerprint = "6ad4941d4c67336c0a9365880c0a319967582cfdfa05c80bca134ff5feb88131";
    internal static readonly string[] Tables = ["trigger_controls", "trigger_configurations", "trigger_operations", "trigger_work", "trigger_receipts",
        "trigger_payloads", "trigger_deliveries", "trigger_events", "trigger_runs", "trigger_run_work", "trigger_delivery_resolutions", "as2_send_fences", "trigger_input_quarantine"];
    internal static readonly string[] AdministrationFunctions = ["operate_trigger", "read_trigger_operation", "read_trigger", "resolve_trigger_delivery"];
    /// <summary>The Operations read shared by the release reader and writer.</summary>
    internal const string OperationsRead = "read_trigger_operations";
    internal static readonly string[] WorkerFunctions = ["read_trigger_definition", "admit_trigger_work", "claim_trigger_work", "finish_trigger_work",
        "claim_trigger_delivery", "observe_trigger_delivery", "read_trigger_schedule", "record_trigger_occurrences", "claim_trigger_run",
        "read_trigger_pending_work", "read_trigger_sources", "record_trigger_run_page", "admit_trigger_batch", "begin_as2_send", "record_trigger_quarantine_page"];
    internal static readonly string[] InternalFunctions = ["guard_trigger", "trigger_definition_valid", "trigger_retry_at", "trigger_work_fence",
        "guard_trigger_run", "trigger_schedule_epoch", "trigger_admission", "trigger_backlog", "trigger_insert_work", "guard_trigger_resolution"];
    internal static readonly string[] Names = [.. InternalFunctions, .. AdministrationFunctions, .. WorkerFunctions, OperationsRead];
    private static readonly Lazy<Dictionary<string, Match>> Original = new(() => BuildRoleGuard.LoadFunctions("0050_trigger_work.sql", 15));
    // 0044 replaces trigger_definition_valid, operate_trigger and admit_trigger_work, and 0045 replaces guard_trigger; later bodies win.
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() =>
    {
        var result = new Dictionary<string, Match>(Original.Value, StringComparer.Ordinal);
        foreach (var (name, match) in BuildRoleGuard.LoadFunctions("0051_trigger_schedules.sql", 15)) result[name] = match;
        foreach (var (name, match) in BuildRoleGuard.LoadFunctions("0052_trigger_operations.sql", 4)) result[name] = match;
        foreach (var (name, match) in BuildRoleGuard.LoadFunctions("0053_as2_send_fence.sql", 1)) result[name] = match;
        foreach (var (name, match) in BuildRoleGuard.LoadFunctions("0054_pilot_quarantine.sql", 3)) result[name] = match;
        return result;
    });
    /// <summary>The ledger guard 0043 replaces; it admits the trigger worker only for an existing order's exact ledger key.</summary>
    internal static Match HostGuard => Original.Value["guard_host_order"];
    internal static Match LedgerGuard => Original.Value["guard_machine_invocation"];

    internal static async Task<bool> InstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass('braid.trigger_work') IS NOT NULL OR to_regclass('braid.trigger_controls') IS NOT NULL", connection, transaction);
        return await command.ExecuteScalarAsync(token) is true;
    }

    internal static async Task ValidateWorkerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=@role AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL
                AND current_setting('transaction_isolation')='read committed' AND current_setting('session_replication_role')='origin'
                AND current_setting('row_security')='on' AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on'
                AND coalesce(nullif(current_setting('braid.organization_id',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_principal_id',true),''),'')=''
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
                AND pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
                AND NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')
                    AND NOT (n.nspname='braid' AND p.proname=ANY(@functions)))
                AND (SELECT count(DISTINCT p.proname) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='braid' AND p.proname=ANY(@functions) AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE'))=cardinality(@functions)
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("role", Role);
            command.Parameters.AddWithValue("functions", WorkerFunctions);
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        // The worker reads release pins and writes ledger rows; admit those complete contracts first.
        await MachineInvocationRoleGuard.ValidateContractAsync(connection, transaction, token);
    }

    /// <summary>Schema, trigger, function and grant admission only. Login checks belong to each caller.</summary>
    internal static async Task ValidateSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Names, Definitions.Value, token);
        var grants = Names.Select(name => (name, "braid_migrator")).ToHashSet();
        foreach (var name in AdministrationFunctions) grants.Add((name, "braid_release_writer"));
        foreach (var name in WorkerFunctions) grants.Add((name, Role));
        grants.Add((OperationsRead, "braid_release_reader")); grants.Add((OperationsRead, "braid_release_writer"));
        await using (var command = new NpgsqlCommand("""
            SELECT p.proname,CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee)::text END,
                a.privilege_type,a.is_grantable,a.grantor=p.proowner
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
            WHERE n.nspname='braid' AND p.proname=ANY($1)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(Names);
            await using var rows = await command.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
                if (rows.GetString(2) != "EXECUTE" || rows.GetBoolean(3) || !rows.GetBoolean(4) || !grants.Remove((rows.GetString(0), rows.GetString(1)))) throw Invalid();
            if (grants.Count != 0) throw Invalid();
        }
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token) != Fingerprint) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Trigger database contract unavailable.");
}
