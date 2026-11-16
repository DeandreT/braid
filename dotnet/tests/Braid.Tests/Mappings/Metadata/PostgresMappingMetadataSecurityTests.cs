using Npgsql;
using Braid.Application;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMappingMetadataSecurityTests(PostgresFixture fixture)
{
    private const string Login = "braid_catalog_editor";
    private static readonly string[] ReadTables =
        ["organizations", "principals", "organization_memberships", "projects", "project_memberships", "mappings"];
    private static readonly string[] UpdateColumns = ["name", "description", "version", "updated_at"];

    [PostgresFact]
    public async Task DedicatedEditorCanWriteOnlyFourMetadataColumnsAndCannotReadPrivateWorkflowOrHistory()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var original = await test.HeaderAsync();
        var immutable = await test.ImmutableSnapshotAsync();
        await AssertAdmittedAsync(test);
        await using var direct = NpgsqlDataSource.Create(test.Case.MetadataConnectionString);
        await using (var identity = direct.CreateCommand("SELECT current_user || ':' || current_database()"))
            Assert.Equal($"{Login}:{test.Case.Name}", await identity.ExecuteScalarAsync());
        await using (var safety = direct.CreateCommand("""
            SELECT NOT rolsuper AND NOT rolbypassrls AND NOT rolcreaterole AND NOT rolcreatedb
              AND NOT rolreplication AND NOT rolinherit
              AND NOT pg_has_role(current_user,'braid_migrator','MEMBER')
              AND NOT EXISTS (SELECT 1 FROM pg_class relation JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
                              WHERE namespace.nspname='braid' AND relation.relowner=pg_roles.oid)
            FROM pg_roles WHERE rolname=current_user
            """))
            Assert.Equal(true, await safety.ExecuteScalarAsync());
        foreach (var table in ReadTables)
        {
            await using var rights = direct.CreateCommand("""
                SELECT has_table_privilege($1,'SELECT')
                  AND NOT has_table_privilege($1,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                """);
            rights.Parameters.AddWithValue($"braid.{table}");
            Assert.Equal(true, await rights.ExecuteScalarAsync());
        }
        foreach (var column in UpdateColumns)
        {
            await using var rights = direct.CreateCommand("SELECT has_column_privilege('braid.mappings',$1,'UPDATE')");
            rights.Parameters.AddWithValue(column);
            Assert.Equal(true, await rights.ExecuteScalarAsync());
        }
        foreach (var column in new[] { "organization_id", "project_id", "id", "backend_id", "creator_principal_id", "owner_principal_id", "created_at", "head_revision_id", "head_sequence" })
        {
            await using var rights = direct.CreateCommand("SELECT has_column_privilege('braid.mappings',$1,'UPDATE')");
            rights.Parameters.AddWithValue(column);
            Assert.Equal(false, await rights.ExecuteScalarAsync());
        }
        foreach (var sql in new[]
        {
            "SELECT id FROM braid.mapping_revisions",
            "SELECT display_filename FROM braid.revision_ingestions",
            "SELECT evidence FROM braid.revision_ingestion_attempts",
            "SELECT checksum FROM braid.schema_migrations",
            "SELECT braid.guard_mappings_transition()",
            "SET ROLE braid_migrator",
            "SET ROLE braid_ingest",
            "SET ROLE braid_catalog_reader",
            "CREATE SCHEMA metadata_security_probe",
            "CREATE TABLE braid.metadata_security_probe(id integer)",
            "CREATE TEMP TABLE metadata_security_probe(id integer)",
            "INSERT INTO braid.mappings(organization_id) SELECT organization_id FROM braid.projects WHERE false",
            "UPDATE braid.projects SET name='changed' WHERE false",
            "UPDATE braid.mappings SET head_sequence=head_sequence+1 WHERE false",
            "UPDATE braid.mappings SET head_revision_id=gen_random_uuid() WHERE false",
            "UPDATE braid.mappings SET backend_id='changed' WHERE false",
            "UPDATE braid.mappings SET owner_principal_id=gen_random_uuid() WHERE false",
            "UPDATE braid.mapping_revisions SET native_digest=repeat('0',64) WHERE false",
            "DELETE FROM braid.mappings WHERE false",
            "TRUNCATE braid.mappings"
        })
            await AssertDirectDeniedAsync(direct, test.Scope.OrganizationId, sql, PostgresErrorCodes.InsufficientPrivilege);
        await using (var scope = await test.Database.BeginTransactionAsync(test.Scope.OrganizationId))
        {
            await using var update = scope.CreateCommand("""
                UPDATE braid.mappings SET name=$1,description=$2,version=version+1,
                    updated_at=greatest(updated_at,clock_timestamp())
                WHERE organization_id=$3 AND project_id=$4 AND id=$5
                """);
            Add(update, "Direct permitted metadata", "Direct permitted description", test.Scope.OrganizationId, test.Scope.ProjectId, test.Scope.MappingId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
            await scope.CommitAsync();
        }
        var current = await test.HeaderAsync();
        Assert.Equal("Direct permitted metadata", current.Name);
        Assert.Equal("Direct permitted description", current.Description);
        Assert.Equal(2, current.Version);
        Assert.Equal(original.Head, current.Head);
        PostgresMappingMetadataTestSupport.AssertIdentityUnchanged(original, current);
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
    }

    [PostgresFact]
    public async Task NextLeaseRejectsBroadUpdatesPrivateSelectionMissingRightsAndGrantOptions()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        await AssertAdmittedAsync(test);
        await test.Case.ExecuteAsync("CREATE TABLE braid.metadata_grant_probe(id integer)");
        await test.Case.ExecuteAsync("CREATE SEQUENCE braid.metadata_grant_probe_sequence");
        await test.Case.ExecuteAsync("CREATE FUNCTION braid.metadata_grant_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
        await test.Case.ExecuteAsync("REVOKE ALL ON FUNCTION braid.metadata_grant_probe() FROM PUBLIC");
        try
        {
            foreach (var (alter, restore) in new[]
            {
                ($"GRANT UPDATE ON braid.mappings TO {Login}", $"""
                    REVOKE UPDATE ON braid.mappings FROM {Login};
                    GRANT UPDATE(name,description,version,updated_at) ON braid.mappings TO {Login}
                    """),
                ($"GRANT UPDATE(head_sequence) ON braid.mappings TO {Login}", $"REVOKE UPDATE(head_sequence) ON braid.mappings FROM {Login}"),
                ($"GRANT UPDATE(backend_id) ON braid.mappings TO {Login}", $"REVOKE UPDATE(backend_id) ON braid.mappings FROM {Login}"),
                ($"GRANT UPDATE(name) ON braid.projects TO {Login}", $"REVOKE UPDATE(name) ON braid.projects FROM {Login}"),
                ($"GRANT INSERT ON braid.mappings TO {Login}", $"REVOKE INSERT ON braid.mappings FROM {Login}"),
                ($"GRANT DELETE,TRUNCATE,TRIGGER ON braid.mappings TO {Login}", $"REVOKE DELETE,TRUNCATE,TRIGGER ON braid.mappings FROM {Login}"),
                ($"GRANT REFERENCES(id) ON braid.projects TO {Login}", $"REVOKE REFERENCES(id) ON braid.projects FROM {Login}"),
                ($"GRANT SELECT ON braid.mapping_revisions TO {Login}", $"REVOKE SELECT ON braid.mapping_revisions FROM {Login}"),
                ($"GRANT SELECT(display_filename) ON braid.revision_ingestions TO {Login}", $"REVOKE SELECT(display_filename) ON braid.revision_ingestions FROM {Login}"),
                ($"GRANT SELECT(evidence) ON braid.revision_ingestion_attempts TO {Login}", $"REVOKE SELECT(evidence) ON braid.revision_ingestion_attempts FROM {Login}"),
                ($"GRANT SELECT ON braid.schema_migrations TO {Login}", $"REVOKE SELECT ON braid.schema_migrations FROM {Login}"),
                ($"GRANT SELECT(id) ON braid.metadata_grant_probe TO {Login}", $"REVOKE SELECT(id) ON braid.metadata_grant_probe FROM {Login}"),
                ($"GRANT EXECUTE ON FUNCTION braid.metadata_grant_probe() TO {Login}", $"REVOKE EXECUTE ON FUNCTION braid.metadata_grant_probe() FROM {Login}"),
                ($"GRANT USAGE ON SEQUENCE braid.metadata_grant_probe_sequence TO {Login}", $"REVOKE USAGE ON SEQUENCE braid.metadata_grant_probe_sequence FROM {Login}"),
                ("GRANT SELECT ON SEQUENCE braid.metadata_grant_probe_sequence TO PUBLIC", "REVOKE SELECT ON SEQUENCE braid.metadata_grant_probe_sequence FROM PUBLIC"),
                ($"GRANT UPDATE(name) ON braid.mappings TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR UPDATE(name) ON braid.mappings FROM {Login}"),
                ($"GRANT SELECT ON braid.mappings TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR SELECT ON braid.mappings FROM {Login}"),
                ($"GRANT SELECT(name) ON braid.mappings TO {Login} WITH GRANT OPTION", $"REVOKE SELECT(name) ON braid.mappings FROM {Login}"),
                ($"GRANT USAGE ON SCHEMA braid TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR USAGE ON SCHEMA braid FROM {Login}"),
                ($"GRANT CONNECT ON DATABASE {test.Case.Name} TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE {test.Case.Name} FROM {Login}"),
                ($"REVOKE SELECT ON braid.organization_memberships FROM {Login}", $"GRANT SELECT ON braid.organization_memberships TO {Login}"),
                ($"REVOKE UPDATE(updated_at) ON braid.mappings FROM {Login}", $"GRANT UPDATE(updated_at) ON braid.mappings TO {Login}")
            })
                await MutateRefuseAndRestoreAsync(test, alter, restore);
        }
        finally
        {
            await test.Case.ExecuteAsync("DROP FUNCTION braid.metadata_grant_probe()");
            await test.Case.ExecuteAsync("DROP SEQUENCE braid.metadata_grant_probe_sequence");
            await test.Case.ExecuteAsync("DROP TABLE braid.metadata_grant_probe");
        }
    }

    [PostgresFact]
    public async Task PublicDefaultAndReachableRolePrivilegesCannotWidenTheEditor()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        await AssertAdmittedAsync(test);
        foreach (var (alter, restore) in new[]
        {
            ("GRANT UPDATE(head_sequence) ON braid.mappings TO PUBLIC", "REVOKE UPDATE(head_sequence) ON braid.mappings FROM PUBLIC"),
            ("GRANT SELECT(evidence) ON braid.revision_ingestion_attempts TO PUBLIC", "REVOKE SELECT(evidence) ON braid.revision_ingestion_attempts FROM PUBLIC"),
            ($"ALTER DEFAULT PRIVILEGES IN SCHEMA braid GRANT UPDATE ON TABLES TO {Login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA braid REVOKE UPDATE ON TABLES FROM {Login}"),
            ("ALTER DEFAULT PRIVILEGES IN SCHEMA braid GRANT SELECT ON TABLES TO PUBLIC", "ALTER DEFAULT PRIVILEGES IN SCHEMA braid REVOKE SELECT ON TABLES FROM PUBLIC"),
            ($"ALTER DEFAULT PRIVILEGES IN SCHEMA braid GRANT EXECUTE ON FUNCTIONS TO {Login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA braid REVOKE EXECUTE ON FUNCTIONS FROM {Login}")
        })
            await MutateRefuseAndRestoreAsync(test, alter, restore);
        var role = $"metadata_attack_{test.Case.Name}";
        await test.Case.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS");
        try
        {
            await test.Case.ExecuteAsync($"GRANT UPDATE(head_sequence) ON braid.mappings TO {role}");
            await test.Case.BootstrapAsync($"GRANT {role} TO {Login}");
            try { await AssertRefusedAsync(test); }
            finally { await test.Case.BootstrapAsync($"REVOKE {role} FROM {Login}"); }
            await test.Case.ExecuteAsync($"REVOKE UPDATE(head_sequence) ON braid.mappings FROM {role}");
            await test.Case.BootstrapAsync($"GRANT {role} TO {Login} WITH ADMIN OPTION");
            try { await AssertRefusedAsync(test); }
            finally { await test.Case.BootstrapAsync($"REVOKE {role} FROM {Login}"); }
            await AssertAdmittedAsync(test);
        }
        finally
        {
            await test.Case.ExecuteAsync($"REVOKE UPDATE(head_sequence) ON braid.mappings FROM {role}");
            await test.Case.BootstrapAsync($"DROP ROLE {role}");
        }
    }

    [PostgresFact]
    public async Task WrongPoolsUnsafeRolesCreationRightsAndUnreachableOwnersRejectWriterAdmission()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        foreach (var connectionString in new[]
        {
            test.Case.MigrationConnectionString, test.Case.RuntimeConnectionString,
            test.Case.IngestionConnectionString, test.Case.CatalogConnectionString
        })
        {
            await using var wrongPool = new PostgresMappingMetadataDatabase(connectionString);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresMappingMetadataWriter(wrongPool).ReplaceAsync(
                test.Actor, test.Scope, new(test.Scope, 1), OriginalEdit()));
        }
        foreach (var (alter, restore) in new[]
        {
            ($"ALTER ROLE {Login} SUPERUSER", $"ALTER ROLE {Login} NOSUPERUSER"),
            ($"ALTER ROLE {Login} BYPASSRLS", $"ALTER ROLE {Login} NOBYPASSRLS"),
            ($"ALTER ROLE {Login} CREATEROLE", $"ALTER ROLE {Login} NOCREATEROLE"),
            ($"ALTER ROLE {Login} CREATEDB", $"ALTER ROLE {Login} NOCREATEDB"),
            ($"ALTER ROLE {Login} REPLICATION", $"ALTER ROLE {Login} NOREPLICATION"),
            ($"ALTER ROLE {Login} INHERIT", $"ALTER ROLE {Login} NOINHERIT"),
            ($"ALTER ROLE {Login} NOLOGIN", $"ALTER ROLE {Login} LOGIN"),
            ($"GRANT braid_migrator TO {Login}", $"REVOKE braid_migrator FROM {Login}"),
            ($"GRANT braid_ingest TO {Login}", $"REVOKE braid_ingest FROM {Login}"),
            ($"GRANT pg_read_all_data TO {Login}", $"REVOKE pg_read_all_data FROM {Login}"),
            ($"GRANT pg_write_all_data TO {Login}", $"REVOKE pg_write_all_data FROM {Login}"),
            ($"GRANT CREATE ON DATABASE {test.Case.Name} TO {Login}", $"REVOKE CREATE ON DATABASE {test.Case.Name} FROM {Login}"),
            ($"GRANT TEMPORARY ON DATABASE {test.Case.Name} TO {Login}", $"REVOKE TEMPORARY ON DATABASE {test.Case.Name} FROM {Login}"),
            ($"GRANT CREATE ON SCHEMA braid TO {Login}", $"REVOKE CREATE ON SCHEMA braid FROM {Login}")
        })
        {
            await test.Case.BootstrapAsync(alter);
            try { await AssertRefusedAsync(test); }
            catch (Exception failure) { throw new InvalidOperationException($"Editor admission probe failed after: {alter}", failure); }
            finally { await test.Case.BootstrapAsync(restore); }
            await AssertAdmittedAsync(test);
        }
        var owner = $"metadata_owner_{test.Case.Name}";
        await test.Case.BootstrapAsync($"CREATE ROLE {owner} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS");
        try
        {
            Assert.False(await test.Case.ScalarAsync<bool>("SELECT pg_has_role($1,$2,'MEMBER')", Login, owner));
            foreach (var target in new[] { "SCHEMA braid", "FUNCTION braid.guard_mappings_transition()" }
                .Concat(ReadTables.Select(table => $"TABLE braid.{table}")))
            {
                await test.Case.BootstrapAsync($"ALTER {target} OWNER TO {owner}");
                try { await AssertRefusedAsync(test); }
                finally { await test.Case.BootstrapAsync($"ALTER {target} OWNER TO braid_migrator"); }
                await AssertAdmittedAsync(test);
            }
        }
        finally { await test.Case.BootstrapAsync($"DROP ROLE {owner}"); }
    }

    [PostgresFact]
    public async Task SourcePoliciesColumnsConstraintsAndInternalEnforcementMustKeepTheirAcceptedDefinitions()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        foreach (var table in ReadTables.Where(table => table != "principals"))
        {
            var policy = $"{table}_organization_scope";
            var expression = $"{(table == "organizations" ? "id" : "organization_id")} = NULLIF(current_setting('braid.organization_id',true),'')::uuid";
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE braid.{table} DISABLE ROW LEVEL SECURITY", $"ALTER TABLE braid.{table} ENABLE ROW LEVEL SECURITY"),
                ($"DROP POLICY {policy} ON braid.{table}", $"CREATE POLICY {policy} ON braid.{table} USING ({expression}) WITH CHECK ({expression})"),
                ($"ALTER POLICY {policy} ON braid.{table} USING (true)", $"ALTER POLICY {policy} ON braid.{table} USING ({expression})"),
                ($"CREATE POLICY metadata_unscoped_probe ON braid.{table} USING (true)", $"DROP POLICY metadata_unscoped_probe ON braid.{table}")
            })
                await MutateRefuseAndRestoreAsync(test, alter, restore);
        }
        foreach (var (table, constraint, sourceDefinition) in new[]
        {
            ("projects", "projects_name_check", "CHECK (char_length(name) BETWEEN 1 AND 200 AND char_length(btrim(name)) > 0)"),
            ("mappings", "mappings_name_check", "CHECK (char_length(name) BETWEEN 1 AND 200 AND name = btrim(name) AND char_length(btrim(name)) > 0)")
        })
        {
            var original = await ConstraintAsync(test.Case, table, constraint);
            await MutateRefuseAndRestoreAsync(test,
                $"ALTER TABLE braid.{table} DROP CONSTRAINT {constraint}; ALTER TABLE braid.{table} ADD CONSTRAINT {constraint} CHECK (true)",
                $"ALTER TABLE braid.{table} DROP CONSTRAINT {constraint}; ALTER TABLE braid.{table} ADD CONSTRAINT {constraint} {sourceDefinition}",
                async () => Assert.Equal(original, await ConstraintAsync(test.Case, table, constraint)));
        }
        const string membershipConstraint = "organization_memberships_organization_id_fkey";
        var membershipDefinition = await ConstraintAsync(test.Case, "organization_memberships", membershipConstraint);
        await MutateRefuseAndRestoreAsync(test,
            $"ALTER TABLE braid.organization_memberships DROP CONSTRAINT {membershipConstraint}; ALTER TABLE braid.organization_memberships ADD CONSTRAINT {membershipConstraint} {membershipDefinition} NOT VALID",
            $"ALTER TABLE braid.organization_memberships DROP CONSTRAINT {membershipConstraint}; ALTER TABLE braid.organization_memberships ADD CONSTRAINT {membershipConstraint} {membershipDefinition}",
            async () => Assert.Equal(membershipDefinition, await ConstraintAsync(test.Case, "organization_memberships", membershipConstraint)));
        var internalTrigger = await test.Case.ScalarAsync<string>("""
            SELECT tgname FROM pg_trigger
            WHERE tgrelid='braid.mappings'::regclass AND tgisinternal AND tgconstraint<>0
            ORDER BY oid LIMIT 1
            """);
        foreach (var mode in new[] { "DISABLE", "ENABLE ALWAYS", "ENABLE REPLICA" })
        {
            await test.Case.BootstrapAsync($"ALTER TABLE braid.mappings {mode} TRIGGER \"{internalTrigger}\"");
            try { await AssertRefusedAsync(test); }
            finally { await test.Case.BootstrapAsync($"ALTER TABLE braid.mappings ENABLE TRIGGER \"{internalTrigger}\""); }
            await AssertAdmittedAsync(test);
        }
        foreach (var (alter, restore) in new[]
        {
            ("ALTER POLICY mappings_organization_scope ON braid.mappings WITH CHECK (true)", "ALTER POLICY mappings_organization_scope ON braid.mappings WITH CHECK (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid)"),
            ($"ALTER POLICY mappings_organization_scope ON braid.mappings TO {Login}", "ALTER POLICY mappings_organization_scope ON braid.mappings TO PUBLIC"),
            ("ALTER TABLE braid.principals ENABLE ROW LEVEL SECURITY", "ALTER TABLE braid.principals DISABLE ROW LEVEL SECURITY"),
            ("ALTER TABLE braid.mappings ADD COLUMN metadata_unknown_column integer", "ALTER TABLE braid.mappings DROP COLUMN metadata_unknown_column"),
            ("ALTER TABLE braid.mappings ALTER COLUMN description DROP NOT NULL", "ALTER TABLE braid.mappings ALTER COLUMN description SET NOT NULL"),
            ("ALTER TABLE braid.mappings ALTER COLUMN version SET DEFAULT 2", "ALTER TABLE braid.mappings ALTER COLUMN version SET DEFAULT 1"),
            ("CREATE TABLE braid.metadata_inherited_probe () INHERITS (braid.mappings)", "DROP TABLE braid.metadata_inherited_probe")
        })
            await MutateRefuseAndRestoreAsync(test, alter, restore);
    }

    [PostgresFact]
    public async Task EffectiveMappingTransitionCannotBeDisabledReplacedWidenedOrShadowed()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        const string function = "braid.guard_mappings_transition()";
        var triggerDefinition = await test.Case.ScalarAsync<string>(
            "SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid='braid.mappings'::regclass AND tgname='mappings_transition_guard'");
        var functionDefinition = await test.Case.ScalarAsync<string>("SELECT pg_get_functiondef($1::regprocedure)", function);
        foreach (var (alter, restore) in new[]
        {
            ("ALTER TABLE braid.mappings DISABLE TRIGGER mappings_transition_guard", "ALTER TABLE braid.mappings ENABLE TRIGGER mappings_transition_guard"),
            ("DROP TRIGGER mappings_transition_guard ON braid.mappings", triggerDefinition),
            ($"CREATE OR REPLACE FUNCTION {function} RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,braid AS $$ BEGIN RETURN NEW; END; $$", functionDefinition),
            ($"ALTER FUNCTION {function} SECURITY DEFINER", $"ALTER FUNCTION {function} SECURITY INVOKER"),
            ($"ALTER FUNCTION {function} SET search_path=public,braid", $"ALTER FUNCTION {function} SET search_path=pg_catalog,braid"),
            ($"GRANT EXECUTE ON FUNCTION {function} TO {Login}", $"REVOKE EXECUTE ON FUNCTION {function} FROM {Login}"),
            ($"GRANT EXECUTE ON FUNCTION {function} TO PUBLIC", $"REVOKE EXECUTE ON FUNCTION {function} FROM PUBLIC"),
            ($"CREATE TRIGGER metadata_transition_probe BEFORE UPDATE ON braid.mappings FOR EACH ROW EXECUTE FUNCTION {function}", "DROP TRIGGER metadata_transition_probe ON braid.mappings"),
            ($"CREATE TRIGGER metadata_ownership_probe BEFORE UPDATE ON braid.projects FOR EACH ROW EXECUTE FUNCTION {function}", "DROP TRIGGER metadata_ownership_probe ON braid.projects"),
            ("CREATE RULE metadata_rewrite_probe AS ON UPDATE TO braid.mappings DO ALSO SELECT 1", "DROP RULE metadata_rewrite_probe ON braid.mappings")
        })
            await MutateRefuseAndRestoreAsync(test, alter, restore);
    }

    [PostgresFact]
    public async Task RawNoopCounterOnlyTimestampOnlyAndMixedHeadMetadataUpdatesAreRejectedWithoutChangingFacts()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var row = await test.RowSnapshotAsync();
        var immutable = await test.ImmutableSnapshotAsync();
        await using var editor = NpgsqlDataSource.Create(test.Case.MetadataConnectionString);
        foreach (var assignment in new[]
        {
            "name=name,description=description,version=version,updated_at=updated_at",
            "version=version+1",
            "updated_at=updated_at+interval '1 second'",
            "name='Changed without a version'",
            "name='Skipped metadata version',version=version+2"
        })
            await AssertDirectDeniedAsync(editor, test.Scope.OrganizationId,
                $"UPDATE braid.mappings SET {assignment} WHERE organization_id=$1 AND project_id=$2 AND id=$3",
                PostgresErrorCodes.CheckViolation, test.Scope.OrganizationId, test.Scope.ProjectId, test.Scope.MappingId);
        foreach (var assignment in new[]
        {
            "name='Mixed change',version=version+1,head_revision_id=gen_random_uuid(),head_sequence=head_sequence+1,updated_at=greatest(updated_at,clock_timestamp())",
            "head_sequence=head_sequence+1",
            "backend_id='Substituted backend',name='Changed metadata',version=version+1",
            "owner_principal_id=gen_random_uuid(),name='Transferred owner',version=version+1"
        })
        {
            await using var command = test.Case.Owner.CreateCommand(
                $"UPDATE braid.mappings SET {assignment} WHERE organization_id=$1 AND project_id=$2 AND id=$3");
            Add(command, test.Scope.OrganizationId, test.Scope.ProjectId, test.Scope.MappingId);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        }
        Assert.Equal(row, await test.RowSnapshotAsync());
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
        await AssertAdmittedAsync(test);
        var result = await test.ReplaceAsync(1, "Accepted disjoint metadata", "Accepted replacement");
        Assert.Equal(MappingMetadataWriteOutcome.Updated, result.Outcome);
        Assert.Equal(2, result.Metadata!.Version);
        Assert.Equal(test.Original.Facts.FutureRevision, (await test.HeaderAsync()).Head.Scope);
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
    }

    private static MappingMetadataEdit OriginalEdit() => new("Original synthetic metadata", "Original description");

    private static async Task AssertAdmittedAsync(PostgresMappingMetadataTestSupport test)
    {
        var result = await test.Writer.ReplaceAsync(test.Actor, test.Scope, new(test.Scope, 1), OriginalEdit());
        Assert.Equal(MappingMetadataWriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(test.Scope, result.Metadata!.Scope);
        Assert.Equal(1, result.Metadata.Version);
    }

    private static Task AssertRefusedAsync(PostgresMappingMetadataTestSupport test) =>
        Assert.ThrowsAsync<InvalidOperationException>(() => test.Writer.ReplaceAsync(
            test.Actor, test.Scope, new(test.Scope, 1), OriginalEdit()));

    private static async Task MutateRefuseAndRestoreAsync(PostgresMappingMetadataTestSupport test,
        string alter, string restore, Func<Task>? inspectRestoration = null)
    {
        var before = await test.RowSnapshotAsync();
        var immutable = await test.ImmutableSnapshotAsync();
        await test.Case.ExecuteAsync(alter);
        try { await AssertRefusedAsync(test); }
        catch (Exception failure) { throw new InvalidOperationException($"Metadata admission probe failed after: {alter}", failure); }
        finally { await test.Case.ExecuteAsync(restore); }
        if (inspectRestoration is not null)
            await inspectRestoration();
        await AssertAdmittedAsync(test);
        Assert.Equal(before, await test.RowSnapshotAsync());
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
    }

    private static Task<string> ConstraintAsync(PostgresCase test, string table, string constraint) => test.ScalarAsync<string>(
        "SELECT pg_get_constraintdef(oid,false) FROM pg_constraint WHERE conrelid=$1::regclass AND conname=$2", $"braid.{table}", constraint);

    private static async Task AssertDirectDeniedAsync(NpgsqlDataSource source, Guid organization, string sql,
        string expectedState, params object[] values)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var bind = new NpgsqlCommand("SELECT set_config('braid.organization_id',$1,true)", connection, transaction))
        {
            bind.Parameters.AddWithValue(organization.ToString("D"));
            await bind.ExecuteScalarAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        Add(command, values);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.True(failure.SqlState == expectedState, $"Direct metadata statement had SQLSTATE {failure.SqlState}: {sql}");
    }

    private static void Add(NpgsqlCommand command, params object[] values)
    {
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
    }
}
