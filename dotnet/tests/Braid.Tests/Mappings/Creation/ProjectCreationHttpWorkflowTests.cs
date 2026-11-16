using System.Net;
using System.Text;
using Braid.Domain;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProjectCreationHttpWorkflowTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task CapableAdmittedActorWithoutProjectMembershipCreatesExactlyOneVisibleAdministratorProject()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture, grantCapability: false);
        await test.SetCapabilityAsync(true, test.Seed.Admitted);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test, principal: test.Seed.Admitted);
        var context = await api.ContextAsync();
        Assert.True(context.GetProperty("features").GetProperty("createProjects").GetBoolean());
        Assert.True(context.GetProperty("organizationActions").GetProperty("createProjects").GetBoolean());
        using (var list = await api.Client.GetAsync(ProjectCreationHttpTestSupport.CreatePath(test.OrganizationId)))
            Assert.Empty((await ProjectCreationHttpTestSupport.JsonAsync(list)).GetProperty("projects").EnumerateArray());
        using (var hidden = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(new(test.OrganizationId, test.Seed.HiddenProjectA))))
            await ProjectCreationHttpTransactionSupport.ErrorAsync(hidden, HttpStatusCode.NotFound, "notFound");
        var before = await test.CountsAsync();
        var key = Guid.NewGuid();
        var metadata = new ProjectCreationMetadata("\u3000Created 😀\u00a0", " \r\nExact description\t ");
        using var response = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(test.OrganizationId),
            context.GetProperty("requestToken").GetString(), key,
            ProjectCreationHttpTestSupport.Body("\u3000Created 😀\u00a0", metadata.Description));
        var receipt = await ProjectCreationHttpTransactionSupport.SuccessAsync(response, HttpStatusCode.Created,
            "created", test.OrganizationId, test.Seed.Admitted, metadata);
        await ProjectCreationHttpTransactionSupport.AssertCreatedRowsAsync(test, receipt, key, test.Seed.Admitted, metadata, before);
        Assert.Equal(1, api.CreatorCalls);
        var scope = ProjectCreationHttpTransactionSupport.Scope(receipt);
        using (var projectResponse = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(scope)))
        {
            var project = await ProjectCreationHttpTestSupport.JsonAsync(projectResponse);
            Assert.Equal(metadata.Name, project.GetProperty("name").GetString());
            Assert.Equal(metadata.Description, project.GetProperty("description").GetString());
            Assert.Equal("1", project.GetProperty("version").GetString());
            Assert.Equal("administrator", project.GetProperty("access").GetProperty("role").GetString());
            Assert.False(project.GetProperty("access").GetProperty("actions").GetProperty("createProjects").GetBoolean());
        }
        using (var listResponse = await api.Client.GetAsync(ProjectCreationHttpTestSupport.CreatePath(test.OrganizationId)))
        {
            var project = Assert.Single((await ProjectCreationHttpTestSupport.JsonAsync(listResponse)).GetProperty("projects").EnumerateArray());
            Assert.Equal(scope.ProjectId.ToString("D"), project.GetProperty("scope").GetProperty("projectId").GetString());
        }
        using var stillHidden = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(new(test.OrganizationId, test.Seed.HiddenProjectA)));
        await ProjectCreationHttpTransactionSupport.ErrorAsync(stillHidden, HttpStatusCode.NotFound, "notFound");
        await api.AssertNoHeldTransactionsAsync();
    }

    [PostgresFact]
    public async Task EquivalentTrimmedNameEscapedPropertiesAndJsonWhitespaceReplayExactFactsWithoutWrites()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var key = Guid.NewGuid();
        using var createdResponse = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, key);
        var original = await ProjectCreationHttpTransactionSupport.SuccessAsync(createdResponse, HttpStatusCode.Created,
            "created", test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
        var before = await test.CountsAsync();
        var fact = await test.CreationSnapshotAsync(key);
        using var replayResponse = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(test.OrganizationId), token, key,
            Encoding.UTF8.GetBytes(" { \"description\" : \"Exact description\", \"n\\u0061me\" : \"\\u3000Created project\\u00a0\" } "));
        var replay = await ProjectCreationHttpTransactionSupport.SuccessAsync(replayResponse, HttpStatusCode.OK,
            "replayed", test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
        Assert.Equal(original.GetRawText(), replay.GetRawText());
        Assert.Equal(before, await test.CountsAsync());
        Assert.Equal(fact, await test.CreationSnapshotAsync(key));
    }

    [PostgresFact]
    public async Task ChangedAcceptedIntentAndCapableOtherActorConflictWithoutDisclosingWinnerFacts()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await test.SetCapabilityAsync(true, test.Seed.Admitted);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var key = Guid.NewGuid();
        using (var created = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, key))
            await ProjectCreationHttpTransactionSupport.SuccessAsync(created, HttpStatusCode.Created, "created",
                test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
        var before = await test.CountsAsync();
        var fact = await test.CreationSnapshotAsync(key);
        foreach (var metadata in new[]
        {
            new ProjectCreationMetadata("Changed name", "Exact description"),
            new ProjectCreationMetadata("Created project", "Exact description "),
            new ProjectCreationMetadata("created project", "Exact description")
        })
        {
            using var changed = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, key, metadata);
            await ProjectCreationHttpTransactionSupport.ErrorAsync(changed, HttpStatusCode.Conflict, "idempotencyConflict");
        }
        await using var other = await ProjectCreationHttpTestSupport.StartAsync(test, principal: test.Seed.Admitted);
        using var crossActor = await ProjectCreationHttpTransactionSupport.PostAsync(other, await other.TokenAsync(), key);
        await ProjectCreationHttpTransactionSupport.ErrorAsync(crossActor, HttpStatusCode.Conflict, "idempotencyConflict");
        Assert.Equal(before, await test.CountsAsync());
        Assert.Equal(fact, await test.CreationSnapshotAsync(key));
    }

    [PostgresFact]
    public async Task SameKeyIsIndependentOnAnotherExplicitOrganizationHostAndForeignCurrentHostStaysOpaque()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await test.SetCapabilityAsync(true, organization: test.Seed.OrganizationB);
        await using var first = await ProjectCreationHttpTestSupport.StartAsync(test);
        await using var second = await ProjectCreationHttpTestSupport.StartAsync(test, organization: test.Seed.OrganizationB);
        var firstToken = await first.TokenAsync();
        var key = Guid.NewGuid();
        var before = await test.CountsAsync();
        using var firstResponse = await ProjectCreationHttpTransactionSupport.PostAsync(first, firstToken, key);
        var firstReceipt = await ProjectCreationHttpTransactionSupport.SuccessAsync(firstResponse, HttpStatusCode.Created,
            "created", test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
        first.ResetObservations();
        using (var foreign = await first.PostAsync(ProjectCreationHttpTestSupport.CreatePath(test.Seed.OrganizationB),
            firstToken, key, ProjectCreationHttpTestSupport.Body("Private other organization intent", "")))
            await ProjectCreationHttpTransactionSupport.ErrorAsync(foreign, HttpStatusCode.NotFound, "notFound");
        Assert.Equal(0, first.BodyReads);
        Assert.Equal(0, first.CreatorCalls);
        using var secondResponse = await ProjectCreationHttpTransactionSupport.PostAsync(second, await second.TokenAsync(), key);
        var secondReceipt = await ProjectCreationHttpTransactionSupport.SuccessAsync(secondResponse, HttpStatusCode.Created,
            "created", test.Seed.OrganizationB, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
        Assert.NotEqual(ProjectCreationHttpTransactionSupport.Scope(firstReceipt), ProjectCreationHttpTransactionSupport.Scope(secondReceipt));
        Assert.Equal(before.Select(x => x + 2), await test.CountsAsync());
    }

    [PostgresFact]
    public async Task ExistingAdministratorWithoutExplicitCapabilityCannotCreateOrAdvertiseOrganizationCreation()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture, grantCapability: false);
        await test.Case.ExecuteAsync("""
            UPDATE braid.project_memberships SET role='administrator',version=version+1
            WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3
            """, test.OrganizationId, test.Seed.ProjectA, test.Seed.Alice);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test);
        var context = await api.ContextAsync();
        Assert.True(context.GetProperty("features").GetProperty("createProjects").GetBoolean());
        Assert.False(context.GetProperty("organizationActions").GetProperty("createProjects").GetBoolean());
        using (var projectResponse = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(new(test.OrganizationId, test.Seed.ProjectA))))
        {
            var project = await ProjectCreationHttpTestSupport.JsonAsync(projectResponse);
            Assert.Equal("administrator", project.GetProperty("access").GetProperty("role").GetString());
            Assert.False(project.GetProperty("access").GetProperty("actions").GetProperty("createProjects").GetBoolean());
        }
        api.ResetObservations();
        var before = await test.CountsAsync();
        using var denied = await ProjectCreationHttpTransactionSupport.PostAsync(api, context.GetProperty("requestToken").GetString()!, Guid.NewGuid());
        await ProjectCreationHttpTransactionSupport.ErrorAsync(denied, HttpStatusCode.Forbidden, "permissionDenied");
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, api.CreatorCalls);
        Assert.Equal(before, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task ContextReadsFreshExplicitCapabilityWhileFeatureSupportAndExistingVisibilityStaySeparate()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture, grantCapability: false);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test);
        foreach (var allowed in new[] { false, true, false, true })
        {
            await test.SetCapabilityAsync(allowed);
            var context = await api.ContextAsync();
            Assert.True(context.GetProperty("features").GetProperty("createProjects").GetBoolean());
            Assert.Equal(allowed, context.GetProperty("organizationActions").GetProperty("createProjects").GetBoolean());
            using var hidden = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(new(test.OrganizationId, test.Seed.HiddenProjectA)));
            await ProjectCreationHttpTransactionSupport.ErrorAsync(hidden, HttpStatusCode.NotFound, "notFound");
        }
        Assert.Equal(new long[] { 3, 3, 0 }, await test.CountsAsync());
    }
}
