using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>Admits only this build's exact additive triggers; older schema fingerprints remain meaningful.</summary>
internal static class AutomationSchemaAdmission
{
    internal const string Fingerprint = "285288318669e3fbc159f6ce94e3f94af1542926b075bd226c0eaf8a7c4ff043";
    internal const string ComparisonFingerprint = "96cc2ae0c9aff9f089107b3cc4c974fd9bd267f6bfab324ede0265a3b49774fb";
    internal static readonly string[] ReadFunctions = ["automation_read", "automation_inbox"];
    internal static readonly string[] WriteFunctions = ["automation_read", "automation_write", "automation_claim_evaluation",
        "automation_finish_evaluation", "automation_claim_delivery", "automation_finish_delivery", "automation_inbox", "automation_read_notification"];
    internal static readonly string[] Tables = ["automation_projects", "automation_mappings", "automation_suites", "automation_evaluations",
        "notification_subscriptions", "notification_events", "notifications", "notification_destinations", "notification_deliveries",
        "notification_delivery_attempts", "notification_endpoint_windows", "organization_notifications"];
    private static readonly string[] OriginalFunctions = ["automation_authorized", "notification_emit", "automation_revision_inserted", "automation_gate",
        "automation_build_guard", "automation_build_event", "automation_release_event", "automation_endpoint_event", "automation_read",
        "automation_write", "automation_claim_evaluation", "automation_settle_suite", "automation_finish_evaluation", "automation_claim_delivery", "automation_finish_delivery", "automation_case_guard", "automation_result_passes", "automation_record_guard",
        "automation_organization_authorized", "automation_invitation_event", "automation_inbox", "automation_read_notification"];
    internal static readonly string[] ComparisonFunctions = ["automation_read_comparison", "automation_complete_comparison"];
    internal static readonly string[] Functions = [.. OriginalFunctions, .. ComparisonFunctions, "automation_comparison_header"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0030_regression_automation.sql", OriginalFunctions.Length));
    private static readonly Lazy<Dictionary<string, Match>> ComparisonDefinitions = new(() => BuildRoleGuard.LoadFunctions("0036_automation_comparison_settlement.sql", 7));

    internal static async Task<bool> HasComparisonSettlementAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT to_regprocedure('braid.automation_read_comparison()') IS NOT NULL", connection, transaction);
        return await command.ExecuteScalarAsync(token) is true;
    }
    private static readonly (string Name, string Table, string Function, short Type)[] Triggers =
    [
        ("automation_invitation_event", "organization_invitations", "automation_invitation_event", 21),
        ("automation_organization_notification_guard", "organization_notifications", "automation_record_guard", 31),
        ("automation_organization_notification_no_truncate", "organization_notifications", "automation_record_guard", 34),
        ("automation_suite_guard", "automation_suites", "automation_record_guard", 31),
        ("automation_suite_no_truncate", "automation_suites", "automation_record_guard", 34),
        ("automation_evaluation_guard", "automation_evaluations", "automation_record_guard", 31),
        ("automation_evaluation_no_truncate", "automation_evaluations", "automation_record_guard", 34),
        ("automation_event_guard", "notification_events", "automation_record_guard", 31),
        ("automation_event_no_truncate", "notification_events", "automation_record_guard", 34),
        ("automation_delivery_attempt_guard", "notification_delivery_attempts", "automation_record_guard", 31),
        ("automation_delivery_attempt_no_truncate", "notification_delivery_attempts", "automation_record_guard", 34),
        ("automation_revision_inserted", "mapping_revisions", "automation_revision_inserted", 5),
        ("automation_case_guard", "regression_cases", "automation_case_guard", 23),
        ("automation_build_guard", "build_approvals", "automation_build_guard", 7),
        ("automation_qualification_guard", "build_qualifications", "automation_build_guard", 7),
        ("automation_build_event", "build_runs", "automation_build_event", 17),
        ("automation_qualification_event", "build_qualifications", "automation_build_event", 5),
        ("automation_release_event", "local_release_audit", "automation_release_event", 5),
        ("automation_endpoint_event", "machine_invocation_audit", "automation_endpoint_event", 5)
    ];
    internal static string[] TriggerNames(string table) => Triggers.Where(t => t.Table == table).Select(t => t.Name).ToArray();
    internal static string[] AllTriggerNames => Triggers.Select(t => t.Name).ToArray();

    internal static async Task<bool> InstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        // Any owned-name object is a partial installation, not an old schema.
        // Otherwise a trigger-only remnant could be hidden by source fingerprints
        // without ever admitting its attachment, function or permissions.
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='braid' AND c.relname=ANY(@tables))
                OR EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='braid' AND p.proname=ANY(@functions))
                OR EXISTS(SELECT 1 FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
                    JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='braid' AND t.tgname=ANY(@triggers))
            """, connection, transaction);
        command.Parameters.AddWithValue("tables", Tables);
        command.Parameters.AddWithValue("functions", Functions);
        command.Parameters.AddWithValue("triggers", AllTriggerNames);
        return await command.ExecuteScalarAsync(token) is true;
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await InstalledAsync(connection, transaction, token)) throw new InvalidOperationException("Automation schema unavailable.");
        if (await LifecycleCatalogAdmission.ValidateIfInstalledAsync(connection, transaction, token)) return;
        await ValidateTriggersAsync(connection, transaction, token);
    }

    internal static async Task ValidateTriggersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await InstalledAsync(connection, transaction, token)) return;
        var comparisons = await HasComparisonSettlementAsync(connection, transaction, token);
        var definitions = new Dictionary<string, Match>(Definitions.Value, StringComparer.Ordinal);
        if (comparisons)
            foreach (var (name, definition) in ComparisonDefinitions.Value) definitions[name] = definition;
        var functions = comparisons ? Functions : OriginalFunctions;
        if (comparisons) await ValidateComparisonRoleContractAsync(connection, transaction, false, token);
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, functions, definitions, token);
        await ValidateFunctionGrantsAsync(connection, transaction, functions, comparisons, token);
        await using var command = new NpgsqlCommand("""
            SELECT t.tgname,c.relname,p.proname,t.tgtype,t.tgenabled='O' AND t.tgnargs=0 AND t.tgattr::text=''
                AND t.tgqual IS NULL AND NOT t.tgisinternal AND NOT t.tgdeferrable AND NOT t.tginitdeferred
                AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL AND t.tgconstraint=0
                AND (t.tgfoid=pg_catalog.to_regprocedure(pg_catalog.format('braid.%I()',p.proname))) IS TRUE
            FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid
            WHERE n.nspname='braid' AND t.tgname=ANY(@names)
            """, connection, transaction);
        command.Parameters.AddWithValue("names", AllTriggerNames);
        var expected = Triggers.ToDictionary(t => t.Name);
        await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
            if (!expected.Remove(rows.GetString(0), out var t) || rows.GetString(1) != t.Table || rows.GetString(2) != t.Function ||
                rows.GetInt16(3) != t.Type || !rows.GetBoolean(4)) throw new InvalidOperationException("Automation trigger contract unavailable.");
        if (expected.Count != 0) throw new InvalidOperationException("Automation triggers incomplete.");
        await rows.DisposeAsync();
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token, validateAutomation: false) != (comparisons ? ComparisonFingerprint : Fingerprint))
            throw new InvalidOperationException("Automation table contract unavailable.");
    }

    private static async Task ValidateFunctionGrantsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string[] functions, bool comparisons, CancellationToken token)
    {
        var expected = functions.Select(name => (Name: name, Role: "braid_migrator")).ToHashSet();
        foreach (var name in ReadFunctions) expected.Add((name, "braid_run_reader"));
        foreach (var name in WriteFunctions) expected.Add((name, "braid_run_writer"));
        if (comparisons) foreach (var name in ComparisonFunctions) expected.Add((name, "braid_comparison_settler"));
        await using var command = new NpgsqlCommand("""
            SELECT p.proname,CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee)::text END,
                a.privilege_type,a.is_grantable,a.grantor=p.proowner
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a
            WHERE n.nspname='braid' AND p.proname=ANY(@names)
            """, connection, transaction);
        command.Parameters.AddWithValue("names", functions);
        await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
            if (rows.GetString(2) != "EXECUTE" || rows.GetBoolean(3) || !rows.GetBoolean(4) ||
                !expected.Remove((rows.GetString(0), rows.GetString(1))))
                throw new InvalidOperationException("Automation function grants unavailable.");
        if (expected.Count != 0) throw new InvalidOperationException("Automation function grants incomplete.");
    }

    internal static async Task ValidateComparisonRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => await ValidateComparisonRoleContractAsync(connection, transaction, true, token);

    private static async Task ValidateComparisonRoleContractAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool requireSession, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT (NOT $2 OR current_user=session_user AND current_user='braid_comparison_settler') AND r.rolcanlogin
                AND NOT r.rolsuper AND NOT r.rolbypassrls AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole
                AND NOT r.rolreplication AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL
                AND (NOT $2 OR current_setting('transaction_isolation')='read committed') AND current_setting('session_replication_role')='origin'
                AND current_setting('row_security')='on' AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND has_schema_privilege(r.oid,'braid','USAGE')
                AND (NOT $2 OR coalesce(nullif(current_setting('braid.organization_id',true),''),'')='')
                AND NOT EXISTS(SELECT 1 FROM pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
                AND NOT has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspowner=r.oid OR has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND has_function_privilege(r.oid,p.oid,'EXECUTE')
                    AND NOT (n.nspname='braid' AND p.proname=ANY($1::text[])))
            FROM pg_roles r WHERE r.rolname='braid_comparison_settler'
            """, connection, transaction);
        command.Parameters.AddWithValue(ComparisonFunctions);
        command.Parameters.AddWithValue(requireSession);
        if (await command.ExecuteScalarAsync(token) is not true || !await HasComparisonSettlementAsync(connection, transaction, token))
            throw new InvalidOperationException("Comparison settlement profile unavailable.");
    }
}
