using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class MaintenanceRoleGuard
{
    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand("""
            WITH maintenance AS (
                SELECT role.* FROM pg_catalog.pg_roles role WHERE role.rolname = 'weft_maintenance'
            ), reachable_roles AS (
                SELECT role.* FROM pg_catalog.pg_roles role
                WHERE role.rolname = session_user OR pg_catalog.pg_has_role(session_user, role.oid, 'MEMBER')
            ), application_relations AS (
                SELECT relation.* FROM pg_catalog.pg_class relation
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = 'weft' AND relation.relkind IN ('r','p','v','m','f')
            )
            SELECT current_user = session_user AND current_user = 'weft_maintenance'
              AND (SELECT count(*) FROM maintenance) = 1
              AND (SELECT count(*) FROM reachable_roles) = 1
              AND (SELECT rolcanlogin AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls
                          AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolreplication FROM maintenance)
              AND pg_catalog.current_setting('transaction_read_only') = 'off'
              AND pg_catalog.current_setting('transaction_isolation') = 'read committed'
              AND pg_catalog.current_setting('session_replication_role') = 'origin'
              AND pg_catalog.current_setting('row_security') = 'on'
              AND pg_catalog.current_setting('server_encoding') = 'UTF8'
              AND pg_catalog.current_setting('client_encoding') = 'UTF8'
              AND pg_catalog.current_setting('standard_conforming_strings') = 'on'
              AND NULLIF(pg_catalog.current_setting('weft.organization_id', true), '') IS NULL
              AND NULLIF(pg_catalog.current_setting('weft.project_id', true), '') IS NULL
              AND pg_catalog.has_database_privilege(pg_catalog.current_database(), 'CONNECT')
              AND NOT pg_catalog.has_database_privilege(pg_catalog.current_database(), 'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
              AND NOT pg_catalog.has_database_privilege('public', pg_catalog.current_database(), 'CONNECT,CREATE,TEMPORARY')
              AND pg_catalog.has_schema_privilege('weft', 'USAGE')
              AND NOT pg_catalog.has_schema_privilege('weft', 'CREATE,USAGE WITH GRANT OPTION')
              AND NOT pg_catalog.has_schema_privilege('public', 'weft', 'USAGE,CREATE')
              AND (SELECT owner.rolname = 'weft_migrator' FROM pg_catalog.pg_namespace namespace
                   JOIN pg_catalog.pg_roles owner ON owner.oid = namespace.nspowner WHERE namespace.nspname = 'weft')
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_auth_members membership
                  WHERE membership.member IN (SELECT oid FROM reachable_roles)
                     OR membership.roleid IN (SELECT oid FROM reachable_roles)
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_shdepend dependency
                  WHERE dependency.refclassid = 'pg_catalog.pg_authid'::regclass AND dependency.deptype = 'o'
                    AND dependency.refobjid IN (SELECT oid FROM reachable_roles)
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_namespace namespace CROSS JOIN reachable_roles role
                  WHERE namespace.nspname NOT LIKE 'pg_temp_%' AND namespace.nspname NOT LIKE 'pg_toast_temp_%'
                    AND (namespace.nspowner = role.oid OR pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'CREATE')
                         OR pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'USAGE WITH GRANT OPTION'))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM application_relations relation CROSS JOIN reachable_roles role
                  WHERE pg_catalog.has_table_privilege(role.oid, relation.oid,
                            'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,SELECT WITH GRANT OPTION')
                     OR (NOT relation.relname = ANY($1::text[]) AND pg_catalog.has_table_privilege(role.oid, relation.oid, 'SELECT'))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM application_relations relation JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
                  CROSS JOIN reachable_roles role
                  WHERE attribute.attnum > 0 AND NOT attribute.attisdropped
                    AND (pg_catalog.has_column_privilege(role.oid, relation.oid, attribute.attnum,
                             'INSERT,UPDATE,REFERENCES,SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION,REFERENCES WITH GRANT OPTION')
                         OR (NOT relation.relname = ANY($1::text[]) AND pg_catalog.has_column_privilege(role.oid, relation.oid, attribute.attnum, 'SELECT')))
              )
              AND (SELECT count(*) FROM application_relations relation
                   WHERE relation.relkind = 'r' AND relation.relname = ANY($1::text[])
                     AND pg_catalog.has_table_privilege(relation.oid, 'SELECT')) = pg_catalog.cardinality($1::text[])
              AND NOT EXISTS (
                  SELECT 1 FROM application_relations relation
                  WHERE pg_catalog.has_table_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                     OR pg_catalog.has_any_column_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
                  WHERE namespace.nspname = 'weft'
                    AND (pg_catalog.has_function_privilege('public', function.oid, 'EXECUTE')
                         OR pg_catalog.has_function_privilege(function.oid, 'EXECUTE WITH GRANT OPTION')
                         OR (pg_catalog.has_function_privilege(function.oid, 'EXECUTE') AND NOT function.proname = ANY($2::text[])))
              )
              AND (SELECT count(*) FROM pg_catalog.pg_proc function
                   JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
                   WHERE namespace.nspname = 'weft' AND function.proname = ANY($2::text[])
                     AND pg_catalog.has_function_privilege(function.oid, 'EXECUTE')) = pg_catalog.cardinality($2::text[])
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_class sequence JOIN pg_catalog.pg_namespace namespace ON namespace.oid = sequence.relnamespace
                  WHERE namespace.nspname = 'weft' AND sequence.relkind = 'S'
                    AND CASE WHEN sequence.relkind = 'S' THEN
                        (pg_catalog.has_sequence_privilege('public', sequence.oid, 'USAGE,SELECT,UPDATE')
                         OR pg_catalog.has_sequence_privilege(sequence.oid, 'USAGE,SELECT,UPDATE,USAGE WITH GRANT OPTION,SELECT WITH GRANT OPTION,UPDATE WITH GRANT OPTION'))
                        ELSE false END
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_default_acl defaults
                  CROSS JOIN LATERAL pg_catalog.aclexplode(defaults.defaclacl) grant_entry
                  WHERE defaults.defaclnamespace IN (0, (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = 'weft'))
                    AND (defaults.defaclrole IN (SELECT oid FROM reachable_roles)
                         OR grant_entry.grantee = 0 OR grant_entry.grantee IN (SELECT oid FROM reachable_roles))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_parameter_acl parameter_acl
                  CROSS JOIN LATERAL pg_catalog.aclexplode(parameter_acl.paracl) grant_entry
                  WHERE grant_entry.grantee = 0 OR grant_entry.grantee IN (SELECT oid FROM reachable_roles)
              )
              AND NOT pg_catalog.has_parameter_privilege('session_replication_role', 'SET,ALTER SYSTEM')
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(MaintenanceSchemaContract.ReadTables);
            command.Parameters.AddWithValue(MaintenanceSchemaContract.EntryFunctionNames);
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException("Maintenance metadata requires its dedicated login, unbound scoped transaction and exact restricted privileges.");
        }

        await SupplementalMaintenanceSchemaAdmission.ValidateAsync(connection, transaction, cancellationToken);
    }
}
