using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class RevisionDerivationRoleGuard
{
    internal const string ReaderRole = "weft_report_reader";
    internal const string DeriverRole = "weft_deriver";

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        bool writable, CancellationToken cancellationToken)
    {
        var reads = RevisionDerivationSchemaContract.ReadTables;
        await using (var guard = new NpgsqlCommand("""
            WITH reachable_roles AS (
                SELECT role.* FROM pg_catalog.pg_roles role
                WHERE role.rolname = session_user OR pg_catalog.pg_has_role(session_user, role.oid, 'MEMBER')
            ), owned_relations AS (
                SELECT relation.* FROM pg_catalog.pg_class relation
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = 'weft' AND relation.relkind IN ('r','p','v','m','f')
            )
            SELECT current_user = session_user AND current_user = $2
              AND (SELECT count(*) FROM reachable_roles) = 1
              AND (SELECT rolcanlogin AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls
                          AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolreplication
                   FROM reachable_roles WHERE rolname = session_user)
              AND pg_catalog.current_setting('transaction_read_only') = CASE WHEN $1 THEN 'off' ELSE 'on' END
              AND pg_catalog.current_setting('transaction_isolation') = 'read committed'
              AND pg_catalog.current_setting('session_replication_role') = 'origin'
              AND pg_catalog.current_setting('row_security') = 'on'
              AND pg_catalog.current_setting('server_encoding') = 'UTF8'
              AND pg_catalog.current_setting('client_encoding') = 'UTF8'
              AND pg_catalog.current_setting('standard_conforming_strings') = 'on'
              AND NULLIF(pg_catalog.current_setting('weft.organization_id', true), '') IS NULL
              AND pg_catalog.has_database_privilege(pg_catalog.current_database(), 'CONNECT')
              AND NOT pg_catalog.has_database_privilege(pg_catalog.current_database(), 'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
              AND NOT pg_catalog.has_database_privilege('public', pg_catalog.current_database(), 'CONNECT,CREATE,TEMPORARY')
              AND pg_catalog.has_schema_privilege('weft', 'USAGE')
              AND NOT pg_catalog.has_schema_privilege('weft', 'CREATE,USAGE WITH GRANT OPTION')
              AND NOT pg_catalog.has_schema_privilege('public', 'weft', 'USAGE,CREATE')
              AND (SELECT owner.rolname = 'weft_migrator' FROM pg_catalog.pg_namespace namespace
                   JOIN pg_catalog.pg_roles owner ON owner.oid = namespace.nspowner WHERE namespace.nspname = 'weft')
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership
                              WHERE membership.member IN (SELECT oid FROM reachable_roles))
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_namespace namespace CROSS JOIN reachable_roles role
                  WHERE namespace.nspname NOT LIKE 'pg_temp_%' AND namespace.nspname NOT LIKE 'pg_toast_temp_%'
                    AND (namespace.nspowner = role.oid OR pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'CREATE'))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                  CROSS JOIN reachable_roles role WHERE namespace.nspname = 'weft' AND relation.relowner = role.oid
              )
              AND NOT EXISTS (
                  SELECT 1 FROM owned_relations relation CROSS JOIN reachable_roles role
                  WHERE pg_catalog.has_table_privilege(role.oid, relation.oid, 'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,SELECT WITH GRANT OPTION')
                    OR (NOT relation.relname = ANY($3::text[]) AND pg_catalog.has_table_privilege(role.oid, relation.oid, 'SELECT'))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM owned_relations relation JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
                  CROSS JOIN reachable_roles role
                  WHERE attribute.attnum > 0 AND NOT attribute.attisdropped
                    AND (pg_catalog.has_column_privilege(role.oid, relation.oid, attribute.attnum, 'REFERENCES,SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION')
                         OR (NOT relation.relname = ANY($3::text[]) AND pg_catalog.has_column_privilege(role.oid, relation.oid, attribute.attnum, 'SELECT'))
                         OR (pg_catalog.has_column_privilege(role.oid, relation.oid, attribute.attnum, 'INSERT')
                             AND NOT ($1 AND ((relation.relname = 'revision_derivations' AND attribute.attname = ANY($4::text[]))
                                              OR (relation.relname = 'revision_derivation_attempts' AND attribute.attname = ANY($5::text[])))))
                         OR (pg_catalog.has_column_privilege(role.oid, relation.oid, attribute.attnum, 'UPDATE')
                             AND NOT ($1 AND ((relation.relname = 'revision_derivations' AND attribute.attname = ANY($6::text[]))
                                              OR (relation.relname = 'revision_derivation_attempts' AND attribute.attname = ANY($7::text[]))))))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM owned_relations relation
                  WHERE pg_catalog.has_table_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                     OR pg_catalog.has_any_column_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
                     OR (relation.relname = ANY($3::text[]) AND NOT pg_catalog.has_table_privilege(relation.oid, 'SELECT'))
              )
              AND (SELECT count(*) FROM owned_relations WHERE relkind = 'r' AND relname = ANY($3::text[])) = pg_catalog.cardinality($3::text[])
              AND NOT EXISTS (
                  SELECT 1 FROM owned_relations relation
                  CROSS JOIN LATERAL pg_catalog.aclexplode(relation.relacl) grant_entry
                  WHERE grant_entry.grantee IN (SELECT oid FROM reachable_roles)
                    AND (grant_entry.grantor <> relation.relowner OR grant_entry.is_grantable
                         OR grant_entry.privilege_type <> 'SELECT' OR NOT relation.relname = ANY($3::text[]))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM owned_relations relation JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
                  CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) grant_entry
                  WHERE attribute.attnum > 0 AND NOT attribute.attisdropped AND grant_entry.grantee IN (SELECT oid FROM reachable_roles)
                    AND (grant_entry.grantor <> relation.relowner OR grant_entry.is_grantable OR NOT $1
                         OR NOT ((grant_entry.privilege_type = 'INSERT' AND
                                  ((relation.relname = 'revision_derivations' AND attribute.attname = ANY($4::text[]))
                                   OR (relation.relname = 'revision_derivation_attempts' AND attribute.attname = ANY($5::text[]))))
                                 OR (grant_entry.privilege_type = 'UPDATE' AND
                                     ((relation.relname = 'revision_derivations' AND attribute.attname = ANY($6::text[]))
                                      OR (relation.relname = 'revision_derivation_attempts' AND attribute.attname = ANY($7::text[]))))))
              )
              AND (NOT $1 OR (
                  (SELECT count(*) FROM owned_relations relation JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
                   WHERE relation.relname = 'revision_derivations' AND attribute.attnum > 0 AND NOT attribute.attisdropped
                     AND attribute.attname = ANY($4::text[]) AND pg_catalog.has_column_privilege(relation.oid, attribute.attnum, 'INSERT')) = pg_catalog.cardinality($4::text[])
                  AND (SELECT count(*) FROM owned_relations relation JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
                       WHERE relation.relname = 'revision_derivation_attempts' AND attribute.attnum > 0 AND NOT attribute.attisdropped
                         AND attribute.attname = ANY($5::text[]) AND pg_catalog.has_column_privilege(relation.oid, attribute.attnum, 'INSERT')) = pg_catalog.cardinality($5::text[])
                  AND (SELECT count(*) FROM owned_relations relation JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
                       WHERE relation.relname = 'revision_derivations' AND attribute.attnum > 0 AND NOT attribute.attisdropped
                         AND attribute.attname = ANY($6::text[]) AND pg_catalog.has_column_privilege(relation.oid, attribute.attnum, 'UPDATE')) = pg_catalog.cardinality($6::text[])
                  AND (SELECT count(*) FROM owned_relations relation JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
                       WHERE relation.relname = 'revision_derivation_attempts' AND attribute.attnum > 0 AND NOT attribute.attisdropped
                         AND attribute.attname = ANY($7::text[]) AND pg_catalog.has_column_privilege(relation.oid, attribute.attnum, 'UPDATE')) = pg_catalog.cardinality($7::text[])
              ))
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
                  WHERE namespace.nspname = 'weft' AND
                    (pg_catalog.has_function_privilege('public', function.oid, 'EXECUTE') OR
                     EXISTS (SELECT 1 FROM reachable_roles role WHERE pg_catalog.has_function_privilege(role.oid, function.oid, 'EXECUTE')))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_class sequence JOIN pg_catalog.pg_namespace namespace ON namespace.oid = sequence.relnamespace
                  WHERE namespace.nspname = 'weft' AND sequence.relkind = 'S'
                    AND CASE WHEN sequence.relkind = 'S' THEN
                        (pg_catalog.has_sequence_privilege('public', sequence.oid, 'USAGE,SELECT,UPDATE') OR
                         EXISTS (SELECT 1 FROM reachable_roles role WHERE pg_catalog.has_sequence_privilege(role.oid, sequence.oid, 'USAGE,SELECT,UPDATE')))
                        ELSE false END
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_default_acl defaults CROSS JOIN LATERAL pg_catalog.aclexplode(defaults.defaclacl) grant_entry
                  WHERE defaults.defaclnamespace IN (0, (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = 'weft'))
                    AND (defaults.defaclrole IN (SELECT oid FROM reachable_roles) OR grant_entry.grantee = 0
                         OR grant_entry.grantee IN (SELECT oid FROM reachable_roles))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_parameter_acl parameter_acl CROSS JOIN LATERAL pg_catalog.aclexplode(parameter_acl.paracl) grant_entry
                  WHERE grant_entry.grantee = 0 OR grant_entry.grantee IN (SELECT oid FROM reachable_roles)
              )
              AND NOT pg_catalog.has_parameter_privilege('session_replication_role', 'SET,ALTER SYSTEM')
            """, connection, transaction))
        {
            guard.Parameters.AddWithValue(writable);
            guard.Parameters.AddWithValue(writable ? DeriverRole : ReaderRole);
            guard.Parameters.AddWithValue(reads);
            guard.Parameters.AddWithValue(RevisionDerivationSchemaContract.KeyInsertColumns);
            guard.Parameters.AddWithValue(RevisionDerivationSchemaContract.AttemptInsertColumns);
            guard.Parameters.AddWithValue(RevisionDerivationSchemaContract.KeyUpdateColumns);
            guard.Parameters.AddWithValue(RevisionDerivationSchemaContract.AttemptUpdateColumns);
            if (await guard.ExecuteScalarAsync(cancellationToken) is not true)
                throw Refusal("Revision report metadata requires its dedicated login and exact restricted privilege profile.");
        }

        // These nine relation locks protect source definitions during admission;
        // they neither lock membership rows nor promise an ACL commit fence.
        await using (var protect = new NpgsqlCommand("LOCK TABLE " +
            string.Join(", ", reads.Select(name => "ONLY weft." + name)) + " IN ACCESS SHARE MODE", connection, transaction))
            await protect.ExecuteNonQueryAsync(cancellationToken);
        await ValidateRelationsAsync(connection, transaction, reads, cancellationToken);
        await ValidateConstraintTriggersAsync(connection, transaction, reads, cancellationToken);
        await ValidatePoliciesAsync(connection, transaction, reads, cancellationToken);
        await ValidateSourceIndexAsync(connection, transaction, cancellationToken);
        foreach (var table in RevisionDerivationSchemaContract.Tables.Values)
        {
            await ValidateTableAsync(connection, transaction, table, cancellationToken);
            await ValidateTransitionAsync(connection, transaction, table, cancellationToken);
        }
        await ValidateNewIndexesAsync(connection, transaction, cancellationToken);
    }

    private static async Task ValidateNewIndexesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var count = RevisionDerivationSchemaContract.NewTables.Sum(name =>
            RevisionDerivationSchemaContract.Tables[name].Constraints.Count(constraint => constraint.Kind is 'p' or 'u'));
        await using var command = new NpgsqlCommand("""
            SELECT count(*) = $2 AND COALESCE(bool_and(
                constraint_row.oid IS NOT NULL AND index_relation.relname = constraint_row.conname
                AND index_relation.relkind = 'i' AND index_relation.relpersistence = 'p'
                AND NOT index_relation.relispartition AND index_relation.reloptions IS NULL
                AND owner.rolname = 'weft_migrator' AND access_method.amname = 'btree'
                AND index_row.indisunique AND index_row.indisvalid AND index_row.indisready AND index_row.indislive
                AND index_row.indimmediate AND NOT index_row.indisexclusion AND NOT index_row.indnullsnotdistinct
                AND index_row.indisprimary = (constraint_row.contype = 'p')
                AND index_row.indexprs IS NULL AND index_row.indpred IS NULL
                AND index_row.indnkeyatts = pg_catalog.cardinality(constraint_row.conkey)
                AND index_row.indnatts = pg_catalog.cardinality(constraint_row.conkey)
                AND ARRAY(SELECT attnum FROM pg_catalog.unnest(index_row.indkey::smallint[]) WITH ORDINALITY key(attnum, ord) ORDER BY ord) = constraint_row.conkey
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.unnest(index_row.indoption::smallint[]) option_value(flags) WHERE flags <> 0)
                AND ARRAY(SELECT collation_oid FROM pg_catalog.unnest(index_row.indcollation::oid[]) WITH ORDINALITY values_row(collation_oid, ord) ORDER BY ord)
                    = ARRAY(SELECT attribute.attcollation
                            FROM pg_catalog.unnest(index_row.indkey::smallint[]) WITH ORDINALITY key(attnum, ord)
                            JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = index_row.indrelid AND attribute.attnum = key.attnum ORDER BY key.ord)
                AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.unnest(index_row.indclass::oid[]) WITH ORDINALITY classes(opclass_oid, ord)
                    JOIN pg_catalog.pg_opclass opclass ON opclass.oid = classes.opclass_oid
                    JOIN pg_catalog.pg_namespace opclass_namespace ON opclass_namespace.oid = opclass.opcnamespace
                    JOIN pg_catalog.unnest(index_row.indkey::smallint[]) WITH ORDINALITY key(attnum, ord) ON key.ord = classes.ord
                    JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = index_row.indrelid AND attribute.attnum = key.attnum
                    WHERE NOT opclass.opcdefault OR opclass.opcmethod <> access_method.oid
                       OR opclass_namespace.nspname <> 'pg_catalog' OR opclass.opcintype <> attribute.atttypid)
            ), false)
            FROM pg_catalog.pg_index index_row JOIN pg_catalog.pg_class index_relation ON index_relation.oid = index_row.indexrelid
            JOIN pg_catalog.pg_class relation ON relation.oid = index_row.indrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid = index_relation.relowner
            JOIN pg_catalog.pg_am access_method ON access_method.oid = index_relation.relam
            LEFT JOIN pg_catalog.pg_constraint constraint_row ON constraint_row.conrelid = relation.oid
                AND constraint_row.conindid = index_relation.oid AND constraint_row.contype IN ('p','u')
            WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[])
            """, connection, transaction);
        command.Parameters.AddWithValue(RevisionDerivationSchemaContract.NewTables);
        command.Parameters.AddWithValue(count);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Revision derivation keys must preserve their exact accepted unique indexes.");
    }

    private static async Task ValidateSourceIndexAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*) = 1 AND COALESCE(bool_and(
                index_relation.relkind = 'i' AND index_relation.relpersistence = 'p'
                AND NOT index_relation.relispartition AND index_relation.reloptions IS NULL
                AND owner.rolname = 'weft_migrator' AND access_method.amname = 'btree'
                AND source_namespace.nspname = 'weft' AND source_relation.relname = 'mapping_revisions'
                AND index_row.indisunique AND index_row.indisvalid AND index_row.indisready
                AND index_row.indislive AND index_row.indimmediate AND NOT index_row.indisprimary
                AND NOT index_row.indisexclusion AND NOT index_row.indnullsnotdistinct
                AND NOT index_row.indisreplident AND index_row.indexprs IS NULL AND index_row.indpred IS NULL
                AND index_row.indnkeyatts = pg_catalog.cardinality($1::text[])
                AND index_row.indnatts = pg_catalog.cardinality($1::text[])
                AND ARRAY(SELECT attribute.attname::text
                          FROM pg_catalog.unnest(index_row.indkey::smallint[]) WITH ORDINALITY key(attnum, ord)
                          JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = index_row.indrelid AND attribute.attnum = key.attnum
                          WHERE NOT attribute.attisdropped ORDER BY key.ord) = $1::text[]
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.unnest(index_row.indoption::smallint[]) option_value(flags) WHERE flags <> 0)
                AND ARRAY(SELECT collation_oid
                          FROM pg_catalog.unnest(index_row.indcollation::oid[]) WITH ORDINALITY collation_values(collation_oid, ord)
                          ORDER BY ord) = ARRAY(
                    SELECT attribute.attcollation
                    FROM pg_catalog.unnest(index_row.indkey::smallint[]) WITH ORDINALITY key(attnum, ord)
                    JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = index_row.indrelid AND attribute.attnum = key.attnum
                    ORDER BY key.ord)
                AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.unnest(index_row.indclass::oid[]) WITH ORDINALITY classes(opclass_oid, ord)
                    JOIN pg_catalog.pg_opclass opclass ON opclass.oid = classes.opclass_oid
                    JOIN pg_catalog.pg_namespace opclass_namespace ON opclass_namespace.oid = opclass.opcnamespace
                    JOIN pg_catalog.unnest(index_row.indkey::smallint[]) WITH ORDINALITY key(attnum, ord) ON key.ord = classes.ord
                    JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = index_row.indrelid AND attribute.attnum = key.attnum
                    WHERE NOT opclass.opcdefault OR opclass.opcmethod <> access_method.oid
                       OR opclass_namespace.nspname <> 'pg_catalog' OR opclass.opcintype <> attribute.atttypid)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint constraint_row
                                WHERE constraint_row.conindid = index_relation.oid AND constraint_row.contype IN ('p','u','x'))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits inheritance
                                WHERE inheritance.inhparent = index_relation.oid OR inheritance.inhrelid = index_relation.oid)
            ), false)
            FROM pg_catalog.pg_class index_relation
            JOIN pg_catalog.pg_namespace index_namespace ON index_namespace.oid = index_relation.relnamespace
            JOIN pg_catalog.pg_index index_row ON index_row.indexrelid = index_relation.oid
            JOIN pg_catalog.pg_class source_relation ON source_relation.oid = index_row.indrelid
            JOIN pg_catalog.pg_namespace source_namespace ON source_namespace.oid = source_relation.relnamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid = index_relation.relowner
            JOIN pg_catalog.pg_am access_method ON access_method.oid = index_relation.relam
            WHERE index_namespace.nspname = 'weft' AND index_relation.relname = 'mapping_revisions_derivation_source_key'
            """, connection, transaction);
        command.Parameters.AddWithValue(RevisionDerivationSchemaContract.SourceIndexColumns);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Revision report derivations require the exact full native source index.");
    }

    private static async Task ValidateRelationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string[] tables, CancellationToken cancellationToken)
    {
        // Compare the actual locked sources, not identities from the earlier ACL snapshot.
        await using var command = new NpgsqlCommand("""
            SELECT count(*) = cardinality($1::text[]) AND COALESCE(bool_and(
                relation.relkind = 'r' AND relation.relpersistence = 'p' AND NOT relation.relispartition
                AND relation_owner.rolname = 'weft_migrator' AND schema_owner.rolname = 'weft_migrator'
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits inheritance
                                WHERE inheritance.inhparent = relation.oid OR inheritance.inhrelid = relation.oid)), false)
            FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_roles relation_owner ON relation_owner.oid = relation.relowner
            JOIN pg_catalog.pg_roles schema_owner ON schema_owner.oid = namespace.nspowner
            WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[])
            """, connection, transaction);
        command.Parameters.AddWithValue(tables);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("The revision report metadata sources must preserve the accepted permanent relation ownership and ancestry.");
    }

    private static async Task ValidateConstraintTriggersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string[] tables, CancellationToken cancellationToken)
    {
        // A validated FK alone does not show that its internal enforcement triggers
        // remain enabled. Catalog inspection needs no SELECT on the referenced rows.
        await using var command = new NpgsqlCommand("""
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_trigger trigger_row
                JOIN pg_catalog.pg_class relation ON relation.oid = trigger_row.tgrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[])
                  AND trigger_row.tgisinternal AND trigger_row.tgconstraint <> 0 AND trigger_row.tgenabled <> 'O'
            ) AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_constraint constraint_row
                JOIN pg_catalog.pg_class relation ON relation.oid = constraint_row.conrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[]) AND constraint_row.contype = 'f'
                  AND (SELECT count(*) FROM pg_catalog.pg_trigger trigger_row
                       WHERE trigger_row.tgconstraint = constraint_row.oid AND trigger_row.tgisinternal AND trigger_row.tgenabled = 'O') <> 4
            )
            """, connection, transaction);
        command.Parameters.AddWithValue(tables);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("The revision report metadata source constraint triggers must preserve the accepted origin enforcement.");
    }

    private static async Task ValidatePoliciesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string[] tables, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT relation.relname, relation.relrowsecurity, policy.polname, policy.polcmd::text,
                   policy.polpermissive, policy.polroles, pg_catalog.pg_get_expr(policy.polqual, policy.polrelid, false),
                   pg_catalog.pg_get_expr(policy.polwithcheck, policy.polrelid, false), relation.relforcerowsecurity
            FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            LEFT JOIN pg_catalog.pg_policy policy ON policy.polrelid = relation.oid
            WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[])
            ORDER BY relation.relname, policy.polname
            """, connection, transaction);
        command.Parameters.AddWithValue(tables);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var expression = "(" + (name == "organizations" ? "id" : "organization_id") +
                " = (NULLIF(current_setting('weft.organization_id'::text, true), ''::text))::uuid)";
            if (!seen.Add(name) || reader.GetBoolean(8))
                throw Refusal("The revision report metadata organization policies do not match the accepted guard.");
            if (name == "principals")
            {
                if (reader.GetBoolean(1) || !reader.IsDBNull(2))
                    throw Refusal("The revision report metadata principal source does not match the accepted guard.");
                continue;
            }
            if (!reader.GetBoolean(1) || reader.IsDBNull(2) ||
                reader.GetString(2) != name + "_organization_scope" || reader.GetString(3) != "*" ||
                !reader.GetBoolean(4) || !reader.GetFieldValue<uint[]>(5).SequenceEqual([0u]) ||
                reader.IsDBNull(6) || reader.GetString(6) != expression || reader.IsDBNull(7) || reader.GetString(7) != expression)
                throw Refusal("The revision report metadata organization policies do not match the accepted guard.");
        }
        if (seen.Count != tables.Length)
            throw Refusal("The revision report metadata organization policies are incomplete.");
    }

    private static async Task ValidateTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CatalogSchemaContract.Table table, CancellationToken cancellationToken)
    {
        await using (var columns = new NpgsqlCommand("""
            SELECT attribute.attname, pg_catalog.format_type(attribute.atttypid, attribute.atttypmod),
                   attribute.attnotnull, pg_catalog.pg_get_expr(defaults.adbin, defaults.adrelid),
                   attribute.attidentity::text, attribute.attgenerated::text,
                   attribute.attcollation = data_type.typcollation
            FROM pg_catalog.pg_attribute attribute
            JOIN pg_catalog.pg_type data_type ON data_type.oid = attribute.atttypid
            LEFT JOIN pg_catalog.pg_attrdef defaults ON defaults.adrelid = attribute.attrelid AND defaults.adnum = attribute.attnum
            WHERE attribute.attrelid = ('weft.' || $1)::regclass AND attribute.attnum > 0 AND NOT attribute.attisdropped
            ORDER BY attribute.attnum
            """, connection, transaction))
        {
            columns.Parameters.AddWithValue(table.Name);
            await using var reader = await columns.ExecuteReaderAsync(cancellationToken);
            var index = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (index >= table.Columns.Length)
                    throw Refusal("The revision report metadata table columns do not match this build.");
                var expected = table.Columns[index++];
                var actualDefault = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (reader.GetString(0) != expected.Name || reader.GetString(1) != expected.Type ||
                    reader.GetBoolean(2) != expected.NotNull || actualDefault != expected.Default ||
                    reader.GetString(4) != "" || reader.GetString(5) != "" || !reader.GetBoolean(6))
                    throw Refusal("The revision report metadata table columns do not match this build.");
            }
            if (index != table.Columns.Length)
                throw Refusal("The revision report metadata table columns are incomplete.");
        }

        await using var constraints = new NpgsqlCommand("""
            SELECT constraint_row.conname, constraint_row.contype::text, constraint_row.condeferrable,
                   constraint_row.condeferred, constraint_row.convalidated,
                   ARRAY(SELECT attribute.attname::text FROM unnest(constraint_row.conkey) WITH ORDINALITY key(attnum, ord)
                         JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = constraint_row.conrelid AND attribute.attnum = key.attnum ORDER BY key.ord),
                   referenced_namespace.nspname, referenced.relname,
                   ARRAY(SELECT attribute.attname::text FROM unnest(constraint_row.confkey) WITH ORDINALITY key(attnum, ord)
                         JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = constraint_row.confrelid AND attribute.attnum = key.attnum ORDER BY key.ord),
                   constraint_row.confdeltype::text, constraint_row.confupdtype::text, constraint_row.confmatchtype::text,
                   constraint_row.connoinherit, pg_catalog.pg_get_constraintdef(constraint_row.oid, false),
                   constraint_row.conislocal, constraint_row.coninhcount
            FROM pg_catalog.pg_constraint constraint_row
            LEFT JOIN pg_catalog.pg_class referenced ON referenced.oid = constraint_row.confrelid
            LEFT JOIN pg_catalog.pg_namespace referenced_namespace ON referenced_namespace.oid = referenced.relnamespace
            WHERE constraint_row.conrelid = ('weft.' || $1)::regclass ORDER BY constraint_row.conname
            """, connection, transaction);
        constraints.Parameters.AddWithValue(table.Name);
        await using var constraintReader = await constraints.ExecuteReaderAsync(cancellationToken);
        var expectedConstraints = table.Constraints.ToDictionary(value => value.Name, StringComparer.Ordinal);
        while (await constraintReader.ReadAsync(cancellationToken))
        {
            var name = constraintReader.GetString(0);
            if (!expectedConstraints.Remove(name, out var expected) || constraintReader.GetString(1) != expected.Kind.ToString() ||
                constraintReader.GetBoolean(2) != expected.Deferred || constraintReader.GetBoolean(3) != expected.Deferred ||
                !constraintReader.GetBoolean(4) || !constraintReader.GetBoolean(14) || constraintReader.GetInt32(15) != 0)
                throw Refusal("The revision report metadata constraints do not match this build.");
            if (expected.Kind == 'c')
            {
                if (constraintReader.GetBoolean(12) || !RevisionDerivationSchemaContract.TryGetCheck(name, out var definition) ||
                    definition != constraintReader.GetString(13))
                    throw Refusal("The compiled revision report metadata checks do not match the installed definitions.");
            }
            else if (!constraintReader.GetFieldValue<string[]>(5).SequenceEqual(expected.Columns))
                throw Refusal("The revision report metadata key ancestry does not match this build.");
            if (expected.Kind == 'f' && (constraintReader.GetString(6) != "weft" || constraintReader.GetString(7) != expected.ReferencedTable ||
                !constraintReader.GetFieldValue<string[]>(8).SequenceEqual(expected.ReferencedColumns) ||
                constraintReader.GetString(9) != "r" || constraintReader.GetString(10) != (RevisionDerivationSchemaContract.NewTables.Contains(table.Name, StringComparer.Ordinal) ? "r" : "a") || constraintReader.GetString(11) != "s"))
                throw Refusal("The revision report metadata foreign keys do not preserve accepted ancestry.");
        }
        if (expectedConstraints.Count != 0)
            throw Refusal("The revision report metadata constraints are incomplete.");
    }

    private static async Task ValidateTransitionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CatalogSchemaContract.Table table, CancellationToken cancellationToken)
    {
        if (table.FunctionBody is null)
        {
            await using var absent = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger WHERE tgrelid = ('weft.' || $1)::regclass AND NOT tgisinternal)", connection, transaction);
            absent.Parameters.AddWithValue(table.Name);
            if (await absent.ExecuteScalarAsync(cancellationToken) is not false)
                throw Refusal("The revision report metadata ownership sources must not have additional transition triggers.");
            await ValidateNoRulesAsync(connection, transaction, table.Name, cancellationToken);
            return;
        }

        await using (var functionIdentity = new NpgsqlCommand("""
            SELECT count(*) = 1
            FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
            WHERE namespace.nspname = 'weft' AND function.proname = 'guard_' || $1 || '_transition'
            """, connection, transaction))
        {
            functionIdentity.Parameters.AddWithValue(table.Name);
            if (await functionIdentity.ExecuteScalarAsync(cancellationToken) is not true)
                throw Refusal("Revision report metadata transition functions must preserve their unique accepted identities.");
        }

        await using var command = new NpgsqlCommand("""
            SELECT trigger_row.tgname, trigger_row.tgtype, trigger_row.tgenabled::text, trigger_row.tgnargs,
                   trigger_row.tgattr::text, trigger_row.tgqual IS NULL, trigger_row.tgdeferrable,
                   trigger_row.tginitdeferred, trigger_row.tgoldtable IS NULL AND trigger_row.tgnewtable IS NULL,
                   function_namespace.nspname, function.proname, owner.rolname, language.lanname,
                   function.prosecdef, function.proconfig, function.prosrc, function.pronargs,
                   function.prorettype = 'pg_catalog.trigger'::regtype, function.proretset,
                   function.prokind::text, function.provolatile::text, function.proparallel::text, function.proisstrict, function.proleakproof,
                   function.pronargdefaults, function.provariadic = 0, function.proargnames IS NULL,
                   function.proallargtypes IS NULL, function.proargmodes IS NULL, function.prosupport = 0
            FROM pg_catalog.pg_trigger trigger_row JOIN pg_catalog.pg_proc function ON function.oid = trigger_row.tgfoid
            JOIN pg_catalog.pg_namespace function_namespace ON function_namespace.oid = function.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid = function.proowner
            JOIN pg_catalog.pg_language language ON language.oid = function.prolang
            WHERE trigger_row.tgrelid = ('weft.' || $1)::regclass AND NOT trigger_row.tgisinternal
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != table.Name + "_transition_guard" ||
            reader.GetInt16(1) != 31 || reader.GetString(2) != "O" || reader.GetInt16(3) != 0 || reader.GetString(4) != "" ||
            !reader.GetBoolean(5) || reader.GetBoolean(6) || reader.GetBoolean(7) || !reader.GetBoolean(8) ||
            reader.GetString(9) != "weft" || reader.GetString(10) != "guard_" + table.Name + "_transition" ||
            reader.GetString(11) != "weft_migrator" || reader.GetString(12) != "plpgsql" || reader.GetBoolean(13) ||
            reader.IsDBNull(14) || !reader.GetFieldValue<string[]>(14).SequenceEqual([RevisionDerivationSchemaContract.NewTables.Contains(table.Name, StringComparer.Ordinal)
                ? "search_path=pg_catalog, weft, pg_temp" : "search_path=pg_catalog, weft"]) ||
            reader.GetString(15) != table.FunctionBody || reader.GetInt16(16) != 0 || !reader.GetBoolean(17) || reader.GetBoolean(18) ||
            reader.GetString(19) != "f" || reader.GetString(20) != "v" || reader.GetString(21) != "u" || reader.GetBoolean(22) || reader.GetBoolean(23) || reader.GetInt16(24) != 0 ||
            !reader.GetBoolean(25) || !reader.GetBoolean(26) || !reader.GetBoolean(27) || !reader.GetBoolean(28) || !reader.GetBoolean(29) ||
            await reader.ReadAsync(cancellationToken))
            throw Refusal("The revision report metadata transition guards do not match the accepted definitions.");
        await reader.DisposeAsync();
        await ValidateNoRulesAsync(connection, transaction, table.Name, cancellationToken);
    }

    private static async Task ValidateNoRulesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string table, CancellationToken cancellationToken)
    {
        await using var rules = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_rewrite WHERE ev_class = ('weft.' || $1)::regclass)", connection, transaction);
        rules.Parameters.AddWithValue(table);
        if (await rules.ExecuteScalarAsync(cancellationToken) is not false)
            throw Refusal("Revision report metadata sources must not have rewrite rules.");
    }

    private static InvalidOperationException Refusal(string message) => new(message);
}
