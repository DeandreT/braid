using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Xunit.Abstractions;

namespace Braid.Tests;

/// <summary>Organization-switching migration, membership incarnation, discovery and invitation replay.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresOrganizationSwitchingTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private const int PrefixVersion = 56;

    [PostgresFact]
    public async Task Project_creation_assertion_requires_both_exact_active_hooks()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var connection = await test.Owner.OpenConnectionAsync();
        foreach (var (name, events, wrongFunction) in new[] {
            ("project_creations_transition_guard", "INSERT OR UPDATE OR DELETE", "request_key_guard"),
            ("request_key_guard", "INSERT OR DELETE", "guard_project_creations_transition") })
        foreach (var change in new[] { $"DROP TRIGGER {name} ON braid.project_creations", $"ALTER TABLE braid.project_creations DISABLE TRIGGER {name}",
            $"CREATE OR REPLACE TRIGGER {name} BEFORE {events} ON braid.project_creations FOR EACH ROW EXECUTE FUNCTION braid.{wrongFunction}()" })
        {
            await using var tx = await connection.BeginTransactionAsync();
            await using var assert = new NpgsqlCommand("SELECT braid.assert_project_creation_contract_v1()", connection, tx);
            await assert.ExecuteNonQueryAsync();
            await using var tamper = new NpgsqlCommand(change, connection, tx);
            await tamper.ExecuteNonQueryAsync();
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => assert.ExecuteNonQueryAsync());
            Assert.Equal("P0001", refusal.SqlState);
            await tx.RollbackAsync();
        }
    }
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");

    [PostgresFact]
    public async Task Admission_state_is_pinned_and_older_source_fingerprints_stay_meaningful()
    {
        await using var test = await fixture.CreateCaseAsync();
        Assert.Equal(66, MigrationRunner.EmbeddedMigrations.Count);
        Assert.Contains("CREATE TABLE braid.organization_admission_state", MigrationRunner.EmbeddedMigrations[PrefixVersion].Sql, StringComparison.Ordinal);
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var state = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction,
            [OrganizationSwitchingSchemaAdmission.StateTable], default, validateAutomation: false);
        output.WriteLine("AdmissionState=" + state);
        Assert.Equal(OrganizationSwitchingSchemaAdmission.StateFingerprint, state);
        await OrganizationSwitchingSchemaAdmission.ValidateAsync(connection, transaction, default);
        Assert.Equal(AdministrationRoleGuard.Fingerprint,
            await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, AdministrationRoleGuard.AuthorityTables, default));
    }

    [PostgresFact]
    public async Task Tampered_membership_triggers_refuse_every_source_capture()
    {
        await using var test = await fixture.CreateCaseAsync();
        await test.ExecuteAsync("ALTER TABLE braid.organization_memberships DISABLE TRIGGER organization_admission_incarnation");
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RegressionCaseSchemaContract.CaptureAsync(connection, transaction, AdministrationRoleGuard.AuthorityTables, default));
    }

    [PostgresFact]
    public async Task Populated_upgrade_backfills_every_retained_membership_once_and_conserves_history()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await MigratePrefixAsync(test);
        var seed = await PostgresSeedData.CreateAsync(test);
        var bOnly = Guid.NewGuid();
        await test.ExecuteAsync("INSERT INTO braid.principals(id) VALUES($1)", bOnly);
        await test.ExecuteAsync("INSERT INTO braid.organization_memberships(organization_id,principal_id) VALUES($1,$2)", seed.OrganizationB, bOnly);
        await test.ExecuteAsync("INSERT INTO braid.organization_memberships(organization_id,principal_id,active) VALUES($1,$2,false)", seed.OrganizationA, seed.Bob);
        await test.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Admitted);
        foreach (var (principal, subject, enabled) in new[] { (seed.Alice, Guid.NewGuid(), true), (bOnly, Guid.NewGuid(), true), (seed.Bob, Guid.NewGuid(), false) })
        {
            var key = EntraUserIdentity.Key(Tenant, subject);
            await test.ExecuteAsync("INSERT INTO braid.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES($1,$2,$3,$4,$5)",
                Guid.NewGuid(), key.Provider, key.Authority, key.Subject, principal);
            if (!enabled)
                await test.ExecuteAsync("UPDATE braid.external_identity_bindings SET enabled=false,version=2,disabled_at=clock_timestamp() WHERE principal_id=$1", principal);
        }
        var tables = await test.ScalarAsync<string[]>("""
            SELECT array_agg(c.relname::text ORDER BY c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='braid' AND c.relkind='r' AND c.relname<>'schema_migrations'
            """);
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables) before[table] = await RowsAsync(test, table);
        var journal = await JournalAsync(test);

        await test.MigrateAsync();
        foreach (var table in tables) Assert.Equal(before[table], await RowsAsync(test, table));
        Assert.Equal(journal, await JournalAsync(test, PrefixVersion));
        Assert.Equal(Enumerable.Range(1, 66), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.True(await test.ScalarAsync<bool>("""
            SELECT (SELECT count(*) FROM braid.organization_memberships)=(SELECT count(*) FROM braid.organization_admission_state)
                AND (SELECT count(*) FROM braid.organization_memberships WHERE NOT active)=2
                AND NOT EXISTS(SELECT 1 FROM braid.organization_memberships m LEFT JOIN braid.organization_admission_state s
                    USING(organization_id,principal_id) WHERE s.version IS DISTINCT FROM 1)
            """), "Every retained membership, active or inactive, must start at incarnation 1.");

        var full = await RowsAsync(test, OrganizationSwitchingSchemaAdmission.StateTable);
        var fullJournal = await JournalAsync(test);
        await test.MigrateAsync();
        Assert.Equal(full, await RowsAsync(test, OrganizationSwitchingSchemaAdmission.StateTable));
        Assert.Equal(fullJournal, await JournalAsync(test));
        foreach (var table in tables) Assert.Equal(before[table], await RowsAsync(test, table));
    }

    [PostgresFact]
    public async Task Misconfigured_discovery_login_refuses_the_migration_without_recording_it()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await MigratePrefixAsync(test);
        var journal = await JournalAsync(test);
        await test.BootstrapAsync("ALTER ROLE braid_organization_discovery INHERIT");
        try
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => test.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
        finally { await test.BootstrapAsync("ALTER ROLE braid_organization_discovery NOINHERIT"); }
        Assert.Equal(journal, await JournalAsync(test));
        Assert.True(await test.ScalarAsync<bool>("SELECT to_regclass('braid.organization_admission_state') IS NULL"));
        await test.MigrateAsync();
        Assert.Equal(66, await test.ScalarAsync<int>("SELECT max(version) FROM braid.schema_migrations"));
    }

    [PostgresFact]
    public async Task Incarnation_advances_once_per_activity_change_and_is_retained_history()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        Task<long> Version() => test.ScalarAsync<long>(
            "SELECT version FROM braid.organization_admission_state WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Alice);
        Task SetActive(bool active) => test.ExecuteAsync(
            "UPDATE braid.organization_memberships SET active=$3 WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Alice, active);
        Assert.Equal(1L, await Version());
        await SetActive(true);
        Assert.Equal(1L, await Version());
        await SetActive(false);
        await SetActive(false);
        Assert.Equal(2L, await Version());
        await SetActive(true);
        Assert.Equal(3L, await Version());
        Assert.Equal(1L, await test.ScalarAsync<long>(
            "SELECT version FROM braid.organization_admission_state WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationB, seed.Alice));
        foreach (var sql in new[]
        {
            "UPDATE braid.organization_admission_state SET version=version+1",
            "UPDATE braid.organization_admission_state SET version=1",
            "DELETE FROM braid.organization_admission_state",
            "TRUNCATE braid.organization_admission_state",
            "DELETE FROM braid.organization_memberships WHERE principal_id='" + seed.Admitted + "'"
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => test.ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
        Assert.Equal(3L, await Version());
        Assert.False(await test.ScalarAsync<bool>("SELECT has_table_privilege('braid_runtime','braid.organization_admission_state','SELECT,INSERT,UPDATE,DELETE')"));

        // An exhausted incarnation refuses the activity change rather than wrapping or resetting.
        await test.ExecuteAsync("ALTER TABLE braid.organization_admission_state DISABLE TRIGGER organization_admission_state_guard");
        await test.ExecuteAsync("UPDATE braid.organization_admission_state SET version=9223372036854775807 WHERE organization_id=$1 AND principal_id=$2",
            seed.OrganizationA, seed.Alice);
        await test.ExecuteAsync("ALTER TABLE braid.organization_admission_state ENABLE TRIGGER organization_admission_state_guard");
        var overflow = await Assert.ThrowsAsync<PostgresException>(() => SetActive(false));
        Assert.Equal(PostgresErrorCodes.NumericValueOutOfRange, overflow.SqlState);
        Assert.True(await test.ScalarAsync<bool>("SELECT active FROM braid.organization_memberships WHERE organization_id=$1 AND principal_id=$2",
            seed.OrganizationA, seed.Alice));
    }

    [PostgresFact]
    public async Task Catalog_admission_reads_the_current_incarnation_and_refuses_suspension()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        var reader = new PostgresCatalogReader(database);
        var alice = new ActorContext(new(seed.Alice));
        Assert.Equal(1L, (await reader.GetOrganizationAdmissionAsync(alice, seed.OrganizationA))!.AdmissionVersion);
        Assert.Null(await reader.GetOrganizationAdmissionAsync(new ActorContext(new(seed.Bob)), seed.OrganizationA));
        Assert.NotNull(await reader.GetOrganizationAdmissionAsync(new ActorContext(new(seed.Bob)), seed.OrganizationB));
        await test.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Alice);
        Assert.Null(await reader.GetOrganizationAdmissionAsync(alice, seed.OrganizationA));
        Assert.NotNull(await reader.GetOrganizationAdmissionAsync(alice, seed.OrganizationB));
        await test.ExecuteAsync("UPDATE braid.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Alice);
        Assert.Equal(3L, (await reader.GetOrganizationAdmissionAsync(alice, seed.OrganizationA))!.AdmissionVersion);
    }

    [PostgresFact]
    public async Task Discovery_pages_only_current_admissions_of_the_verified_binding_and_refuses_tampering()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var binding = Guid.NewGuid();
        var key = EntraUserIdentity.Key(Tenant, Guid.NewGuid());
        await test.ExecuteAsync("INSERT INTO braid.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES($1,$2,$3,$4,$5)",
            binding, key.Provider, key.Authority, key.Subject, seed.Alice);
        await using var discovery = new PostgresOrganizationDiscovery(test.RegressionConnectionString("braid_organization_discovery"));
        var identity = new OrganizationDiscoveryIdentity(new(seed.Alice), binding, 1);
        var all = (await discovery.DiscoverAsync(identity, 100, null))!;
        Assert.Equal([seed.OrganizationA, seed.OrganizationB], all.Items.Select(item => item.OrganizationId));
        Assert.Equal(["Organization A", "Organization B"], all.Items.Select(item => item.Name));
        Assert.Null(all.After);
        var first = (await discovery.DiscoverAsync(identity, 1, null))!;
        Assert.Equal(seed.OrganizationA, Assert.Single(first.Items).OrganizationId);
        Assert.Equal(seed.OrganizationA, first.After);
        var second = (await discovery.DiscoverAsync(identity, 1, first.After))!;
        Assert.Equal(seed.OrganizationB, Assert.Single(second.Items).OrganizationId);
        Assert.Null(second.After);

        await test.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationB, seed.Alice);
        Assert.Equal(seed.OrganizationA, Assert.Single((await discovery.DiscoverAsync(identity, 100, null))!.Items).OrganizationId);
        // A stale binding version or another principal's tuple is refused, never an empty page.
        await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.DiscoverAsync(new(new(seed.Alice), binding, 2), 100, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.DiscoverAsync(new(new(seed.Bob), binding, 1), 100, null));
        await test.ExecuteAsync("UPDATE braid.external_identity_bindings SET enabled=false,version=2,disabled_at=clock_timestamp() WHERE binding_id=$1", binding);
        await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.DiscoverAsync(new(new(seed.Alice), binding, 2), 100, null));

        // The direct function refuses every other login.
        var direct = await Assert.ThrowsAsync<PostgresException>(() => test.ScalarAsync<long>(
            "SELECT count(*) FROM braid.discover_organization_admissions($1,1,$2,NULL,10)", binding, seed.Alice));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, direct.SqlState);

        foreach (var (tamper, restore) in new[]
        {
            ("GRANT SELECT ON braid.organizations TO braid_organization_discovery", "REVOKE SELECT ON braid.organizations FROM braid_organization_discovery"),
            ("GRANT EXECUTE ON FUNCTION braid.administration_authorized(uuid,uuid,uuid) TO braid_organization_discovery",
             "REVOKE EXECUTE ON FUNCTION braid.administration_authorized(uuid,uuid,uuid) FROM braid_organization_discovery"),
            ("GRANT EXECUTE ON FUNCTION braid.discover_organization_admissions(uuid,bigint,uuid,uuid,integer) TO PUBLIC",
             "REVOKE EXECUTE ON FUNCTION braid.discover_organization_admissions(uuid,bigint,uuid,uuid,integer) FROM PUBLIC"),
            ("ALTER FUNCTION braid.discover_organization_admissions(uuid,bigint,uuid,uuid,integer) SET search_path=public",
             "ALTER FUNCTION braid.discover_organization_admissions(uuid,bigint,uuid,uuid,integer) SET search_path=pg_catalog,braid,pg_temp")
        })
        {
            await test.ExecuteAsync(tamper);
            try { await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.DiscoverAsync(identity, 100, null)); }
            finally { await test.ExecuteAsync(restore); }
        }
    }

    [PostgresFact]
    public async Task Redeemed_replay_is_acknowledged_only_while_identity_admission_and_grants_still_hold()
    {
        await using var test = await fixture.CreateCaseAsync();
        var org = Guid.NewGuid(); var administrator = Guid.NewGuid(); var project = Guid.NewGuid();
        await test.ExecuteAsync("INSERT INTO braid.organizations(id,name) VALUES($1,'Invitation fixture')", org);
        await test.ExecuteAsync("INSERT INTO braid.principals(id) VALUES($1)", administrator);
        await test.ExecuteAsync("INSERT INTO braid.organization_memberships(organization_id,principal_id) VALUES($1,$2)", org, administrator);
        await test.ExecuteAsync("INSERT INTO braid.projects(organization_id,id,name,creator_principal_id) VALUES($1,$2,'Project',$3)", org, project, administrator);
        await test.ExecuteAsync("INSERT INTO braid.project_memberships(organization_id,project_id,principal_id,role) VALUES($1,$2,$3,'administrator')", org, project, administrator);
        await OrganizationAdministratorBootstrap.BootstrapAsync(test.MigrationConnectionString, org, administrator, Tenant);
        await using var store = new PostgresAdministrationStore(test.RegressionConnectionString("braid_administration"));
        var actor = new ActorContext(new(administrator));
        async Task<string> Write(AdministrationOperation operation, Guid target, object data, bool scoped = false)
        {
            var version = (await store.ReadOrganizationAsync(actor, org, default))!.Version;
            return await store.WriteAsync(actor, org, scoped ? project : null, version, operation, target,
                JsonSerializer.SerializeToElement(data, ProjectionJson.CreateOptions()), default);
        }
        var secret = OrganizationInvitationSecret.Create(); var oid = Guid.NewGuid();
        var identity = EntraUserIdentity.Key(Tenant, oid);
        Assert.Equal("updated", await Write(AdministrationOperation.Invite, Guid.NewGuid(), new
        {
            tenantId = Tenant,
            expectedObjectId = oid,
            expiresAt = DateTimeOffset.UtcNow.AddHours(1),
            projectRoles = new[] { new { projectId = project, role = "viewer" } },
            secretHash = OrganizationInvitationSecret.Hash(secret)
        }));
        Assert.True(await store.RedeemAsync(org, secret, identity, default));
        var principal = await test.ScalarAsync<Guid>("SELECT principal_id FROM braid.external_identity_bindings WHERE subject=$1", oid.ToString("D"));
        Assert.True(await store.RedeemAsync(org, secret, identity, default));
        Assert.False(await store.RedeemAsync(org, secret, EntraUserIdentity.Key(Tenant, Guid.NewGuid()), default));
        var audit = await test.ScalarAsync<long>("SELECT count(*) FROM braid.administration_audit WHERE operation='inviteRedeem'");

        // Suspension: the historical redemption is no longer fresh admission, and replay restores nothing.
        Assert.Equal("updated", await Write(AdministrationOperation.OrganizationMember, principal, new { active = false, role = "member" }));
        Assert.False(await store.RedeemAsync(org, secret, identity, default));
        Assert.False(await test.ScalarAsync<bool>("SELECT active FROM braid.organization_memberships WHERE organization_id=$1 AND principal_id=$2", org, principal));
        Assert.Equal("updated", await Write(AdministrationOperation.OrganizationMember, principal, new { active = true, role = "member" }));
        Assert.True(await store.RedeemAsync(org, secret, identity, default));
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT version FROM braid.organization_admission_state WHERE organization_id=$1 AND principal_id=$2", org, principal));

        // A removed project grant is not re-added by replay.
        await test.ExecuteAsync("UPDATE braid.project_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", org, principal);
        Assert.False(await store.RedeemAsync(org, secret, identity, default));
        Assert.False(await test.ScalarAsync<bool>("SELECT active FROM braid.project_memberships WHERE organization_id=$1 AND principal_id=$2", org, principal));
        await test.ExecuteAsync("UPDATE braid.project_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2", org, principal);

        // A disabled binding is never revived.
        await test.ExecuteAsync("UPDATE braid.external_identity_bindings SET enabled=false,version=2,disabled_at=clock_timestamp() WHERE principal_id=$1", principal);
        Assert.False(await store.RedeemAsync(org, secret, identity, default));
        Assert.False(await test.ScalarAsync<bool>("SELECT enabled FROM braid.external_identity_bindings WHERE principal_id=$1", principal));
        Assert.Equal(audit, await test.ScalarAsync<long>("SELECT count(*) FROM braid.administration_audit WHERE operation='inviteRedeem'"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.external_identity_bindings WHERE subject=$1", oid.ToString("D")));
    }

    private static async Task MigratePrefixAsync(PostgresCase test)
    {
        await using var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(PrefixVersion).ToArray());
        await prefix.MigrateAsync();
    }

    private static Task<string> JournalAsync(PostgresCase test, int maximum = int.MaxValue) => test.ScalarAsync<string>(
        "SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM braid.schema_migrations r WHERE version<=$1", maximum);

    private static Task<string> RowsAsync(PostgresCase test, string table) => test.ScalarAsync<string>(
        $"SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb)::text FROM ONLY braid.{table} r");
}
