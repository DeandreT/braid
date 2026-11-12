using Npgsql;

namespace Weft.Persistence.Postgres;

// All accepted definitions come from the checksum-pinned migration descriptors.
// This is catalog-only admission: ordinary pools acquire no new table privileges
// or relation locks merely to inspect the maintenance supplement.
internal static class SupplementalMaintenanceSchemaAdmission
{
    internal static string[] TriggerNames(string table) => MaintenanceSchemaContract.Triggers
        .Where(trigger => trigger.Table == table).Select(trigger => trigger.Name).Concat(AutomationSchemaAdmission.TriggerNames(table))
        .Concat(table == "revision_ingestions" ? new[] { "chat_mapping_write_commit", "revision_ingestions_creation_guard" } : []).ToArray();

    internal static string[] IndexNames(IEnumerable<string> tables) => MaintenanceSchemaContract.Indexes
        .Where(index => tables.Contains(index.Table, StringComparer.Ordinal)).Select(index => index.Name).ToArray();

    internal static string[] PublicationReads => PublicationSchemaContract.ReadColumns
        .Select(column => PublicationSchemaContract.ReadTable + "." + column)
        .Concat(PublicationSchemaContract.RetirementIdentityColumns.Select(column => "private_object_retirements." + column)).ToArray();

    internal static string[] PublicationInserts => PublicationSchemaContract.InsertColumns
        .Select(column => PublicationSchemaContract.ReadTable + "." + column).ToArray();

    internal static string[] PublicationUpdates => PublicationSchemaContract.BindingColumns
        .Select(column => PublicationSchemaContract.ReadTable + "." + column).ToArray();

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await AutomationSchemaAdmission.ValidateTriggersAsync(connection, transaction, cancellationToken);
        await ChatSchemaContract.ValidateMappingCommitAuditAsync(connection, transaction, cancellationToken);
        // Loading these descriptors verifies the embedded source digest and all
        // required pristine PostgreSQL captures before inspecting a live schema.
        var tables = RevisionDerivationSchemaContract.Tables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var table in IngestionSchemaContract.Tables.Values)
            tables[table.Name] = new(table.Name, table.Columns, table.Constraints, table.FunctionBody);
        foreach (var table in MaintenanceSchemaContract.Tables.Values)
            tables.Add(table.Name, table);
        await ValidateRelationsAsync(connection, transaction, tables.Keys.ToArray(), cancellationToken);
        await ValidateTablesAsync(connection, transaction, tables, cancellationToken);
        await ValidateConstraintTriggersAsync(connection, transaction, tables.Keys.ToArray(), cancellationToken);
        await ValidatePoliciesAsync(connection, transaction, tables.Keys.ToArray(), cancellationToken);
        await ValidateFunctionsAsync(connection, transaction, cancellationToken);
        await ValidateTriggersAsync(connection, transaction, tables, cancellationToken);
        if (await CreationSchemaAdmission.ShapeAsync(connection, transaction, cancellationToken) != CreationSchemaAdmission.ShapeDigest)
            throw Refusal("Creation publication guards and their authority dependencies must match the admitted schema.");
        await ValidateIndexesAsync(connection, transaction, cancellationToken);
        await ValidateGrantsAsync(connection, transaction, cancellationToken);
    }

    private static async Task ValidateRelationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string[] tables, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*) = pg_catalog.cardinality($1::text[]) AND COALESCE(bool_and(
                relation.relkind = 'r' AND relation.relpersistence = 'p' AND NOT relation.relispartition
                AND relation.reloptions IS NULL AND owner.rolname = 'weft_migrator'
                AND schema_owner.rolname = 'weft_migrator'
                AND relation.relrowsecurity = (relation.relname <> 'principals') AND NOT relation.relforcerowsecurity
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits inheritance
                                WHERE inheritance.inhparent = relation.oid OR inheritance.inhrelid = relation.oid)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_rewrite rule WHERE rule.ev_class = relation.oid)), false)
            FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid = relation.relowner
            JOIN pg_catalog.pg_roles schema_owner ON schema_owner.oid = namespace.nspowner
            WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[])
            """, connection, transaction);
        command.Parameters.AddWithValue(tables);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Maintenance relations must preserve their exact permanent ownership, ancestry and row security.");
    }

    private static async Task ValidatePoliciesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string[] tables, CancellationToken cancellationToken)
    {
        var expected = MaintenanceSchemaContract.Policies.ToDictionary(policy => policy.Table, StringComparer.Ordinal);
        foreach (var name in tables.Except(MaintenanceSchemaContract.NewTables, StringComparer.Ordinal).Where(name => name != "principals"))
        {
            var expression = "(" + (name == "organizations" ? "id" : "organization_id") +
                " = (NULLIF(current_setting('weft.organization_id'::text, true), ''::text))::uuid)";
            expected.Add(name, new(name + "_organization_scope", name, '*', [0u], expression, expression));
        }
        await using var command = new NpgsqlCommand("""
            SELECT relation.relname, policy.polname, policy.polcmd::text, policy.polpermissive, policy.polroles,
                   pg_catalog.pg_get_expr(policy.polqual, policy.polrelid, false),
                   pg_catalog.pg_get_expr(policy.polwithcheck, policy.polrelid, false)
            FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_policy policy ON policy.polrelid = relation.oid
            WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[])
            """, connection, transaction);
        command.Parameters.AddWithValue(tables);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!expected.Remove(reader.GetString(0), out var policy) || reader.IsDBNull(1) ||
                reader.GetString(1) != policy.Name || reader.GetString(2) != policy.Command.ToString() ||
                !reader.GetBoolean(3) || !reader.GetFieldValue<uint[]>(4).SequenceEqual(policy.Roles) ||
                string.IsNullOrEmpty(policy.Expression) || string.IsNullOrEmpty(policy.CheckExpression) ||
                reader.IsDBNull(5) || reader.GetString(5) != policy.Expression ||
                reader.IsDBNull(6) || reader.GetString(6) != policy.CheckExpression)
                throw Refusal("Maintenance row policies must match the trusted scoped definitions exactly.");
        }
        if (expected.Count != 0)
            throw Refusal("Maintenance row policies are incomplete.");
    }

    private static async Task ValidateFunctionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var expected = MaintenanceSchemaContract.Functions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("""
            SELECT function.proname, owner.rolname, language.lanname, function.prosecdef, function.proconfig,
                   function.prosrc, pg_catalog.pg_get_function_identity_arguments(function.oid),
                   pg_catalog.pg_get_function_result(function.oid), function.proretset, function.prokind::text,
                   function.provolatile::text, function.proparallel::text, function.proisstrict, function.proleakproof,
                   function.pronargdefaults, function.provariadic = 0, function.proallargtypes IS NULL,
                   function.proargmodes IS NULL, function.prosupport = 0
            FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid = function.proowner
            JOIN pg_catalog.pg_language language ON language.oid = function.prolang
            WHERE namespace.nspname = 'weft' AND function.proname = ANY($1::text[])
            """, connection, transaction);
        command.Parameters.AddWithValue(expected.Keys.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!expected.Remove(reader.GetString(0), out var function) || reader.GetString(1) != "weft_migrator" ||
                reader.GetString(2) != "plpgsql" || reader.GetBoolean(3) != function.SecurityDefiner ||
                reader.IsDBNull(4) || !reader.GetFieldValue<string[]>(4).SequenceEqual(["search_path=" + function.SearchPath]) ||
                reader.GetString(5) != function.Body || reader.GetString(6) != function.IdentityArguments ||
                reader.GetString(7) != function.ResultType || reader.GetBoolean(8) || reader.GetString(9) != "f" ||
                reader.GetString(10) != function.Volatility.ToString() || reader.GetString(11) != function.Parallel.ToString() ||
                reader.GetBoolean(12) || reader.GetBoolean(13) || reader.GetInt16(14) != 0 ||
                !reader.GetBoolean(15) || !reader.GetBoolean(16) || !reader.GetBoolean(17) || !reader.GetBoolean(18))
                throw Refusal("Maintenance routines must preserve their unique trusted body, signature and definer profile.");
        }
        if (expected.Count != 0)
            throw Refusal("Maintenance routines are incomplete.");
    }

    private sealed record ExpectedTrigger(string Name, string Table, string Function, short Events,
        bool SecurityDefiner, string Body, string SearchPath);

    private static async Task ValidateTriggersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyDictionary<string, CatalogSchemaContract.Table> tables, CancellationToken cancellationToken)
    {
        var expected = MaintenanceSchemaContract.Triggers.Select(trigger => new ExpectedTrigger(
            trigger.Name, trigger.Table, trigger.Function, trigger.Events, true,
            MaintenanceSchemaContract.Functions[trigger.Function].Body, "pg_catalog, weft, pg_temp")).ToList();
        expected.Add(new("chat_mapping_write_commit", "revision_ingestions", "audit_chat_mapping_write_commit", 17,
            true, ChatSchemaContract.MappingCommitTriggerBody, "pg_catalog, weft, pg_temp"));
        expected.Add(new("revision_ingestions_creation_guard", "revision_ingestions", "creation_ingestion_guard", 23,
            true, CreationSchemaAdmission.IngestionGuardBody, "pg_catalog, weft, pg_temp"));
        // Validate the retained older guard as well: this admits the complete set,
        // never an arbitrary extra trigger or a count relaxed to at least one.
        foreach (var table in tables.Values.Where(table => table.FunctionBody is not null &&
                     !MaintenanceSchemaContract.NewTables.Contains(table.Name, StringComparer.Ordinal)))
        {
            var derived = RevisionDerivationSchemaContract.NewTables.Contains(table.Name, StringComparer.Ordinal);
            expected.Add(new(table.Name + "_transition_guard", table.Name, "guard_" + table.Name + "_transition", 31,
                false, table.FunctionBody!, derived ? "pg_catalog, weft, pg_temp" : "pg_catalog, weft"));
        }
        var byName = expected.ToDictionary(trigger => trigger.Name, StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("""
            SELECT relation.relname, trigger_row.tgname, trigger_row.tgtype, trigger_row.tgenabled::text,
                   trigger_row.tgnargs, trigger_row.tgattr::text, trigger_row.tgqual IS NULL,
                   trigger_row.tgdeferrable, trigger_row.tginitdeferred,
                   trigger_row.tgoldtable IS NULL AND trigger_row.tgnewtable IS NULL, trigger_row.tgconstraint = 0,
                   function_namespace.nspname, function.proname, owner.rolname, language.lanname,
                   function.prosecdef, function.proconfig, function.prosrc, function.pronargs,
                   function.prorettype = 'pg_catalog.trigger'::regtype, function.proretset, function.prokind::text,
                   function.provolatile::text, function.proparallel::text, function.proisstrict, function.proleakproof,
                   function.pronargdefaults, function.provariadic = 0, function.proargnames IS NULL,
                   function.proallargtypes IS NULL, function.proargmodes IS NULL, function.prosupport = 0,
                   (SELECT count(*) FROM pg_catalog.pg_proc sibling WHERE sibling.pronamespace = function.pronamespace
                       AND sibling.proname = function.proname)
            FROM pg_catalog.pg_trigger trigger_row JOIN pg_catalog.pg_class relation ON relation.oid = trigger_row.tgrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_proc function ON function.oid = trigger_row.tgfoid
            JOIN pg_catalog.pg_namespace function_namespace ON function_namespace.oid = function.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid = function.proowner
            JOIN pg_catalog.pg_language language ON language.oid = function.prolang
            WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[]) AND NOT trigger_row.tgisinternal
                AND NOT trigger_row.tgname=ANY($2::text[])
            """, connection, transaction);
        command.Parameters.AddWithValue(tables.Keys.ToArray());
        command.Parameters.AddWithValue(AutomationSchemaAdmission.AllTriggerNames);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!byName.Remove(reader.GetString(1), out var trigger) || reader.GetString(0) != trigger.Table ||
                reader.GetInt16(2) != trigger.Events || reader.GetString(3) != "O" || reader.GetInt16(4) != 0 ||
                reader.GetString(5) != "" || !reader.GetBoolean(6) || reader.GetBoolean(7) || reader.GetBoolean(8) ||
                !reader.GetBoolean(9) || !reader.GetBoolean(10) || reader.GetString(11) != "weft" ||
                reader.GetString(12) != trigger.Function || reader.GetString(13) != "weft_migrator" ||
                reader.GetString(14) != "plpgsql" || reader.GetBoolean(15) != trigger.SecurityDefiner ||
                reader.IsDBNull(16) || !reader.GetFieldValue<string[]>(16).SequenceEqual(["search_path=" + trigger.SearchPath]) ||
                reader.GetString(17) != trigger.Body || reader.GetInt16(18) != 0 || !reader.GetBoolean(19) ||
                reader.GetBoolean(20) || reader.GetString(21) != "f" || reader.GetString(22) != "v" ||
                reader.GetString(23) != "u" || reader.GetBoolean(24) || reader.GetBoolean(25) || reader.GetInt16(26) != 0 ||
                !reader.GetBoolean(27) || !reader.GetBoolean(28) || !reader.GetBoolean(29) || !reader.GetBoolean(30) ||
                !reader.GetBoolean(31) || reader.GetInt64(32) != 1)
                throw Refusal("Maintenance attachment and transition triggers must preserve their exact trusted set and definitions.");
        }
        if (byName.Count != 0)
            throw Refusal("Maintenance attachment and transition triggers are incomplete.");
    }

    private sealed record ExpectedIndex(string Name, string Table, string[] Columns, string? Predicate, bool Unique,
        char? ConstraintKind, string? Expressions = null, string[]? KeyTypes = null);

    private static async Task ValidateIndexesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var expected = MaintenanceSchemaContract.Indexes.Select(index => new ExpectedIndex(
            index.Name, index.Table, index.Columns, index.Predicate, index.Unique, null, index.Expressions, index.KeyTypes)).ToList();
        foreach (var table in MaintenanceSchemaContract.Tables.Values)
            expected.AddRange(table.Constraints.Where(constraint => constraint.Kind is 'p' or 'u').Select(constraint =>
                new ExpectedIndex(constraint.Name, table.Name, constraint.Columns, null, true, constraint.Kind)));
        var byName = expected.ToDictionary(index => index.Name, StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("""
            SELECT index_relation.relname, relation.relname, index_row.indisunique, index_row.indisprimary,
                   pg_catalog.pg_get_expr(index_row.indpred, index_row.indrelid, false), constraint_row.contype::text,
                   ARRAY(SELECT COALESCE(attribute.attname::text, '') FROM pg_catalog.unnest(index_row.indkey::smallint[]) WITH ORDINALITY key(attnum, ord)
                         LEFT JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = index_row.indrelid AND attribute.attnum = key.attnum
                         WHERE key.attnum = 0 OR NOT attribute.attisdropped ORDER BY key.ord),
                   index_namespace.nspname = 'weft' AND index_relation.relkind = 'i'
                   AND index_relation.relpersistence = 'p' AND NOT index_relation.relispartition
                   AND index_relation.reloptions IS NULL AND owner.rolname = 'weft_migrator' AND access_method.amname = 'btree'
                   AND index_row.indisvalid AND index_row.indisready AND index_row.indislive AND index_row.indimmediate
                   AND NOT index_row.indisexclusion AND NOT index_row.indnullsnotdistinct AND NOT index_row.indisreplident
                   AND index_row.indnkeyatts = index_row.indnatts
                   AND NOT EXISTS (SELECT 1 FROM pg_catalog.unnest(index_row.indoption::smallint[]) option_value(flags) WHERE flags <> 0)
                   AND ARRAY(SELECT collation_oid FROM pg_catalog.unnest(index_row.indcollation::oid[]) WITH ORDINALITY value(collation_oid, ord) ORDER BY ord)
                       = ARRAY(SELECT COALESCE(attribute.attcollation, data_type.typcollation)
                               FROM pg_catalog.unnest(index_row.indkey::smallint[]) WITH ORDINALITY key(attnum, ord)
                               LEFT JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = index_row.indrelid AND attribute.attnum = key.attnum
                               JOIN pg_catalog.pg_attribute index_attribute ON index_attribute.attrelid = index_row.indexrelid AND index_attribute.attnum = key.ord
                               JOIN pg_catalog.pg_type data_type ON data_type.oid = index_attribute.atttypid ORDER BY key.ord)
                   AND NOT EXISTS (
                       SELECT 1 FROM pg_catalog.unnest(index_row.indclass::oid[]) WITH ORDINALITY classes(opclass_oid, ord)
                       JOIN pg_catalog.pg_opclass opclass ON opclass.oid = classes.opclass_oid
                       JOIN pg_catalog.pg_namespace opclass_namespace ON opclass_namespace.oid = opclass.opcnamespace
                       JOIN pg_catalog.unnest(index_row.indkey::smallint[]) WITH ORDINALITY key(attnum, ord) ON key.ord = classes.ord
                       JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = index_row.indexrelid AND attribute.attnum = key.ord
                       WHERE NOT opclass.opcdefault OR opclass.opcmethod <> access_method.oid
                          OR opclass_namespace.nspname <> 'pg_catalog' OR opclass.opcintype <> attribute.atttypid)
                   AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits inheritance
                                   WHERE inheritance.inhparent = index_relation.oid OR inheritance.inhrelid = index_relation.oid),
                   pg_catalog.pg_get_expr(index_row.indexprs, index_row.indrelid, false),
                   ARRAY(SELECT pg_catalog.format_type(attribute.atttypid, attribute.atttypmod)
                         FROM pg_catalog.pg_attribute attribute WHERE attribute.attrelid = index_row.indexrelid
                           AND attribute.attnum > 0 AND NOT attribute.attisdropped ORDER BY attribute.attnum)
            FROM pg_catalog.pg_index index_row JOIN pg_catalog.pg_class index_relation ON index_relation.oid = index_row.indexrelid
            JOIN pg_catalog.pg_namespace index_namespace ON index_namespace.oid = index_relation.relnamespace
            JOIN pg_catalog.pg_class relation ON relation.oid = index_row.indrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid = index_relation.relowner
            JOIN pg_catalog.pg_am access_method ON access_method.oid = index_relation.relam
            LEFT JOIN pg_catalog.pg_constraint constraint_row ON constraint_row.conindid = index_relation.oid
                AND constraint_row.conrelid = relation.oid AND constraint_row.contype IN ('p','u','x')
            WHERE namespace.nspname = 'weft' AND (relation.relname = ANY($1::text[]) OR index_relation.relname = ANY($2::text[]))
            """, connection, transaction);
        command.Parameters.AddWithValue(MaintenanceSchemaContract.NewTables);
        command.Parameters.AddWithValue(expected.Select(index => index.Name).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!byName.Remove(reader.GetString(0), out var index) || reader.GetString(1) != index.Table ||
                reader.GetBoolean(2) != index.Unique || reader.GetBoolean(3) != (index.ConstraintKind == 'p') ||
                (reader.IsDBNull(4) ? null : reader.GetString(4)) != index.Predicate ||
                (reader.IsDBNull(5) ? null : reader.GetString(5)) != index.ConstraintKind?.ToString() ||
                !reader.GetFieldValue<string[]>(6).SequenceEqual(index.Columns) || !reader.GetBoolean(7) ||
                (reader.IsDBNull(8) ? null : reader.GetString(8)) != index.Expressions ||
                (index.KeyTypes is not null && !reader.GetFieldValue<string[]>(9).SequenceEqual(index.KeyTypes)))
                throw Refusal("Maintenance seek and identity indexes must match their complete trusted definitions.");
        }
        if (byName.Count != 0)
            throw Refusal("Maintenance seek and identity indexes are incomplete.");
    }

    private static async Task ValidateGrantsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var expected = new HashSet<(string Table, string? Column, string Role, string Privilege)>();
        foreach (var table in MaintenanceSchemaContract.NewTables)
        {
            foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
                expected.Add((table, null, "weft_migrator", privilege));
            expected.Add((table, null, "weft_maintenance", "SELECT"));
        }
        foreach (var role in new[] { "weft_ingest", "weft_deriver" })
        {
            foreach (var column in PublicationSchemaContract.ReadColumns)
                expected.Add((PublicationSchemaContract.ReadTable, column, role, "SELECT"));
            foreach (var column in PublicationSchemaContract.InsertColumns)
                expected.Add((PublicationSchemaContract.ReadTable, column, role, "INSERT"));
            foreach (var column in PublicationSchemaContract.BindingColumns)
                expected.Add((PublicationSchemaContract.ReadTable, column, role, "UPDATE"));
            foreach (var column in PublicationSchemaContract.RetirementIdentityColumns)
                expected.Add(("private_object_retirements", column, role, "SELECT"));
        }
        await using (var command = new NpgsqlCommand("""
            SELECT relation.relname, NULL::text, role.rolname, grant_entry.privilege_type,
                   grant_entry.grantor = relation.relowner AND NOT grant_entry.is_grantable
            FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(relation.relacl, pg_catalog.acldefault('r', relation.relowner))) grant_entry
            LEFT JOIN pg_catalog.pg_roles role ON role.oid = grant_entry.grantee
            WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[])
            UNION ALL
            SELECT relation.relname, attribute.attname::text, role.rolname, grant_entry.privilege_type,
                   grant_entry.grantor = relation.relowner AND NOT grant_entry.is_grantable
            FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
            CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) grant_entry
            LEFT JOIN pg_catalog.pg_roles role ON role.oid = grant_entry.grantee
            WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[]) AND attribute.attnum > 0 AND NOT attribute.attisdropped
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(MaintenanceSchemaContract.NewTables);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(2) || !reader.GetBoolean(4) ||
                    !expected.Remove((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3))))
                    throw Refusal("Maintenance and publication grants must match the exact owner-issued table and column profile.");
            }
            if (expected.Count != 0)
                throw Refusal("Maintenance and publication grants are incomplete.");
        }

        var functions = MaintenanceSchemaContract.Functions.Keys.ToArray();
        var expectedFunctionGrants = functions.Select(name => (Name: name, Role: "weft_migrator")).ToHashSet();
        foreach (var name in MaintenanceSchemaContract.EntryFunctionNames)
            expectedFunctionGrants.Add((name, "weft_maintenance"));
        await using var functionGrants = new NpgsqlCommand("""
            SELECT function.proname, role.rolname, grant_entry.privilege_type,
                   grant_entry.grantor = function.proowner AND NOT grant_entry.is_grantable
            FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
            CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(function.proacl, pg_catalog.acldefault('f', function.proowner))) grant_entry
            LEFT JOIN pg_catalog.pg_roles role ON role.oid = grant_entry.grantee
            WHERE namespace.nspname = 'weft' AND function.proname = ANY($1::text[])
            """, connection, transaction);
        functionGrants.Parameters.AddWithValue(functions);
        await using var functionReader = await functionGrants.ExecuteReaderAsync(cancellationToken);
        while (await functionReader.ReadAsync(cancellationToken))
        {
            if (functionReader.IsDBNull(1) || functionReader.GetString(2) != "EXECUTE" || !functionReader.GetBoolean(3) ||
                !expectedFunctionGrants.Remove((functionReader.GetString(0), functionReader.GetString(1))))
                throw Refusal("Maintenance routine grants must preserve the exact restricted entry points.");
        }
        if (expectedFunctionGrants.Count != 0)
            throw Refusal("Maintenance routine grants are incomplete.");
    }

    private static async Task ValidateTablesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyDictionary<string, CatalogSchemaContract.Table> tables, CancellationToken cancellationToken)
    {
        await using (var columns = new NpgsqlCommand("""
            SELECT relation.relname, attribute.attname, pg_catalog.format_type(attribute.atttypid, attribute.atttypmod),
                   attribute.attnotnull, pg_catalog.pg_get_expr(defaults.adbin, defaults.adrelid),
                   attribute.attidentity::text, attribute.attgenerated::text,
                   attribute.attcollation = data_type.typcollation
            FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
            JOIN pg_catalog.pg_type data_type ON data_type.oid = attribute.atttypid
            LEFT JOIN pg_catalog.pg_attrdef defaults ON defaults.adrelid = attribute.attrelid AND defaults.adnum = attribute.attnum
            WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[]) AND attribute.attnum > 0 AND NOT attribute.attisdropped
            ORDER BY relation.relname, attribute.attnum
            """, connection, transaction))
        {
            columns.Parameters.AddWithValue(tables.Keys.ToArray());
            await using var reader = await columns.ExecuteReaderAsync(cancellationToken);
            var positions = tables.Keys.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                if (!positions.TryGetValue(name, out var position) || position >= tables[name].Columns.Length)
                    throw Refusal("Maintenance table columns do not match this build.");
                var expected = tables[name].Columns[position];
                positions[name] = position + 1;
                if (reader.GetString(1) != expected.Name || reader.GetString(2) != expected.Type ||
                    reader.GetBoolean(3) != expected.NotNull || (reader.IsDBNull(4) ? null : reader.GetString(4)) != expected.Default ||
                    reader.GetString(5) != "" || reader.GetString(6) != "" || !reader.GetBoolean(7))
                    throw Refusal("Maintenance table columns do not match this build.");
            }
            if (positions.Any(pair => pair.Value != tables[pair.Key].Columns.Length))
                throw Refusal("Maintenance table columns are incomplete.");
        }

        await using var constraints = new NpgsqlCommand("""
            SELECT relation.relname, constraint_row.conname, constraint_row.contype::text, constraint_row.condeferrable,
                   constraint_row.condeferred, constraint_row.convalidated,
                   ARRAY(SELECT attribute.attname::text FROM pg_catalog.unnest(constraint_row.conkey) WITH ORDINALITY key(attnum, ord)
                         JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = constraint_row.conrelid AND attribute.attnum = key.attnum ORDER BY key.ord),
                   referenced_namespace.nspname, referenced.relname,
                   ARRAY(SELECT attribute.attname::text FROM pg_catalog.unnest(constraint_row.confkey) WITH ORDINALITY key(attnum, ord)
                         JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = constraint_row.confrelid AND attribute.attnum = key.attnum ORDER BY key.ord),
                   constraint_row.confdeltype::text, constraint_row.confupdtype::text, constraint_row.confmatchtype::text,
                   constraint_row.connoinherit, pg_catalog.pg_get_constraintdef(constraint_row.oid, false),
                   constraint_row.conislocal, constraint_row.coninhcount
            FROM pg_catalog.pg_constraint constraint_row JOIN pg_catalog.pg_class relation ON relation.oid = constraint_row.conrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            LEFT JOIN pg_catalog.pg_class referenced ON referenced.oid = constraint_row.confrelid
            LEFT JOIN pg_catalog.pg_namespace referenced_namespace ON referenced_namespace.oid = referenced.relnamespace
            WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[])
            """, connection, transaction);
        constraints.Parameters.AddWithValue(tables.Keys.ToArray());
        await using var constraintReader = await constraints.ExecuteReaderAsync(cancellationToken);
        var expectedConstraints = tables.Values.SelectMany(table => table.Constraints.Select(constraint =>
            (Table: table.Name, Constraint: constraint))).ToDictionary(value => (value.Table, value.Constraint.Name), value => value.Constraint);
        while (await constraintReader.ReadAsync(cancellationToken))
        {
            var name = constraintReader.GetString(1);
            if (!expectedConstraints.Remove((constraintReader.GetString(0), name), out var expected) ||
                constraintReader.GetString(2) != expected.Kind.ToString() ||
                constraintReader.GetBoolean(3) != expected.Deferred || constraintReader.GetBoolean(4) != expected.Deferred ||
                !constraintReader.GetBoolean(5) || !constraintReader.GetBoolean(15) || constraintReader.GetInt32(16) != 0)
                throw Refusal("Maintenance constraints do not match this build.");
            if (expected.Kind == 'c')
            {
                if (constraintReader.GetBoolean(13) || !TryGetCheck(name, out var definition) ||
                    definition != constraintReader.GetString(14))
                    throw Refusal("Compiled maintenance checks do not match the installed definitions.");
            }
            else if (!constraintReader.GetFieldValue<string[]>(6).SequenceEqual(expected.Columns))
                throw Refusal("Maintenance key ancestry does not match this build.");
            if (expected.Kind == 'f' && (constraintReader.GetString(7) != "weft" || constraintReader.GetString(8) != expected.ReferencedTable ||
                !constraintReader.GetFieldValue<string[]>(9).SequenceEqual(expected.ReferencedColumns) ||
                constraintReader.GetString(10) != "r" || constraintReader.GetString(11) !=
                    (MaintenanceSchemaContract.NewTables.Contains(constraintReader.GetString(0), StringComparer.Ordinal) ||
                     RevisionDerivationSchemaContract.NewTables.Contains(constraintReader.GetString(0), StringComparer.Ordinal) ? "r" : "a") ||
                constraintReader.GetString(12) != "s"))
                throw Refusal("Maintenance foreign keys do not preserve accepted ancestry.");
        }
        if (expectedConstraints.Count != 0)
            throw Refusal("Maintenance constraints are incomplete.");
    }

    private static async Task ValidateConstraintTriggersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string[] tables, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_constraint constraint_row
                JOIN pg_catalog.pg_class relation ON relation.oid = constraint_row.conrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = 'weft' AND relation.relname = ANY($1::text[]) AND constraint_row.contype = 'f'
                  AND ((SELECT count(*) FROM pg_catalog.pg_trigger trigger_row
                        WHERE trigger_row.tgconstraint = constraint_row.oid AND trigger_row.tgisinternal
                          AND trigger_row.tgenabled = 'O'
                          AND trigger_row.tgrelid IN (constraint_row.conrelid, constraint_row.confrelid)) <> 4
                       OR EXISTS (SELECT 1 FROM pg_catalog.pg_trigger trigger_row
                                  WHERE trigger_row.tgconstraint = constraint_row.oid
                                    AND (NOT trigger_row.tgisinternal OR trigger_row.tgenabled <> 'O'
                                         OR trigger_row.tgrelid NOT IN (constraint_row.conrelid, constraint_row.confrelid))))
            )
            """, connection, transaction);
        command.Parameters.AddWithValue(tables);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Maintenance foreign keys must retain their complete enabled origin enforcement.");
    }

    private static bool TryGetCheck(string name, out string? definition) =>
        MaintenanceSchemaContract.TryGetCheck(name, out definition) || RevisionDerivationSchemaContract.TryGetCheck(name, out definition);

    private static InvalidOperationException Refusal(string message) => new(message);
}
