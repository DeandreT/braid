using Npgsql;

namespace Weft.Persistence.Postgres;

internal static partial class ServerSessionSchemaContract
{
    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        _ = TransitionBody.Value;
        // Reuse the exact trusted binding/principal descriptors, source functions and ACL guard.
        // Root separately owns its precise four-column/two-session-role ACL extension.
        await ExternalIdentitySchemaContract.ValidateAsync(connection, transaction, false, cancellationToken);
        await using (var protect = new NpgsqlCommand(
            "SELECT handle_digest FROM ONLY weft.server_sessions WHERE false", connection, transaction))
            await protect.ExecuteNonQueryAsync(cancellationToken);
        await ValidateRelationsAsync(connection, transaction, cancellationToken);
        foreach (var table in new[] { Organizations, Sessions })
        {
            await ValidateColumnsAsync(connection, transaction, table, cancellationToken);
            await ValidateConstraintsAsync(connection, transaction, table, cancellationToken);
            await ValidateIndexesAsync(connection, transaction, table, cancellationToken);
            await ValidatePolicyAsync(connection, transaction, table, cancellationToken);
        }
        await ValidateFunctionAsync(connection, transaction, cancellationToken);
        await ValidateTriggersAsync(connection, transaction, cancellationToken);
        await ValidateSessionGrantsAsync(connection, transaction, cancellationToken);
        // No organization SELECT is granted to the reader to acquire an organization row lock.
        // This fresh source admission is not a fence against a malicious migration owner.
    }

    private static async Task ValidateRelationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=4 AND COALESCE(bool_and(
                r.relkind='r' AND r.relpersistence='p' AND NOT r.relispartition
                AND r.reloptions IS NULL AND r.relreplident='d'
                AND r.relnatts=CASE WHEN r.relname='server_sessions' THEN 13
                    WHEN r.relname='external_identity_bindings' THEN 9 ELSE 3 END
                AND owner.rolname='weft_migrator' AND schema_owner.rolname='weft_migrator'
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a
                    WHERE a.attrelid=r.oid AND a.attnum>0 AND a.attisdropped)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=r.oid OR i.inhparent=r.oid)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_rewrite w WHERE w.ev_class=r.oid)),false)
            FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid=r.relowner
            JOIN pg_catalog.pg_roles schema_owner ON schema_owner.oid=n.nspowner
            WHERE n.nspname='weft' AND r.relname IN ('organizations','principals','external_identity_bindings','server_sessions')
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Server session source relations do not match their permanent owned definitions.");
    }

    private static async Task ValidateColumnsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Table table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),a.attnotnull,
                pg_catalog.pg_get_expr(d.adbin,d.adrelid),a.attisdropped,a.attidentity::text,a.attgenerated::text,
                a.attndims::integer,a.attinhcount::integer,a.attislocal,n.nspname,
                a.attcollation=t.typcollation,a.attnum::integer
            FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_type t ON t.oid=a.atttypid
            JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            WHERE a.attrelid=('weft.' || $1)::regclass AND a.attnum>0 ORDER BY a.attnum
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ordinal = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (ordinal >= table.Columns.Length)
                throw Refusal("Server session source columns do not match this build.");
            var expected = table.Columns[ordinal++];
            if (reader.GetString(0) != expected.Name || reader.GetString(1) != expected.Type ||
                reader.GetBoolean(2) != expected.NotNull || (reader.IsDBNull(3) ? null : reader.GetString(3)) != expected.Default ||
                reader.GetBoolean(4) || reader.GetString(5) != "" || reader.GetString(6) != "" ||
                reader.GetInt32(7) != 0 || reader.GetInt32(8) != 0 || !reader.GetBoolean(9) ||
                reader.GetString(10) != "pg_catalog" || !reader.GetBoolean(11) || reader.GetInt32(12) != ordinal)
                throw Refusal("Server session source column types, defaults or physical order are inconsistent.");
        }
        if (ordinal != table.Columns.Length)
            throw Refusal("Server session source columns are incomplete.");
    }

    private static async Task ValidateConstraintsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Table table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.conname,c.contype::text,c.condeferrable,c.condeferred,c.convalidated,c.connoinherit,
                ARRAY(SELECT a.attname::text FROM unnest(c.conkey) WITH ORDINALITY k(attnum,ord)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum ORDER BY k.ord),
                n.nspname,r.relname,
                ARRAY(SELECT a.attname::text FROM unnest(c.confkey) WITH ORDINALITY k(attnum,ord)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.attnum ORDER BY k.ord),
                c.confdeltype::text,c.confupdtype::text,c.confmatchtype::text,pg_catalog.pg_get_constraintdef(c.oid,false),
                (SELECT count(*)=4 AND count(DISTINCT p.proname)=4 AND bool_and(
                    t.tgisinternal AND t.tgenabled='O' AND pn.nspname='pg_catalog'
                    AND NOT t.tgdeferrable AND NOT t.tginitdeferred AND t.tgparentid=0
                    AND t.tgnargs=0 AND octet_length(t.tgargs)=0 AND t.tgattr::text='' AND t.tgqual IS NULL
                    AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL
                    AND ((t.tgrelid=c.conrelid AND t.tgconstrrelid=c.confrelid AND
                        ((p.proname='RI_FKey_check_ins' AND t.tgtype=5) OR (p.proname='RI_FKey_check_upd' AND t.tgtype=17)))
                    OR (t.tgrelid=c.confrelid AND t.tgconstrrelid=c.conrelid AND
                        ((p.proname='RI_FKey_restrict_del' AND t.tgtype=9) OR (p.proname='RI_FKey_noaction_upd' AND t.tgtype=17)))))
                 FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid
                 JOIN pg_catalog.pg_namespace pn ON pn.oid=p.pronamespace WHERE t.tgconstraint=c.oid)
            FROM pg_catalog.pg_constraint c LEFT JOIN pg_catalog.pg_class r ON r.oid=c.confrelid
            LEFT JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            WHERE c.conrelid=('weft.' || $1)::regclass ORDER BY c.conname
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        var expected = table.Constraints.ToDictionary(value => value.Name, StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!expected.Remove(reader.GetString(0), out var definition) || reader.GetString(1) != definition.Kind.ToString() ||
                reader.GetBoolean(2) || reader.GetBoolean(3) || !reader.GetBoolean(4))
                throw Refusal("Server session source constraints do not match this build.");
            if (definition.Kind == 'c')
            {
                if (reader.GetBoolean(5) || reader.GetString(13) != definition.Definition)
                    throw Refusal("Server session source checks do not match their trusted definitions.");
            }
            else if (!reader.GetFieldValue<string[]>(6).SequenceEqual(definition.Columns))
                throw Refusal("Server session source key columns are inconsistent.");
            if (definition.Kind == 'f' && (reader.IsDBNull(7) || reader.GetString(7) != "weft" ||
                reader.GetString(8) != definition.ReferencedTable ||
                !reader.GetFieldValue<string[]>(9).SequenceEqual(definition.ReferencedColumns!) ||
                reader.GetString(10) != "r" || reader.GetString(11) != "a" || reader.GetString(12) != "s" ||
                reader.IsDBNull(14) || !reader.GetBoolean(14)))
                throw Refusal("Server session foreign-key ancestry or exact enforcement is inconsistent.");
        }
        if (expected.Count != 0)
            throw Refusal("Server session source constraints are incomplete.");
    }

    private sealed record IndexDefinition(bool Primary, bool Unique, string[] Columns);

    private static async Task ValidateIndexesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Table table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.relname,i.indisprimary,i.indisunique,i.indisexclusion,i.indimmediate,i.indisvalid,
                i.indisready,i.indislive,i.indnullsnotdistinct,i.indnkeyatts,i.indnatts,
                i.indexprs IS NULL,i.indpred IS NULL,m.amname,o.rolname,n.nspname,r.reloptions IS NULL,
                ARRAY(SELECT a.attname::text FROM unnest(i.indkey) WITH ORDINALITY k(attnum,ord)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum ORDER BY k.ord),
                ARRAY(SELECT ns.nspname || '.' || op.opcname FROM unnest(i.indclass) WITH ORDINALITY k(opclass,ord)
                    JOIN pg_catalog.pg_opclass op ON op.oid=k.opclass JOIN pg_catalog.pg_namespace ns ON ns.oid=op.opcnamespace ORDER BY k.ord),
                ARRAY(SELECT v::integer FROM unnest(i.indoption) WITH ORDINALITY k(v,ord) ORDER BY k.ord),
                NOT EXISTS (SELECT 1 FROM unnest(i.indkey,i.indcollation) k(attnum,collation_oid)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum WHERE a.attcollation<>k.collation_oid),
                r.relkind='i' AND r.relpersistence='p' AND NOT r.relispartition
            FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class r ON r.oid=i.indexrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace JOIN pg_catalog.pg_roles o ON o.oid=r.relowner
            JOIN pg_catalog.pg_am m ON m.oid=r.relam WHERE i.indrelid=('weft.' || $1)::regclass ORDER BY r.relname
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        var expected = table.Constraints.Where(value => value.Kind == 'p').ToDictionary(
            value => value.Name, value => new IndexDefinition(true, true, value.Columns), StringComparer.Ordinal);
        if (table.Name == SessionTable)
        {
            expected.Add("server_sessions_organization", new(false, false, ["organization_id"]));
            expected.Add("server_sessions_binding", new(false, false, ["binding_id"]));
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!expected.Remove(reader.GetString(0), out var definition) || reader.GetBoolean(1) != definition.Primary ||
                reader.GetBoolean(2) != definition.Unique || reader.GetBoolean(3) || !reader.GetBoolean(4) || !reader.GetBoolean(5) ||
                !reader.GetBoolean(6) || !reader.GetBoolean(7) || reader.GetBoolean(8) ||
                reader.GetInt16(9) != definition.Columns.Length || reader.GetInt16(10) != definition.Columns.Length ||
                !reader.GetBoolean(11) || !reader.GetBoolean(12) || reader.GetString(13) != "btree" ||
                reader.GetString(14) != "weft_migrator" || reader.GetString(15) != "weft" || !reader.GetBoolean(16) ||
                !reader.GetFieldValue<string[]>(17).SequenceEqual(definition.Columns) ||
                !reader.GetFieldValue<string[]>(18).SequenceEqual(definition.Columns.Select(name =>
                    "pg_catalog." + (table.Columns.Single(column => column.Name == name).Type == "bytea" ? "bytea_ops" : "uuid_ops"))) ||
                reader.GetFieldValue<int[]>(19).Any(option => option != 0) || !reader.GetBoolean(20) || !reader.GetBoolean(21))
                throw Refusal("Server session indexes do not match their exact key semantics.");
        }
        if (expected.Count != 0)
            throw Refusal("Server session source indexes are incomplete.");
    }

    private static async Task ValidatePolicyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Table table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.relrowsecurity,r.relforcerowsecurity,p.polname,p.polcmd::text,p.polpermissive,p.polroles,
                pg_catalog.pg_get_expr(p.polqual,p.polrelid,false),pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid,false)
            FROM pg_catalog.pg_class r LEFT JOIN pg_catalog.pg_policy p ON p.polrelid=r.oid
            WHERE r.oid=('weft.' || $1)::regclass ORDER BY p.polname
            """, connection, transaction);
        command.Parameters.AddWithValue(table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var expression = "(" + (table.Name == "organizations" ? "id" : "organization_id") +
            " = (NULLIF(current_setting('weft.organization_id'::text, true), ''::text))::uuid)";
        if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0) || reader.GetBoolean(1) ||
            reader.IsDBNull(2) || reader.GetString(2) != table.Name + "_organization_scope" || reader.GetString(3) != "*" ||
            !reader.GetBoolean(4) || !reader.GetFieldValue<uint[]>(5).SequenceEqual([0u]) ||
            reader.IsDBNull(6) || reader.GetString(6) != expression || reader.IsDBNull(7) || reader.GetString(7) != expression ||
            await reader.ReadAsync(cancellationToken))
            throw Refusal("Server session source policies do not match the exact organization guard.");
    }

    private static async Task ValidateFunctionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=1 AND COALESCE(bool_and(owner.rolname='weft_migrator' AND language.lanname='plpgsql'
                AND f.prokind='f' AND NOT f.prosecdef AND NOT f.proretset AND NOT f.proleakproof
                AND f.provolatile='s' AND f.proparallel='u' AND NOT f.proisstrict
                AND pg_catalog.format_type(f.prorettype,NULL)='trigger' AND f.pronargs=0
                AND f.pronargdefaults=0 AND f.proargdefaults IS NULL AND f.proallargtypes IS NULL
                AND f.proargmodes IS NULL AND f.probin IS NULL AND f.prosqlbody IS NULL
                AND f.prosupport=0 AND f.procost=100 AND f.prorows=0
                AND COALESCE(f.proargnames,ARRAY[]::text[])=ARRAY[]::text[]
                AND f.proconfig=ARRAY[$2::text] AND f.prosrc=$1
                AND (SELECT count(*)=1 AND bool_and(a.grantor=f.proowner AND a.grantee=f.proowner
                    AND a.privilege_type='EXECUTE' AND NOT a.is_grantable)
                    FROM pg_catalog.aclexplode(COALESCE(f.proacl,pg_catalog.acldefault('f',f.proowner))) a)),false)
            FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid=f.proowner JOIN pg_catalog.pg_language language ON language.oid=f.prolang
            WHERE n.nspname='weft' AND f.proname='guard_server_sessions_transition'
            """, connection, transaction);
        command.Parameters.AddWithValue(TransitionBody.Value);
        command.Parameters.AddWithValue(SearchPath);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Server session transition routine does not match its trusted source and exact security descriptors.");
    }

    private static async Task ValidateTriggersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT r.relname,t.tgname,t.tgtype,t.tgenabled::text,t.tgnargs,t.tgattr::text,t.tgqual IS NULL,
                t.tgdeferrable,t.tginitdeferred,t.tgoldtable IS NULL AND t.tgnewtable IS NULL,
                n.nspname,p.proname,t.tgconstraint=0,t.tgparentid=0,t.tgconstrrelid=0,octet_length(t.tgargs)=0
            FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class r ON r.oid=t.tgrelid
            JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            WHERE r.relnamespace=(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='weft')
                AND r.relname IN ('organizations','server_sessions') AND NOT t.tgisinternal ORDER BY r.relname,t.tgname
            """, connection, transaction);
        var expected = new Dictionary<string, short>(StringComparer.Ordinal)
        {
            ["server_sessions_transition_guard"] = 31,
            ["server_sessions_truncate_guard"] = 34
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(0) != SessionTable || !expected.Remove(reader.GetString(1), out var type) ||
                reader.GetInt16(2) != type || reader.GetString(3) != "O" || reader.GetInt16(4) != 0 || reader.GetString(5) != "" ||
                !reader.GetBoolean(6) || reader.GetBoolean(7) || reader.GetBoolean(8) || !reader.GetBoolean(9) ||
                reader.GetString(10) != "weft" || reader.GetString(11) != "guard_server_sessions_transition" ||
                !reader.GetBoolean(12) || !reader.GetBoolean(13) || !reader.GetBoolean(14) || !reader.GetBoolean(15))
                throw Refusal("Server session transition triggers do not match their exact trusted definitions.");
        }
        if (expected.Count != 0)
            throw Refusal("Server session transition enforcement is incomplete.");
    }

    private static async Task ValidateSessionGrantsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH relation AS (SELECT * FROM pg_catalog.pg_class WHERE oid='weft.server_sessions'::regclass),
            columns AS (SELECT a.* FROM pg_catalog.pg_attribute a,relation r WHERE a.attrelid=r.oid AND a.attnum>0),
            expected AS (
                SELECT c.attnum,r.relowner AS grantor,role.oid AS grantee,'SELECT'::text AS privilege_type,false AS is_grantable
                FROM columns c,relation r,pg_catalog.pg_roles role WHERE role.rolname IN ('weft_session_reader','weft_session_writer')
                UNION ALL SELECT c.attnum,r.relowner,role.oid,'INSERT',false
                    FROM columns c,relation r,pg_catalog.pg_roles role WHERE role.rolname='weft_session_writer'
                UNION ALL SELECT c.attnum,r.relowner,role.oid,'UPDATE',false
                    FROM columns c,relation r,pg_catalog.pg_roles role WHERE role.rolname='weft_session_writer'
                        AND c.attname IN ('idle_expires_at','version','revoked_at')
            ), actual AS (
                SELECT c.attnum,a.grantor,a.grantee,a.privilege_type,a.is_grantable FROM columns c
                CROSS JOIN LATERAL pg_catalog.aclexplode(c.attacl) a
            )
            SELECT (SELECT count(*)=7 AND bool_and(a.grantor=r.relowner AND a.grantee=r.relowner AND NOT a.is_grantable
                        AND a.privilege_type IN ('SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER'))
                    FROM relation r CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(r.relacl,pg_catalog.acldefault('r',r.relowner))) a)
                AND (SELECT count(*)=42 FROM expected)
                AND NOT EXISTS (SELECT * FROM expected EXCEPT SELECT * FROM actual)
                AND NOT EXISTS (SELECT * FROM actual EXCEPT SELECT * FROM expected)
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw Refusal("Server session privileges require exact column-only reader and writer grants.");
    }
}
