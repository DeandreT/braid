using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// Actual PostgreSQL role, schema and RLS evidence. Committed source and report
/// completion fixtures are explicitly synthetic metadata, never native/store proof.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRevisionDerivationSecurityTests(PostgresFixture fixture)
{
    private const string ReaderLogin = "weft_report_reader";
    private const string DeriverLogin = "weft_deriver";
    private const string KeyTable = "revision_derivations";
    private const string AttemptTable = "revision_derivation_attempts";
    private const string SourceIndex = "mapping_revisions_derivation_source_key";
    private const string NativeForeignKey = "revision_derivations_native_facts_fk";
    private static readonly string[] NewTables = [KeyTable, AttemptTable];
    private static readonly string[] ReadTables =
        ["organizations", "principals", "organization_memberships", "projects", "project_memberships", "mappings", "mapping_revisions", .. NewTables];
    private static readonly string[] ScopedTables = ReadTables.Where(table => table != "principals").ToArray();
    private static readonly string[] OldLogins =
        ["weft_runtime", "weft_ingest", "weft_catalog_reader", "weft_catalog_editor", "weft_project_creator"];
    private static readonly string[] KeyInsertColumns =
    [
        "organization_id", "project_id", "mapping_id", "revision_id", "operation_kind",
        "native_object_id", "native_class", "native_digest", "native_byte_count", "logical_entry_document",
        "dependency_manifest_id", "backend_id", "engine_revision", "backend_build", "adapter_build",
        "schema_version", "algorithm_version", "codec_version", "policy_schema_version", "policy_json", "policy_fingerprint",
        "native_byte_limit", "worker_response_byte_limit", "item_limit", "response_item_limit", "json_depth_limit",
        "detail_depth_limit", "diagnostic_limit", "validation_issue_limit", "text_byte_limit", "header_byte_limit",
        "stored_object_byte_limit", "response_byte_limit", "key_fingerprint"
    ];
    private static readonly string[] AttemptInsertColumns =
        ["organization_id", "project_id", "mapping_id", "revision_id", "cache_entry_id", "generation", "actor_principal_id", "claimant_instance_id", "repair_target_derivation_id"];
    private static readonly string[] KeyUpdateColumns = ["current_generation", "current_attempt_id", "selected_derivation_id"];
    private static readonly string[] AttemptUpdateColumns =
    [
        "completion_kind", "failure_code", "observation_present", "backend_status", "provenance_confirmed",
        "provenance_organization_id", "provenance_project_id", "provenance_mapping_id", "provenance_revision_id",
        "provenance_native_digest", "provenance_backend_id", "provenance_engine_revision", "provenance_backend_build", "provenance_adapter_build",
        "projection_coverage", "document_item_count", "document_diagnostic_count", "coverage_gap_count", "operation_diagnostic_count",
        "is_valid", "total_issue_count", "emitted_issue_count", "issues_truncated", "validation_diagnostic_count", "derived_digest", "derived_byte_count"
    ];
    private static readonly string[] SourceTuple =
        ["organization_id", "project_id", "mapping_id", "id", "native_object_id", "native_class", "native_digest", "native_byte_count", "logical_entry_document", "dependency_manifest_id", "backend_id"];

    [PostgresFact]
    public async Task DedicatedLoginsHaveExactlyNineReadRelationsAndOnlyTheNamedDeriverWriteColumns()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        await AssertHealthyAsync(test);
        foreach (var (login, connectionString) in Logins(test.Case))
        {
            await using var direct = NpgsqlDataSource.Create(connectionString);
            await using (var identity = direct.CreateCommand("SELECT session_user || ':' || current_user || ':' || current_database()"))
                Assert.Equal($"{login}:{login}:{test.Case.Name}", await identity.ExecuteScalarAsync());
            await using (var profile = direct.CreateCommand("""
                SELECT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreaterole
                  AND NOT rolcreatedb AND NOT rolinherit AND NOT rolreplication
                  AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member=role_row.oid OR roleid=role_row.oid)
                  AND has_database_privilege(current_user,current_database(),'CONNECT')
                  AND NOT has_database_privilege(current_user,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                  AND has_schema_privilege(current_user,'weft','USAGE')
                  AND NOT has_schema_privilege(current_user,'weft','CREATE,USAGE WITH GRANT OPTION')
                  AND NOT has_parameter_privilege(current_user,'session_replication_role','SET,ALTER SYSTEM')
                FROM pg_roles role_row WHERE rolname=current_user
                """))
                Assert.Equal(true, await profile.ExecuteScalarAsync());
            Assert.Equal(ReadTables.Order(StringComparer.Ordinal), await test.Case.ScalarAsync<string[]>("""
                SELECT array_agg(relation.relname::text ORDER BY relation.relname::text COLLATE "C")
                FROM pg_class relation JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
                WHERE namespace.nspname='weft' AND relation.relkind IN ('r','p','v','m','f')
                  AND has_table_privilege($1,relation.oid,'SELECT')
                """, login));
            foreach (var table in ReadTables)
            {
                Assert.True(await test.Case.ScalarAsync<bool>("""
                    SELECT has_table_privilege($1,$2,'SELECT')
                      AND NOT has_table_privilege($1,$2,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,SELECT WITH GRANT OPTION')
                      AND NOT has_any_column_privilege($1,$2,'REFERENCES,SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION')
                    """, login, $"weft.{table}"));
                var writable = login == DeriverLogin;
                Assert.Equal(writable && table == KeyTable ? KeyInsertColumns : writable && table == AttemptTable ? AttemptInsertColumns : [],
                    await GrantedColumnsAsync(test.Case, login, table, "INSERT"));
                Assert.Equal(writable && table == KeyTable ? KeyUpdateColumns : writable && table == AttemptTable ? AttemptUpdateColumns : [],
                    await GrantedColumnsAsync(test.Case, login, table, "UPDATE"));
            }
            Assert.True(await test.Case.ScalarAsync<bool>("""
                SELECT ARRAY(SELECT routine.proname::text FROM pg_proc routine JOIN pg_namespace namespace ON namespace.oid=routine.pronamespace
                         WHERE namespace.nspname='weft' AND has_function_privilege($1,routine.oid,'EXECUTE') ORDER BY routine.proname)
                    = CASE WHEN $1='weft_deriver' THEN ARRAY['index_catalog_report']::text[] ELSE ARRAY[]::text[] END
                  AND NOT EXISTS (SELECT 1 FROM pg_class relation JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
                         WHERE namespace.nspname='weft' AND CASE WHEN relation.relkind='S'
                         THEN has_sequence_privilege($1,relation.oid,'USAGE,SELECT,UPDATE') ELSE false END)
                  AND NOT EXISTS (SELECT 1 FROM pg_default_acl defaults CROSS JOIN LATERAL aclexplode(defaults.defaclacl) privilege
                         WHERE defaults.defaclnamespace IN (0,(SELECT oid FROM pg_namespace WHERE nspname='weft'))
                           AND privilege.grantee IN (0,(SELECT oid FROM pg_roles WHERE rolname=$1)))
                """, login));
        }
        Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task DirectLoginsCannotReadPrivateWorkOrWriteSourcesGeneratedFactsAndAdministrativeObjects()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var immutable = await test.ImmutableSourceSnapshotAsync();
        foreach (var (_, connectionString) in Logins(test.Case))
        {
            await using var direct = NpgsqlDataSource.Create(connectionString);
            foreach (var sql in new[]
            {
                "SELECT * FROM weft.revision_ingestions WHERE false", "SELECT evidence FROM weft.revision_ingestion_attempts WHERE false",
                "SELECT checksum FROM weft.schema_migrations", "SELECT can_create_projects FROM weft.organization_project_creation_capabilities WHERE false",
                "SELECT fingerprint FROM weft.project_creations WHERE false", "INSERT INTO weft.principals(id) VALUES (gen_random_uuid())",
                "UPDATE weft.projects SET name='forbidden' WHERE false", "UPDATE weft.project_memberships SET role='administrator' WHERE false",
                "UPDATE weft.mappings SET description='forbidden' WHERE false", "UPDATE weft.mapping_revisions SET native_digest=repeat('0',64) WHERE false",
                "INSERT INTO weft.revision_derivations(cache_entry_id) VALUES (gen_random_uuid())",
                "UPDATE weft.revision_derivations SET created_at=clock_timestamp() WHERE false",
                "UPDATE weft.revision_derivations SET key_fingerprint=repeat('0',64) WHERE false",
                "UPDATE weft.revision_derivations SET selected_usable=false WHERE false",
                "INSERT INTO weft.revision_derivation_attempts(claim_token) VALUES (gen_random_uuid())",
                "UPDATE weft.revision_derivation_attempts SET finished_at=clock_timestamp() WHERE false",
                "UPDATE weft.revision_derivation_attempts SET result_usable=true WHERE false",
                "UPDATE weft.revision_derivation_attempts SET candidate_object_id=gen_random_uuid() WHERE false",
                "DELETE FROM weft.revision_derivations WHERE false", "DELETE FROM weft.revision_derivation_attempts WHERE false",
                "TRUNCATE weft.revision_derivations", "SELECT weft.guard_revision_derivations_transition()",
                "SELECT weft.guard_revision_derivation_attempts_transition()", "SELECT weft.assert_project_creation_contract_v1()",
                "SET ROLE weft_migrator", "SET ROLE weft_runtime", "SET ROLE weft_project_creator",
                "SET session_replication_role=replica", "CREATE SCHEMA report_security_probe",
                "CREATE TABLE weft.report_security_probe(id integer)", "CREATE TEMP TABLE report_security_probe(id integer)"
            }) await AssertDirectDeniedAsync(direct, test.Revision.OrganizationId, sql);
            await AssertGrantCannotChangeProjectPrivilegesAsync(test.Case, direct, test.Revision.OrganizationId);
        }
        Assert.Equal(immutable, await test.ImmutableSourceSnapshotAsync());
        Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task GuardedPoolsUseOnlyNineApplicationLocksAndTheirDistinctSafeTransactionModes()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        foreach (var writable in new[] { false, true })
        {
            await using var transaction = await BeginAsync(test, writable);
            await using (var settings = transaction.CreateCommand("""
                SELECT current_setting('transaction_read_only'), current_setting('transaction_isolation'),
                  current_setting('lock_timeout'),current_setting('statement_timeout'),
                  current_setting('row_security'),current_setting('session_replication_role'),current_setting('weft.organization_id')
                """))
            await using (var row = await settings.ExecuteReaderAsync())
            {
                Assert.True(await row.ReadAsync());
                Assert.Equal(writable ? "off" : "on", row.GetString(0));
                Assert.Equal("read committed", row.GetString(1));
                Assert.Equal("1s", row.GetString(2));
                Assert.Equal("5s", row.GetString(3));
                Assert.Equal("on", row.GetString(4));
                Assert.Equal("origin", row.GetString(5));
                Assert.Equal(test.Revision.OrganizationId.ToString("D"), row.GetString(6));
            }
            await using var locks = transaction.CreateCommand("""
                SELECT array_agg(DISTINCT relation.relname::text ORDER BY relation.relname::text)
                FROM pg_locks held JOIN pg_class relation ON relation.oid=held.relation
                JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
                WHERE held.pid=pg_backend_pid() AND held.granted AND namespace.nspname='weft'
                  AND relation.relkind IN ('r','p','v','m','f')
                """);
            Assert.Equal(ReadTables.Order(StringComparer.Ordinal), Assert.IsType<string[]>(await locks.ExecuteScalarAsync()));
        }
    }

    [PostgresFact]
    public async Task SwappedOwnerExistingAndOtherDedicatedLoginsAreErrorsRatherThanEmptyReports()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        foreach (var connectionString in new[] { test.Case.MigrationConnectionString, test.Case.RuntimeConnectionString,
            test.Case.IngestionConnectionString, test.Case.CatalogConnectionString, test.Case.MetadataConnectionString,
            test.Case.ProjectCreationConnectionString, test.Case.DerivationConnectionString })
        {
            await using var pool = new PostgresRevisionReportDatabase(connectionString);
            var reader = new PostgresRevisionReportMetadataReader(pool);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetSourceAsync(test.Actor, test.Revision));
        }
        foreach (var connectionString in new[] { test.Case.MigrationConnectionString, test.Case.RuntimeConnectionString,
            test.Case.IngestionConnectionString, test.Case.CatalogConnectionString, test.Case.MetadataConnectionString,
            test.Case.ProjectCreationConnectionString, test.Case.ReportConnectionString })
        {
            await using var pool = new PostgresRevisionDerivationDatabase(connectionString);
            var store = new PostgresRevisionDerivationStore(pool);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryClaimAsync(test.Actor, test.Key, Guid.NewGuid()));
        }
        Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task UnsafeRoleAttributesReachableRolesAndDatabaseSchemaOrParameterRightsRefuseTheNextLease()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        foreach (var (login, _) in Logins(test.Case))
        {
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER ROLE {login} SUPERUSER", $"ALTER ROLE {login} NOSUPERUSER"),
                ($"ALTER ROLE {login} BYPASSRLS", $"ALTER ROLE {login} NOBYPASSRLS"),
                ($"ALTER ROLE {login} CREATEROLE", $"ALTER ROLE {login} NOCREATEROLE"),
                ($"ALTER ROLE {login} CREATEDB", $"ALTER ROLE {login} NOCREATEDB"),
                ($"ALTER ROLE {login} REPLICATION", $"ALTER ROLE {login} NOREPLICATION"),
                ($"ALTER ROLE {login} INHERIT", $"ALTER ROLE {login} NOINHERIT"),
                ($"ALTER ROLE {login} NOLOGIN", $"ALTER ROLE {login} LOGIN"),
                ($"GRANT weft_migrator TO {login}", $"REVOKE weft_migrator FROM {login}"),
                ($"GRANT pg_read_all_data TO {login}", $"REVOKE pg_read_all_data FROM {login}"),
                ($"GRANT pg_write_all_data TO {login}", $"REVOKE pg_write_all_data FROM {login}"),
                ($"GRANT SET ON PARAMETER session_replication_role TO {login}", $"REVOKE SET ON PARAMETER session_replication_role FROM {login}"),
                ($"GRANT CREATE ON DATABASE {test.Case.Name} TO {login}", $"REVOKE CREATE ON DATABASE {test.Case.Name} FROM {login}"),
                ($"GRANT TEMPORARY ON DATABASE {test.Case.Name} TO {login}", $"REVOKE TEMPORARY ON DATABASE {test.Case.Name} FROM {login}"),
                ($"GRANT CREATE ON SCHEMA weft TO {login}", $"REVOKE CREATE ON SCHEMA weft FROM {login}")
            }) await MutateAsync(test, alter, restore, login == DeriverLogin, bootstrap: true);
        }
    }

    [PostgresFact]
    public async Task ColumnTableSequenceFunctionAndGrantOptionChangesAreNeverAdoptedAsValidProfiles()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        await test.Case.ExecuteAsync("CREATE TABLE weft.derivation_grant_probe(id integer)");
        await test.Case.ExecuteAsync("CREATE SEQUENCE weft.derivation_grant_probe_sequence");
        await test.Case.ExecuteAsync("CREATE FUNCTION weft.derivation_grant_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
        await test.Case.ExecuteAsync("REVOKE ALL ON FUNCTION weft.derivation_grant_probe() FROM PUBLIC");
        try
        {
            foreach (var (login, _) in Logins(test.Case))
            {
                foreach (var (alter, restore) in new[]
                {
                    ($"GRANT SELECT(phase) ON weft.revision_ingestions TO {login}", $"REVOKE SELECT(phase) ON weft.revision_ingestions FROM {login}"),
                    ($"GRANT SELECT(evidence) ON weft.revision_ingestion_attempts TO {login}", $"REVOKE SELECT(evidence) ON weft.revision_ingestion_attempts FROM {login}"),
                    ($"GRANT SELECT ON weft.schema_migrations TO {login}", $"REVOKE SELECT ON weft.schema_migrations FROM {login}"),
                    ($"GRANT SELECT ON weft.organization_project_creation_capabilities TO {login}", $"REVOKE SELECT ON weft.organization_project_creation_capabilities FROM {login}"),
                    ($"GRANT UPDATE(name) ON weft.mappings TO {login}", $"REVOKE UPDATE(name) ON weft.mappings FROM {login}"),
                    ($"GRANT INSERT ON weft.revision_derivations TO {login}", RestoreColumnPrivilege(login, KeyTable, "INSERT", KeyInsertColumns)),
                    ($"GRANT UPDATE ON weft.revision_derivation_attempts TO {login}", RestoreColumnPrivilege(login, AttemptTable, "UPDATE", AttemptUpdateColumns)),
                    ($"GRANT INSERT(cache_entry_id) ON weft.revision_derivations TO {login}", $"REVOKE INSERT(cache_entry_id) ON weft.revision_derivations FROM {login}"),
                    ($"GRANT UPDATE(finished_at) ON weft.revision_derivation_attempts TO {login}", $"REVOKE UPDATE(finished_at) ON weft.revision_derivation_attempts FROM {login}"),
                    ($"GRANT DELETE,TRUNCATE,TRIGGER ON weft.revision_derivations TO {login}", $"REVOKE DELETE,TRUNCATE,TRIGGER ON weft.revision_derivations FROM {login}"),
                    ($"GRANT REFERENCES(id) ON weft.mapping_revisions TO {login}", $"REVOKE REFERENCES(id) ON weft.mapping_revisions FROM {login}"),
                    ($"GRANT SELECT ON weft.projects TO {login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR SELECT ON weft.projects FROM {login}"),
                    ($"GRANT SELECT(name) ON weft.projects TO {login} WITH GRANT OPTION", $"REVOKE SELECT(name) ON weft.projects FROM {login}"),
                    ($"GRANT USAGE ON SCHEMA weft TO {login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR USAGE ON SCHEMA weft FROM {login}"),
                    ($"GRANT CONNECT ON DATABASE {test.Case.Name} TO {login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE {test.Case.Name} FROM {login}"),
                    ($"GRANT SELECT(id) ON weft.derivation_grant_probe TO {login}", $"REVOKE SELECT(id) ON weft.derivation_grant_probe FROM {login}"),
                    ($"GRANT EXECUTE ON FUNCTION weft.derivation_grant_probe() TO {login}", $"REVOKE EXECUTE ON FUNCTION weft.derivation_grant_probe() FROM {login}"),
                    ($"GRANT USAGE ON SEQUENCE weft.derivation_grant_probe_sequence TO {login}", $"REVOKE USAGE ON SEQUENCE weft.derivation_grant_probe_sequence FROM {login}"),
                    ($"REVOKE SELECT ON weft.project_memberships FROM {login}", $"GRANT SELECT ON weft.project_memberships TO {login}")
                }) await MutateAsync(test, alter, restore, login == DeriverLogin);
            }
            foreach (var (table, column, privilege) in new[]
            {
                (KeyTable, "operation_kind", "INSERT"), (KeyTable, "current_generation", "UPDATE"),
                (AttemptTable, "actor_principal_id", "INSERT"), (AttemptTable, "failure_code", "UPDATE")
            })
            {
                await MutateAsync(test, $"REVOKE {privilege}({column}) ON weft.{table} FROM {DeriverLogin}",
                    $"GRANT {privilege}({column}) ON weft.{table} TO {DeriverLogin}", writable: true);
                await MutateAsync(test, $"GRANT {privilege}({column}) ON weft.{table} TO {DeriverLogin} WITH GRANT OPTION",
                    $"REVOKE GRANT OPTION FOR {privilege}({column}) ON weft.{table} FROM {DeriverLogin}", writable: true);
            }
        }
        finally
        {
            await test.Case.ExecuteAsync("DROP FUNCTION weft.derivation_grant_probe()");
            await test.Case.ExecuteAsync("DROP SEQUENCE weft.derivation_grant_probe_sequence");
            await test.Case.ExecuteAsync("DROP TABLE weft.derivation_grant_probe");
        }
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task NewAndSourceRelationOwnersAndInvokerFunctionOwnersMustRemainTheTrustedMigrator()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var role = "derivation_owner_" + Guid.NewGuid().ToString("N");
        await test.Case.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS");
        try
        {
            foreach (var login in new[] { ReaderLogin, DeriverLogin })
                Assert.False(await test.Case.ScalarAsync<bool>("SELECT pg_has_role($1,$2,'MEMBER')", login, role));
            foreach (var target in new[] { "SCHEMA weft", "TABLE weft.projects", "TABLE weft.mapping_revisions",
                "TABLE weft.revision_derivations", "TABLE weft.revision_derivation_attempts",
                "FUNCTION weft.guard_revision_derivations_transition()", "FUNCTION weft.guard_revision_derivation_attempts_transition()" })
                await MutateAsync(test, $"ALTER {target} OWNER TO {role}", $"ALTER {target} OWNER TO weft_migrator",
                    writable: null, bootstrap: true);
        }
        finally { await test.Case.BootstrapAsync($"DROP ROLE {role}"); }
    }

    [PostgresFact]
    public async Task EveryScopedSourceAndNewTableRequiresRlsAndExactNewPublicAllPolicies()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        foreach (var table in ScopedTables)
            await MutateAsync(test, $"ALTER TABLE weft.{table} DISABLE ROW LEVEL SECURITY",
                $"ALTER TABLE weft.{table} ENABLE ROW LEVEL SECURITY", writable: null);
        await MutateAsync(test, "ALTER TABLE weft.principals ENABLE ROW LEVEL SECURITY",
            "ALTER TABLE weft.principals DISABLE ROW LEVEL SECURITY", writable: null);
        foreach (var table in NewTables)
        {
            var policy = $"{table}_organization_scope";
            const string expression = "organization_id = NULLIF(current_setting('weft.organization_id',true),'')::uuid";
            var recreate = $"CREATE POLICY {policy} ON weft.{table} USING ({expression}) WITH CHECK ({expression})";
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE weft.{table} FORCE ROW LEVEL SECURITY", $"ALTER TABLE weft.{table} NO FORCE ROW LEVEL SECURITY"),
                ($"DROP POLICY {policy} ON weft.{table}", recreate),
                ($"ALTER POLICY {policy} ON weft.{table} USING (true)", $"ALTER POLICY {policy} ON weft.{table} USING ({expression})"),
                ($"ALTER POLICY {policy} ON weft.{table} WITH CHECK (true)", $"ALTER POLICY {policy} ON weft.{table} WITH CHECK ({expression})"),
                ($"CREATE POLICY derivation_unscoped_probe ON weft.{table} USING (true)", $"DROP POLICY derivation_unscoped_probe ON weft.{table}"),
                ($"ALTER POLICY {policy} ON weft.{table} TO {ReaderLogin}", $"ALTER POLICY {policy} ON weft.{table} TO PUBLIC"),
                ($"DROP POLICY {policy} ON weft.{table}; CREATE POLICY {policy} ON weft.{table} FOR SELECT USING ({expression})",
                    $"DROP POLICY {policy} ON weft.{table}; {recreate}"),
                ($"DROP POLICY {policy} ON weft.{table}; CREATE POLICY {policy} ON weft.{table} AS RESTRICTIVE USING ({expression}) WITH CHECK ({expression})",
                    $"DROP POLICY {policy} ON weft.{table}; {recreate}")
            }) await MutateAsync(test, alter, restore, writable: null);
        }
    }

    [PostgresFact]
    public async Task ActualNewMetadataRowsAreInvisibleWithoutTheirExactOrganizationBinding()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        Assert.Equal(new long[] { 1, 1, 0, 0 }, await test.CountsAsync());
        foreach (var (_, connectionString) in Logins(test.Case))
        {
            await using var direct = NpgsqlDataSource.Create(connectionString);
            foreach (var organization in new Guid?[] { null, test.Seed.OrganizationB, test.Revision.OrganizationId })
            {
                await using var connection = await direct.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                if (organization is { } selected)
                {
                    await using var bind = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true)", connection, transaction);
                    bind.Parameters.AddWithValue(selected.ToString("D"));
                    await bind.ExecuteScalarAsync();
                }
                foreach (var table in NewTables)
                {
                    await using var read = new NpgsqlCommand($"SELECT count(*) FROM weft.{table}", connection, transaction);
                    Assert.Equal(organization == test.Revision.OrganizationId ? 1L : 0L, await read.ExecuteScalarAsync());
                }
                if (organization == test.Revision.OrganizationId)
                {
                    await using var read = new NpgsqlCommand("SELECT derivation_id FROM weft.revision_derivation_attempts", connection, transaction);
                    Assert.Equal(claim.DerivationId, await read.ExecuteScalarAsync());
                }
            }
        }
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task TransitionBodySecurityModeSearchPathShapeAndExecutionGrantsCannotBeWeakened()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        foreach (var table in NewTables)
        {
            var trigger = $"{table}_transition_guard";
            var function = $"weft.guard_{table}_transition()";
            var definition = await test.Case.ScalarAsync<string>(
                "SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid=$1::regclass AND tgname=$2", $"weft.{table}", trigger);
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE weft.{table} DISABLE TRIGGER {trigger}", $"ALTER TABLE weft.{table} ENABLE TRIGGER {trigger}"),
                ($"ALTER TABLE weft.{table} ENABLE ALWAYS TRIGGER {trigger}", $"ALTER TABLE weft.{table} ENABLE TRIGGER {trigger}"),
                ($"ALTER TABLE weft.{table} ENABLE REPLICA TRIGGER {trigger}", $"ALTER TABLE weft.{table} ENABLE TRIGGER {trigger}"),
                ($"DROP TRIGGER {trigger} ON weft.{table}", definition),
                ($"CREATE OR REPLACE FUNCTION {function} RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,weft,pg_temp AS $$ BEGIN RETURN NEW; END; $$",
                    SourceFunction($"guard_{table}_transition")),
                ($"ALTER FUNCTION {function} SECURITY DEFINER", $"ALTER FUNCTION {function} SECURITY INVOKER"),
                ($"ALTER FUNCTION {function} SET search_path=public,weft", $"ALTER FUNCTION {function} SET search_path=pg_catalog,weft,pg_temp"),
                ($"GRANT EXECUTE ON FUNCTION {function} TO {ReaderLogin}", $"REVOKE EXECUTE ON FUNCTION {function} FROM {ReaderLogin}"),
                ($"GRANT EXECUTE ON FUNCTION {function} TO {DeriverLogin}", $"REVOKE EXECUTE ON FUNCTION {function} FROM {DeriverLogin}"),
                ($"GRANT EXECUTE ON FUNCTION {function} TO PUBLIC", $"REVOKE EXECUTE ON FUNCTION {function} FROM PUBLIC"),
                ($"CREATE TRIGGER derivation_extra_probe BEFORE UPDATE ON weft.{table} FOR EACH ROW EXECUTE FUNCTION {function}",
                    $"DROP TRIGGER derivation_extra_probe ON weft.{table}"),
                ($"DROP TRIGGER {trigger} ON weft.{table}; CREATE TRIGGER {trigger} BEFORE UPDATE ON weft.{table} FOR EACH ROW EXECUTE FUNCTION {function}",
                    $"DROP TRIGGER {trigger} ON weft.{table}; {definition}"),
                ($"DROP TRIGGER {trigger} ON weft.{table}; CREATE TRIGGER {trigger} BEFORE INSERT OR UPDATE OR DELETE ON weft.{table} FOR EACH ROW WHEN (true) EXECUTE FUNCTION {function}",
                    $"DROP TRIGGER {trigger} ON weft.{table}; {definition}")
            })
            {
                var target = alter.Contains($" TO {ReaderLogin}", StringComparison.Ordinal) ? false :
                    alter.Contains($" TO {DeriverLogin}", StringComparison.Ordinal) ? (bool?)true : null;
                await MutateAsync(test, alter, restore, target);
            }
        }
        foreach (var table in new[] { KeyTable, AttemptTable, "mapping_revisions" })
        {
            var internalTrigger = await test.Case.ScalarAsync<string>("""
                SELECT tgname FROM pg_trigger WHERE tgrelid=$1::regclass AND tgisinternal AND tgconstraint<>0
                ORDER BY tgname LIMIT 1
                """, $"weft.{table}");
            await MutateAsync(test, $"ALTER TABLE weft.{table} DISABLE TRIGGER \"{internalTrigger}\"",
                $"ALTER TABLE weft.{table} ENABLE TRIGGER \"{internalTrigger}\"", writable: null, bootstrap: true);
        }
    }

    [PostgresFact]
    public async Task AllNewChecksMustRemainValidatedAndInheritableWithoutMisclassifyingOrdinaryKeys()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        Assert.True(await test.Case.ScalarAsync<bool>("""
            SELECT bool_and(connoinherit) FROM pg_constraint
            WHERE conrelid IN ('weft.revision_derivations'::regclass,'weft.revision_derivation_attempts'::regclass)
              AND contype IN ('p','u','f')
            """));
        await AssertHealthyAsync(test);
        foreach (var table in NewTables)
        {
            var checks = SourceCheckNames(table);
            Assert.Equal(table == KeyTable ? 9 : 11, checks.Length);
            foreach (var constraint in checks)
            {
                var source = SourceConstraint(table, constraint);
                var replace = $"ALTER TABLE weft.{table} DROP CONSTRAINT {constraint}; ALTER TABLE weft.{table} ADD CONSTRAINT {constraint} ";
                var accepted = await test.Case.ScalarAsync<string>(
                    "SELECT pg_get_constraintdef(oid,false) FROM pg_constraint WHERE conrelid=$1::regclass AND conname=$2", $"weft.{table}", constraint);
                await MutateAsync(test, replace + source + " NO INHERIT", replace + source, writable: null,
                    auditRestoration: async () => Assert.Equal(accepted, await test.Case.ScalarAsync<string>(
                        "SELECT pg_get_constraintdef(oid,false) FROM pg_constraint WHERE conrelid=$1::regclass AND conname=$2", $"weft.{table}", constraint)));
            }
            var probe = table == KeyTable ? "revision_derivations_native_check" : "revision_derivation_attempts_lease_check";
            var prefix = $"ALTER TABLE weft.{table} DROP CONSTRAINT {probe}; ALTER TABLE weft.{table} ADD CONSTRAINT {probe} ";
            await MutateAsync(test, prefix + "CHECK (true)", prefix + SourceConstraint(table, probe), writable: null);
            await MutateAsync(test, prefix + SourceConstraint(table, probe) + " NOT VALID", prefix + SourceConstraint(table, probe), writable: null);
            await MutateAsync(test, $"ALTER TABLE weft.{table} ADD CONSTRAINT derivation_extra_check CHECK (true)",
                $"ALTER TABLE weft.{table} DROP CONSTRAINT derivation_extra_check", writable: null);
        }
    }

    [PostgresFact]
    public async Task FullNativeTupleIndexAndForeignKeyCannotBeReplacedWithWeakerOrDeferredAncestry()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        Assert.Equal(SourceTuple.Select(column => column == "id" ? "revision_id" : column),
            await test.Case.ScalarAsync<string[]>("""
                SELECT array_agg(attribute.attname::text ORDER BY key.ordinality)
                FROM pg_constraint foreign_key CROSS JOIN LATERAL unnest(foreign_key.conkey) WITH ORDINALITY key(attnum,ordinality)
                JOIN pg_attribute attribute ON attribute.attrelid=foreign_key.conrelid AND attribute.attnum=key.attnum
                WHERE foreign_key.conrelid='weft.revision_derivations'::regclass AND foreign_key.conname=$1
                """, NativeForeignKey));
        Assert.Equal(SourceTuple, await test.Case.ScalarAsync<string[]>("""
            SELECT array_agg(attribute.attname::text ORDER BY key.ordinality)
            FROM pg_constraint foreign_key CROSS JOIN LATERAL unnest(foreign_key.confkey) WITH ORDINALITY key(attnum,ordinality)
            JOIN pg_attribute attribute ON attribute.attrelid=foreign_key.confrelid AND attribute.attnum=key.attnum
            WHERE foreign_key.conrelid='weft.revision_derivations'::regclass AND foreign_key.conname=$1
            """, NativeForeignKey));
        Assert.True(await test.Case.ScalarAsync<bool>("""
            SELECT contype='f' AND confrelid='weft.mapping_revisions'::regclass
              AND conindid='weft.mapping_revisions_derivation_source_key'::regclass
              AND convalidated AND conislocal AND coninhcount=0 AND conparentid=0
              AND NOT condeferrable AND NOT condeferred AND confmatchtype='s' AND confdeltype='r' AND confupdtype='r'
            FROM pg_constraint WHERE conrelid='weft.revision_derivations'::regclass AND conname=$1
            """, NativeForeignKey));
        Assert.Equal(SourceTuple, await test.Case.ScalarAsync<string[]>("""
            SELECT array_agg(attribute.attname::text ORDER BY key.ordinality)
            FROM pg_index index_row CROSS JOIN LATERAL unnest(index_row.indkey) WITH ORDINALITY key(attnum,ordinality)
            JOIN pg_attribute attribute ON attribute.attrelid=index_row.indrelid AND attribute.attnum=key.attnum
            WHERE index_row.indexrelid='weft.mapping_revisions_derivation_source_key'::regclass
            """));
        Assert.True(await test.Case.ScalarAsync<bool>("""
            SELECT index_row.indisunique AND index_row.indisvalid AND index_row.indisready AND index_row.indimmediate
              AND index_row.indnkeyatts=11 AND index_row.indnatts=11 AND index_row.indpred IS NULL
              AND index_row.indexprs IS NULL AND access_method.amname='btree'
              AND relation.relowner=(SELECT oid FROM pg_roles WHERE rolname='weft_migrator')
            FROM pg_index index_row JOIN pg_class relation ON relation.oid=index_row.indexrelid
            JOIN pg_am access_method ON access_method.oid=relation.relam
            WHERE index_row.indexrelid='weft.mapping_revisions_derivation_source_key'::regclass
            """));
        var nativeDefinition = SourceConstraint(KeyTable, NativeForeignKey);
        var replace = $"ALTER TABLE weft.{KeyTable} DROP CONSTRAINT {NativeForeignKey}; ALTER TABLE weft.{KeyTable} ADD CONSTRAINT {NativeForeignKey} ";
        foreach (var altered in new[] { nativeDefinition + " NOT VALID", nativeDefinition + " DEFERRABLE INITIALLY IMMEDIATE",
            nativeDefinition.Replace("ON DELETE RESTRICT", "ON DELETE CASCADE", StringComparison.Ordinal),
            "FOREIGN KEY (organization_id,project_id,mapping_id,revision_id) REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id) ON DELETE RESTRICT ON UPDATE RESTRICT" })
            await MutateAsync(test, replace + altered, replace + nativeDefinition, writable: null);
        var displaced = SourceIndex + "_displaced";
        foreach (var definition in new[]
        {
            $"CREATE INDEX {SourceIndex} ON weft.mapping_revisions ({string.Join(',', SourceTuple)})",
            $"CREATE UNIQUE INDEX {SourceIndex} ON weft.mapping_revisions ({string.Join(',', SourceTuple.Reverse())})",
            $"CREATE UNIQUE INDEX {SourceIndex} ON weft.mapping_revisions ({string.Join(',', SourceTuple)}) INCLUDE (sequence)",
            $"CREATE UNIQUE INDEX {SourceIndex} ON weft.mapping_revisions ({string.Join(',', SourceTuple)}) WHERE native_byte_count>0"
        }) await MutateAsync(test, $"ALTER INDEX weft.{SourceIndex} RENAME TO {displaced}; {definition}",
            $"DROP INDEX weft.{SourceIndex}; ALTER INDEX weft.{displaced} RENAME TO {SourceIndex}", writable: null);
        foreach (var (table, constraint) in new[] { (KeyTable, "revision_derivations_current_fk"),
            (KeyTable, "revision_derivations_selected_fk"), (AttemptTable, "revision_derivation_attempts_entry_fk"),
            (AttemptTable, "revision_derivation_attempts_actor_fk"), (AttemptTable, "revision_derivation_attempts_repair_target_fk") })
        {
            var source = SourceConstraint(table, constraint);
            var prefix = $"ALTER TABLE weft.{table} DROP CONSTRAINT {constraint}; ALTER TABLE weft.{table} ADD CONSTRAINT {constraint} ";
            await MutateAsync(test, prefix + source + " NOT VALID", prefix + source, writable: null);
        }
    }

    [PostgresFact]
    public async Task ColumnShapesDefaultsAndRewriteRulesCannotWidenTheAdmittedSchema()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        foreach (var table in NewTables)
        {
            await MutateAsync(test, $"ALTER TABLE weft.{table} ADD COLUMN derivation_unknown_column integer",
                $"ALTER TABLE weft.{table} DROP COLUMN derivation_unknown_column", writable: null);
            foreach (var login in new[] { ReaderLogin, DeriverLogin })
                await MutateAsync(test, $"ALTER TABLE weft.{table} ADD COLUMN derivation_extra_grant integer; GRANT UPDATE(derivation_extra_grant) ON weft.{table} TO {login}",
                    $"ALTER TABLE weft.{table} DROP COLUMN derivation_extra_grant", login == DeriverLogin);
        }
        foreach (var (alter, restore) in new[]
        {
            ("ALTER TABLE weft.revision_derivations ALTER COLUMN algorithm_version DROP NOT NULL", "ALTER TABLE weft.revision_derivations ALTER COLUMN algorithm_version SET NOT NULL"),
            ("ALTER TABLE weft.revision_derivations ALTER COLUMN current_generation SET DEFAULT 1", "ALTER TABLE weft.revision_derivations ALTER COLUMN current_generation SET DEFAULT 0"),
            ("ALTER TABLE weft.revision_derivation_attempts ALTER COLUMN derived_class SET DEFAULT 'native'", "ALTER TABLE weft.revision_derivation_attempts ALTER COLUMN derived_class SET DEFAULT 'derived'"),
            ("ALTER TABLE weft.revision_derivation_attempts ALTER COLUMN repair_target_derivation_id SET DEFAULT gen_random_uuid()", "ALTER TABLE weft.revision_derivation_attempts ALTER COLUMN repair_target_derivation_id DROP DEFAULT"),
            ("CREATE RULE derivation_rewrite_probe AS ON UPDATE TO weft.revision_derivations DO ALSO SELECT 1", "DROP RULE derivation_rewrite_probe ON weft.revision_derivations")
        }) await MutateAsync(test, alter, restore, writable: null);
    }

    [PostgresFact]
    public async Task EachOfTheElevenSourceFactsIsProtectedByTheActualImmediateNativeForeignKey()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        var original = await test.ImmutableSourceSnapshotAsync();
        var counts = await test.CountsAsync();
        // Isolate the FK from the new trigger and native CHECK using only the
        // owner fixture. No forged row commits, and application guards must
        // reject this temporary schema before the independent SQL probes.
        await test.Case.ExecuteAsync("ALTER TABLE weft.revision_derivations DISABLE TRIGGER revision_derivations_transition_guard; ALTER TABLE weft.revision_derivations DROP CONSTRAINT revision_derivations_native_check");
        try
        {
            await AssertRefusedAsync(test, writable: null);
            await AssertNativeCloneAsync(test, claim.CacheEntryId, changedColumn: null);
            foreach (var column in SourceTuple.Select(column => column == "id" ? "revision_id" : column))
                await AssertNativeCloneAsync(test, claim.CacheEntryId, column);
        }
        finally
        {
            await test.Case.ExecuteAsync("ALTER TABLE weft.revision_derivations ADD CONSTRAINT revision_derivations_native_check " +
                SourceConstraint(KeyTable, "revision_derivations_native_check") +
                "; ALTER TABLE weft.revision_derivations ENABLE TRIGGER revision_derivations_transition_guard");
        }
        Assert.Equal(counts, await test.CountsAsync());
        Assert.Equal(original, await test.ImmutableSourceSnapshotAsync());
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task ExistingProfilesGainNoReportRightsAndExistingGuardedCatalogAndCreationReadsStillWork()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        foreach (var login in OldLogins)
        {
            foreach (var table in NewTables)
                Assert.True(await test.Case.ScalarAsync<bool>("""
                    SELECT NOT has_table_privilege($1,$2,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                      AND NOT has_any_column_privilege($1,$2,'SELECT,INSERT,UPDATE,REFERENCES')
                    """, login, $"weft.{table}"));
            foreach (var table in NewTables)
                Assert.False(await test.Case.ScalarAsync<bool>("SELECT has_function_privilege($1,$2,'EXECUTE')",
                    login, $"weft.guard_{table}_transition()"));
        }
        await using (var catalog = new PostgresCatalogDatabase(test.Case.CatalogConnectionString))
            Assert.NotNull(await new PostgresCatalogReader(catalog).GetRevisionAsync(test.Actor, test.Revision));
        await using (var creator = new PostgresProjectCreationDatabase(test.Case.ProjectCreationConnectionString))
            Assert.NotNull(await new PostgresProjectCreator(creator).GetAdmissionAsync(test.Actor, test.Revision.OrganizationId));
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task PublicDefaultAndEvenNoninheritedRoleMembershipCannotWidenEitherDedicatedLogin()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        await test.Case.ExecuteAsync("CREATE SEQUENCE weft.derivation_public_sequence");
        try
        {
            foreach (var (alter, restore) in new[]
            {
                ("GRANT SELECT(phase) ON weft.revision_ingestions TO PUBLIC", "REVOKE SELECT(phase) ON weft.revision_ingestions FROM PUBLIC"),
                ("GRANT SELECT ON weft.revision_derivations TO PUBLIC", "REVOKE SELECT ON weft.revision_derivations FROM PUBLIC"),
                ("GRANT UPDATE(name) ON weft.mappings TO PUBLIC", "REVOKE UPDATE(name) ON weft.mappings FROM PUBLIC"),
                ("GRANT INSERT(cache_entry_id) ON weft.revision_derivations TO PUBLIC", "REVOKE INSERT(cache_entry_id) ON weft.revision_derivations FROM PUBLIC"),
                ("GRANT SELECT ON SEQUENCE weft.derivation_public_sequence TO PUBLIC", "REVOKE SELECT ON SEQUENCE weft.derivation_public_sequence FROM PUBLIC"),
                ("GRANT USAGE ON SCHEMA weft TO PUBLIC", "REVOKE USAGE ON SCHEMA weft FROM PUBLIC"),
                ("ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT UPDATE ON TABLES TO PUBLIC", "ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE UPDATE ON TABLES FROM PUBLIC")
            }) await MutateAsync(test, alter, restore, writable: null);
            foreach (var (login, _) in Logins(test.Case))
            {
                foreach (var (alter, restore) in new[]
                {
                    ($"ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT SELECT ON TABLES TO {login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE SELECT ON TABLES FROM {login}"),
                    ($"ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT EXECUTE ON FUNCTIONS TO {login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE EXECUTE ON FUNCTIONS FROM {login}")
                }) await MutateAsync(test, alter, restore, login == DeriverLogin);
            }
            var role = "derivation_attack_" + Guid.NewGuid().ToString("N");
            await test.Case.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS");
            try
            {
                await test.Case.ExecuteAsync($"GRANT SELECT(evidence) ON weft.revision_ingestion_attempts TO {role}");
                foreach (var (login, _) in Logins(test.Case))
                {
                    await MutateAsync(test, $"GRANT {role} TO {login}", $"REVOKE {role} FROM {login}", login == DeriverLogin, bootstrap: true);
                    await MutateAsync(test, $"GRANT {role} TO {login} WITH ADMIN OPTION", $"REVOKE {role} FROM {login}", login == DeriverLogin, bootstrap: true);
                }
            }
            finally
            {
                await test.Case.ExecuteAsync($"REVOKE SELECT(evidence) ON weft.revision_ingestion_attempts FROM {role}");
                await test.Case.BootstrapAsync($"DROP ROLE {role}");
            }
        }
        finally { await test.Case.ExecuteAsync("DROP SEQUENCE weft.derivation_public_sequence"); }
        await AssertHealthyAsync(test);
    }

    private static IEnumerable<(string Login, string ConnectionString)> Logins(PostgresCase test) =>
        [(ReaderLogin, test.ReportConnectionString), (DeriverLogin, test.DerivationConnectionString)];

    private static Task<string[]> GrantedColumnsAsync(PostgresCase test, string login, string table, string privilege) =>
        test.ScalarAsync<string[]>("""
            SELECT coalesce(array_agg(attribute.attname::text ORDER BY attribute.attnum),ARRAY[]::text[])
            FROM pg_attribute attribute WHERE attribute.attrelid=$2::regclass AND attribute.attnum>0 AND NOT attribute.attisdropped
              AND has_column_privilege($1,$2,attribute.attname,$3)
            """, login, $"weft.{table}", privilege);

    private static string RestoreColumnPrivilege(string login, string table, string privilege, string[] columns) =>
        $"REVOKE {privilege} ON weft.{table} FROM {login}" + (login == DeriverLogin
            ? $"; GRANT {privilege}({string.Join(',', columns)}) ON weft.{table} TO {login}" : "");

    private static string SourceFunction(string name)
    {
        var sql = MigrationRunner.EmbeddedMigrations.Single(migration => migration.Version == 6).Sql;
        var declarations = Regex.Matches(sql, @"^CREATE(?: OR REPLACE)? FUNCTION weft\." + Regex.Escape(name) +
            @"\(.*?\bAS \$(?<delimiter>[A-Za-z_][A-Za-z_0-9]*)\$.*?\$\k<delimiter>\$;",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var source = Assert.Single(declarations.Cast<Match>()).Value;
        return source.StartsWith("CREATE OR REPLACE", StringComparison.Ordinal) ? source :
            "CREATE OR REPLACE" + source["CREATE".Length..];
    }

    private static string TableDeclaration(string table)
    {
        var sql = MigrationRunner.EmbeddedMigrations.Single(migration => migration.Version == 6).Sql;
        var start = sql.IndexOf($"CREATE TABLE weft.{table} (", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The intended source table {table} is missing.");
        var end = sql.IndexOf("\n);", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The intended source table {table} is incomplete.");
        return sql[start..end];
    }

    private static string[] SourceCheckNames(string table) => Regex.Matches(TableDeclaration(table),
        @"\bCONSTRAINT (?<name>[a-z_]+)\s+CHECK\s*\(", RegexOptions.CultureInvariant)
        .Select(match => match.Groups["name"].Value).ToArray();

    private static string SourceConstraint(string table, string constraint)
    {
        var declaration = Regex.Match(TableDeclaration(table), @"\bCONSTRAINT " + Regex.Escape(constraint) +
            @"\s+(?<definition>.*?)(?=,\s*\n\s*CONSTRAINT |\z)", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (declaration.Success) return declaration.Groups["definition"].Value.Trim();
        var sql = MigrationRunner.EmbeddedMigrations.Single(migration => migration.Version == 6).Sql;
        var added = Regex.Match(sql, @"ALTER TABLE weft\." + Regex.Escape(table) + @" ADD CONSTRAINT " + Regex.Escape(constraint) +
            @"\s+(?<definition>[^;]+);", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(added.Success, $"The intended source constraint {constraint} is missing.");
        return added.Groups["definition"].Value.Trim();
    }

    private static async Task AssertNativeCloneAsync(PostgresRevisionDerivationTestSupport test, Guid cacheEntryId, string? changedColumn)
    {
        var columns = KeyInsertColumns.Concat(new[] { "cache_entry_id", "created_at" }).ToArray();
        var values = columns.Select(column => column == "key_fingerprint" ? "$1::text" : column == "cache_entry_id" ? "gen_random_uuid()" :
            column == changedColumn ? column switch
            {
                "organization_id" or "project_id" or "mapping_id" or "revision_id" or "native_object_id" => "gen_random_uuid()",
                "native_byte_count" => "source.native_byte_count+1",
                "native_digest" => "repeat('f',64)",
                _ => "'changed-source-fact'"
            } : $"source.{column}");
        await using var connection = await test.Case.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var clone = new NpgsqlCommand($"INSERT INTO weft.revision_derivations({string.Join(',', columns)}) SELECT {string.Join(',', values)} FROM weft.revision_derivations source WHERE source.cache_entry_id=$2", connection, transaction);
        var probeFingerprint = Sha256Digest.FromBytes(Encoding.UTF8.GetBytes("isolated FK probe " + changedColumn)).Value;
        Assert.NotEqual(test.Key.Fingerprint.Value, probeFingerprint);
        clone.Parameters.AddWithValue(probeFingerprint);
        clone.Parameters.AddWithValue(cacheEntryId);
        if (changedColumn is null)
            Assert.Equal(1, await clone.ExecuteNonQueryAsync());
        else
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => clone.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
            Assert.Equal(NativeForeignKey, error.ConstraintName);
        }
        // Dispose rolls back the valid control as well as every failed probe.
    }

    private static Task<PostgresTransaction> BeginAsync(PostgresRevisionDerivationTestSupport test, bool writable) =>
        writable ? test.DerivationDatabase.BeginTransactionAsync(test.Revision.OrganizationId) :
            test.ReportDatabase.BeginTransactionAsync(test.Revision.OrganizationId);

    private static async Task AssertHealthyAsync(PostgresRevisionDerivationTestSupport test)
    {
        foreach (var writable in new[] { false, true })
        {
            await using var transaction = await BeginAsync(test, writable);
            await using var verify = transaction.CreateCommand("SELECT current_user,current_setting('weft.organization_id')");
            await using var row = await verify.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(writable ? DeriverLogin : ReaderLogin, row.GetString(0));
            Assert.Equal(test.Revision.OrganizationId.ToString("D"), row.GetString(1));
        }
        var source = Assert.IsType<AuthorizedRevisionReportSource>(await test.Reader.GetSourceAsync(test.Actor, test.Revision));
        Assert.Equal(test.Source, source.Source);
    }

    private static async Task AssertRefusedAsync(PostgresRevisionDerivationTestSupport test, bool? writable)
    {
        foreach (var target in writable is { } single ? new[] { single } : new[] { false, true })
            await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var unexpected = await BeginAsync(test, target); });
    }

    private static async Task MutateAsync(PostgresRevisionDerivationTestSupport test, string alter, string restore,
        bool? writable, bool bootstrap = false, Func<Task>? auditRestoration = null)
    {
        if (bootstrap) await test.Case.BootstrapAsync(alter); else await test.Case.ExecuteAsync(alter);
        try { await AssertRefusedAsync(test, writable); }
        catch (Exception failure) { throw new InvalidOperationException($"Revision derivation admission probe failed after: {alter}", failure); }
        finally { if (bootstrap) await test.Case.BootstrapAsync(restore); else await test.Case.ExecuteAsync(restore); }
        if (auditRestoration is not null) await auditRestoration();
        await AssertHealthyAsync(test);
    }

    private static async Task AssertDirectDeniedAsync(NpgsqlDataSource source, Guid organization, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var bind = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true)", connection, transaction))
        {
            bind.Parameters.AddWithValue(organization.ToString("D"));
            await bind.ExecuteScalarAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresException error;
        try { error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync()); }
        catch (Exception failure)
        {
            throw new InvalidOperationException($"Direct revision metadata statement did not produce its required refusal: {sql}", failure);
        }
        Assert.True(error.SqlState == PostgresErrorCodes.InsufficientPrivilege,
            $"Direct revision metadata statement had SQLSTATE {error.SqlState}: {sql}");
    }

    private static async Task AssertGrantCannotChangeProjectPrivilegesAsync(PostgresCase test, NpgsqlDataSource source, Guid organization)
    {
        var ownerBefore = await test.ScalarAsync<string>(ProjectAclSnapshotSql);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var bind = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true)", connection, transaction))
        {
            bind.Parameters.AddWithValue(organization.ToString("D"));
            await bind.ExecuteScalarAsync();
        }
        await using (var rights = new NpgsqlCommand(ProjectUnchangedRightsSql, connection, transaction))
            Assert.Equal(true, await rights.ExecuteScalarAsync());
        string before;
        await using (var snapshot = new NpgsqlCommand(ProjectAclSnapshotSql, connection, transaction))
            before = Assert.IsType<string>(await snapshot.ExecuteScalarAsync());
        Assert.Equal(ownerBefore, before);
        var notices = new ConcurrentQueue<PostgresNotice>();
        NoticeEventHandler observer = (_, args) => notices.Enqueue(args.Notice);
        connection.Notice += observer;
        try
        {
            // A role with SELECT but no grant option receives a real warning,
            // rather than an exception. Permission and ACL checks prove no grant.
            await using (var grant = new NpgsqlCommand("GRANT SELECT ON weft.projects TO PUBLIC", connection, transaction))
                await grant.ExecuteNonQueryAsync();
            Assert.Contains(notices, notice => notice.SqlState == PostgresErrorCodes.PrivilegeNotGrantedWarning &&
                notice.InvariantSeverity == "WARNING");
            await using (var snapshot = new NpgsqlCommand(ProjectAclSnapshotSql, connection, transaction))
                Assert.Equal(before, await snapshot.ExecuteScalarAsync());
            await using (var rights = new NpgsqlCommand(ProjectUnchangedRightsSql, connection, transaction))
                Assert.Equal(true, await rights.ExecuteScalarAsync());
            await transaction.CommitAsync();
        }
        finally { connection.Notice -= observer; }
        Assert.Equal(ownerBefore, await test.ScalarAsync<string>(ProjectAclSnapshotSql));
    }

    private const string ProjectAclSnapshotSql = """
        SELECT jsonb_build_object('tableAcl',relation.relacl::text,'columnAcls',
            (SELECT jsonb_agg(jsonb_build_array(attribute.attname::text,attribute.attacl::text) ORDER BY attribute.attnum)
             FROM pg_attribute attribute WHERE attribute.attrelid=relation.oid AND attribute.attnum>0 AND NOT attribute.attisdropped))::text
        FROM pg_class relation WHERE relation.oid='weft.projects'::regclass
        """;

    private const string ProjectUnchangedRightsSql = """
        SELECT has_table_privilege(current_user,'weft.projects','SELECT')
          AND NOT has_table_privilege(current_user,'weft.projects','INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,SELECT WITH GRANT OPTION')
          AND NOT has_any_column_privilege(current_user,'weft.projects','INSERT,UPDATE,REFERENCES,SELECT WITH GRANT OPTION')
          AND NOT EXISTS (SELECT 1 FROM pg_class relation CROSS JOIN LATERAL aclexplode(coalesce(relation.relacl,acldefault('r',relation.relowner))) privilege
                          WHERE relation.oid='weft.projects'::regclass AND privilege.grantee=0)
          AND NOT EXISTS (SELECT 1 FROM pg_attribute attribute CROSS JOIN LATERAL aclexplode(attribute.attacl) privilege
                          WHERE attribute.attrelid='weft.projects'::regclass AND attribute.attnum>0 AND NOT attribute.attisdropped AND privilege.grantee=0)
        """;
}
