using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

internal static class AdministrationRoleGuard
{
    internal const string Fingerprint = "2ca888327d4ccef9b9d533ca2e3768c7ac1e4d0cb113a90e0d7bdffb951223ad";
    private const string PreOperationsFingerprint = "0c52060e446d671829fd99f911656dac94e49ac386f4bfcff126b6d5f4c08dc4";
    internal static readonly string[] Tables = ["administration_state", "organization_administration_roles", "organization_invitations", "administration_audit"];
    internal static readonly string[] Objects = [.. Tables, "administration_audit_sequence_seq"];
    internal static readonly string[] AuthorityTables = [.. Tables, "organizations", "principals", "organization_memberships",
        "projects", "project_memberships", "project_payload_read_grants", "external_identity_bindings", "local_release_targets",
        "invocation_grants", "invocation_grant_audit"];
    internal static readonly string[] Names = ["administration_audit_guard", "administration_authorized", "administration_identity_available", "administration_can_deactivate", "read_administration", "write_administration", "redeem_organization_invitation", "guard_machine_invocation"];
    private static readonly string[] Permitted = ["read_administration", "write_administration", "redeem_organization_invitation", "read_audit_log", "read_ai_usage", "read_ai_quotas", "write_ai_quota"];
    private static readonly string[] LifecyclePermitted = [.. Permitted, "read_resource_lifecycle", "change_resource_lifecycle", "read_lifecycle_operation",
        "delete_resource_item", "read_resource_deletion", "read_retention_policies", "replace_retention_policies", "preview_resource_purge", "request_resource_purge",
        "read_deletion_job", "read_resource_purge_operation", "read_resource_purge", "retire_release_target", "read_target_retirement", "resume_deletion_job"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0027_administration.sql", Names.Length));
    private static readonly Lazy<Match> SwitchingRedemption = new(() => BuildRoleGuard.LoadFunctions("0057_organization_switching.sql", 4)["redeem_organization_invitation"]);

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await ValidateRoleAsync(connection, transaction, token);
        if (await LifecycleInstalledAsync(connection, transaction, token))
            await LifecycleCatalogAdmission.ValidateAsync(connection, transaction, token);
        else await ValidateContractAsync(connection, transaction, token);
    }

    internal static async Task ValidateRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user='braid_administration' AND r.rolcanlogin AND NOT r.rolsuper
                AND NOT r.rolbypassrls AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL AND current_setting('row_security')='on'
                AND current_setting('session_replication_role')='origin' AND current_setting('transaction_isolation')='read committed'
                AND coalesce(nullif(current_setting('braid.organization_id',true),''),'')=''
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members WHERE member=r.oid OR roleid=r.oid)
                AND NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')
                        AND NOT(n.nspname='braid' AND p.proname=ANY(@permitted)))
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("permitted", await LifecycleInstalledAsync(connection, transaction, token) ? LifecyclePermitted : Permitted);
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
    }

    /// <summary>The installed functions and authority shape, independent of the connecting role.</summary>
    internal static async Task ValidateContractAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var definitions = Definitions.Value;
        if (await TriggerRoleGuard.InstalledAsync(connection, transaction, token))
            definitions = new(definitions, StringComparer.Ordinal) { ["guard_machine_invocation"] = TriggerRoleGuard.LedgerGuard };
        if (await OrganizationSwitchingInstalledAsync(connection, transaction, token))
            definitions = new(definitions, StringComparer.Ordinal) { ["redeem_organization_invitation"] = SwitchingRedemption.Value };
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Names, definitions, token);
        var fingerprint = PreOperationsFingerprint;
        if (await ReleaseOperationsRoleGuard.InstalledAsync(connection, transaction, token))
        {
            await ReleaseOperationsRoleGuard.ValidateAsync(connection, transaction, token);
            fingerprint = Fingerprint;
        }
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, AuthorityTables, token) != fingerprint)
            throw Invalid();
    }
    private static async Task<bool> LifecycleInstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass('braid.resource_lifecycles') IS NOT NULL", connection, transaction);
        return await command.ExecuteScalarAsync(token) is true;
    }
    internal static async Task<bool> OrganizationSwitchingInstalledAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('braid.organization_admission_state') IS NOT NULL", connection, transaction);
        return await command.ExecuteScalarAsync(token) is true;
    }
    private static InvalidOperationException Invalid() => new("Administration database contract unavailable.");
}
