using System.Text.RegularExpressions;
using Npgsql;

namespace Weft.Persistence.Postgres;

public enum BuildRunDatabaseProfile { Reader, Writer, Dispatcher }

internal static class BuildRunRoleGuard
{
    // Populated only from the schema-only capture of an actual fresh schema19 installation.
    internal const string PublicationFingerprint = "ea508b213711db2549431e0dba351b813c6c2a438e3771d72c7befeb430ca375";
    internal const string RunFingerprint = "040ec2c5fe79930c69d543df1972463b79ca876dd9d80c783224a04d7c0dfafe";
    internal static readonly string[] Tables = ["build_runs"];
    internal static readonly string[] Names = ["lock_build_run_authority", "guard_build_run", "guard_build_run_publication",
        "read_build_run", "write_build_run", "claim_build_run", "transition_build_run", "discover_build_runs"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0019_build_dispatch.sql", Names.Length));

    internal static async Task<bool> IsInstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='weft' AND c.relname='build_runs')
                OR EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='weft' AND p.proname=ANY(@names))
            """, connection, transaction);
        command.Parameters.AddWithValue("names", Names);
        return await command.ExecuteScalarAsync(token) is true;
    }

    internal static async Task ValidateContractAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Names, Definitions.Value, token);
        var metadata = await BuildMetadataRoleGuard.IsInstalledAsync(connection, transaction, token);
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token) !=
            (metadata ? BuildMetadataRoleGuard.RunFingerprint : RunFingerprint)) throw Invalid();
        if (metadata) await BuildMetadataRoleGuard.ValidateContractAsync(connection, transaction, token);
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, BuildRunDatabaseProfile profile, CancellationToken token)
    {
        var (role, functions) = profile switch
        {
            BuildRunDatabaseProfile.Reader => ("weft_build_run_reader", new[] { "read_build_run" }),
            BuildRunDatabaseProfile.Writer => ("weft_build_run_writer", new[] { "read_build_run", "write_build_run" }),
            BuildRunDatabaseProfile.Dispatcher => ("weft_build_dispatcher", new[] { "discover_build_runs", "claim_build_run", "transition_build_run" }),
            _ => throw Invalid()
        };
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
                AND NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')
                    AND NOT (n.nspname='weft' AND p.proname=ANY(@functions)))
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("role", role);
            command.Parameters.AddWithValue("functions", functions);
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        if (!await IsInstalledAsync(connection, transaction, token)) throw Invalid();
        await BuildRoleGuard.ValidateContractAsync(connection, transaction, token);
    }

    private static InvalidOperationException Invalid() => new("Build run database contract unavailable.");
}
