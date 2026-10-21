using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresProjectCreationMigrationTests(PostgresFixture fixture)
{
    private static readonly string[] HistoricalChecksums =
    [
        "0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1",
        "d61a8bc2d56b6a39607414cf0f14c58db511594369ac124fd270a08f77455ae5",
        "0ed73cc9d597390366b661beb24f3bdfeddf1dbd9d49812ae575c2c42283fff3",
        "7335f18b8c5686b9e3ba3a2b9719d52e51fb22f5c07071e201bb7a239a48b113"
    ];

    [PostgresFact]
    public async Task FreshCurrentInstallationHasOnlyExplicitCreatorRightsAndNoSeededCapabilityOrFacts()
    {
        await using var test = await fixture.CreateCaseAsync();
        await PostgresSeedData.CreateAsync(test);
        await AssertCurrentHistoryAsync(test);
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.organization_project_creation_capabilities"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.project_creations"));
        Assert.True(await test.ScalarAsync<bool>("""
            SELECT has_schema_privilege('weft_project_creator','weft','USAGE')
              AND NOT has_schema_privilege('weft_project_creator','weft','CREATE')
              AND has_function_privilege('weft_project_creator','weft.get_project_creation_admission_v1(uuid,uuid)','EXECUTE')
              AND has_function_privilege('weft_project_creator','weft.create_project_v1(uuid,uuid,uuid,text,text)','EXECUTE')
              AND NOT EXISTS (SELECT 1 FROM information_schema.role_table_grants
                WHERE table_schema='weft' AND grantee='weft_project_creator')
              AND NOT EXISTS (SELECT 1 FROM information_schema.role_column_grants
                WHERE table_schema='weft' AND grantee='weft_project_creator')
            """));
        var history = await JournalAsync(test, maximumVersion: MigrationRunner.EmbeddedMigrations[^1].Version);
        var structure = await OldStructureAsync(test);
        await test.MigrateAsync();
        await test.MigrateAsync();
        Assert.Equal(history, await JournalAsync(test, maximumVersion: MigrationRunner.EmbeddedMigrations[^1].Version));
        Assert.Equal(structure, await OldStructureAsync(test));
    }

    [PostgresFact]
    public async Task ConcurrentUpgradeFromCurrent0004PreservesEveryOldFactDefinitionGrantAndJournalEntry()
    {
        await using var old = await Current0004Async(fixture);
        var historical = await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case);
        var structure = await OldStructureAsync(old.Case);
        var journal = await JournalAsync(old.Case);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var runner = new MigrationRunner(old.Case.MigrationConnectionString);
            await runner.MigrateAsync();
        }));
        await AssertCurrentHistoryAsync(old.Case);
        Assert.Equal(journal, await JournalAsync(old.Case, maximumVersion: 4));
        Assert.Equal(structure, await OldStructureAsync(old.Case));
        Assert.Equal(historical.RetainedFactsJson,
            (await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case)).RetainedFactsJson);
        await using var database = new PostgresProjectCreationDatabase(old.Case.ProjectCreationConnectionString);
        var creator = new PostgresProjectCreator(database);
        Assert.False((await creator.GetAdmissionAsync(old.Actor, old.Project.OrganizationId))!.CanCreateProjects);
        await using var provisioner = new OrganizationProjectCreationCapabilityProvisioner(old.Case.MigrationConnectionString);
        await provisioner.SetAsync(old.Project.OrganizationId, old.Actor.PrincipalId, true);
        var key = Guid.NewGuid();
        var created = await creator.CreateAsync(old.Actor, old.Project.OrganizationId, key, ProjectCreationTestSupport.DefaultMetadata);
        Assert.Equal(ProjectCreationOutcome.Created, created.Outcome);
        var newFact = await old.Case.ScalarAsync<string>("SELECT to_jsonb(fact)::text FROM weft.project_creations fact WHERE organization_id=$1 AND idempotency_key=$2",
            old.Project.OrganizationId, key);
        var afterCreation = await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case);
        var allJournal = await JournalAsync(old.Case, maximumVersion: MigrationRunner.EmbeddedMigrations[^1].Version);
        await old.Case.MigrateAsync();
        await old.Case.MigrateAsync();
        Assert.Equal(afterCreation, await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case));
        Assert.Equal(allJournal, await JournalAsync(old.Case, maximumVersion: MigrationRunner.EmbeddedMigrations[^1].Version));
        Assert.Equal(newFact, await old.Case.ScalarAsync<string>("SELECT to_jsonb(fact)::text FROM weft.project_creations fact WHERE organization_id=$1 AND idempotency_key=$2",
            old.Project.OrganizationId, key));
        var replay = await creator.CreateAsync(old.Actor, old.Project.OrganizationId, key, ProjectCreationTestSupport.DefaultMetadata);
        Assert.Equal(ProjectCreationOutcome.Replayed, replay.Outcome);
        Assert.Equal(created.Receipt, replay.Receipt);
    }

    [PostgresFact]
    public async Task Failed0005RollsBackNewTablesFunctionsGrantsAndJournalTogetherWithoutChangingCurrent0004()
    {
        await using var old = await Current0004Async(fixture);
        var before = await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case);
        var structure = await OldStructureAsync(old.Case);
        var migration = MigrationRunner.EmbeddedMigrations.Single(script => script.Version == 5);
        await using (var broken = new MigrationRunner(old.Case.MigrationConnectionString,
            [.. MigrationRunner.EmbeddedMigrations.Take(4), new MigrationScript(5, migration.Sql + "\nSELECT 1/0;\n")]))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => broken.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        Assert.Equal(before, await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case));
        Assert.Equal(structure, await OldStructureAsync(old.Case));
        Assert.False(await old.Case.ScalarAsync<bool>("SELECT has_schema_privilege('weft_project_creator','weft','USAGE')"));
        Assert.Equal(0L, await old.Case.ScalarAsync<long>("""
            SELECT count(*) FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace schema ON schema.oid=relation.relnamespace
              WHERE schema.nspname='weft' AND relation.relname IN ('project_creations','organization_project_creation_capabilities')
            """));
        Assert.Equal(0L, await old.Case.ScalarAsync<long>("""
            SELECT count(*) FROM pg_catalog.pg_proc routine JOIN pg_catalog.pg_namespace schema ON schema.oid=routine.pronamespace
              WHERE schema.nspname='weft' AND routine.proname IN ('get_project_creation_admission_v1','create_project_v1',
                'assert_project_creation_contract_v1','project_creation_fingerprint_v1','guard_project_creations_transition')
            """));
        await old.Case.MigrateAsync();
        await AssertCurrentHistoryAsync(old.Case);
        Assert.Equal(before.RetainedFactsJson,
            (await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case)).RetainedFactsJson);
    }

    [PostgresFact]
    public async Task MissingCreatorRoleRollsBackFreshAndCurrent0004InstallationsInItsOwnCluster()
    {
        var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionProjectCreator: false);
        await isolated.InitializeAsync();
        try
        {
            await using var fresh = await isolated.CreateCaseAsync(migrate: false);
            await using var old = await Current0004Async(isolated);
            var before = await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case);
            var freshError = await Assert.ThrowsAsync<PostgresException>(fresh.MigrateAsync);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, freshError.SqlState);
            Assert.Equal(0L, await fresh.ScalarAsync<long>("SELECT count(*) FROM pg_catalog.pg_namespace WHERE nspname='weft'"));
            var upgradeError = await Assert.ThrowsAsync<PostgresException>(old.Case.MigrateAsync);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, upgradeError.SqlState);
            Assert.Equal(before, await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case));
            var password = new NpgsqlConnectionStringBuilder(fresh.ProjectCreationConnectionString).Password!;
            Assert.True(Guid.TryParseExact(password, "N", out _));
            await fresh.BootstrapAsync($"""
                CREATE ROLE weft_project_creator LOGIN PASSWORD '{password}'
                  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                GRANT CONNECT ON DATABASE {fresh.Name}, {old.Case.Name} TO weft_project_creator;
                """);
            await fresh.MigrateAsync();
            await old.Case.MigrateAsync();
            await AssertCurrentHistoryAsync(fresh);
            await AssertCurrentHistoryAsync(old.Case);
            Assert.Equal(before.RetainedFactsJson,
                (await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case)).RetainedFactsJson);
            await using var database = new PostgresProjectCreationDatabase(old.Case.ProjectCreationConnectionString);
            var admission = await new PostgresProjectCreator(database).GetAdmissionAsync(old.Actor, old.Project.OrganizationId);
            Assert.False(admission!.CanCreateProjects);
        }
        finally { await isolated.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task OwnerCapabilityProvisionerRefusesHistoricalOrTamperedHistoryWithoutSeedingCapability()
    {
        await using var old = await Current0004Async(fixture);
        await using var provisioner = new OrganizationProjectCreationCapabilityProvisioner(old.Case.MigrationConnectionString);
        var before = await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.SetAsync(old.Project.OrganizationId, old.Actor.PrincipalId, true));
        Assert.Equal(before, await HistoricalIngestionSeed.CaptureSnapshotAsync(old.Case));
        await old.Case.MigrateAsync();
        var checksum = await old.Case.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=5");
        await old.Case.ExecuteAsync("UPDATE weft.schema_migrations SET checksum=$1 WHERE version=5", new string('f', 64));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.SetAsync(old.Project.OrganizationId, old.Actor.PrincipalId, true));
            Assert.Equal(0L, await old.Case.ScalarAsync<long>("SELECT count(*) FROM weft.organization_project_creation_capabilities"));
        }
        finally { await old.Case.ExecuteAsync("UPDATE weft.schema_migrations SET checksum=$1 WHERE version=5", checksum); }
        await provisioner.SetAsync(old.Project.OrganizationId, old.Actor.PrincipalId, true);
        Assert.True(await old.Case.ScalarAsync<bool>("SELECT can_create_projects FROM weft.organization_project_creation_capabilities WHERE organization_id=$1 AND principal_id=$2",
            old.Project.OrganizationId, old.Actor.PrincipalId.Value));
    }

    private static async Task<PostgresCatalogTestSupport> Current0004Async(PostgresFixture owner)
    {
        var support = await PostgresCatalogTestSupport.CreateAsync(owner, installCatalog: false);
        try
        {
            var created = await support.CommitCreateAsync(filename: "historical-project.native.json");
            await support.CommitAppendAsync(created, valid: false);
            await using var prefix = new MigrationRunner(support.Case.MigrationConnectionString,
                MigrationRunner.EmbeddedMigrations.Take(4).ToArray());
            await prefix.MigrateAsync();
            Assert.Equal(HistoricalChecksums, await support.Case.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
            return support;
        }
        catch { await support.DisposeAsync(); throw; }
    }

    private static async Task AssertCurrentHistoryAsync(PostgresCase test)
    {
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(HistoricalChecksums.Append(ProjectCreationTestSupport.MigrationChecksum).Append(RevisionDerivationSchemaContract.MigrationChecksum).Append(MaintenanceSchemaContract.MigrationChecksum).Append(ExternalIdentitySchemaContract.MigrationChecksum).Append(PayloadPermissionSchemaContract.MigrationChecksum).Append(ServerSessionSchemaContract.MigrationChecksum).Append(ChatSchemaContract.MigrationChecksum).Append(PreviewSchemaTestContract.MigrationChecksum).Append(PreviewSchemaTestContract.MessagesMigrationChecksum).Append(RegressionSchemaTestContract.CasesChecksum).Append(RegressionSchemaTestContract.RunsChecksum).Append(RegressionSchemaTestContract.DispatchChecksum),
            await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
    }

    private static Task<string> JournalAsync(PostgresCase test, int maximumVersion = 7) => test.ScalarAsync<string>(
        "SELECT jsonb_agg(to_jsonb(journal) ORDER BY version)::text FROM weft.schema_migrations journal WHERE version<=$1", maximumVersion);

    // Pin the full0004 object set; later supplements are asserted by their own exact schema tests.
    private static Task<string> OldStructureAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
          'relations',(SELECT jsonb_agg(jsonb_build_object('name',relation.relname,'owner',pg_get_userbyid(relation.relowner),
            'rls',relation.relrowsecurity,'force',relation.relforcerowsecurity,'acl',
            (SELECT jsonb_agg(jsonb_build_object('grantor',pg_get_userbyid(acl.grantor),'grantee',acl.grantee,
              'privilege',acl.privilege_type,'grantable',acl.is_grantable)
              ORDER BY acl.grantor,acl.grantee,acl.privilege_type,acl.is_grantable)
              FROM pg_catalog.aclexplode(relation.relacl) acl
              WHERE NOT (relation.relname IN ('organizations','principals','organization_memberships','projects',
                'project_memberships','mappings','mapping_revisions') AND acl.privilege_type='SELECT'
                AND NOT acl.is_grantable AND acl.grantor=relation.relowner
                AND acl.grantee IN (SELECT oid FROM pg_catalog.pg_roles WHERE rolname IN ('weft_report_reader','weft_deriver')))))
            ORDER BY relation.relname)
            FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace schema ON schema.oid=relation.relnamespace
            WHERE schema.nspname='weft' AND relation.relname IN ('organizations','principals','organization_memberships','projects',
              'project_memberships','mappings','revision_ingestions','revision_ingestion_attempts','mapping_revisions','schema_migrations')),
          'columns',(SELECT jsonb_agg(to_jsonb(column_row) ORDER BY table_name,ordinal_position)
            FROM information_schema.columns column_row WHERE table_schema='weft' AND table_name IN
              ('organizations','principals','organization_memberships','projects','project_memberships',
               'mappings','mapping_revisions','revision_ingestions','revision_ingestion_attempts','schema_migrations')),
          'constraints',(SELECT jsonb_agg(jsonb_build_object('table',relation.relname,'name',constraint_row.conname,
            'definition',pg_get_constraintdef(constraint_row.oid),'validated',constraint_row.convalidated,
            'deferred',constraint_row.condeferrable,'initiallyDeferred',constraint_row.condeferred) ORDER BY relation.relname,constraint_row.conname)
            FROM pg_catalog.pg_constraint constraint_row JOIN pg_catalog.pg_class relation ON relation.oid=constraint_row.conrelid
            JOIN pg_catalog.pg_namespace schema ON schema.oid=relation.relnamespace WHERE schema.nspname='weft'
              AND relation.relname IN ('organizations','principals','organization_memberships','projects','project_memberships',
                'mappings','mapping_revisions','revision_ingestions','revision_ingestion_attempts','schema_migrations')),
          'policies',(SELECT jsonb_agg(jsonb_build_object('table',relation.relname,'name',policy.polname,'command',policy.polcmd,
            'permissive',policy.polpermissive,'roles',policy.polroles::text,'using',pg_get_expr(policy.polqual,policy.polrelid),
            'check',pg_get_expr(policy.polwithcheck,policy.polrelid)) ORDER BY relation.relname,policy.polname)
            FROM pg_catalog.pg_policy policy JOIN pg_catalog.pg_class relation ON relation.oid=policy.polrelid
            JOIN pg_catalog.pg_namespace schema ON schema.oid=relation.relnamespace WHERE schema.nspname='weft'
              AND relation.relname IN ('organizations','principals','organization_memberships','projects','project_memberships',
                'mappings','mapping_revisions','revision_ingestions','revision_ingestion_attempts','schema_migrations')),
          'userTriggers',(SELECT jsonb_agg(jsonb_build_object('table',relation.relname,'name',trigger.tgname,
            'definition',pg_get_triggerdef(trigger.oid),'enabled',trigger.tgenabled) ORDER BY relation.relname,trigger.tgname)
            FROM pg_catalog.pg_trigger trigger JOIN pg_catalog.pg_class relation ON relation.oid=trigger.tgrelid
            JOIN pg_catalog.pg_namespace schema ON schema.oid=relation.relnamespace WHERE schema.nspname='weft' AND NOT trigger.tgisinternal
              AND trigger.tgname IN ('mappings_transition_guard','mapping_revisions_transition_guard',
                'revision_ingestions_transition_guard','revision_ingestion_attempts_transition_guard')),
          'oldFunctions',(SELECT jsonb_agg(jsonb_build_object('name',routine.proname,'definition',pg_get_functiondef(routine.oid),
            'acl',routine.proacl::text) ORDER BY routine.proname) FROM pg_catalog.pg_proc routine
            JOIN pg_catalog.pg_namespace schema ON schema.oid=routine.pronamespace WHERE schema.nspname='weft'
              AND routine.proname IN ('guard_mappings_transition','guard_revision_ingestions_transition',
                'guard_revision_ingestion_attempts_transition','guard_mapping_revisions_transition')),
          'oldRoleSchemaRights',(SELECT jsonb_agg(jsonb_build_object('role',role,
            'usage',has_schema_privilege(role,'weft','USAGE'),'create',has_schema_privilege(role,'weft','CREATE')) ORDER BY role)
            FROM unnest(ARRAY['weft_runtime','weft_ingest','weft_catalog_reader','weft_catalog_editor']) role))::text
        """);
}
