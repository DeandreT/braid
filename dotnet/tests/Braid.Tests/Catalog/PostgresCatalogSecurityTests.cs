using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresCatalogSecurityTests(PostgresFixture fixture)
{
    private const string Login = "braid_catalog_reader";
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
              AND NOT pg_has_role(current_user,'braid_migrator','MEMBER')
              AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                              WHERE n.nspname='braid' AND c.relowner=pg_roles.oid)
            FROM pg_roles WHERE rolname=current_user
            """))
            Assert.Equal(true, await safety.ExecuteScalarAsync());
        foreach (var table in FullReadTables)
        {
            await using var rights = direct.CreateCommand("""
                SELECT has_table_privilege($1,'SELECT')
                  AND NOT has_table_privilege($1,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                """);
            rights.Parameters.AddWithValue($"braid.{table}");
            Assert.Equal(true, await rights.ExecuteScalarAsync());
        }
        await using (var reservationRights = direct.CreateCommand("SELECT has_table_privilege('braid.revision_ingestions','SELECT')"))
            Assert.Equal(false, await reservationRights.ExecuteScalarAsync());
        foreach (var column in new[] { "organization_id", "project_id", "operation_id", "display_filename" })
        {
            await using var rights = direct.CreateCommand("SELECT has_column_privilege('braid.revision_ingestions',$1,'SELECT')");
            rights.Parameters.AddWithValue(column);
            Assert.Equal(true, await rights.ExecuteScalarAsync());
        }
        foreach (var column in new[] { "phase", "actor_principal_id", "lease_token", "intent_fingerprint", "engine_revision", "backend_build", "native_object_id", "native_digest", "last_error" })
        {
            await using var rights = direct.CreateCommand("SELECT has_column_privilege('braid.revision_ingestions',$1,'SELECT')");
            rights.Parameters.AddWithValue(column);
            Assert.Equal(false, await rights.ExecuteScalarAsync());
            await AssertDirectPrivilegeDeniedAsync(direct, seed.OrganizationA,
                $"SELECT {column} FROM braid.revision_ingestions WHERE false");
        }
        foreach (var sql in new[]
        {
            "SELECT * FROM braid.revision_ingestions WHERE false",
            "SELECT evidence FROM braid.revision_ingestion_attempts WHERE false",
            "SELECT generation FROM braid.revision_ingestion_attempts WHERE false",
            "SELECT checksum FROM braid.schema_migrations",
            "SELECT braid.guard_mappings_transition()",
            "SET ROLE braid_migrator",
            "SET ROLE braid_ingest",
            "CREATE SCHEMA catalog_security_probe",
            "CREATE TABLE braid.catalog_security_probe(id integer)",
            "CREATE TEMP TABLE catalog_security_probe(id integer)",
            "INSERT INTO braid.principals(id) VALUES (gen_random_uuid())",
            "INSERT INTO braid.mappings(organization_id) SELECT organization_id FROM braid.projects WHERE false",
            "INSERT INTO braid.revision_ingestions(organization_id) SELECT organization_id FROM braid.projects WHERE false",
            "UPDATE braid.projects SET name='changed' WHERE false",
            "UPDATE braid.mappings SET name='changed' WHERE false",
            "UPDATE braid.revision_ingestions SET display_filename='changed.json' WHERE false",
            "UPDATE braid.mapping_revisions SET native_digest=repeat('0',64) WHERE false",
            "DELETE FROM braid.projects WHERE false",
            "DELETE FROM braid.mappings WHERE false",
            "DELETE FROM braid.revision_ingestions WHERE false",
            "DELETE FROM braid.mapping_revisions WHERE false",
            "TRUNCATE braid.mapping_revisions",
            "UPDATE braid.schema_migrations SET checksum=repeat('0',64)"
        })
            await AssertDirectPrivilegeDeniedAsync(direct, seed.OrganizationA, sql);
        await using (var scope = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            await using var allowed = scope.CreateCommand("SELECT organization_id,project_id,operation_id,display_filename FROM braid.revision_ingestions");
            await using var rows = await allowed.ExecuteReaderAsync();
            Assert.False(await rows.ReadAsync());
        }
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
    }

    [PostgresFact]
    public async Task NextLeaseRejectsExtraPrivateColumnsWritesFunctionsUnknownRelationsAndGrantOptions()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        await AssertAdmittedAsync(database, seed);
        await test.ExecuteAsync("CREATE TABLE braid.catalog_grant_probe(id integer)");
        await test.ExecuteAsync("CREATE SEQUENCE braid.catalog_grant_probe_sequence");
        await test.ExecuteAsync("CREATE FUNCTION braid.catalog_grant_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
        await test.ExecuteAsync("REVOKE ALL ON FUNCTION braid.catalog_grant_probe() FROM PUBLIC");
        try
        {
            foreach (var (alter, restore) in new[]
            {
                ($"GRANT SELECT(phase) ON braid.revision_ingestions TO {Login}", $"REVOKE SELECT(phase) ON braid.revision_ingestions FROM {Login}"),
                ($"GRANT SELECT(native_digest) ON braid.revision_ingestions TO {Login}", $"REVOKE SELECT(native_digest) ON braid.revision_ingestions FROM {Login}"),
                ($"GRANT SELECT ON braid.revision_ingestions TO {Login}", $"""
                    REVOKE SELECT ON braid.revision_ingestions FROM {Login};
                    GRANT SELECT(organization_id,project_id,operation_id,display_filename) ON braid.revision_ingestions TO {Login}
                    """),
                ($"GRANT SELECT(evidence) ON braid.revision_ingestion_attempts TO {Login}", $"REVOKE SELECT(evidence) ON braid.revision_ingestion_attempts FROM {Login}"),
                ($"GRANT SELECT ON braid.revision_ingestion_attempts TO {Login}", $"REVOKE SELECT ON braid.revision_ingestion_attempts FROM {Login}"),
                ($"GRANT INSERT(name) ON braid.projects TO {Login}", $"REVOKE INSERT(name) ON braid.projects FROM {Login}"),
                ($"GRANT UPDATE(name) ON braid.mappings TO {Login}", $"REVOKE UPDATE(name) ON braid.mappings FROM {Login}"),
                ($"GRANT UPDATE(display_filename) ON braid.revision_ingestions TO {Login}", $"REVOKE UPDATE(display_filename) ON braid.revision_ingestions FROM {Login}"),
                ($"GRANT DELETE ON braid.mapping_revisions TO {Login}", $"REVOKE DELETE ON braid.mapping_revisions FROM {Login}"),
                ($"GRANT REFERENCES(id) ON braid.projects TO {Login}", $"REVOKE REFERENCES(id) ON braid.projects FROM {Login}"),
                ($"GRANT TRUNCATE,TRIGGER ON braid.mapping_revisions TO {Login}", $"REVOKE TRUNCATE,TRIGGER ON braid.mapping_revisions FROM {Login}"),
                ($"GRANT SELECT ON braid.schema_migrations TO {Login}", $"REVOKE SELECT ON braid.schema_migrations FROM {Login}"),
                ($"GRANT SELECT(id) ON braid.catalog_grant_probe TO {Login}", $"REVOKE SELECT(id) ON braid.catalog_grant_probe FROM {Login}"),
                ($"GRANT EXECUTE ON FUNCTION braid.catalog_grant_probe() TO {Login}", $"REVOKE EXECUTE ON FUNCTION braid.catalog_grant_probe() FROM {Login}"),
                ($"GRANT USAGE ON SEQUENCE braid.catalog_grant_probe_sequence TO {Login}", $"REVOKE USAGE ON SEQUENCE braid.catalog_grant_probe_sequence FROM {Login}"),
                ("GRANT SELECT ON SEQUENCE braid.catalog_grant_probe_sequence TO PUBLIC", "REVOKE SELECT ON SEQUENCE braid.catalog_grant_probe_sequence FROM PUBLIC"),
                ($"GRANT SELECT ON braid.projects TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR SELECT ON braid.projects FROM {Login}"),
                ($"GRANT SELECT(name) ON braid.projects TO {Login} WITH GRANT OPTION", $"REVOKE SELECT(name) ON braid.projects FROM {Login}"),
                ($"GRANT SELECT(display_filename) ON braid.revision_ingestions TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR SELECT(display_filename) ON braid.revision_ingestions FROM {Login}"),
                ($"GRANT USAGE ON SCHEMA braid TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR USAGE ON SCHEMA braid FROM {Login}"),
                ($"GRANT CONNECT ON DATABASE {test.Name} TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE {test.Name} FROM {Login}"),
                ($"REVOKE SELECT ON braid.project_memberships FROM {Login}", $"GRANT SELECT ON braid.project_memberships TO {Login}"),
                ($"REVOKE SELECT(display_filename) ON braid.revision_ingestions FROM {Login}", $"GRANT SELECT(display_filename) ON braid.revision_ingestions TO {Login}")
            })
                await MutateRefuseAndRestoreAsync(test, database, seed, alter, restore);
        }
        finally
        {
            await test.ExecuteAsync("DROP FUNCTION braid.catalog_grant_probe()");
            await test.ExecuteAsync("DROP SEQUENCE braid.catalog_grant_probe_sequence");
            await test.ExecuteAsync("DROP TABLE braid.catalog_grant_probe");
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
            ("GRANT SELECT(phase) ON braid.revision_ingestions TO PUBLIC", "REVOKE SELECT(phase) ON braid.revision_ingestions FROM PUBLIC"),
            ("GRANT SELECT(evidence) ON braid.revision_ingestion_attempts TO PUBLIC", "REVOKE SELECT(evidence) ON braid.revision_ingestion_attempts FROM PUBLIC"),
            ("GRANT UPDATE(name) ON braid.mappings TO PUBLIC", "REVOKE UPDATE(name) ON braid.mappings FROM PUBLIC"),
            ($"ALTER DEFAULT PRIVILEGES IN SCHEMA braid GRANT SELECT ON TABLES TO {Login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA braid REVOKE SELECT ON TABLES FROM {Login}"),
            ("ALTER DEFAULT PRIVILEGES IN SCHEMA braid GRANT UPDATE ON TABLES TO PUBLIC", "ALTER DEFAULT PRIVILEGES IN SCHEMA braid REVOKE UPDATE ON TABLES FROM PUBLIC"),
            ($"ALTER DEFAULT PRIVILEGES IN SCHEMA braid GRANT EXECUTE ON FUNCTIONS TO {Login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA braid REVOKE EXECUTE ON FUNCTIONS FROM {Login}")
        })
            await MutateRefuseAndRestoreAsync(test, database, seed, alter, restore);
        var role = $"catalog_attack_{test.Name}";
        await test.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS");
        try
        {
            await test.ExecuteAsync($"GRANT SELECT(phase) ON braid.revision_ingestions TO {role}");
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
            await test.ExecuteAsync($"REVOKE SELECT(phase) ON braid.revision_ingestions FROM {role}");
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
            ($"GRANT braid_migrator TO {Login}", $"REVOKE braid_migrator FROM {Login}"),
            ($"GRANT pg_read_all_data TO {Login}", $"REVOKE pg_read_all_data FROM {Login}"),
            ($"GRANT pg_write_all_data TO {Login}", $"REVOKE pg_write_all_data FROM {Login}"),
            ($"GRANT pg_read_server_files TO {Login}", $"REVOKE pg_read_server_files FROM {Login}"),
            ($"GRANT pg_write_server_files TO {Login}", $"REVOKE pg_write_server_files FROM {Login}"),
            ($"GRANT pg_execute_server_program TO {Login}", $"REVOKE pg_execute_server_program FROM {Login}"),
            ($"GRANT pg_signal_backend TO {Login}", $"REVOKE pg_signal_backend FROM {Login}"),
            ($"GRANT CREATE ON DATABASE {test.Name} TO {Login}", $"REVOKE CREATE ON DATABASE {test.Name} FROM {Login}"),
            ($"GRANT TEMPORARY ON DATABASE {test.Name} TO {Login}", $"REVOKE TEMPORARY ON DATABASE {test.Name} FROM {Login}"),
            ($"GRANT CREATE ON SCHEMA braid TO {Login}", $"REVOKE CREATE ON SCHEMA braid FROM {Login}")
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
            var targets = new[] { "SCHEMA braid" }
                .Concat(FullReadTables.Append("revision_ingestions").Select(table => $"TABLE braid.{table}"))
                .Concat(TransitionTables.Select(table => $"FUNCTION braid.guard_{table}_transition()"));
            foreach (var target in targets)
            {
                await test.BootstrapAsync($"ALTER {target} OWNER TO {role}");
                try { await AssertRefusedAsync(database, seed); }
                finally { await test.BootstrapAsync($"ALTER {target} OWNER TO braid_migrator"); }
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
            var expression = $"{column} = NULLIF(current_setting('braid.organization_id',true),'')::uuid";
            var recreate = $"CREATE POLICY {policy} ON braid.{table} USING ({expression}) WITH CHECK ({expression})";
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE braid.{table} DISABLE ROW LEVEL SECURITY", $"ALTER TABLE braid.{table} ENABLE ROW LEVEL SECURITY"),
                ($"ALTER TABLE braid.{table} FORCE ROW LEVEL SECURITY", $"ALTER TABLE braid.{table} NO FORCE ROW LEVEL SECURITY"),
                ($"DROP POLICY {policy} ON braid.{table}", recreate),
                ($"ALTER POLICY {policy} ON braid.{table} USING (true)", $"ALTER POLICY {policy} ON braid.{table} USING ({expression})"),
                ($"ALTER POLICY {policy} ON braid.{table} WITH CHECK (true)", $"ALTER POLICY {policy} ON braid.{table} WITH CHECK ({expression})"),
                ($"CREATE POLICY catalog_unscoped_probe ON braid.{table} USING (true)", $"DROP POLICY catalog_unscoped_probe ON braid.{table}"),
                ($"ALTER POLICY {policy} ON braid.{table} TO {Login}", $"ALTER POLICY {policy} ON braid.{table} TO PUBLIC")
            })
                await MutateRefuseAndRestoreAsync(test, database, seed, alter, restore);
        }
        const string projectExpression = "organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid";
        const string recreateProject = $"CREATE POLICY projects_organization_scope ON braid.projects USING ({projectExpression}) WITH CHECK ({projectExpression})";
        foreach (var (alter, restore) in new[]
        {
            ($"DROP POLICY projects_organization_scope ON braid.projects; CREATE POLICY projects_organization_scope ON braid.projects FOR SELECT USING ({projectExpression})",
             $"DROP POLICY projects_organization_scope ON braid.projects; {recreateProject}"),
            ($"DROP POLICY projects_organization_scope ON braid.projects; CREATE POLICY projects_organization_scope ON braid.projects AS RESTRICTIVE USING ({projectExpression}) WITH CHECK ({projectExpression})",
             $"DROP POLICY projects_organization_scope ON braid.projects; {recreateProject}"),
            ("ALTER TABLE braid.principals ENABLE ROW LEVEL SECURITY", "ALTER TABLE braid.principals DISABLE ROW LEVEL SECURITY")
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
            var function = $"braid.guard_{table}_transition()";
            var triggerDefinition = await test.ScalarAsync<string>(
                "SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid=$1::regclass AND tgname=$2", $"braid.{table}", trigger);
            var functionDefinition = await test.ScalarAsync<string>("SELECT pg_get_functiondef($1::regprocedure)", function);
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE braid.{table} DISABLE TRIGGER {trigger}", $"ALTER TABLE braid.{table} ENABLE TRIGGER {trigger}"),
                ($"DROP TRIGGER {trigger} ON braid.{table}", triggerDefinition),
                ($"CREATE OR REPLACE FUNCTION {function} RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,braid AS $$ BEGIN RETURN NEW; END; $$", functionDefinition),
                ($"ALTER FUNCTION {function} SECURITY DEFINER", $"ALTER FUNCTION {function} SECURITY INVOKER"),
                ($"ALTER FUNCTION {function} SET search_path=public,braid", $"ALTER FUNCTION {function} SET search_path=pg_catalog,braid"),
                ($"GRANT EXECUTE ON FUNCTION {function} TO {Login}", $"REVOKE EXECUTE ON FUNCTION {function} FROM {Login}"),
                ($"GRANT EXECUTE ON FUNCTION {function} TO PUBLIC", $"REVOKE EXECUTE ON FUNCTION {function} FROM PUBLIC"),
                ($"CREATE TRIGGER catalog_transition_probe BEFORE UPDATE ON braid.{table} FOR EACH ROW EXECUTE FUNCTION {function}", $"DROP TRIGGER catalog_transition_probe ON braid.{table}")
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
                "SELECT pg_get_constraintdef(oid,false) FROM pg_constraint WHERE conrelid=$1::regclass AND conname=$2", $"braid.{table}", constraint);
            var restore = $"ALTER TABLE braid.{table} DROP CONSTRAINT {constraint}; ALTER TABLE braid.{table} ADD CONSTRAINT {constraint} {sourceDefinition}";
            await MutateRefuseAndRestoreAsync(test, database, seed,
                $"ALTER TABLE braid.{table} DROP CONSTRAINT {constraint}; ALTER TABLE braid.{table} ADD CONSTRAINT {constraint} CHECK (true)", restore,
                async () => Assert.Equal(definition, await test.ScalarAsync<string>(
                    "SELECT pg_get_constraintdef(oid,false) FROM pg_constraint WHERE conrelid=$1::regclass AND conname=$2", $"braid.{table}", constraint)));
        }
        const string membershipConstraint = "organization_memberships_organization_id_fkey";
        var membershipDefinition = await test.ScalarAsync<string>(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='braid.organization_memberships'::regclass AND conname=$1", membershipConstraint);
        await MutateRefuseAndRestoreAsync(test, database, seed,
            $"ALTER TABLE braid.organization_memberships DROP CONSTRAINT {membershipConstraint}; ALTER TABLE braid.organization_memberships ADD CONSTRAINT {membershipConstraint} {membershipDefinition} NOT VALID",
            $"ALTER TABLE braid.organization_memberships DROP CONSTRAINT {membershipConstraint}; ALTER TABLE braid.organization_memberships ADD CONSTRAINT {membershipConstraint} {membershipDefinition}");
        var internalTrigger = await test.ScalarAsync<string>("""
            SELECT tgname FROM pg_trigger
            WHERE tgrelid='braid.project_memberships'::regclass AND tgisinternal AND tgconstraint<>0
            ORDER BY oid LIMIT 1
            """);
        foreach (var mode in new[] { "DISABLE", "ENABLE ALWAYS", "ENABLE REPLICA" })
        {
            await test.BootstrapAsync($"ALTER TABLE braid.project_memberships {mode} TRIGGER \"{internalTrigger}\"");
            try { await AssertRefusedAsync(database, seed); }
            finally { await test.BootstrapAsync($"ALTER TABLE braid.project_memberships ENABLE TRIGGER \"{internalTrigger}\""); }
            await AssertAdmittedAsync(database, seed);
        }
        foreach (var (alter, restore) in new[]
        {
            ("ALTER TABLE braid.projects ADD COLUMN catalog_unknown_column integer", "ALTER TABLE braid.projects DROP COLUMN catalog_unknown_column"),
            ("ALTER TABLE braid.projects ALTER COLUMN name DROP NOT NULL", "ALTER TABLE braid.projects ALTER COLUMN name SET NOT NULL"),
            ("ALTER TABLE braid.projects ALTER COLUMN version SET DEFAULT 2", "ALTER TABLE braid.projects ALTER COLUMN version SET DEFAULT 1"),
            ("ALTER TABLE braid.revision_ingestions ALTER COLUMN display_filename SET DEFAULT 'substituted.json'", "ALTER TABLE braid.revision_ingestions ALTER COLUMN display_filename DROP DEFAULT"),
            ("CREATE TRIGGER catalog_legacy_probe BEFORE UPDATE ON braid.projects FOR EACH ROW EXECUTE FUNCTION braid.guard_mappings_transition()", "DROP TRIGGER catalog_legacy_probe ON braid.projects"),
            ("CREATE RULE catalog_rewrite_probe AS ON UPDATE TO braid.mappings DO ALSO SELECT 1", "DROP RULE catalog_rewrite_probe ON braid.mappings")
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
        await using (var bind = new NpgsqlCommand("SELECT set_config('braid.organization_id',$1,true)", connection, transaction))
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
