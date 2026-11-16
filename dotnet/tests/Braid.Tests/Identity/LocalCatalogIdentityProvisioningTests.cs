using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class LocalCatalogIdentityProvisioningTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ExplicitIdentityCreatesOnlyItsOrganizationPrincipalAndAdmissionAndRepeatPreservesEveryFact()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);
        var organization = Guid.NewGuid();
        var principal = Guid.NewGuid();
        const string name = "Explicit local organization 'with quotes'";

        await provisioning.ProvisionAsync(organization, name, principal);

        Assert.Equal(name, await test.ScalarAsync<string>("SELECT name FROM braid.organizations WHERE id=$1", organization));
        Assert.True(await test.ScalarAsync<bool>("SELECT active FROM braid.principals WHERE id=$1", principal));
        Assert.True(await test.ScalarAsync<bool>("SELECT active FROM braid.organization_memberships WHERE organization_id=$1 AND principal_id=$2", organization, principal));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organizations"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.principals"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organization_memberships"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships"));
        await AssertNoCatalogWritesAsync(test);

        var before = await SnapshotAsync(test);
        await provisioning.ProvisionAsync(organization, name, principal);
        await provisioning.ProvisionAsync(organization, name, principal);
        Assert.Equal(before, await SnapshotAsync(test));
    }

    [PostgresFact]
    public async Task EveryExplicitRoleAddsOnlyThatMembershipToTheExistingSelectedProject()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var projectFacts = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(project) ORDER BY organization_id,id)::text FROM braid.projects project");
        await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);
        foreach (var (role, stored) in new[]
        {
            (ProjectRole.Viewer, "viewer"), (ProjectRole.Runner, "runner"),
            (ProjectRole.Publisher, "publisher"), (ProjectRole.Administrator, "administrator")
        })
        {
            var principal = Guid.NewGuid();
            var project = new ProjectScope(seed.OrganizationA, seed.ProjectA);
            await provisioning.ProvisionAsync(seed.OrganizationA, "Organization A", principal, project, role);
            Assert.True(await test.ScalarAsync<bool>("SELECT active FROM braid.principals WHERE id=$1", principal));
            Assert.True(await test.ScalarAsync<bool>("SELECT active FROM braid.organization_memberships WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, principal));
            Assert.Equal(stored, await test.ScalarAsync<string>("SELECT role FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, principal));
            Assert.True(await test.ScalarAsync<bool>("SELECT active FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, principal));
            Assert.Equal(1L, await test.ScalarAsync<long>("SELECT version FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, principal));
            Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships WHERE principal_id=$1", principal));
            var before = await SnapshotAsync(test);
            await provisioning.ProvisionAsync(seed.OrganizationA, "Organization A", principal, project, role);
            Assert.Equal(before, await SnapshotAsync(test));
        }
        Assert.Equal(projectFacts, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(project) ORDER BY organization_id,id)::text FROM braid.projects project"));
        Assert.Equal("viewer", await test.ScalarAsync<string>("SELECT role FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice));
        await AssertNoCatalogWritesAsync(test);
    }

    [PostgresFact]
    public async Task ConcurrentMatchedOwnerClientsCreateOneIdentityAdmissionAndProjectGrant()
    {
        await using var test = await fixture.CreateCaseAsync();
        var newOrganization = Guid.NewGuid();
        var newPrincipal = Guid.NewGuid();
        await ConcurrentProvisionAsync(newOrganization, "Concurrent new organization", newPrincipal);
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organizations"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.principals WHERE id=$1 AND active", newPrincipal));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organization_memberships WHERE organization_id=$1 AND principal_id=$2 AND active", newOrganization, newPrincipal));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships"));

        var seed = await PostgresSeedData.CreateAsync(test);
        var principal = Guid.NewGuid();
        var project = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        await ConcurrentProvisionAsync(seed.OrganizationA, "Organization A", principal, project, ProjectRole.Publisher);

        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.principals WHERE id=$1 AND active", principal));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organization_memberships WHERE organization_id=$1 AND principal_id=$2 AND active", seed.OrganizationA, principal));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3 AND active AND role='publisher' AND version=1", seed.OrganizationA, seed.ProjectA, principal));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships WHERE principal_id=$1", principal));
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"));
        await AssertNoCatalogWritesAsync(test);

        async Task ConcurrentProvisionAsync(Guid organizationId, string name, Guid principalId,
            ProjectScope? selectedProject = null, ProjectRole? role = null)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Independent data sources avoid turning the concurrency proof into leases
            // serialized by one MaxPoolSize=1 fixture connection pool.
            var operations = Enumerable.Range(0, 4).Select(async _ =>
            {
                await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);
                await start.Task;
                await provisioning.ProvisionAsync(organizationId, name, principalId, selectedProject, role);
            }).ToArray();
            start.SetResult();
            await Task.WhenAll(operations);
        }
    }

    [PostgresFact]
    public async Task ExistingInactivePrincipalIsPreservedAndTheNewOrganizationIsRolledBack()
    {
        await using var test = await fixture.CreateCaseAsync();
        var principal = Guid.NewGuid();
        var organization = Guid.NewGuid();
        await test.ExecuteAsync("INSERT INTO braid.principals(id,active) VALUES ($1,false)", principal);
        var before = await SnapshotAsync(test);
        await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioning.ProvisionAsync(organization, "Must roll back", principal));

        Assert.Equal(before, await SnapshotAsync(test));
        Assert.False(await test.ScalarAsync<bool>("SELECT active FROM braid.principals WHERE id=$1", principal));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organizations WHERE id=$1", organization));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organization_memberships"));
    }

    [PostgresFact]
    public async Task ExistingInactiveOrganizationAdmissionIsRefusedWithoutReactivation()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Admitted);
        var before = await SnapshotAsync(test);
        await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioning.ProvisionAsync(seed.OrganizationA, "Organization A", seed.Admitted));

        Assert.Equal(before, await SnapshotAsync(test));
        Assert.False(await test.ScalarAsync<bool>("SELECT active FROM braid.organization_memberships WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Admitted));
    }

    [PostgresFact]
    public async Task ConflictingOrganizationNameIsPreservedAndTheRequestedPrincipalIsNotCreated()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var principal = Guid.NewGuid();
        var before = await SnapshotAsync(test);
        await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioning.ProvisionAsync(seed.OrganizationA, "Different organization metadata", principal));

        Assert.Equal(before, await SnapshotAsync(test));
        Assert.Equal("Organization A", await test.ScalarAsync<string>("SELECT name FROM braid.organizations WHERE id=$1", seed.OrganizationA));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.principals WHERE id=$1", principal));
    }

    [PostgresFact]
    public async Task ExistingConflictingRoleOrInactiveProjectGrantIsNeverChanged()
    {
        foreach (var inactive in new[] { false, true })
        {
            await using var test = await fixture.CreateCaseAsync();
            var seed = await PostgresSeedData.CreateAsync(test);
            if (inactive)
                await test.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice);
            var before = await SnapshotAsync(test);
            await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);
            var requested = inactive ? ProjectRole.Viewer : ProjectRole.Publisher;

            await Assert.ThrowsAsync<InvalidOperationException>(() => provisioning.ProvisionAsync(seed.OrganizationA, "Organization A", seed.Alice,
                new ProjectScope(seed.OrganizationA, seed.ProjectA), requested));

            Assert.Equal(before, await SnapshotAsync(test));
            Assert.Equal("viewer", await test.ScalarAsync<string>("SELECT role FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice));
            Assert.Equal(!inactive, await test.ScalarAsync<bool>("SELECT active FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice));
        }
    }

    [PostgresFact]
    public async Task MissingProjectRollsBackAllNewIdentityRowsAndDoesNotCreateAProject()
    {
        await using var test = await fixture.CreateCaseAsync();
        var organization = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var before = await SnapshotAsync(test);
        await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioning.ProvisionAsync(organization, "Entirely new local identity", principal,
            new ProjectScope(organization, Guid.NewGuid()), ProjectRole.Administrator));

        Assert.Equal(before, await SnapshotAsync(test));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organizations"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.principals"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organization_memberships"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships"));
        await AssertNoCatalogWritesAsync(test);
    }

    [PostgresFact]
    public async Task AProjectFromAnotherOrganizationCannotReceiveTheGrantAndNewPrincipalRowsRollBack()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var principal = Guid.NewGuid();
        var before = await SnapshotAsync(test);
        await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioning.ProvisionAsync(seed.OrganizationA, "Organization A", principal,
            new ProjectScope(seed.OrganizationA, seed.ProjectB), ProjectRole.Administrator));

        Assert.Equal(before, await SnapshotAsync(test));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.principals WHERE id=$1", principal));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organization_memberships WHERE principal_id=$1", principal));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships WHERE principal_id=$1", principal));
    }

    [PostgresFact]
    public async Task RuntimeIngestionAndCatalogPoolsCannotPerformTheOwnerOperation()
    {
        await using var test = await fixture.CreateCaseAsync();
        var before = await SnapshotAsync(test);
        foreach (var connection in new[] { test.RuntimeConnectionString, test.IngestionConnectionString, test.CatalogConnectionString })
        {
            await using var provisioning = new LocalCatalogIdentityProvisioner(connection);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provisioning.ProvisionAsync(Guid.NewGuid(), "Refused runtime provision", Guid.NewGuid()));
        }
        Assert.Equal(before, await SnapshotAsync(test));
        await AssertNoCatalogWritesAsync(test);
    }

    [PostgresFact]
    public async Task OutdatedMissingTamperedOrUnknownMigrationHistoryIsRefusedWithoutAdoptionOrWrites()
    {
        foreach (var history in new[] { "outdated", "missing", "tampered", "unknown" })
        {
            await using var test = await fixture.CreateCaseAsync(migrate: history != "outdated");
            if (history == "outdated")
            {
                await using var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(2).ToArray());
                await old.MigrateAsync();
            }
            else if (history == "missing")
                await test.ExecuteAsync("DELETE FROM braid.schema_migrations WHERE version=1");
            else if (history == "tampered")
                await test.ExecuteAsync("UPDATE braid.schema_migrations SET checksum=$1 WHERE version=1", new string('a', 64));
            else
                await test.ExecuteAsync("INSERT INTO braid.schema_migrations(version,checksum) VALUES (99,$1)", new string('a', 64));
            var before = await SnapshotAsync(test);
            var journal = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(migration) ORDER BY version)::text FROM braid.schema_migrations migration");
            await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);

            await Assert.ThrowsAsync<InvalidOperationException>(() => provisioning.ProvisionAsync(Guid.NewGuid(), "Refused history provision", Guid.NewGuid()));

            Assert.Equal(before, await SnapshotAsync(test));
            Assert.Equal(journal, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(migration) ORDER BY version)::text FROM braid.schema_migrations migration"));
            await AssertNoCatalogWritesAsync(test);
        }
    }

    [PostgresFact]
    public async Task InvalidIdsNamesAndUnpairedForeignOrUnknownProjectOptionsMakeNoWrites()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var provisioning = new LocalCatalogIdentityProvisioner(test.MigrationConnectionString);
        var organization = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var project = new ProjectScope(organization, Guid.NewGuid());
        var before = await SnapshotAsync(test);
        Func<Task>[] invalid =
        [
            () => provisioning.ProvisionAsync(Guid.Empty, "Valid local name", principal),
            () => provisioning.ProvisionAsync(organization, "Valid local name", Guid.Empty),
            () => provisioning.ProvisionAsync(organization, "  ", principal),
            () => provisioning.ProvisionAsync(organization, new string('x', 201), principal),
            () => provisioning.ProvisionAsync(organization, "Valid local name", principal, project),
            () => provisioning.ProvisionAsync(organization, "Valid local name", principal, projectRole: ProjectRole.Viewer),
            () => provisioning.ProvisionAsync(organization, "Valid local name", principal, new ProjectScope(Guid.NewGuid(), project.ProjectId), ProjectRole.Viewer),
            () => provisioning.ProvisionAsync(organization, "Valid local name", principal, project, (ProjectRole)99)
        ];
        foreach (var operation in invalid)
            await Assert.ThrowsAnyAsync<ArgumentException>(operation);
        Assert.Equal(before, await SnapshotAsync(test));
        await AssertNoCatalogWritesAsync(test);
    }

    private static Task<string> SnapshotAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
            'organizations', (SELECT COALESCE(jsonb_agg(to_jsonb(source_row) ORDER BY id), '[]'::jsonb) FROM braid.organizations source_row),
            'principals', (SELECT COALESCE(jsonb_agg(to_jsonb(source_row) ORDER BY id), '[]'::jsonb) FROM braid.principals source_row),
            'organizationMemberships', (SELECT COALESCE(jsonb_agg(to_jsonb(source_row) ORDER BY organization_id,principal_id), '[]'::jsonb) FROM braid.organization_memberships source_row),
            'projects', (SELECT COALESCE(jsonb_agg(to_jsonb(source_row) ORDER BY organization_id,id), '[]'::jsonb) FROM braid.projects source_row),
            'projectMemberships', (SELECT COALESCE(jsonb_agg(to_jsonb(source_row) ORDER BY organization_id,project_id,principal_id), '[]'::jsonb) FROM braid.project_memberships source_row)
        )::text
        """);

    private static async Task AssertNoCatalogWritesAsync(PostgresCase test)
    {
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.mappings"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestions"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestion_attempts"));
    }
}
