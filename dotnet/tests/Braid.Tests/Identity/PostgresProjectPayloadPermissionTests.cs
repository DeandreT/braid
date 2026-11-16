using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresProjectPayloadPermissionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task EveryExistingRoleNeedsItsOwnExplicitGrantAndRoleChangesDoNotIssueOrChangeOne()
    {
        foreach (var role in new[] { "viewer", "runner", "publisher", "administrator" })
        {
            await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
            await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role=$1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
                role, test.Scope.OrganizationId, test.Scope.ProjectId, test.Actor.PrincipalId.Value);
            Assert.Null(await test.ObserveAsync());
            PostgresProjectPayloadTestSupport.SamePrivate("[]", await test.GrantsAsync());
            var authority = await test.AuthorityAsync();
            PostgresProjectPayloadTestSupport.Grant(await test.SetAsync(), ProjectPayloadGrantOwnerOutcome.Created,
                test.Scope, test.Actor.PrincipalId, true, 1);
            PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
            PostgresProjectPayloadTestSupport.SamePrivate(authority, await test.AuthorityAsync());
            var grants = await test.GrantsAsync();
            foreach (var replacement in new[] { "viewer", "runner", "publisher", "administrator" })
            {
                await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role=$1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
                    replacement, test.Scope.OrganizationId, test.Scope.ProjectId, test.Actor.PrincipalId.Value);
                PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
                PostgresProjectPayloadTestSupport.SamePrivate(grants, await test.GrantsAsync());
            }
        }
    }

    [PostgresFact]
    public async Task ExplicitGrantIsIsolatedByCompleteOrganizationProjectAndPrincipalTuple()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        var neighbor = new ProjectScope(test.Seed.OrganizationA, test.Seed.HiddenProjectA);
        var foreign = new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB);
        var other = new ActorContext(new PrincipalId(test.Seed.Admitted));
        await test.Seed.AddMembershipAsync(test.Case, neighbor.OrganizationId, neighbor.ProjectId, test.Seed.Alice, "administrator");
        await test.Seed.AddMembershipAsync(test.Case, test.Scope.OrganizationId, test.Scope.ProjectId, test.Seed.Admitted, "administrator");
        await test.SetAsync();
        PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
        Assert.Null(await test.Reader.ObserveAsync(test.Actor, neighbor));
        Assert.Null(await test.Reader.ObserveAsync(test.Actor, foreign));
        Assert.Null(await test.Reader.ObserveAsync(other, test.Scope));
        Assert.Null(await test.Reader.ObserveAsync(new(new PrincipalId(Guid.NewGuid())), test.Scope));
        Assert.Null(await test.Reader.ObserveAsync(test.Actor, new(Guid.NewGuid(), test.Scope.ProjectId)));
        Assert.Null(await test.Reader.ObserveAsync(test.Actor, new(test.Scope.OrganizationId, Guid.NewGuid())));
        await test.Owner.SetAsync(foreign, test.Actor.PrincipalId, true, 0);
        PostgresProjectPayloadTestSupport.Exact(await test.Reader.ObserveAsync(test.Actor, foreign), foreign, test.Actor.PrincipalId);
        Assert.Null(await test.Reader.ObserveAsync(other, foreign));
        PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
        // Reusing the same restricted pool across scopes must not leave an organization binding.
        await using var connection = await test.Database.DataSource.OpenConnectionAsync();
        await using var context = new NpgsqlCommand("SELECT COALESCE(current_setting('braid.organization_id',true),'')=''", connection);
        Assert.True(Assert.IsType<bool>(await context.ExecuteScalarAsync()));
    }

    [PostgresFact]
    public async Task ReusedReaderRechecksEachAuthorityAndRestorationCannotUndoAnExplicitRevoke()
    {
        foreach (var table in new[] { "principals", "organization_memberships", "project_memberships" })
        {
            await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
            await test.SetAsync();
            var grants = await test.GrantsAsync();
            PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
            await SetAuthorityAsync(test, table, false);
            Assert.Null(await test.ObserveAsync());
            PostgresProjectPayloadTestSupport.SamePrivate(grants, await test.GrantsAsync());
            PostgresProjectPayloadTestSupport.Refused(await test.SetAsync(true, 1), ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable);
            await SetAuthorityAsync(test, table, true);
            PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
            PostgresProjectPayloadTestSupport.SamePrivate(grants, await test.GrantsAsync());
            await SetAuthorityAsync(test, table, false);
            PostgresProjectPayloadTestSupport.Grant(await test.SetAsync(false, 1), ProjectPayloadGrantOwnerOutcome.Updated,
                test.Scope, test.Actor.PrincipalId, false, 2);
            PostgresProjectPayloadTestSupport.Refused(await test.SetAsync(true, 2), ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable);
            await SetAuthorityAsync(test, table, true);
            Assert.Null(await test.ObserveAsync());
            PostgresProjectPayloadTestSupport.Grant(await test.SetAsync(true, 2), ProjectPayloadGrantOwnerOutcome.Updated,
                test.Scope, test.Actor.PrincipalId, true, 3);
            PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId, version: 3);
        }
    }

    [PostgresFact]
    public async Task MissingAuthorityRefusesGrantWithoutCreatingParentsAdmissionsOrAnImplicitGrant()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        var before = await test.AuthorityAsync();
        foreach (var (scope, principal) in new[]
        {
            (test.Scope, new PrincipalId(Guid.NewGuid())),
            (new ProjectScope(Guid.NewGuid(), test.Scope.ProjectId), test.Actor.PrincipalId),
            (new ProjectScope(test.Scope.OrganizationId, Guid.NewGuid()), test.Actor.PrincipalId),
            (test.Scope, new PrincipalId(test.Seed.Admitted)),
            (new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB), new PrincipalId(test.Seed.OtherProjectAdmin))
        })
        {
            PostgresProjectPayloadTestSupport.Refused(await test.Owner.SetAsync(scope, principal, true, 0),
                ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable);
            PostgresProjectPayloadTestSupport.Refused(await test.Owner.SetAsync(scope, principal, false, 0),
                ProjectPayloadGrantOwnerOutcome.Unchanged);
            PostgresProjectPayloadTestSupport.SamePrivate(before, await test.AuthorityAsync());
            PostgresProjectPayloadTestSupport.SamePrivate("[]", await test.GrantsAsync());
        }
    }

    [PostgresFact]
    public async Task OwnerUsesExactVersionBeforeReplayAndOnlyExplicitChangesAdvanceTheGrant()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        var authority = await test.AuthorityAsync();
        PostgresProjectPayloadTestSupport.Refused(await test.SetAsync(false), ProjectPayloadGrantOwnerOutcome.Unchanged);
        PostgresProjectPayloadTestSupport.Refused(await test.SetAsync(false, 1), ProjectPayloadGrantOwnerOutcome.VersionConflict);
        PostgresProjectPayloadTestSupport.SamePrivate("[]", await test.GrantsAsync());
        PostgresProjectPayloadTestSupport.Grant(await test.SetAsync(), ProjectPayloadGrantOwnerOutcome.Created,
            test.Scope, test.Actor.PrincipalId, true, 1);
        var original = await test.GrantsAsync();
        PostgresProjectPayloadTestSupport.Refused(await test.SetAsync(true, 0), ProjectPayloadGrantOwnerOutcome.VersionConflict);
        PostgresProjectPayloadTestSupport.Refused(await test.SetAsync(false, 2), ProjectPayloadGrantOwnerOutcome.VersionConflict);
        PostgresProjectPayloadTestSupport.Grant(await test.SetAsync(true, 1), ProjectPayloadGrantOwnerOutcome.Unchanged,
            test.Scope, test.Actor.PrincipalId, true, 1);
        PostgresProjectPayloadTestSupport.SamePrivate(original, await test.GrantsAsync());
        PostgresProjectPayloadTestSupport.Grant(await test.SetAsync(false, 1), ProjectPayloadGrantOwnerOutcome.Updated,
            test.Scope, test.Actor.PrincipalId, false, 2);
        Assert.Null(await test.ObserveAsync());
        var revoked = await test.GrantsAsync();
        PostgresProjectPayloadTestSupport.Refused(await test.SetAsync(false, 1), ProjectPayloadGrantOwnerOutcome.VersionConflict);
        PostgresProjectPayloadTestSupport.Grant(await test.SetAsync(false, 2), ProjectPayloadGrantOwnerOutcome.Unchanged,
            test.Scope, test.Actor.PrincipalId, false, 2);
        PostgresProjectPayloadTestSupport.SamePrivate(revoked, await test.GrantsAsync());
        PostgresProjectPayloadTestSupport.Grant(await test.SetAsync(true, 2), ProjectPayloadGrantOwnerOutcome.Updated,
            test.Scope, test.Actor.PrincipalId, true, 3);
        PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId, version: 3);
        PostgresProjectPayloadTestSupport.SamePrivate(authority, await test.AuthorityAsync());
    }

    [PostgresFact]
    public async Task IdentityPreparationBindingAndProjectCreationNeverIssueProductionPayloadPermission()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await using var identity = new ExternalIdentityBindingProvisioner(test.Case.MigrationConnectionString);
        var principal = new PrincipalId(Guid.NewGuid());
        Assert.Equal(ExternalIdentityPrincipalPreparationOutcome.Created, await identity.EnsurePrincipalAsync(principal));
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.Created,
            await identity.EnsureOrganizationAdmissionAsync(test.Scope.OrganizationId, principal));
        var key = new ExternalIdentityKey("payload-test", "https://identity.example/tenant", $"private-{Guid.NewGuid():N}");
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Created, (await identity.BindAsync(key, principal)).Outcome);
        await test.Seed.AddMembershipAsync(test.Case, test.Scope.OrganizationId, test.Scope.ProjectId, principal.Value, "administrator");
        Assert.Null(await test.Reader.ObserveAsync(new(principal), test.Scope));
        await using var capability = new OrganizationProjectCreationCapabilityProvisioner(test.Case.MigrationConnectionString);
        await capability.SetAsync(test.Scope.OrganizationId, principal, true);
        await using var creationDatabase = new PostgresProjectCreationDatabase(test.Case.ProjectCreationConnectionString);
        var created = await new PostgresProjectCreator(creationDatabase).CreateAsync(new(principal), test.Scope.OrganizationId,
            Guid.NewGuid(), new ProjectCreationMetadata("Payload permission remains explicit", ""));
        Assert.Equal(ProjectCreationOutcome.Created, created.Outcome);
        var receipt = Assert.IsType<ProjectCreationReceipt>(created.Receipt);
        Assert.Null(await test.Reader.ObserveAsync(new(principal), receipt.Project));
        PostgresProjectPayloadTestSupport.SamePrivate("[]", await test.GrantsAsync());
    }

    [PostgresFact]
    public async Task RecreatedPoolsAndOriginalClusterRestartRetainGrantedRevokedAndInactiveAuthorityStates()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        var revokedScope = new ProjectScope(test.Seed.OrganizationA, test.Seed.HiddenProjectA);
        var inactiveScope = new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB);
        var revokedPrincipal = new PrincipalId(test.Seed.OtherProjectAdmin);
        await test.SetAsync();
        await test.Owner.SetAsync(revokedScope, revokedPrincipal, true, 0);
        await test.Owner.SetAsync(revokedScope, revokedPrincipal, false, 1);
        await test.Owner.SetAsync(inactiveScope, test.Actor.PrincipalId, true, 0);
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            inactiveScope.OrganizationId, inactiveScope.ProjectId, test.Actor.PrincipalId.Value);
        var before = await test.GrantsAsync();
        var readerConnection = test.Case.PayloadPermissionConnectionString;
        var ownerConnection = test.Case.MigrationConnectionString;
        await test.Database.DisposeAsync();
        await test.Owner.DisposeAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var database = new PostgresPayloadPermissionDatabase(readerConnection);
        var reader = new PostgresProjectPayloadPermissionReader(database);
        PostgresProjectPayloadTestSupport.Exact(await reader.ObserveAsync(test.Actor, test.Scope), test.Scope, test.Actor.PrincipalId);
        Assert.Null(await reader.ObserveAsync(new(revokedPrincipal), revokedScope));
        Assert.Null(await reader.ObserveAsync(test.Actor, inactiveScope));
        await using var owner = new ProjectPayloadGrantProvisioner(ownerConnection);
        PostgresProjectPayloadTestSupport.Grant(await owner.SetAsync(revokedScope, revokedPrincipal, false, 2),
            ProjectPayloadGrantOwnerOutcome.Unchanged, revokedScope, revokedPrincipal, false, 2);
        await using var connection = PostgresProjectPayloadTestSupport.Unpooled(ownerConnection);
        await connection.OpenAsync();
        await using var snapshot = new NpgsqlCommand("SELECT COALESCE(jsonb_agg(to_jsonb(g) ORDER BY to_jsonb(g)::text),'[]'::jsonb)::text FROM braid.project_payload_read_grants g", connection);
        PostgresProjectPayloadTestSupport.SamePrivate(before, Assert.IsType<string>(await snapshot.ExecuteScalarAsync()));
    }

    private static Task SetAuthorityAsync(PostgresProjectPayloadTestSupport test, string table, bool active) => table switch
    {
        "principals" => test.Case.ExecuteAsync("UPDATE braid.principals SET active=$1 WHERE id=$2", active, test.Actor.PrincipalId.Value),
        "organization_memberships" => test.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=$1 WHERE organization_id=$2 AND principal_id=$3",
            active, test.Scope.OrganizationId, test.Actor.PrincipalId.Value),
        "project_memberships" => test.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=$1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
            active, test.Scope.OrganizationId, test.Scope.ProjectId, test.Actor.PrincipalId.Value),
        _ => throw new InvalidOperationException("Unknown owned authority probe.")
    };
}
