using System.Net;
using Npgsql;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProjectCreationHttpRecoveryTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ApiFreshPoolAndSameOwnedClusterRestartReplayTheOriginalHttpReceiptWithoutNewFacts()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var initial = await ProjectCreationHttpTestSupport.StartAsync(test);
        var key = Guid.NewGuid();
        using var createdResponse = await ProjectCreationHttpTransactionSupport.PostAsync(initial, await initial.TokenAsync(), key);
        var original = await ProjectCreationHttpTransactionSupport.SuccessAsync(createdResponse, HttpStatusCode.Created,
            "created", test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
        var counts = await test.CountsAsync();
        var fact = await test.CreationSnapshotAsync(key);
        await using (var restarted = await initial.RestartAsync())
        {
            // A fresh successful context issues this host's framework token; the
            // shared test key provider does not assert a hosted key lifecycle.
            using var replayResponse = await ProjectCreationHttpTransactionSupport.PostAsync(restarted, await restarted.TokenAsync(), key);
            var replay = await ProjectCreationHttpTransactionSupport.SuccessAsync(replayResponse, HttpStatusCode.OK,
                "replayed", test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
            Assert.Equal(original.GetRawText(), replay.GetRawText());
            Assert.Equal(counts, await test.CountsAsync());
            Assert.Equal(fact, await test.CreationSnapshotAsync(key));
        }
        await test.Database.DisposeAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var afterClusterRestart = await ProjectCreationHttpTestSupport.StartAsync(test);
        using var finalResponse = await ProjectCreationHttpTransactionSupport.PostAsync(afterClusterRestart,
            await afterClusterRestart.TokenAsync(), key);
        var final = await ProjectCreationHttpTransactionSupport.SuccessAsync(finalResponse, HttpStatusCode.OK,
            "replayed", test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
        Assert.Equal(original.GetRawText(), final.GetRawText());
        using var read = await afterClusterRestart.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(ProjectCreationHttpTransactionSupport.Scope(original)));
        var project = await ProjectCreationHttpTestSupport.JsonAsync(read);
        Assert.Equal("administrator", project.GetProperty("access").GetProperty("role").GetString());
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case);
        await using (var count = new NpgsqlCommand("""
            SELECT ARRAY[(SELECT count(*) FROM braid.projects),(SELECT count(*) FROM braid.project_memberships),
                (SELECT count(*) FROM braid.project_creations)]
            """, owner))
            Assert.Equal(counts, (long[])(await count.ExecuteScalarAsync())!);
        await using (var snapshot = new NpgsqlCommand("""
            SELECT to_jsonb(fact)::text FROM braid.project_creations fact WHERE organization_id=$1 AND idempotency_key=$2
            """, owner))
        {
            snapshot.Parameters.AddWithValue(test.OrganizationId);
            snapshot.Parameters.AddWithValue(key);
            Assert.Equal(fact, await snapshot.ExecuteScalarAsync());
        }
        await afterClusterRestart.AssertNoHeldTransactionsAsync();
    }

    [PostgresFact]
    public async Task ActualRequestAbortWhileBodyIsGatedReleasesTheOnlyPermitWithoutCallingCreator()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1,
            transportObserver: new RevisionUploadTransportObserver());
        var token = await api.TokenAsync();
        var creatorPid = await ProjectCreationTestSupport.BackendAsync(api.CreationDatabase!.DataSource);
        api.ResetObservations();
        var before = await test.CountsAsync();
        var gate = api.GateBody();
        using var interrupted = await api.StartWithheldBodyPostAsync(
            ProjectCreationHttpTestSupport.CreatePath(test.OrganizationId), token, Guid.NewGuid());
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, api.CompletedAdmissionReads);
            Assert.Equal(0, api.CreatorCalls);
            Assert.Equal(0, api.BodyBytes);
            await api.AssertNoHeldTransactionsAsync();
            gate.Release.TrySetResult();
            Assert.Equal(3, await gate.FirstReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await gate.SecondReadPending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3L, gate.SuccessfulBytes);
            Assert.Equal(2, gate.InnerReadEntries);
            Assert.Equal(3, api.BodyBytes);
            Assert.Equal(0, api.CreatorCalls);
            await api.AssertNoHeldTransactionsAsync();
            // The real prefix has been delivered and the next actual network
            // read is pending before this owned peer resets its connection.
            interrupted.Abort();
            await gate.RequestAborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await gate.RequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var finish = await gate.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var settlementDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var settlement = await interrupted.WaitForTransportSettlementAsync(settlementDeadline.Token);
            using var diagnostic = System.Text.Json.JsonDocument.Parse(gate.DiagnosticSnapshot());
            var diagnosticMessage = System.Text.Json.JsonSerializer.Serialize(new
            {
                request = diagnostic.RootElement,
                transport = settlement
            });
            Assert.False(finish.ResponseHasStarted, diagnosticMessage);
            Assert.True(settlement.ConnectionPipelineSettled, diagnosticMessage);
            Assert.True(settlement.EncodedBytes == 0, diagnosticMessage);
            Assert.True(settlement.DeliveredBytes is null, diagnosticMessage);
            Assert.Equal(0, api.CreatorCalls);
            Assert.Equal(3, api.BodyBytes);
            Assert.Equal(3L, gate.SuccessfulBytes);
            Assert.Equal(before, await test.CountsAsync());
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(System.Text.Json.JsonSerializer.Serialize(new
            {
                Request = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(gate.DiagnosticSnapshot()),
                Transport = new
                {
                    EncodedBytes = interrupted.Transport?.EncodedBytes,
                    ConnectionPipelineSettled = interrupted.Transport?.PipelineSettled.Task.IsCompletedSuccessfully ?? false,
                    DeliveredBytes = (long?)null
                }
            }), exception);
        }
        finally
        {
            interrupted.Abort();
            gate.Release.TrySetResult();
        }
        await api.AssertNoHeldTransactionsAsync();
        await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(api.CreationDatabase!, creatorPid);
        using var recovered = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, Guid.NewGuid());
        await ProjectCreationHttpTransactionSupport.SuccessAsync(recovered, HttpStatusCode.Created, "created",
            test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
        Assert.Equal(before.Select(x => x + 1), await test.CountsAsync());
        await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(api.CreationDatabase!, creatorPid);
    }

    [PostgresFact]
    public async Task ActualHttpAbortAtObservedLateFactInsertRollsBackThreeWritesAndRecoversSameBackendAndPermit()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1);
        var token = await api.TokenAsync();
        var creatorPid = await ProjectCreationTestSupport.BackendAsync(api.CreationDatabase!.DataSource);
        var before = await test.CountsAsync();
        var key = Guid.NewGuid();
        var observation = api.ObserveNextPost();
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case);
        var blockerPid = await ProjectCreationHttpTransactionSupport.BackendAsync(owner);
        await using var blocker = await owner.BeginTransactionAsync();
        await ProjectCreationHttpTransactionSupport.LockFactsAsync(owner, blocker);
        using var cancellation = new CancellationTokenSource();
        await using var blockingObserver = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case, creatorPid, blockerPid);
        var pending = ProjectCreationHttpTransactionSupport.PostAsync(api, token, key, cancellationToken: cancellation.Token);
        try
        {
            await blockingObserver.WaitForBlockedAsync(pending);
            await ProjectCreationHttpTransactionSupport.AssertLateFactInsertLocksAsync(test.Case, creatorPid);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await observation.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await observation.RequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observation.AbortedAtFinish);
            Assert.Equal(1, api.CompletedCreatorCalls);
        }
        finally
        {
            cancellation.Cancel();
            await blocker.DisposeAsync();
            await ProjectCreationHttpTransactionSupport.DrainAsync(pending);
        }
        Assert.Equal(before, await test.CountsAsync());
        await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(api.CreationDatabase!, creatorPid);
        using var recovered = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, key);
        var receipt = await ProjectCreationHttpTransactionSupport.SuccessAsync(recovered, HttpStatusCode.Created, "created",
            test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
        await ProjectCreationHttpTransactionSupport.AssertCreatedRowsAsync(test, receipt, key, test.Seed.Alice,
            ProjectCreationTestSupport.DefaultMetadata, before);
        await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(api.CreationDatabase!, creatorPid);
    }

    [PostgresFact]
    public async Task UnchangedActualSqlLockDeadlineReturnsClosedFailureAndRecoversSameKeyPoolAndPermit()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1);
        var token = await api.TokenAsync();
        var creatorPid = await ProjectCreationTestSupport.BackendAsync(api.CreationDatabase!.DataSource);
        var before = await test.CountsAsync();
        var key = Guid.NewGuid();
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case);
        var blockerPid = await ProjectCreationHttpTransactionSupport.BackendAsync(owner);
        await using var blocker = await owner.BeginTransactionAsync();
        await ProjectCreationHttpTransactionSupport.LockFactsAsync(owner, blocker);
        await using var blockingObserver = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case, creatorPid, blockerPid);
        var pending = ProjectCreationHttpTransactionSupport.PostAsync(api, token, key);
        try
        {
            await blockingObserver.WaitForBlockedAsync(pending);
            await ProjectCreationHttpTransactionSupport.AssertLateFactInsertLocksAsync(test.Case, creatorPid);
            using var failed = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await ProjectCreationHttpTransactionSupport.ErrorAsync(failed, HttpStatusCode.InternalServerError, "persistenceFailure");
        }
        finally
        {
            await blocker.DisposeAsync();
            await ProjectCreationHttpTransactionSupport.DrainAsync(pending);
        }
        Assert.Equal(before, await test.CountsAsync());
        await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(api.CreationDatabase!, creatorPid);
        using var recovered = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, key);
        await ProjectCreationHttpTransactionSupport.SuccessAsync(recovered, HttpStatusCode.Created, "created",
            test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
        Assert.Equal(before.Select(x => x + 1), await test.CountsAsync());
    }

    [PostgresFact]
    public async Task ActualCreatorRoleAndSourceFailuresNeverBecomeFalseContextOrDenialAndRecoverWithoutWrites()
    {
        foreach (var mutation in new[] { "role", "source" })
        {
            await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
            await using var api = await ProjectCreationHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1);
            var token = await api.TokenAsync();
            var creatorPid = await ProjectCreationTestSupport.BackendAsync(api.CreationDatabase!.DataSource);
            var before = await test.CountsAsync();
            var corrupt = mutation == "role" ? "GRANT SELECT ON braid.project_creations TO braid_project_creator" :
                "ALTER TABLE braid.project_creations DISABLE ROW LEVEL SECURITY";
            var restore = mutation == "role" ? "REVOKE SELECT ON braid.project_creations FROM braid_project_creator" :
                "ALTER TABLE braid.project_creations ENABLE ROW LEVEL SECURITY";
            await test.Case.ExecuteAsync(corrupt);
            try
            {
                using (var failedContext = await api.Client.GetAsync("/api/v1/catalog/context"))
                    await ProjectCreationHttpTransactionSupport.ErrorAsync(failedContext, HttpStatusCode.InternalServerError, "persistenceFailure");
                api.ResetObservations();
                using var failedPost = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, Guid.NewGuid());
                await ProjectCreationHttpTransactionSupport.ErrorAsync(failedPost, HttpStatusCode.InternalServerError, "persistenceFailure");
                Assert.Equal(0, api.BodyReads);
                Assert.Equal(0, api.CreatorCalls);
                Assert.Equal(before, await test.CountsAsync());
            }
            finally { await test.Case.ExecuteAsync(restore); }
            Assert.True((await api.ContextAsync()).GetProperty("organizationActions").GetProperty("createProjects").GetBoolean());
            await ProjectCreationHttpTransactionSupport.AssertUnboundAsync(api.CreationDatabase!, creatorPid);
            using var recovered = await ProjectCreationHttpTransactionSupport.PostAsync(api, token, Guid.NewGuid());
            await ProjectCreationHttpTransactionSupport.SuccessAsync(recovered, HttpStatusCode.Created, "created",
                test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
            Assert.Equal(before.Select(x => x + 1), await test.CountsAsync());
        }
    }

    [PostgresFact]
    public async Task WrongLoginWrongPasswordAndUnavailableCreatorLoginNeverFallbackToCatalogCredentials()
    {
        foreach (var failure in new[] { "wrong-login", "wrong-password", "unavailable-login" })
        {
            await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
            await using var correct = await ProjectCreationHttpTestSupport.StartAsync(test);
            var token = await correct.TokenAsync();
            var key = Guid.NewGuid();
            var before = await test.CountsAsync();
            var connection = new NpgsqlConnectionStringBuilder(failure == "wrong-login"
                ? test.Case.CatalogConnectionString : test.Case.ProjectCreationConnectionString);
            if (failure == "wrong-password") connection.Password = "explicitly-wrong-test-only-password";
            await using var wrongDatabase = new PostgresProjectCreationDatabase(connection.ConnectionString);
            var wrongCreator = new PostgresProjectCreator(wrongDatabase);
            if (failure == "unavailable-login") await test.Case.BootstrapAsync("ALTER ROLE braid_project_creator NOLOGIN");
            try
            {
                await using var wrong = await ProjectCreationHttpTestSupport.StartAsync(test, creator: wrongCreator, admissionReader: wrongCreator);
                using (var failedContext = await wrong.Client.GetAsync("/api/v1/catalog/context"))
                    await ProjectCreationHttpTransactionSupport.ErrorAsync(failedContext, HttpStatusCode.InternalServerError, "persistenceFailure");
                // Shared explicit ephemeral test keys and the same actor/org
                // let the real boundary validate this cookie/token on this host.
                using var authenticated = wrong.NewClient(correct.Cookies);
                using var failedPost = await wrong.PostAsync(ProjectCreationHttpTestSupport.CreatePath(test.OrganizationId),
                    token, key, ProjectCreationHttpTestSupport.Body("Created project", "Exact description"), client: authenticated);
                await ProjectCreationHttpTransactionSupport.ErrorAsync(failedPost, HttpStatusCode.InternalServerError, "persistenceFailure");
                Assert.Equal(0, wrong.BodyReads);
                Assert.Equal(0, wrong.CreatorCalls);
                Assert.Equal(before, await test.CountsAsync());
            }
            finally
            {
                if (failure == "unavailable-login") await test.Case.BootstrapAsync("ALTER ROLE braid_project_creator LOGIN");
            }
            using var recovered = await ProjectCreationHttpTransactionSupport.PostAsync(correct, token, key);
            await ProjectCreationHttpTransactionSupport.SuccessAsync(recovered, HttpStatusCode.Created, "created",
                test.OrganizationId, test.Seed.Alice, ProjectCreationTestSupport.DefaultMetadata);
            Assert.Equal(before.Select(x => x + 1), await test.CountsAsync());
        }
    }
}
