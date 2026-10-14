using System.Net;
using Weft.Domain;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProjectCreationHttpAuthorityTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task CommittedPrincipalAdmissionAndCapabilityRevocationDuringActualBodyReadDeniesWithoutWrites()
    {
        foreach (var authority in new[] { "principal", "admission", "capability" })
        {
            await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
            await using var api = await ProjectCreationHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1);
            var context = await api.ContextAsync();
            Assert.True(context.GetProperty("organizationActions").GetProperty("createProjects").GetBoolean());
            var token = context.GetProperty("requestToken").GetString()!;
            api.ResetObservations();
            var before = await test.CountsAsync();
            var gate = api.GateBody();
            var pending = ProjectCreationHttpTransactionSupport.PostAsync(api, token, Guid.NewGuid());
            var (revoke, restore, values) = Mutation(test, authority);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, api.CompletedAdmissionReads);
                Assert.Equal(0, api.CreatorCalls);
                Assert.Equal(0, api.BodyBytes);
                await api.AssertNoHeldTransactionsAsync();
                // Real owner UPDATE completes while the HTTP body is still gated;
                // no authority row lock or creator transaction spans body I/O.
                await test.Case.ExecuteAsync(revoke, values).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(pending.IsCompleted);
                gate.Release.TrySetResult();
                using var denied = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                await ProjectCreationHttpTransactionSupport.ErrorAsync(denied,
                    authority == "capability" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound,
                    authority == "capability" ? "permissionDenied" : "notFound");
                Assert.Equal(1, api.CreatorCalls);
                Assert.Equal(1, api.CompletedCreatorCalls);
                Assert.Equal(before, await test.CountsAsync());
                await gate.RequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await api.AssertNoHeldTransactionsAsync();
            }
            finally
            {
                gate.Release.TrySetResult();
                await ProjectCreationHttpTransactionSupport.DrainAsync(pending);
                await test.Case.ExecuteAsync(restore, values);
            }
            using var recovered = await ProjectCreationHttpTransactionSupport.PostAsync(api, await api.TokenAsync(), Guid.NewGuid());
            await ProjectCreationHttpTransactionSupport.SuccessAsync(recovered, HttpStatusCode.Created, "created",
                test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
            Assert.Equal(before.Select(x => x + 1), await test.CountsAsync());
        }
    }

    [PostgresFact]
    public async Task CachedPositiveContextAndValidTokenNeverAuthorizeAnExistingKeyAfterFreshRevocation()
    {
        foreach (var authority in new[] { "principal", "admission", "capability" })
        {
            await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
            await using var api = await ProjectCreationHttpTestSupport.StartAsync(test);
            var context = await api.ContextAsync();
            var token = context.GetProperty("requestToken").GetString()!;
            Assert.True(context.GetProperty("organizationActions").GetProperty("createProjects").GetBoolean());
            var key = Guid.NewGuid();
            using (var created = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, key))
                await ProjectCreationHttpTransactionSupport.SuccessAsync(created, HttpStatusCode.Created, "created",
                    test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
            var fact = await test.CreationSnapshotAsync(key);
            var before = await test.CountsAsync();
            var (revoke, restore, values) = Mutation(test, authority);
            await test.Case.ExecuteAsync(revoke, values);
            try
            {
                api.ResetObservations();
                using var denied = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, key,
                    new ProjectCreationMetadata("Unknown winner must remain private", ""));
                await ProjectCreationHttpTransactionSupport.ErrorAsync(denied,
                    authority == "capability" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound,
                    authority == "capability" ? "permissionDenied" : "notFound");
                Assert.Equal(0, api.BodyReads);
                Assert.Equal(0, api.CreatorCalls);
                using var freshContext = await api.Client.GetAsync("/api/v1/catalog/context");
                if (authority == "capability")
                {
                    var fresh = await ProjectCreationHttpTestSupport.JsonAsync(freshContext);
                    Assert.False(fresh.GetProperty("organizationActions").GetProperty("createProjects").GetBoolean());
                    Assert.True(fresh.GetProperty("features").GetProperty("createProjects").GetBoolean());
                }
                else await ProjectCreationHttpTransactionSupport.ErrorAsync(freshContext, HttpStatusCode.NotFound, "notFound");
                Assert.Equal(before, await test.CountsAsync());
                Assert.Equal(fact, await test.CreationSnapshotAsync(key));
            }
            finally { await test.Case.ExecuteAsync(restore, values); }
        }
    }

    [PostgresFact]
    public async Task ReplayedHistoricalAdministratorFactsNeverPromoteReactivateOrRecreateCurrentMembership()
    {
        foreach (var change in new[] { "demote", "deactivate", "delete" })
        {
            await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
            await using var api = await ProjectCreationHttpTestSupport.StartAsync(test);
            var token = await api.TokenAsync();
            var key = Guid.NewGuid();
            using var first = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, key);
            var original = await ProjectCreationHttpTransactionSupport.SuccessAsync(first, HttpStatusCode.Created, "created",
                test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
            var scope = ProjectCreationHttpTransactionSupport.Scope(original);
            var fact = await test.CreationSnapshotAsync(key);
            await test.Case.ExecuteAsync("""
                UPDATE weft.projects SET name='Current mutable project',description='Current description',
                    version=version+1,updated_at=clock_timestamp() WHERE organization_id=$1 AND id=$2
                """, scope.OrganizationId, scope.ProjectId);
            await test.Case.ExecuteAsync(change switch
            {
                "demote" => "UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                "deactivate" => "UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                "delete" => "DELETE FROM weft.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                _ => throw new InvalidOperationException()
            }, scope.OrganizationId, scope.ProjectId, test.Seed.Alice);
            var before = await test.CountsAsync();
            var current = await CurrentStateAsync(test, scope);
            using var replayResponse = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, key);
            var replay = await ProjectCreationHttpTransactionSupport.SuccessAsync(replayResponse, HttpStatusCode.OK, "replayed",
                test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
            Assert.Equal(original.GetRawText(), replay.GetRawText());
            Assert.Equal(before, await test.CountsAsync());
            Assert.Equal(current, await CurrentStateAsync(test, scope));
            Assert.Equal(fact, await test.CreationSnapshotAsync(key));
            using var projectResponse = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(scope));
            if (change == "demote")
            {
                var project = await ProjectCreationHttpTestSupport.JsonAsync(projectResponse);
                Assert.Equal("viewer", project.GetProperty("access").GetProperty("role").GetString());
                Assert.Equal("Current mutable project", project.GetProperty("name").GetString());
                Assert.False(project.GetProperty("access").GetProperty("actions").GetProperty("createProjects").GetBoolean());
            }
            else await ProjectCreationHttpTransactionSupport.ErrorAsync(projectResponse, HttpStatusCode.NotFound, "notFound");
        }
    }

    private static Task<string[]> CurrentStateAsync(ProjectCreationTestSupport test, ProjectScope scope) => test.Case.ScalarAsync<string[]>("""
        SELECT ARRAY[(SELECT to_jsonb(project)::text FROM weft.projects project WHERE organization_id=$1 AND id=$2),
            COALESCE((SELECT to_jsonb(member)::text FROM weft.project_memberships member
                WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3),'absent')]
        """, scope.OrganizationId, scope.ProjectId, test.Seed.Alice);

    private static (string Revoke, string Restore, object[] Values) Mutation(ProjectCreationTestSupport test, string authority) => authority switch
    {
        "principal" => ("UPDATE weft.principals SET active=false WHERE id=$1",
            "UPDATE weft.principals SET active=true WHERE id=$1", [test.Seed.Alice]),
        "admission" => ("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
            "UPDATE weft.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2",
            [test.OrganizationId, test.Seed.Alice]),
        "capability" => ("UPDATE weft.organization_project_creation_capabilities SET can_create_projects=false WHERE organization_id=$1 AND principal_id=$2",
            "UPDATE weft.organization_project_creation_capabilities SET can_create_projects=true WHERE organization_id=$1 AND principal_id=$2",
            [test.OrganizationId, test.Seed.Alice]),
        _ => throw new ArgumentOutOfRangeException(nameof(authority))
    };
}
