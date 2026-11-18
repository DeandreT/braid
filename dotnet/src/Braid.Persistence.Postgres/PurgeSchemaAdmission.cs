using Npgsql;

namespace Braid.Persistence.Postgres;

internal static class PurgeSchemaAdmission
{
    internal static async Task ValidateWorkerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user='braid_lifecycle_worker' AND r.rolcanlogin AND NOT r.rolsuper
                AND NOT r.rolbypassrls AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND r.rolconfig IS NULL AND current_setting('row_security')='on'
                AND current_setting('session_replication_role')='origin' AND current_setting('transaction_isolation')='read committed'
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
            command.Parameters.AddWithValue("permitted", new[] { "claim_deletion_job", "advance_deletion_job", "claim_deletion_objects", "finish_deletion_object", "release_deletion_job", "sweep_retention", "sweep_retention_history", "purge_catalog_batch" });
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await LifecycleCatalogAdmission.ValidateAsync(connection, transaction, token);
    }

    private static InvalidOperationException Invalid() => new("Lifecycle worker database contract unavailable.");
}
