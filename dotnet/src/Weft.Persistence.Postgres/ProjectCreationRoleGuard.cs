using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class ProjectCreationRoleGuard
{
    // This same explicit-login profile can be checked by the trusted invoker
    // helper while its effective role is the definer owner. It reads catalogs only.
    internal const string RoleProfileSql = """
        WITH creator AS (
            SELECT role.* FROM pg_catalog.pg_roles role WHERE role.rolname = 'weft_project_creator'
        ), reachable_roles AS (
            SELECT role.* FROM pg_catalog.pg_roles role
            WHERE role.rolname = 'weft_project_creator'
               OR pg_catalog.pg_has_role('weft_project_creator', role.oid, 'MEMBER')
        ), application_relations AS (
            SELECT relation.* FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'weft' AND relation.relkind IN ('r','p','v','m','f')
        ), expected_functions(signature, creator_execute) AS (
            VALUES ('weft.get_project_creation_admission_v1(uuid,uuid)', true),
                   ('weft.create_project_v1(uuid,uuid,uuid,text,text)', true),
                   ('weft.assert_project_creation_contract_v1()', false),
                   ('weft.project_creation_fingerprint_v1(uuid,uuid,uuid,text,text)', false),
                   ('weft.guard_project_creations_transition()', false)
        ), installed_functions AS (
            SELECT expected.signature, expected.creator_execute, function.* FROM expected_functions expected
            LEFT JOIN pg_catalog.pg_proc function ON function.oid = pg_catalog.to_regprocedure(expected.signature)
        )
        SELECT (SELECT count(*) FROM creator) = 1
          AND (SELECT count(*) FROM reachable_roles) = 1
          AND (SELECT rolcanlogin AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls
                      AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolreplication FROM creator)
          AND pg_catalog.has_database_privilege('weft_project_creator', current_database(), 'CONNECT')
          AND NOT pg_catalog.has_database_privilege('public', current_database(), 'CONNECT,CREATE,TEMPORARY')
          AND pg_catalog.has_schema_privilege('weft_project_creator', 'weft', 'USAGE')
          AND NOT pg_catalog.has_schema_privilege('public', 'weft', 'USAGE,CREATE')
          AND (SELECT owner.rolname = 'weft_migrator' FROM pg_catalog.pg_namespace namespace
               JOIN pg_catalog.pg_roles owner ON owner.oid = namespace.nspowner WHERE namespace.nspname = 'weft')
          AND NOT EXISTS (
              SELECT 1 FROM reachable_roles role
              WHERE role.rolsuper OR role.rolbypassrls OR role.rolcreaterole OR role.rolcreatedb OR role.rolreplication
                OR role.rolname IN ('weft_migrator','weft_runtime','weft_ingest','weft_catalog_reader','weft_catalog_editor',
                                   'pg_read_all_data','pg_write_all_data','pg_read_server_files','pg_write_server_files',
                                   'pg_execute_server_program','pg_signal_backend','pg_maintain')
                OR pg_catalog.has_database_privilege(role.oid, current_database(), 'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                OR pg_catalog.has_parameter_privilege(role.oid, 'session_replication_role', 'SET,ALTER SYSTEM')
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_auth_members membership
              WHERE membership.member IN (SELECT oid FROM reachable_roles)
                 OR membership.roleid IN (SELECT oid FROM reachable_roles)
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_database database_row CROSS JOIN reachable_roles role
              WHERE database_row.datdba = role.oid
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_namespace namespace CROSS JOIN reachable_roles role
              WHERE namespace.nspname NOT LIKE 'pg_temp_%' AND namespace.nspname NOT LIKE 'pg_toast_temp_%'
                AND (namespace.nspowner = role.oid OR pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'CREATE')
                     OR pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'USAGE WITH GRANT OPTION'))
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_class relation CROSS JOIN reachable_roles role WHERE relation.relowner = role.oid
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_proc function CROSS JOIN reachable_roles role WHERE function.proowner = role.oid
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_type type_row CROSS JOIN reachable_roles role WHERE type_row.typowner = role.oid
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_shdepend dependency
              WHERE dependency.refclassid = 'pg_catalog.pg_authid'::regclass AND dependency.deptype = 'o'
                AND dependency.refobjid IN (SELECT oid FROM reachable_roles)
          )
          AND NOT EXISTS (
              SELECT 1 FROM application_relations relation CROSS JOIN reachable_roles role
              WHERE pg_catalog.has_table_privilege(role.oid, relation.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                 OR pg_catalog.has_any_column_privilege(role.oid, relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
          )
          AND NOT EXISTS (
              SELECT 1 FROM application_relations relation
              WHERE pg_catalog.has_table_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                 OR pg_catalog.has_any_column_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_class sequence JOIN pg_catalog.pg_namespace namespace ON namespace.oid = sequence.relnamespace
              WHERE namespace.nspname = 'weft' AND sequence.relkind = 'S'
                AND CASE WHEN sequence.relkind = 'S' THEN
                    (pg_catalog.has_sequence_privilege('public', sequence.oid, 'USAGE,SELECT,UPDATE')
                     OR EXISTS (SELECT 1 FROM reachable_roles role
                                WHERE pg_catalog.has_sequence_privilege(role.oid, sequence.oid, 'USAGE,SELECT,UPDATE')))
                    ELSE false END
          )
          AND (SELECT count(*) FROM installed_functions WHERE oid IS NOT NULL) = 5
          AND (SELECT count(*) FROM installed_functions
               WHERE creator_execute AND pg_catalog.has_function_privilege('weft_project_creator', oid, 'EXECUTE')) = 2
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
              WHERE namespace.nspname = 'weft'
                AND (pg_catalog.has_function_privilege('public', function.oid, 'EXECUTE')
                     OR EXISTS (SELECT 1 FROM reachable_roles role
                                WHERE pg_catalog.has_function_privilege(role.oid, function.oid, 'EXECUTE WITH GRANT OPTION')
                                   OR (pg_catalog.has_function_privilege(role.oid, function.oid, 'EXECUTE')
                                       AND NOT EXISTS (SELECT 1 FROM installed_functions accepted
                                                       WHERE accepted.oid = function.oid AND accepted.creator_execute))))
          )
          AND NOT EXISTS (
              SELECT 1 FROM installed_functions function CROSS JOIN pg_catalog.pg_roles old_role
              WHERE old_role.rolname IN ('weft_runtime','weft_ingest','weft_catalog_reader','weft_catalog_editor')
                AND pg_catalog.has_function_privilege(old_role.oid, function.oid, 'EXECUTE')
          )
          AND NOT EXISTS (
              SELECT 1 FROM installed_functions function
              CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(function.proacl, pg_catalog.acldefault('f', function.proowner))) grant_entry
              WHERE grant_entry.grantor <> function.proowner OR grant_entry.privilege_type <> 'EXECUTE' OR grant_entry.is_grantable
                 OR NOT (grant_entry.grantee = function.proowner
                         OR (function.creator_execute AND grant_entry.grantee = (SELECT oid FROM creator)))
          )
          AND NOT EXISTS (
              SELECT 1 FROM installed_functions function
              WHERE (SELECT count(*) FROM pg_catalog.aclexplode(COALESCE(function.proacl, pg_catalog.acldefault('f', function.proowner))))
                    <> CASE WHEN function.creator_execute THEN 2 ELSE 1 END
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
        """;

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var environment = new NpgsqlCommand("""
            SELECT current_user = session_user AND current_user = 'weft_project_creator'
              AND current_setting('transaction_read_only') = 'off'
              AND current_setting('transaction_isolation') = 'read committed'
              AND current_setting('session_replication_role') = 'origin'
              AND current_setting('row_security') = 'on'
              AND current_setting('server_encoding') = 'UTF8'
              AND current_setting('client_encoding') = 'UTF8'
              AND current_setting('standard_conforming_strings') = 'on'
            """, connection, transaction))
        {
            if (await environment.ExecuteScalarAsync(cancellationToken) is not true)
                throw Refusal("Project creation requires the dedicated creator and accepted transaction environment.");
        }
        await using (var profile = new NpgsqlCommand(RoleProfileSql, connection, transaction))
        {
            if (await profile.ExecuteScalarAsync(cancellationToken) is not true)
                throw Refusal("Project creation requires the exact execute-only creator privilege profile.");
        }

        // Function/role/ACL checks are admission checks, not commit fences. The
        // owner-only helper takes source relation locks inside each entrypoint;
        // the creator is deliberately not granted SELECT merely to take locks.
        foreach (var function in ProjectCreationSchemaContract.Functions.Values)
            await ValidateFunctionAsync(connection, transaction, function, cancellationToken);
        await ValidateTransitionAsync(connection, transaction, cancellationToken);
    }

    private static async Task ValidateFunctionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ProjectCreationSchemaContract.Function expected, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT namespace.nspname, owner.rolname, language.lanname, function.prokind::text,
                   function.prosecdef, function.provolatile::text, function.proparallel::text,
                   function.proisstrict, function.proleakproof, function.proretset,
                   pg_catalog.format_type(function.prorettype, NULL), function.pronargs,
                   function.pronargdefaults, function.proargdefaults IS NULL, function.provariadic = 0,
                   function.prosupport = 0, function.proconfig, function.prosrc,
                   ARRAY(SELECT pg_catalog.format_type(argument.type_oid, NULL)
                         FROM unnest(function.proargtypes::oid[]) WITH ORDINALITY argument(type_oid, ordinal)
                         ORDER BY argument.ordinal),
                   function.proallargtypes IS NULL, function.proargmodes IS NULL, function.proargnames IS NULL,
                   ARRAY(SELECT pg_catalog.format_type(argument.type_oid, NULL)
                         FROM unnest(function.proallargtypes) WITH ORDINALITY argument(type_oid, ordinal)
                         ORDER BY argument.ordinal),
                   function.proargmodes::text[], function.proargnames,
                   return_namespace.nspname = 'pg_catalog',
                   NOT EXISTS (
                       SELECT 1 FROM unnest(COALESCE(function.proallargtypes, function.proargtypes::oid[])) argument(type_oid)
                       JOIN pg_catalog.pg_type type_row ON type_row.oid = argument.type_oid
                       JOIN pg_catalog.pg_namespace type_namespace ON type_namespace.oid = type_row.typnamespace
                       WHERE type_namespace.nspname <> 'pg_catalog')
            FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid = function.proowner
            JOIN pg_catalog.pg_language language ON language.oid = function.prolang
            JOIN pg_catalog.pg_type return_type ON return_type.oid = function.prorettype
            JOIN pg_catalog.pg_namespace return_namespace ON return_namespace.oid = return_type.typnamespace
            WHERE namespace.nspname = 'weft' AND function.proname = $1
            """, connection, transaction);
        command.Parameters.AddWithValue(expected.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "weft" || reader.GetString(1) != "weft_migrator" ||
            reader.GetString(2) != "plpgsql" || reader.GetString(3) != "f" || reader.GetBoolean(4) != expected.SecurityDefiner ||
            reader.GetString(5) != expected.Volatility.ToString() || reader.GetString(6) != expected.Parallel.ToString() ||
            reader.GetBoolean(7) != expected.Strict || reader.GetBoolean(8) || reader.GetBoolean(9) != expected.ReturnsSet ||
            reader.GetString(10) != expected.ReturnType || reader.GetInt16(11) != expected.InputNames.Length ||
            reader.GetInt16(12) != 0 || !reader.GetBoolean(13) || !reader.GetBoolean(14) || !reader.GetBoolean(15) ||
            reader.IsDBNull(16) || !reader.GetFieldValue<string[]>(16).SequenceEqual([ProjectCreationSchemaContract.SearchPath]) ||
            reader.GetString(17) != expected.Body || !reader.GetFieldValue<string[]>(18).SequenceEqual(expected.InputTypes) ||
            !reader.GetBoolean(25) || !reader.GetBoolean(26))
            throw Refusal("The project creation routines do not match the accepted source definitions.");

        if (expected.OutputNames.Length == 0)
        {
            if (!reader.GetBoolean(19) || !reader.GetBoolean(20) ||
                (expected.InputNames.Length == 0
                    ? !reader.GetBoolean(21)
                    : reader.GetBoolean(21) || !reader.GetFieldValue<string[]>(24).SequenceEqual(expected.InputNames)))
                throw Refusal("The project creation routine arguments do not match this build.");
        }
        else if (reader.GetBoolean(19) || reader.GetBoolean(20) || reader.GetBoolean(21) ||
                 !reader.GetFieldValue<string[]>(22).SequenceEqual(expected.InputTypes.Concat(expected.OutputTypes)) ||
                 !reader.GetFieldValue<string[]>(23).SequenceEqual(
                     Enumerable.Repeat("i", expected.InputNames.Length).Concat(Enumerable.Repeat("t", expected.OutputNames.Length))) ||
                 !reader.GetFieldValue<string[]>(24).SequenceEqual(expected.InputNames.Concat(expected.OutputNames)))
            throw Refusal("The project creation result arguments do not match this build.");
        if (await reader.ReadAsync(cancellationToken))
            throw Refusal("The project creation routines must not have additional overloads.");
    }

    private static async Task ValidateTransitionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT trigger_row.tgname, trigger_row.tgtype, trigger_row.tgenabled::text, trigger_row.tgnargs,
                   trigger_row.tgattr::text, trigger_row.tgqual IS NULL, trigger_row.tgdeferrable,
                   trigger_row.tginitdeferred, trigger_row.tgoldtable IS NULL AND trigger_row.tgnewtable IS NULL,
                   function_namespace.nspname, function.proname
            FROM pg_catalog.pg_trigger trigger_row JOIN pg_catalog.pg_class relation ON relation.oid = trigger_row.tgrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_proc function ON function.oid = trigger_row.tgfoid
            JOIN pg_catalog.pg_namespace function_namespace ON function_namespace.oid = function.pronamespace
            WHERE namespace.nspname = 'weft' AND relation.relname = 'project_creations' AND NOT trigger_row.tgisinternal
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "project_creations_transition_guard" ||
            reader.GetInt16(1) != 31 || reader.GetString(2) != "O" || reader.GetInt16(3) != 0 || reader.GetString(4) != "" ||
            !reader.GetBoolean(5) || reader.GetBoolean(6) || reader.GetBoolean(7) || !reader.GetBoolean(8) ||
            reader.GetString(9) != "weft" || reader.GetString(10) != "guard_project_creations_transition" ||
            await reader.ReadAsync(cancellationToken))
            throw Refusal("The project creation transition guard does not match the accepted definition.");
    }

    private static InvalidOperationException Refusal(string message) => new(message);
}
