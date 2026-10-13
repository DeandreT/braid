using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class IngestionRoleGuard
{
    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var tables = IngestionSchemaContract.Tables.Keys.ToArray();
        var reads = IngestionSchemaContract.LegacyReadTables.Concat(tables).ToArray();
        var updates = IngestionSchemaContract.MutableColumns.SelectMany(pair => pair.Value.Select(column => pair.Key + "." + column)).ToArray();
        await using (var guard = new NpgsqlCommand("""
            WITH reachable_roles AS (
                SELECT role.* FROM pg_catalog.pg_roles role
                WHERE role.rolname = session_user OR pg_catalog.pg_has_role(session_user, role.oid, 'MEMBER')
            ), owned_relations AS (
                SELECT relation.* FROM pg_catalog.pg_class relation
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = 'weft' AND relation.relkind IN ('r','p','v','m','f')
            )
            SELECT current_user = session_user AND current_user = 'weft_ingest'
              AND current_setting('session_replication_role') = 'origin'
              AND current_setting('row_security') = 'on'
              AND pg_catalog.has_database_privilege(current_database(), 'CONNECT')
              AND NOT pg_catalog.has_database_privilege('public', current_database(), 'CONNECT,CREATE,TEMPORARY')
              AND pg_catalog.has_schema_privilege('weft', 'USAGE')
              AND NOT pg_catalog.has_schema_privilege('public', 'weft', 'USAGE,CREATE')
              AND (SELECT owner.rolname = 'weft_migrator' FROM pg_catalog.pg_namespace namespace JOIN pg_catalog.pg_roles owner ON owner.oid = namespace.nspowner WHERE namespace.nspname = 'weft')
              AND (SELECT count(*) FROM owned_relations WHERE relkind = 'r' AND relname = ANY($1::text[])) = cardinality($1::text[])
              AND NOT EXISTS (
                  SELECT 1 FROM owned_relations relation JOIN pg_catalog.pg_roles owner ON owner.oid = relation.relowner
                  WHERE relation.relname = ANY($1::text[])
                    AND (relation.relpersistence <> 'p' OR relation.relispartition OR owner.rolname <> 'weft_migrator'
                         OR EXISTS (SELECT 1 FROM pg_catalog.pg_inherits inheritance WHERE inheritance.inhparent = relation.oid OR inheritance.inhrelid = relation.oid))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM reachable_roles role
                  WHERE role.rolsuper OR role.rolbypassrls OR role.rolcreaterole OR role.rolcreatedb OR role.rolreplication
                    OR role.rolname IN ('weft_migrator','weft_runtime','pg_read_all_data','pg_write_all_data','pg_read_server_files','pg_write_server_files','pg_execute_server_program','pg_signal_backend','pg_maintain')
                    OR pg_catalog.has_database_privilege(role.oid, current_database(), 'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                    OR pg_catalog.has_parameter_privilege(role.oid, 'session_replication_role', 'SET,ALTER SYSTEM')
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_namespace namespace CROSS JOIN reachable_roles role
                  WHERE namespace.nspname NOT LIKE 'pg_temp_%' AND namespace.nspname NOT LIKE 'pg_toast_temp_%'
                    AND (namespace.nspowner = role.oid OR pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'CREATE')
                         OR (namespace.nspname = 'weft' AND pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'USAGE WITH GRANT OPTION')))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM owned_relations relation CROSS JOIN reachable_roles role
                  WHERE relation.relowner = role.oid
                    OR pg_catalog.has_table_privilege(role.oid, relation.oid, 'UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION')
                    OR (NOT relation.relname = ANY($1::text[]) AND pg_catalog.has_table_privilege(role.oid, relation.oid, 'SELECT'))
                    OR (NOT relation.relname = ANY($2::text[]) AND pg_catalog.has_table_privilege(role.oid, relation.oid, 'INSERT'))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM owned_relations relation JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
                  CROSS JOIN reachable_roles role
                  WHERE attribute.attnum > 0 AND NOT attribute.attisdropped
                    AND (pg_catalog.has_column_privilege(role.oid, relation.oid, attribute.attnum, 'REFERENCES,SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION,REFERENCES WITH GRANT OPTION')
                         OR (NOT relation.relname = ANY($1::text[]) AND pg_catalog.has_column_privilege(role.oid, relation.oid, attribute.attnum, 'SELECT'))
                         OR (NOT relation.relname = ANY($2::text[]) AND pg_catalog.has_column_privilege(role.oid, relation.oid, attribute.attnum, 'INSERT'))
                         OR (NOT (relation.relname || '.' || attribute.attname) = ANY($3::text[]) AND pg_catalog.has_column_privilege(role.oid, relation.oid, attribute.attnum, 'UPDATE')))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM owned_relations relation
                  WHERE pg_catalog.has_table_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                     OR pg_catalog.has_any_column_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
                     OR (relation.relname = ANY($1::text[]) AND NOT pg_catalog.has_table_privilege(relation.oid, 'SELECT'))
                     OR (relation.relname = ANY($2::text[]) AND NOT pg_catalog.has_table_privilege(relation.oid, 'INSERT'))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM owned_relations relation JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
                  WHERE attribute.attnum > 0 AND NOT attribute.attisdropped
                    AND (relation.relname || '.' || attribute.attname) = ANY($3::text[])
                    AND NOT pg_catalog.has_column_privilege(relation.oid, attribute.attnum, 'UPDATE')
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
                  WHERE namespace.nspname = 'weft'
                    AND (pg_catalog.has_function_privilege('public', function.oid, 'EXECUTE')
                         OR EXISTS (SELECT 1 FROM reachable_roles role WHERE pg_catalog.has_function_privilege(role.oid, function.oid, 'EXECUTE')))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_class sequence JOIN pg_catalog.pg_namespace namespace ON namespace.oid = sequence.relnamespace
                  WHERE namespace.nspname = 'weft' AND sequence.relkind = 'S'
                    AND (pg_catalog.has_sequence_privilege('public', sequence.oid, 'USAGE,SELECT,UPDATE')
                         OR EXISTS (SELECT 1 FROM reachable_roles role WHERE pg_catalog.has_sequence_privilege(role.oid, sequence.oid, 'USAGE,SELECT,UPDATE')))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_default_acl defaults CROSS JOIN LATERAL pg_catalog.aclexplode(defaults.defaclacl) grant_entry
                  WHERE defaults.defaclnamespace IN (0, (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = 'weft'))
                    AND (grant_entry.grantee = 0 OR grant_entry.grantee IN (SELECT oid FROM reachable_roles)
                         OR grant_entry.grantee = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'weft_runtime'))
              )
            """, connection, transaction))
        {
            guard.Parameters.AddWithValue(reads);
            guard.Parameters.AddWithValue(tables);
            guard.Parameters.AddWithValue(updates);
            if (await guard.ExecuteScalarAsync(cancellationToken) is not true)
                throw Refusal("Ingestion access requires the separate minimally privileged ownership role and exact table/column grants.");
        }

        var scoped = IngestionSchemaContract.LegacyReadTables.Where(name => name != "principals").Concat(tables).ToArray();
        await using (var protect = new NpgsqlCommand("LOCK TABLE " +
            string.Join(", ", scoped.Select(name => "ONLY weft." + name)) + " IN ACCESS SHARE MODE", connection, transaction))
            await protect.ExecuteNonQueryAsync(cancellationToken);

        await ValidatePoliciesAsync(connection, transaction, scoped, cancellationToken);
        foreach (var table in IngestionSchemaContract.Tables.Values)
        {
            await ValidateTableAsync(connection, transaction, table, cancellationToken);
            await ValidateTransitionAsync(connection, transaction, table, cancellationToken);
        }
        await ValidateIdempotencyIndexesAsync(connection, transaction, cancellationToken);
    }

    private static async Task ValidatePoliciesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string[] tables, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT relation.relname, relation.relrowsecurity, policy.polname, policy.polcmd::text,
                   policy.polpermissive, policy.polroles, pg_catalog.pg_get_expr(policy.polqual, policy.polrelid, false),
                   pg_catalog.pg_get_expr(policy.polwithcheck, policy.polrelid, false)
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
            if (!seen.Add(name) || !reader.GetBoolean(1) || reader.IsDBNull(2) ||
                reader.GetString(2) != name + "_organization_scope" || reader.GetString(3) != "*" ||
                !reader.GetBoolean(4) || !reader.GetFieldValue<uint[]>(5).SequenceEqual([0u]) ||
                reader.IsDBNull(6) || reader.GetString(6) != expression || reader.IsDBNull(7) || reader.GetString(7) != expression)
                throw Refusal("The ingestion organization policies do not match the accepted guard.");
        }
        if (seen.Count != tables.Length)
            throw Refusal("The ingestion organization policies are incomplete.");
    }

    private static async Task ValidateTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IngestionSchemaContract.Table table, CancellationToken cancellationToken)
    {
        await using (var columns = new NpgsqlCommand("""
            SELECT attribute.attname, pg_catalog.format_type(attribute.atttypid, attribute.atttypmod),
                   attribute.attnotnull, pg_catalog.pg_get_expr(defaults.adbin, defaults.adrelid),
                   attribute.attidentity::text, attribute.attgenerated::text
            FROM pg_catalog.pg_attribute attribute
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
                    throw Refusal("The ingestion table columns do not match this build.");
                var expected = table.Columns[index++];
                var actualDefault = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (reader.GetString(0) != expected.Name || reader.GetString(1) != expected.Type ||
                    reader.GetBoolean(2) != expected.NotNull || actualDefault != expected.Default ||
                    reader.GetString(4) != "" || reader.GetString(5) != "")
                    throw Refusal("The ingestion table columns do not match this build.");
            }
            if (index != table.Columns.Length)
                throw Refusal("The ingestion table columns are incomplete.");
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
                   constraint_row.connoinherit, pg_catalog.pg_get_constraintdef(constraint_row.oid, false)
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
                !constraintReader.GetBoolean(4))
                throw Refusal("The ingestion constraints do not match this build.");
            if (expected.Kind == 'c')
            {
                if (constraintReader.GetBoolean(12) || !IngestionCheckDefinitions.Values.TryGetValue(name, out var definition) ||
                    definition != constraintReader.GetString(13))
                    throw Refusal("The compiled ingestion checks do not match the installed definitions.");
            }
            else if (!constraintReader.GetFieldValue<string[]>(5).SequenceEqual(expected.Columns))
                throw Refusal("The ingestion key ancestry does not match this build.");
            if (expected.Kind == 'f' && (constraintReader.GetString(6) != "weft" || constraintReader.GetString(7) != expected.ReferencedTable ||
                !constraintReader.GetFieldValue<string[]>(8).SequenceEqual(expected.ReferencedColumns) ||
                constraintReader.GetString(9) != "r" || constraintReader.GetString(10) != "a" || constraintReader.GetString(11) != "s"))
                throw Refusal("The ingestion foreign keys do not preserve accepted ancestry.");
        }
        if (expectedConstraints.Count != 0)
            throw Refusal("The ingestion constraints are incomplete.");
    }

    private static async Task ValidateTransitionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IngestionSchemaContract.Table table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT trigger_row.tgname, trigger_row.tgtype, trigger_row.tgenabled::text, trigger_row.tgnargs,
                   trigger_row.tgattr::text, trigger_row.tgqual IS NULL, trigger_row.tgdeferrable,
                   trigger_row.tginitdeferred, trigger_row.tgoldtable IS NULL AND trigger_row.tgnewtable IS NULL,
                   function_namespace.nspname, function.proname, owner.rolname, language.lanname,
                   function.prosecdef, function.proconfig, function.prosrc, function.pronargs,
                   function.prorettype = 'pg_catalog.trigger'::regtype, function.proretset,
                   function.prokind::text, function.provolatile::text, function.proparallel::text, function.proisstrict, function.proleakproof
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
            reader.IsDBNull(14) || !reader.GetFieldValue<string[]>(14).SequenceEqual(["search_path=pg_catalog, weft"]) ||
            reader.GetString(15) != table.FunctionBody || reader.GetInt16(16) != 0 || !reader.GetBoolean(17) || reader.GetBoolean(18) ||
            reader.GetString(19) != "f" || reader.GetString(20) != "v" || reader.GetString(21) != "u" || reader.GetBoolean(22) || reader.GetBoolean(23) ||
            await reader.ReadAsync(cancellationToken))
            throw Refusal("The ingestion transition guards do not match the accepted definitions.");
        await reader.DisposeAsync();
        await using var rules = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_rewrite WHERE ev_class = ('weft.' || $1)::regclass)", connection, transaction);
        rules.Parameters.AddWithValue(table.Name);
        if (await rules.ExecuteScalarAsync(cancellationToken) is not false)
            throw Refusal("Ingestion workflow tables must not have rewrite rules.");
    }

    private static async Task ValidateIdempotencyIndexesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, (string[] Columns, string Predicate)>(StringComparer.Ordinal)
        {
            ["revision_ingestions_create_key"] = (["organization_id", "project_id", "actor_principal_id", "idempotency_key"], "(operation_kind = 'create'::text)"),
            ["revision_ingestions_append_key"] = (["organization_id", "project_id", "actor_principal_id", "target_mapping_id", "idempotency_key"], "(operation_kind = 'append'::text)"),
            ["revision_ingestions_future_mapping_key"] = (["organization_id", "project_id", "future_mapping_id"], "(operation_kind = 'create'::text)")
        };
        await using var command = new NpgsqlCommand("""
            SELECT relation.relname, index_row.indisunique, index_row.indisvalid, index_row.indisready,
                   index_row.indimmediate, index_row.indexprs IS NULL, index_row.indnkeyatts = index_row.indnatts,
                   pg_catalog.pg_get_expr(index_row.indpred, index_row.indrelid, false),
                   ARRAY(SELECT attribute.attname::text FROM unnest(index_row.indkey) WITH ORDINALITY key(attnum, ord)
                         JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = index_row.indrelid AND attribute.attnum = key.attnum ORDER BY key.ord)
            FROM pg_catalog.pg_index index_row JOIN pg_catalog.pg_class relation ON relation.oid = index_row.indexrelid
            WHERE index_row.indrelid = 'weft.revision_ingestions'::regclass AND relation.relname = ANY($1::text[])
            """, connection, transaction);
        command.Parameters.AddWithValue(expected.Keys.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!expected.Remove(reader.GetString(0), out var value) || !reader.GetBoolean(1) || !reader.GetBoolean(2) ||
                !reader.GetBoolean(3) || !reader.GetBoolean(4) || !reader.GetBoolean(5) || !reader.GetBoolean(6) ||
                reader.IsDBNull(7) || reader.GetString(7) != value.Predicate || !reader.GetFieldValue<string[]>(8).SequenceEqual(value.Columns))
                throw Refusal("The ingestion idempotency keys do not match the accepted definitions.");
        }
        if (expected.Count != 0)
            throw Refusal("The ingestion idempotency keys are incomplete.");
    }

    private static InvalidOperationException Refusal(string message) => new(message);
}
