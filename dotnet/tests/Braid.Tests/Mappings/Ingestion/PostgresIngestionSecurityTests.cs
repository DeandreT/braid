using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresIngestionSecurityTests(PostgresFixture fixture)
{
    private static readonly string[] IngestionTables =
        ["mappings", "revision_ingestions", "revision_ingestion_attempts", "mapping_revisions"];

    [PostgresFact]
    public async Task ReaderRejectsColumnOnlyMutationUnknownSelectionAndGrantOptionsOnTheNextLease()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        await AssertReaderAdmittedAsync(database, seed.OrganizationA);
        await test.ExecuteAsync("CREATE TABLE braid.reader_security_probe(id integer)");
        try
        {
            foreach (var (alter, restore) in new[]
            {
                ("GRANT INSERT(name) ON braid.projects TO braid_runtime",
                 "REVOKE INSERT(name) ON braid.projects FROM braid_runtime"),
                ("GRANT UPDATE(name) ON braid.projects TO braid_runtime",
                 "REVOKE UPDATE(name) ON braid.projects FROM braid_runtime"),
                ("GRANT REFERENCES(id) ON braid.projects TO braid_runtime",
                 "REVOKE REFERENCES(id) ON braid.projects FROM braid_runtime"),
                ("GRANT SELECT(phase) ON braid.revision_ingestions TO braid_runtime",
                 "REVOKE SELECT(phase) ON braid.revision_ingestions FROM braid_runtime"),
                ("GRANT SELECT(id) ON braid.reader_security_probe TO braid_runtime",
                 "REVOKE SELECT(id) ON braid.reader_security_probe FROM braid_runtime"),
                ("GRANT SELECT ON braid.projects TO braid_runtime WITH GRANT OPTION",
                 "REVOKE GRANT OPTION FOR SELECT ON braid.projects FROM braid_runtime"),
                ("GRANT SELECT(name) ON braid.projects TO braid_runtime WITH GRANT OPTION",
                 "REVOKE SELECT(name) ON braid.projects FROM braid_runtime"),
                ("GRANT USAGE ON SCHEMA braid TO braid_runtime WITH GRANT OPTION",
                 "REVOKE GRANT OPTION FOR USAGE ON SCHEMA braid FROM braid_runtime"),
                ($"GRANT CONNECT ON DATABASE {test.Name} TO braid_runtime WITH GRANT OPTION",
                 $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE {test.Name} FROM braid_runtime")
            })
            {
                await WithMutationAsync(test, alter, restore,
                    () => AssertReaderRefusedAsync(database, seed.OrganizationA));
                await AssertReaderAdmittedAsync(database, seed.OrganizationA);
            }
        }
        finally { await test.ExecuteAsync("DROP TABLE braid.reader_security_probe"); }
    }

    [PostgresFact]
    public async Task IngestionLoginHasWorkflowRightsButCannotMutateOwnershipImmutableColumnsOrHistory()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresIngestionDatabase(test.IngestionConnectionString);
        await AssertIngestionAdmittedAsync(database, seed.OrganizationA);
        await using var ingestion = NpgsqlDataSource.Create(test.IngestionConnectionString);
        await using (var identity = ingestion.CreateCommand("SELECT current_user || ':' || current_database()"))
            Assert.Equal($"braid_ingest:{test.Name}", await identity.ExecuteScalarAsync());
        Assert.Equal($"braid_migrator:{test.Name}",
            await test.ScalarAsync<string>("SELECT current_user || ':' || current_database()"));
        foreach (var table in IngestionTables)
        {
            await using var rights = ingestion.CreateCommand("SELECT has_table_privilege($1,'SELECT') AND has_table_privilege($1,'INSERT') AND NOT has_table_privilege($1,'UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')");
            rights.Parameters.AddWithValue($"braid.{table}");
            Assert.Equal(true, await rights.ExecuteScalarAsync());
        }
        foreach (var sql in new[]
        {
            "SET ROLE braid_migrator",
            "CREATE SCHEMA ingestion_security_probe",
            "CREATE TABLE braid.ingestion_security_probe(id integer)",
            "UPDATE braid.projects SET name='changed' WHERE false",
            "INSERT INTO braid.principals(id) VALUES (gen_random_uuid())",
            "UPDATE braid.mappings SET name='changed' WHERE false",
            "UPDATE braid.revision_ingestions SET backend_build='changed' WHERE false",
            "UPDATE braid.revision_ingestion_attempts SET claim_token=gen_random_uuid() WHERE false",
            "UPDATE braid.mapping_revisions SET native_digest=repeat('0',64) WHERE false",
            "DELETE FROM braid.mappings WHERE false",
            "DELETE FROM braid.revision_ingestions WHERE false",
            "DELETE FROM braid.revision_ingestion_attempts WHERE false",
            "DELETE FROM braid.mapping_revisions WHERE false",
            "TRUNCATE braid.mapping_revisions",
            "SELECT checksum FROM braid.schema_migrations",
            "UPDATE braid.schema_migrations SET checksum=repeat('0',64)",
            "DELETE FROM braid.schema_migrations",
            "CREATE TEMP TABLE ingestion_security_probe(id integer)"
        })
            await AssertDirectPrivilegeDeniedAsync(ingestion, seed.OrganizationA, sql);
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"));
        Assert.Equal((long)MigrationRunner.EmbeddedMigrations.Count, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
    }

    [PostgresFact]
    public async Task IngestionRejectsBroadImmutableReferenceJournalAndGrantOptionPrivileges()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresIngestionDatabase(test.IngestionConnectionString);
        await test.ExecuteAsync("CREATE TABLE braid.ingestion_grant_probe(id integer)");
        await test.ExecuteAsync("CREATE SEQUENCE braid.ingestion_grant_probe_sequence");
        await test.ExecuteAsync("CREATE FUNCTION braid.ingestion_grant_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
        await test.ExecuteAsync("REVOKE ALL ON FUNCTION braid.ingestion_grant_probe() FROM PUBLIC");
        try
        {
            await AssertIngestionAdmittedAsync(database, seed.OrganizationA);
            foreach (var (alter, restore) in new[]
            {
                ("GRANT UPDATE ON braid.mappings TO braid_ingest", """
                    REVOKE UPDATE ON braid.mappings FROM braid_ingest;
                    GRANT UPDATE(head_revision_id,head_sequence,updated_at) ON braid.mappings TO braid_ingest
                    """),
                ("GRANT UPDATE ON braid.revision_ingestions TO braid_ingest", """
                    REVOKE UPDATE ON braid.revision_ingestions FROM braid_ingest;
                    GRANT UPDATE(phase,lease_owner_id,lease_token,lease_generation,lease_expires_at,claim_count,
                        verified_stage_generation,verified_stage_at,eligible_attempt_generation,eligible_admission,
                        last_error,completed_at,committed_revision_id) ON braid.revision_ingestions TO braid_ingest
                    """),
                ("GRANT UPDATE(name) ON braid.mappings TO braid_ingest", "REVOKE UPDATE(name) ON braid.mappings FROM braid_ingest"),
                ("GRANT UPDATE(engine_revision) ON braid.revision_ingestions TO braid_ingest", "REVOKE UPDATE(engine_revision) ON braid.revision_ingestions FROM braid_ingest"),
                ("GRANT UPDATE(claim_token) ON braid.revision_ingestion_attempts TO braid_ingest", "REVOKE UPDATE(claim_token) ON braid.revision_ingestion_attempts FROM braid_ingest"),
                ("GRANT UPDATE(native_digest) ON braid.mapping_revisions TO braid_ingest", "REVOKE UPDATE(native_digest) ON braid.mapping_revisions FROM braid_ingest"),
                ("GRANT REFERENCES(id) ON braid.mappings TO braid_ingest", "REVOKE REFERENCES(id) ON braid.mappings FROM braid_ingest"),
                ("GRANT DELETE ON braid.revision_ingestions TO braid_ingest", "REVOKE DELETE ON braid.revision_ingestions FROM braid_ingest"),
                ("GRANT TRUNCATE,TRIGGER ON braid.mapping_revisions TO braid_ingest", "REVOKE TRUNCATE,TRIGGER ON braid.mapping_revisions FROM braid_ingest"),
                ("GRANT SELECT ON braid.schema_migrations TO braid_ingest", "REVOKE SELECT ON braid.schema_migrations FROM braid_ingest"),
                ("GRANT SELECT(id) ON braid.ingestion_grant_probe TO braid_ingest", "REVOKE SELECT(id) ON braid.ingestion_grant_probe FROM braid_ingest"),
                ("GRANT USAGE ON SEQUENCE braid.ingestion_grant_probe_sequence TO braid_ingest", "REVOKE USAGE ON SEQUENCE braid.ingestion_grant_probe_sequence FROM braid_ingest"),
                ("GRANT SELECT ON SEQUENCE braid.ingestion_grant_probe_sequence TO PUBLIC", "REVOKE SELECT ON SEQUENCE braid.ingestion_grant_probe_sequence FROM PUBLIC"),
                ("GRANT EXECUTE ON FUNCTION braid.ingestion_grant_probe() TO braid_ingest", "REVOKE EXECUTE ON FUNCTION braid.ingestion_grant_probe() FROM braid_ingest"),
                ("GRANT SELECT ON braid.mappings TO braid_ingest WITH GRANT OPTION", "REVOKE GRANT OPTION FOR SELECT ON braid.mappings FROM braid_ingest; GRANT SELECT ON braid.mappings TO braid_ingest"),
                ("GRANT UPDATE(head_sequence) ON braid.mappings TO braid_ingest WITH GRANT OPTION", "REVOKE GRANT OPTION FOR UPDATE(head_sequence) ON braid.mappings FROM braid_ingest; GRANT UPDATE(head_sequence) ON braid.mappings TO braid_ingest"),
                ("REVOKE INSERT ON braid.mapping_revisions FROM braid_ingest", "GRANT INSERT ON braid.mapping_revisions TO braid_ingest"),
                ("REVOKE UPDATE(verified_stage_at) ON braid.revision_ingestion_attempts FROM braid_ingest", "GRANT UPDATE(verified_stage_at) ON braid.revision_ingestion_attempts TO braid_ingest")
            })
            {
                await WithMutationAsync(test, alter, restore,
                    () => AssertIngestionRefusedAsync(database, seed.OrganizationA));
                try { await AssertIngestionAdmittedAsync(database, seed.OrganizationA); }
                catch (Exception failure)
                {
                    throw new InvalidOperationException($"The original ingestion grants were not restored after: {alter}", failure);
                }
            }
        }
        finally
        {
            await test.ExecuteAsync("DROP FUNCTION braid.ingestion_grant_probe()");
            await test.ExecuteAsync("DROP SEQUENCE braid.ingestion_grant_probe_sequence");
            await test.ExecuteAsync("DROP TABLE braid.ingestion_grant_probe");
        }
    }

    [PostgresFact]
    public async Task PublicInheritedAndDefaultGrantsCannotWidenEitherRestrictedRole()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var ingestion = new PostgresIngestionDatabase(test.IngestionConnectionString);
        await using var reader = new PostgresDatabase(test.RuntimeConnectionString);
        foreach (var (alter, restore) in new[]
        {
            ("GRANT UPDATE(name) ON braid.mappings TO PUBLIC", "REVOKE UPDATE(name) ON braid.mappings FROM PUBLIC"),
            ("GRANT SELECT(phase) ON braid.revision_ingestions TO PUBLIC", "REVOKE SELECT(phase) ON braid.revision_ingestions FROM PUBLIC"),
            ("ALTER DEFAULT PRIVILEGES IN SCHEMA braid GRANT UPDATE ON TABLES TO braid_ingest", "ALTER DEFAULT PRIVILEGES IN SCHEMA braid REVOKE UPDATE ON TABLES FROM braid_ingest")
        })
        {
            await WithMutationAsync(test, alter, restore,
                async () =>
                {
                    await AssertIngestionRefusedAsync(ingestion, seed.OrganizationA);
                    if (alter.Contains("TO PUBLIC", StringComparison.Ordinal))
                        await AssertReaderRefusedAsync(reader, seed.OrganizationA);
                });
            await AssertIngestionAdmittedAsync(ingestion, seed.OrganizationA);
            await AssertReaderAdmittedAsync(reader, seed.OrganizationA);
        }
        var role = $"ingestion_attack_{test.Name}";
        await test.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS");
        try
        {
            await test.ExecuteAsync($"GRANT UPDATE(name) ON braid.mappings TO {role}");
            foreach (var login in new[] { "braid_ingest", "braid_runtime" })
            {
                await test.BootstrapAsync($"GRANT {role} TO {login}");
                try
                {
                    if (login == "braid_ingest")
                        await AssertIngestionRefusedAsync(ingestion, seed.OrganizationA);
                    else
                        await AssertReaderRefusedAsync(reader, seed.OrganizationA);
                }
                finally { await test.BootstrapAsync($"REVOKE {role} FROM {login}"); }
            }
        }
        finally
        {
            await test.ExecuteAsync($"REVOKE UPDATE(name) ON braid.mappings FROM {role}");
            await test.BootstrapAsync($"DROP ROLE {role}");
        }
        await AssertIngestionAdmittedAsync(ingestion, seed.OrganizationA);
        await AssertReaderAdmittedAsync(reader, seed.OrganizationA);
    }

    [PostgresFact]
    public async Task PrivilegedRolesCreationRightsAndSwappedPoolsAreRefused()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using (var owner = new PostgresIngestionDatabase(test.MigrationConnectionString))
            await AssertIngestionRefusedAsync(owner, seed.OrganizationA);
        await using (var readerAsWriter = new PostgresIngestionDatabase(test.RuntimeConnectionString))
            await AssertIngestionRefusedAsync(readerAsWriter, seed.OrganizationA);
        await using (var writerAsReader = new PostgresDatabase(test.IngestionConnectionString))
            await AssertReaderRefusedAsync(writerAsReader, seed.OrganizationA);
        await using var database = new PostgresIngestionDatabase(test.IngestionConnectionString);
        foreach (var (alter, restore) in new[]
        {
            ("ALTER ROLE braid_ingest SUPERUSER", "ALTER ROLE braid_ingest NOSUPERUSER"),
            ("ALTER ROLE braid_ingest BYPASSRLS", "ALTER ROLE braid_ingest NOBYPASSRLS"),
            ("ALTER ROLE braid_ingest CREATEROLE", "ALTER ROLE braid_ingest NOCREATEROLE"),
            ("ALTER ROLE braid_ingest CREATEDB", "ALTER ROLE braid_ingest NOCREATEDB"),
            ("ALTER ROLE braid_ingest REPLICATION", "ALTER ROLE braid_ingest NOREPLICATION"),
            ("GRANT braid_migrator TO braid_ingest", "REVOKE braid_migrator FROM braid_ingest"),
            ("GRANT pg_read_all_data TO braid_ingest", "REVOKE pg_read_all_data FROM braid_ingest"),
            ("GRANT pg_write_all_data TO braid_ingest", "REVOKE pg_write_all_data FROM braid_ingest"),
            ("GRANT pg_read_server_files TO braid_ingest", "REVOKE pg_read_server_files FROM braid_ingest"),
            ("GRANT pg_write_server_files TO braid_ingest", "REVOKE pg_write_server_files FROM braid_ingest"),
            ("GRANT pg_execute_server_program TO braid_ingest", "REVOKE pg_execute_server_program FROM braid_ingest"),
            ("GRANT pg_signal_backend TO braid_ingest", "REVOKE pg_signal_backend FROM braid_ingest"),
            ($"GRANT CREATE ON DATABASE {test.Name} TO braid_ingest", $"REVOKE CREATE ON DATABASE {test.Name} FROM braid_ingest"),
            ($"GRANT TEMPORARY ON DATABASE {test.Name} TO braid_ingest", $"REVOKE TEMPORARY ON DATABASE {test.Name} FROM braid_ingest"),
            ("GRANT CREATE ON SCHEMA braid TO braid_ingest", "REVOKE CREATE ON SCHEMA braid FROM braid_ingest"),
            ($"GRANT CONNECT ON DATABASE {test.Name} TO braid_ingest WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE {test.Name} FROM braid_ingest"),
            ("GRANT USAGE ON SCHEMA braid TO braid_ingest WITH GRANT OPTION", "REVOKE GRANT OPTION FOR USAGE ON SCHEMA braid FROM braid_ingest")
        })
        {
            await test.BootstrapAsync(alter);
            try { await AssertIngestionRefusedAsync(database, seed.OrganizationA); }
            finally { await test.BootstrapAsync(restore); }
            await AssertIngestionAdmittedAsync(database, seed.OrganizationA);
        }
    }

    [PostgresFact]
    public async Task UnreachableAlternateSchemaOrWorkflowTableOwnerRejectsAdmissionWithoutReturningFacts()
    {
        await using var support = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var facts = await support.ReserveAsync();
        var role = $"ingestion_owner_{support.Case.Name}";
        await support.Case.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS");
        try
        {
            Assert.False(await support.Case.ScalarAsync<bool>("SELECT pg_has_role('braid_ingest',$1,'MEMBER')", role));
            foreach (var target in new[] { "SCHEMA braid" }.Concat(IngestionTables.Select(table => $"TABLE braid.{table}")))
            {
                await support.Case.BootstrapAsync($"ALTER {target} OWNER TO {role}");
                try
                {
                    await using var fresh = new PostgresIngestionDatabase(support.Case.IngestionConnectionString);
                    await AssertIngestionRefusedAsync(fresh, support.Project.OrganizationId);
                    var store = new PostgresRevisionIngestionStore(fresh);
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        store.GetReceiptAsync(support.Actor, support.Project, facts.OperationId));
                }
                finally { await support.Case.BootstrapAsync($"ALTER {target} OWNER TO braid_migrator"); }
                await AssertIngestionAdmittedAsync(support.Database, support.Project.OrganizationId);
                var receipt = Assert.IsType<IngestionReceipt>(
                    await support.Store.GetReceiptAsync(support.Actor, support.Project, facts.OperationId));
                Assert.Equal(facts, receipt.Facts);
                Assert.Equal(IngestionPhase.Reserved, receipt.Phase);
            }
        }
        finally { await support.Case.BootstrapAsync($"DROP ROLE {role}"); }
    }

    [PostgresFact]
    public async Task NewTablesHideUnboundAndForeignOrganizationRowsAndRejectCrossOrganizationInsertion()
    {
        await using var support = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await support.CommitCreateAsync();
        var otherProject = new ProjectScope(support.Seed.OrganizationB, support.Seed.ProjectB);
        var reserved = await support.Store.ReserveAsync(support.Actor, otherProject, Guid.NewGuid(),
            PostgresIngestionTestSupport.CreateIntent(), PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, reserved.Outcome);
        var otherFacts = reserved.Receipt!.Facts;
        var claimed = await support.Store.ClaimAsync(support.Actor, otherProject, otherFacts.OperationId, Guid.NewGuid());
        var claim = Assert.IsType<IngestionClaim>(claimed.Claim);
        Assert.Equal(IngestionStoreOutcome.Applied, (await support.Store.MarkStagedAsync(support.Actor, claim, otherFacts.NativeObject)).Outcome);
        Assert.Equal(IngestionPhase.Ready, (await support.Store.CompleteAttemptAsync(support.Actor, claim,
            PostgresIngestionTestSupport.SyntheticCompletion(otherFacts))).Receipt!.Phase);
        Assert.Equal(IngestionPhase.Committed, (await support.Store.FinalizeAsync(support.Actor, claim)).Receipt!.Phase);
        await using var direct = NpgsqlDataSource.Create(support.Case.IngestionConnectionString);
        await using (var connection = await direct.OpenConnectionAsync())
        {
            foreach (var table in IngestionTables)
            {
                await using var unbound = new NpgsqlCommand($"SELECT count(*) FROM braid.{table}", connection);
                Assert.Equal(0L, await unbound.ExecuteScalarAsync());
            }
        }
        await using (var scope = await support.Database.BeginTransactionAsync(support.Seed.OrganizationA))
        {
            foreach (var table in IngestionTables)
            {
                await using var visible = scope.CreateCommand($"SELECT count(*) FROM braid.{table}");
                Assert.Equal(1L, await visible.ExecuteScalarAsync());
                await using var foreign = scope.CreateCommand($"SELECT count(*) FROM braid.{table} WHERE organization_id=$1");
                foreign.Parameters.AddWithValue(support.Seed.OrganizationB);
                Assert.Equal(0L, await foreign.ExecuteScalarAsync());
            }
            await using var foreignUpdate = scope.CreateCommand("UPDATE braid.revision_ingestions SET last_error='needs_source' WHERE operation_id=$1");
            foreignUpdate.Parameters.AddWithValue(otherFacts.OperationId);
            Assert.Equal(0, await foreignUpdate.ExecuteNonQueryAsync());
        }
        await using (var connection = await direct.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var bind = new NpgsqlCommand("SELECT set_config('braid.organization_id',$1,true)", connection, transaction))
            {
                bind.Parameters.AddWithValue(support.Seed.OrganizationA.ToString("D"));
                await bind.ExecuteScalarAsync();
            }
            await using var crossOrganization = new NpgsqlCommand("""
                INSERT INTO braid.revision_ingestion_attempts
                    (organization_id,project_id,operation_id,generation,claimant_instance_id,claim_token)
                VALUES ($1,$2,$3,2,gen_random_uuid(),gen_random_uuid())
                """, connection, transaction);
            crossOrganization.Parameters.AddWithValue(support.Seed.OrganizationB);
            crossOrganization.Parameters.AddWithValue(support.Seed.ProjectB);
            crossOrganization.Parameters.AddWithValue(otherFacts.OperationId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => crossOrganization.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        Assert.Equal(2L, await support.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestions"));
        Assert.Equal(2L, await support.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal(DBNull.Value, await support.Case.ScalarAsync<object>("SELECT last_error FROM braid.revision_ingestions WHERE operation_id=$1", otherFacts.OperationId));
    }

    [PostgresFact]
    public async Task EveryIngestionPolicyRejectsDisabledMissingWeakenedAdditionalOrChangedRoleGuards()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresIngestionDatabase(test.IngestionConnectionString);
        const string expression = "organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid";
        foreach (var table in IngestionTables)
        {
            var policy = $"{table}_organization_scope";
            var restorePolicy = $"CREATE POLICY {policy} ON braid.{table} USING ({expression}) WITH CHECK ({expression})";
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE braid.{table} DISABLE ROW LEVEL SECURITY", $"ALTER TABLE braid.{table} ENABLE ROW LEVEL SECURITY"),
                ($"DROP POLICY {policy} ON braid.{table}", restorePolicy),
                ($"ALTER POLICY {policy} ON braid.{table} USING (true)", $"ALTER POLICY {policy} ON braid.{table} USING ({expression})"),
                ($"ALTER POLICY {policy} ON braid.{table} WITH CHECK (true)", $"ALTER POLICY {policy} ON braid.{table} WITH CHECK ({expression})"),
                ($"CREATE POLICY ingestion_extra_probe ON braid.{table} USING (true)", $"DROP POLICY ingestion_extra_probe ON braid.{table}"),
                ($"ALTER POLICY {policy} ON braid.{table} TO braid_ingest", $"ALTER POLICY {policy} ON braid.{table} TO PUBLIC"),
                ($"DROP POLICY {policy} ON braid.{table}; CREATE POLICY {policy} ON braid.{table} FOR SELECT USING ({expression})",
                 $"DROP POLICY {policy} ON braid.{table}; {restorePolicy}"),
                ($"DROP POLICY {policy} ON braid.{table}; CREATE POLICY {policy} ON braid.{table} AS RESTRICTIVE USING ({expression}) WITH CHECK ({expression})",
                 $"DROP POLICY {policy} ON braid.{table}; {restorePolicy}")
            })
            {
                await WithMutationAsync(test, alter, restore,
                    () => AssertIngestionRefusedAsync(database, seed.OrganizationA));
                await AssertIngestionAdmittedAsync(database, seed.OrganizationA);
            }
        }
    }

    [PostgresFact]
    public async Task EveryTransitionTriggerRejectsDisabledMissingReplacedAndAdditionalDefinitions()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresIngestionDatabase(test.IngestionConnectionString);
        foreach (var table in IngestionTables)
        {
            var trigger = $"{table}_transition_guard";
            var function = $"braid.guard_{table}_transition()";
            var triggerDefinition = await test.ScalarAsync<string>("SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid=$1::regclass AND tgname=$2", $"braid.{table}", trigger);
            var functionDefinition = await test.ScalarAsync<string>("SELECT pg_get_functiondef($1::regprocedure)", function);
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE braid.{table} DISABLE TRIGGER {trigger}", $"ALTER TABLE braid.{table} ENABLE TRIGGER {trigger}"),
                ($"DROP TRIGGER {trigger} ON braid.{table}", triggerDefinition),
                ($"CREATE OR REPLACE FUNCTION {function} RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,braid AS $$ BEGIN RETURN NEW; END; $$", functionDefinition),
                ($"ALTER FUNCTION {function} SECURITY DEFINER", $"ALTER FUNCTION {function} SECURITY INVOKER"),
                ($"ALTER FUNCTION {function} SET search_path=public,braid", $"ALTER FUNCTION {function} SET search_path=pg_catalog,braid"),
                ($"GRANT EXECUTE ON FUNCTION {function} TO braid_ingest", $"REVOKE EXECUTE ON FUNCTION {function} FROM braid_ingest"),
                ($"GRANT EXECUTE ON FUNCTION {function} TO PUBLIC", $"REVOKE EXECUTE ON FUNCTION {function} FROM PUBLIC"),
                ($"CREATE TRIGGER ingestion_extra_probe BEFORE UPDATE ON braid.{table} FOR EACH ROW EXECUTE FUNCTION {function}", $"DROP TRIGGER ingestion_extra_probe ON braid.{table}")
            })
            {
                await WithMutationAsync(test, alter, restore,
                    () => AssertIngestionRefusedAsync(database, seed.OrganizationA));
                await AssertIngestionAdmittedAsync(database, seed.OrganizationA);
            }
        }
        await WithMutationAsync(test,
            "CREATE RULE ingestion_rule_probe AS ON UPDATE TO braid.mappings DO ALSO SELECT 1",
            "DROP RULE ingestion_rule_probe ON braid.mappings",
            () => AssertIngestionRefusedAsync(database, seed.OrganizationA));
        await AssertIngestionAdmittedAsync(database, seed.OrganizationA);
    }

    [PostgresFact]
    public async Task OwnerCannotRewriteReservedIntentIdentityProfileOrNativeFacts()
    {
        await using var support = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var facts = await support.ReserveAsync();
        var before = await RowJsonAsync(support.Case, "revision_ingestions", "operation_id=$1", facts.OperationId);
        foreach (var assignment in new[]
        {
            "organization_id=gen_random_uuid()", "project_id=gen_random_uuid()", "operation_id=gen_random_uuid()",
            $"actor_principal_id='{support.Seed.Admitted:D}'::uuid", "operation_kind='append'",
            "idempotency_key=gen_random_uuid()", "fingerprint_version=2", "intent_fingerprint=repeat('0',64)",
            "target_mapping_id=gen_random_uuid()", "future_mapping_id=gen_random_uuid()", "future_revision_id=gen_random_uuid()",
            "native_object_id=gen_random_uuid()", "native_class='derived'", "native_digest=repeat('0',64)", "native_byte_count=native_byte_count+1",
            "logical_entry_document='changed.json'", "dependency_manifest_id='changed'", "backend_id='changed'",
            "engine_revision='changed'", "backend_build='changed'", "adapter_build='changed'", "admission_policy_id='changed'",
            "effective_native_byte_limit=effective_native_byte_limit-1", "display_filename='changed.json'", "create_name='changed'",
            "create_description='changed'", "head_condition='equal'", "expected_head_revision_id=gen_random_uuid()",
            "created_at=created_at+interval '1 second'"
        })
            await AssertOwnerCheckDeniedAsync(support.Case,
                $"UPDATE braid.revision_ingestions SET {assignment} WHERE operation_id=$1", facts.OperationId);
        await AssertOwnerCheckDeniedAsync(support.Case,
            "DELETE FROM braid.revision_ingestions WHERE operation_id=$1", facts.OperationId);
        Assert.Equal(before, await RowJsonAsync(support.Case, "revision_ingestions", "operation_id=$1", facts.OperationId));
        var replay = await support.Store.ReserveAsync(support.Actor, support.Project, facts.IdempotencyKey,
            facts.Intent, PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Existing, replay.Outcome);
        Assert.Equal(facts, replay.Receipt!.Facts);
    }

    [PostgresFact]
    public async Task ClaimFencesAndOriginalEligibleEvidenceCannotBeReplacedExtendedOrClearedByOwner()
    {
        await using var support = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var facts = await support.ReserveAsync();
        var claim = await support.ClaimAsync(facts);
        var before = await RowJsonAsync(support.Case, "revision_ingestions", "operation_id=$1", facts.OperationId);
        foreach (var assignment in new[]
        {
            "lease_token=gen_random_uuid()", "lease_owner_id=gen_random_uuid()",
            "lease_expires_at=lease_expires_at+interval '1 second'",
            "lease_generation=lease_generation+2,claim_count=claim_count+2",
            "lease_generation=0,claim_count=0,lease_owner_id=NULL,lease_token=NULL,lease_expires_at=NULL"
        })
            await AssertOwnerCheckDeniedAsync(support.Case,
                $"UPDATE braid.revision_ingestions SET {assignment} WHERE operation_id=$1", facts.OperationId);
        Assert.Equal(before, await RowJsonAsync(support.Case, "revision_ingestions", "operation_id=$1", facts.OperationId));
        await support.ReadyAsync(facts, claim);
        before = await RowJsonAsync(support.Case, "revision_ingestions", "operation_id=$1", facts.OperationId);
        foreach (var assignment in new[]
        {
            "eligible_attempt_generation=NULL,eligible_admission=NULL",
            "eligible_attempt_generation=eligible_attempt_generation+1",
            "eligible_admission='eligible_decoded_invalid'",
            "verified_stage_generation=NULL,verified_stage_at=NULL",
            "phase='reserved'"
        })
            await AssertOwnerCheckDeniedAsync(support.Case,
                $"UPDATE braid.revision_ingestions SET {assignment} WHERE operation_id=$1", facts.OperationId);
        Assert.Equal(before, await RowJsonAsync(support.Case, "revision_ingestions", "operation_id=$1", facts.OperationId));
        Assert.Equal(IngestionPhase.Committed, (await support.Store.FinalizeAsync(support.Actor, claim)).Receipt!.Phase);
    }

    [PostgresFact]
    public async Task CompletedAttemptClaimProvenanceAndEvidenceAreImmutableEvenForTheOwner()
    {
        await using var support = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var facts = await support.ReserveAsync();
        var claim = await support.ClaimAsync(facts);
        await support.ReadyAsync(facts, claim);
        var before = await RowJsonAsync(support.Case, "revision_ingestion_attempts", "operation_id=$1 AND generation=$2", facts.OperationId, claim.Generation);
        foreach (var assignment in new[]
        {
            "organization_id=gen_random_uuid()", "project_id=gen_random_uuid()", "operation_id=gen_random_uuid()", "generation=generation+1",
            "claimant_instance_id=gen_random_uuid()", "claim_token=gen_random_uuid()", "started_at=started_at+interval '1 second'",
            "verified_stage_at=verified_stage_at+interval '1 second'", "finished_at=NULL", "completion_kind='operational_failure'",
            "backend_status='invalid_mapping'", "admission_classification='eligible_decoded_invalid'", "provenance_confirmed=false",
            "provenance_organization_id=gen_random_uuid()", "provenance_project_id=gen_random_uuid()",
            "provenance_mapping_id=gen_random_uuid()", "provenance_revision_id=gen_random_uuid()", "provenance_native_digest=repeat('0',64)",
            "provenance_backend_id='changed'", "provenance_engine_revision='changed'", "provenance_backend_build='changed'",
            "provenance_adapter_build='changed'", "is_valid=false", "total_issue_count=1", "emitted_issue_count=1", "issues_truncated=true",
            "evidence_version='changed'", "evidence=jsonb_set(evidence,'{confirmed}','false'::jsonb)",
            "reused_attempt_generation=2,reused_admission='eligible_valid'", "last_error='backend_failure'"
        })
            await AssertOwnerCheckDeniedAsync(support.Case,
                $"UPDATE braid.revision_ingestion_attempts SET {assignment} WHERE operation_id=$1 AND generation=$2",
                facts.OperationId, claim.Generation);
        await AssertOwnerCheckDeniedAsync(support.Case,
            "DELETE FROM braid.revision_ingestion_attempts WHERE operation_id=$1 AND generation=$2", facts.OperationId, claim.Generation);
        Assert.Equal(before, await RowJsonAsync(support.Case, "revision_ingestion_attempts", "operation_id=$1 AND generation=$2", facts.OperationId, claim.Generation));
        Assert.Equal(IngestionPhase.Committed, (await support.Store.FinalizeAsync(support.Actor, claim)).Receipt!.Phase);
    }

    [PostgresFact]
    public async Task TerminalReceiptsMappingHeadersAndCommittedRevisionsCannotBeRewrittenOrDeleted()
    {
        await using var support = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await support.CommitCreateAsync();
        var facts = receipt.Facts;
        var operationBefore = await RowJsonAsync(support.Case, "revision_ingestions", "operation_id=$1", facts.OperationId);
        var mappingBefore = await RowJsonAsync(support.Case, "mappings", "id=$1", facts.FutureRevision.MappingId);
        var revisionBefore = await RowJsonAsync(support.Case, "mapping_revisions", "id=$1", facts.FutureRevision.RevisionId);
        foreach (var assignment in new[]
        {
            "phase='ready',completed_at=NULL,committed_revision_id=NULL", "completed_at=completed_at+interval '1 second'",
            "committed_revision_id=gen_random_uuid()", "lease_generation=lease_generation+1,claim_count=claim_count+1",
            "lease_owner_id=gen_random_uuid(),lease_token=gen_random_uuid(),lease_expires_at=clock_timestamp()+interval '120 seconds'"
        })
            await AssertOwnerCheckDeniedAsync(support.Case,
                $"UPDATE braid.revision_ingestions SET {assignment} WHERE operation_id=$1", facts.OperationId);
        foreach (var assignment in new[]
        {
            "name='changed'", "description='changed'", "backend_id='changed'", $"creator_principal_id='{support.Seed.Admitted:D}'::uuid",
            $"owner_principal_id='{support.Seed.Admitted:D}'::uuid", "version=version+1", "created_at=created_at+interval '1 second'",
            "head_revision_id=gen_random_uuid()", "head_revision_id=gen_random_uuid(),head_sequence=head_sequence+2",
            "head_revision_id=gen_random_uuid(),head_sequence=head_sequence+1,updated_at=updated_at-interval '1 second'"
        })
            await AssertOwnerCheckDeniedAsync(support.Case,
                $"UPDATE braid.mappings SET {assignment} WHERE id=$1", facts.FutureRevision.MappingId);
        foreach (var assignment in new[]
        {
            "id=gen_random_uuid()", "sequence=sequence+1", "native_object_id=gen_random_uuid()", "native_digest=repeat('0',64)",
            "native_byte_count=native_byte_count+1", "backend_id='changed'", "initial_admission='eligible_decoded_invalid'",
            "initial_attempt_generation=initial_attempt_generation+1", "committed_at=committed_at+interval '1 second'"
        })
            await AssertOwnerCheckDeniedAsync(support.Case,
                $"UPDATE braid.mapping_revisions SET {assignment} WHERE id=$1", facts.FutureRevision.RevisionId);
        foreach (var (table, column, id) in new[]
        {
            ("revision_ingestions", "operation_id", facts.OperationId),
            ("mappings", "id", facts.FutureRevision.MappingId), ("mapping_revisions", "id", facts.FutureRevision.RevisionId)
        })
            await AssertOwnerCheckDeniedAsync(support.Case, $"DELETE FROM braid.{table} WHERE {column}=$1", id);
        Assert.Equal(operationBefore, await RowJsonAsync(support.Case, "revision_ingestions", "operation_id=$1", facts.OperationId));
        Assert.Equal(mappingBefore, await RowJsonAsync(support.Case, "mappings", "id=$1", facts.FutureRevision.MappingId));
        Assert.Equal(revisionBefore, await RowJsonAsync(support.Case, "mapping_revisions", "id=$1", facts.FutureRevision.RevisionId));
        var replay = Assert.IsType<IngestionReceipt>(await support.Store.GetReceiptAsync(support.Actor, support.Project, facts.OperationId));
        Assert.Equal(IngestionPhase.Committed, replay.Phase);
        Assert.Equal(receipt.Facts, replay.Facts);
        Assert.Equal(receipt.CommittedRevision, replay.CommittedRevision);
    }

    [PostgresFact]
    public async Task CompositeMappingMembershipAndAttemptForeignKeysRejectForeignOrganizationProjectAndActor()
    {
        await using var support = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await support.CommitCreateAsync();
        foreach (var changes in new Dictionary<string, string>[]
        {
            new() { ["organization_id"] = Uuid(support.Seed.OrganizationB) },
            new() { ["project_id"] = Uuid(support.Seed.ProjectB) },
            new() { ["project_id"] = Uuid(support.Seed.HiddenProjectA) },
            new() { ["creator_principal_id"] = Uuid(support.Seed.Bob) },
            new() { ["owner_principal_id"] = Uuid(support.Seed.Bob) }
        })
        {
            changes["id"] = "gen_random_uuid()";
            var sql = await CopyInsertSqlAsync(support.Case, "mappings", "id=$1", changes);
            await AssertOwnerForeignKeyDeniedAsync(support.Case, sql, receipt.Facts.FutureRevision.MappingId);
        }
        var facts = await support.ReserveAsync();
        await support.ClaimAsync(facts);
        foreach (var changes in new Dictionary<string, string>[]
        {
            new() { ["organization_id"] = Uuid(support.Seed.OrganizationB) },
            new() { ["project_id"] = Uuid(support.Seed.HiddenProjectA) },
            new() { ["operation_id"] = "gen_random_uuid()" }
        })
        {
            var sql = await CopyInsertSqlAsync(support.Case, "revision_ingestion_attempts", "operation_id=$1", changes);
            await AssertOwnerForeignKeyDeniedAsync(support.Case, sql, facts.OperationId);
        }
        Assert.Equal(1L, await support.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mappings"));
        Assert.Equal(2L, await support.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestion_attempts"));
    }

    [PostgresFact]
    public async Task RevisionInsertionRequiresExactReservedNativeFactsAndOriginalEligibleAttemptAncestry()
    {
        await using var support = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var first = await support.CommitCreateAsync();
        var other = await support.CommitCreateAsync();
        var facts = await support.ReserveAsync(intent: PostgresIngestionTestSupport.AppendIntent(first));
        var claim = await support.ClaimAsync(facts);
        await support.ReadyAsync(facts, claim);
        // This pending append provides a real, unique future revision and completed synthetic
        // eligible attempt. Each attack changes one FK fact, while every other fact is valid.
        foreach (var (column, value) in new[]
        {
            ("organization_id", Uuid(support.Seed.OrganizationB)),
            ("project_id", Uuid(support.Seed.HiddenProjectA)),
            ("mapping_id", Uuid(other.Facts.FutureRevision.MappingId)),
            ("id", "gen_random_uuid()"), ("native_object_id", Uuid(other.Facts.NativeObject.Id.ObjectId)),
            ("native_digest", "repeat('0',64)"), ("native_byte_count", "native_byte_count+1"),
            ("backend_id", "'other'"), ("creator_principal_id", Uuid(support.Seed.Admitted)),
            ("ingestion_operation_id", Uuid(other.Facts.OperationId)),
            ("initial_attempt_generation", "2"), ("initial_admission", "'eligible_decoded_invalid'")
        })
        {
            var sql = PendingRevisionInsertSql(new Dictionary<string, string> { [column] = value });
            await AssertOwnerForeignKeyDeniedAsync(support.Case, sql, facts.OperationId);
        }
        foreach (var (column, value) in new[]
        {
            ("native_class", "'derived'"), ("native_byte_count", "0"), ("native_byte_count", "4194305"),
            ("native_digest", "repeat('A',64)"), ("logical_entry_document", "'foreign.json'"),
            ("dependency_manifest_id", "'foreign'"), ("initial_admission", "'retryable_failure'")
        })
            await AssertOwnerCheckDeniedAsync(support.Case,
                PendingRevisionInsertSql(new Dictionary<string, string> { [column] = value }), facts.OperationId);
        // Roll back a positive control to prove the candidate shape itself is admissible.
        await using (var connection = await support.Case.Owner.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand(PendingRevisionInsertSql(new Dictionary<string, string>()), connection, transaction);
            insert.Parameters.AddWithValue(facts.OperationId);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            await transaction.RollbackAsync();
        }
        var committed = Assert.IsType<IngestionReceipt>((await support.Store.FinalizeAsync(support.Actor, claim)).Receipt);
        Assert.Equal(IngestionPhase.Committed, committed.Phase);
        Assert.Equal(2L, committed.CommittedRevision!.Sequence);
        Assert.Equal(3L, await support.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
    }

    [PostgresFact]
    public async Task DeferredHeadForeignKeyRejectsUnclosedCyclesAndForeignRevisionPointersAtomically()
    {
        await using var support = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var first = await support.CommitCreateAsync();
        var other = await support.CommitCreateAsync();
        var sql = await CopyInsertSqlAsync(support.Case, "mappings", "id=$1",
            new Dictionary<string, string> { ["id"] = "gen_random_uuid()", ["head_revision_id"] = "gen_random_uuid()" });
        // The INSERT statement is accepted; the missing head rejects COMMIT through the
        // initially deferred cycle. No placeholder mapping survives the failed transaction.
        await using (var connection = await support.Case.Owner.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand(sql, connection, transaction);
            insert.Parameters.AddWithValue(first.Facts.FutureRevision.MappingId);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }
        var before = await RowJsonAsync(support.Case, "mappings", "id=$1", first.Facts.FutureRevision.MappingId);
        await AssertOwnerForeignKeyDeniedAsync(support.Case,
            "UPDATE braid.mappings SET head_revision_id=$1,head_sequence=head_sequence+1,updated_at=greatest(updated_at,clock_timestamp()) WHERE id=$2",
            other.Facts.FutureRevision.RevisionId, first.Facts.FutureRevision.MappingId);
        Assert.Equal(before, await RowJsonAsync(support.Case, "mappings", "id=$1", first.Facts.FutureRevision.MappingId));
        Assert.Equal(2L, await support.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mappings"));
        Assert.Equal(2L, await support.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
    }

    private static string Uuid(Guid value) => $"'{value:D}'::uuid";

    private static Task<string> RowJsonAsync(PostgresCase test, string table, string predicate, params object[] values) =>
        test.ScalarAsync<string>($"SELECT row_to_json(stored)::text FROM braid.{table} stored WHERE {predicate}", values);

    private static async Task AssertOwnerCheckDeniedAsync(PostgresCase test, string sql, params object[] values)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => test.ExecuteAsync(sql, values));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    private static async Task AssertOwnerForeignKeyDeniedAsync(PostgresCase test, string sql, params object[] values)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => test.ExecuteAsync(sql, values));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    private static async Task<string> CopyInsertSqlAsync(PostgresCase test, string table, string predicate,
        IReadOnlyDictionary<string, string> changes)
    {
        var columns = new List<string>();
        await using var query = test.Owner.CreateCommand("SELECT attname FROM pg_attribute WHERE attrelid=$1::regclass AND attnum>0 AND NOT attisdropped ORDER BY attnum");
        query.Parameters.AddWithValue($"braid.{table}");
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        Assert.All(changes.Keys, column => Assert.Contains(column, columns));
        var names = string.Join(",", columns.Select(column => $"\"{column}\""));
        var values = string.Join(",", columns.Select(column => changes.TryGetValue(column, out var expression) ? expression : $"\"{column}\""));
        return $"INSERT INTO braid.{table} ({names}) SELECT {values} FROM braid.{table} WHERE {predicate}";
    }

    private static string PendingRevisionInsertSql(IReadOnlyDictionary<string, string> changes)
    {
        var facts = new (string Column, string Expression)[]
        {
            ("organization_id", "organization_id"), ("project_id", "project_id"),
            ("mapping_id", "future_mapping_id"), ("id", "future_revision_id"), ("sequence", "2"),
            ("native_object_id", "native_object_id"), ("native_class", "native_class"),
            ("native_digest", "native_digest"), ("native_byte_count", "native_byte_count"),
            ("logical_entry_document", "logical_entry_document"), ("dependency_manifest_id", "dependency_manifest_id"),
            ("backend_id", "backend_id"), ("creator_principal_id", "actor_principal_id"),
            ("committed_at", "clock_timestamp()"), ("ingestion_operation_id", "operation_id"),
            ("initial_attempt_generation", "eligible_attempt_generation"), ("initial_admission", "eligible_admission")
        };
        var columns = string.Join(",", facts.Select(fact => fact.Column));
        var expressions = string.Join(",", facts.Select(fact => changes.TryGetValue(fact.Column, out var expression) ? expression : fact.Expression));
        return $"INSERT INTO braid.mapping_revisions ({columns}) SELECT {expressions} FROM braid.revision_ingestions WHERE operation_id=$1";
    }

    private static async Task WithMutationAsync(PostgresCase test, string alter, string restore, Func<Task> verify)
    {
        await test.ExecuteAsync(alter);
        try { await verify(); }
        catch (Exception failure)
        {
            throw new InvalidOperationException($"Security admission probe failed after: {alter}", failure);
        }
        finally { await test.ExecuteAsync(restore); }
    }

    private static async Task AssertReaderAdmittedAsync(PostgresDatabase database, Guid organization)
    {
        await using var scope = await database.BeginTransactionAsync(organization);
        await using var readOnly = scope.CreateCommand("SHOW transaction_read_only");
        Assert.Equal("on", await readOnly.ExecuteScalarAsync());
    }

    private static Task AssertReaderRefusedAsync(PostgresDatabase database, Guid organization) =>
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var scope = await database.BeginTransactionAsync(organization);
        });

    private static async Task AssertIngestionAdmittedAsync(PostgresIngestionDatabase database, Guid organization)
    {
        await using var scope = await database.BeginTransactionAsync(organization);
        await using var readOnly = scope.CreateCommand("SHOW transaction_read_only");
        Assert.Equal("off", await readOnly.ExecuteScalarAsync());
    }

    private static Task AssertIngestionRefusedAsync(PostgresIngestionDatabase database, Guid organization) =>
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var scope = await database.BeginTransactionAsync(organization);
        });

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
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}
