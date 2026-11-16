using Npgsql;

namespace Braid.Persistence.Postgres;

// The caller binds a previously empty trusted organization context before this fresh admission.
internal static class ServerSessionRoleGuard
{
    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ServerSessionDatabaseProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roleName = RoleName(profile);
        await using (var environment = new NpgsqlCommand("""
            SET LOCAL search_path=pg_catalog;
            SET LOCAL TimeZone='UTC';
            SET LOCAL DateStyle='ISO, YMD';
            SET LOCAL IntervalStyle='postgres'
            """, connection, transaction))
            await environment.ExecuteNonQueryAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=$1
                AND role.rolcanlogin AND NOT role.rolinherit AND NOT role.rolsuper AND NOT role.rolbypassrls
                AND NOT role.rolcreaterole AND NOT role.rolcreatedb AND NOT role.rolreplication
                AND current_setting('transaction_read_only')=$2
                AND current_setting('transaction_isolation')='read committed'
                AND current_setting('session_replication_role')='origin' AND current_setting('row_security')='on'
                AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on' AND current_setting('TimeZone')='UTC'
                AND current_setting('DateStyle')='ISO, YMD' AND current_setting('IntervalStyle')='postgres'
                AND current_setting('lock_timeout')='1s' AND current_setting('statement_timeout')='5s'
                AND COALESCE(CASE WHEN current_setting('braid.organization_id',true)
                    ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                    THEN current_setting('braid.organization_id',true)::uuid
                        <> '00000000-0000-0000-0000-000000000000'::uuid ELSE false END,false)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=role.oid OR m.roleid=role.oid)
            FROM pg_catalog.pg_roles role WHERE role.rolname=current_user
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(roleName);
            command.Parameters.AddWithValue(profile == ServerSessionDatabaseProfile.Reader ? "on" : "off");
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
                throw Refusal("Server sessions require the exact dedicated role and accepted transaction environment.");
        }
        // A profile cannot hide a broadened sibling profile merely because it does not use it.
        await ValidateProfileAsync(connection, transaction, ServerSessionDatabaseProfile.Reader, cancellationToken);
        await ValidateProfileAsync(connection, transaction, ServerSessionDatabaseProfile.Writer, cancellationToken);
        await ServerSessionSchemaContract.ValidateAsync(connection, transaction, cancellationToken);
    }

    private static async Task ValidateProfileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ServerSessionDatabaseProfile profile, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH target AS (
                SELECT * FROM pg_catalog.pg_roles WHERE rolname=$1
            ), application_relations AS (
                SELECT r.*,n.nspname FROM pg_catalog.pg_class r
                JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
                WHERE pg_catalog.left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema'
                    AND r.relkind IN ('r','p','v','m','f')
            ), required(table_name,column_name,can_insert,can_update) AS (
                VALUES
                ('server_sessions','handle_digest',$2::boolean,false),
                ('server_sessions','organization_id',$2::boolean,false),
                ('server_sessions','binding_id',$2::boolean,false),
                ('server_sessions','binding_version',$2::boolean,false),
                ('server_sessions','login_epoch',$2::boolean,false),
                ('server_sessions','issued_at',$2::boolean,false),
                ('server_sessions','idle_expires_at',$2::boolean,$2::boolean),
                ('server_sessions','absolute_expires_at',$2::boolean,false),
                ('server_sessions','version',$2::boolean,$2::boolean),
                ('server_sessions','revoked_at',$2::boolean,$2::boolean),
                ('server_sessions','policy_version',$2::boolean,false),
                ('server_sessions','codec_version',$2::boolean,false),
                ('server_sessions','protected_ticket',$2::boolean,false),
                ('external_identity_bindings','binding_id',false,false),
                ('external_identity_bindings','principal_id',false,false),
                ('external_identity_bindings','enabled',false,false),
                ('external_identity_bindings','version',false,false),
                ('principals','id',false,false),('principals','active',false,false)
                UNION ALL SELECT 'organizations','id',false,false WHERE $2::boolean
            )
            SELECT count(*)=1 AND COALESCE(bool_and(
                role.rolcanlogin AND NOT role.rolinherit AND NOT role.rolsuper AND NOT role.rolbypassrls
                AND NOT role.rolcreaterole AND NOT role.rolcreatedb AND NOT role.rolreplication
                AND pg_catalog.has_database_privilege(role.oid,current_database(),'CONNECT')
                AND NOT pg_catalog.has_database_privilege(role.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT pg_catalog.has_database_privilege('public',current_database(),'CONNECT,CREATE,TEMPORARY')
                AND pg_catalog.has_schema_privilege(role.oid,'braid','USAGE')
                AND NOT pg_catalog.has_schema_privilege(role.oid,'braid','USAGE WITH GRANT OPTION')
                AND NOT pg_catalog.has_schema_privilege('public','braid','USAGE,CREATE')
                AND NOT pg_catalog.has_parameter_privilege(role.oid,'session_replication_role','SET,ALTER SYSTEM')
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=role.oid OR m.roleid=role.oid)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n
                    WHERE pg_catalog.left(n.nspname,8)<>'pg_temp_' AND pg_catalog.left(n.nspname,14)<>'pg_toast_temp_'
                        AND (n.nspowner=role.oid OR pg_catalog.has_schema_privilege(role.oid,n.oid,'CREATE')))
                AND NOT EXISTS (SELECT 1 FROM application_relations r
                    WHERE r.relowner=role.oid
                        OR pg_catalog.has_table_privilege(role.oid,r.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'))
                AND NOT EXISTS (SELECT 1 FROM application_relations r
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid AND a.attnum>0
                    CROSS JOIN LATERAL pg_catalog.aclexplode(a.attacl) grant_row
                    WHERE grant_row.grantee=role.oid AND (grant_row.grantor<>r.relowner OR grant_row.is_grantable))
                AND NOT EXISTS (SELECT 1 FROM application_relations r
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid
                    WHERE a.attnum>0 AND NOT a.attisdropped AND (
                        pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,
                            'REFERENCES,SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION')
                        OR (pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,'SELECT') AND NOT EXISTS (
                            SELECT 1 FROM required x WHERE r.nspname='braid' AND x.table_name=r.relname AND x.column_name=a.attname))
                        OR (pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,'INSERT') AND NOT EXISTS (
                            SELECT 1 FROM required x WHERE r.nspname='braid' AND x.table_name=r.relname AND x.column_name=a.attname AND x.can_insert))
                        OR (pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,'UPDATE') AND NOT EXISTS (
                            SELECT 1 FROM required x WHERE r.nspname='braid' AND x.table_name=r.relname AND x.column_name=a.attname AND x.can_update))))
                AND NOT EXISTS (SELECT 1 FROM required x
                    LEFT JOIN application_relations r ON r.nspname='braid' AND r.relname=x.table_name
                    LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid AND a.attname=x.column_name AND a.attnum>0 AND NOT a.attisdropped
                    WHERE r.oid IS NULL OR a.attnum IS NULL
                        OR NOT pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,'SELECT')
                        OR (x.can_insert AND NOT pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,'INSERT'))
                        OR (x.can_update AND NOT pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,'UPDATE')))
                AND NOT EXISTS (SELECT 1 FROM application_relations r
                    WHERE pg_catalog.has_table_privilege('public',r.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege('public',r.oid,'SELECT,INSERT,UPDATE,REFERENCES'))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
                    WHERE n.nspname='braid' AND (f.proowner=role.oid
                        OR pg_catalog.has_function_privilege(role.oid,f.oid,'EXECUTE')
                        OR pg_catalog.has_function_privilege('public',f.oid,'EXECUTE')))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class s JOIN pg_catalog.pg_namespace n ON n.oid=s.relnamespace
                    WHERE pg_catalog.left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema' AND CASE WHEN s.relkind='S' THEN
                        (s.relowner=role.oid OR pg_catalog.has_sequence_privilege(role.oid,s.oid,'USAGE,SELECT,UPDATE')
                            OR pg_catalog.has_sequence_privilege('public',s.oid,'USAGE,SELECT,UPDATE')) ELSE false END)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_default_acl d
                    CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
                    WHERE d.defaclnamespace IN (0,(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='braid'))
                        AND (d.defaclrole=role.oid OR a.grantee IN (0,role.oid)))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_parameter_acl p
                    CROSS JOIN LATERAL pg_catalog.aclexplode(p.paracl) a WHERE a.grantee IN (0,role.oid))
            ),false) FROM target role
            """, connection, transaction);
        command.Parameters.AddWithValue(RoleName(profile));
        command.Parameters.AddWithValue(profile == ServerSessionDatabaseProfile.Writer);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Server sessions require the exact restricted reader and writer column profiles.");
    }

    private static string RoleName(ServerSessionDatabaseProfile profile) => profile switch
    {
        ServerSessionDatabaseProfile.Reader => "braid_session_reader",
        ServerSessionDatabaseProfile.Writer => "braid_session_writer",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    private static InvalidOperationException Refusal(string message) => new(message);
}
