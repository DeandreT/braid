using System.Text.RegularExpressions;
using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class AdministrationRoleGuard
{
    internal const string Fingerprint = "5e0e08d979ba04d41915459f894b3b3eaad76a0765f157e853e85178ef2a6fe1";
    private const string PreOperationsFingerprint = "f35b0b26429ffd6f9c5ee9eb03ede7073b9e5c7476ab9e67e3d9ae297e62fb70";
    internal static readonly string[] Tables = ["administration_state", "organization_administration_roles", "organization_invitations", "administration_audit"];
    internal static readonly string[] Objects = [.. Tables, "administration_audit_sequence_seq"];
    internal static readonly string[] AuthorityTables = [.. Tables, "organizations", "principals", "organization_memberships",
        "projects", "project_memberships", "project_payload_read_grants", "external_identity_bindings", "local_release_targets",
        "invocation_grants", "invocation_grant_audit"];
    internal static readonly string[] Names = ["administration_audit_guard", "administration_authorized", "administration_identity_available", "administration_can_deactivate", "read_administration", "write_administration", "redeem_organization_invitation", "guard_machine_invocation"];
    private static readonly string[] Permitted = ["read_administration", "write_administration", "redeem_organization_invitation"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0027_administration.sql", Names.Length));

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user='weft_administration' AND r.rolcanlogin AND NOT r.rolsuper
                AND NOT r.rolbypassrls AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL AND current_setting('row_security')='on'
                AND current_setting('session_replication_role')='origin' AND current_setting('transaction_isolation')='read committed'
                AND coalesce(nullif(current_setting('weft.organization_id',true),''),'')=''
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members WHERE member=r.oid OR roleid=r.oid)
                AND NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')
                        AND NOT(n.nspname='weft' AND p.proname=ANY(@permitted)))
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("permitted", Permitted);
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Names, Definitions.Value, token);
        var fingerprint = PreOperationsFingerprint;
        if (await ReleaseOperationsRoleGuard.InstalledAsync(connection, transaction, token))
        {
            await ReleaseOperationsRoleGuard.ValidateAsync(connection, transaction, token);
            fingerprint = Fingerprint;
        }
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, AuthorityTables, token) != fingerprint)
            throw Invalid();
    }
    private static InvalidOperationException Invalid() => new("Administration database contract unavailable.");
}
