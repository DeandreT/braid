using System.Data;
using Npgsql;
using Weft.Application;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresRevisionDerivationMigrationTests(PostgresFixture fixture)
{
    private static readonly string[] HistoricalChecksums =
    [
        "0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1",
        "d61a8bc2d56b6a39607414cf0f14c58db511594369ac124fd270a08f77455ae5",
        "0ed73cc9d597390366b661beb24f3bdfeddf1dbd9d49812ae575c2c42283fff3",
        "7335f18b8c5686b9e3ba3a2b9719d52e51fb22f5c07071e201bb7a239a48b113",
        "2ae8603df7c7ecd56e587ae65d32e1225b8569702e98ff24e5d0fc1694fae5c1"
    ];

    [PostgresFact]
    public async Task Fresh0006HasNoSeededFactsAndReplayPreservesJournalDefinitionsAndCredentials()
    {
        await using var test = await fixture.CreateCaseAsync();
        await AssertCurrentHistoryAsync(test);
        Assert.Equal(0L, await test.ScalarAsync<long>("""
            SELECT (SELECT count(*) FROM weft.organizations)+(SELECT count(*) FROM weft.principals)
              +(SELECT count(*) FROM weft.organization_memberships)+(SELECT count(*) FROM weft.projects)
              +(SELECT count(*) FROM weft.project_memberships)+(SELECT count(*) FROM weft.mappings)
              +(SELECT count(*) FROM weft.mapping_revisions)+(SELECT count(*) FROM weft.revision_ingestions)
              +(SELECT count(*) FROM weft.revision_ingestion_attempts)+(SELECT count(*) FROM weft.project_creations)
              +(SELECT count(*) FROM weft.organization_project_creation_capabilities)
              +(SELECT count(*) FROM weft.revision_derivations)+(SELECT count(*) FROM weft.revision_derivation_attempts)
            """));
        await AssertSourceIndexAsync(test);
        var journal = await JournalAsync(test);
        var definitions = await OldDefinitionsAsync(test);
        var roles = await OldRolesAsync(test);
        await AssertCredentialsAsync(test);
        await test.MigrateAsync();
        await test.MigrateAsync();
        Assert.Equal(journal, await JournalAsync(test));
        Assert.Equal(definitions, await OldDefinitionsAsync(test));
        Assert.Equal(roles, await OldRolesAsync(test));
        await AssertCredentialsAsync(test);
        await AssertNoReportRowsAsync(test);
    }

    [PostgresFact]
    public async Task ConcurrentUpgradeFromExact0005PreservesCompleteOldFactsDefinitionsGrantsAndCredentials()
    {
        await using var old = await Current0005Async(fixture);
        var before = await FactsAsync(old.Case);
        var journal = await JournalAsync(old.Case, 5);
        var definitions = await OldDefinitionsAsync(old.Case);
        var roles = await OldRolesAsync(old.Case);
        await AssertCredentialsAsync(old.Case);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var runner = new MigrationRunner(old.Case.MigrationConnectionString);
            await runner.MigrateAsync();
        }));
        await AssertCurrentHistoryAsync(old.Case);
        Assert.Equal(journal, await JournalAsync(old.Case, 5));
        Assert.Equal(before, await FactsAsync(old.Case));
        Assert.Equal(definitions, await OldDefinitionsAsync(old.Case));
        Assert.Equal(roles, await OldRolesAsync(old.Case));
        await AssertCredentialsAsync(old.Case);
        await AssertNoReportRowsAsync(old.Case);
        await AssertSourceIndexAsync(old.Case);
        var allJournal = await JournalAsync(old.Case);
        await old.Case.MigrateAsync();
        await old.Case.MigrateAsync();
        Assert.Equal(allJournal, await JournalAsync(old.Case));
        Assert.Equal(before, await FactsAsync(old.Case));
        Assert.Equal(definitions, await OldDefinitionsAsync(old.Case));
        Assert.Equal(roles, await OldRolesAsync(old.Case));
        await AssertCredentialsAsync(old.Case);
    }

    [PostgresFact]
    public async Task Failed0006RollsBackItsIndexTablesFunctionsGrantsAndJournalThenRecovers()
    {
        await using var old = await Current0005Async(fixture);
        var facts = await FactsAsync(old.Case);
        var definitions = await OldDefinitionsAsync(old.Case);
        var roles = await OldRolesAsync(old.Case);
        var journal = await JournalAsync(old.Case, 5);
        var migration = MigrationRunner.EmbeddedMigrations.Single(script => script.Version == 6);
        await using (var broken = new MigrationRunner(old.Case.MigrationConnectionString,
            [.. MigrationRunner.EmbeddedMigrations.Take(5), new MigrationScript(6, migration.Sql + "\nSELECT 1/0;\n")]))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => broken.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        Assert.Equal(facts, await FactsAsync(old.Case));
        Assert.Equal(definitions, await OldDefinitionsAsync(old.Case));
        Assert.Equal(roles, await OldRolesAsync(old.Case));
        Assert.Equal(journal, await JournalAsync(old.Case, 5));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, await VersionsAsync(old.Case));
        await AssertNo0006ObjectsOrRightsAsync(old.Case);
        await AssertCredentialsAsync(old.Case);
        await old.Case.MigrateAsync();
        await AssertCurrentHistoryAsync(old.Case);
        Assert.Equal(facts, await FactsAsync(old.Case));
        Assert.Equal(definitions, await OldDefinitionsAsync(old.Case));
        Assert.Equal(journal, await JournalAsync(old.Case, 5));
        await AssertCredentialsAsync(old.Case);
        await AssertNoReportRowsAsync(old.Case);
    }

    [PostgresFact]
    public Task MissingReaderRoleRollsBackFreshAndExact0005UpgradeThenExplicitProvisioningRecovers() =>
        MissingRoleAsync("weft_report_reader");

    [PostgresFact]
    public Task MissingDeriverRoleRollsBackFreshAndExact0005UpgradeThenExplicitProvisioningRecovers() =>
        MissingRoleAsync("weft_deriver");

    private static async Task MissingRoleAsync(string role)
    {
        // Absence is created in a separate exact-owned cluster, never by removing
        // a shared fixture role or touching another case's resources.
        var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionProjectCreator: true,
            provisionReportReader: role != "weft_report_reader", provisionDeriver: role != "weft_deriver");
        await isolated.InitializeAsync();
        try
        {
            await using var fresh = await isolated.CreateCaseAsync(migrate: false);
            await using var old = await Current0005Async(isolated);
            var facts = await FactsAsync(old.Case);
            var definitions = await OldDefinitionsAsync(old.Case);
            var roles = await OldRolesAsync(old.Case);
            var journal = await JournalAsync(old.Case, 5);
            var freshError = await Assert.ThrowsAsync<PostgresException>(fresh.MigrateAsync);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, freshError.SqlState);
            Assert.Equal(0L, await fresh.ScalarAsync<long>("SELECT count(*) FROM pg_catalog.pg_namespace WHERE nspname='weft'"));
            var upgradeError = await Assert.ThrowsAsync<PostgresException>(old.Case.MigrateAsync);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, upgradeError.SqlState);
            Assert.Equal(facts, await FactsAsync(old.Case));
            Assert.Equal(definitions, await OldDefinitionsAsync(old.Case));
            Assert.Equal(roles, await OldRolesAsync(old.Case));
            Assert.Equal(journal, await JournalAsync(old.Case, 5));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, await VersionsAsync(old.Case));
            await AssertNo0006ObjectsOrRightsAsync(old.Case);
            var configured = role == "weft_report_reader" ? fresh.ReportConnectionString : fresh.DerivationConnectionString;
            var password = new NpgsqlConnectionStringBuilder(configured).Password!;
            Assert.True(Guid.TryParseExact(password, "N", out _));
            await fresh.BootstrapAsync($"""
                CREATE ROLE {role} LOGIN PASSWORD '{password}'
                  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                GRANT CONNECT ON DATABASE {fresh.Name}, {old.Case.Name} TO {role};
                """);
            await fresh.MigrateAsync();
            await old.Case.MigrateAsync();
            await AssertCurrentHistoryAsync(fresh);
            await AssertCurrentHistoryAsync(old.Case);
            Assert.Equal(facts, await FactsAsync(old.Case));
            Assert.Equal(definitions, await OldDefinitionsAsync(old.Case));
            Assert.Equal(roles, await OldRolesAsync(old.Case));
            Assert.Equal(journal, await JournalAsync(old.Case, 5));
            await AssertCredentialsAsync(fresh);
            await AssertCredentialsAsync(old.Case);
            await AssertNoReportRowsAsync(fresh);
            await AssertNoReportRowsAsync(old.Case);
        }
        finally { await isolated.DisposeAsync(); }
    }

    private static async Task<PostgresCatalogTestSupport> Current0005Async(PostgresFixture owner)
    {
        var support = await PostgresCatalogTestSupport.CreateAsync(owner, installCatalog: false);
        try
        {
            // Genuine qualified0002 admission facts precede unchanged0003..0005.
            // These are explicitly synthetic metadata fixtures, not native/store evidence.
            var created = await support.CommitCreateAsync(filename: "historical-report-source.native.json");
            await support.CommitAppendAsync(created, valid: false, filename: "historical-invalid.native.json");
            await using (var prefix = new MigrationRunner(support.Case.MigrationConnectionString,
                MigrationRunner.EmbeddedMigrations.Take(5).ToArray()))
                await prefix.MigrateAsync();
            Assert.Equal(HistoricalChecksums, await support.Case.ScalarAsync<string[]>(
                "SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
            await SeedSyntheticHistoricalCapabilityAsync(support);
            await using var database = new PostgresProjectCreationDatabase(support.Case.ProjectCreationConnectionString);
            var result = await new PostgresProjectCreator(database).CreateAsync(support.Actor, support.Project.OrganizationId,
                Guid.NewGuid(), ProjectCreationTestSupport.DefaultMetadata);
            Assert.Equal(ProjectCreationOutcome.Created, result.Outcome);
            return support;
        }
        catch { await support.DisposeAsync(); throw; }
    }

    private static async Task SeedSyntheticHistoricalCapabilityAsync(PostgresCatalogTestSupport support)
    {
        // Historical fixture data only. The current owner provisioner deliberately
        // requires the latest full history; this seed requires exactly unchanged0001..0005.
        await using var connection = await support.Case.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var protect = new NpgsqlCommand("""
            SET LOCAL lock_timeout='1s'; SET LOCAL statement_timeout='5s';
            LOCK TABLE ONLY weft.schema_migrations IN SHARE MODE;
            """, connection, transaction))
            await protect.ExecuteNonQueryAsync();
        await using (var owner = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user='weft_migrator'
              AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreaterole AND NOT rolreplication
              AND current_setting('transaction_read_only')='off'
              AND current_setting('session_replication_role')='origin' AND current_setting('row_security')='on'
            FROM pg_catalog.pg_roles WHERE rolname=current_user
            """, connection, transaction))
            Assert.True(await owner.ExecuteScalarAsync() is true);
        await using (var history = new NpgsqlCommand(
            "SELECT version,checksum FROM weft.schema_migrations ORDER BY version", connection, transaction))
        await using (var reader = await history.ExecuteReaderAsync())
        {
            for (var index = 0; index < HistoricalChecksums.Length; index++)
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(index + 1, reader.GetInt32(0));
                Assert.Equal(HistoricalChecksums[index], reader.GetString(1));
            }
            Assert.False(await reader.ReadAsync());
        }
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO weft.organization_project_creation_capabilities(organization_id,principal_id,can_create_projects)
            SELECT o.id,p.id,true FROM weft.organizations o
            JOIN weft.organization_memberships m ON m.organization_id=o.id
            JOIN weft.principals p ON p.id=m.principal_id
            WHERE o.id=$1 AND p.id=$2 AND p.active AND m.active
            """, connection, transaction))
        {
            seed.Parameters.AddWithValue(support.Project.OrganizationId);
            seed.Parameters.AddWithValue(support.Actor.PrincipalId.Value);
            Assert.Equal(1, await seed.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();
    }

    private static Task<int[]> VersionsAsync(PostgresCase test) => test.ScalarAsync<int[]>(
        "SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations");

    private static async Task AssertCurrentHistoryAsync(PostgresCase test)
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, await VersionsAsync(test));
        Assert.Equal(HistoricalChecksums.Append(RevisionDerivationSchemaContract.MigrationChecksum),
            await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
    }

    private static Task<string> JournalAsync(PostgresCase test, int maximumVersion = 6) => test.ScalarAsync<string>(
        "SELECT jsonb_agg(to_jsonb(journal) ORDER BY version)::text FROM weft.schema_migrations journal WHERE version<=$1", maximumVersion);

    private static Task<string> FactsAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
          'organizations',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM weft.organizations t),
          'principals',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM weft.principals t),
          'organization_memberships',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM weft.organization_memberships t),
          'projects',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM weft.projects t),
          'project_memberships',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM weft.project_memberships t),
          'mappings',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM weft.mappings t),
          'mapping_revisions',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM weft.mapping_revisions t),
          'revision_ingestions',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM weft.revision_ingestions t),
          'revision_ingestion_attempts',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM weft.revision_ingestion_attempts t),
          'project_creations',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM weft.project_creations t),
          'organization_project_creation_capabilities',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text)
            FROM weft.organization_project_creation_capabilities t))::text
        """);

    private static Task<string> OldRolesAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_agg(jsonb_build_object('name',rolname,'super',rolsuper,'inherit',rolinherit,
          'createRole',rolcreaterole,'createDb',rolcreatedb,'login',rolcanlogin,'replication',rolreplication,
          'bypassRls',rolbypassrls,'connections',rolconnlimit,'validUntil',rolvaliduntil,'config',rolconfig,
          'connect',has_database_privilege(rolname,current_database(),'CONNECT'),
          'create',has_database_privilege(rolname,current_database(),'CREATE'),
          'temp',has_database_privilege(rolname,current_database(),'TEMP'),
          'memberships',(SELECT jsonb_agg(to_jsonb(m) ORDER BY m.roleid,m.member,m.grantor)
            FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)) ORDER BY rolname)::text
        FROM pg_catalog.pg_roles r WHERE rolname IN
          ('weft_migrator','weft_runtime','weft_ingest','weft_catalog_reader','weft_catalog_editor','weft_project_creator')
        """);

    private static async Task AssertCredentialsAsync(PostgresCase test)
    {
        foreach (var configured in new[] { test.MigrationConnectionString, test.RuntimeConnectionString,
                     test.IngestionConnectionString, test.CatalogConnectionString, test.MetadataConnectionString,
                     test.ProjectCreationConnectionString, test.ReportConnectionString, test.DerivationConnectionString })
        {
            // A new unpooled login proves the original configured credential still works;
            // no password/hash is selected, serialized or asserted as failure output.
            var options = new NpgsqlConnectionStringBuilder(configured) { Pooling = false };
            await using var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT current_user=$1 AND current_user=session_user", connection);
            command.Parameters.AddWithValue(options.Username!);
            Assert.True((bool)(await command.ExecuteScalarAsync())!);
        }
    }

    private static async Task AssertNoReportRowsAsync(PostgresCase test) => Assert.Equal(new long[] { 0, 0 },
        await test.ScalarAsync<long[]>("SELECT ARRAY[(SELECT count(*) FROM weft.revision_derivations),(SELECT count(*) FROM weft.revision_derivation_attempts)]"));

    private static async Task AssertNo0006ObjectsOrRightsAsync(PostgresCase test)
    {
        Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.ScalarAsync<long[]>("""
            SELECT ARRAY[
              (SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='weft' AND c.relname IN ('revision_derivations','revision_derivation_attempts','mapping_revisions_derivation_source_key')),
              (SELECT count(*) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='weft' AND p.proname IN ('guard_revision_derivations_transition','guard_revision_derivation_attempts_transition')),
              (SELECT count(*) FROM information_schema.role_table_grants WHERE table_schema='weft' AND grantee IN ('weft_report_reader','weft_deriver')),
              (SELECT count(*) FROM information_schema.role_column_grants WHERE table_schema='weft' AND grantee IN ('weft_report_reader','weft_deriver'))]
            """));
        Assert.Equal(0L, await test.ScalarAsync<long>("""
            SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname IN ('weft_report_reader','weft_deriver')
              AND has_schema_privilege(rolname,'weft','USAGE')
            """));
    }

    private static async Task AssertSourceIndexAsync(PostgresCase test) => Assert.True(await test.ScalarAsync<bool>("""
        SELECT i.indisunique AND NOT i.indisprimary AND i.indisvalid AND i.indisready
          AND i.indpred IS NULL AND i.indexprs IS NULL
          AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_constraint c WHERE c.conindid=i.indexrelid AND c.conrelid=i.indrelid)
          AND (SELECT array_agg(a.attname::text ORDER BY column_position.ordinality)
            FROM unnest(i.indkey) WITH ORDINALITY column_position(number,ordinality)
            JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=column_position.number)
            =ARRAY['organization_id','project_id','mapping_id','id','native_object_id','native_class',
              'native_digest','native_byte_count','logical_entry_document','dependency_manifest_id','backend_id']
        FROM pg_catalog.pg_index i WHERE i.indexrelid='weft.mapping_revisions_derivation_source_key'::regclass
        """));

    private static Task<string> OldDefinitionsAsync(PostgresCase test) => test.ScalarAsync<string>("""
        WITH old_relations AS (
          SELECT r.* FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
          WHERE n.nspname='weft' AND r.relkind IN ('r','p')
            AND r.relname NOT IN ('revision_derivations','revision_derivation_attempts'))
        SELECT jsonb_build_object(
          'relations',(SELECT jsonb_agg(jsonb_build_object('name',r.relname,'owner',pg_get_userbyid(r.relowner),
            'kind',r.relkind,'rls',r.relrowsecurity,'force',r.relforcerowsecurity,'acl',
            (SELECT jsonb_agg(to_jsonb(acl) ORDER BY acl.grantor,acl.grantee,acl.privilege_type,acl.is_grantable)
              FROM pg_catalog.aclexplode(r.relacl) acl WHERE NOT
                (r.relname IN ('organizations','principals','organization_memberships','projects','project_memberships','mappings','mapping_revisions')
                 AND acl.privilege_type='SELECT' AND NOT acl.is_grantable AND acl.grantor=r.relowner
                 AND acl.grantee IN (SELECT oid FROM pg_catalog.pg_roles WHERE rolname IN ('weft_report_reader','weft_deriver')))))
            ORDER BY r.relname) FROM old_relations r),
          'columns',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'number',a.attnum,'name',a.attname,
            'type',format_type(a.atttypid,a.atttypmod),'notNull',a.attnotnull,'identity',a.attidentity,
            'generated',a.attgenerated,'default',pg_get_expr(d.adbin,d.adrelid),'acl',a.attacl::text)
            ORDER BY r.relname,a.attnum) FROM old_relations r JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=r.oid AND d.adnum=a.attnum WHERE a.attnum>0 AND NOT a.attisdropped),
          'constraints',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'name',c.conname,'type',c.contype,
            'definition',pg_get_constraintdef(c.oid),'validated',c.convalidated,'deferred',c.condeferrable,
            'initiallyDeferred',c.condeferred,'noInherit',c.connoinherit) ORDER BY r.relname,c.conname)
            FROM old_relations r JOIN pg_catalog.pg_constraint c ON c.conrelid=r.oid),
          'indexes',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'name',index_relation.relname,
            'definition',pg_get_indexdef(i.indexrelid),'valid',i.indisvalid,'ready',i.indisready)
            ORDER BY r.relname,index_relation.relname) FROM old_relations r JOIN pg_catalog.pg_index i ON i.indrelid=r.oid
            JOIN pg_catalog.pg_class index_relation ON index_relation.oid=i.indexrelid
            WHERE index_relation.relname<>'mapping_revisions_derivation_source_key'),
          'policies',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'name',p.polname,'command',p.polcmd,
            'permissive',p.polpermissive,'roles',p.polroles::text,'using',pg_get_expr(p.polqual,p.polrelid),
            'check',pg_get_expr(p.polwithcheck,p.polrelid)) ORDER BY r.relname,p.polname)
            FROM old_relations r JOIN pg_catalog.pg_policy p ON p.polrelid=r.oid),
          'triggers',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'name',t.tgname,
            'definition',pg_get_triggerdef(t.oid),'enabled',t.tgenabled) ORDER BY r.relname,t.tgname)
            FROM old_relations r JOIN pg_catalog.pg_trigger t ON t.tgrelid=r.oid WHERE NOT t.tgisinternal),
          'functions',(SELECT jsonb_agg(jsonb_build_object('name',p.proname,'signature',pg_get_function_identity_arguments(p.oid),
            'owner',pg_get_userbyid(p.proowner),'definition',pg_get_functiondef(p.oid),'acl',p.proacl::text)
            ORDER BY p.proname,pg_get_function_identity_arguments(p.oid)) FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='weft'
              AND p.proname NOT IN ('guard_revision_derivations_transition','guard_revision_derivation_attempts_transition')),
          'schemaAcl',(SELECT jsonb_agg(to_jsonb(acl) ORDER BY acl.grantor,acl.grantee,acl.privilege_type,acl.is_grantable)
            FROM pg_catalog.pg_namespace n CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) acl
            WHERE n.nspname='weft' AND NOT (acl.privilege_type='USAGE' AND NOT acl.is_grantable AND acl.grantor=n.nspowner
              AND acl.grantee IN (SELECT oid FROM pg_catalog.pg_roles WHERE rolname IN ('weft_report_reader','weft_deriver')))),
          'defaultAcls',(SELECT jsonb_agg(to_jsonb(d) ORDER BY d.defaclrole,d.defaclnamespace,d.defaclobjtype)
            FROM pg_catalog.pg_default_acl d LEFT JOIN pg_catalog.pg_namespace n ON n.oid=d.defaclnamespace
            WHERE n.nspname='weft' OR d.defaclnamespace=0 AND pg_get_userbyid(d.defaclrole)='weft_migrator'))::text
        """);
}
