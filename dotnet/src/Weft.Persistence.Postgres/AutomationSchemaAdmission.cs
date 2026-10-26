using System.Text.RegularExpressions;
using Npgsql;

namespace Weft.Persistence.Postgres;

/// <summary>Admits only this build's exact additive triggers; older schema fingerprints remain meaningful.</summary>
internal static class AutomationSchemaAdmission
{
    internal const string Fingerprint = "f4ef820285687cdcabc360a935954781d612c04fbc4ede1c5d22712882bf31da";
    internal static readonly string[] ReadFunctions = ["automation_read", "automation_inbox"];
    internal static readonly string[] WriteFunctions = ["automation_read", "automation_write", "automation_claim_evaluation",
        "automation_finish_evaluation", "automation_claim_delivery", "automation_finish_delivery", "automation_inbox", "automation_read_notification"];
    internal static readonly string[] Tables = ["automation_projects", "automation_mappings", "automation_suites", "automation_evaluations",
        "notification_subscriptions", "notification_events", "notifications", "notification_destinations", "notification_deliveries",
        "notification_delivery_attempts", "notification_endpoint_windows", "organization_notifications"];
    internal static readonly string[] Functions = ["automation_authorized", "notification_emit", "automation_revision_inserted", "automation_gate",
        "automation_build_guard", "automation_build_event", "automation_release_event", "automation_endpoint_event", "automation_read",
        "automation_write", "automation_claim_evaluation", "automation_settle_suite", "automation_finish_evaluation", "automation_claim_delivery", "automation_finish_delivery", "automation_case_guard", "automation_result_passes", "automation_record_guard",
        "automation_organization_authorized", "automation_invitation_event", "automation_inbox", "automation_read_notification"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0030_regression_automation.sql", Functions.Length));
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
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='weft' AND c.relname=ANY(@tables))
                OR to_regprocedure('weft.automation_gate(uuid,uuid,uuid,uuid)') IS NOT NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("tables", Tables);
        return await command.ExecuteScalarAsync(token) is true;
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await InstalledAsync(connection, transaction, token)) throw new InvalidOperationException("Automation schema unavailable.");
        await ValidateTriggersAsync(connection, transaction, token);
    }

    internal static async Task ValidateTriggersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await InstalledAsync(connection, transaction, token)) return;
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Functions, Definitions.Value, token);
        await ValidateFunctionGrantsAsync(connection, transaction, token);
        await using var command = new NpgsqlCommand("""
            SELECT t.tgname,c.relname,p.proname,t.tgtype,t.tgenabled='O' AND t.tgnargs=0 AND t.tgattr::text=''
                AND t.tgqual IS NULL AND NOT t.tgisinternal AND NOT t.tgdeferrable AND NOT t.tginitdeferred
                AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL AND t.tgconstraint=0
            FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid
            WHERE n.nspname='weft' AND t.tgname=ANY(@names)
            """, connection, transaction);
        command.Parameters.AddWithValue("names", AllTriggerNames);
        var expected = Triggers.ToDictionary(t => t.Name);
        await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
            if (!expected.Remove(rows.GetString(0), out var t) || rows.GetString(1) != t.Table || rows.GetString(2) != t.Function ||
                rows.GetInt16(3) != t.Type || !rows.GetBoolean(4)) throw new InvalidOperationException("Automation trigger contract unavailable.");
        if (expected.Count != 0) throw new InvalidOperationException("Automation triggers incomplete.");
        await rows.DisposeAsync();
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token, validateAutomation: false) != Fingerprint)
            throw new InvalidOperationException("Automation table contract unavailable.");
    }

    private static async Task ValidateFunctionGrantsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var expected = Functions.Select(name => (Name: name, Role: "weft_migrator")).ToHashSet();
        foreach (var name in ReadFunctions) expected.Add((name, "weft_run_reader"));
        foreach (var name in WriteFunctions) expected.Add((name, "weft_run_writer"));
        await using var command = new NpgsqlCommand("""
            SELECT p.proname,CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee)::text END,
                a.privilege_type,a.is_grantable,a.grantor=p.proowner
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a
            WHERE n.nspname='weft' AND p.proname=ANY(@names)
            """, connection, transaction);
        command.Parameters.AddWithValue("names", Functions);
        await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
            if (rows.GetString(2) != "EXECUTE" || rows.GetBoolean(3) || !rows.GetBoolean(4) ||
                !expected.Remove((rows.GetString(0), rows.GetString(1))))
                throw new InvalidOperationException("Automation function grants unavailable.");
        if (expected.Count != 0) throw new InvalidOperationException("Automation function grants incomplete.");
    }
}
