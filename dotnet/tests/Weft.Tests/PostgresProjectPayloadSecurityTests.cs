using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresProjectPayloadSecurityTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task DedicatedReaderHasOnlyIntersectionColumnsAndDirectSqlCannotWriteOrReadForeignData()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await test.SetAsync();
        var foreign = new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB);
        await test.Owner.SetAsync(foreign, test.Actor.PrincipalId, true, 0);
        var before = await test.GrantsAsync();
        await using (var connection = PostgresProjectPayloadTestSupport.Unpooled(test.Case.PayloadPermissionConnectionString))
        {
            await connection.OpenAsync();
            await using (var rights = new NpgsqlCommand("""
                SELECT current_user='weft_payload_permission_reader' AND session_user=current_user
                    AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                    AND NOT rolreplication AND NOT rolinherit
                    AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_roles r WHERE r.rolname<>current_user
                        AND pg_catalog.pg_has_role(current_user,r.oid,'MEMBER'))
                    AND NOT pg_catalog.has_table_privilege(current_user,'weft.project_payload_read_grants','SELECT')
                    AND NOT pg_catalog.has_table_privilege(current_user,'weft.project_memberships','SELECT')
                    AND pg_catalog.has_column_privilege(current_user,'weft.project_payload_read_grants','version','SELECT')
                    AND pg_catalog.has_column_privilege(current_user,'weft.project_memberships','role','SELECT')
                    AND pg_catalog.has_column_privilege(current_user,'weft.principals','active','SELECT')
                    AND (SELECT count(*) FROM information_schema.column_privileges
                        WHERE grantee=current_user AND table_schema='weft' AND privilege_type='SELECT')=17
                FROM pg_catalog.pg_roles WHERE rolname=current_user
                """, connection))
                Assert.True(Assert.IsType<bool>(await rights.ExecuteScalarAsync()));
            await using (var unbound = new NpgsqlCommand("SELECT organization_id FROM weft.project_payload_read_grants", connection))
            await using (var rows = await unbound.ExecuteReaderAsync())
                Assert.False(await rows.ReadAsync());
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var bind = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true)", connection, transaction))
            {
                bind.Parameters.AddWithValue(test.Scope.OrganizationId.ToString());
                await bind.ExecuteScalarAsync();
            }
            await using (var select = new NpgsqlCommand("SELECT organization_id,project_id,principal_id,active,version FROM weft.project_payload_read_grants", connection, transaction))
            await using (var rows = await select.ExecuteReaderAsync())
            {
                Assert.True(await rows.ReadAsync());
                Assert.True(rows.GetGuid(0) == test.Scope.OrganizationId && rows.GetGuid(1) == test.Scope.ProjectId &&
                    rows.GetGuid(2) == test.Actor.PrincipalId.Value, "A restricted payload reader returned a foreign tuple.");
                Assert.True(rows.GetBoolean(3));
                Assert.Equal(1, rows.GetInt64(4));
                Assert.False(await rows.ReadAsync());
            }
            await transaction.RollbackAsync();
        }
        foreach (var sql in new[]
        {
            "UPDATE weft.project_payload_read_grants SET active=false,version=version+1",
            "DELETE FROM weft.project_payload_read_grants",
            "TRUNCATE weft.project_payload_read_grants",
            "INSERT INTO weft.principals(id,active) VALUES(gen_random_uuid(),true)",
            "SELECT created_at FROM weft.principals",
            "SELECT id FROM weft.organizations",
            "SELECT name FROM weft.projects",
            "SELECT name FROM weft.mappings",
            "SELECT subject FROM weft.external_identity_bindings",
            "SELECT version FROM weft.schema_migrations",
            "SELECT weft.external_identity_key_valid_v1('provider','authority','subject')"
        }) await PostgresProjectPayloadTestSupport.DeniedAsync(test.Case.PayloadPermissionConnectionString, sql);
        foreach (var profile in new[]
        {
            test.Case.RuntimeConnectionString, test.Case.IngestionConnectionString, test.Case.CatalogConnectionString,
            test.Case.MetadataConnectionString, test.Case.ProjectCreationConnectionString, test.Case.ReportConnectionString,
            test.Case.DerivationConnectionString, test.Case.MaintenanceConnectionString, test.Case.IdentityConnectionString
        }) await PostgresProjectPayloadTestSupport.DeniedAsync(profile, "SELECT principal_id FROM weft.project_payload_read_grants");
        PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
    }

    [PostgresFact]
    public async Task BothPortsRefuseUnsafeReaderRoleAttributesMembershipAndBroaderOrMissingRightsAndRecoverAfterRestore()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await test.SetAsync();
        var before = await test.GrantsAsync();
        async Task Refused()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.ObserveAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.SetAsync(false, 1));
            PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
        }
        foreach (var (change, restore) in new[]
        {
            ("ALTER ROLE weft_payload_permission_reader INHERIT", "ALTER ROLE weft_payload_permission_reader NOINHERIT"),
            ("ALTER ROLE weft_payload_permission_reader BYPASSRLS", "ALTER ROLE weft_payload_permission_reader NOBYPASSRLS"),
            ("GRANT pg_read_all_data TO weft_payload_permission_reader", "REVOKE pg_read_all_data FROM weft_payload_permission_reader")
        })
        {
            await test.DriftAsync(change, restore, Refused, bootstrap: true);
            PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
        }
        foreach (var (change, restore) in new[]
        {
            ("GRANT SELECT ON weft.project_payload_read_grants TO weft_payload_permission_reader",
                "REVOKE SELECT ON weft.project_payload_read_grants FROM weft_payload_permission_reader; GRANT SELECT(organization_id,project_id,principal_id,active,version) ON weft.project_payload_read_grants TO weft_payload_permission_reader"),
            ("GRANT SELECT(created_at) ON weft.principals TO weft_payload_permission_reader",
                "REVOKE SELECT(created_at) ON weft.principals FROM weft_payload_permission_reader"),
            ("GRANT UPDATE(active) ON weft.principals TO PUBLIC", "REVOKE UPDATE(active) ON weft.principals FROM PUBLIC"),
            ("REVOKE SELECT(version) ON weft.project_payload_read_grants FROM weft_payload_permission_reader",
                "GRANT SELECT(version) ON weft.project_payload_read_grants TO weft_payload_permission_reader")
        })
        {
            await test.DriftAsync(change, restore, Refused);
            PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
        }
    }

    [PostgresFact]
    public async Task BothPortsRefuseSourceShapeAndRlsDriftWithoutChangingRowsAndTrustedRestorationRecovers()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await test.SetAsync();
        var before = await test.GrantsAsync();
        async Task Refused()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.ObserveAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.SetAsync(false, 1));
            PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
        }
        foreach (var (change, restore) in new[]
        {
            ("ALTER TABLE weft.principals RENAME COLUMN created_at TO payload_unknown_probe",
                "ALTER TABLE weft.principals RENAME COLUMN payload_unknown_probe TO created_at"),
            ("ALTER TABLE weft.project_payload_read_grants DISABLE ROW LEVEL SECURITY",
                "ALTER TABLE weft.project_payload_read_grants ENABLE ROW LEVEL SECURITY"),
            ("ALTER POLICY project_payload_read_grants_organization_scope ON weft.project_payload_read_grants USING(true) WITH CHECK(true)",
                "ALTER POLICY project_payload_read_grants_organization_scope ON weft.project_payload_read_grants USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid) WITH CHECK(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid)"),
            ("ALTER TABLE weft.project_payload_read_grants DISABLE TRIGGER project_payload_read_grants_transition_guard",
                "ALTER TABLE weft.project_payload_read_grants ENABLE TRIGGER project_payload_read_grants_transition_guard")
        })
        {
            await test.DriftAsync(change, restore, Refused);
            PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
        }
        // A dropped physical attribute is not a pristine shape restoration.
        await using var extra = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await extra.SetAsync();
        var extraBefore = await extra.GrantsAsync();
        await extra.Case.ExecuteAsync("ALTER TABLE weft.project_payload_read_grants ADD COLUMN payload_unknown_probe integer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => extra.ObserveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => extra.SetAsync(false, 1));
        PostgresProjectPayloadTestSupport.SamePrivate(extraBefore, await extra.GrantsAsync());
    }

    [PostgresFact]
    public async Task ReaderAndMigrationConnectionsCannotBeInterchangedAndRefusalsPreserveEveryAuthorityRow()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await test.SetAsync();
        var before = await test.GrantsAsync();
        var authority = await test.AuthorityAsync();
        await using var swappedOwner = new ProjectPayloadGrantProvisioner(test.Case.PayloadPermissionConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => swappedOwner.SetAsync(test.Scope, test.Actor.PrincipalId, false, 1));
        await using var swappedDatabase = new PostgresPayloadPermissionDatabase(test.Case.MigrationConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresProjectPayloadPermissionReader(swappedDatabase).ObserveAsync(test.Actor, test.Scope));
        PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
        PostgresProjectPayloadTestSupport.SamePrivate(authority, await test.AuthorityAsync());
    }

    [PostgresFact]
    public async Task CallerCancellationBeforeAdmissionRemainsCancellationAndLeavesBothStoresUnchanged()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        var before = await test.GrantsAsync();
        var authority = await test.AuthorityAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (var call in new Func<Task>[]
        {
            () => test.ObserveAsync(cancellation.Token), () => test.SetAsync(cancellationToken: cancellation.Token)
        })
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(call);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
        PostgresProjectPayloadTestSupport.SamePrivate(authority, await test.AuthorityAsync());
    }

    [PostgresFact]
    public async Task ActualConnectionFailureRemainsRedactedFailureRatherThanDeniedAccessOrOwnerSuccess()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        var before = await test.GrantsAsync();
        var authority = await test.AuthorityAsync();
        var secret = $"private-{Guid.NewGuid():N}";
        var readerConnection = new NpgsqlConnectionStringBuilder(test.Case.PayloadPermissionConnectionString)
        { Password = secret, Pooling = false }.ConnectionString;
        var ownerConnection = new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString)
        { Password = secret, Pooling = false }.ConnectionString;
        await using var database = new PostgresPayloadPermissionDatabase(readerConnection);
        await using var owner = new ProjectPayloadGrantProvisioner(ownerConnection);
        foreach (var call in new Func<Task>[]
        {
            () => new PostgresProjectPayloadPermissionReader(database).ObserveAsync(test.Actor, test.Scope),
            () => owner.SetAsync(test.Scope, test.Actor.PrincipalId, true, 0)
        })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(call);
            Assert.Null(error.InnerException);
            Assert.True(error.Message is "Project payload permission persistence failed." or "Project payload grant owner operation failed.",
                "A payload persistence failure returned an unexpected diagnostic category.");
            Assert.False(error.ToString().Contains(secret, StringComparison.Ordinal), "A payload failure exposed a private credential.");
            Assert.False(error.ToString().Contains(readerConnection, StringComparison.Ordinal), "A payload failure exposed a private connection.");
            Assert.False(error.ToString().Contains(ownerConnection, StringComparison.Ordinal), "A payload failure exposed a private connection.");
        }
        PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
        PostgresProjectPayloadTestSupport.SamePrivate(authority, await test.AuthorityAsync());
    }

    [PostgresFact]
    public async Task DirectOwnerSqlCannotReassignEraseOrSkipVersionedGrantHistory()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await test.SetAsync();
        var before = await test.GrantsAsync();
        foreach (var sql in new[]
        {
            "UPDATE weft.project_payload_read_grants SET organization_id=$1",
            "UPDATE weft.project_payload_read_grants SET project_id=$1",
            "UPDATE weft.project_payload_read_grants SET principal_id=$1"
        }) await PostgresProjectPayloadTestSupport.CheckViolationAsync(test.Case, sql, Guid.NewGuid());
        foreach (var sql in new[]
        {
            "UPDATE weft.project_payload_read_grants SET active=false",
            "UPDATE weft.project_payload_read_grants SET active=false,version=version+2",
            "UPDATE weft.project_payload_read_grants SET version=version+1",
            "UPDATE weft.project_payload_read_grants SET version=0",
            "DELETE FROM weft.project_payload_read_grants",
            "TRUNCATE weft.project_payload_read_grants"
        }) await PostgresProjectPayloadTestSupport.CheckViolationAsync(test.Case, sql);
        PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
        PostgresProjectPayloadTestSupport.Grant(await test.SetAsync(false, 1), ProjectPayloadGrantOwnerOutcome.Updated,
            test.Scope, test.Actor.PrincipalId, false, 2);
        var revoked = await test.GrantsAsync();
        await PostgresProjectPayloadTestSupport.CheckViolationAsync(test.Case,
            "UPDATE weft.project_payload_read_grants SET active=true");
        PostgresProjectPayloadTestSupport.SamePrivate(revoked, await test.GrantsAsync());
    }

    [PostgresFact]
    public async Task DirectInsertRequiresExistingExactProjectMembershipAndValidInitialState()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        const string insert = "INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,$4,$5)";
        foreach (var (active, version) in new[] { (false, 1L), (true, 0L), (true, -1L), (true, 2L) })
            await PostgresProjectPayloadTestSupport.CheckViolationAsync(test.Case, insert,
                test.Scope.OrganizationId, test.Scope.ProjectId, test.Actor.PrincipalId.Value, active, version);
        // These inserts have valid initial state; the exact composite FKs, rather
        // than the initial-state trigger, must reject missing project/admission.
        foreach (var (organization, project, principal) in new[]
        {
            (test.Seed.OrganizationA, Guid.NewGuid(), test.Seed.Alice),
            (test.Seed.OrganizationA, test.Seed.ProjectA, test.Seed.Admitted),
            (test.Seed.OrganizationB, test.Seed.ProjectA, test.Seed.Alice)
        })
        {
            await using var command = test.Case.Owner.CreateCommand(insert);
            foreach (var value in new object[] { organization, project, principal, true, 1L })
                command.Parameters.Add(new NpgsqlParameter { Value = value });
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }
        PostgresProjectPayloadTestSupport.SamePrivate("[]", await test.GrantsAsync());
    }

    [PostgresFact]
    public async Task MaximumStoredVersionAllowsExactReplayAndRefusesMutationWithoutOverflowOrWrapping()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await test.SetAsync();
        // Trusted fixture seeding alone bypasses the ordinary transition guard
        // to reach an otherwise impractically distant positive Int64 boundary.
        await test.Case.ExecuteAsync("ALTER TABLE weft.project_payload_read_grants DISABLE TRIGGER project_payload_read_grants_transition_guard");
        try { await test.Case.ExecuteAsync("UPDATE weft.project_payload_read_grants SET version=$1", long.MaxValue); }
        finally { await test.Case.ExecuteAsync("ALTER TABLE weft.project_payload_read_grants ENABLE TRIGGER project_payload_read_grants_transition_guard"); }
        var before = await test.GrantsAsync();
        var authority = await test.AuthorityAsync();
        PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId, version: long.MaxValue);
        PostgresProjectPayloadTestSupport.Grant(await test.SetAsync(true, long.MaxValue), ProjectPayloadGrantOwnerOutcome.Unchanged,
            test.Scope, test.Actor.PrincipalId, true, long.MaxValue);
        PostgresProjectPayloadTestSupport.Refused(await test.SetAsync(false, long.MaxValue - 1), ProjectPayloadGrantOwnerOutcome.VersionConflict);
        PostgresProjectPayloadTestSupport.Refused(await test.SetAsync(false, long.MaxValue), ProjectPayloadGrantOwnerOutcome.VersionExhausted);
        PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
        PostgresProjectPayloadTestSupport.SamePrivate(authority, await test.AuthorityAsync());
    }
}
