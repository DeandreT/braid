using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresCatalogSecurityTests(PostgresFixture fixture)
{
    private const string Login = "weft_catalog_reader";
    private static readonly string[] FullReadTables =
        ["organizations", "principals", "organization_memberships", "projects", "project_memberships", "mappings", "mapping_revisions"];
    private static readonly string[] ScopedTables =
        ["organizations", "organization_memberships", "projects", "project_memberships", "mappings", "mapping_revisions", "revision_ingestions"];
    private static readonly string[] TransitionTables = ["mappings", "mapping_revisions", "revision_ingestions"];

    [PostgresFact]
    public async Task CatalogLoginHasOnlyNarrowReadRightsAndDirectPrivateReadsOrWritesAreDenied()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        await AssertAdmittedAsync(database, seed);
        await using var direct = NpgsqlDataSource.Create(test.CatalogConnectionString);
        await using (var identity = direct.CreateCommand("SELECT current_user || ':' || current_database()"))
            Assert.Equal($"{Login}:{test.Name}", await identity.ExecuteScalarAsync());
        await using (var safety = direct.CreateCommand("""
            SELECT NOT rolsuper AND NOT rolbypassrls AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolreplication
              AND NOT pg_has_role(current_user,'weft_migrator','MEMBER')
              AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                              WHERE n.nspname='weft' AND c.relowner=pg_roles.oid)
            FROM pg_roles WHERE rolname=current_user
            """))
            Assert.Equal(true, await safety.ExecuteScalarAsync());
        foreach (var table in FullReadTables)
        {
            await using var rights = direct.CreateCommand("""
                SELECT has_table_privilege($1,'SELECT')
                  AND NOT has_table_privilege($1,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                """);
            rights.Parameters.AddWithValue($"weft.{table}");
            Assert.Equal(true, await rights.ExecuteScalarAsync());
        }
        await using (var reservationRights = direct.CreateCommand("SELECT has_table_privilege('weft.revision_ingestions','SELECT')"))
            Assert.Equal(false, await reservationRights.ExecuteScalarAsync());
        foreach (var column in new[] { "organization_id", "project_id", "operation_id", "display_filename" })
        {
            await using var rights = direct.CreateCommand("SELECT has_column_privilege('weft.revision_ingestions',$1,'SELECT')");
            rights.Parameters.AddWithValue(column);
            Assert.Equal(true, await rights.ExecuteScalarAsync());
        }
        foreach (var column in new[] { "phase", "actor_principal_id", "lease_token", "intent_fingerprint", "engine_revision", "backend_build", "native_object_id", "native_digest", "last_error" })
        {
            await using var rights = direct.CreateCommand("SELECT has_column_privilege('weft.revision_ingestions',$1,'SELECT')");
            rights.Parameters.AddWithValue(column);
            Assert.Equal(false, await rights.ExecuteScalarAsync());
            await AssertDirectPrivilegeDeniedAsync(direct, seed.OrganizationA,
                $"SELECT {column} FROM weft.revision_ingestions WHERE false");
        }
        foreach (var sql in new[]
        {
            "SELECT * FROM weft.revision_ingestions WHERE false",
            "SELECT evidence FROM weft.revision_ingestion_attempts WHERE false",
            "SELECT generation FROM weft.revision_ingestion_attempts WHERE false",
            "SELECT checksum FROM weft.schema_migrations",
            "SELECT weft.guard_mappings_transition()",
            "SET ROLE weft_migrator",
            "SET ROLE weft_ingest",
            "CREATE SCHEMA catalog_security_probe",
            "CREATE TABLE weft.catalog_security_probe(id integer)",
            "CREATE TEMP TABLE catalog_security_probe(id integer)",
            "INSERT INTO weft.principals(id) VALUES (gen_random_uuid())",
            "INSERT INTO weft.mappings(organization_id) SELECT organization_id FROM weft.projects WHERE false",
            "INSERT INTO weft.revision_ingestions(organization_id) SELECT organization_id FROM weft.projects WHERE false",
            "UPDATE weft.projects SET name='changed' WHERE false",
            "UPDATE weft.mappings SET name='changed' WHERE false",
            "UPDATE weft.revision_ingestions SET display_filename='changed.json' WHERE false",
            "UPDATE weft.mapping_revisions SET native_digest=repeat('0',64) WHERE false",
            "DELETE FROM weft.projects WHERE false",
            "DELETE FROM weft.mappings WHERE false",
            "DELETE FROM weft.revision_ingestions WHERE false",
            "DELETE FROM weft.mapping_revisions WHERE false",
            "TRUNCATE weft.mapping_revisions",
            "UPDATE weft.schema_migrations SET checksum=repeat('0',64)"
        })
            await AssertDirectPrivilegeDeniedAsync(direct, seed.OrganizationA, sql);
        await using (var scope = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            await using var allowed = scope.CreateCommand("SELECT organization_id,project_id,operation_id,display_filename FROM weft.revision_ingestions");
            await using var rows = await allowed.ExecuteReaderAsync();
            Assert.False(await rows.ReadAsync());
        }
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.projects"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task NextLeaseRejectsExtraPrivateColumnsWritesFunctionsUnknownRelationsAndGrantOptions()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        await AssertAdmittedAsync(database, seed);
        await test.ExecuteAsync("CREATE TABLE weft.catalog_grant_probe(id integer)");
        await test.ExecuteAsync("CREATE SEQUENCE weft.catalog_grant_probe_sequence");
        await test.ExecuteAsync("CREATE FUNCTION weft.catalog_grant_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
        await test.ExecuteAsync("REVOKE ALL ON FUNCTION weft.catalog_grant_probe() FROM PUBLIC");
        try
        {
            foreach (var (alter, restore) in new[]
            {
                ($"GRANT SELECT(phase) ON weft.revision_ingestions TO {Login}", $"REVOKE SELECT(phase) ON weft.revision_ingestions FROM {Login}"),
                ($"GRANT SELECT(native_digest) ON weft.revision_ingestions TO {Login}", $"REVOKE SELECT(native_digest) ON weft.revision_ingestions FROM {Login}"),
                ($"GRANT SELECT ON weft.revision_ingestions TO {Login}", $"""
                    REVOKE SELECT ON weft.revision_ingestions FROM {Login};
                    GRANT SELECT(organization_id,project_id,operation_id,display_filename) ON weft.revision_ingestions TO {Login}
                    """),
                ($"GRANT SELECT(evidence) ON weft.revision_ingestion_attempts TO {Login}", $"REVOKE SELECT(evidence) ON weft.revision_ingestion_attempts FROM {Login}"),
                ($"GRANT SELECT ON weft.revision_ingestion_attempts TO {Login}", $"REVOKE SELECT ON weft.revision_ingestion_attempts FROM {Login}"),
                ($"GRANT INSERT(name) ON weft.projects TO {Login}", $"REVOKE INSERT(name) ON weft.projects FROM {Login}"),
                ($"GRANT UPDATE(name) ON weft.mappings TO {Login}", $"REVOKE UPDATE(name) ON weft.mappings FROM {Login}"),
                ($"GRANT UPDATE(display_filename) ON weft.revision_ingestions TO {Login}", $"REVOKE UPDATE(display_filename) ON weft.revision_ingestions FROM {Login}"),
                ($"GRANT DELETE ON weft.mapping_revisions TO {Login}", $"REVOKE DELETE ON weft.mapping_revisions FROM {Login}"),
                ($"GRANT REFERENCES(id) ON weft.projects TO {Login}", $"REVOKE REFERENCES(id) ON weft.projects FROM {Login}"),
                ($"GRANT TRUNCATE,TRIGGER ON weft.mapping_revisions TO {Login}", $"REVOKE TRUNCATE,TRIGGER ON weft.mapping_revisions FROM {Login}"),
                ($"GRANT SELECT ON weft.schema_migrations TO {Login}", $"REVOKE SELECT ON weft.schema_migrations FROM {Login}"),
                ($"GRANT SELECT(id) ON weft.catalog_grant_probe TO {Login}", $"REVOKE SELECT(id) ON weft.catalog_grant_probe FROM {Login}"),
                ($"GRANT EXECUTE ON FUNCTION weft.catalog_grant_probe() TO {Login}", $"REVOKE EXECUTE ON FUNCTION weft.catalog_grant_probe() FROM {Login}"),
                ($"GRANT USAGE ON SEQUENCE weft.catalog_grant_probe_sequence TO {Login}", $"REVOKE USAGE ON SEQUENCE weft.catalog_grant_probe_sequence FROM {Login}"),
                ("GRANT SELECT ON SEQUENCE weft.catalog_grant_probe_sequence TO PUBLIC", "REVOKE SELECT ON SEQUENCE weft.catalog_grant_probe_sequence FROM PUBLIC"),
                ($"GRANT SELECT ON weft.projects TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR SELECT ON weft.projects FROM {Login}"),
                ($"GRANT SELECT(name) ON weft.projects TO {Login} WITH GRANT OPTION", $"REVOKE SELECT(name) ON weft.projects FROM {Login}"),
                ($"GRANT SELECT(display_filename) ON weft.revision_ingestions TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR SELECT(display_filename) ON weft.revision_ingestions FROM {Login}"),
                ($"GRANT USAGE ON SCHEMA weft TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR USAGE ON SCHEMA weft FROM {Login}"),
                ($"GRANT CONNECT ON DATABASE {test.Name} TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE {test.Name} FROM {Login}"),
                ($"REVOKE SELECT ON weft.project_memberships FROM {Login}", $"GRANT SELECT ON weft.project_memberships TO {Login}"),
                ($"REVOKE SELECT(display_filename) ON weft.revision_ingestions FROM {Login}", $"GRANT SELECT(display_filename) ON weft.revision_ingestions TO {Login}")
            })
                await MutateRefuseAndRestoreAsync(test, database, seed, alter, restore);
        }
        finally
        {
            await test.ExecuteAsync("DROP FUNCTION weft.catalog_grant_probe()");
            await test.ExecuteAsync("DROP SEQUENCE weft.catalog_grant_probe_sequence");
            await test.ExecuteAsync("DROP TABLE weft.catalog_grant_probe");
        }
    }

    [PostgresFact]
    public async Task PublicDefaultAndInheritedGrantsCannotWidenTheCatalogRole()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        await AssertAdmittedAsync(database, seed);
        foreach (var (alter, restore) in new[]
        {
            ("GRANT SELECT(phase) ON weft.revision_ingestions TO PUBLIC", "REVOKE SELECT(phase) ON weft.revision_ingestions FROM PUBLIC"),
            ("GRANT SELECT(evidence) ON weft.revision_ingestion_attempts TO PUBLIC", "REVOKE SELECT(evidence) ON weft.revision_ingestion_attempts FROM PUBLIC"),
            ("GRANT UPDATE(name) ON weft.mappings TO PUBLIC", "REVOKE UPDATE(name) ON weft.mappings FROM PUBLIC"),
            ($"ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT SELECT ON TABLES TO {Login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE SELECT ON TABLES FROM {Login}"),
            ("ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT UPDATE ON TABLES TO PUBLIC", "ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE UPDATE ON TABLES FROM PUBLIC"),
            ($"ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT EXECUTE ON FUNCTIONS TO {Login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE EXECUTE ON FUNCTIONS FROM {Login}")
        })
            await MutateRefuseAndRestoreAsync(test, database, seed, alter, restore);
        var role = $"catalog_attack_{test.Name}";
        await test.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS");
        try
        {
            await test.ExecuteAsync($"GRANT SELECT(phase) ON weft.revision_ingestions TO {role}");
            await test.BootstrapAsync($"GRANT {role} TO {Login}");
            try { await AssertRefusedAsync(database, seed); }
            finally { await test.BootstrapAsync($"REVOKE {role} FROM {Login}"); }
            await test.BootstrapAsync($"GRANT {role} TO {Login} WITH ADMIN OPTION");
            try { await AssertRefusedAsync(database, seed); }
            finally { await test.BootstrapAsync($"REVOKE {role} FROM {Login}"); }
            await AssertAdmittedAsync(database, seed);
        }
        finally
        {
            await test.ExecuteAsync($"REVOKE SELECT(phase) ON weft.revision_ingestions FROM {role}");
            await test.BootstrapAsync($"DROP ROLE {role}");
        }
    }

    [PostgresFact]
    public async Task UnsafeRoleAttributesReachablePrivilegesCreationRightsAndSwappedPoolsAreRefused()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        foreach (var connectionString in new[] { test.MigrationConnectionString, test.RuntimeConnectionString, test.IngestionConnectionString })
        {
            await using var wrongPool = new PostgresCatalogDatabase(connectionString);
            await AssertRefusedAsync(wrongPool, seed);
        }
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        await AssertAdmittedAsync(database, seed);
        foreach (var (alter, restore) in new[]
        {
            ($"ALTER ROLE {Login} SUPERUSER", $"ALTER ROLE {Login} NOSUPERUSER"),
            ($"ALTER ROLE {Login} BYPASSRLS", $"ALTER ROLE {Login} NOBYPASSRLS"),
            ($"ALTER ROLE {Login} CREATEROLE", $"ALTER ROLE {Login} NOCREATEROLE"),
            ($"ALTER ROLE {Login} CREATEDB", $"ALTER ROLE {Login} NOCREATEDB"),
            ($"ALTER ROLE {Login} REPLICATION", $"ALTER ROLE {Login} NOREPLICATION"),
            ($"ALTER ROLE {Login} INHERIT", $"ALTER ROLE {Login} NOINHERIT"),
            ($"GRANT weft_migrator TO {Login}", $"REVOKE weft_migrator FROM {Login}"),
            ($"GRANT pg_read_all_data TO {Login}", $"REVOKE pg_read_all_data FROM {Login}"),
            ($"GRANT pg_write_all_data TO {Login}", $"REVOKE pg_write_all_data FROM {Login}"),
            ($"GRANT pg_read_server_files TO {Login}", $"REVOKE pg_read_server_files FROM {Login}"),
            ($"GRANT pg_write_server_files TO {Login}", $"REVOKE pg_write_server_files FROM {Login}"),
            ($"GRANT pg_execute_server_program TO {Login}", $"REVOKE pg_execute_server_program FROM {Login}"),
            ($"GRANT pg_signal_backend TO {Login}", $"REVOKE pg_signal_backend FROM {Login}"),
            ($"GRANT CREATE ON DATABASE {test.Name} TO {Login}", $"REVOKE CREATE ON DATABASE {test.Name} FROM {Login}"),
            ($"GRANT TEMPORARY ON DATABASE {test.Name} TO {Login}", $"REVOKE TEMPORARY ON DATABASE {test.Name} FROM {Login}"),
            ($"GRANT CREATE ON SCHEMA weft TO {Login}", $"REVOKE CREATE ON SCHEMA weft FROM {Login}")
        })
        {
            await test.BootstrapAsync(alter);
            try { await AssertRefusedAsync(database, seed); }
            catch (Exception failure) { throw new InvalidOperationException($"Catalog role admission probe failed after: {alter}", failure); }
            finally { await test.BootstrapAsync(restore); }
            await AssertAdmittedAsync(database, seed);
        }
    }

    [PostgresFact]
    public async Task UnreachableAlternateSchemaOrSourceTableOwnersRejectCatalogReads()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        await AssertAdmittedAsync(database, seed);
        var role = $"catalog_owner_{test.Name}";
        await test.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS");
        try
        {
            Assert.False(await test.ScalarAsync<bool>("SELECT pg_has_role($1,$2,'MEMBER')", Login, role));
            var targets = new[] { "SCHEMA weft" }
                .Concat(FullReadTables.Append("revision_ingestions").Select(table => $"TABLE weft.{table}"))
                .Concat(TransitionTables.Select(table => $"FUNCTION weft.guard_{table}_transition()"));
            foreach (var target in targets)
            {
                await test.BootstrapAsync($"ALTER {target} OWNER TO {role}");
                try { await AssertRefusedAsync(database, seed); }
                finally { await test.BootstrapAsync($"ALTER {target} OWNER TO weft_migrator"); }
                await AssertAdmittedAsync(database, seed);
            }
        }
        finally { await test.BootstrapAsync($"DROP ROLE {role}"); }
    }

    [PostgresFact]
    public async Task EveryScopedSourcePolicyRejectsMissingDisabledWeakenedAdditionalOrChangedAuthority()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        await AssertAdmittedAsync(database, seed);
        foreach (var table in ScopedTables)
        {
            var policy = $"{table}_organization_scope";
            var column = table == "organizations" ? "id" : "organization_id";
            var expression = $"{column} = NULLIF(current_setting('weft.organization_id',true),'')::uuid";
            var recreate = $"CREATE POLICY {policy} ON weft.{table} USING ({expression}) WITH CHECK ({expression})";
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE weft.{table} DISABLE ROW LEVEL SECURITY", $"ALTER TABLE weft.{table} ENABLE ROW LEVEL SECURITY"),
                ($"ALTER TABLE weft.{table} FORCE ROW LEVEL SECURITY", $"ALTER TABLE weft.{table} NO FORCE ROW LEVEL SECURITY"),
                ($"DROP POLICY {policy} ON weft.{table}", recreate),
                ($"ALTER POLICY {policy} ON weft.{table} USING (true)", $"ALTER POLICY {policy} ON weft.{table} USING ({expression})"),
                ($"ALTER POLICY {policy} ON weft.{table} WITH CHECK (true)", $"ALTER POLICY {policy} ON weft.{table} WITH CHECK ({expression})"),
                ($"CREATE POLICY catalog_unscoped_probe ON weft.{table} USING (true)", $"DROP POLICY catalog_unscoped_probe ON weft.{table}"),
                ($"ALTER POLICY {policy} ON weft.{table} TO {Login}", $"ALTER POLICY {policy} ON weft.{table} TO PUBLIC")
            })
                await MutateRefuseAndRestoreAsync(test, database, seed, alter, restore);
        }
        const string projectExpression = "organization_id = NULLIF(current_setting('weft.organization_id',true),'')::uuid";
        const string recreateProject = $"CREATE POLICY projects_organization_scope ON weft.projects USING ({projectExpression}) WITH CHECK ({projectExpression})";
        foreach (var (alter, restore) in new[]
        {
            ($"DROP POLICY projects_organization_scope ON weft.projects; CREATE POLICY projects_organization_scope ON weft.projects FOR SELECT USING ({projectExpression})",
             $"DROP POLICY projects_organization_scope ON weft.projects; {recreateProject}"),
            ($"DROP POLICY projects_organization_scope ON weft.projects; CREATE POLICY projects_organization_scope ON weft.projects AS RESTRICTIVE USING ({projectExpression}) WITH CHECK ({projectExpression})",
             $"DROP POLICY projects_organization_scope ON weft.projects; {recreateProject}"),
            ("ALTER TABLE weft.principals ENABLE ROW LEVEL SECURITY", "ALTER TABLE weft.principals DISABLE ROW LEVEL SECURITY")
        })
            await MutateRefuseAndRestoreAsync(test, database, seed, alter, restore);
    }

    [PostgresFact]
    public async Task ChangedTrustedTransitionsConstraintsOrSourceColumnsReturnErrorsInsteadOfEmptyCatalogs()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        await AssertAdmittedAsync(database, seed);
        foreach (var table in TransitionTables)
        {
            var trigger = $"{table}_transition_guard";
            var function = $"weft.guard_{table}_transition()";
            var triggerDefinition = await test.ScalarAsync<string>(
                "SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid=$1::regclass AND tgname=$2", $"weft.{table}", trigger);
            var functionDefinition = await test.ScalarAsync<string>("SELECT pg_get_functiondef($1::regprocedure)", function);
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE weft.{table} DISABLE TRIGGER {trigger}", $"ALTER TABLE weft.{table} ENABLE TRIGGER {trigger}"),
                ($"DROP TRIGGER {trigger} ON weft.{table}", triggerDefinition),
                ($"CREATE OR REPLACE FUNCTION {function} RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,weft AS $$ BEGIN RETURN NEW; END; $$", functionDefinition),
                ($"ALTER FUNCTION {function} SECURITY DEFINER", $"ALTER FUNCTION {function} SECURITY INVOKER"),
                ($"ALTER FUNCTION {function} SET search_path=public,weft", $"ALTER FUNCTION {function} SET search_path=pg_catalog,weft"),
                ($"GRANT EXECUTE ON FUNCTION {function} TO {Login}", $"REVOKE EXECUTE ON FUNCTION {function} FROM {Login}"),
                ($"GRANT EXECUTE ON FUNCTION {function} TO PUBLIC", $"REVOKE EXECUTE ON FUNCTION {function} FROM PUBLIC"),
                ($"CREATE TRIGGER catalog_transition_probe BEFORE UPDATE ON weft.{table} FOR EACH ROW EXECUTE FUNCTION {function}", $"DROP TRIGGER catalog_transition_probe ON weft.{table}")
            })
                await MutateRefuseAndRestoreAsync(test, database, seed, alter, restore);
        }
        // Reparse the accepted migration spelling: deparsed BETWEEN expressions
        // expand to AND trees that the raw grammar can flatten when reinstalled.
        foreach (var (table, constraint, sourceDefinition) in new[]
        {
            ("organizations", "organizations_id_check", "CHECK (id <> '00000000-0000-0000-0000-000000000000')"),
            ("principals", "principals_id_check", "CHECK (id <> '00000000-0000-0000-0000-000000000000')"),
            ("projects", "projects_name_check", "CHECK (char_length(name) BETWEEN 1 AND 200 AND char_length(btrim(name)) > 0)"),
            ("project_memberships", "project_memberships_role_check", "CHECK (role IN ('viewer', 'runner', 'publisher', 'administrator'))"),
            ("mappings", "mappings_name_check", "CHECK (char_length(name) BETWEEN 1 AND 200 AND name = btrim(name) AND char_length(btrim(name)) > 0)"),
            ("mapping_revisions", "mapping_revisions_admission_check", "CHECK (initial_admission IN ('eligible_valid', 'eligible_decoded_invalid'))"),
            ("revision_ingestions", "revision_ingestions_filename_check", """CHECK (octet_length(display_filename) BETWEEN 1 AND 255 AND display_filename NOT IN ('.', '..') AND display_filename !~ '[/\\[:cntrl:]]')""")
        })
        {
            var definition = await test.ScalarAsync<string>(
                "SELECT pg_get_constraintdef(oid,false) FROM pg_constraint WHERE conrelid=$1::regclass AND conname=$2", $"weft.{table}", constraint);
            var restore = $"ALTER TABLE weft.{table} DROP CONSTRAINT {constraint}; ALTER TABLE weft.{table} ADD CONSTRAINT {constraint} {sourceDefinition}";
            await MutateRefuseAndRestoreAsync(test, database, seed,
                $"ALTER TABLE weft.{table} DROP CONSTRAINT {constraint}; ALTER TABLE weft.{table} ADD CONSTRAINT {constraint} CHECK (true)", restore,
                async () => Assert.Equal(definition, await test.ScalarAsync<string>(
                    "SELECT pg_get_constraintdef(oid,false) FROM pg_constraint WHERE conrelid=$1::regclass AND conname=$2", $"weft.{table}", constraint)));
        }
        const string membershipConstraint = "organization_memberships_organization_id_fkey";
        var membershipDefinition = await test.ScalarAsync<string>(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='weft.organization_memberships'::regclass AND conname=$1", membershipConstraint);
        await MutateRefuseAndRestoreAsync(test, database, seed,
            $"ALTER TABLE weft.organization_memberships DROP CONSTRAINT {membershipConstraint}; ALTER TABLE weft.organization_memberships ADD CONSTRAINT {membershipConstraint} {membershipDefinition} NOT VALID",
            $"ALTER TABLE weft.organization_memberships DROP CONSTRAINT {membershipConstraint}; ALTER TABLE weft.organization_memberships ADD CONSTRAINT {membershipConstraint} {membershipDefinition}");
        var internalTrigger = await test.ScalarAsync<string>("""
            SELECT tgname FROM pg_trigger
            WHERE tgrelid='weft.project_memberships'::regclass AND tgisinternal AND tgconstraint<>0
            ORDER BY oid LIMIT 1
            """);
        foreach (var mode in new[] { "DISABLE", "ENABLE ALWAYS", "ENABLE REPLICA" })
        {
            await test.BootstrapAsync($"ALTER TABLE weft.project_memberships {mode} TRIGGER \"{internalTrigger}\"");
            try { await AssertRefusedAsync(database, seed); }
            finally { await test.BootstrapAsync($"ALTER TABLE weft.project_memberships ENABLE TRIGGER \"{internalTrigger}\""); }
            await AssertAdmittedAsync(database, seed);
        }
        foreach (var (alter, restore) in new[]
        {
            ("ALTER TABLE weft.projects ADD COLUMN catalog_unknown_column integer", "ALTER TABLE weft.projects DROP COLUMN catalog_unknown_column"),
            ("ALTER TABLE weft.projects ALTER COLUMN name DROP NOT NULL", "ALTER TABLE weft.projects ALTER COLUMN name SET NOT NULL"),
            ("ALTER TABLE weft.projects ALTER COLUMN version SET DEFAULT 2", "ALTER TABLE weft.projects ALTER COLUMN version SET DEFAULT 1"),
            ("ALTER TABLE weft.revision_ingestions ALTER COLUMN display_filename SET DEFAULT 'substituted.json'", "ALTER TABLE weft.revision_ingestions ALTER COLUMN display_filename DROP DEFAULT"),
            ("CREATE TRIGGER catalog_legacy_probe BEFORE UPDATE ON weft.projects FOR EACH ROW EXECUTE FUNCTION weft.guard_mappings_transition()", "DROP TRIGGER catalog_legacy_probe ON weft.projects"),
            ("CREATE RULE catalog_rewrite_probe AS ON UPDATE TO weft.mappings DO ALSO SELECT 1", "DROP RULE catalog_rewrite_probe ON weft.mappings")
        })
            await MutateRefuseAndRestoreAsync(test, database, seed, alter, restore);
    }

    private static async Task AssertAdmittedAsync(PostgresCatalogDatabase database, PostgresSeedData seed)
    {
        var reader = new PostgresCatalogReader(database);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var project = Assert.IsType<CatalogProject>(await reader.GetProjectAsync(actor, new ProjectScope(seed.OrganizationA, seed.ProjectA)));
        Assert.Equal("Project A", project.Summary.Name);
        Assert.NotNull(await reader.ListMappingsAsync(actor, project.Summary.Scope, 10));
    }

    private static async Task AssertRefusedAsync(PostgresCatalogDatabase database, PostgresSeedData seed)
    {
        var reader = new PostgresCatalogReader(database);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var project = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetProjectAsync(actor, project));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ListMappingsAsync(actor, project, 10));
    }

    private static async Task MutateRefuseAndRestoreAsync(PostgresCase test, PostgresCatalogDatabase database,
        PostgresSeedData seed, string alter, string restore, Func<Task>? inspectRestoration = null)
    {
        await test.ExecuteAsync(alter);
        try { await AssertRefusedAsync(database, seed); }
        catch (Exception failure) { throw new InvalidOperationException($"Catalog admission probe failed after: {alter}", failure); }
        finally { await test.ExecuteAsync(restore); }
        if (inspectRestoration is not null)
            await inspectRestoration();
        await AssertAdmittedAsync(database, seed);
    }

    private static async Task AssertDirectPrivilegeDeniedAsync(NpgsqlDataSource source, Guid organization, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var bind = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true)", connection, transaction))
        {
            bind.Parameters.AddWithValue(organization.ToString("D"));
            await bind.ExecuteScalarAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.True(error.SqlState == PostgresErrorCodes.InsufficientPrivilege,
            $"Direct catalog statement had SQLSTATE {error.SqlState}: {sql}");
    }
}
