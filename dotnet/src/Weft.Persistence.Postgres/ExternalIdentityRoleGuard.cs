using Npgsql;

namespace Weft.Persistence.Postgres;

// Identity resolution observes global provider bindings and current internal
// principal activity. Organization/project authority remains in separate pools.
internal static class ExternalIdentityRoleGuard
{
    internal static async Task ValidateReaderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await PrepareEnvironmentAsync(connection, transaction, true, cancellationToken);
        await ValidateReaderProfileAsync(connection, transaction, cancellationToken);
        await ExternalIdentitySchemaContract.ValidateAsync(connection, transaction, false, cancellationToken);
    }

    internal static async Task ValidateOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await PrepareEnvironmentAsync(connection, transaction, false, cancellationToken);
        // The owner cannot bypass a broadened identity-reader profile merely
        // because its own credentials can inspect the same underlying sources.
        await ValidateReaderProfileAsync(connection, transaction, cancellationToken);
        await using (var protect = new NpgsqlCommand("LOCK TABLE ONLY weft.schema_migrations IN SHARE MODE", connection, transaction))
            await protect.ExecuteNonQueryAsync(cancellationToken);
        var expected = MigrationRunner.EmbeddedMigrations;
        if (expected.Count < 8 || expected[7].Version != 8 || expected[7].Checksum != ExternalIdentitySchemaContract.MigrationChecksum)
            throw Refusal("External identity ownership requires its exact current build migrations.");
        await using (var history = new NpgsqlCommand("SELECT version,checksum FROM weft.schema_migrations ORDER BY version", connection, transaction))
        await using (var reader = await history.ExecuteReaderAsync(cancellationToken))
        {
            var index = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (index >= expected.Count || reader.GetInt32(0) != expected[index].Version || reader.GetString(1) != expected[index].Checksum)
                    throw Refusal("External identity ownership requires the current exact migration history.");
                index++;
            }
            if (index != expected.Count)
                throw Refusal("External identity ownership requires the current complete migration history.");
        }
        await ExternalIdentitySchemaContract.ValidateAsync(connection, transaction, true, cancellationToken);
    }

    private static async Task PrepareEnvironmentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        bool reader, CancellationToken cancellationToken)
    {
        // Canonical deparsing must not depend on a caller's search path. This is
        // transaction-local and existing normalized pools reset on return.
        await using (var path = new NpgsqlCommand("SET LOCAL search_path=pg_catalog", connection, transaction))
            await path.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=$1
                AND role.rolcanlogin AND NOT role.rolinherit AND NOT role.rolsuper AND NOT role.rolbypassrls
                AND NOT role.rolcreaterole AND NOT role.rolcreatedb AND NOT role.rolreplication
                AND current_setting('transaction_read_only')=$2
                AND current_setting('transaction_isolation')='read committed'
                AND current_setting('session_replication_role')='origin' AND current_setting('row_security')='on'
                AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on'
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=role.oid OR m.roleid=role.oid)
            FROM pg_catalog.pg_roles role WHERE role.rolname=current_user
            """, connection, transaction);
        command.Parameters.AddWithValue(reader ? "weft_identity_reader" : "weft_migrator");
        command.Parameters.AddWithValue(reader ? "on" : "off");
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("External identity requires its dedicated role and accepted transaction environment.");
    }

    private static async Task ValidateReaderProfileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH target AS (
                SELECT * FROM pg_catalog.pg_roles WHERE rolname='weft_identity_reader'
            ), application_relations AS (
                SELECT r.* FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
                WHERE n.nspname='weft' AND r.relkind IN ('r','p','v','m','f')
            ), required(table_name,column_name) AS (VALUES
                ('external_identity_bindings','binding_id'),('external_identity_bindings','provider'),
                ('external_identity_bindings','authority'),('external_identity_bindings','subject'),
                ('external_identity_bindings','principal_id'),('external_identity_bindings','enabled'),
                ('external_identity_bindings','version'),('external_identity_bindings','created_at'),
                ('external_identity_bindings','disabled_at'),('principals','id'),('principals','active')
            )
            SELECT count(*)=1 AND COALESCE(bool_and(
                role.rolcanlogin AND NOT role.rolinherit AND NOT role.rolsuper AND NOT role.rolbypassrls
                AND NOT role.rolcreaterole AND NOT role.rolcreatedb AND NOT role.rolreplication
                AND pg_catalog.has_database_privilege(role.oid,current_database(),'CONNECT')
                AND NOT pg_catalog.has_database_privilege(role.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT pg_catalog.has_database_privilege('public',current_database(),'CONNECT,CREATE,TEMPORARY')
                AND pg_catalog.has_schema_privilege(role.oid,'weft','USAGE')
                AND NOT pg_catalog.has_schema_privilege(role.oid,'weft','USAGE WITH GRANT OPTION')
                AND NOT pg_catalog.has_schema_privilege('public','weft','USAGE,CREATE')
                AND NOT pg_catalog.has_parameter_privilege(role.oid,'session_replication_role','SET,ALTER SYSTEM')
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=role.oid OR m.roleid=role.oid)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n
                    WHERE n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
                      AND (n.nspowner=role.oid OR pg_catalog.has_schema_privilege(role.oid,n.oid,'CREATE')))
                AND NOT EXISTS (SELECT 1 FROM application_relations r
                    WHERE r.relowner=role.oid
                      OR pg_catalog.has_table_privilege(role.oid,r.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'))
                AND NOT EXISTS (SELECT 1 FROM application_relations r
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid
                    WHERE a.attnum>0 AND NOT a.attisdropped
                      AND (pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,'INSERT,UPDATE,REFERENCES,SELECT WITH GRANT OPTION')
                        OR (pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,'SELECT')
                          AND NOT EXISTS (SELECT 1 FROM required x WHERE x.table_name=r.relname AND x.column_name=a.attname))))
                AND NOT EXISTS (SELECT 1 FROM required x LEFT JOIN application_relations r ON r.relname=x.table_name
                    LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid AND a.attname=x.column_name AND a.attnum>0 AND NOT a.attisdropped
                    WHERE r.oid IS NULL OR a.attnum IS NULL OR NOT pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,'SELECT'))
                AND NOT EXISTS (SELECT 1 FROM application_relations r
                    WHERE pg_catalog.has_table_privilege('public',r.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                      OR pg_catalog.has_any_column_privilege('public',r.oid,'SELECT,INSERT,UPDATE,REFERENCES'))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
                    WHERE n.nspname='weft' AND (f.proowner=role.oid
                      OR pg_catalog.has_function_privilege(role.oid,f.oid,'EXECUTE')
                      OR pg_catalog.has_function_privilege('public',f.oid,'EXECUTE')))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class s JOIN pg_catalog.pg_namespace n ON n.oid=s.relnamespace
                    WHERE n.nspname='weft' AND CASE WHEN s.relkind='S' THEN
                        (s.relowner=role.oid OR pg_catalog.has_sequence_privilege(role.oid,s.oid,'USAGE,SELECT,UPDATE')
                          OR pg_catalog.has_sequence_privilege('public',s.oid,'USAGE,SELECT,UPDATE')) ELSE false END)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_default_acl d
                    CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
                    WHERE d.defaclnamespace IN (0,(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='weft'))
                      AND (d.defaclrole=role.oid OR a.grantee IN (0,role.oid)))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_parameter_acl p
                    CROSS JOIN LATERAL pg_catalog.aclexplode(p.paracl) a WHERE a.grantee IN (0,role.oid))
            ),false) FROM target role
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("External identity requires the exact least-privileged identity reader and column grants.");
    }

    private static InvalidOperationException Refusal(string message) => new(message);
}
