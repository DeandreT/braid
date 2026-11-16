using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>Admits the API audit login and the exact 0025 contract before each audit write.</summary>
internal static class ApiRequestAuditRoleGuard
{
    internal const string Role = "braid_api_audit_writer";
    internal const string Fingerprint = "4adbaf7fe9eadb0cdb61ff0de16cfc182236ef1750ba56a4d1c26bff07b895cf";
    internal static readonly string[] Tables = ["api_request_audit"];
    internal static readonly string[] Names = ["guard_api_request_audit", "record_api_request"];
    internal static readonly string[] WriterFunctions = ["record_api_request"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0025_api_request_audit.sql", Names.Length));

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=@role AND r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL
                AND current_setting('transaction_isolation')='read committed' AND current_setting('session_replication_role')='origin'
                AND current_setting('row_security')='on' AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on'
                AND coalesce(nullif(current_setting('braid.organization_id',true),''),'')=''
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
            command.Parameters.AddWithValue("functions", WriterFunctions);
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Names, Definitions.Value, token);
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token) != Fingerprint) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("API request audit database contract unavailable.");
}
