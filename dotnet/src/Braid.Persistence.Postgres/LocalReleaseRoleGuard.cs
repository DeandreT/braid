using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

internal static class LocalReleaseRoleGuard
{
    internal const string Fingerprint = "a794cb940b522b27a025b35a4e08c149f0ae5c19a74dbc638d991aadacbf05e3";
    internal static readonly string[] Tables = ["local_release_targets", "local_releases", "local_release_audit"];
    internal static readonly string[] Names = ["local_release_canonical", "lock_local_release_scope", "local_release_admitted",
        "guard_local_release", "guard_local_release_history", "write_local_target", "write_local_release",
        "transition_local_release", "read_local_target", "read_local_release"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0022_local_releases.sql", Names.Length));

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool writer, CancellationToken token)
    {
        var operations = await ReleaseOperationsRoleGuard.InstalledAsync(connection, transaction, token);
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=@role AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL
                AND current_setting('transaction_isolation')='read committed' AND current_setting('session_replication_role')='origin'
                AND current_setting('row_security')='on' AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on'
                AND coalesce(nullif(current_setting('braid.organization_id',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_principal_id',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_worker_id',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_claim_token',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_claim_attempt',true),''),'')=''
                AND coalesce(nullif(current_setting('braid.build_claim_intent',true),''),'')=''
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
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("role", writer ? "braid_release_writer" : "braid_release_reader");
            string[] functions = writer
                ? ["read_local_target", "read_local_release", "write_local_target", "write_local_release", "transition_local_release"]
                : ["read_local_target", "read_local_release"];
            if (operations) functions = [.. functions, "read_release_operations", .. writer ? new[] { "operate_release_target", "read_release_operation" } : []];
            command.Parameters.AddWithValue("functions", functions);
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await QualificationRoleGuard.ValidateContractAsync(connection, transaction, token);
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Names, Definitions.Value, token);
        if (operations) await ReleaseOperationsRoleGuard.ValidateAsync(connection, transaction, token);
        else if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token) != Fingerprint) throw Invalid();
    }
    private static InvalidOperationException Invalid() => new("Local release database contract unavailable.");
}
