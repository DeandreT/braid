using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>Admits the two machine invocation logins and the exact 0024 contract, plus the release contract it reads.</summary>
internal static class MachineInvocationRoleGuard
{
    internal const string HostOrderChecksum = "2c60244079205d7a1eece9303f3aec37bf1ef147a67b2048b2143c0deaedda67";
    internal static readonly string[] HostTables = ["host_order_pins", "host_target_slots"];
    internal static readonly string[] AttemptFunctions = ["claim_machine_attempt", "finish_machine_attempt"];
    internal const string Fingerprint = "db1363ae7e46e305d971c082463024c349b77d2c46836dd7dbe2b0918d736d37";
    internal static readonly string[] Tables = ["invocation_grants", "invocation_grant_audit", "machine_invocations", "machine_invocation_audit", "machine_invocation_results"];
    internal static readonly string[] Names = ["guard_machine_invocation", "guard_machine_invocation_history", "machine_invocation_grant",
        "machine_invocation_provenance", "write_invocation_grant", "read_invocation_grants", "claim_machine_invocation",
        "finish_machine_invocation", "purge_machine_invocation_results"];
    internal static readonly string[] GrantFunctions = ["write_invocation_grant", "read_invocation_grants"];
    internal static readonly string[] InvocationFunctions = ["claim_machine_invocation", "finish_machine_invocation", "purge_machine_invocation_results"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0024_machine_invocation.sql", Names.Length));

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool grants, CancellationToken token)
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
                AND (SELECT count(DISTINCT p.proname) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='braid' AND p.proname=ANY(@functions) AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE'))=cardinality(@functions)
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("role", grants ? "braid_invocation_grant_writer" : "braid_invocation_writer");
            command.Parameters.AddWithValue("functions", grants ? GrantFunctions : operations ? [.. InvocationFunctions, "record_invocation_metadata"] : InvocationFunctions);
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await ValidateContractAsync(connection, transaction, token);
    }

    internal static async Task ValidateContractAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (await LifecycleCatalogAdmission.ValidateIfInstalledAsync(connection, transaction, token)) return;
        // The grant and claim functions read the release pointer and its qualified build; admit that contract unchanged.
        await QualificationRoleGuard.ValidateContractAsync(connection, transaction, token);
        await LocalReleaseRoleGuard.ValidateFunctionsAsync(connection, transaction, await LocalReleaseRoleGuard.ReplicasInstalledAsync(connection, transaction, token), token);
        if (await ReleaseOperationsRoleGuard.InstalledAsync(connection, transaction, token))
            await ReleaseOperationsRoleGuard.ValidateAsync(connection, transaction, token);
        else
        {
            if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, LocalReleaseRoleGuard.Tables, token) != LocalReleaseRoleGuard.Fingerprint)
                throw Invalid();
            await ValidateLedgerContractAsync(connection, transaction, token);
        }
    }

    // Schema and function admission only: callers such as the operations reader have
    // their own login contract. Keep this leaf independent of release/operations guards.
    internal static async Task ValidateLedgerContractAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var definitions = Definitions.Value;
        await using (var installed = new NpgsqlCommand("SELECT to_regclass('braid.administration_state') IS NOT NULL", connection, transaction))
            if (await installed.ExecuteScalarAsync(token) is true)
            {
                definitions = new(definitions);
                definitions["guard_machine_invocation"] = BuildRoleGuard.LoadFunctions("0027_administration.sql", AdministrationRoleGuard.Names.Length)["guard_machine_invocation"];
            }
        await ValidateOrderContractAsync(connection, transaction, definitions, token);
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token) != Fingerprint) throw Invalid();
    }

    internal static async Task ValidateOrderContractAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Dictionary<string, Match> definitions, CancellationToken token)
    {
        await using var installed = new NpgsqlCommand("SELECT to_regclass('braid.host_order_pins') IS NOT NULL", connection, transaction);
        if (await installed.ExecuteScalarAsync(token) is true)
        {
            var host = BuildRoleGuard.LoadFunctions("0048_host_orders.sql", 3);
            definitions = new(definitions)
            {
                ["claim_machine_attempt"] = definitions["claim_machine_invocation"],
                ["finish_machine_attempt"] = definitions["finish_machine_invocation"],
                ["claim_machine_invocation"] = host["claim_machine_invocation"],
                ["finish_machine_invocation"] = host["finish_machine_invocation"],
                ["guard_host_order"] = host["guard_host_order"]
            };
            var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, HostTables, token);
            if (fingerprint != "a6051dbac0b52c929038326588072df8418f51548f003dec6499ed6203127861") throw Invalid();
        }
        if (await TriggerRoleGuard.InstalledAsync(connection, transaction, token))
        {
            definitions = new(definitions) { ["guard_machine_invocation"] = TriggerRoleGuard.LedgerGuard, ["guard_host_order"] = TriggerRoleGuard.HostGuard };
            await TriggerRoleGuard.ValidateSchemaAsync(connection, transaction, token);
        }
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, definitions.Keys.ToArray(), definitions, token);
    }

    private static InvalidOperationException Invalid() => new("Machine invocation database contract unavailable.");
}
