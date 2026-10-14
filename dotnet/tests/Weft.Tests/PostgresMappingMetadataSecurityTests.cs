using Npgsql;
using Weft.Application;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMappingMetadataSecurityTests(PostgresFixture fixture)
{
    private const string Login = "weft_catalog_editor";
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
              AND NOT pg_has_role(current_user,'weft_migrator','MEMBER')
              AND NOT EXISTS (SELECT 1 FROM pg_class relation JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
                              WHERE namespace.nspname='weft' AND relation.relowner=pg_roles.oid)
            FROM pg_roles WHERE rolname=current_user
            """))
            Assert.Equal(true, await safety.ExecuteScalarAsync());
        foreach (var table in ReadTables)
        {
            await using var rights = direct.CreateCommand("""
                SELECT has_table_privilege($1,'SELECT')
                  AND NOT has_table_privilege($1,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                """);
            rights.Parameters.AddWithValue($"weft.{table}");
            Assert.Equal(true, await rights.ExecuteScalarAsync());
        }
        foreach (var column in UpdateColumns)
        {
            await using var rights = direct.CreateCommand("SELECT has_column_privilege('weft.mappings',$1,'UPDATE')");
            rights.Parameters.AddWithValue(column);
            Assert.Equal(true, await rights.ExecuteScalarAsync());
        }
        foreach (var column in new[] { "organization_id", "project_id", "id", "backend_id", "creator_principal_id", "owner_principal_id", "created_at", "head_revision_id", "head_sequence" })
        {
            await using var rights = direct.CreateCommand("SELECT has_column_privilege('weft.mappings',$1,'UPDATE')");
            rights.Parameters.AddWithValue(column);
            Assert.Equal(false, await rights.ExecuteScalarAsync());
        }
        foreach (var sql in new[]
        {
            "SELECT id FROM weft.mapping_revisions",
            "SELECT display_filename FROM weft.revision_ingestions",
            "SELECT evidence FROM weft.revision_ingestion_attempts",
            "SELECT checksum FROM weft.schema_migrations",
            "SELECT weft.guard_mappings_transition()",
            "SET ROLE weft_migrator",
            "SET ROLE weft_ingest",
            "SET ROLE weft_catalog_reader",
            "CREATE SCHEMA metadata_security_probe",
            "CREATE TABLE weft.metadata_security_probe(id integer)",
            "CREATE TEMP TABLE metadata_security_probe(id integer)",
            "INSERT INTO weft.mappings(organization_id) SELECT organization_id FROM weft.projects WHERE false",
            "UPDATE weft.projects SET name='changed' WHERE false",
            "UPDATE weft.mappings SET head_sequence=head_sequence+1 WHERE false",
            "UPDATE weft.mappings SET head_revision_id=gen_random_uuid() WHERE false",
            "UPDATE weft.mappings SET backend_id='changed' WHERE false",
            "UPDATE weft.mappings SET owner_principal_id=gen_random_uuid() WHERE false",
            "UPDATE weft.mapping_revisions SET native_digest=repeat('0',64) WHERE false",
            "DELETE FROM weft.mappings WHERE false",
            "TRUNCATE weft.mappings"
        })
            await AssertDirectDeniedAsync(direct, test.Scope.OrganizationId, sql, PostgresErrorCodes.InsufficientPrivilege);
        await using (var scope = await test.Database.BeginTransactionAsync(test.Scope.OrganizationId))
        {
            await using var update = scope.CreateCommand("""
                UPDATE weft.mappings SET name=$1,description=$2,version=version+1,
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
        await test.Case.ExecuteAsync("CREATE TABLE weft.metadata_grant_probe(id integer)");
        await test.Case.ExecuteAsync("CREATE SEQUENCE weft.metadata_grant_probe_sequence");
        await test.Case.ExecuteAsync("CREATE FUNCTION weft.metadata_grant_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
        await test.Case.ExecuteAsync("REVOKE ALL ON FUNCTION weft.metadata_grant_probe() FROM PUBLIC");
        try
        {
            foreach (var (alter, restore) in new[]
            {
                ($"GRANT UPDATE ON weft.mappings TO {Login}", $"""
                    REVOKE UPDATE ON weft.mappings FROM {Login};
                    GRANT UPDATE(name,description,version,updated_at) ON weft.mappings TO {Login}
                    """),
                ($"GRANT UPDATE(head_sequence) ON weft.mappings TO {Login}", $"REVOKE UPDATE(head_sequence) ON weft.mappings FROM {Login}"),
                ($"GRANT UPDATE(backend_id) ON weft.mappings TO {Login}", $"REVOKE UPDATE(backend_id) ON weft.mappings FROM {Login}"),
                ($"GRANT UPDATE(name) ON weft.projects TO {Login}", $"REVOKE UPDATE(name) ON weft.projects FROM {Login}"),
                ($"GRANT INSERT ON weft.mappings TO {Login}", $"REVOKE INSERT ON weft.mappings FROM {Login}"),
                ($"GRANT DELETE,TRUNCATE,TRIGGER ON weft.mappings TO {Login}", $"REVOKE DELETE,TRUNCATE,TRIGGER ON weft.mappings FROM {Login}"),
                ($"GRANT REFERENCES(id) ON weft.projects TO {Login}", $"REVOKE REFERENCES(id) ON weft.projects FROM {Login}"),
                ($"GRANT SELECT ON weft.mapping_revisions TO {Login}", $"REVOKE SELECT ON weft.mapping_revisions FROM {Login}"),
                ($"GRANT SELECT(display_filename) ON weft.revision_ingestions TO {Login}", $"REVOKE SELECT(display_filename) ON weft.revision_ingestions FROM {Login}"),
                ($"GRANT SELECT(evidence) ON weft.revision_ingestion_attempts TO {Login}", $"REVOKE SELECT(evidence) ON weft.revision_ingestion_attempts FROM {Login}"),
                ($"GRANT SELECT ON weft.schema_migrations TO {Login}", $"REVOKE SELECT ON weft.schema_migrations FROM {Login}"),
                ($"GRANT SELECT(id) ON weft.metadata_grant_probe TO {Login}", $"REVOKE SELECT(id) ON weft.metadata_grant_probe FROM {Login}"),
                ($"GRANT EXECUTE ON FUNCTION weft.metadata_grant_probe() TO {Login}", $"REVOKE EXECUTE ON FUNCTION weft.metadata_grant_probe() FROM {Login}"),
                ($"GRANT USAGE ON SEQUENCE weft.metadata_grant_probe_sequence TO {Login}", $"REVOKE USAGE ON SEQUENCE weft.metadata_grant_probe_sequence FROM {Login}"),
                ("GRANT SELECT ON SEQUENCE weft.metadata_grant_probe_sequence TO PUBLIC", "REVOKE SELECT ON SEQUENCE weft.metadata_grant_probe_sequence FROM PUBLIC"),
                ($"GRANT UPDATE(name) ON weft.mappings TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR UPDATE(name) ON weft.mappings FROM {Login}"),
                ($"GRANT SELECT ON weft.mappings TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR SELECT ON weft.mappings FROM {Login}"),
                ($"GRANT SELECT(name) ON weft.mappings TO {Login} WITH GRANT OPTION", $"REVOKE SELECT(name) ON weft.mappings FROM {Login}"),
                ($"GRANT USAGE ON SCHEMA weft TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR USAGE ON SCHEMA weft FROM {Login}"),
                ($"GRANT CONNECT ON DATABASE {test.Case.Name} TO {Login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE {test.Case.Name} FROM {Login}"),
                ($"REVOKE SELECT ON weft.organization_memberships FROM {Login}", $"GRANT SELECT ON weft.organization_memberships TO {Login}"),
                ($"REVOKE UPDATE(updated_at) ON weft.mappings FROM {Login}", $"GRANT UPDATE(updated_at) ON weft.mappings TO {Login}")
            })
                await MutateRefuseAndRestoreAsync(test, alter, restore);
        }
        finally
        {
            await test.Case.ExecuteAsync("DROP FUNCTION weft.metadata_grant_probe()");
            await test.Case.ExecuteAsync("DROP SEQUENCE weft.metadata_grant_probe_sequence");
            await test.Case.ExecuteAsync("DROP TABLE weft.metadata_grant_probe");
        }
    }

    [PostgresFact]
    public async Task PublicDefaultAndReachableRolePrivilegesCannotWidenTheEditor()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        await AssertAdmittedAsync(test);
        foreach (var (alter, restore) in new[]
        {
            ("GRANT UPDATE(head_sequence) ON weft.mappings TO PUBLIC", "REVOKE UPDATE(head_sequence) ON weft.mappings FROM PUBLIC"),
            ("GRANT SELECT(evidence) ON weft.revision_ingestion_attempts TO PUBLIC", "REVOKE SELECT(evidence) ON weft.revision_ingestion_attempts FROM PUBLIC"),
            ($"ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT UPDATE ON TABLES TO {Login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE UPDATE ON TABLES FROM {Login}"),
            ("ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT SELECT ON TABLES TO PUBLIC", "ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE SELECT ON TABLES FROM PUBLIC"),
            ($"ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT EXECUTE ON FUNCTIONS TO {Login}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE EXECUTE ON FUNCTIONS FROM {Login}")
        })
            await MutateRefuseAndRestoreAsync(test, alter, restore);
        var role = $"metadata_attack_{test.Case.Name}";
        await test.Case.BootstrapAsync($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS");
        try
        {
            await test.Case.ExecuteAsync($"GRANT UPDATE(head_sequence) ON weft.mappings TO {role}");
            await test.Case.BootstrapAsync($"GRANT {role} TO {Login}");
            try { await AssertRefusedAsync(test); }
            finally { await test.Case.BootstrapAsync($"REVOKE {role} FROM {Login}"); }
            await test.Case.ExecuteAsync($"REVOKE UPDATE(head_sequence) ON weft.mappings FROM {role}");
            await test.Case.BootstrapAsync($"GRANT {role} TO {Login} WITH ADMIN OPTION");
            try { await AssertRefusedAsync(test); }
            finally { await test.Case.BootstrapAsync($"REVOKE {role} FROM {Login}"); }
            await AssertAdmittedAsync(test);
        }
        finally
        {
            await test.Case.ExecuteAsync($"REVOKE UPDATE(head_sequence) ON weft.mappings FROM {role}");
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
            ($"GRANT weft_migrator TO {Login}", $"REVOKE weft_migrator FROM {Login}"),
            ($"GRANT weft_ingest TO {Login}", $"REVOKE weft_ingest FROM {Login}"),
            ($"GRANT pg_read_all_data TO {Login}", $"REVOKE pg_read_all_data FROM {Login}"),
            ($"GRANT pg_write_all_data TO {Login}", $"REVOKE pg_write_all_data FROM {Login}"),
            ($"GRANT CREATE ON DATABASE {test.Case.Name} TO {Login}", $"REVOKE CREATE ON DATABASE {test.Case.Name} FROM {Login}"),
            ($"GRANT TEMPORARY ON DATABASE {test.Case.Name} TO {Login}", $"REVOKE TEMPORARY ON DATABASE {test.Case.Name} FROM {Login}"),
            ($"GRANT CREATE ON SCHEMA weft TO {Login}", $"REVOKE CREATE ON SCHEMA weft FROM {Login}")
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
            foreach (var target in new[] { "SCHEMA weft", "FUNCTION weft.guard_mappings_transition()" }
                .Concat(ReadTables.Select(table => $"TABLE weft.{table}")))
            {
                await test.Case.BootstrapAsync($"ALTER {target} OWNER TO {owner}");
                try { await AssertRefusedAsync(test); }
                finally { await test.Case.BootstrapAsync($"ALTER {target} OWNER TO weft_migrator"); }
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
            var expression = $"{(table == "organizations" ? "id" : "organization_id")} = NULLIF(current_setting('weft.organization_id',true),'')::uuid";
            foreach (var (alter, restore) in new[]
            {
                ($"ALTER TABLE weft.{table} DISABLE ROW LEVEL SECURITY", $"ALTER TABLE weft.{table} ENABLE ROW LEVEL SECURITY"),
                ($"DROP POLICY {policy} ON weft.{table}", $"CREATE POLICY {policy} ON weft.{table} USING ({expression}) WITH CHECK ({expression})"),
                ($"ALTER POLICY {policy} ON weft.{table} USING (true)", $"ALTER POLICY {policy} ON weft.{table} USING ({expression})"),
                ($"CREATE POLICY metadata_unscoped_probe ON weft.{table} USING (true)", $"DROP POLICY metadata_unscoped_probe ON weft.{table}")
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
                $"ALTER TABLE weft.{table} DROP CONSTRAINT {constraint}; ALTER TABLE weft.{table} ADD CONSTRAINT {constraint} CHECK (true)",
                $"ALTER TABLE weft.{table} DROP CONSTRAINT {constraint}; ALTER TABLE weft.{table} ADD CONSTRAINT {constraint} {sourceDefinition}",
                async () => Assert.Equal(original, await ConstraintAsync(test.Case, table, constraint)));
        }
        const string membershipConstraint = "organization_memberships_organization_id_fkey";
        var membershipDefinition = await ConstraintAsync(test.Case, "organization_memberships", membershipConstraint);
        await MutateRefuseAndRestoreAsync(test,
            $"ALTER TABLE weft.organization_memberships DROP CONSTRAINT {membershipConstraint}; ALTER TABLE weft.organization_memberships ADD CONSTRAINT {membershipConstraint} {membershipDefinition} NOT VALID",
            $"ALTER TABLE weft.organization_memberships DROP CONSTRAINT {membershipConstraint}; ALTER TABLE weft.organization_memberships ADD CONSTRAINT {membershipConstraint} {membershipDefinition}",
            async () => Assert.Equal(membershipDefinition, await ConstraintAsync(test.Case, "organization_memberships", membershipConstraint)));
        var internalTrigger = await test.Case.ScalarAsync<string>("""
            SELECT tgname FROM pg_trigger
            WHERE tgrelid='weft.mappings'::regclass AND tgisinternal AND tgconstraint<>0
            ORDER BY oid LIMIT 1
            """);
        foreach (var mode in new[] { "DISABLE", "ENABLE ALWAYS", "ENABLE REPLICA" })
        {
            await test.Case.BootstrapAsync($"ALTER TABLE weft.mappings {mode} TRIGGER \"{internalTrigger}\"");
            try { await AssertRefusedAsync(test); }
            finally { await test.Case.BootstrapAsync($"ALTER TABLE weft.mappings ENABLE TRIGGER \"{internalTrigger}\""); }
            await AssertAdmittedAsync(test);
        }
        foreach (var (alter, restore) in new[]
        {
            ("ALTER POLICY mappings_organization_scope ON weft.mappings WITH CHECK (true)", "ALTER POLICY mappings_organization_scope ON weft.mappings WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id',true),'')::uuid)"),
            ($"ALTER POLICY mappings_organization_scope ON weft.mappings TO {Login}", "ALTER POLICY mappings_organization_scope ON weft.mappings TO PUBLIC"),
            ("ALTER TABLE weft.principals ENABLE ROW LEVEL SECURITY", "ALTER TABLE weft.principals DISABLE ROW LEVEL SECURITY"),
            ("ALTER TABLE weft.mappings ADD COLUMN metadata_unknown_column integer", "ALTER TABLE weft.mappings DROP COLUMN metadata_unknown_column"),
            ("ALTER TABLE weft.mappings ALTER COLUMN description DROP NOT NULL", "ALTER TABLE weft.mappings ALTER COLUMN description SET NOT NULL"),
            ("ALTER TABLE weft.mappings ALTER COLUMN version SET DEFAULT 2", "ALTER TABLE weft.mappings ALTER COLUMN version SET DEFAULT 1"),
            ("CREATE TABLE weft.metadata_inherited_probe () INHERITS (weft.mappings)", "DROP TABLE weft.metadata_inherited_probe")
        })
            await MutateRefuseAndRestoreAsync(test, alter, restore);
    }

    [PostgresFact]
    public async Task EffectiveMappingTransitionCannotBeDisabledReplacedWidenedOrShadowed()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        const string function = "weft.guard_mappings_transition()";
        var triggerDefinition = await test.Case.ScalarAsync<string>(
            "SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid='weft.mappings'::regclass AND tgname='mappings_transition_guard'");
        var functionDefinition = await test.Case.ScalarAsync<string>("SELECT pg_get_functiondef($1::regprocedure)", function);
        foreach (var (alter, restore) in new[]
        {
            ("ALTER TABLE weft.mappings DISABLE TRIGGER mappings_transition_guard", "ALTER TABLE weft.mappings ENABLE TRIGGER mappings_transition_guard"),
            ("DROP TRIGGER mappings_transition_guard ON weft.mappings", triggerDefinition),
            ($"CREATE OR REPLACE FUNCTION {function} RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,weft AS $$ BEGIN RETURN NEW; END; $$", functionDefinition),
            ($"ALTER FUNCTION {function} SECURITY DEFINER", $"ALTER FUNCTION {function} SECURITY INVOKER"),
            ($"ALTER FUNCTION {function} SET search_path=public,weft", $"ALTER FUNCTION {function} SET search_path=pg_catalog,weft"),
            ($"GRANT EXECUTE ON FUNCTION {function} TO {Login}", $"REVOKE EXECUTE ON FUNCTION {function} FROM {Login}"),
            ($"GRANT EXECUTE ON FUNCTION {function} TO PUBLIC", $"REVOKE EXECUTE ON FUNCTION {function} FROM PUBLIC"),
            ($"CREATE TRIGGER metadata_transition_probe BEFORE UPDATE ON weft.mappings FOR EACH ROW EXECUTE FUNCTION {function}", "DROP TRIGGER metadata_transition_probe ON weft.mappings"),
            ($"CREATE TRIGGER metadata_ownership_probe BEFORE UPDATE ON weft.projects FOR EACH ROW EXECUTE FUNCTION {function}", "DROP TRIGGER metadata_ownership_probe ON weft.projects"),
            ("CREATE RULE metadata_rewrite_probe AS ON UPDATE TO weft.mappings DO ALSO SELECT 1", "DROP RULE metadata_rewrite_probe ON weft.mappings")
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
                $"UPDATE weft.mappings SET {assignment} WHERE organization_id=$1 AND project_id=$2 AND id=$3",
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
                $"UPDATE weft.mappings SET {assignment} WHERE organization_id=$1 AND project_id=$2 AND id=$3");
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
        "SELECT pg_get_constraintdef(oid,false) FROM pg_constraint WHERE conrelid=$1::regclass AND conname=$2", $"weft.{table}", constraint);

    private static async Task AssertDirectDeniedAsync(NpgsqlDataSource source, Guid organization, string sql,
        string expectedState, params object[] values)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var bind = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true)", connection, transaction))
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
