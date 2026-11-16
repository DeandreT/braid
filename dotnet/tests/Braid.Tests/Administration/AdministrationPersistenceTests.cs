using System.Text.Json;
using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class AdministrationPersistenceTests(PostgresFixture fixture)
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");

    [PostgresFact]
    public async Task Administrative_schema_has_the_expected_authority_and_append_only_audit_shape()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, AdministrationRoleGuard.AuthorityTables, default);
        Assert.Equal(AdministrationRoleGuard.Fingerprint, fingerprint);
    }

    [RegressionPostgresTheory]
    [InlineData("column")]
    [InlineData("authority")]
    [InlineData("audit")]
    [InlineData("tablePrivilege")]
    [InlineData("functionPrivilege")]
    public async Task Unexpected_authority_schema_or_privileges_refuse_reads_writes_and_redemption(string change)
    {
        await using var data = await Data.CreateAsync(fixture);
        var secret = OrganizationInvitationSecret.Create(); var oid = Guid.NewGuid();
        Assert.Equal("updated", await data.Invite(Guid.NewGuid(), secret, oid));
        var snapshot = (await data.Store.ReadOrganizationAsync(data.Actor, data.Org, default))!;
        await data.Case.ExecuteAsync(change switch
        {
            "column" => "ALTER TABLE braid.organization_invitations ALTER COLUMN secret_hash DROP NOT NULL",
            "authority" => "ALTER TABLE braid.project_memberships ADD COLUMN unexpected_authority boolean NOT NULL DEFAULT false",
            "audit" => "ALTER TABLE braid.administration_audit DISABLE TRIGGER USER",
            "tablePrivilege" => "GRANT SELECT ON braid.organization_invitations TO braid_administration",
            _ => "GRANT EXECUTE ON FUNCTION braid.administration_authorized(uuid,uuid,uuid) TO braid_administration"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Store.ReadOrganizationAsync(data.Actor, data.Org, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Store.ReadProjectAsync(data.Actor, new(data.Org, data.Project), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Store.WriteAsync(data.Actor, data.Org, null, snapshot.Version,
            AdministrationOperation.OrganizationMember, Guid.NewGuid(), JsonSerializer.SerializeToElement(new { active = true, role = "member" }), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Store.RedeemAsync(data.Org, secret, EntraUserIdentity.Key(Tenant, oid), default));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.principals"));
        Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.administration_audit WHERE operation='inviteRedeem'"));
    }

    [PostgresFact]
    public async Task Organization_role_is_separate_and_cannot_read_or_grant_a_project()
    {
        await using var data = await Data.CreateAsync(fixture, projectAdministrator: false);
        var organization = await data.Store.ReadOrganizationAsync(data.Actor, data.Org, default);
        Assert.NotNull(organization);
        Assert.Null(await data.Store.ReadProjectAsync(data.Actor, new(data.Org, data.Project), default));
        Assert.Equal("refused", await data.Write(AdministrationOperation.ProjectMember, data.Actor.PrincipalId.Value,
            new { active = true, role = "administrator" }, project: true));
        Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships"));
    }

    [PostgresFact]
    public async Task Last_administrator_protection_covers_project_organization_and_principal_deactivation()
    {
        await using var data = await Data.CreateAsync(fixture);
        foreach (var (op, body, project) in new[]
        {
            (AdministrationOperation.ProjectMember, new { active = false, role = "administrator" }, true),
            (AdministrationOperation.ProjectMember, new { active = true, role = "viewer" }, true),
            (AdministrationOperation.OrganizationMember, new { active = false, role = "administrator" }, false),
            (AdministrationOperation.OrganizationMember, new { active = true, role = "member" }, false)
        }) Assert.Equal("lastAdministrator", await data.Write(op, data.Actor.PrincipalId.Value, body, project));
        Assert.Equal("lastAdministrator", await data.Write(AdministrationOperation.PrincipalDisable, data.Actor.PrincipalId.Value, new { }));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.administration_audit"));
    }

    [PostgresFact]
    public async Task Concurrent_demotions_use_one_org_gate_and_stale_edits_cannot_remove_the_survivor()
    {
        await using var data = await Data.CreateAsync(fixture);
        var other = await data.AdmitAsync();
        Assert.Equal("updated", await data.Write(AdministrationOperation.ProjectMember, other, new { active = true, role = "administrator" }, true));
        var version = (await data.Store.ReadProjectAsync(data.Actor, new(data.Org, data.Project), default))!.Version;
        Task<string> Remove(Guid target) => data.Store.WriteAsync(data.Actor, data.Org, data.Project, version,
            AdministrationOperation.ProjectMember, target, JsonSerializer.SerializeToElement(new { active = false, role = "administrator" }), default);
        var outcomes = await Task.WhenAll(Remove(other), Remove(data.Actor.PrincipalId.Value));
        Assert.Single(outcomes, o => o == "updated");
        Assert.Single(outcomes, o => o is "preconditionFailed" or "refused");
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships WHERE active AND role='administrator'"));
    }

    [PostgresFact]
    public async Task Project_members_and_payload_grants_remain_independent_and_keep_revoked_history()
    {
        await using var data = await Data.CreateAsync(fixture);
        var other = await data.AdmitAsync();
        Assert.Equal("updated", await data.Write(AdministrationOperation.ProjectMember, other, new { active = true, role = "runner" }, true));
        Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.project_payload_read_grants"));
        Assert.Equal("updated", await data.Write(AdministrationOperation.PayloadRead, other, new { active = true }, true));
        Assert.Equal("updated", await data.Write(AdministrationOperation.PayloadRead, other, new { active = false }, true));
        Assert.Equal("updated", await data.Write(AdministrationOperation.ProjectMember, other, new { active = false, role = "runner" }, true));
        Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT version FROM braid.project_payload_read_grants WHERE principal_id=$1", other));
        Assert.False(await data.Case.ScalarAsync<bool>("SELECT active FROM braid.project_memberships WHERE principal_id=$1", other));
        Assert.Equal("refused", await data.Write(AdministrationOperation.PayloadRead, other, new { active = true }, true));
    }

    [PostgresFact]
    public async Task Invitations_bind_verified_tenant_and_object_once_without_default_project_access()
    {
        await using var data = await Data.CreateAsync(fixture);
        var secret = OrganizationInvitationSecret.Create();
        var invite = Guid.NewGuid(); var oid = Guid.NewGuid();
        Assert.Equal("updated", await data.Invite(invite, secret, oid));
        Assert.False(await data.Store.RedeemAsync(data.Org, secret, EntraUserIdentity.Key(Guid.NewGuid(), oid), default));
        Assert.False(await data.Store.RedeemAsync(data.Org, secret, EntraUserIdentity.Key(Tenant, Guid.NewGuid()), default));
        var identity = EntraUserIdentity.Key(Tenant, oid);
        Assert.True(await data.Store.RedeemAsync(data.Org, secret, identity, default));
        Assert.True(await data.Store.RedeemAsync(data.Org, secret, identity, default));
        Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.organization_memberships"));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships"));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.administration_audit WHERE operation='inviteRedeem'"));
        var audit = await data.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(a))::text FROM braid.administration_audit a");
        Assert.DoesNotContain(secret, audit); Assert.DoesNotContain(OrganizationInvitationSecret.Hash(secret), audit);
    }

    [PostgresFact]
    public async Task Revoked_expired_and_revoked_inviter_links_never_admit_anyone()
    {
        await using var data = await Data.CreateAsync(fixture);
        foreach (var reason in new[] { "revoked", "expired", "inviter" })
        {
            var secret = OrganizationInvitationSecret.Create(); var invite = Guid.NewGuid(); var oid = Guid.NewGuid();
            Assert.Equal("updated", await data.Invite(invite, secret, oid));
            if (reason == "revoked") Assert.Equal("updated", await data.Write(AdministrationOperation.InviteRevoke, invite, new { }));
            else if (reason == "expired") await data.Case.ExecuteAsync("UPDATE braid.organization_invitations SET created_at=clock_timestamp()-interval '2 hours',expires_at=clock_timestamp()-interval '1 hour' WHERE invitation_id=$1", invite);
            else await data.Case.ExecuteAsync("UPDATE braid.organization_administration_roles SET role='member'");
            Assert.False(await data.Store.RedeemAsync(data.Org, secret, EntraUserIdentity.Key(Tenant, oid), default));
        }
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.principals"));
    }

    [PostgresFact]
    public async Task Cross_organization_reads_and_writes_and_ordinary_member_escalation_are_refused()
    {
        await using var data = await Data.CreateAsync(fixture);
        var member = await data.AdmitAsync();
        var actor = new ActorContext(new(member));
        Assert.Null(await data.Store.ReadOrganizationAsync(actor, data.Org, default));
        Assert.Equal("refused", await data.Store.WriteAsync(actor, data.Org, null, 2, AdministrationOperation.OrganizationMember,
            member, JsonSerializer.SerializeToElement(new { active = true, role = "administrator" }), default));
        Assert.Null(await data.Store.ReadOrganizationAsync(data.Actor, Guid.NewGuid(), default));
        Assert.Null(await data.Store.ReadProjectAsync(data.Actor, new(data.Org, Guid.NewGuid()), default));
    }

    [PostgresFact]
    public async Task Restricted_login_cannot_read_tables_and_audit_history_cannot_be_changed_even_by_owner()
    {
        await using var data = await Data.CreateAsync(fixture);
        foreach (var sql in new[] { "SELECT * FROM braid.organization_invitations", "UPDATE braid.principals SET active=true", "DELETE FROM braid.administration_audit" })
            await PostgresExternalIdentityTestSupport.DeniedAsync(data.Case.RegressionConnectionString("braid_administration"), sql);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => data.Case.ExecuteAsync("DELETE FROM braid.administration_audit"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => data.Case.ExecuteAsync("TRUNCATE braid.administration_audit"));
    }

    [PostgresFact]
    public async Task Machine_grants_use_existing_invocation_semantics_and_audit_both_administrative_changes()
    {
        await using var releases = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture, activate: false);
        var org = releases.Target.Mapping.OrganizationId; var project = releases.Target.Mapping.ProjectId;
        await OrganizationAdministratorBootstrap.BootstrapAsync(releases.Case.MigrationConnectionString, org, releases.Actor.PrincipalId.Value, Tenant);
        await using var store = new PostgresAdministrationStore(releases.Case.RegressionConnectionString("braid_administration"));
        var grant = Guid.NewGuid(); var oid = Guid.NewGuid();
        var data = JsonSerializer.SerializeToElement(new { targetId = releases.Target.TargetId, tenantId = Tenant, objectId = oid });
        var version = (await store.ReadProjectAsync(releases.Actor, new(org, project), default))!.Version;
        Assert.Equal("updated", await store.WriteAsync(releases.Actor, org, project, version, AdministrationOperation.InvocationGrant, grant, data, default));
        var observed = (await store.ReadProjectAsync(releases.Actor, new(org, project), default))!;
        Assert.Contains(observed.Grants, g => g.GrantId == grant && g.ObjectId == oid && g.RevokedAt is null);
        Assert.Equal("updated", await store.WriteAsync(releases.Actor, org, project, observed.Version, AdministrationOperation.InvocationRevoke, grant,
            JsonSerializer.SerializeToElement(new { }), default));
        Assert.Equal(2L, await releases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.invocation_grant_audit WHERE grant_id=$1", grant));
        Assert.Equal(2L, await releases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.administration_audit WHERE target_id=$1", grant));
        Assert.True(await releases.Releases.Store.DisableAsync(releases.Actor, releases.Target));
        var current = (await store.ReadProjectAsync(releases.Actor, new(org, project), default))!;
        Assert.Equal("refused", await store.WriteAsync(releases.Actor, org, project, current.Version, AdministrationOperation.InvocationGrant, Guid.NewGuid(), data, default));
    }

    [PostgresFact]
    public async Task Binding_controls_are_tenant_scoped_and_legacy_bindings_cannot_satisfy_last_administrator_protection()
    {
        await using var data = await Data.CreateAsync(fixture);
        var primary = Guid.NewGuid(); var foreign = Guid.NewGuid();
        await data.Case.ExecuteAsync("INSERT INTO braid.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES($1,'entra.oid',$2,$3,$4)",
            primary, Tenant.ToString("D"), Guid.NewGuid().ToString("D"), data.Actor.PrincipalId.Value);
        await data.Case.ExecuteAsync("INSERT INTO braid.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES($1,'entra.oid',$2,$3,$4)",
            foreign, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), data.Actor.PrincipalId.Value);
        var visible = (await data.Store.ReadOrganizationAsync(data.Actor, data.Org, default))!;
        Assert.Equal(primary, Assert.Single(Assert.Single(visible.Members).Bindings).BindingId);
        Assert.Equal("refused", await data.Write(AdministrationOperation.BindingDisable, foreign, new { }));
        Assert.Equal("lastAdministrator", await data.Write(AdministrationOperation.BindingDisable, primary, new { }));
        await data.Case.ExecuteAsync("INSERT INTO braid.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES($1,'entra.oid',$2,$3,$4)",
            Guid.NewGuid(), Tenant.ToString("D"), Guid.NewGuid().ToString("D"), data.Actor.PrincipalId.Value);
        Assert.Equal("updated", await data.Write(AdministrationOperation.BindingDisable, primary, new { }));
    }

    [PostgresFact]
    public async Task An_administrator_without_any_enabled_stable_binding_cannot_count_as_the_remaining_administrator()
    {
        await using var data = await Data.CreateAsync(fixture);
        var other = await data.AdmitAsync(); var primary = Guid.NewGuid(); var secondary = Guid.NewGuid();
        Assert.Equal("updated", await data.Write(AdministrationOperation.OrganizationMember, other, new { active = true, role = "administrator" }));
        Assert.Equal("updated", await data.Write(AdministrationOperation.ProjectMember, other, new { active = true, role = "administrator" }, true));
        foreach (var (id, principal) in new[] { (primary, data.Actor.PrincipalId.Value), (secondary, other) })
            await data.Case.ExecuteAsync("INSERT INTO braid.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES($1,'entra.oid',$2,$3,$4)",
                id, Tenant.ToString("D"), Guid.NewGuid().ToString("D"), principal);
        Assert.Equal("updated", await data.Write(AdministrationOperation.BindingDisable, primary, new { }));
        var actor = new ActorContext(new(other));
        var version = (await data.Store.ReadOrganizationAsync(actor, data.Org, default))!.Version;
        foreach (var (op, target, project, body) in new (AdministrationOperation, Guid, Guid?, object)[]
        {
            (AdministrationOperation.BindingDisable, secondary, null, new { }),
            (AdministrationOperation.PrincipalDisable, other, null, new { }),
            (AdministrationOperation.OrganizationMember, other, null, new { active = true, role = "member" }),
            (AdministrationOperation.ProjectMember, other, data.Project, new { active = true, role = "viewer" })
        })
            Assert.Equal("lastAdministrator", await data.Store.WriteAsync(actor, data.Org, project, version, op, target, JsonSerializer.SerializeToElement(body), default));
    }

    [RegressionPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cross_organization_redemption_and_identity_disable_cannot_both_commit(bool binding)
    {
        await using var data = await Data.CreateAsync(fixture);
        var org = Guid.NewGuid(); var administrator = Guid.NewGuid();
        await data.Case.ExecuteAsync("INSERT INTO braid.organizations(id,name) VALUES($1,'Second organization')", org);
        await data.Case.ExecuteAsync("INSERT INTO braid.principals(id) VALUES($1)", administrator);
        await data.Case.ExecuteAsync("INSERT INTO braid.organization_memberships(organization_id,principal_id) VALUES($1,$2)", org, administrator);
        await OrganizationAdministratorBootstrap.BootstrapAsync(data.Case.MigrationConnectionString, org, administrator, Tenant);
        var actor = new ActorContext(new(administrator));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var principal = await data.AdmitAsync(); var oid = Guid.NewGuid(); var bindingId = Guid.NewGuid();
            await data.Case.ExecuteAsync("INSERT INTO braid.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES($1,'entra.oid',$2,$3,$4)",
                bindingId, Tenant.ToString("D"), oid.ToString("D"), principal);
            var secret = OrganizationInvitationSecret.Create();
            var version = (await data.Store.ReadOrganizationAsync(actor, org, default))!.Version;
            Assert.Equal("updated", await data.Store.WriteAsync(actor, org, null, version, AdministrationOperation.Invite, Guid.NewGuid(),
                JsonSerializer.SerializeToElement(new
                {
                    tenantId = Tenant,
                    expectedObjectId = oid,
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    projectRoles = Array.Empty<object>(),
                    secretHash = OrganizationInvitationSecret.Hash(secret)
                }), default));
            var disable = data.Write(binding ? AdministrationOperation.BindingDisable : AdministrationOperation.PrincipalDisable,
                binding ? bindingId : principal, new { });
            var redeem = data.Store.RedeemAsync(org, secret, EntraUserIdentity.Key(Tenant, oid), default);
            await Task.WhenAll(disable, redeem);
            Assert.Equal(await redeem ? "refused" : "updated", await disable);
        }
    }

    [PostgresFact]
    public Task Upgrade_from_25_preserves_existing_bindings_roles_and_project_history() => UpgradeAsync(25);

    [PostgresFact]
    public Task Upgrade_from_26_preserves_display_metadata_bindings_roles_and_project_history() => UpgradeAsync(26);

    private async Task UpgradeAsync(int prefixVersion)
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(prefixVersion).ToArray())) await prefix.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        // Seed the actual historical table shape. The current owner tool correctly
        // refuses an installation that has not yet reached this build's full history.
        var binding = Guid.NewGuid();
        var identity = EntraUserIdentity.Key(Tenant, Guid.NewGuid());
        await test.ExecuteAsync("INSERT INTO braid.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES($1,$2,$3,$4,$5)",
            binding, identity.Provider, identity.Authority, identity.Subject, seed.Alice);
        if (prefixVersion >= 26)
        {
            await using var display = new PostgresAccountDisplayMetadata(test.AccountDisplayConnectionString);
            await display.ObserveAsync(seed.OrganizationA, binding, "Original display name", DateTimeOffset.UtcNow, default);
        }
        var tables = new[] { "principals", "organization_memberships", "projects", "project_memberships", "external_identity_bindings" }
            .Concat(prefixVersion >= 26 ? ["account_display_metadata"] : Array.Empty<string>()).ToArray();
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables) before[table] = await Rows(table);
        var journal = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM braid.schema_migrations r");
        await test.MigrateAsync();
        foreach (var table in tables) Assert.Equal(before[table], await Rows(table));
        Assert.Equal(journal, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM braid.schema_migrations r WHERE version<=$1", prefixVersion));
        Assert.Equal(RegressionSchemaTestContract.AccountDisplayChecksum, await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=26"));
        Assert.Equal(RegressionSchemaTestContract.AdministrationChecksum, await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=27"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organization_administration_roles"));
        Assert.True(await test.ScalarAsync<bool>("SELECT active FROM braid.principals WHERE id=$1", seed.Alice));
        if (prefixVersion >= 26)
        {
            await using var display = new PostgresAccountDisplayMetadata(test.AccountDisplayConnectionString);
            Assert.Equal("Original display name", await display.ReadAsync(seed.OrganizationA, binding, default));
        }
        Task<string> Rows(string table) => test.ScalarAsync<string>($"SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb)::text FROM ONLY braid.{table} r");
    }

    private sealed class Data(PostgresCase test, Guid org, Guid project, ActorContext actor, PostgresAdministrationStore store) : IAsyncDisposable
    {
        internal PostgresCase Case { get; } = test;
        internal Guid Org { get; } = org;
        internal Guid Project { get; } = project;
        internal ActorContext Actor { get; } = actor;
        internal PostgresAdministrationStore Store { get; } = store;
        internal static async Task<Data> CreateAsync(PostgresFixture fixture, bool projectAdministrator = true)
        {
            var test = await fixture.CreateCaseAsync();
            var org = Guid.NewGuid(); var principal = Guid.NewGuid(); var project = Guid.NewGuid();
            await test.ExecuteAsync("INSERT INTO braid.organizations(id,name) VALUES($1,'Administration fixture')", org);
            await test.ExecuteAsync("INSERT INTO braid.principals(id) VALUES($1)", principal);
            await test.ExecuteAsync("INSERT INTO braid.organization_memberships(organization_id,principal_id) VALUES($1,$2)", org, principal);
            await test.ExecuteAsync("INSERT INTO braid.projects(organization_id,id,name,creator_principal_id) VALUES($1,$2,'Project',$3)", org, project, principal);
            if (projectAdministrator) await test.ExecuteAsync("INSERT INTO braid.project_memberships(organization_id,project_id,principal_id,role) VALUES($1,$2,$3,'administrator')", org, project, principal);
            await OrganizationAdministratorBootstrap.BootstrapAsync(test.MigrationConnectionString, org, principal, Tenant);
            return new(test, org, project, new(new(principal)), new(test.RegressionConnectionString("braid_administration")));
        }
        internal async Task<Guid> AdmitAsync()
        {
            var id = Guid.NewGuid();
            await Case.ExecuteAsync("INSERT INTO braid.principals(id) VALUES($1)", id);
            Assert.Equal("updated", await Write(AdministrationOperation.OrganizationMember, id, new { active = true, role = "member" }));
            return id;
        }
        internal async Task<string> Write(AdministrationOperation op, Guid target, object data, bool project = false)
        {
            var version = (await Store.ReadOrganizationAsync(Actor, Org, default))!.Version;
            return await Store.WriteAsync(Actor, Org, project ? Project : null, version, op, target,
                JsonSerializer.SerializeToElement(data, ProjectionJson.CreateOptions()), default);
        }
        internal Task<string> Invite(Guid id, string secret, Guid oid) => Write(AdministrationOperation.Invite, id,
            new { tenantId = Tenant, expectedObjectId = oid, expiresAt = DateTimeOffset.UtcNow.AddHours(1), projectRoles = Array.Empty<object>(), secretHash = OrganizationInvitationSecret.Hash(secret) });
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); await Case.DisposeAsync(); }
    }
}
