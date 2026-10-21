using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

// Real nine-script source conservation. Private catalog representations are
// hashed before assertions, so failures expose fixed category labels only.
internal sealed class PostgresServerSessionUpgradeConservation
{
    internal static readonly string[] PrefixChecksums =
    [
        "0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1",
        "d61a8bc2d56b6a39607414cf0f14c58db511594369ac124fd270a08f77455ae5",
        "0ed73cc9d597390366b661beb24f3bdfeddf1dbd9d49812ae575c2c42283fff3",
        "7335f18b8c5686b9e3ba3a2b9719d52e51fb22f5c07071e201bb7a239a48b113",
        "2ae8603df7c7ecd56e587ae65d32e1225b8569702e98ff24e5d0fc1694fae5c1",
        "b156596e07ec82a5cc8c11ef801d9ca5e15d4e6e64c76faf13635c005c986777",
        "39cbd985bf607ae1fac08700794ad7bd939988d4fe49766f181ca6984b2a29fa",
        "437630e970879ff5c03e60d46cfd972fdf82799f1ac4a68ff4fe020cec985546",
        "de0a8f45d704c667c95a8b029fb78785c1be4638b93093bde3c25f082938ea36"
    ];
    internal static readonly string[] PrefixTables =
    [
        "schema_migrations", "organizations", "principals", "organization_memberships", "projects", "project_memberships",
        "mappings", "mapping_revisions", "revision_ingestions", "revision_ingestion_attempts",
        "organization_project_creation_capabilities", "project_creations", "revision_derivations", "revision_derivation_attempts",
        "private_object_publications", "private_object_retirements", "private_object_cleanup_attempts", "external_identity_bindings",
        "project_payload_read_grants"
    ];
    private static readonly string[] PrefixRoles =
    [
        "weft_migrator", "weft_runtime", "weft_ingest", "weft_catalog_reader", "weft_catalog_editor",
        "weft_project_creator", "weft_report_reader", "weft_deriver", "weft_maintenance", "weft_identity_reader",
        "weft_payload_permission_reader"
    ];
    private static readonly string[] NewFunctions = ["guard_server_sessions_transition"];
    private static readonly string[] SessionColumns =
        ["handle_digest", "organization_id", "binding_id", "binding_version", "login_epoch", "issued_at",
         "idle_expires_at", "absolute_expires_at", "version", "revoked_at", "policy_version", "codec_version", "protected_ticket"];
    private static readonly string[] Categories =
        ["relations", "columns", "constraints", "indexes", "policies", "triggers", "functions", "grants", "roles", "settings", "rights", "uninstalledProfiles"];
    private readonly IReadOnlyDictionary<string, byte[]> _digests;

    private PostgresServerSessionUpgradeConservation(IReadOnlyDictionary<string, byte[]> digests) => _digests = digests;
    public override string ToString() => "PostgresServerSessionUpgradeConservation";

    internal static async Task<PostgresServerSessionUpgradeConservation> CaptureNineAsync(NpgsqlConnection connection,
        CancellationToken cancellationToken = default) =>
        new(await ObserveAsync(connection, false, cancellationToken));

    internal async Task AssertSessionUpgradePreservedAsync(NpgsqlConnection connection, PostgresCase test, CancellationToken cancellationToken = default)
    {
        await AdmitChatSuffixAsync(test, cancellationToken);
        var current = await ObserveAsync(connection, true, cancellationToken);
        foreach (var category in Categories)
            Assert.True(CryptographicOperations.FixedTimeEquals(_digests[category], current[category]), category switch
            {
                "relations" => "The prior source relations changed during session upgrade.",
                "columns" => "The prior source column definitions changed during session upgrade.",
                "constraints" => "The prior source constraints changed during session upgrade.",
                "indexes" => "The prior source indexes changed during session upgrade.",
                "policies" => "The prior source policies changed during session upgrade.",
                "triggers" => "The prior source trigger enforcement changed during session upgrade.",
                "functions" => "The prior source routines changed during session upgrade.",
                "grants" => "The prior object grants changed during session upgrade.",
                "roles" => "The prior role and default authority changed during session upgrade.",
                "settings" => "The prior database and role settings changed during session upgrade.",
                _ => "The prior effective privileges changed during session upgrade."
            });
    }

    // Only this precise suffix is projected out, after the complete combined
    // function/role/column/FK/trigger contract has independently admitted it.
    internal static async Task AdmitChatSuffixAsync(PostgresCase test, CancellationToken ct = default)
    {
        var principal = new PrincipalId(Guid.NewGuid());
        var key = new ChatConversationKey(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), principal, Guid.NewGuid());
        await using var database = new PostgresChatDatabase(test.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        await using var admitted = await database.BeginAsync(new(principal), key, false, ct);
        await RegressionSchemaTestContract.AdmitAsync(test, ct);
    }

    // Rejected-install boundaries deliberately retain the actual partial or unsafe
    // new-profile state. They do not assert that those profiles are admissible.
    internal static async Task<PostgresServerSessionUpgradeConservation> CaptureBeforeRejectedInstallAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        new(await ObserveAsync(connection, false, cancellationToken, captureUninstalledProfiles: true));

    internal async Task AssertRejectedInstallPreservedAsync(NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        var current = await ObserveAsync(connection, false, cancellationToken, captureUninstalledProfiles: true);
        foreach (var category in Categories)
            Assert.True(CryptographicOperations.FixedTimeEquals(_digests[category], current[category]),
                "A rejected session installation changed its exact historical source or profile boundary.");
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> ObserveAsync(NpgsqlConnection connection,
        bool afterUpgrade, CancellationToken cancellationToken, bool captureUninstalledProfiles = false)
    {
        var prefix = MigrationRunner.EmbeddedMigrations.Take(9).Select(script => script.Checksum).ToArray();
        Assert.True(prefix.SequenceEqual(PrefixChecksums, StringComparer.Ordinal),
            "The embedded migration prefix is not the trusted nine-script source.");
        try
        {
            await using var command = new NpgsqlCommand(SnapshotSql, connection);
            command.Parameters.AddWithValue(afterUpgrade);
            command.Parameters.AddWithValue(PrefixTables);
            command.Parameters.AddWithValue(PrefixRoles);
            command.Parameters.AddWithValue(NewFunctions);
            command.Parameters.AddWithValue(prefix);
            command.Parameters.AddWithValue(ServerSessionSchemaContract.MigrationChecksum);
            command.Parameters.AddWithValue(SessionColumns);
            command.Parameters.AddWithValue(captureUninstalledProfiles);
            command.Parameters.AddWithValue(ChatSchemaContract.ChatTables);
            command.Parameters.AddWithValue(ChatSchemaContract.FunctionNames);
            command.Parameters.AddWithValue(new[] { ChatDatabaseProfile.Reader, ChatDatabaseProfile.Writer, ChatDatabaseProfile.Lifecycle }
                .SelectMany(p => ChatSchemaContract.ColumnGrants(p).Select(g => g + "|" + ChatRoleGuard.RoleName(p))).ToArray());
            command.Parameters.AddWithValue(ChatSchemaContract.MigrationChecksum);
            command.Parameters.AddWithValue(PreviewSchemaTestContract.Tables.Concat(RegressionSchemaTestContract.Tables).ToArray());
            command.Parameters.AddWithValue(PreviewSchemaTestContract.FunctionNames.Concat(RegressionSchemaTestContract.Functions).ToArray());
            command.Parameters.AddWithValue(PreviewSchemaTestContract.Roles.Concat(RegressionSchemaTestContract.Roles).ToArray());
            command.Parameters.AddWithValue(PreviewSchemaTestContract.ParentForeignKeys);
            command.Parameters.AddWithValue(PreviewSchemaTestContract.MigrationChecksum);
            command.Parameters.AddWithValue(PreviewSchemaTestContract.MessagesMigrationChecksum);
            command.Parameters.AddWithValue(RegressionSchemaTestContract.CasesChecksum);
            command.Parameters.AddWithValue(RegressionSchemaTestContract.RunsChecksum);
            command.Parameters.AddWithValue(RegressionSchemaTestContract.DispatchChecksum);
            command.Parameters.AddWithValue(RegressionSchemaTestContract.HistoryChecksum);
            command.Parameters.AddWithValue(RegressionSchemaTestContract.BuildChecksum);
            command.Parameters.AddWithValue(RegressionSchemaTestContract.BuildDispatchChecksum);
            command.Parameters.AddWithValue(RegressionSchemaTestContract.BuildMetadataChecksum);
            command.Parameters.AddWithValue(RegressionSchemaTestContract.QualificationChecksum);
            command.Parameters.AddWithValue(RegressionSchemaTestContract.LocalReleaseChecksum);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            Assert.True(value is string, "The private session upgrade conservation observation is unavailable.");
            using var document = JsonDocument.Parse((string)value!);
            Assert.True(document.RootElement.GetProperty("acceptedDelta").ValueKind == JsonValueKind.True,
                "The session upgrade source or reader delta is outside the accepted scope.");
            var digests = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var category in Categories)
                digests.Add(category, SHA256.HashData(Encoding.UTF8.GetBytes(document.RootElement.GetProperty(category).GetRawText())));
            return digests;
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("The private session upgrade conservation observation failed."); }
        catch (JsonException)
        { throw new InvalidOperationException("The private session upgrade conservation representation is invalid."); }
    }

    // There is no open transaction across migration. Only the exact additive
    // child FK parent triggers and thirteen exact old-column SELECT grants are
    // removed from before/after comparison; all fifty-five new column grants
    // (reader19, writer20SELECT+13INSERT+3UPDATE) are independently validated.
    private const string SnapshotSql = """
        WITH old_relations AS (
            SELECT r.* FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            WHERE n.nspname='weft' AND r.relname=ANY($2::text[])
        ), application_relations AS (
            SELECT r.* FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            WHERE n.nspname='weft' AND r.relkind IN ('r','p','v','m','f','S')
        ), source_relations AS (
            SELECT * FROM application_relations WHERE NOT ($1 AND (relname=ANY($9::text[]) OR relname=ANY($13::text[])))
        ), old_functions AS (
            SELECT p.* FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='weft' AND NOT p.proname=ANY($4::text[])
                AND NOT ($1 AND (p.proname=ANY($10::text[]) OR p.proname=ANY($14::text[])))
        ), chat_roles AS (
            SELECT oid,rolname FROM pg_roles WHERE rolname IN ('weft_chat_reader','weft_chat_writer','weft_chat_lifecycle')
        ), preview_roles AS (
            SELECT oid,rolname FROM pg_roles WHERE rolname=ANY($15::text[])
        ), chat_columns AS (
            SELECT split_part(x,'|',1) tab,split_part(x,'|',2) col,split_part(x,'|',3) privilege,split_part(x,'|',4) role
            FROM unnest($11::text[]) x
        ), session_profiles(role_name) AS (
            VALUES ('weft_session_reader'::text),('weft_session_writer'::text)
        ), session_roles AS (
            SELECT r.* FROM pg_catalog.pg_roles r JOIN session_profiles p ON r.rolname=p.role_name
        ), owner_role AS (
            SELECT oid FROM pg_catalog.pg_roles WHERE rolname='weft_migrator'
        ), table_acls AS (
            SELECT r.relname::text relation_name,a.* FROM old_relations r
            CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(r.relacl,pg_catalog.acldefault('r',r.relowner))) a
        ), unprojected_column_acls AS (
            SELECT r.relname::text relation_name,c.attname::text column_name,a.*
            FROM old_relations r JOIN pg_catalog.pg_attribute c ON c.attrelid=r.oid AND c.attnum>0 AND NOT c.attisdropped
            CROSS JOIN LATERAL pg_catalog.aclexplode(c.attacl) a
        ), column_acls AS (
            SELECT a.* FROM unprojected_column_acls a WHERE NOT ($1 AND EXISTS(SELECT 1 FROM chat_columns e
                JOIN chat_roles role ON role.rolname=e.role WHERE a.relation_name=e.tab AND a.column_name=e.col
                    AND a.privilege_type=e.privilege AND a.grantee=role.oid AND a.grantor=(SELECT oid FROM owner_role) AND NOT a.is_grantable))
        ), expected_session_columns(role_name,relation_name,column_name,privilege_type) AS (
            SELECT p.role_name,'server_sessions'::text,c.name,'SELECT'::text
            FROM session_profiles p CROSS JOIN unnest($7::text[]) c(name)
            UNION ALL
            SELECT p.role_name,e.relation_name,e.column_name,'SELECT'::text
            FROM session_profiles p CROSS JOIN (VALUES
                ('external_identity_bindings'::text,'binding_id'::text),('external_identity_bindings','principal_id'),
                ('external_identity_bindings','enabled'),('external_identity_bindings','version'),
                ('principals','id'),('principals','active')) e(relation_name,column_name)
            UNION ALL SELECT 'weft_session_writer','organizations','id','SELECT'
            UNION ALL SELECT 'weft_session_writer','server_sessions',name,'INSERT' FROM unnest($7::text[]) name
            UNION ALL SELECT 'weft_session_writer','server_sessions',name,'UPDATE'
                FROM unnest(ARRAY['idle_expires_at','version','revoked_at']) name
        ), intended_column_grants AS (
            SELECT a.* FROM column_acls a JOIN session_roles role ON role.oid=a.grantee
            JOIN expected_session_columns e ON e.role_name=role.rolname AND e.relation_name=a.relation_name
                AND e.column_name=a.column_name AND e.privilege_type=a.privilege_type
            WHERE a.grantor=(SELECT oid FROM owner_role) AND NOT a.is_grantable
        ), all_session_column_grants AS (
            SELECT role.rolname::text role_name,r.relname::text relation_name,c.attname::text column_name,a.*
            FROM application_relations r JOIN pg_catalog.pg_attribute c ON c.attrelid=r.oid AND c.attnum>0 AND NOT c.attisdropped
            CROSS JOIN LATERAL pg_catalog.aclexplode(c.attacl) a JOIN session_roles role ON role.oid=a.grantee
        ), schema_acls AS (
            SELECT a.* FROM pg_catalog.pg_namespace n
            CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(n.nspacl,pg_catalog.acldefault('n',n.nspowner))) a
            WHERE n.nspname='weft' AND NOT ($1 AND a.grantee IN (SELECT oid FROM chat_roles UNION ALL SELECT oid FROM preview_roles)
                AND a.grantor=(SELECT oid FROM owner_role) AND NOT a.is_grantable AND a.privilege_type='USAGE')
        ), intended_schema_grants AS (
            SELECT * FROM schema_acls WHERE grantor=(SELECT oid FROM owner_role) AND grantee IN (SELECT oid FROM session_roles)
                AND privilege_type='USAGE' AND NOT is_grantable
        ), database_acls AS (
            SELECT a.* FROM pg_catalog.pg_database d
            CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(d.datacl,pg_catalog.acldefault('d',d.datdba))) a
            WHERE d.datname=current_database()
        ), intended_database_grants AS (
            SELECT * FROM database_acls WHERE grantor=(SELECT oid FROM owner_role) AND grantee IN (SELECT oid FROM session_roles)
                AND privilege_type='CONNECT' AND NOT is_grantable
        ), expected_foreign_keys(name,parent,child_columns,parent_columns) AS (
            VALUES ('server_sessions_organization_fkey','organizations',ARRAY['organization_id'],ARRAY['id']),
                ('server_sessions_binding_fkey','external_identity_bindings',ARRAY['binding_id'],ARRAY['binding_id'])
        ), session_fks AS (
            SELECT c.* FROM pg_catalog.pg_constraint c JOIN expected_foreign_keys expected ON c.conname=expected.name
            JOIN pg_catalog.pg_class parent ON parent.oid=c.confrelid
            JOIN pg_catalog.pg_namespace parent_namespace ON parent_namespace.oid=parent.relnamespace
            WHERE c.conrelid=pg_catalog.to_regclass('weft.server_sessions')
                AND parent_namespace.nspname='weft' AND parent.relname=expected.parent AND c.contype='f' AND c.convalidated
                AND NOT c.condeferrable AND NOT c.condeferred
                AND c.confdeltype='r' AND c.confupdtype='a' AND c.confmatchtype='s'
                AND ARRAY(SELECT a.attname::text FROM unnest(c.conkey) WITH ORDINALITY key(attnum,position)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=key.attnum AND NOT a.attisdropped
                    ORDER BY key.position)=expected.child_columns
                AND ARRAY(SELECT a.attname::text FROM unnest(c.confkey) WITH ORDINALITY key(attnum,position)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=key.attnum AND NOT a.attisdropped
                    ORDER BY key.position)=expected.parent_columns
        ), intended_parent_trigger_candidates AS (
            SELECT t.oid,c.oid foreign_key,p.proname::text action_name
            FROM pg_catalog.pg_trigger t JOIN session_fks c ON c.oid=t.tgconstraint
            JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            WHERE t.tgrelid=c.confrelid AND t.tgconstrrelid=c.conrelid AND t.tgisinternal AND t.tgenabled='O'
                AND t.tgnargs=0 AND octet_length(t.tgargs)=0 AND t.tgattr::text='' AND t.tgqual IS NULL
                AND NOT t.tgdeferrable AND NOT t.tginitdeferred AND t.tgparentid=0
                AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL AND n.nspname='pg_catalog'
                AND ((p.proname='RI_FKey_restrict_del' AND t.tgtype=9)
                    OR (p.proname='RI_FKey_noaction_upd' AND t.tgtype=17))
        ), intended_parent_triggers AS (
            SELECT candidate.oid FROM intended_parent_trigger_candidates candidate
            WHERE (SELECT count(*) FROM intended_parent_trigger_candidates x
                    WHERE x.foreign_key=candidate.foreign_key AND x.action_name='RI_FKey_restrict_del')=1
                AND (SELECT count(*) FROM intended_parent_trigger_candidates x
                    WHERE x.foreign_key=candidate.foreign_key AND x.action_name='RI_FKey_noaction_upd')=1
        ), old_roles AS (
            SELECT * FROM pg_catalog.pg_roles WHERE rolname=ANY($3::text[])
        ), privilege_roles AS (
            SELECT name FROM unnest($3::text[]) name UNION ALL SELECT 'public'::text
        ), table_privileges AS (
            SELECT name FROM unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER',
                'SELECT WITH GRANT OPTION','INSERT WITH GRANT OPTION','UPDATE WITH GRANT OPTION','DELETE WITH GRANT OPTION',
                'TRUNCATE WITH GRANT OPTION','REFERENCES WITH GRANT OPTION','TRIGGER WITH GRANT OPTION']) name
        ), column_privileges AS (
            SELECT name FROM unnest(ARRAY['SELECT','INSERT','UPDATE','REFERENCES','SELECT WITH GRANT OPTION',
                'INSERT WITH GRANT OPTION','UPDATE WITH GRANT OPTION','REFERENCES WITH GRANT OPTION']) name
        ), session_delta AS (
            SELECT CASE WHEN NOT $1 THEN (SELECT count(*) FROM session_roles)=0 ELSE
                (SELECT count(*) FROM session_roles)=2
                AND (SELECT bool_and(rolcanlogin AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls
                    AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND rolconfig IS NULL)
                    FROM session_roles)
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m
                    WHERE m.member IN (SELECT oid FROM session_roles) OR m.roleid IN (SELECT oid FROM session_roles))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_db_role_setting s WHERE s.setrole IN (SELECT oid FROM session_roles))
                AND (SELECT count(*) FROM intended_column_grants)=13
                AND (SELECT count(*) FROM all_session_column_grants)=55
                AND NOT EXISTS (SELECT 1 FROM all_session_column_grants a
                    WHERE a.grantor<>(SELECT oid FROM owner_role) OR a.is_grantable
                        OR NOT EXISTS (SELECT 1 FROM expected_session_columns e WHERE e.role_name=a.role_name
                            AND e.relation_name=a.relation_name AND e.column_name=a.column_name
                            AND e.privilege_type=a.privilege_type))
                AND NOT EXISTS (SELECT 1 FROM expected_session_columns e WHERE NOT EXISTS (
                    SELECT 1 FROM all_session_column_grants a WHERE a.role_name=e.role_name
                        AND a.relation_name=e.relation_name AND a.column_name=e.column_name
                        AND a.privilege_type=e.privilege_type))
                AND (SELECT count(*) FROM intended_schema_grants)=2
                AND NOT EXISTS (SELECT 1 FROM schema_acls a WHERE a.grantee IN (SELECT oid FROM session_roles)
                    AND NOT EXISTS (SELECT 1 FROM intended_schema_grants i WHERE to_jsonb(i)=to_jsonb(a)))
                AND (SELECT count(*) FROM intended_database_grants)=2
                AND NOT EXISTS (SELECT 1 FROM database_acls a WHERE a.grantee IN (SELECT oid FROM session_roles)
                    AND NOT EXISTS (SELECT 1 FROM intended_database_grants i WHERE to_jsonb(i)=to_jsonb(a)))
                AND NOT EXISTS (SELECT 1 FROM application_relations r CROSS JOIN session_roles role
                    WHERE r.relowner=role.oid OR pg_catalog.has_table_privilege(role.oid,r.oid,
                        'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'))
                AND NOT EXISTS (SELECT 1 FROM application_relations r JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid
                    CROSS JOIN session_roles role WHERE a.attnum>0 AND NOT a.attisdropped
                      AND (pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,
                            'REFERENCES,SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION')
                        OR EXISTS (SELECT 1 FROM unnest(ARRAY['SELECT','INSERT','UPDATE']) privilege(name)
                            WHERE pg_catalog.has_column_privilege(role.oid,r.oid,a.attnum,privilege.name)
                              IS DISTINCT FROM EXISTS (SELECT 1 FROM expected_session_columns e
                                WHERE e.role_name=role.rolname AND e.relation_name=r.relname
                                    AND e.column_name=a.attname AND e.privilege_type=privilege.name))))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
                    CROSS JOIN session_roles role WHERE n.nspname='weft' AND (f.proowner=role.oid
                        OR pg_catalog.has_function_privilege(role.oid,f.oid,'EXECUTE,EXECUTE WITH GRANT OPTION')))
                AND (SELECT bool_and(pg_catalog.has_schema_privilege(oid,'weft','USAGE')
                    AND NOT pg_catalog.has_schema_privilege(oid,'weft','CREATE,USAGE WITH GRANT OPTION')
                    AND pg_catalog.has_database_privilege(oid,current_database(),'CONNECT')
                    AND NOT pg_catalog.has_database_privilege(oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION'))
                    FROM session_roles)
                END valid
        )
        SELECT jsonb_build_object(
            'acceptedDelta',($8 OR COALESCE((SELECT valid FROM session_delta),false))
                AND (SELECT count(*) FROM old_relations)=cardinality($2::text[])
                AND (SELECT count(*) FROM old_roles)=cardinality($3::text[])
                AND ARRAY(SELECT relname::text FROM source_relations ORDER BY relname)=
                    ARRAY(SELECT name FROM unnest($2::text[] || CASE WHEN $1 THEN ARRAY['server_sessions']::text[]
                        ELSE ARRAY[]::text[] END) name ORDER BY name)
                AND (SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations)=
                    ARRAY(SELECT generate_series(1,9 + CASE WHEN $1 THEN 11 ELSE 0 END))
                AND (SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations WHERE version<=9)=$5::text[]
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=9+1)=$6::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=11)=$12::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=12)=$17::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=13)=$18::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=14)=$19::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=15)=$20::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=16)=$21::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=17)=$22::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=18)=$23::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=19)=$24::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=20)=$25::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=21)=$26::text)
                AND (NOT $1 OR (SELECT checksum FROM weft.schema_migrations WHERE version=22)=$27::text)
                AND (SELECT count(*) FROM session_fks)=CASE WHEN $1 THEN 2 ELSE 0 END
                AND (SELECT count(*) FROM intended_parent_triggers)=CASE WHEN $1 THEN 4 ELSE 0 END
                AND (SELECT count(*) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='weft' AND p.proname=ANY($4::text[]))=CASE WHEN $1 THEN cardinality($4::text[]) ELSE 0 END
                AND NOT EXISTS (SELECT 1 FROM privilege_roles role CROSS JOIN source_relations r
                    WHERE role.name<>'weft_migrator' AND r.relname='server_sessions'
                      AND (pg_catalog.has_table_privilege(role.name,r.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(role.name,r.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS (SELECT 1 FROM privilege_roles role CROSS JOIN pg_catalog.pg_proc p
                    JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE role.name<>'weft_migrator' AND n.nspname='weft' AND p.proname=ANY($4::text[])
                      AND pg_catalog.has_function_privilege(role.name,p.oid,'EXECUTE,EXECUTE WITH GRANT OPTION')),
            'uninstalledProfiles',CASE WHEN $8 THEN jsonb_build_object(
                'attributes',(SELECT jsonb_agg(to_jsonb(r) ORDER BY rolname) FROM session_roles r),
                'membership',(SELECT jsonb_agg(to_jsonb(m) ORDER BY m.roleid,m.member,m.grantor)
                    FROM pg_catalog.pg_auth_members m
                    WHERE m.member IN (SELECT oid FROM session_roles) OR m.roleid IN (SELECT oid FROM session_roles)),
                'settings',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.setdatabase,s.setrole)
                    FROM pg_catalog.pg_db_role_setting s WHERE s.setrole IN (SELECT oid FROM session_roles)))
                ELSE NULL END,
            'relations',jsonb_build_object(
                'definitions',(SELECT jsonb_agg(jsonb_build_array(oid,relname,relowner,relkind,relpersistence,relispartition,
                    relrowsecurity,relforcerowsecurity,reloptions,relreplident,reltype,relam,relhasrules) ORDER BY relname) FROM old_relations),
                'ancestry',(SELECT jsonb_agg(to_jsonb(i) ORDER BY i.inhrelid,i.inhseqno) FROM pg_catalog.pg_inherits i
                    WHERE i.inhrelid IN (SELECT oid FROM old_relations) OR i.inhparent IN (SELECT oid FROM old_relations)),
                'schemaOwner',(SELECT nspowner FROM pg_catalog.pg_namespace WHERE nspname='weft')),
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
                WHERE NOT ($1 AND (t.oid IN (SELECT oid FROM intended_parent_triggers) OR EXISTS(
                    SELECT 1 FROM pg_constraint c WHERE c.oid=t.tgconstraint AND c.conrelid=to_regclass('weft.chat_conversations')
                    AND c.conname IN ('chat_conversations_creator_fkey','chat_conversations_revision_fkey','chat_conversations_projection_fkey','chat_conversations_validation_fkey'))
                OR EXISTS(SELECT 1 FROM pg_constraint c WHERE c.oid=t.tgconstraint AND t.tgisinternal AND c.contype='f'
                    AND (c.conrelid=to_regclass('weft.preview_runs') AND c.conname=ANY($16::text[])
                        OR c.conrelid IN (to_regclass('weft.regression_cases'),to_regclass('weft.regression_case_versions'),to_regclass('weft.regression_runs'))))))),
            'functions',(SELECT jsonb_agg(to_jsonb(f) ORDER BY f.oid) FROM old_functions f),
            'grants',jsonb_build_object(
                'table',(SELECT jsonb_agg(to_jsonb(a) ORDER BY relation_name,grantor,grantee,privilege_type,is_grantable) FROM table_acls a),
                'column',(SELECT jsonb_agg(to_jsonb(a) ORDER BY relation_name,column_name,grantor,grantee,privilege_type,is_grantable)
                    FROM column_acls a WHERE NOT ($1 AND EXISTS (SELECT 1 FROM intended_column_grants i WHERE to_jsonb(i)=to_jsonb(a)))),
                'schema',(SELECT jsonb_agg(to_jsonb(a) ORDER BY grantor,grantee,privilege_type,is_grantable)
                    FROM schema_acls a WHERE NOT ($1 AND EXISTS (SELECT 1 FROM intended_schema_grants i WHERE to_jsonb(i)=to_jsonb(a)))),
                'database',(SELECT jsonb_agg(to_jsonb(a) ORDER BY grantor,grantee,privilege_type,is_grantable)
                    FROM database_acls a WHERE NOT ($1 AND EXISTS (SELECT 1 FROM intended_database_grants i WHERE to_jsonb(i)=to_jsonb(a))))),
            'roles',jsonb_build_object(
                'attributes',(SELECT jsonb_agg(jsonb_build_array(oid,rolname,rolsuper,rolinherit,rolcreaterole,rolcreatedb,
                    rolcanlogin,rolreplication,rolbypassrls,rolconnlimit,rolvaliduntil,rolconfig) ORDER BY rolname) FROM old_roles),
                'membership',(SELECT jsonb_agg(to_jsonb(m) ORDER BY m.roleid,m.member,m.grantor) FROM pg_catalog.pg_auth_members m
                    WHERE m.member IN (SELECT oid FROM old_roles) OR m.roleid IN (SELECT oid FROM old_roles)),
                'defaultAcl',(SELECT jsonb_agg(to_jsonb(a) ORDER BY oid) FROM pg_catalog.pg_default_acl a
                    WHERE a.defaclnamespace IN (0,(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='weft'))),
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
                    pg_catalog.has_schema_privilege(role.name,'weft','USAGE'),pg_catalog.has_schema_privilege(role.name,'weft','CREATE'),
                    pg_catalog.has_schema_privilege(role.name,'weft','USAGE WITH GRANT OPTION,CREATE WITH GRANT OPTION'),
                    pg_catalog.has_database_privilege(role.name,current_database(),'CONNECT'),pg_catalog.has_database_privilege(role.name,current_database(),'CREATE'),
                    pg_catalog.has_database_privilege(role.name,current_database(),'TEMPORARY'),
                    pg_catalog.has_database_privilege(role.name,current_database(),'CONNECT WITH GRANT OPTION,CREATE WITH GRANT OPTION,TEMPORARY WITH GRANT OPTION'))
                    ORDER BY role.name) FROM privilege_roles role)))::text
        """;
}
