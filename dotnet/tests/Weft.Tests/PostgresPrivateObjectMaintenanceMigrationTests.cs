using System.Text;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual migration evidence; historical native/report rows below are authored metadata, not stored-object evidence.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresPrivateObjectMaintenanceMigrationTests(PostgresFixture fixture)
{
    private static readonly string[] PrefixChecksums =
    [
        "0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1",
        "d61a8bc2d56b6a39607414cf0f14c58db511594369ac124fd270a08f77455ae5",
        "0ed73cc9d597390366b661beb24f3bdfeddf1dbd9d49812ae575c2c42283fff3",
        "7335f18b8c5686b9e3ba3a2b9719d52e51fb22f5c07071e201bb7a239a48b113",
        "2ae8603df7c7ecd56e587ae65d32e1225b8569702e98ff24e5d0fc1694fae5c1",
        "b156596e07ec82a5cc8c11ef801d9ca5e15d4e6e64c76faf13635c005c986777"
    ];

    [PostgresFact]
    public async Task Fresh0007AndRepeatedMigrationHaveNoBackfillAndPreserveJournalCredentialsAndDefinitions()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await Migrate0007Async(test);
        await AssertHistoryAsync(test);
        Assert.Equal(0L, await test.ScalarAsync<long>("""
            SELECT (SELECT count(*) FROM weft.organizations)+(SELECT count(*) FROM weft.principals)
              +(SELECT count(*) FROM weft.organization_memberships)+(SELECT count(*) FROM weft.projects)+(SELECT count(*) FROM weft.project_memberships)
              +(SELECT count(*) FROM weft.mappings)+(SELECT count(*) FROM weft.mapping_revisions)
              +(SELECT count(*) FROM weft.revision_ingestions)+(SELECT count(*) FROM weft.revision_ingestion_attempts)
              +(SELECT count(*) FROM weft.project_creations)+(SELECT count(*) FROM weft.organization_project_creation_capabilities)
              +(SELECT count(*) FROM weft.revision_derivations)+(SELECT count(*) FROM weft.revision_derivation_attempts)
              +(SELECT count(*) FROM weft.private_object_publications)+(SELECT count(*) FROM weft.private_object_retirements)
              +(SELECT count(*) FROM weft.private_object_cleanup_attempts)
            """));
        var journal = await JournalAsync(test);
        var definitions = await AllDefinitionsAsync(test);
        await AssertCredentialsAsync(test);
        await Migrate0007Async(test);
        await Migrate0007Async(test);
        Assert.Equal(journal, await JournalAsync(test));
        Assert.Equal(definitions, await AllDefinitionsAsync(test));
        await AssertCredentialsAsync(test);
        await AssertNoMaintenanceRowsAsync(test);
    }

    [PostgresFact]
    public async Task ConcurrentGenuine0006UpgradePreservesAllPriorRowsDefinitionsEffectiveGrantsAndCredentials()
    {
        await using var old = await Current0006Async(fixture);
        var facts = await FactsAsync(old.Case);
        var journal = await JournalAsync(old.Case, 6);
        var definitions = await HistoricalDefinitionsAsync(old.Case);
        var grants = await HistoricalGrantsAsync(old.Case);
        await AssertCredentialsAsync(old.Case);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var runner = new MigrationRunner(old.Case.MigrationConnectionString,
                MigrationRunner.EmbeddedMigrations.Take(7).ToArray());
            await runner.MigrateAsync();
        }));
        await AssertHistoryAsync(old.Case);
        Assert.Equal(facts, await FactsAsync(old.Case));
        Assert.Equal(journal, await JournalAsync(old.Case, 6));
        Assert.Equal(definitions, await HistoricalDefinitionsAsync(old.Case));
        Assert.Equal(grants, await HistoricalGrantsAsync(old.Case));
        await AssertCredentialsAsync(old.Case);
        await AssertNoMaintenanceRowsAsync(old.Case);
        var allJournal = await JournalAsync(old.Case);
        var allDefinitions = await AllDefinitionsAsync(old.Case);
        await Migrate0007Async(old.Case);
        await Migrate0007Async(old.Case);
        Assert.Equal(allJournal, await JournalAsync(old.Case));
        Assert.Equal(allDefinitions, await AllDefinitionsAsync(old.Case));
        Assert.Equal(facts, await FactsAsync(old.Case));
    }

    [PostgresFact]
    public async Task Failed0007RollsBackEverySupplementAndGrantWithoutChangingGenuine0006ThenRecovers()
    {
        await using var old = await Current0006Async(fixture);
        var facts = await FactsAsync(old.Case);
        var journal = await JournalAsync(old.Case);
        var definitions = await AllDefinitionsAsync(old.Case);
        var grants = await HistoricalGrantsAsync(old.Case);
        var migration = MigrationRunner.EmbeddedMigrations.Single(script => script.Version == 7);
        await using (var broken = new MigrationRunner(old.Case.MigrationConnectionString,
            [.. MigrationRunner.EmbeddedMigrations.Take(6), new MigrationScript(7, migration.Sql + "\nSELECT 1/0;\n")]))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => broken.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        Assert.Equal(facts, await FactsAsync(old.Case));
        Assert.Equal(journal, await JournalAsync(old.Case));
        Assert.Equal(definitions, await AllDefinitionsAsync(old.Case));
        Assert.Equal(grants, await HistoricalGrantsAsync(old.Case));
        await AssertNo0007ObjectsOrRightsAsync(old.Case);
        await AssertCredentialsAsync(old.Case);
        await Migrate0007Async(old.Case);
        await AssertHistoryAsync(old.Case);
        Assert.Equal(facts, await FactsAsync(old.Case));
        Assert.Equal(journal, await JournalAsync(old.Case, 6));
        await AssertNoMaintenanceRowsAsync(old.Case);
    }

    [PostgresFact]
    public Task MissingMaintenanceRoleAtomicallyRefusesFreshAnd0006UpgradeThenExplicitProvisioningRecovers() =>
        RefusedRoleAsync(missing: true);

    [PostgresFact]
    public Task UnsafeMaintenanceRoleAtomicallyRefusesFreshAnd0006UpgradeWithoutAdoptingOrRotatingIt() =>
        RefusedRoleAsync(missing: false);

    [PostgresFact]
    public async Task DedicatedMaintenanceLoginCannotRunMigrationsOrChangeTheMigrationJournal()
    {
        await using var test = await fixture.CreateCaseAsync();
        var journal = await JournalAsync(test, 8);
        await using var runner = new MigrationRunner(test.MaintenanceConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync());
        var options = new NpgsqlConnectionStringBuilder(test.MaintenanceConnectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE weft.schema_migrations SET checksum=repeat('0',64)", connection);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        Assert.Equal(journal, await JournalAsync(test, 8));
    }

    private static async Task RefusedRoleAsync(bool missing)
    {
        // Role changes are confined to this exact-owned cluster, never the shared fixture.
        var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionMaintenance: !missing);
        await isolated.InitializeAsync();
        try
        {
            await using var fresh = await isolated.CreateCaseAsync(migrate: false);
            await using var old = await Current0006Async(isolated, maintenanceMissing: missing);
            var facts = await FactsAsync(old.Case);
            var journal = await JournalAsync(old.Case);
            var definitions = await AllDefinitionsAsync(old.Case);
            if (!missing) await fresh.BootstrapAsync("ALTER ROLE weft_maintenance INHERIT;");
            var freshError = await Assert.ThrowsAsync<PostgresException>(() => Migrate0007Async(fresh));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, freshError.SqlState);
            Assert.Equal(0L, await fresh.ScalarAsync<long>("SELECT count(*) FROM pg_namespace WHERE nspname='weft'"));
            var oldError = await Assert.ThrowsAsync<PostgresException>(() => Migrate0007Async(old.Case));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, oldError.SqlState);
            Assert.Equal(facts, await FactsAsync(old.Case));
            Assert.Equal(journal, await JournalAsync(old.Case));
            Assert.Equal(definitions, await AllDefinitionsAsync(old.Case));
            await AssertNo0007ObjectsOrRightsAsync(old.Case);
            if (missing)
            {
                var password = new NpgsqlConnectionStringBuilder(fresh.MaintenanceConnectionString).Password!;
                Assert.True(Guid.TryParseExact(password, "N", out _));
                await fresh.BootstrapAsync($"""
                    CREATE ROLE weft_maintenance LOGIN PASSWORD '{password}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    GRANT CONNECT ON DATABASE {fresh.Name}, {old.Case.Name} TO weft_maintenance;
                    """);
            }
            else await fresh.BootstrapAsync("ALTER ROLE weft_maintenance NOINHERIT;");
            await Migrate0007Async(fresh);
            await Migrate0007Async(old.Case);
            await AssertHistoryAsync(fresh);
            await AssertHistoryAsync(old.Case);
            Assert.Equal(facts, await FactsAsync(old.Case));
            Assert.Equal(journal, await JournalAsync(old.Case, 6));
            await AssertCredentialsAsync(fresh);
            await AssertCredentialsAsync(old.Case);
        }
        finally { await isolated.DisposeAsync(); }
    }

    // These source/ACL conservation cases qualify the original0007 increment.
    // Current-full authority/runtime cases and the genuine0007-to0008 upgrade remain separate.
    private static async Task Migrate0007Async(PostgresCase test)
    {
        await using var runner = new MigrationRunner(test.MigrationConnectionString,
            MigrationRunner.EmbeddedMigrations.Take(7).ToArray());
        await runner.MigrateAsync();
    }

    private static async Task<PostgresCatalogTestSupport> Current0006Async(PostgresFixture owner, bool maintenanceMissing = false)
    {
        var support = await PostgresCatalogTestSupport.CreateAsync(owner, installCatalog: false);
        try
        {
            // Genuine unchanged0002 creation/append precedes0003..0006. Evidence is
            // explicitly synthetic C1 metadata; no bytes, worker or filesystem are involved.
            var first = await support.CommitCreateAsync(filename: "historical-maintenance.native.json");
            await support.CommitAppendAsync(first, valid: false);
            await using (var runner = new MigrationRunner(support.Case.MigrationConnectionString,
                MigrationRunner.EmbeddedMigrations.Take(6).ToArray()))
                await runner.MigrateAsync();
            Assert.Equal(PrefixChecksums, await support.Case.ScalarAsync<string[]>(
                "SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
            var facts = first.Facts;
            var source = new RevisionReportSource(facts.FutureRevision, facts.NativeObject,
                facts.Intent.LogicalDocument, facts.Intent.DependencyIdentity, facts.BackendProfile.BackendId);
            foreach (var state in new[] { "pending", "failed", "usable" })
                await SeedSyntheticHistoricalReportAsync(support, new ProjectionCacheKey(source,
                    PostgresRevisionDerivationTestSupport.Profile, "weft.mapping-projection.v1",
                    "synthetic.historical." + state, "synthetic.report.codec.v1", new()), state);
            Assert.Equal(new long[] { 3, 3, 1, 1 }, await support.Case.ScalarAsync<long[]>("""
                SELECT ARRAY[(SELECT count(*) FROM weft.revision_derivations),
                  (SELECT count(*) FROM weft.revision_derivation_attempts),
                  (SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable),
                  (SELECT count(*) FROM weft.revision_derivation_attempts WHERE completion_kind='failed')]
                """));
            if (!maintenanceMissing) await AssertCredentialsAsync(support.Case);
            return support;
        }
        catch { await support.DisposeAsync(); throw; }
    }

    private static async Task SeedSyntheticHistoricalReportAsync(PostgresCatalogTestSupport support, RevisionReportKey key, string state)
    {
        // Author metadata through the genuine0006 enabled constraints/transitions.
        // Current ordinary pools deliberately require0007, so none is borrowed here.
        await using var connection = await ProjectCreationTestSupport.OpenOwnerAsync(support.Case);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO weft.revision_derivations
              (organization_id,project_id,mapping_id,revision_id,operation_kind,native_object_id,native_class,
               native_digest,native_byte_count,logical_entry_document,dependency_manifest_id,backend_id,
               engine_revision,backend_build,adapter_build,schema_version,algorithm_version,codec_version,
               policy_schema_version,policy_json,policy_fingerprint,native_byte_limit,worker_response_byte_limit,
               item_limit,response_item_limit,json_depth_limit,detail_depth_limit,diagnostic_limit,
               validation_issue_limit,text_byte_limit,header_byte_limit,stored_object_byte_limit,response_byte_limit,key_fingerprint)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,
                    $22,$23,$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,$34) RETURNING cache_entry_id
            """, connection, transaction);
        PostgresRevisionDerivationTestSupport.AddScope(insert, key.Scope);
        object[] values = ["projection", key.Source.NativeObject.Id.ObjectId, "native", key.NativeDigest.Value,
            key.Source.NativeObject.ByteCount, key.Source.LogicalDocument, key.Source.DependencyIdentity,
            key.BackendId, key.BackendRevision, key.BackendBuild, key.AdapterBuild, key.SchemaVersion,
            key.AlgorithmVersion, key.CodecVersion, key.Policy.SchemaVersion,
            Encoding.UTF8.GetString(key.Policy.CopyCanonicalUtf8()), key.Policy.Fingerprint.Value,
            key.Policy.NativeByteLimit, key.Policy.WorkerResponseByteLimit, key.Policy.ItemLimit,
            key.Policy.ResponseItemLimit, key.Policy.JsonDepthLimit, key.Policy.DetailDepthLimit,
            key.Policy.DiagnosticLimit, key.Policy.ValidationIssueLimit, key.Policy.TextByteLimit,
            key.Policy.HeaderByteLimit, key.Policy.StoredObjectByteLimit, key.Policy.ResponseByteLimit, key.Fingerprint.Value];
        foreach (var value in values) insert.Parameters.AddWithValue(value);
        var cacheId = Assert.IsType<Guid>(await insert.ExecuteScalarAsync());
        await using var claim = new NpgsqlCommand("""
            INSERT INTO weft.revision_derivation_attempts
              (organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation,actor_principal_id,claimant_instance_id)
            VALUES ($1,$2,$3,$4,$5,1,$6,$7) RETURNING derivation_id
            """, connection, transaction);
        PostgresRevisionDerivationTestSupport.AddScope(claim, key.Scope);
        claim.Parameters.AddWithValue(cacheId);
        claim.Parameters.AddWithValue(support.Actor.PrincipalId.Value);
        claim.Parameters.AddWithValue(Guid.NewGuid());
        var derivation = Assert.IsType<Guid>(await claim.ExecuteScalarAsync());
        await using (var advance = new NpgsqlCommand("""
            UPDATE weft.revision_derivations SET current_generation=1,current_attempt_id=$2 WHERE cache_entry_id=$1
            """, connection, transaction))
        {
            advance.Parameters.AddWithValue(cacheId); advance.Parameters.AddWithValue(derivation);
            Assert.Equal(1, await advance.ExecuteNonQueryAsync());
        }
        if (state != "pending")
        {
            var completion = state == "failed" ? """
                UPDATE weft.revision_derivation_attempts SET completion_kind='failed',failure_code='backend_failure'
                WHERE derivation_id=$1
                """ : """
                UPDATE weft.revision_derivation_attempts a SET completion_kind='usable',observation_present=true,
                  backend_status='succeeded',provenance_confirmed=true,provenance_organization_id=k.organization_id,
                  provenance_project_id=k.project_id,provenance_mapping_id=k.mapping_id,provenance_revision_id=k.revision_id,
                  provenance_native_digest=k.native_digest,provenance_backend_id=k.backend_id,
                  provenance_engine_revision=k.engine_revision,provenance_backend_build=k.backend_build,
                  provenance_adapter_build=k.adapter_build,projection_coverage='complete',document_item_count=3,
                  document_diagnostic_count=0,coverage_gap_count=0,operation_diagnostic_count=0,
                  derived_digest=repeat('a',64),derived_byte_count=73
                FROM weft.revision_derivations k WHERE a.derivation_id=$1 AND k.cache_entry_id=a.cache_entry_id
                """;
            await using var finish = new NpgsqlCommand(completion, connection, transaction);
            finish.Parameters.AddWithValue(derivation);
            Assert.Equal(1, await finish.ExecuteNonQueryAsync());
            if (state == "usable")
            {
                await using var select = new NpgsqlCommand(
                    "UPDATE weft.revision_derivations SET selected_derivation_id=$2 WHERE cache_entry_id=$1", connection, transaction);
                select.Parameters.AddWithValue(cacheId); select.Parameters.AddWithValue(derivation);
                Assert.Equal(1, await select.ExecuteNonQueryAsync());
            }
        }
        await transaction.CommitAsync();
    }

    private static async Task AssertHistoryAsync(PostgresCase test)
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, await test.ScalarAsync<int[]>(
            "SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(PrefixChecksums.Append(MaintenanceSchemaContract.MigrationChecksum), await test.ScalarAsync<string[]>(
            "SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(PrefixChecksums, MigrationRunner.EmbeddedMigrations.Take(6).Select(script => script.Checksum));
    }

    private static Task<string> JournalAsync(PostgresCase test, int maximum = 7) => test.ScalarAsync<string>(
        "SELECT jsonb_agg(to_jsonb(j) ORDER BY version)::text FROM weft.schema_migrations j WHERE version<=$1", maximum);

    private static Task<string> FactsAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
          'ownership',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM (
            SELECT 'organizations' AS kind,to_jsonb(o) AS row FROM weft.organizations o UNION ALL
            SELECT 'principals',to_jsonb(o) FROM weft.principals o UNION ALL
            SELECT 'organization_memberships',to_jsonb(o) FROM weft.organization_memberships o UNION ALL
            SELECT 'projects',to_jsonb(o) FROM weft.projects o UNION ALL
            SELECT 'project_memberships',to_jsonb(o) FROM weft.project_memberships o) t),
          'catalog',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM (
            SELECT 'mappings' AS kind,to_jsonb(o) AS row FROM weft.mappings o UNION ALL
            SELECT 'mapping_revisions',to_jsonb(o) FROM weft.mapping_revisions o UNION ALL
            SELECT 'revision_ingestions',to_jsonb(o) FROM weft.revision_ingestions o UNION ALL
            SELECT 'revision_ingestion_attempts',to_jsonb(o) FROM weft.revision_ingestion_attempts o UNION ALL
            SELECT 'project_creations',to_jsonb(o) FROM weft.project_creations o UNION ALL
            SELECT 'capabilities',to_jsonb(o) FROM weft.organization_project_creation_capabilities o UNION ALL
            SELECT 'revision_derivations',to_jsonb(o) FROM weft.revision_derivations o UNION ALL
            SELECT 'revision_derivation_attempts',to_jsonb(o) FROM weft.revision_derivation_attempts o) t))::text
        """);

    private static Task<string> HistoricalGrantsAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
          'tables',(SELECT jsonb_agg(to_jsonb(g) ORDER BY grantee,table_name,privilege_type)
            FROM information_schema.role_table_grants g WHERE table_schema='weft'
              AND table_name NOT IN ('private_object_publications','private_object_retirements','private_object_cleanup_attempts')),
          'columns',(SELECT jsonb_agg(to_jsonb(g) ORDER BY grantee,table_name,column_name,privilege_type)
            FROM information_schema.role_column_grants g WHERE table_schema='weft'
              AND table_name NOT IN ('private_object_publications','private_object_retirements','private_object_cleanup_attempts')),
          'database',(SELECT jsonb_agg(jsonb_build_object('role',rolname,
            'connect',has_database_privilege(oid,current_database(),'CONNECT'),
            'create',has_database_privilege(oid,current_database(),'CREATE'),
            'temp',has_database_privilege(oid,current_database(),'TEMPORARY'),
            'grantOption',has_database_privilege(oid,current_database(),'CONNECT WITH GRANT OPTION,CREATE WITH GRANT OPTION,TEMPORARY WITH GRANT OPTION'))
            ORDER BY rolname) FROM pg_roles WHERE rolname IN
              ('weft_migrator','weft_runtime','weft_ingest','weft_catalog_reader','weft_catalog_editor',
               'weft_project_creator','weft_report_reader','weft_deriver','weft_maintenance')),
          'oldSchema',(SELECT jsonb_agg(jsonb_build_object('role',rolname,
            'usage',has_schema_privilege(oid,'weft','USAGE'),'create',has_schema_privilege(oid,'weft','CREATE'),
            'grantOption',has_schema_privilege(oid,'weft','USAGE WITH GRANT OPTION,CREATE WITH GRANT OPTION'))
            ORDER BY rolname) FROM pg_roles WHERE rolname IN
              ('weft_migrator','weft_runtime','weft_ingest','weft_catalog_reader','weft_catalog_editor',
               'weft_project_creator','weft_report_reader','weft_deriver')),
          'oldFunctions',(SELECT jsonb_agg(jsonb_build_object('role',r.rolname,'function',p.proname,
            'arguments',pg_get_function_identity_arguments(p.oid),'execute',has_function_privilege(r.oid,p.oid,'EXECUTE'),
            'grantOption',has_function_privilege(r.oid,p.oid,'EXECUTE WITH GRANT OPTION'))
            ORDER BY r.rolname,p.proname,pg_get_function_identity_arguments(p.oid))
            FROM pg_roles r CROSS JOIN pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='weft' AND r.rolname IN
              ('weft_migrator','weft_runtime','weft_ingest','weft_catalog_reader','weft_catalog_editor',
               'weft_project_creator','weft_report_reader','weft_deriver','weft_maintenance')
              AND p.proname IN ('guard_mappings_transition','guard_mapping_revisions_transition',
                'guard_revision_ingestions_transition','guard_revision_ingestion_attempts_transition','guard_project_creations_transition',
                'get_project_creation_admission_v1','create_project_v1','assert_project_creation_contract_v1','project_creation_fingerprint_v1',
                'guard_revision_derivations_transition','guard_revision_derivation_attempts_transition')),
          'roles',(SELECT jsonb_agg(jsonb_build_object('name',rolname,'login',rolcanlogin,'inherit',rolinherit,
            'super',rolsuper,'bypass',rolbypassrls,'createdb',rolcreatedb,'createrole',rolcreaterole,'replication',rolreplication,
            'limit',rolconnlimit,'until',rolvaliduntil,'config',rolconfig) ORDER BY rolname) FROM pg_roles r WHERE rolname IN
            ('weft_migrator','weft_runtime','weft_ingest','weft_catalog_reader','weft_catalog_editor',
             'weft_project_creator','weft_report_reader','weft_deriver','weft_maintenance')))::text
        """);

    private static Task<string> HistoricalDefinitionsAsync(PostgresCase test) => DefinitionsAsync(test, historical: true);
    private static Task<string> AllDefinitionsAsync(PostgresCase test) => DefinitionsAsync(test, historical: false);

    private static Task<string> DefinitionsAsync(PostgresCase test, bool historical) => test.ScalarAsync<string>("""
        WITH relations AS (SELECT c.* FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
          WHERE n.nspname='weft' AND c.relkind='r' AND (NOT $1 OR c.relname NOT IN
            ('private_object_publications','private_object_retirements','private_object_cleanup_attempts')))
        SELECT jsonb_build_object(
          'relations',(SELECT jsonb_agg(jsonb_build_object('name',r.relname,'owner',r.relowner,
            'rls',r.relrowsecurity,'force',r.relforcerowsecurity,'acl',r.relacl) ORDER BY r.relname) FROM relations r),
          'columns',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'attribute',to_jsonb(a),'default',pg_get_expr(d.adbin,d.adrelid))
            ORDER BY r.relname,a.attnum) FROM relations r JOIN pg_attribute a ON a.attrelid=r.oid AND a.attnum>0
            LEFT JOIN pg_attrdef d ON d.adrelid=r.oid AND d.adnum=a.attnum WHERE NOT a.attisdropped),
          'constraints',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'name',c.conname,
            'definition',pg_get_constraintdef(c.oid),'validated',c.convalidated,'noInherit',c.connoinherit)
            ORDER BY r.relname,c.conname) FROM relations r JOIN pg_constraint c ON c.conrelid=r.oid),
          'indexes',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'definition',pg_get_indexdef(i.indexrelid),
            'valid',i.indisvalid,'ready',i.indisready) ORDER BY r.relname,c.relname)
            FROM relations r JOIN pg_index i ON i.indrelid=r.oid JOIN pg_class c ON c.oid=i.indexrelid
            WHERE NOT $1 OR c.relname NOT IN ('revision_ingestions_cleanup_scan','revision_derivation_attempts_cleanup_scan')),
          'triggers',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'name',t.tgname,
            'definition',pg_get_triggerdef(t.oid),'enabled',t.tgenabled) ORDER BY r.relname,t.tgname)
            FROM relations r JOIN pg_trigger t ON t.tgrelid=r.oid WHERE NOT t.tgisinternal AND
              (NOT $1 OR t.tgname IN ('mappings_transition_guard','mapping_revisions_transition_guard',
                'revision_ingestions_transition_guard','revision_ingestion_attempts_transition_guard',
                'project_creations_transition_guard','revision_derivations_transition_guard','revision_derivation_attempts_transition_guard'))),
          'policies',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'name',p.polname,'command',p.polcmd,
            'roles',p.polroles,'permissive',p.polpermissive,'using',pg_get_expr(p.polqual,p.polrelid),
            'check',pg_get_expr(p.polwithcheck,p.polrelid)) ORDER BY r.relname,p.polname)
            FROM relations r JOIN pg_policy p ON p.polrelid=r.oid),
          'functions',(SELECT jsonb_agg(jsonb_build_object('name',p.proname,'definition',pg_get_functiondef(p.oid),'acl',p.proacl)
            ORDER BY p.proname,pg_get_function_identity_arguments(p.oid)) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='weft' AND (NOT $1 OR p.proname IN ('guard_mappings_transition','guard_mapping_revisions_transition',
              'guard_revision_ingestions_transition','guard_revision_ingestion_attempts_transition','guard_project_creations_transition',
              'get_project_creation_admission_v1','create_project_v1','assert_project_creation_contract_v1','project_creation_fingerprint_v1',
              'guard_revision_derivations_transition','guard_revision_derivation_attempts_transition'))))::text
        """, historical);

    private static async Task AssertCredentialsAsync(PostgresCase test)
    {
        foreach (var configured in new[] { test.MigrationConnectionString, test.RuntimeConnectionString,
            test.IngestionConnectionString, test.CatalogConnectionString, test.MetadataConnectionString,
            test.ProjectCreationConnectionString, test.ReportConnectionString, test.DerivationConnectionString,
            test.MaintenanceConnectionString })
        {
            // Fresh logins test original credentials without selecting/printing passwords or hashes.
            var options = new NpgsqlConnectionStringBuilder(configured) { Pooling = false };
            await using var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT current_user=$1 AND current_user=session_user", connection);
            command.Parameters.AddWithValue(options.Username!);
            Assert.Equal(true, await command.ExecuteScalarAsync());
        }
    }

    private static async Task AssertNoMaintenanceRowsAsync(PostgresCase test) => Assert.Equal(new long[] { 0, 0, 0 },
        await test.ScalarAsync<long[]>("SELECT ARRAY[(SELECT count(*) FROM weft.private_object_publications)," +
            "(SELECT count(*) FROM weft.private_object_retirements),(SELECT count(*) FROM weft.private_object_cleanup_attempts)]"));

    private static async Task AssertNo0007ObjectsOrRightsAsync(PostgresCase test)
    {
        Assert.Equal(0L, await test.ScalarAsync<long>("""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='weft' AND c.relname IN ('private_object_publications','private_object_retirements',
              'private_object_cleanup_attempts','revision_ingestions_cleanup_scan','revision_derivation_attempts_cleanup_scan')
            """));
        Assert.Equal(0L, await test.ScalarAsync<long>("""
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='weft'
              AND p.proname IN ('claim_private_object_cleanup_v1','finish_private_object_cleanup_v1','get_private_object_cleanup_attempt_v1')
            """));
        Assert.Equal(0L, await test.ScalarAsync<long>("""
            SELECT count(*) FROM pg_roles WHERE rolname='weft_maintenance' AND has_schema_privilege(oid,'weft','USAGE')
            """));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations WHERE version=7"));
    }
}
