using Npgsql;
using NpgsqlTypes;

namespace Weft.Persistence.Postgres;

// Admission is fresh on every checkout, including no-op/replay calls. It is not
// a grant/owner commit fence against an administrator changing ACLs mid-command.
internal static class ChatRoleGuard
{
    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ChatDatabaseProfile profile, bool readOnly, CancellationToken ct)
    {
        await using (var prepare = new NpgsqlCommand("""
            SET LOCAL search_path=pg_catalog;
            SET LOCAL TimeZone='UTC';
            SET LOCAL DateStyle='ISO, YMD';
            SET LOCAL IntervalStyle='postgres'
            """, connection, transaction))
            await prepare.ExecuteNonQueryAsync(ct);
        await using (var environment = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=$1
                AND current_setting('transaction_read_only')=$2
                AND current_setting('transaction_isolation')='read committed'
                AND current_setting('session_replication_role')='origin' AND current_setting('row_security')='on'
                AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on' AND current_setting('TimeZone')='UTC'
                AND current_setting('DateStyle')='ISO, YMD' AND current_setting('IntervalStyle')='postgres'
                AND current_setting('lock_timeout')='5s' AND current_setting('statement_timeout')='15s'
                AND COALESCE(current_setting('weft.organization_id',true)
                    ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',false)
                AND COALESCE(current_setting('weft.chat_principal_id',true)
                    ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',false)
                AND current_setting('weft.organization_id')<>'00000000-0000-0000-0000-000000000000'
                AND current_setting('weft.chat_principal_id')<>'00000000-0000-0000-0000-000000000000'
            """, connection, transaction))
        {
            environment.Parameters.AddWithValue(RoleName(profile));
            environment.Parameters.AddWithValue(readOnly ? "on" : "off");
            if (await environment.ExecuteScalarAsync(ct) is not true) throw Refusal();
        }
        await ValidateInstallationAsync(connection, transaction, profile, ct);
    }

    internal static async Task ValidateInstallationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ChatDatabaseProfile profile, CancellationToken ct)
    {
        // Cooperative migration fence uses the existing runner's database-local key.
        // Read-only transactions can hold this shared transaction advisory lock.
        await using (var migration = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock_shared(6287392987644524871)", connection, transaction))
            await migration.ExecuteNonQueryAsync(ct);
        await ValidateProfileAsync(connection, transaction, ChatDatabaseProfile.Reader, ct);
        await ValidateProfileAsync(connection, transaction, ChatDatabaseProfile.Writer, ct);
        await ValidateProfileAsync(connection, transaction, ChatDatabaseProfile.Lifecycle, ct);
        await ValidateProfileAsync(connection, transaction, ChatDatabaseProfile.Approver, ct);
        await ChatSchemaContract.ValidateAsync(connection, transaction, profile, ct);
        // Relation acquisition may have waited. Do not reuse a pre-wait ACL
        // observation for the operation admitted after that wait.
        await ValidateProfileAsync(connection, transaction, ChatDatabaseProfile.Reader, ct);
        await ValidateProfileAsync(connection, transaction, ChatDatabaseProfile.Writer, ct);
        await ValidateProfileAsync(connection, transaction, ChatDatabaseProfile.Lifecycle, ct);
        await ValidateProfileAsync(connection, transaction, ChatDatabaseProfile.Approver, ct);
    }

    private static async Task ValidateProfileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ChatDatabaseProfile profile, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            WITH target AS (SELECT * FROM pg_catalog.pg_roles WHERE rolname=$1),
            application_relations AS (
                SELECT r.*,n.nspname FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
                WHERE pg_catalog.left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema' AND r.relkind IN ('r','p','v','m','f')
            ), required AS (
                SELECT split_part(x,'|',1) relation_name,split_part(x,'|',2) column_name,split_part(x,'|',3) privilege_type
                FROM unnest($2::text[]) x
            ), actual AS (
                SELECT r.nspname,r.relname::text relation_name,c.attname::text column_name,a.*
                FROM application_relations r JOIN pg_catalog.pg_attribute c ON c.attrelid=r.oid AND c.attnum>0
                CROSS JOIN LATERAL pg_catalog.aclexplode(c.attacl) a,target role WHERE a.grantee=role.oid
            )
            SELECT count(*)=1 AND COALESCE(bool_and(
                role.rolcanlogin AND NOT role.rolinherit AND NOT role.rolsuper AND NOT role.rolbypassrls
                AND NOT role.rolcreatedb AND NOT role.rolcreaterole AND NOT role.rolreplication AND role.rolconfig IS NULL AND role.rolvaliduntil IS NULL
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_db_role_setting s WHERE s.setrole=role.oid)
                AND pg_catalog.has_database_privilege(role.oid,current_database(),'CONNECT')
                AND NOT pg_catalog.has_database_privilege(role.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT pg_catalog.has_database_privilege('public',current_database(),'CONNECT,CREATE,TEMPORARY')
                AND (SELECT datdba=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname='weft_migrator') FROM pg_catalog.pg_database WHERE datname=current_database())
                AND pg_catalog.has_schema_privilege(role.oid,'weft','USAGE')
                AND NOT pg_catalog.has_schema_privilege(role.oid,'weft','USAGE WITH GRANT OPTION')
                AND NOT pg_catalog.has_schema_privilege('public','weft','USAGE,CREATE')
                AND NOT pg_catalog.has_parameter_privilege(role.oid,'session_replication_role','SET,ALTER SYSTEM')
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=role.oid OR m.roleid=role.oid)
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE pg_catalog.left(n.nspname,8)<>'pg_temp_'
                    AND pg_catalog.left(n.nspname,14)<>'pg_toast_temp_' AND (n.nspowner=role.oid OR pg_catalog.has_schema_privilege(role.oid,n.oid,'CREATE')))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE pg_catalog.left(n.nspname,3)<>'pg_'
                    AND n.nspname NOT IN ('information_schema','public','weft') AND pg_catalog.has_schema_privilege(role.oid,n.oid,'USAGE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n
                    CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) a WHERE a.grantee=role.oid
                    AND (n.nspname<>'weft' OR a.privilege_type<>'USAGE' OR a.is_grantable OR a.grantor<>n.nspowner))
                AND (SELECT count(*)=1 FROM pg_catalog.pg_namespace n
                    CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) a WHERE n.nspname='weft' AND a.grantee=role.oid)
                AND NOT EXISTS(SELECT 1 FROM application_relations r WHERE r.relowner=role.oid
                    OR pg_catalog.has_table_privilege(role.oid,r.oid,'SELECT,INSERT,UPDATE,TRUNCATE,REFERENCES,TRIGGER')
                    OR (pg_catalog.has_table_privilege(role.oid,r.oid,'DELETE') AND NOT ($3 AND r.nspname='weft' AND r.relname='chat_conversations')))
                AND ($3=pg_catalog.has_table_privilege(role.oid,'weft.chat_conversations','DELETE'))
                AND NOT EXISTS(SELECT 1 FROM actual a WHERE a.nspname<>'weft' OR a.is_grantable
                    OR a.grantor<>(SELECT relowner FROM application_relations r WHERE r.nspname=a.nspname AND r.relname=a.relation_name))
                AND NOT EXISTS(SELECT relation_name,column_name,privilege_type FROM actual EXCEPT SELECT * FROM required)
                AND NOT EXISTS(SELECT * FROM required EXCEPT SELECT relation_name,column_name,privilege_type FROM actual)
                AND NOT EXISTS(SELECT 1 FROM application_relations r JOIN pg_catalog.pg_attribute c ON c.attrelid=r.oid
                    WHERE c.attnum>0 AND NOT c.attisdropped AND (
                        pg_catalog.has_column_privilege(role.oid,r.oid,c.attnum,'REFERENCES,SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION')
                        OR EXISTS(SELECT 1 FROM unnest(ARRAY['SELECT','INSERT','UPDATE']) p WHERE
                            pg_catalog.has_column_privilege(role.oid,r.oid,c.attnum,p) AND NOT EXISTS(
                                SELECT 1 FROM required x WHERE r.nspname='weft' AND x.relation_name=r.relname AND x.column_name=c.attname AND x.privilege_type=p))))
                AND NOT EXISTS(SELECT 1 FROM application_relations r WHERE
                    pg_catalog.has_table_privilege('public',r.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR pg_catalog.has_any_column_privilege('public',r.oid,'SELECT,INSERT,UPDATE,REFERENCES'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
                    WHERE pg_catalog.left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema' AND (f.proowner=role.oid
                        OR pg_catalog.has_function_privilege('public',f.oid,'EXECUTE')
                        OR (pg_catalog.has_function_privilege(role.oid,f.oid,'EXECUTE')
                            AND NOT (f.oid=ANY($4::oid[])))))
                AND NOT EXISTS(SELECT 1 FROM unnest($4::oid[]) f WHERE NOT pg_catalog.has_function_privilege(role.oid,f,'EXECUTE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc f CROSS JOIN LATERAL pg_catalog.aclexplode(f.proacl) a
                    WHERE a.grantee=role.oid AND (NOT f.oid=ANY($4::oid[]) OR a.is_grantable OR a.grantor<>f.proowner OR a.privilege_type<>'EXECUTE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class s JOIN pg_catalog.pg_namespace n ON n.oid=s.relnamespace
                    WHERE pg_catalog.left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema' AND CASE WHEN s.relkind='S' THEN
                        (s.relowner=role.oid OR pg_catalog.has_sequence_privilege(role.oid,s.oid,'USAGE,SELECT,UPDATE')
                            OR pg_catalog.has_sequence_privilege('public',s.oid,'USAGE,SELECT,UPDATE')) ELSE false END)
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_default_acl d CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
                    WHERE d.defaclrole=role.oid OR a.grantee IN (0,role.oid))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_parameter_acl p CROSS JOIN LATERAL pg_catalog.aclexplode(p.paracl) a
                    WHERE a.grantee IN (0,role.oid))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_database d
                    CROSS JOIN LATERAL pg_catalog.aclexplode(d.datacl) a WHERE d.datname=current_database() AND a.grantee=role.oid
                        AND (a.privilege_type<>'CONNECT' OR a.is_grantable OR a.grantor<>d.datdba))
            ),false) FROM target role
            """, connection, transaction);
        command.Parameters.AddWithValue(RoleName(profile));
        command.Parameters.AddWithValue(ChatSchemaContract.ColumnGrants(profile));
        command.Parameters.AddWithValue(profile == ChatDatabaseProfile.Writer);
        // Resolve the source-declared exact routine signatures, never a name-prefix allowlist.
        var signatures = ChatSchemaContract.FunctionGrants(profile);
        await using var resolve = new NpgsqlCommand("SELECT array_agg(x::regprocedure::oid ORDER BY x) FROM unnest($1::text[]) x", connection, transaction);
        resolve.Parameters.AddWithValue(signatures);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Oid,
            Value = (uint[])(await resolve.ExecuteScalarAsync(ct) ?? throw Refusal())
        });
        if (await command.ExecuteScalarAsync(ct) is not true) throw Refusal();
    }

    internal static string RoleName(ChatDatabaseProfile profile) => profile switch
    {
        ChatDatabaseProfile.Reader => "weft_chat_reader",
        ChatDatabaseProfile.Writer => "weft_chat_writer",
        ChatDatabaseProfile.Lifecycle => "weft_chat_lifecycle",
        ChatDatabaseProfile.Approver => "weft_chat_approver",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
    private static InvalidOperationException Refusal() => new("Private chat requires its exact restricted profiles and accepted transaction environment.");
}
