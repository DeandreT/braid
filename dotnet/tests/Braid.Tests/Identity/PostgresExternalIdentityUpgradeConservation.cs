using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

// Test-only conservation of a real seven-script installation. Metadata snapshots
// are hashed privately and assertions never format their contents or SQL errors.
internal sealed class PostgresExternalIdentityUpgradeConservation
{
    private static readonly string[] OldTables =
    [
        "schema_migrations", "organizations", "principals", "organization_memberships", "projects", "project_memberships",
        "mappings", "mapping_revisions", "revision_ingestions", "revision_ingestion_attempts",
        "organization_project_creation_capabilities", "project_creations", "revision_derivations", "revision_derivation_attempts",
        "private_object_publications", "private_object_retirements", "private_object_cleanup_attempts"
    ];
    private static readonly string[] OldRoles =
    [
        "braid_migrator", "braid_runtime", "braid_ingest", "braid_catalog_reader", "braid_catalog_editor",
        "braid_project_creator", "braid_report_reader", "braid_deriver", "braid_maintenance"
    ];
    private static readonly string[] NewFunctions =
        ["external_identity_key_valid_v1", "external_identity_binding_state_valid_v1", "guard_external_identity_bindings_transition"];
    private static readonly string[] Categories =
        ["relations", "columns", "constraints", "indexes", "policies", "triggers", "functions", "grants", "roles", "settings", "rights"];
    private readonly IReadOnlyDictionary<string, byte[]> _digests;

    private PostgresExternalIdentityUpgradeConservation(IReadOnlyDictionary<string, byte[]> digests) => _digests = digests;
    public override string ToString() => "PostgresExternalIdentityUpgradeConservation";

    internal static async Task<PostgresExternalIdentityUpgradeConservation> CaptureSevenAsync(NpgsqlConnection connection,
        CancellationToken cancellationToken = default) =>
        new(await ObserveAsync(connection, false, cancellationToken));

    internal async Task AssertEightPreservedAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        var current = await ObserveAsync(connection, true, cancellationToken);
        foreach (var category in Categories)
            Assert.True(CryptographicOperations.FixedTimeEquals(_digests[category], current[category]), category switch
            {
                "relations" => "The prior source relations changed during identity upgrade.",
                "columns" => "The prior source column definitions changed during identity upgrade.",
                "constraints" => "The prior source constraints changed during identity upgrade.",
                "indexes" => "The prior source indexes changed during identity upgrade.",
                "policies" => "The prior source policies changed during identity upgrade.",
                "triggers" => "The prior source trigger enforcement changed during identity upgrade.",
                "functions" => "The prior source routines changed during identity upgrade.",
                "grants" => "The prior object grants changed during identity upgrade.",
                "roles" => "The prior role and default authority changed during identity upgrade.",
                "settings" => "The prior database and role settings changed during identity upgrade.",
                _ => "The prior effective privileges changed during identity upgrade."
            });
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> ObserveAsync(NpgsqlConnection connection,
        bool afterUpgrade, CancellationToken cancellationToken)
    {
        var prefix = MigrationRunner.EmbeddedMigrations.Take(7).Select(script => script.Checksum).ToArray();
        Assert.True(prefix.Length == 7, "The trusted seven-script source prefix is unavailable.");
        try
        {
            await using var command = new NpgsqlCommand(SnapshotSql, connection);
            command.Parameters.AddWithValue(afterUpgrade);
            command.Parameters.AddWithValue(OldTables);
            command.Parameters.AddWithValue(OldRoles);
            command.Parameters.AddWithValue(NewFunctions);
            command.Parameters.AddWithValue(prefix);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            Assert.True(value is string, "The private source conservation observation is unavailable.");
            using var document = JsonDocument.Parse((string)value!);
            Assert.True(document.RootElement.GetProperty("acceptedDelta").ValueKind == JsonValueKind.True,
                "The identity upgrade source or reader delta is outside the accepted scope.");
            var digests = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var category in Categories)
                digests.Add(category, SHA256.HashData(Encoding.UTF8.GetBytes(document.RootElement.GetProperty(category).GetRawText())));
            return digests;
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("The private source conservation observation failed."); }
        catch (JsonException)
        { throw new InvalidOperationException("The private source conservation representation is invalid."); }
    }

    // Every group is one statement snapshot. No transaction or relation lock is
    // retained across migration. Definitions are before/after structural values,
    // not unexecuted guesses of pristine PostgreSQL deparser text.
    private const string SnapshotSql = """
        WITH old_relations AS (
            SELECT r.* FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            WHERE n.nspname='braid' AND r.relname=ANY($2::text[])
        ), all_source_relations AS (
            SELECT r.relname::text name FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            WHERE n.nspname='braid' AND r.relkind IN ('r','p','v','m','f','S')
        ), old_functions AS (
            SELECT p.* FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='braid' AND NOT p.proname=ANY($4::text[])
        ), reader_role AS (
            SELECT * FROM pg_catalog.pg_roles WHERE rolname='braid_identity_reader'
        ), owner_role AS (
            SELECT oid FROM pg_catalog.pg_roles WHERE rolname='braid_migrator'
        ), table_acls AS (
            SELECT r.relname::text relation_name,a.* FROM old_relations r
            CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(r.relacl,pg_catalog.acldefault('r',r.relowner))) a
        ), column_acls AS (
            SELECT r.relname::text relation_name,c.attname::text column_name,a.*
            FROM old_relations r JOIN pg_catalog.pg_attribute c ON c.attrelid=r.oid AND c.attnum>0 AND NOT c.attisdropped
            CROSS JOIN LATERAL pg_catalog.aclexplode(c.attacl) a
        ), intended_column_grants AS (
            SELECT * FROM column_acls WHERE relation_name='principals' AND column_name IN ('id','active')
                AND grantor=(SELECT oid FROM owner_role) AND grantee=(SELECT oid FROM reader_role)
                AND privilege_type='SELECT' AND NOT is_grantable
        ), schema_acls AS (
            SELECT a.* FROM pg_catalog.pg_namespace n
            CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(n.nspacl,pg_catalog.acldefault('n',n.nspowner))) a
            WHERE n.nspname='braid'
        ), intended_schema_grant AS (
            SELECT * FROM schema_acls WHERE grantor=(SELECT oid FROM owner_role) AND grantee=(SELECT oid FROM reader_role)
                AND privilege_type='USAGE' AND NOT is_grantable
        ), binding_fk AS (
            SELECT c.* FROM pg_catalog.pg_constraint c
            WHERE c.conname='external_identity_bindings_principal_fkey'
                AND c.conrelid=pg_catalog.to_regclass('braid.external_identity_bindings')
                AND c.confrelid='braid.principals'::regclass AND c.contype='f' AND c.convalidated
                AND NOT c.condeferrable AND NOT c.condeferred
                AND c.confdeltype='r' AND c.confupdtype='a' AND c.confmatchtype='s'
                AND c.conkey=ARRAY[(SELECT attnum FROM pg_catalog.pg_attribute
                    WHERE attrelid=c.conrelid AND attname='principal_id' AND NOT attisdropped)]::smallint[]
                AND c.confkey=ARRAY[(SELECT attnum FROM pg_catalog.pg_attribute
                    WHERE attrelid=c.confrelid AND attname='id' AND NOT attisdropped)]::smallint[]
        ), intended_parent_trigger_candidates AS (
            SELECT t.oid,p.proname::text action_name FROM pg_catalog.pg_trigger t JOIN binding_fk c ON c.oid=t.tgconstraint
            JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            WHERE t.tgrelid=c.confrelid AND t.tgconstrrelid=c.conrelid AND t.tgisinternal AND t.tgenabled='O'
                AND t.tgnargs=0 AND octet_length(t.tgargs)=0 AND t.tgattr::text='' AND t.tgqual IS NULL
                AND NOT t.tgdeferrable AND NOT t.tginitdeferred AND t.tgparentid=0
                AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL AND n.nspname='pg_catalog'
                AND ((p.proname='RI_FKey_restrict_del' AND t.tgtype=9)
                    OR (p.proname='RI_FKey_noaction_upd' AND t.tgtype=17))
        ), intended_parent_triggers AS (
            SELECT candidate.oid FROM intended_parent_trigger_candidates candidate
            WHERE (SELECT count(*) FROM intended_parent_trigger_candidates WHERE action_name='RI_FKey_restrict_del')=1
                AND (SELECT count(*) FROM intended_parent_trigger_candidates WHERE action_name='RI_FKey_noaction_upd')=1
        ), old_roles AS (
            SELECT * FROM pg_catalog.pg_roles WHERE rolname=ANY($3::text[]) OR rolname='braid_identity_reader'
        ), privilege_roles AS (
            SELECT name FROM unnest($3::text[]) name UNION ALL SELECT 'public'::text
        ), table_privileges AS (
            SELECT name FROM unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER',
                'SELECT WITH GRANT OPTION','INSERT WITH GRANT OPTION','UPDATE WITH GRANT OPTION','DELETE WITH GRANT OPTION',
                'TRUNCATE WITH GRANT OPTION','REFERENCES WITH GRANT OPTION','TRIGGER WITH GRANT OPTION']) name
        ), column_privileges AS (
            SELECT name FROM unnest(ARRAY['SELECT','INSERT','UPDATE','REFERENCES','SELECT WITH GRANT OPTION',
                'INSERT WITH GRANT OPTION','UPDATE WITH GRANT OPTION','REFERENCES WITH GRANT OPTION']) name
        ), reader_delta AS (
            SELECT (SELECT count(*) FROM reader_role)=1
                AND (SELECT count(*) FROM intended_column_grants)=CASE WHEN $1 THEN 2 ELSE 0 END
                AND (SELECT count(*) FROM intended_schema_grant)=CASE WHEN $1 THEN 1 ELSE 0 END
                AND NOT EXISTS (SELECT 1 FROM table_acls WHERE grantee=(SELECT oid FROM reader_role))
                AND NOT EXISTS (SELECT 1 FROM column_acls a WHERE grantee=(SELECT oid FROM reader_role)
                    AND NOT EXISTS (SELECT 1 FROM intended_column_grants i WHERE to_jsonb(i)=to_jsonb(a)))
                AND NOT EXISTS (SELECT 1 FROM schema_acls a WHERE grantee=(SELECT oid FROM reader_role)
                    AND NOT EXISTS (SELECT 1 FROM intended_schema_grant i WHERE to_jsonb(i)=to_jsonb(a)))
                AND NOT EXISTS (SELECT 1 FROM old_functions f WHERE
                    pg_catalog.has_function_privilege((SELECT oid FROM reader_role),f.oid,'EXECUTE,EXECUTE WITH GRANT OPTION'))
                AND NOT EXISTS (SELECT 1 FROM old_relations r WHERE
                    pg_catalog.has_table_privilege((SELECT oid FROM reader_role),r.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'))
                AND NOT EXISTS (SELECT 1 FROM old_relations r JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid
                    WHERE a.attnum>0 AND NOT a.attisdropped
                      AND (pg_catalog.has_column_privilege((SELECT oid FROM reader_role),r.oid,a.attnum,'INSERT,UPDATE,REFERENCES,SELECT WITH GRANT OPTION')
                        OR (pg_catalog.has_column_privilege((SELECT oid FROM reader_role),r.oid,a.attnum,'SELECT')
                          IS DISTINCT FROM ($1 AND r.relname='principals' AND a.attname IN ('id','active')))))
                AND pg_catalog.has_schema_privilege((SELECT oid FROM reader_role),'braid','USAGE')=$1
                AND NOT pg_catalog.has_schema_privilege((SELECT oid FROM reader_role),'braid','CREATE,USAGE WITH GRANT OPTION')
                AND pg_catalog.has_database_privilege((SELECT oid FROM reader_role),current_database(),'CONNECT')
                AND NOT pg_catalog.has_database_privilege((SELECT oid FROM reader_role),current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION') valid
        )
        SELECT jsonb_build_object(
            'acceptedDelta',COALESCE((SELECT valid FROM reader_delta),false)
                AND (SELECT count(*) FROM old_relations)=cardinality($2::text[])
                AND (SELECT count(*) FROM old_roles)=cardinality($3::text[])+1
                AND ARRAY(SELECT name FROM all_source_relations ORDER BY name)=
                    ARRAY(SELECT name FROM unnest($2::text[] || CASE WHEN $1 THEN ARRAY['external_identity_bindings']::text[] ELSE ARRAY[]::text[] END) name ORDER BY name)
                AND (SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations)=
                    ARRAY(SELECT generate_series(1,CASE WHEN $1 THEN 8 ELSE 7 END))
                AND (SELECT array_agg(checksum ORDER BY version) FROM braid.schema_migrations WHERE version<=7)=$5::text[]
                AND (SELECT count(*) FROM binding_fk)=CASE WHEN $1 THEN 1 ELSE 0 END
                AND (SELECT count(*) FROM intended_parent_triggers)=CASE WHEN $1 THEN 2 ELSE 0 END
                AND (SELECT count(*) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='braid' AND p.proname=ANY($4::text[]))=CASE WHEN $1 THEN 3 ELSE 0 END,
            'relations',jsonb_build_object(
                'definitions',(SELECT jsonb_agg(jsonb_build_array(oid,relname,relowner,relkind,relpersistence,relispartition,
                    relrowsecurity,relforcerowsecurity,reloptions,relreplident,reltype,relam,relhasrules) ORDER BY relname) FROM old_relations),
                'ancestry',(SELECT jsonb_agg(to_jsonb(i) ORDER BY i.inhrelid,i.inhseqno) FROM pg_catalog.pg_inherits i
                    WHERE i.inhrelid IN (SELECT oid FROM old_relations) OR i.inhparent IN (SELECT oid FROM old_relations)),
                'schemaOwner',(SELECT nspowner FROM pg_catalog.pg_namespace WHERE nspname='braid')),
            'columns',(SELECT jsonb_agg(jsonb_build_array(r.relname,to_jsonb(a)-'attacl',d.adbin::text)
                ORDER BY r.relname,a.attnum) FROM old_relations r JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid AND a.attnum>0
                LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=r.oid AND d.adnum=a.attnum),
            'constraints',(SELECT jsonb_agg(jsonb_build_array(r.relname,to_jsonb(c)) ORDER BY r.relname,c.conname)
                FROM old_relations r JOIN pg_catalog.pg_constraint c ON c.conrelid=r.oid),
            'indexes',(SELECT jsonb_agg(jsonb_build_array(r.relname,index_namespace.nspname,x.relname,to_jsonb(i),x.relowner,x.relam,x.reloptions)
                ORDER BY r.relname,index_namespace.nspname,x.relname) FROM old_relations r JOIN pg_catalog.pg_index i ON i.indrelid=r.oid
                JOIN pg_catalog.pg_class x ON x.oid=i.indexrelid
                JOIN pg_catalog.pg_namespace index_namespace ON index_namespace.oid=x.relnamespace),
            'policies',(SELECT jsonb_agg(jsonb_build_array(r.relname,to_jsonb(p)) ORDER BY r.relname,p.polname)
                FROM old_relations r JOIN pg_catalog.pg_policy p ON p.polrelid=r.oid),
            'triggers',(SELECT jsonb_agg(jsonb_build_array(r.relname,to_jsonb(t)) ORDER BY r.relname,t.tgname)
                FROM old_relations r JOIN pg_catalog.pg_trigger t ON t.tgrelid=r.oid
                WHERE NOT ($1 AND t.oid IN (SELECT oid FROM intended_parent_triggers))),
            'functions',(SELECT jsonb_agg(to_jsonb(f) ORDER BY f.oid) FROM old_functions f),
            'grants',jsonb_build_object(
                'table',(SELECT jsonb_agg(to_jsonb(a) ORDER BY relation_name,grantor,grantee,privilege_type,is_grantable) FROM table_acls a),
                'column',(SELECT jsonb_agg(to_jsonb(a) ORDER BY relation_name,column_name,grantor,grantee,privilege_type,is_grantable)
                    FROM column_acls a WHERE NOT ($1 AND EXISTS (SELECT 1 FROM intended_column_grants i WHERE to_jsonb(i)=to_jsonb(a)))),
                'schema',(SELECT jsonb_agg(to_jsonb(a) ORDER BY grantor,grantee,privilege_type,is_grantable)
                    FROM schema_acls a WHERE NOT ($1 AND EXISTS (SELECT 1 FROM intended_schema_grant i WHERE to_jsonb(i)=to_jsonb(a)))),
                'database',(SELECT datacl FROM pg_catalog.pg_database WHERE datname=current_database())),
            'roles',jsonb_build_object(
                'attributes',(SELECT jsonb_agg(jsonb_build_array(oid,rolname,rolsuper,rolinherit,rolcreaterole,rolcreatedb,
                    rolcanlogin,rolreplication,rolbypassrls,rolconnlimit,rolvaliduntil,rolconfig) ORDER BY rolname) FROM old_roles),
                'membership',(SELECT jsonb_agg(to_jsonb(m) ORDER BY m.roleid,m.member,m.grantor) FROM pg_catalog.pg_auth_members m
                    WHERE m.member IN (SELECT oid FROM old_roles) OR m.roleid IN (SELECT oid FROM old_roles)),
                'defaultAcl',(SELECT jsonb_agg(to_jsonb(a) ORDER BY oid) FROM pg_catalog.pg_default_acl a
                    WHERE a.defaclnamespace IN (0,(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='braid'))),
                'parameterAcl',(SELECT jsonb_agg(to_jsonb(a) ORDER BY parname) FROM pg_catalog.pg_parameter_acl a)),
            'settings',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.setdatabase,s.setrole) FROM pg_catalog.pg_db_role_setting s
                WHERE s.setdatabase IN (0,(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database()))
                    AND (s.setrole=0 OR s.setrole IN (SELECT oid FROM old_roles))),
            'rights',jsonb_build_object(
                'table',(SELECT jsonb_agg(jsonb_build_array(role.name,r.relname,p.name,pg_catalog.has_table_privilege(role.name,r.oid,p.name))
                    ORDER BY role.name,r.relname,p.name) FROM privilege_roles role CROSS JOIN old_relations r CROSS JOIN table_privileges p),
                'column',(SELECT jsonb_agg(jsonb_build_array(role.name,r.relname,a.attname,p.name,
                    pg_catalog.has_column_privilege(role.name,r.oid,a.attnum,p.name)) ORDER BY role.name,r.relname,a.attnum,p.name)
                    FROM privilege_roles role CROSS JOIN old_relations r JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid AND a.attnum>0 AND NOT a.attisdropped
                    CROSS JOIN column_privileges p),
                'function',(SELECT jsonb_agg(jsonb_build_array(role.name,f.oid,pg_catalog.has_function_privilege(role.name,f.oid,'EXECUTE'),
                    pg_catalog.has_function_privilege(role.name,f.oid,'EXECUTE WITH GRANT OPTION')) ORDER BY role.name,f.oid)
                    FROM privilege_roles role CROSS JOIN old_functions f),
                'schemaDatabase',(SELECT jsonb_agg(jsonb_build_array(role.name,
                    pg_catalog.has_schema_privilege(role.name,'braid','USAGE'),pg_catalog.has_schema_privilege(role.name,'braid','CREATE'),
                    pg_catalog.has_schema_privilege(role.name,'braid','USAGE WITH GRANT OPTION,CREATE WITH GRANT OPTION'),
                    pg_catalog.has_database_privilege(role.name,current_database(),'CONNECT'),pg_catalog.has_database_privilege(role.name,current_database(),'CREATE'),
                    pg_catalog.has_database_privilege(role.name,current_database(),'TEMPORARY'),
                    pg_catalog.has_database_privilege(role.name,current_database(),'CONNECT WITH GRANT OPTION,CREATE WITH GRANT OPTION,TEMPORARY WITH GRANT OPTION'))
                    ORDER BY role.name) FROM privilege_roles role)))::text
        """;
}
