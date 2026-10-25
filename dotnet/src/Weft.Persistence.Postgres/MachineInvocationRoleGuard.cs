using System.Text.RegularExpressions;
using Npgsql;

namespace Weft.Persistence.Postgres;

/// <summary>Admits the two machine invocation logins and the exact 0024 contract, plus the release contract it reads.</summary>
internal static class MachineInvocationRoleGuard
{
    internal const string Fingerprint = "02fc712efc9171d006549040716f76c998ba8a8dee7387e0cd5296f8bacbfd8d";
    internal static readonly string[] Tables = ["invocation_grants", "invocation_grant_audit", "machine_invocations", "machine_invocation_audit", "machine_invocation_results"];
    internal static readonly string[] Names = ["guard_machine_invocation", "guard_machine_invocation_history", "machine_invocation_grant",
        "machine_invocation_provenance", "write_invocation_grant", "read_invocation_grants", "claim_machine_invocation",
        "finish_machine_invocation", "purge_machine_invocation_results"];
    internal static readonly string[] GrantFunctions = ["write_invocation_grant", "read_invocation_grants"];
    internal static readonly string[] InvocationFunctions = ["claim_machine_invocation", "finish_machine_invocation", "purge_machine_invocation_results"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0024_machine_invocation.sql", Names.Length));
    private static readonly Lazy<Dictionary<string, Match>> ReleaseDefinitions = new(() =>
        BuildRoleGuard.LoadFunctions("0022_local_releases.sql", LocalReleaseRoleGuard.Names.Length));

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool grants, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=@role AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL
                AND current_setting('transaction_isolation')='read committed' AND current_setting('session_replication_role')='origin'
                AND current_setting('row_security')='on' AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on'
                AND coalesce(nullif(current_setting('weft.organization_id',true),''),'')=''
                AND coalesce(nullif(current_setting('weft.build_principal_id',true),''),'')=''
                AND coalesce(nullif(current_setting('weft.build_worker_id',true),''),'')=''
                AND coalesce(nullif(current_setting('weft.build_claim_token',true),''),'')=''
                AND coalesce(nullif(current_setting('weft.build_claim_attempt',true),''),'')=''
                AND coalesce(nullif(current_setting('weft.build_claim_intent',true),''),'')=''
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
                    AND NOT (n.nspname='weft' AND p.proname=ANY(@functions)))
                AND (SELECT count(DISTINCT p.proname) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='weft' AND p.proname=ANY(@functions) AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE'))=cardinality(@functions)
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("role", grants ? "weft_invocation_grant_writer" : "weft_invocation_writer");
            command.Parameters.AddWithValue("functions", grants ? GrantFunctions : InvocationFunctions);
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await ValidateContractAsync(connection, transaction, token);
    }

    internal static async Task ValidateContractAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        // The grant and claim functions read the release pointer and its qualified build; admit that contract unchanged.
        await QualificationRoleGuard.ValidateContractAsync(connection, transaction, token);
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, LocalReleaseRoleGuard.Names, ReleaseDefinitions.Value, token);
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, LocalReleaseRoleGuard.Tables, token) != LocalReleaseRoleGuard.Fingerprint)
            throw Invalid();
        var definitions = Definitions.Value;
        await using (var installed = new NpgsqlCommand("SELECT to_regclass('weft.administration_state') IS NOT NULL", connection, transaction))
            if (await installed.ExecuteScalarAsync(token) is true)
            {
                definitions = new(definitions);
                definitions["guard_machine_invocation"] = BuildRoleGuard.LoadFunctions("0027_administration.sql", AdministrationRoleGuard.Names.Length)["guard_machine_invocation"];
            }
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Names, definitions, token);
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token) != Fingerprint) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Machine invocation database contract unavailable.");
}
