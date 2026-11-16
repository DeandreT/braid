using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

internal static class BuildMetadataRoleGuard
{
    internal const string PublicationFingerprint = "0000025e2f9237b7fa7132f8deeea56a032cfb807dc68feacf4b27f2682dc924";
    internal const string RunFingerprint = "1625cabc1bea8d6837c9affba0cb18dcabf389a64abeb26b2ebfad01aa497b40";
    internal static readonly string[] Names = ["read_build_metadata"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0020_build_metadata.sql", Names.Length));

    internal static async Task<bool> IsInstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='braid' AND p.proname='read_build_metadata')
            """, connection, transaction);
        return await command.ExecuteScalarAsync(token) is true;
    }

    internal static async Task ValidateContractAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Names, Definitions.Value, token);
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, BuildRoleGuard.Tables, token) != PublicationFingerprint ||
            await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, BuildRunRoleGuard.Tables, token) != RunFingerprint) throw Invalid();
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user='braid_build_metadata_reader' AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
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
                    AND NOT (n.nspname='braid' AND p.proname='read_build_metadata'))
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        if (!await IsInstalledAsync(connection, transaction, token)) throw Invalid();
        await BuildRoleGuard.ValidateContractAsync(connection, transaction, token);
        // Metadata reads never fall back to a legacy contract after a partial schema rollback.
        await ValidateContractAsync(connection, transaction, token);
    }
    private static InvalidOperationException Invalid() => new("Build metadata database contract unavailable.");
}
