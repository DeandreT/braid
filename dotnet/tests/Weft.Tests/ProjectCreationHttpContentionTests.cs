using System.Net;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProjectCreationHttpContentionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task TwoActualHttpSameKeyRequestsObserveRealWaitsThenCreateAndReplayOrConflictWithoutOrphans()
    {
        foreach (var mode in new[] { "matching", "changed-intent", "other-actor" })
        {
            await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
            if (mode == "other-actor") await test.SetCapabilityAsync(true, test.Seed.Admitted);
            await using var relay = new ProjectCreationHttpCommitGate(test.Case.ProjectCreationConnectionString);
            await using var firstDatabase = new PostgresProjectCreationDatabase(relay.ConnectionString);
            var firstCreator = new PostgresProjectCreator(firstDatabase);
            await using var first = await ProjectCreationHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1,
                creator: firstCreator, admissionReader: firstCreator);
            await using var second = await ProjectCreationHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1,
                principal: mode == "other-actor" ? test.Seed.Admitted : test.Seed.Alice);
            var firstToken = await first.TokenAsync();
            var secondToken = await second.TokenAsync();
            var firstPid = await ProjectCreationTestSupport.BackendAsync(firstDatabase.DataSource);
            var secondPid = await ProjectCreationTestSupport.BackendAsync(second.CreationDatabase!.DataSource);
            Assert.NotEqual(firstPid, secondPid);
            var before = await test.CountsAsync();
            var key = Guid.NewGuid();
            var metadata = new ProjectCreationMetadata("Private HTTP winner", "Original exact description");
            first.ResetObservations();
            var body = first.GateBody();
            // Both contenders execute actual HTTP and the unchanged production
            // creator. The relay holds only the first frontend COMMIT after its
            // advisory read has completed; no facts or SQL result are injected.
            var firstPending = ProjectCreationHttpTransactionSupport.PostAsync(first, firstToken, key, metadata);
            Task<HttpResponseMessage>? secondPending = null;
            try
            {
                await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, first.CompletedAdmissionReads);
                Assert.Equal(0, first.CreatorCalls);
                relay.ArmNextCommit();
                body.Release.TrySetResult();
                await relay.CommitHeld.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(relay.ActualCommitForwarded);
                Assert.False(firstPending.IsCompleted);
                secondPending = ProjectCreationHttpTransactionSupport.PostAsync(second, secondToken, key,
                    mode == "changed-intent" ? new(metadata.Name, metadata.Description + " ") : metadata);
                await ProjectCreationTestSupport.WaitForBlockedAsync(test.Case, secondPid, firstPid, secondPending);
                Assert.Equal(before, await test.CountsAsync());
                relay.ReleaseCommit();
                using var firstResponse = await firstPending.WaitAsync(TimeSpan.FromSeconds(5));
                var original = await ProjectCreationHttpTransactionSupport.SuccessAsync(firstResponse, HttpStatusCode.Created,
                    "created", test.OrganizationId, test.Seed.Alice, metadata);
                using var secondResponse = await secondPending.WaitAsync(TimeSpan.FromSeconds(5));
                if (mode == "matching")
                {
                    var replay = await ProjectCreationHttpTransactionSupport.SuccessAsync(secondResponse, HttpStatusCode.OK,
                        "replayed", test.OrganizationId, test.Seed.Alice, metadata);
                    Assert.Equal(original.GetRawText(), replay.GetRawText());
                }
                else await ProjectCreationHttpTransactionSupport.ErrorAsync(secondResponse, HttpStatusCode.Conflict, "idempotencyConflict");
                await ProjectCreationHttpTransactionSupport.AssertCreatedRowsAsync(test, original, key, test.Seed.Alice, metadata, before);
                Assert.Equal(1, first.CreatorCalls);
                Assert.Equal(1, second.CreatorCalls);
                Assert.True(relay.ActualCommitForwarded);
            }
            finally
            {
                body.Release.TrySetResult();
                relay.ReleaseCommit();
                await ProjectCreationHttpTransactionSupport.DrainAsync(firstPending);
                if (secondPending is not null) await ProjectCreationHttpTransactionSupport.DrainAsync(secondPending);
            }
            await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(firstDatabase, firstPid);
            await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(second.CreationDatabase!, secondPid);
        }
    }

    [PostgresFact]
    public async Task DistinctKeysWithIdenticalNamesUseIndependentActualHttpTransactionsAndCommitExactlyTwice()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var first = await ProjectCreationHttpTestSupport.StartAsync(test);
        await using var second = await ProjectCreationHttpTestSupport.StartAsync(test);
        var firstToken = await first.TokenAsync();
        var secondToken = await second.TokenAsync();
        var firstPid = await ProjectCreationTestSupport.BackendAsync(first.CreationDatabase!.DataSource);
        var secondPid = await ProjectCreationTestSupport.BackendAsync(second.CreationDatabase!.DataSource);
        var firstKey = Guid.NewGuid();
        var secondKey = Guid.NewGuid();
        var before = await test.CountsAsync();
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case);
        var ownerPid = await ProjectCreationHttpTransactionSupport.BackendAsync(owner);
        await using var blocker = await owner.BeginTransactionAsync();
        await ProjectCreationHttpTransactionSupport.LockFactsAsync(owner, blocker);
        var firstPending = ProjectCreationHttpTransactionSupport.PostAsync(first, firstToken, firstKey);
        var secondPending = ProjectCreationHttpTransactionSupport.PostAsync(second, secondToken, secondKey);
        try
        {
            await ProjectCreationTestSupport.WaitForBlockedAsync(test.Case, firstPid, ownerPid, firstPending);
            await ProjectCreationTestSupport.WaitForBlockedAsync(test.Case, secondPid, ownerPid, secondPending);
            await ProjectCreationHttpTransactionSupport.AssertLateFactInsertLocksAsync(test.Case, firstPid);
            await ProjectCreationHttpTransactionSupport.AssertLateFactInsertLocksAsync(test.Case, secondPid);
            await blocker.RollbackAsync();
            using var firstResponse = await firstPending.WaitAsync(TimeSpan.FromSeconds(5));
            using var secondResponse = await secondPending.WaitAsync(TimeSpan.FromSeconds(5));
            var firstReceipt = await ProjectCreationHttpTransactionSupport.SuccessAsync(firstResponse, HttpStatusCode.Created,
                "created", test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
            var secondReceipt = await ProjectCreationHttpTransactionSupport.SuccessAsync(secondResponse, HttpStatusCode.Created,
                "created", test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
            Assert.NotEqual(ProjectCreationHttpTransactionSupport.Scope(firstReceipt), ProjectCreationHttpTransactionSupport.Scope(secondReceipt));
            Assert.Equal(before.Select(x => x + 2), await test.CountsAsync());
            Assert.Equal(2L, await test.Case.ScalarAsync<long>("""
                SELECT count(*) FROM weft.project_creations WHERE organization_id=$1 AND idempotency_key IN ($2,$3)
                """, test.OrganizationId, firstKey, secondKey));
        }
        finally
        {
            await blocker.DisposeAsync();
            await ProjectCreationHttpTransactionSupport.DrainAsync(firstPending);
            await ProjectCreationHttpTransactionSupport.DrainAsync(secondPending);
        }
        await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(first.CreationDatabase!, firstPid);
        await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(second.CreationDatabase!, secondPid);
    }

    [PostgresFact]
    public async Task ActualHttpContenderReplaysHeldProductionWinnerOnlyAfterObservedCommit()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var contenderPid = await ProjectCreationTestSupport.BackendAsync(api.CreationDatabase!.DataSource);
        var winnerPid = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        Assert.NotEqual(winnerPid, contenderPid);
        var before = await test.CountsAsync();
        var key = Guid.NewGuid();
        await using var winner = await test.Database.BeginTransactionAsync(test.OrganizationId);
        // This winner is held through the qualified persistence port, not HTTP.
        // The independently pooled contender below is actual HTTP.
        var original = await test.Creator.CreateInTransactionAsync(winner, test.Actor, test.OrganizationId, key,
            ProjectCreationTestSupport.DefaultMetadata);
        var pending = ProjectCreationHttpTransactionSupport.PostAsync(api, token, key);
        try
        {
            await ProjectCreationTestSupport.WaitForBlockedAsync(test.Case, contenderPid, winnerPid, pending);
            Assert.Equal(before, await test.CountsAsync());
            await winner.CommitAsync();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var receipt = await ProjectCreationHttpTransactionSupport.SuccessAsync(response, HttpStatusCode.OK, "replayed",
                test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
            Assert.Equal(original.Receipt!.Project, ProjectCreationHttpTransactionSupport.Scope(receipt));
            await ProjectCreationHttpTransactionSupport.AssertCreatedRowsAsync(test, receipt, key, test.Seed.Alice,
                ProjectCreationTestSupport.DefaultMetadata, before);
        }
        finally
        {
            await winner.DisposeAsync();
            await ProjectCreationHttpTransactionSupport.DrainAsync(pending);
        }
        await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(api.CreationDatabase!, contenderPid);
    }
}
