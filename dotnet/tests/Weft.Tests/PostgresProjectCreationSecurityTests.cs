using System.Data;
using System.Text.RegularExpressions;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresProjectCreationSecurityTests(PostgresFixture fixture)
{
    private const string Login = "weft_project_creator";
    private const string AdmissionSignature = "weft.get_project_creation_admission_v1(uuid,uuid)";
    private const string CreationSignature = "weft.create_project_v1(uuid,uuid,uuid,text,text)";
    private static readonly string[] NewTables = ["organization_project_creation_capabilities", "project_creations"];
    private static readonly string[] SourceTables =
        ["organizations", "principals", "organization_memberships", "projects", "project_memberships", .. NewTables];
    private static readonly string[] OldLogins =
        ["weft_runtime", "weft_ingest", "weft_catalog_reader", "weft_catalog_editor"];
    private static ProjectCreationMetadata Metadata() => new("Security test project", "Exact immutable creation description");

    [PostgresFact]
    public async Task CreatorHasExactlyTwoEntryPointsAndNoDirectApplicationDataRights()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var direct = NpgsqlDataSource.Create(test.Case.ProjectCreationConnectionString);
        await using (var identity = direct.CreateCommand("SELECT current_user || ':' || session_user || ':' || current_database()"))
            Assert.Equal($"{Login}:{Login}:{test.Case.Name}", await identity.ExecuteScalarAsync());
        await using (var profile = direct.CreateCommand("""
            SELECT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreaterole
              AND NOT rolcreatedb AND NOT rolinherit AND NOT rolreplication
              AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member=role_row.oid OR roleid=role_row.oid)
              AND NOT has_database_privilege(current_user,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
              AND has_schema_privilege(current_user,'weft','USAGE')
              AND NOT has_schema_privilege(current_user,'weft','CREATE,USAGE WITH GRANT OPTION')
            FROM pg_roles role_row WHERE rolname=current_user
            """))
            Assert.Equal(true, await profile.ExecuteScalarAsync());
        await using (var relations = direct.CreateCommand("""
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_class relation JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
                WHERE namespace.nspname='weft' AND relation.relkind IN ('r','p','v','m','f')
                  AND (has_table_privilege(current_user,relation.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR has_any_column_privilege(current_user,relation.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
              AND NOT EXISTS (
                SELECT 1 FROM pg_class sequence JOIN pg_namespace namespace ON namespace.oid=sequence.relnamespace
                WHERE namespace.nspname='weft' AND sequence.relkind='S'
                  AND CASE WHEN sequence.relkind='S' THEN
                    has_sequence_privilege(current_user,sequence.oid,'USAGE,SELECT,UPDATE')
                  ELSE false END)
            """))
            Assert.Equal(true, await relations.ExecuteScalarAsync());
        await using (var functions = direct.CreateCommand("""
            SELECT array_agg(function.proname::text ORDER BY function.proname)
            FROM pg_proc function JOIN pg_namespace namespace ON namespace.oid=function.pronamespace
            WHERE namespace.nspname='weft' AND has_function_privilege(current_user,function.oid,'EXECUTE')
            """))
            Assert.Equal(new[] { "create_project_v1", "get_project_creation_admission_v1" },
                Assert.IsType<string[]>(await functions.ExecuteScalarAsync()));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("""
            SELECT count(*) FROM pg_trigger trigger_row JOIN pg_class relation ON relation.oid=trigger_row.tgrelid
            JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
            WHERE namespace.nspname='weft' AND relation.relname IN
                ('organizations','principals','organization_memberships','projects','project_memberships')
              AND NOT trigger_row.tgisinternal
            """));
        foreach (var statement in new[]
        {
            "SELECT id FROM weft.projects", "SELECT principal_id FROM weft.project_memberships",
            "SELECT can_create_projects FROM weft.organization_project_creation_capabilities",
            "SELECT fingerprint FROM weft.project_creations", "SELECT evidence FROM weft.revision_ingestion_attempts",
            "SELECT checksum FROM weft.schema_migrations",
            "INSERT INTO weft.projects(organization_id) VALUES ($1)",
            "INSERT INTO weft.project_memberships(organization_id) VALUES ($1)",
            "UPDATE weft.project_memberships SET role='administrator' WHERE organization_id=$1",
            "DELETE FROM weft.project_memberships WHERE organization_id=$1",
            "UPDATE weft.projects SET name='Direct mutation' WHERE organization_id=$1",
            "DELETE FROM weft.projects WHERE organization_id=$1",
            "INSERT INTO weft.organization_project_creation_capabilities(organization_id) VALUES ($1)",
            "UPDATE weft.organization_project_creation_capabilities SET can_create_projects=true WHERE organization_id=$1",
            "DELETE FROM weft.organization_project_creation_capabilities WHERE organization_id=$1",
            "INSERT INTO weft.project_creations(organization_id) VALUES ($1)",
            "UPDATE weft.project_creations SET description='rewritten' WHERE organization_id=$1",
            "DELETE FROM weft.project_creations WHERE organization_id=$1", "TRUNCATE weft.project_creations",
            "SELECT weft.assert_project_creation_contract_v1()", "SELECT weft.guard_project_creations_transition()",
            "SELECT weft.project_creation_fingerprint_v1($1,$2,$3,$4,$5)",
            "SET ROLE weft_migrator", "SET ROLE weft_runtime", "SET ROLE weft_ingest",
            "SET ROLE weft_catalog_reader", "SET ROLE weft_catalog_editor",
            "CREATE SCHEMA project_creation_probe", "CREATE TABLE weft.project_creation_probe(id integer)",
            "CREATE TEMP TABLE project_creation_probe(id integer)", "SET session_replication_role=replica",
            "ALTER FUNCTION weft.create_project_v1(uuid,uuid,uuid,text,text) OWNER TO weft_runtime",
            "GRANT SELECT ON weft.projects TO PUBLIC"
        })
        {
            var values = statement.Contains("$5", StringComparison.Ordinal) ? IntentValues(test, Guid.NewGuid()) :
                statement.Contains("$1", StringComparison.Ordinal) ? new object[] { test.OrganizationId } : [];
            await AssertDirectDeniedAsync(direct, test.OrganizationId, statement, PostgresErrorCodes.InsufficientPrivilege, values);
        }
        Assert.Equal(new[] { 3L, 3L, 0L }, await test.CountsAsync());
        await CreateAnchorAsync(test);
    }

    [PostgresFact]
    public async Task BothRawEntryPointsWorkOnlyAsTheCreatorWithTheBoundSafeTransaction()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var key = Guid.NewGuid();
        await using var direct = NpgsqlDataSource.Create(test.Case.ProjectCreationConnectionString);
        await using (var connection = await direct.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await SetTimeoutsAsync(connection, transaction);
            await BindAsync(connection, transaction, test.OrganizationId);
            await using (var admission = new NpgsqlCommand(
                "SELECT outcome FROM weft.get_project_creation_admission_v1($1,$2)", connection, transaction))
            {
                Add(admission, test.OrganizationId, test.Actor.PrincipalId.Value);
                Assert.Equal("admitted", await admission.ExecuteScalarAsync());
            }
            await using (var create = new NpgsqlCommand(
                "SELECT outcome FROM weft.create_project_v1($1,$2,$3,$4,$5)", connection, transaction))
            {
                Add(create, IntentValues(test, key));
                Assert.Equal("created", await create.ExecuteScalarAsync());
            }
            await transaction.CommitAsync();
        }
        var result = await test.CreateAsync(key, Metadata());
        Assert.Equal(ProjectCreationOutcome.Replayed, result.Outcome);
        var receipt = Assert.IsType<ProjectCreationReceipt>(result.Receipt);
        Assert.NotEqual(test.Seed.ProjectA, receipt.Project.ProjectId);
        Assert.NotEqual(test.Seed.HiddenProjectA, receipt.Project.ProjectId);
        Assert.NotEqual(test.Seed.ProjectB, receipt.Project.ProjectId);
        Assert.Equal(new[] { 4L, 4L, 1L }, await test.CountsAsync());
        var snapshot = await SnapshotAsync(test.Case);
        foreach (var connectionString in new[]
        {
            test.Case.MigrationConnectionString, test.Case.RuntimeConnectionString,
            test.Case.IngestionConnectionString, test.Case.CatalogConnectionString, test.Case.MetadataConnectionString
        })
        {
            await using var caller = NpgsqlDataSource.Create(connectionString);
            await AssertRawEntryPointsDeniedAsync(caller, test, test.OrganizationId, PostgresErrorCodes.InsufficientPrivilege);
        }
        Assert.Equal(snapshot, await SnapshotAsync(test.Case));
        await AssertHealthyAsync(test, receipt);
    }

    [PostgresFact]
    public async Task MissingWrongScopeReadOnlyIsolationAndUnsafeSessionSettingsRemainErrors()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        var snapshot = await SnapshotAsync(test.Case);
        await using var direct = NpgsqlDataSource.Create(test.Case.ProjectCreationConnectionString);
        await AssertRawEntryPointsDeniedAsync(direct, test, null, PostgresErrorCodes.InsufficientPrivilege);
        await AssertRawEntryPointsDeniedAsync(direct, test, test.Seed.OrganizationB, PostgresErrorCodes.InsufficientPrivilege);
        foreach (var setup in new[]
        {
            "SET TRANSACTION READ ONLY", "SET LOCAL row_security=off",
            "SET LOCAL lock_timeout='2s'", "SET LOCAL statement_timeout='6s'"
        })
            await AssertRawEntryPointsDeniedAsync(direct, test, test.OrganizationId,
                PostgresErrorCodes.InsufficientPrivilege, setup: setup);
        foreach (var isolation in new[] { IsolationLevel.RepeatableRead, IsolationLevel.Serializable })
            await AssertRawEntryPointsDeniedAsync(direct, test, test.OrganizationId,
                PostgresErrorCodes.InsufficientPrivilege, isolation: isolation);
        // Change encoding and invoke the guard in one failing statement. Its
        // transaction rolls back before a changed protocol encoding can become
        // the next client-command environment. The lateral argument forces the
        // materialized setting to be evaluated before either entrypoint.
        const string encodingPrefix = "WITH encoding AS MATERIALIZED (SELECT set_config('client_encoding','LATIN1',true) value) SELECT guarded.* FROM encoding CROSS JOIN LATERAL ";
        const string encodedOrganization = "CASE WHEN encoding.value='LATIN1' THEN $1::uuid ELSE NULL END";
        await AssertDirectDeniedAsync(direct, test.OrganizationId,
            encodingPrefix + $"weft.get_project_creation_admission_v1({encodedOrganization},$2) guarded",
            PostgresErrorCodes.InsufficientPrivilege, [test.OrganizationId, test.Actor.PrincipalId.Value]);
        await AssertDirectDeniedAsync(direct, test.OrganizationId,
            encodingPrefix + $"weft.create_project_v1({encodedOrganization},$2,$3,$4,$5) guarded",
            PostgresErrorCodes.InsufficientPrivilege, IntentValues(test, Guid.NewGuid()));
        Assert.Equal(snapshot, await SnapshotAsync(test.Case));
        await AssertHealthyAsync(test, receipt);
    }

    [PostgresFact]
    public async Task OldProfilesRetainNoRightsOnNewTablesOrFunctionsAndCannotBecomeCreatorPools()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        foreach (var role in OldLogins)
        {
            foreach (var table in NewTables)
                Assert.True(await test.Case.ScalarAsync<bool>("""
                    SELECT NOT has_table_privilege($1,$2,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                      AND NOT has_any_column_privilege($1,$2,'SELECT,INSERT,UPDATE,REFERENCES')
                    """, role, $"weft.{table}"));
            foreach (var name in ProjectCreationSchemaContract.Functions.Keys)
                Assert.False(await test.Case.ScalarAsync<bool>("SELECT has_function_privilege($1,$2,'EXECUTE')", role, Signature(name)));
        }
        foreach (var connectionString in new[]
        {
            test.Case.MigrationConnectionString, test.Case.RuntimeConnectionString,
            test.Case.IngestionConnectionString, test.Case.CatalogConnectionString, test.Case.MetadataConnectionString
        })
        {
            await using var wrongPool = new PostgresProjectCreationDatabase(connectionString);
            var creator = new PostgresProjectCreator(wrongPool);
            await Assert.ThrowsAsync<InvalidOperationException>(() => creator.GetAdmissionAsync(test.Actor, test.OrganizationId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => creator.CreateAsync(
                test.Actor, test.OrganizationId, Guid.NewGuid(), Metadata()));
        }
        Assert.Equal(new[] { 3L, 3L, 0L }, await test.CountsAsync());
        await CreateAnchorAsync(test);
    }

    [PostgresFact]
    public async Task UnsafeRoleAttributesMembershipParameterAndCreationRightsRefuseTheNextLease()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        foreach (var (alter, restore) in new[]
        {
            ($"ALTER ROLE {Login} SUPERUSER", $"ALTER ROLE {Login} NOSUPERUSER"),
            ($"ALTER ROLE {Login} BYPASSRLS", $"ALTER ROLE {Login} NOBYPASSRLS"),
            ($"ALTER ROLE {Login} CREATEROLE", $"ALTER ROLE {Login} NOCREATEROLE"),
            ($"ALTER ROLE {Login} CREATEDB", $"ALTER ROLE {Login} NOCREATEDB"),
            ($"ALTER ROLE {Login} REPLICATION", $"ALTER ROLE {Login} NOREPLICATION"),
            ($"ALTER ROLE {Login} INHERIT", $"ALTER ROLE {Login} NOINHERIT"),
            ($"ALTER ROLE {Login} NOLOGIN", $"ALTER ROLE {Login} LOGIN"),
            ($"GRANT weft_migrator TO {Login}", $"REVOKE weft_migrator FROM {Login}"),
            ($"GRANT weft_runtime TO {Login}", $"REVOKE weft_runtime FROM {Login}"),
            ($"GRANT weft_ingest TO {Login}", $"REVOKE weft_ingest FROM {Login}"),
            ($"GRANT pg_read_all_data TO {Login}", $"REVOKE pg_read_all_data FROM {Login}"),
            ($"GRANT pg_write_all_data TO {Login}", $"REVOKE pg_write_all_data FROM {Login}"),
            ($"GRANT pg_read_server_files TO {Login}", $"REVOKE pg_read_server_files FROM {Login}"),
            ($"GRANT pg_write_server_files TO {Login}", $"REVOKE pg_write_server_files FROM {Login}"),
            ($"GRANT pg_execute_server_program TO {Login}", $"REVOKE pg_execute_server_program FROM {Login}"),
            ($"GRANT pg_signal_backend TO {Login}", $"REVOKE pg_signal_backend FROM {Login}"),
            ($"GRANT SET ON PARAMETER session_replication_role TO {Login}", $"REVOKE SET ON PARAMETER session_replication_role FROM {Login}"),
            ($"GRANT CREATE ON DATABASE {test.Case.Name} TO {Login}", $"REVOKE CREATE ON DATABASE {test.Case.Name} FROM {Login}"),
            ($"GRANT TEMPORARY ON DATABASE {test.Case.Name} TO {Login}", $"REVOKE TEMPORARY ON DATABASE {test.Case.Name} FROM {Login}"),
            ($"GRANT CREATE ON SCHEMA weft TO {Login}", $"REVOKE CREATE ON SCHEMA weft FROM {Login}")
        })
            await MutateRefuseAndRestoreAsync(test, receipt, alter, restore, bootstrap: true);
    }

    [PostgresFact]
    public async Task TableColumnSequenceRoutineAndGrantOptionPrivilegesCannotWidenCreator()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        await test.Case.ExecuteAsync("CREATE SEQUENCE weft.project_creation_grant_probe_sequence");
        try
        {
            foreach (var (alter, restore) in new[]
            {
                ($"GRANT SELECT ON weft.projects TO {Login}", $"REVOKE SELECT ON weft.projects FROM {Login}"),
                ($"GRANT SELECT(name) ON weft.projects TO {Login}", $"REVOKE SELECT(name) ON weft.projects FROM {Login}"),
                ($"GRANT INSERT ON weft.project_memberships TO {Login}", $"REVOKE INSERT ON weft.project_memberships FROM {Login}"),
                ($"GRANT UPDATE(role) ON weft.project_memberships TO {Login}", $"REVOKE UPDATE(role) ON weft.project_memberships FROM {Login}"),
                ($"GRANT SELECT ON weft.organization_project_creation_capabilities TO {Login}", $"REVOKE SELECT ON weft.organization_project_creation_capabilities FROM {Login}"),
                ($"GRANT UPDATE(can_create_projects) ON weft.organization_project_creation_capabilities TO {Login}", $"REVOKE UPDATE(can_create_projects) ON weft.organization_project_creation_capabilities FROM {Login}"),
                ($"GRANT SELECT(fingerprint) ON weft.project_creations TO {Login}", $"REVOKE SELECT(fingerprint) ON weft.project_creations FROM {Login}"),
                ($"GRANT INSERT,DELETE,TRUNCATE,REFERENCES,TRIGGER ON weft.project_creations TO {Login}", $"REVOKE INSERT,DELETE,TRUNCATE,REFERENCES,TRIGGER ON weft.project_creations FROM {Login}"),
                ($"GRANT SELECT ON weft.schema_migrations TO {Login}", $"REVOKE SELECT ON weft.schema_migrations FROM {Login}"),
                ($"GRANT EXECUTE ON FUNCTION weft.assert_project_creation_contract_v1() TO {Login}", $"REVOKE EXECUTE ON FUNCTION weft.assert_project_creation_contract_v1() FROM {Login}"),
                ($"GRANT USAGE ON SEQUENCE weft.project_creation_grant_probe_sequence TO {Login}", $"REVOKE USAGE ON SEQUENCE weft.project_creation_grant_probe_sequence FROM {Login}"),
                ($"GRANT EXECUTE ON FUNCTION {CreationSignature} TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR EXECUTE ON FUNCTION {CreationSignature} FROM {Login}"),
                ($"GRANT USAGE ON SCHEMA weft TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR USAGE ON SCHEMA weft FROM {Login}"),
                ($"GRANT CONNECT ON DATABASE {test.Case.Name} TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE {test.Case.Name} FROM {Login}"),
                ($"REVOKE EXECUTE ON FUNCTION {AdmissionSignature} FROM {Login}", $"GRANT EXECUTE ON FUNCTION {AdmissionSignature} TO {Login}"),
                ($"REVOKE EXECUTE ON FUNCTION {CreationSignature} FROM {Login}", $"GRANT EXECUTE ON FUNCTION {CreationSignature} TO {Login}"),
                ($"REVOKE USAGE ON SCHEMA weft FROM {Login}", $"GRANT USAGE ON SCHEMA weft TO {Login}"),
                ($"REVOKE CONNECT ON DATABASE {test.Case.Name} FROM {Login}", $"GRANT CONNECT ON DATABASE {test.Case.Name} TO {Login}")
            })
                await MutateRefuseAndRestoreAsync(test, receipt, alter, restore);
        }
        finally { await test.Case.ExecuteAsync("DROP SEQUENCE weft.project_creation_grant_probe_sequence"); }
    }

    [PostgresFact]
    public async Task PublicDefaultAndInheritedPrivilegesCannotWidenCreator()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        foreach (var (alter, restore) in new[]
        {
            ("GRANT SELECT(name) ON weft.projects TO PUBLIC", "REVOKE SELECT(name) ON weft.projects FROM PUBLIC"),
            ("GRANT UPDATE(can_create_projects) ON weft.organization_project_creation_capabilities TO PUBLIC", "REVOKE UPDATE(can_create_projects) ON weft.organization_project_creation_capabilities FROM PUBLIC"),
            ("GRANT USAGE ON SCHEMA weft TO PUBLIC", "REVOKE USAGE ON SCHEMA weft FROM PUBLIC"),
            ($"GRANT CONNECT ON DATABASE {test.Case.Name} TO PUBLIC", $"REVOKE CONNECT ON DATABASE {test.Case.Name} FROM PUBLIC"),
            ($"GRANT EXECUTE ON FUNCTION {AdmissionSignature} TO PUBLIC", $"REVOKE EXECUTE ON FUNCTION {AdmissionSignature} FROM PUBLIC"),
            ($"GRANT EXECUTE ON FUNCTION {CreationSignature} TO weft_runtime", $"REVOKE EXECUTE ON FUNCTION {CreationSignature} FROM weft_runtime"),
            ($"ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT SELECT ON TABLES TO {Login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE SELECT ON TABLES FROM {Login}"),
            ("ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT INSERT ON TABLES TO PUBLIC", "ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE INSERT ON TABLES FROM PUBLIC"),
            ($"ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT EXECUTE ON FUNCTIONS TO {Login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE EXECUTE ON FUNCTIONS FROM {Login}")
        })
            await MutateRefuseAndRestoreAsync(test, receipt, alter, restore);
        var role = $"creation_attack_{test.Case.Name}";
        await test.Case.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS");
        try
        {
            await test.Case.ExecuteAsync($"GRANT SELECT(fingerprint) ON weft.project_creations TO {role}");
            foreach (var options in new[] { "", " WITH ADMIN OPTION" })
            {
                await test.Case.BootstrapAsync($"GRANT {role} TO {Login}{options}");
                try { await AssertRefusedAsync(test, receipt); }
                finally { await test.Case.BootstrapAsync($"REVOKE {role} FROM {Login}"); }
                await AssertHealthyAsync(test, receipt);
            }
        }
        finally
        {
            await test.Case.ExecuteAsync($"REVOKE SELECT(fingerprint) ON weft.project_creations FROM {role}");
            await test.Case.BootstrapAsync($"DROP ROLE {role}");
        }
    }

    [PostgresFact]
    public async Task AlternateSchemaRelationAndFunctionOwnersAreRefusedAndRestored()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        var role = $"creation_owner_{test.Case.Name}";
        await test.Case.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS");
        try
        {
            Assert.False(await test.Case.ScalarAsync<bool>("SELECT pg_has_role($1,$2,'MEMBER')", Login, role));
            var targets = new[] { "SCHEMA weft" }.Concat(SourceTables.Select(table => $"TABLE weft.{table}"))
                .Concat(ProjectCreationSchemaContract.Functions.Keys.Select(name => $"FUNCTION {Signature(name)}"));
            foreach (var target in targets)
                await MutateRefuseAndRestoreAsync(test, receipt, $"ALTER {target} OWNER TO {role}",
                    $"ALTER {target} OWNER TO weft_migrator", bootstrap: true);
        }
        finally { await test.Case.BootstrapAsync($"DROP ROLE {role}"); }
    }

    [PostgresFact]
    public async Task FunctionSourceSecurityPathPropertiesAndOverloadsMustMatchTrustedSource()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        foreach (var accepted in ProjectCreationSchemaContract.Functions.Values)
        {
            var signature = Signature(accepted.Name);
            var source = SourceFunction(accepted.Name);
            var marker = $"$weft_{accepted.Name}$";
            var close = source.LastIndexOf(marker, StringComparison.Ordinal);
            var changedBody = source.Insert(close, "\n-- Actual admission must reject this changed source body.\n");
            foreach (var alteration in new[]
            {
                changedBody,
                $"ALTER FUNCTION {signature} {(accepted.SecurityDefiner ? "SECURITY INVOKER" : "SECURITY DEFINER")}",
                $"ALTER FUNCTION {signature} SET search_path=public,weft,pg_temp",
                $"ALTER FUNCTION {signature} {(accepted.Volatility == 'i' ? "VOLATILE" : "STABLE")}",
                $"ALTER FUNCTION {signature} PARALLEL {(accepted.Parallel == 'u' ? "SAFE" : "UNSAFE")}",
                $"ALTER FUNCTION {signature} {(accepted.Strict ? "CALLED ON NULL INPUT" : "RETURNS NULL ON NULL INPUT")}",
                $"ALTER FUNCTION {signature} SET statement_timeout='6s'"
            })
                await MutateRefuseAndRestoreAsync(test, receipt, alteration, source);
            await MutateRefuseAndRestoreAsync(test, receipt, $"ALTER FUNCTION {signature} LEAKPROOF", source, bootstrap: true);
            var overload = $"weft.{accepted.Name}(integer)";
            await test.Case.ExecuteAsync($"CREATE FUNCTION {overload} RETURNS integer LANGUAGE sql AS 'SELECT $1'; REVOKE ALL ON FUNCTION {overload} FROM PUBLIC");
            try { await AssertRefusedAsync(test, receipt); }
            finally { await test.Case.ExecuteAsync($"DROP FUNCTION {overload}"); }
            await AssertHealthyAsync(test, receipt);
        }
    }

    [PostgresFact]
    public async Task ScopedPoliciesCannotBeMissingDisabledForcedWeakenedOrExtended()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        foreach (var table in SourceTables.Where(table => table != "principals"))
        {
            var policy = $"{table}_organization_scope";
            var expression = $"{(table == "organizations" ? "id" : "organization_id")} = NULLIF(current_setting('weft.organization_id',true),'')::uuid";
            var recreate = $"CREATE POLICY {policy} ON weft.{table} USING ({expression}) WITH CHECK ({expression})";
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE weft.{table} DISABLE ROW LEVEL SECURITY", $"ALTER TABLE weft.{table} ENABLE ROW LEVEL SECURITY"),
                ($"ALTER TABLE weft.{table} FORCE ROW LEVEL SECURITY", $"ALTER TABLE weft.{table} NO FORCE ROW LEVEL SECURITY"),
                ($"DROP POLICY {policy} ON weft.{table}", recreate),
                ($"ALTER POLICY {policy} ON weft.{table} USING (true)", $"ALTER POLICY {policy} ON weft.{table} USING ({expression})"),
                ($"ALTER POLICY {policy} ON weft.{table} WITH CHECK (true)", $"ALTER POLICY {policy} ON weft.{table} WITH CHECK ({expression})"),
                ($"CREATE POLICY creation_unscoped_probe ON weft.{table} USING (true)", $"DROP POLICY creation_unscoped_probe ON weft.{table}"),
                ($"ALTER POLICY {policy} ON weft.{table} TO {Login}", $"ALTER POLICY {policy} ON weft.{table} TO PUBLIC"),
                ($"DROP POLICY {policy} ON weft.{table}; CREATE POLICY {policy} ON weft.{table} FOR SELECT USING ({expression})", $"DROP POLICY {policy} ON weft.{table}; {recreate}"),
                ($"DROP POLICY {policy} ON weft.{table}; CREATE POLICY {policy} ON weft.{table} AS RESTRICTIVE USING ({expression}) WITH CHECK ({expression})", $"DROP POLICY {policy} ON weft.{table}; {recreate}")
            })
                await MutateRefuseAndRestoreAsync(test, receipt, alter, restore, directGuard: true);
        }
        await MutateRefuseAndRestoreAsync(test, receipt, "ALTER TABLE weft.principals ENABLE ROW LEVEL SECURITY",
            "ALTER TABLE weft.principals DISABLE ROW LEVEL SECURITY", directGuard: true);
    }

    [PostgresFact]
    public async Task RelationShapesDefaultsRulesAndInheritanceMustRemainExact()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        foreach (var table in SourceTables)
        {
            var column = table is "organizations" or "principals" ? "id" : "organization_id";
            // Rename restores the exact attnum/shape. Adding then dropping a
            // column leaves a dropped attribute, which this contract rejects.
            await MutateRefuseAndRestoreAsync(test, receipt,
                $"ALTER TABLE weft.{table} RENAME COLUMN {column} TO creation_unknown_column",
                $"ALTER TABLE weft.{table} RENAME COLUMN creation_unknown_column TO {column}", directGuard: true);
        }
        foreach (var (alter, restore) in new[]
        {
            ("ALTER TABLE weft.project_creations ALTER COLUMN description DROP NOT NULL", "ALTER TABLE weft.project_creations ALTER COLUMN description SET NOT NULL"),
            ("ALTER TABLE weft.project_creations ALTER COLUMN fingerprint_version SET DEFAULT 1", "ALTER TABLE weft.project_creations ALTER COLUMN fingerprint_version DROP DEFAULT"),
            ("ALTER TABLE weft.organization_project_creation_capabilities ALTER COLUMN can_create_projects SET DEFAULT true", "ALTER TABLE weft.organization_project_creation_capabilities ALTER COLUMN can_create_projects SET DEFAULT false"),
            ("ALTER TABLE weft.projects ALTER COLUMN version SET DEFAULT 2", "ALTER TABLE weft.projects ALTER COLUMN version SET DEFAULT 1"),
            ("CREATE TABLE weft.creation_inherited_probe () INHERITS (weft.project_creations)", "DROP TABLE weft.creation_inherited_probe"),
            ("CREATE RULE creation_rewrite_probe AS ON UPDATE TO weft.projects DO ALSO SELECT 1", "DROP RULE creation_rewrite_probe ON weft.projects"),
            ("CREATE RULE creation_facts_rewrite_probe AS ON DELETE TO weft.project_creations DO ALSO SELECT 1", "DROP RULE creation_facts_rewrite_probe ON weft.project_creations")
        })
            await MutateRefuseAndRestoreAsync(test, receipt, alter, restore, directGuard: true);
    }

    [PostgresFact]
    public async Task FactsTriggerAndForeignKeyEnforcementCannotBeDisabledOrChanged()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        const string trigger = "project_creations_transition_guard";
        const string function = "weft.guard_project_creations_transition()";
        const string recreate = "CREATE TRIGGER project_creations_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.project_creations FOR EACH ROW EXECUTE FUNCTION weft.guard_project_creations_transition()";
        foreach (var (alter, restore) in new[]
        {
            ($"ALTER TABLE weft.project_creations DISABLE TRIGGER {trigger}", $"ALTER TABLE weft.project_creations ENABLE TRIGGER {trigger}"),
            ($"ALTER TABLE weft.project_creations ENABLE ALWAYS TRIGGER {trigger}", $"ALTER TABLE weft.project_creations ENABLE TRIGGER {trigger}"),
            ($"ALTER TABLE weft.project_creations ENABLE REPLICA TRIGGER {trigger}", $"ALTER TABLE weft.project_creations ENABLE TRIGGER {trigger}"),
            ($"DROP TRIGGER {trigger} ON weft.project_creations", recreate),
            ($"DROP TRIGGER {trigger} ON weft.project_creations; CREATE TRIGGER {trigger} BEFORE INSERT OR UPDATE ON weft.project_creations FOR EACH ROW EXECUTE FUNCTION {function}", $"DROP TRIGGER {trigger} ON weft.project_creations; {recreate}"),
            ($"DROP TRIGGER {trigger} ON weft.project_creations; CREATE TRIGGER {trigger} BEFORE INSERT OR UPDATE OR DELETE ON weft.project_creations FOR EACH ROW EXECUTE FUNCTION weft.guard_project_creations_transition('extra')", $"DROP TRIGGER {trigger} ON weft.project_creations; {recreate}"),
            ($"DROP TRIGGER {trigger} ON weft.project_creations; CREATE TRIGGER {trigger} BEFORE INSERT OR UPDATE OR DELETE ON weft.project_creations FOR EACH STATEMENT EXECUTE FUNCTION {function}", $"DROP TRIGGER {trigger} ON weft.project_creations; {recreate}"),
            ($"DROP TRIGGER {trigger} ON weft.project_creations; CREATE TRIGGER {trigger} BEFORE INSERT OR UPDATE OR DELETE ON weft.project_creations FOR EACH ROW WHEN (true) EXECUTE FUNCTION {function}", $"DROP TRIGGER {trigger} ON weft.project_creations; {recreate}"),
            ($"CREATE TRIGGER creation_facts_extra_probe BEFORE UPDATE ON weft.project_creations FOR EACH ROW EXECUTE FUNCTION {function}", "DROP TRIGGER creation_facts_extra_probe ON weft.project_creations"),
            ($"CREATE TRIGGER creation_old_ownership_probe BEFORE UPDATE ON weft.projects FOR EACH ROW EXECUTE FUNCTION {function}", "DROP TRIGGER creation_old_ownership_probe ON weft.projects"),
            ($"CREATE TRIGGER creation_old_membership_probe BEFORE UPDATE ON weft.project_memberships FOR EACH ROW EXECUTE FUNCTION {function}", "DROP TRIGGER creation_old_membership_probe ON weft.project_memberships")
        })
            await MutateRefuseAndRestoreAsync(test, receipt, alter, restore, directGuard: true);
        // Internal trigger names identify owned objects to mutate, not accepted
        // definitions: only the fixed origin restoration is trusted here.
        foreach (var table in NewTables.Concat(new[] { "organizations", "organization_memberships", "projects" }))
        {
            var internalTrigger = await test.Case.ScalarAsync<string>("""
                SELECT tgname FROM pg_trigger WHERE tgrelid=$1::regclass AND tgisinternal AND tgconstraint<>0
                ORDER BY tgname LIMIT 1
                """, $"weft.{table}");
            foreach (var mode in new[] { "DISABLE", "ENABLE ALWAYS", "ENABLE REPLICA" })
                await MutateRefuseAndRestoreAsync(test, receipt,
                    $"ALTER TABLE weft.{table} {mode} TRIGGER \"{internalTrigger}\"",
                    $"ALTER TABLE weft.{table} ENABLE TRIGGER \"{internalTrigger}\"", bootstrap: true, directGuard: true);
        }
    }

    [PostgresFact]
    public async Task NamedChecksKeysAndScopedForeignKeysMustKeepTheirTrustedDefinitions()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        foreach (var constraint in new[]
        {
            "project_creations_key_check", "project_creations_project_check",
            "project_creations_fingerprint_version_check", "project_creations_fingerprint_check",
            "project_creations_name_check", "project_creations_description_check",
            "project_creations_initial_project_version_check", "project_creations_initial_membership_role_check",
            "project_creations_initial_membership_active_check", "project_creations_initial_membership_version_check"
        })
        {
            var prefix = $"ALTER TABLE weft.project_creations DROP CONSTRAINT {constraint}; ALTER TABLE weft.project_creations ADD CONSTRAINT {constraint} ";
            await MutateRefuseAndRestoreAsync(test, receipt, prefix + "CHECK (true)",
                prefix + SourceConstraint("project_creations", constraint), directGuard: true);
            await MutateRefuseAndRestoreAsync(test, receipt,
                prefix + SourceConstraint("project_creations", constraint) + " NO INHERIT",
                prefix + SourceConstraint("project_creations", constraint), directGuard: true);
        }
        foreach (var (table, constraint) in new[]
        {
            ("organization_project_creation_capabilities", "organization_project_creation_capabilities_membership_fkey"),
            ("project_creations", "project_creations_project_fkey"),
            ("project_creations", "project_creations_actor_fkey")
        })
        {
            var definition = SourceConstraint(table, constraint);
            var prefix = $"ALTER TABLE weft.{table} DROP CONSTRAINT {constraint}; ALTER TABLE weft.{table} ADD CONSTRAINT {constraint} ";
            foreach (var altered in new[]
            {
                definition + " NOT VALID", definition + " DEFERRABLE INITIALLY IMMEDIATE",
                definition.Replace("ON DELETE RESTRICT", "ON DELETE CASCADE", StringComparison.Ordinal),
                definition + " ON UPDATE CASCADE"
            })
                await MutateRefuseAndRestoreAsync(test, receipt, prefix + altered, prefix + definition, directGuard: true);
        }
        foreach (var (table, constraint, changed) in new[]
        {
            ("organization_project_creation_capabilities", "organization_project_creation_capabilities_pkey", "PRIMARY KEY (principal_id,organization_id)"),
            ("project_creations", "project_creations_pkey", "PRIMARY KEY (idempotency_key,organization_id)"),
            ("project_creations", "project_creations_project_key", "UNIQUE (project_id)")
        })
        {
            var prefix = $"ALTER TABLE weft.{table} DROP CONSTRAINT {constraint}; ALTER TABLE weft.{table} ADD CONSTRAINT {constraint} ";
            await MutateRefuseAndRestoreAsync(test, receipt, prefix + changed,
                prefix + SourceConstraint(table, constraint), directGuard: true);
        }
        await MutateRefuseAndRestoreAsync(test, receipt,
            "ALTER TABLE weft.project_creations ADD CONSTRAINT creation_extra_check CHECK (true)",
            "ALTER TABLE weft.project_creations DROP CONSTRAINT creation_extra_check", directGuard: true);
    }

    [PostgresFact]
    public async Task FactInsertRequiresExactFingerprintAncestryAndAllFixedInitialFacts()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        var snapshot = await SnapshotAsync(test.Case);
        await using (var connection = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case))
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var values = await OwnerInitialRowsAsync(connection, transaction, test);
            await ExecuteAsync(connection, transaction, "SAVEPOINT creation_fact_probe");
            await using (var valid = new NpgsqlCommand(FactInsertSql, connection, transaction))
            {
                Add(valid, values);
                Assert.Equal(1, await valid.ExecuteNonQueryAsync());
            }
            await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT creation_fact_probe");
            await ExecuteAsync(connection, transaction, "RELEASE SAVEPOINT creation_fact_probe");

            foreach (var mutate in new Action<object[]>[]
            {
                row => row[1] = Guid.Empty,
                row => row[2] = test.Seed.Admitted,
                row => row[3] = test.Seed.ProjectA,
                row => row[4] = 2,
                row => row[6] = "Different accepted metadata",
                row => row[7] = "Different exact description",
                row => row[8] = ((DateTimeOffset)row[8]).AddSeconds(1),
                row => row[9] = 2L,
                row => row[10] = "publisher",
                row => row[11] = false,
                row => row[12] = 2L
            })
            {
                var altered = values.ToArray();
                mutate(altered);
                altered[5] = ProjectCreationTestSupport.IndependentFingerprint(
                    (Guid)altered[0], (Guid)altered[2], (Guid)altered[1], (string)altered[6], (string)altered[7]);
                await AssertFactInsertDeniedAsync(connection, transaction, altered);
            }
            var badFingerprint = values.ToArray();
            badFingerprint[5] = new string('0', 64);
            await AssertFactInsertDeniedAsync(connection, transaction, badFingerprint);

            foreach (var alteration in new[]
            {
                "UPDATE weft.projects SET creator_principal_id=$3 WHERE organization_id=$1 AND id=$2",
                "UPDATE weft.projects SET name='Changed project metadata' WHERE organization_id=$1 AND id=$2",
                "UPDATE weft.projects SET description='Changed project description' WHERE organization_id=$1 AND id=$2",
                "UPDATE weft.projects SET version=2 WHERE organization_id=$1 AND id=$2",
                "UPDATE weft.projects SET updated_at=updated_at+interval '1 second' WHERE organization_id=$1 AND id=$2",
                "UPDATE weft.project_memberships SET role='publisher' WHERE organization_id=$1 AND project_id=$2",
                "UPDATE weft.project_memberships SET active=false WHERE organization_id=$1 AND project_id=$2",
                "UPDATE weft.project_memberships SET version=2 WHERE organization_id=$1 AND project_id=$2",
                "DELETE FROM weft.project_memberships WHERE organization_id=$1 AND project_id=$2"
            })
            {
                await ExecuteAsync(connection, transaction, "SAVEPOINT creation_ancestry_probe");
                await using (var mutate = new NpgsqlCommand(alteration, connection, transaction))
                {
                    Add(mutate, alteration.Contains("$3", StringComparison.Ordinal)
                        ? new object[] { values[0], values[3], test.Seed.Admitted }
                        : [values[0], values[3]]);
                    Assert.Equal(1, await mutate.ExecuteNonQueryAsync());
                }
                await using (var insert = new NpgsqlCommand(FactInsertSql, connection, transaction))
                {
                    Add(insert, values);
                    var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
                    Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
                }
                await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT creation_ancestry_probe");
                await ExecuteAsync(connection, transaction, "RELEASE SAVEPOINT creation_ancestry_probe");
            }
            await transaction.RollbackAsync();
        }
        Assert.Equal(snapshot, await SnapshotAsync(test.Case));
        await AssertHealthyAsync(test, receipt);
    }

    [PostgresFact]
    public async Task CreationFactsRejectEveryOwnerUpdateAndDeleteWithoutChangingTheOriginalReceipt()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var receipt = await CreateAnchorAsync(test);
        var snapshot = await SnapshotAsync(test.Case);
        foreach (var assignment in new[]
        {
            "description=description", "name='Forged name'", "description='Forged description'",
            "fingerprint=repeat('0',64)", "fingerprint_version=2", "created_at=created_at+interval '1 second'",
            "initial_project_version=2", "initial_membership_role='publisher'",
            "initial_membership_active=false", "initial_membership_version=2", "idempotency_key=gen_random_uuid()",
            "original_actor_id=gen_random_uuid()", "project_id=gen_random_uuid()", "organization_id=gen_random_uuid()"
        })
            await AssertOwnerStatementDeniedAsync(test.Case,
                $"UPDATE weft.project_creations SET {assignment} WHERE organization_id=$1 AND idempotency_key=$2",
                PostgresErrorCodes.CheckViolation, receipt.Project.OrganizationId, receipt.IdempotencyKey);
        await AssertOwnerStatementDeniedAsync(test.Case,
            "DELETE FROM weft.project_creations WHERE organization_id=$1 AND idempotency_key=$2",
            PostgresErrorCodes.CheckViolation, receipt.Project.OrganizationId, receipt.IdempotencyKey);
        Assert.Equal(snapshot, await SnapshotAsync(test.Case));
        await AssertHealthyAsync(test, receipt);
    }

    private static async Task<ProjectCreationReceipt> CreateAnchorAsync(ProjectCreationTestSupport test)
    {
        var result = await test.CreateAsync(metadata: Metadata());
        Assert.Equal(ProjectCreationOutcome.Created, result.Outcome);
        var receipt = Assert.IsType<ProjectCreationReceipt>(result.Receipt);
        await AssertHealthyAsync(test, receipt);
        return receipt;
    }

    private static async Task AssertHealthyAsync(ProjectCreationTestSupport test, ProjectCreationReceipt receipt)
    {
        var admission = Assert.IsType<OrganizationProjectCreationAdmission>(
            await test.Creator.GetAdmissionAsync(test.Actor, test.OrganizationId));
        Assert.True(admission.CanCreateProjects);
        var replay = await test.CreateAsync(receipt.IdempotencyKey, receipt.Metadata);
        Assert.Equal(ProjectCreationOutcome.Replayed, replay.Outcome);
        Assert.Equal(receipt, replay.Receipt);
    }

    private static async Task AssertRefusedAsync(ProjectCreationTestSupport test, ProjectCreationReceipt receipt)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Creator.GetAdmissionAsync(test.Actor, test.OrganizationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.CreateAsync(receipt.IdempotencyKey, receipt.Metadata));
    }

    private static async Task MutateRefuseAndRestoreAsync(ProjectCreationTestSupport test, ProjectCreationReceipt receipt,
        string alter, string restore, bool bootstrap = false, bool directGuard = false)
    {
        var snapshot = await SnapshotAsync(test.Case);
        var backend = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        if (bootstrap) await test.Case.BootstrapAsync(alter);
        else await test.Case.ExecuteAsync(alter);
        try
        {
            await AssertRefusedAsync(test, receipt);
            if (directGuard)
            {
                await using var direct = NpgsqlDataSource.Create(test.Case.ProjectCreationConnectionString);
                await AssertRawEntryPointsDeniedAsync(direct, test, test.OrganizationId, "P0001");
            }
        }
        catch (Exception failure) { throw new InvalidOperationException($"Project creation security probe failed after: {alter}", failure); }
        finally
        {
            if (bootstrap) await test.Case.BootstrapAsync(restore);
            else await test.Case.ExecuteAsync(restore);
        }
        await AssertHealthyAsync(test, receipt);
        Assert.Equal(backend, await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource));
        Assert.Equal(snapshot, await SnapshotAsync(test.Case));
    }

    private static async Task AssertRawEntryPointsDeniedAsync(NpgsqlDataSource source, ProjectCreationTestSupport test,
        Guid? boundOrganization, string expectedState, string? setup = null, IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        foreach (var (sql, values) in new[]
        {
            ("SELECT * FROM weft.get_project_creation_admission_v1($1,$2)", new object[] { test.OrganizationId, test.Actor.PrincipalId.Value }),
            ("SELECT * FROM weft.create_project_v1($1,$2,$3,$4,$5)", IntentValues(test, Guid.NewGuid()))
        })
            await AssertDirectDeniedAsync(source, boundOrganization, sql, expectedState, values, setup, isolation);
    }

    private static async Task AssertDirectDeniedAsync(NpgsqlDataSource source, Guid? organization, string sql,
        string expectedState, object[]? values = null, string? setup = null, IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(isolation);
        await SetTimeoutsAsync(connection, transaction);
        if (setup is not null)
        {
            await using var prepare = new NpgsqlCommand(setup, connection, transaction);
            await prepare.ExecuteNonQueryAsync();
        }
        if (organization is { } bound)
            await BindAsync(connection, transaction, bound);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        Add(command, values ?? []);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.True(failure.SqlState == expectedState,
            $"Direct project creation statement had SQLSTATE {failure.SqlState}: {sql}");
    }

    private static async Task AssertOwnerStatementDeniedAsync(PostgresCase test, string sql, string expectedState,
        params object[] values)
    {
        await using var command = test.Owner.CreateCommand(sql);
        Add(command, values);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(expectedState, failure.SqlState);
    }

    private static async Task BindAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true)", connection, transaction);
        Add(command, organization.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetTimeoutsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SET LOCAL lock_timeout='1s'; SET LOCAL statement_timeout='5s'", connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static object[] IntentValues(ProjectCreationTestSupport test, Guid key) =>
        [test.OrganizationId, test.Actor.PrincipalId.Value, key, Metadata().Name, Metadata().Description];

    private static string Signature(string name) =>
        $"weft.{name}({string.Join(',', ProjectCreationSchemaContract.Functions[name].InputTypes)})";

    private static string SourceFunction(string name)
    {
        var sql = MigrationRunner.EmbeddedMigrations.Single(migration => migration.Version == 5).Sql;
        var declarations = Regex.Matches(sql,
            @"^CREATE(?: OR REPLACE)? FUNCTION weft\." + Regex.Escape(name) +
            @"\(.*?\bAS \$weft_" + Regex.Escape(name) + @"\$.*?\$weft_" + Regex.Escape(name) + @"\$;",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var declaration = Assert.Single(declarations.Cast<Match>()).Value;
        // Restoring from the build's intended migration leaves no live database
        // routine body in the trusted expectation path.
        return declaration.StartsWith("CREATE OR REPLACE", StringComparison.Ordinal) ? declaration :
            "CREATE OR REPLACE" + declaration["CREATE".Length..];
    }

    private static string SourceConstraint(string table, string constraint)
    {
        var sql = MigrationRunner.EmbeddedMigrations.Single(migration => migration.Version == 5).Sql;
        var start = sql.IndexOf($"CREATE TABLE weft.{table} (", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The intended source table {table} is missing.");
        var end = sql.IndexOf("\n);", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The intended source table {table} is incomplete.");
        var declaration = Regex.Match(sql[start..end], @"\bCONSTRAINT " + Regex.Escape(constraint) +
            @"\s+(?<definition>.*?)(?=,\s*\n\s*CONSTRAINT |\z)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(declaration.Success, $"The intended source constraint {constraint} is missing.");
        return declaration.Groups["definition"].Value.Trim();
    }

    private const string FactInsertSql = """
        INSERT INTO weft.project_creations(organization_id,idempotency_key,original_actor_id,project_id,
            fingerprint_version,fingerprint,name,description,created_at,initial_project_version,
            initial_membership_role,initial_membership_active,initial_membership_version)
        VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
        """;

    private static async Task<object[]> OwnerInitialRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ProjectCreationTestSupport test)
    {
        var projectId = Guid.NewGuid();
        var key = Guid.NewGuid();
        DateTimeOffset createdAt;
        await using (var project = new NpgsqlCommand("""
            INSERT INTO weft.projects(organization_id,id,name,description,creator_principal_id,created_at,updated_at,version)
            SELECT $1,$2,$3,$4,$5,stamp,stamp,1 FROM (SELECT clock_timestamp() stamp) clock
            RETURNING created_at
            """, connection, transaction))
        {
            Add(project, test.OrganizationId, projectId, Metadata().Name, Metadata().Description, test.Actor.PrincipalId.Value);
            // Npgsql's timestamp-with-time-zone scalar is DateTime UTC. Read the
            // exact stored value rather than assuming local clock precision.
            createdAt = new DateTimeOffset(Assert.IsType<DateTime>(await project.ExecuteScalarAsync()));
        }
        await using (var membership = new NpgsqlCommand("""
            INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role,active,version)
            VALUES ($1,$2,$3,'administrator',true,1)
            """, connection, transaction))
        {
            Add(membership, test.OrganizationId, projectId, test.Actor.PrincipalId.Value);
            Assert.Equal(1, await membership.ExecuteNonQueryAsync());
        }
        return [test.OrganizationId, key, test.Actor.PrincipalId.Value, projectId, 1,
            ProjectCreationTestSupport.IndependentFingerprint(test.OrganizationId, test.Actor.PrincipalId.Value,
                key, Metadata().Name, Metadata().Description), Metadata().Name, Metadata().Description,
            createdAt, 1L, "administrator", true, 1L];
    }

    private static async Task AssertFactInsertDeniedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        object[] values)
    {
        await ExecuteAsync(connection, transaction, "SAVEPOINT creation_fact_probe");
        await using (var insert = new NpgsqlCommand(FactInsertSql, connection, transaction))
        {
            Add(insert, values);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        }
        await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT creation_fact_probe");
        await ExecuteAsync(connection, transaction, "RELEASE SAVEPOINT creation_fact_probe");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<string> SnapshotAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
          'organizations',(SELECT jsonb_agg(to_jsonb(row) ORDER BY id) FROM weft.organizations row),
          'principals',(SELECT jsonb_agg(to_jsonb(row) ORDER BY id) FROM weft.principals row),
          'admissions',(SELECT jsonb_agg(to_jsonb(row) ORDER BY organization_id,principal_id) FROM weft.organization_memberships row),
          'projects',(SELECT jsonb_agg(to_jsonb(row) ORDER BY organization_id,id) FROM weft.projects row),
          'memberships',(SELECT jsonb_agg(to_jsonb(row) ORDER BY organization_id,project_id,principal_id) FROM weft.project_memberships row),
          'capabilities',(SELECT jsonb_agg(to_jsonb(row) ORDER BY organization_id,principal_id) FROM weft.organization_project_creation_capabilities row),
          'creations',(SELECT jsonb_agg(to_jsonb(row) ORDER BY organization_id,idempotency_key) FROM weft.project_creations row)
        )::text
        """);

    private static void Add(NpgsqlCommand command, params object[] values)
    {
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
    }
}
