using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresOwnershipTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task OrganizationAdmissionAndAnotherProjectsAdministratorGrantGiveNoAccess()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        var reader = new PostgresProjectReader(database);
        var admitted = new ActorContext(new PrincipalId(seed.Admitted));
        var otherAdmin = new ActorContext(new PrincipalId(seed.OtherProjectAdmin));
        var scope = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        Assert.Empty((await reader.ListVisibleProjectsAsync(admitted, seed.OrganizationA, 10)).Projects);
        Assert.Null(await reader.GetVisibleProjectAsync(admitted, scope));
        Assert.Null(await reader.GetAccessAsync(admitted, scope));
        Assert.Null(await reader.GetVisibleProjectAsync(otherAdmin, scope));
        Assert.Null(await reader.GetAccessAsync(otherAdmin, scope));
        var own = Assert.Single((await reader.ListVisibleProjectsAsync(otherAdmin, seed.OrganizationA, 10)).Projects);
        Assert.Equal(seed.HiddenProjectA, own.Scope.ProjectId);
        Assert.Equal(ProjectRole.Administrator, (await reader.GetAccessAsync(otherAdmin, own.Scope))!.Role);
        var alice = new ActorContext(new PrincipalId(seed.Alice));
        Assert.Null(await reader.GetVisibleProjectAsync(alice, new ProjectScope(seed.OrganizationB, seed.ProjectA)));
        Assert.Null(await reader.GetAccessAsync(alice, new ProjectScope(seed.OrganizationB, seed.ProjectA)));
    }

    [PostgresFact]
    public async Task PrincipalOrganizationAndProjectRevocationAffectTheVeryNextRead()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        var reader = new PostgresProjectReader(database);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var scope = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        async Task Check(bool visible)
        {
            Assert.Equal(visible, await reader.GetVisibleProjectAsync(actor, scope) is not null);
            Assert.Equal(visible, await reader.GetAccessAsync(actor, scope) is not null);
            Assert.Equal(visible ? 1 : 0, (await reader.ListVisibleProjectsAsync(actor, seed.OrganizationA, 10)).Projects.Count);
        }
        await Check(true);
        await test.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", seed.Alice);
        await Check(false);
        await test.ExecuteAsync("UPDATE weft.principals SET active=true WHERE id=$1", seed.Alice);
        await Check(true);
        await test.ExecuteAsync("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Alice);
        await Check(false);
        await test.ExecuteAsync("UPDATE weft.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Alice);
        await Check(true);
        await test.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice);
        await Check(false);
        await test.ExecuteAsync("UPDATE weft.project_memberships SET active=true,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice);
        await Check(true);
    }

    [PostgresFact]
    public async Task RoleChangesAffectFreshActionChecksWithoutCachedGrants()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        var reader = new PostgresProjectReader(database);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var scope = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        foreach (var (sqlRole, expectedRole, preview, upload, manage) in new[]
        {
            ("viewer", ProjectRole.Viewer, false, false, false),
            ("runner", ProjectRole.Runner, true, false, false),
            ("publisher", ProjectRole.Publisher, true, true, false),
            ("administrator", ProjectRole.Administrator, true, true, true),
            ("viewer", ProjectRole.Viewer, false, false, false)
        })
        {
            await test.ExecuteAsync("UPDATE weft.project_memberships SET role=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4", sqlRole, seed.OrganizationA, seed.ProjectA, seed.Alice);
            var access = Assert.IsType<ProjectAccess>(await reader.GetAccessAsync(actor, scope));
            Assert.Equal(expectedRole, access.Role);
            Assert.True(access.Can(ProjectAction.ReadMetadata));
            Assert.Equal(preview, access.Can(ProjectAction.SubmitPreview));
            Assert.Equal(upload, access.Can(ProjectAction.UploadRevision));
            Assert.Equal(manage, access.Can(ProjectAction.ManageMembership));
            Assert.False(access.Can((ProjectAction)999));
        }
    }

    [PostgresFact]
    public async Task CompositeForeignKeysAndChecksRejectForeignAncestryAndInvalidFacts()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        async Task Fails(string state, string sql, params object[] values)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => test.ExecuteAsync(sql, values));
            Assert.Equal(state, error.SqlState);
        }
        await Fails(PostgresErrorCodes.ForeignKeyViolation,
            "INSERT INTO weft.projects(organization_id,id,name,creator_principal_id) VALUES ($1,$2,'foreign creator',$3)", seed.OrganizationA, Guid.NewGuid(), seed.Bob);
        await Fails(PostgresErrorCodes.ForeignKeyViolation,
            "INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role) VALUES ($1,$2,$3,'viewer')", seed.OrganizationA, seed.ProjectB, seed.Alice);
        await Fails(PostgresErrorCodes.ForeignKeyViolation,
            "INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role) VALUES ($1,$2,$3,'viewer')", seed.OrganizationA, seed.ProjectA, seed.Bob);
        await Fails(PostgresErrorCodes.CheckViolation,
            "INSERT INTO weft.principals(id) VALUES ($1)", Guid.Empty);
        await Fails(PostgresErrorCodes.CheckViolation,
            "UPDATE weft.project_memberships SET role='owner' WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice);
        await Fails(PostgresErrorCodes.CheckViolation,
            "UPDATE weft.projects SET version=0 WHERE organization_id=$1 AND id=$2", seed.OrganizationA, seed.ProjectA);
        await Fails(PostgresErrorCodes.ForeignKeyViolation,
            "DELETE FROM weft.organization_memberships WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Alice);
        await Fails(PostgresErrorCodes.ForeignKeyViolation,
            "DELETE FROM weft.projects WHERE organization_id=$1 AND id=$2", seed.OrganizationA, seed.ProjectA);
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.projects"));
    }

    [PostgresFact]
    public async Task ParameterizedMetadataIsLiteralAndUuidPaginationIsBoundedAndUnambiguous()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        const string name = "literal '); DROP TABLE weft.principals; --";
        const string description = "UTF-8 café 🌱 $1 \\ \"quoted\"; SELECT pg_sleep(9);";
        await test.ExecuteAsync("UPDATE weft.projects SET name=$1,description=$2 WHERE organization_id=$3 AND id=$4", name, description, seed.OrganizationA, seed.ProjectA);
        var expected = new List<Guid> { seed.ProjectA };
        foreach (var project in new[]
        {
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            Guid.Parse("00000003-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("80000000-0000-0000-0000-000000000000"),
            Guid.Parse("00000004-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        })
        {
            await seed.AddProjectAsync(test, seed.OrganizationA, project, seed.Alice, "paged", "");
            await seed.AddMembershipAsync(test, seed.OrganizationA, project, seed.Alice, "viewer");
            expected.Add(project);
        }
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        var reader = new PostgresProjectReader(database);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var metadata = Assert.IsType<ProjectSummary>(await reader.GetVisibleProjectAsync(actor, new ProjectScope(seed.OrganizationA, seed.ProjectA)));
        Assert.Equal(name, metadata.Name);
        Assert.Equal(description, metadata.Description);
        Assert.Equal(4L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.principals"));
        var actual = new List<Guid>();
        Guid? cursor = null;
        for (var pageNumber = 0; pageNumber < 4; pageNumber++)
        {
            var page = await reader.ListVisibleProjectsAsync(actor, seed.OrganizationA, 2, cursor);
            Assert.InRange(page.Projects.Count, 0, 2);
            actual.AddRange(page.Projects.Select(project => project.Scope.ProjectId));
            if (page.NextProjectId is null)
                break;
            Assert.Equal(page.Projects[^1].Scope.ProjectId, page.NextProjectId);
            Assert.NotEqual(cursor, page.NextProjectId);
            cursor = page.NextProjectId;
        }
        Assert.Equal(expected.OrderBy(id => id.ToString("N"), StringComparer.Ordinal), actual);
        Assert.Equal(actual.Count, actual.Distinct().Count());
        Assert.DoesNotContain(seed.HiddenProjectA, actual);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ListVisibleProjectsAsync(actor, seed.OrganizationA, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ListVisibleProjectsAsync(actor, seed.OrganizationA, 101));
    }

    [PostgresFact]
    public async Task FreshDataSourcesAndSameOwnedClusterRestartPreserveOriginalRecords()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var scope = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        ProjectSummary before;
        await using (var database = new PostgresDatabase(test.RuntimeConnectionString))
            before = Assert.IsType<ProjectSummary>(await new PostgresProjectReader(database).GetVisibleProjectAsync(actor, scope));
        await using (var fresh = new PostgresDatabase(test.RuntimeConnectionString))
            Assert.Equal(before, await new PostgresProjectReader(fresh).GetVisibleProjectAsync(actor, scope));
        await test.DisposeAsync();
        await fixture.RestartAsync();
        await using var restarted = new PostgresDatabase(test.RuntimeConnectionString);
        var reader = new PostgresProjectReader(restarted);
        Assert.Equal(before, await reader.GetVisibleProjectAsync(actor, scope));
        Assert.Equal(ProjectRole.Viewer, (await reader.GetAccessAsync(actor, scope))!.Role);
        Assert.Equal(seed.ProjectA, Assert.Single((await reader.ListVisibleProjectsAsync(actor, seed.OrganizationA, 10)).Projects).Scope.ProjectId);
        await using var migration = new MigrationRunner(test.MigrationConnectionString);
        await migration.MigrateAsync();
        Assert.Equal(before, await reader.GetVisibleProjectAsync(actor, scope));
    }
}
