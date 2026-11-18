using Npgsql;

namespace Braid.Persistence.Postgres;

internal static partial class PayloadPermissionSchemaContract
{
    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        bool owner, CancellationToken cancellationToken)
    {
        // Loads only trusted embedded source, never adopts live definitions.
        _ = Functions.Value;
        var names = owner ? OwnerTables : ReaderTables;
        foreach (var name in names)
        {
            // Column-only reader rights suffice; planning this no-row SELECT takes
            // ACCESS SHARE without granting table-level SELECT or reading private rows.
            var column = Tables[name].Columns[0].Name;
            await using var protect = new NpgsqlCommand(
                "SELECT " + column + " FROM ONLY braid." + name + " WHERE false", connection, transaction);
            await protect.ExecuteNonQueryAsync(cancellationToken);
        }
        await ValidateRelationsAsync(connection, transaction, names, cancellationToken);
        var searchInstalled = await CatalogSearchSchemaAdmission.ValidateAsync(connection, transaction, cancellationToken);
        foreach (var name in names)
        {
            await ValidateColumnsAsync(connection, transaction, Tables[name], cancellationToken);
            await ValidateConstraintsAsync(connection, transaction, Tables[name], cancellationToken);
            await ValidateIndexesAsync(connection, transaction, Tables[name], searchInstalled, cancellationToken);
            await ValidatePoliciesAsync(connection, transaction, Tables[name], cancellationToken);
        }
        foreach (var function in Functions.Value.Values)
            await ValidateFunctionAsync(connection, transaction, function, cancellationToken);
        await ValidateTriggersAsync(connection, transaction, names, cancellationToken);
        await ValidateGrantPrivilegesAsync(connection, transaction, cancellationToken);
        // Relation locks protect source DDL, not concurrent privileged ACL/function
        // changes. This is a fresh admission check, not an adversarial-owner fence.
    }

    private static async Task ValidateRelationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string[] names, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=cardinality($1::text[]) AND COALESCE(bool_and(
                relation.relkind='r' AND relation.relpersistence='p' AND NOT relation.relispartition
                AND relation.reloptions IS NULL AND relation.relreplident='d'
                AND owner.rolname='braid_migrator' AND schema_owner.rolname='braid_migrator'
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits i
                    WHERE i.inhrelid=relation.oid OR i.inhparent=relation.oid)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_rewrite r WHERE r.ev_class=relation.oid)),false)
            FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid=relation.relowner
            JOIN pg_catalog.pg_roles schema_owner ON schema_owner.oid=namespace.nspowner
            WHERE namespace.nspname='braid' AND relation.relname=ANY($1::text[])
            """, connection, transaction);
        command.Parameters.AddWithValue(names);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Payload permission source relations do not match their permanent owned definitions.");
    }

    private static async Task ValidateColumnsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Table table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT attribute.attname,pg_catalog.format_type(attribute.atttypid,attribute.atttypmod),
                attribute.attnotnull,pg_catalog.pg_get_expr(defaults.adbin,defaults.adrelid),attribute.attisdropped,
                attribute.attidentity::text,attribute.attgenerated::text,attribute.attndims::integer,attribute.attinhcount::integer,
                attribute.attislocal,type_namespace.nspname,attribute.attcollation=type.typcollation,
                collation_namespace.nspname,collation_row.collname,collation_row.collprovider::text,collation_row.collisdeterministic,attribute.attnum::integer
            FROM pg_catalog.pg_attribute attribute
            JOIN pg_catalog.pg_type type ON type.oid=attribute.atttypid
            JOIN pg_catalog.pg_namespace type_namespace ON type_namespace.oid=type.typnamespace
            LEFT JOIN pg_catalog.pg_attrdef defaults ON defaults.adrelid=attribute.attrelid AND defaults.adnum=attribute.attnum
            LEFT JOIN pg_catalog.pg_collation collation_row ON collation_row.oid=attribute.attcollation
            LEFT JOIN pg_catalog.pg_namespace collation_namespace ON collation_namespace.oid=collation_row.collnamespace
            WHERE attribute.attrelid=('braid.' || $1)::regclass AND attribute.attnum>0
            ORDER BY attribute.attnum
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ordinal = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (ordinal >= table.Columns.Length)
                throw Refusal("Payload permission source columns do not match this build.");
            var expected = table.Columns[ordinal++];
            if (reader.GetString(0) != expected.Name || reader.GetString(1) != expected.Type ||
                reader.GetBoolean(2) != expected.NotNull || reader.GetInt32(16) != ordinal || (reader.IsDBNull(3) ? null : reader.GetString(3)) != expected.Default ||
                reader.GetBoolean(4) || reader.GetString(5) != "" || reader.GetString(6) != "" ||
                reader.GetInt32(7) != 0 || reader.GetInt32(8) != 0 || !reader.GetBoolean(9) || reader.GetString(10) != "pg_catalog")
                throw Refusal("Payload permission source columns do not match this build.");
            if (expected.CCollation
                ? reader.IsDBNull(12) || reader.GetString(12) != "pg_catalog" || reader.GetString(13) != "C" ||
                  reader.GetString(14) != "c" || !reader.GetBoolean(15)
                : !reader.GetBoolean(11))
                throw Refusal("Payload permission source collations do not match this build.");
        }
        if (ordinal != table.Columns.Length)
            throw Refusal("Payload permission source columns are incomplete.");
    }

    private static async Task ValidateConstraintsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Table table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT constraint_row.conname,constraint_row.contype::text,constraint_row.condeferrable,
                constraint_row.condeferred,constraint_row.convalidated,constraint_row.connoinherit,
                ARRAY(SELECT a.attname::text FROM unnest(constraint_row.conkey) WITH ORDINALITY k(attnum,ord)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=constraint_row.conrelid AND a.attnum=k.attnum ORDER BY k.ord),
                referenced_namespace.nspname,referenced.relname,
                ARRAY(SELECT a.attname::text FROM unnest(constraint_row.confkey) WITH ORDINALITY k(attnum,ord)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=constraint_row.confrelid AND a.attnum=k.attnum ORDER BY k.ord),
                constraint_row.confdeltype::text,constraint_row.confupdtype::text,constraint_row.confmatchtype::text,
                pg_catalog.pg_get_constraintdef(constraint_row.oid,false),
                (SELECT count(*)=4 AND bool_and(t.tgisinternal AND t.tgenabled='O' AND n.nspname='pg_catalog')
                 FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid
                 JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE t.tgconstraint=constraint_row.oid)
            FROM pg_catalog.pg_constraint constraint_row
            LEFT JOIN pg_catalog.pg_class referenced ON referenced.oid=constraint_row.confrelid
            LEFT JOIN pg_catalog.pg_namespace referenced_namespace ON referenced_namespace.oid=referenced.relnamespace
            WHERE constraint_row.conrelid=('braid.' || $1)::regclass ORDER BY constraint_row.conname
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        var expected = table.Constraints.ToDictionary(value => value.Name, StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!expected.Remove(reader.GetString(0), out var definition) || reader.GetString(1) != definition.Kind.ToString() ||
                reader.GetBoolean(2) || reader.GetBoolean(3) || !reader.GetBoolean(4))
                throw Refusal("Payload permission source constraints do not match this build.");
            if (definition.Kind == 'c')
            {
                if (reader.GetBoolean(5) || reader.GetString(13) != definition.Definition)
                    throw Refusal("Payload permission source checks do not match this build.");
            }
            else if (!reader.GetFieldValue<string[]>(6).SequenceEqual(definition.Columns))
                throw Refusal("Payload permission source key columns do not match this build.");
            if (definition.Kind == 'f' && (reader.IsDBNull(7) || reader.GetString(7) != "braid" ||
                reader.GetString(8) != definition.ReferencedTable ||
                !reader.GetFieldValue<string[]>(9).SequenceEqual(definition.ReferencedColumns!) ||
                reader.GetString(10) != "r" || reader.GetString(11) != "a" || reader.GetString(12) != "s" ||
                reader.IsDBNull(14) || !reader.GetBoolean(14)))
                throw Refusal("Payload permission source ancestry or its enforcement is inconsistent.");
        }
        if (expected.Count != 0)
            throw Refusal("Payload permission source constraints are incomplete.");
    }

    private sealed record IndexDefinition(bool Primary, bool Unique, string[] Columns, string? Predicate = null);

    private static async Task ValidateIndexesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Table table, bool searchInstalled, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT index_relation.relname,index_row.indisprimary,index_row.indisunique,index_row.indisexclusion,
                index_row.indimmediate,index_row.indisvalid,index_row.indisready,index_row.indislive,index_row.indnullsnotdistinct,
                index_row.indnkeyatts,index_row.indnatts,index_row.indexprs IS NULL,pg_catalog.pg_get_expr(index_row.indpred,index_row.indrelid,false),
                method.amname,index_owner.rolname,index_namespace.nspname,index_relation.reloptions IS NULL,
                ARRAY(SELECT a.attname::text FROM unnest(index_row.indkey) WITH ORDINALITY k(attnum,ord)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=index_row.indrelid AND a.attnum=k.attnum ORDER BY k.ord),
                ARRAY(SELECT n.nspname || '.' || o.opcname FROM unnest(index_row.indclass) WITH ORDINALITY k(opclass,ord)
                    JOIN pg_catalog.pg_opclass o ON o.oid=k.opclass JOIN pg_catalog.pg_namespace n ON n.oid=o.opcnamespace ORDER BY k.ord),
                ARRAY(SELECT option_value::integer FROM unnest(index_row.indoption) WITH ORDINALITY k(option_value,ord) ORDER BY k.ord),
                NOT EXISTS (SELECT 1 FROM unnest(index_row.indkey,index_row.indcollation) k(attnum,collation_oid)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=index_row.indrelid AND a.attnum=k.attnum
                    WHERE a.attcollation<>k.collation_oid)
            FROM pg_catalog.pg_index index_row JOIN pg_catalog.pg_class index_relation ON index_relation.oid=index_row.indexrelid
            JOIN pg_catalog.pg_namespace index_namespace ON index_namespace.oid=index_relation.relnamespace
            JOIN pg_catalog.pg_roles index_owner ON index_owner.oid=index_relation.relowner
            JOIN pg_catalog.pg_am method ON method.oid=index_relation.relam
            WHERE index_row.indrelid=('braid.' || $1)::regclass ORDER BY index_relation.relname
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        var expected = table.Constraints.Where(value => value.Kind is 'p' or 'u')
            .ToDictionary(value => value.Name,
                value => new IndexDefinition(value.Kind == 'p', true, value.Columns), StringComparer.Ordinal);
        if (table.Name == "project_memberships")
            expected.Add("project_memberships_principal_projects", new(false, false,
                ["organization_id", "principal_id", "project_id"], "active"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // These additive indexes have already passed the complete search-schema guard.
            if (searchInstalled && CatalogSearchSchemaAdmission.IndexNames.Contains(reader.GetString(0), StringComparer.Ordinal))
                continue;
            if (!expected.Remove(reader.GetString(0), out var definition) || reader.GetBoolean(1) != definition.Primary ||
                reader.GetBoolean(2) != definition.Unique || reader.GetBoolean(3) || !reader.GetBoolean(4) || !reader.GetBoolean(5) ||
                !reader.GetBoolean(6) || !reader.GetBoolean(7) || reader.GetBoolean(8) ||
                reader.GetInt16(9) != definition.Columns.Length || reader.GetInt16(10) != definition.Columns.Length ||
                !reader.GetBoolean(11) || (reader.IsDBNull(12) ? null : reader.GetString(12)) != definition.Predicate || reader.GetString(13) != "btree" ||
                reader.GetString(14) != "braid_migrator" || reader.GetString(15) != "braid" || !reader.GetBoolean(16) ||
                !reader.GetFieldValue<string[]>(17).SequenceEqual(definition.Columns) ||
                !reader.GetFieldValue<string[]>(18).SequenceEqual(definition.Columns.Select(name =>
                    "pg_catalog." + (table.Columns.Single(column => column.Name == name).Type == "uuid" ? "uuid_ops" : "text_ops"))) ||
                reader.GetFieldValue<int[]>(19).Any(option => option != 0) || !reader.GetBoolean(20))
                throw Refusal("Payload permission source indexes do not match their exact key semantics.");
        }
        if (expected.Count != 0)
            throw Refusal("Payload permission source indexes are incomplete.");
    }

    private static async Task ValidatePoliciesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Table table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.relrowsecurity,r.relforcerowsecurity,p.polname,p.polcmd::text,p.polpermissive,p.polroles,
                pg_catalog.pg_get_expr(p.polqual,p.polrelid,false),pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid,false)
            FROM pg_catalog.pg_class r LEFT JOIN pg_catalog.pg_policy p ON p.polrelid=r.oid
            WHERE r.oid=('braid.' || $1)::regclass ORDER BY p.polname
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetBoolean(0) != table.Scoped || reader.GetBoolean(1))
            throw Refusal("Payload permission source row security is inconsistent.");
        if (table.Scoped)
        {
            var expression = "(" + (table.Name == "organizations" ? "id" : "organization_id") +
                " = (NULLIF(current_setting('braid.organization_id'::text, true), ''::text))::uuid)";
            if (reader.IsDBNull(2) || reader.GetString(2) != table.Name + "_organization_scope" || reader.GetString(3) != "*" ||
                !reader.GetBoolean(4) || !reader.GetFieldValue<uint[]>(5).SequenceEqual([0u]) ||
                reader.IsDBNull(6) || reader.GetString(6) != expression || reader.IsDBNull(7) || reader.GetString(7) != expression)
                throw Refusal("Payload permission source policies do not match the accepted guard.");
        }
        else if (!reader.IsDBNull(2))
            throw Refusal("Payload permission global principal source cannot acquire organization policy semantics.");
        if (await reader.ReadAsync(cancellationToken))
            throw Refusal("Payload permission source row policies are ambiguous.");
    }

    private static async Task ValidateFunctionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Function expected, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=1 AND COALESCE(bool_and(owner.rolname='braid_migrator' AND language.lanname='plpgsql'
                AND function.prokind='f' AND NOT function.prosecdef AND NOT function.proretset AND NOT function.proleakproof
                AND function.provolatile::text=$6 AND function.proparallel::text=$7 AND function.proisstrict=$8
                AND pg_catalog.format_type(function.prorettype,NULL)=$5 AND function.pronargs=cardinality($3::text[])
                AND function.pronargdefaults=0 AND function.proargdefaults IS NULL AND function.proallargtypes IS NULL
                AND function.proargmodes IS NULL AND function.probin IS NULL AND function.prosqlbody IS NULL
                AND function.prosupport=0 AND function.procost=100 AND function.prorows=0
                AND COALESCE(function.proargnames,ARRAY[]::text[])=$4::text[]
                AND ARRAY(SELECT pg_catalog.format_type(t,NULL) FROM unnest(function.proargtypes) t)=$3::text[]
                AND function.proconfig=ARRAY[$9::text] AND function.prosrc=$2
                AND (SELECT count(*)=1 AND bool_and(a.grantor=function.proowner AND a.grantee=function.proowner
                    AND a.privilege_type='EXECUTE' AND NOT a.is_grantable)
                    FROM pg_catalog.aclexplode(COALESCE(function.proacl,pg_catalog.acldefault('f',function.proowner))) a)),false)
            FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid=function.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid=function.proowner
            JOIN pg_catalog.pg_language language ON language.oid=function.prolang
            WHERE namespace.nspname='braid' AND function.proname=$1
            """, connection, transaction);
        command.Parameters.AddWithValue(expected.Name);
        command.Parameters.AddWithValue(expected.Body);
        command.Parameters.AddWithValue(expected.ArgumentTypes);
        command.Parameters.AddWithValue(expected.ArgumentNames);
        command.Parameters.AddWithValue(expected.ReturnType);
        command.Parameters.AddWithValue(expected.Volatility.ToString());
        command.Parameters.AddWithValue(expected.Parallel.ToString());
        command.Parameters.AddWithValue(expected.Strict);
        command.Parameters.AddWithValue(SearchPath);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Payload permission source routines do not match their exact trusted definitions.");
    }

    private static async Task ValidateTriggersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string[] names, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.relname,t.tgname,t.tgtype,t.tgenabled::text,t.tgnargs,t.tgattr::text,t.tgqual IS NULL,
                t.tgdeferrable,t.tginitdeferred,t.tgoldtable IS NULL AND t.tgnewtable IS NULL,
                n.nspname,p.proname,t.tgconstraint=0,t.tgparentid=0,t.tgconstrrelid=0,octet_length(t.tgargs)=0
            FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class r ON r.oid=t.tgrelid
            JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            WHERE r.relnamespace=(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='braid')
                AND r.relname=ANY($1::text[]) AND NOT t.tgisinternal AND NOT t.tgname=ANY($2::text[]) ORDER BY r.relname,t.tgname
            """, connection, transaction);
        command.Parameters.AddWithValue(names);
        await OrganizationSwitchingSchemaAdmission.ValidateAsync(connection, transaction, cancellationToken);
        command.Parameters.AddWithValue(OrganizationSwitchingSchemaAdmission.AllTriggerNames);
        var expected = new Dictionary<string, short>(StringComparer.Ordinal)
        {
            ["project_payload_read_grants_transition_guard"] = 31,
            ["project_payload_read_grants_truncate_guard"] = 34
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(0) != GrantTable || !expected.Remove(reader.GetString(1), out var type) ||
                reader.GetInt16(2) != type || reader.GetString(3) != "O" || reader.GetInt16(4) != 0 || reader.GetString(5) != "" ||
                !reader.GetBoolean(6) || reader.GetBoolean(7) || reader.GetBoolean(8) || !reader.GetBoolean(9) ||
                reader.GetString(10) != "braid" || reader.GetString(11) != "guard_project_payload_read_grants_transition" ||
                !reader.GetBoolean(12) || !reader.GetBoolean(13) || !reader.GetBoolean(14) || !reader.GetBoolean(15))
                throw Refusal("Payload permission transition enforcement does not match its trusted definition.");
        }
        if (expected.Count != 0)
            throw Refusal("Payload permission transition enforcement is incomplete.");
    }

    private static async Task ValidateGrantPrivilegesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*)=7 AND bool_and(a.grantor=r.relowner AND a.grantee=r.relowner AND NOT a.is_grantable)
                FROM pg_catalog.aclexplode(COALESCE(r.relacl,pg_catalog.acldefault('r',r.relowner))) a)
                AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_attribute column_row
                    WHERE column_row.attrelid=r.oid AND column_row.attnum>0
                      AND (SELECT count(*)=1 AND bool_and(a.grantor=r.relowner
                          AND a.grantee=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname='braid_payload_permission_reader')
                          AND a.privilege_type='SELECT' AND NOT a.is_grantable)
                          FROM pg_catalog.aclexplode(COALESCE(column_row.attacl,ARRAY[]::aclitem[])) a) IS DISTINCT FROM true
                )
            FROM pg_catalog.pg_class r WHERE r.oid='braid.project_payload_read_grants'::regclass
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Payload permission grant privileges must remain owner-only with the exact payload reader columns.");
    }
}
