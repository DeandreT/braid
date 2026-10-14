using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using Xunit.Abstractions;

namespace Weft.Tests;

/// <summary>Actual HTTP admission, original-actor recovery and current-authority races over qualified C2 ports.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RevisionUploadHttpRecoveryTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [RevisionIngestionFact]
    public async Task SameKeyInProgressHasOwnReceiptLocationAndSaturationRefusesBeforeSourceWhileReceiptReadsRemainAvailable()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 2);
        var token = await api.TokenAsync();
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        var firstGate = new RevisionIngestionGate();
        var secondGate = new RevisionIngestionGate();
        test.BackendPort.AfterValidationGate = call => call == 1 ? firstGate : call == 2 ? secondGate : null;
        var key = Guid.NewGuid();
        var first = api.PostAsync(path, token, key, bytes);
        Task<HttpResponseMessage>? second = null;
        try
        {
            await firstGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var operation = await test.Case.ScalarAsync<Guid>("SELECT operation_id FROM weft.revision_ingestions WHERE idempotency_key=$1", key);
            var receiptPath = RevisionUploadHttpTestSupport.ReceiptPath(test.Project, operation);
            using var inProgressResponse = await api.PostAsync(path, token, key, bytes, chunked: true);
            var inProgress = await RevisionUploadHttpTestSupport.ResultAsync(inProgressResponse, HttpStatusCode.Accepted, "inProgress");
            Assert.Equal(operation, RevisionUploadHttpTestSupport.Operation(inProgress));
            Assert.Equal(receiptPath, inProgressResponse.Headers.Location?.OriginalString);
            Assert.Equal(JsonValueKind.Null, inProgress.GetProperty("committedRevisionUrl").ValueKind);
            Assert.Equal(1, test.BackendPort.Calls);
            second = api.PostAsync(path, token, Guid.NewGuid(), bytes);
            await secondGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var calls = api.ServiceCalls;
            var reads = api.BodyReads;
            var busy = await api.HeadersOnlyPostAsync(path, token, Guid.NewGuid().ToString("D"));
            RevisionUploadHttpTestSupport.AssertRawError(busy, HttpStatusCode.ServiceUnavailable, "ingestionAdmissionBusy");
            Assert.Equal(calls, api.ServiceCalls);
            Assert.Equal(reads, api.BodyReads);
            using var poll = await api.Client.GetAsync(receiptPath);
            var receipt = await CatalogHttpTestSupport.JsonAsync(poll);
            RevisionUploadHttpTestSupport.AssertSafeReceipt(receipt);
            Assert.Equal(operation.ToString("D"), receipt.GetProperty("operationId").GetString());
            Assert.Equal(1, receipt.GetProperty("claimCount").GetInt32());
            Assert.True(poll.Headers.CacheControl?.NoStore);
            Assert.Null(poll.Headers.Location);
            Assert.Null(poll.Headers.ETag);
            using var busyResume = await api.PostAsync(receiptPath + "/resume", token, null, []);
            await RevisionUploadHttpTestSupport.AssertErrorAsync(busyResume, HttpStatusCode.ServiceUnavailable, "ingestionAdmissionBusy");
            Assert.Equal(calls, api.ServiceCalls);
            Assert.Equal(reads, api.BodyReads);
            await test.AssertNoHeldTransactionsAsync();
            await api.AssertNoHeldCatalogTransactionAsync();
            firstGate.Release(); secondGate.Release();
            using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(15));
            using var secondResponse = await second.WaitAsync(TimeSpan.FromSeconds(15));
            await RevisionUploadHttpTestSupport.ResultAsync(firstResponse, HttpStatusCode.OK, "committedValid");
            await RevisionUploadHttpTestSupport.ResultAsync(secondResponse, HttpStatusCode.OK, "committedValid");
        }
        finally
        {
            firstGate.Release(); secondGate.Release(); test.BackendPort.AfterValidationGate = null;
            try { using var completed = await first.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { if (second is not null) { using var completed = await second.WaitAsync(TimeSpan.FromSeconds(20)); } }
        }
        Assert.Equal(2, test.BackendPort.Calls);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        using var next = await api.PostAsync(path, token, Guid.NewGuid(), bytes);
        await RevisionUploadHttpTestSupport.ResultAsync(next, HttpStatusCode.OK, "committedValid");
    }

    [RevisionIngestionFact]
    public async Task ActualEqualHeadContendersHaveOneCommitAndOneSafeConflictWithoutChangingMappingMetadata()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        using var initialResponse = await api.PostAsync(RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery(), token, Guid.NewGuid(), bytes);
        var initial = await RevisionUploadHttpTestSupport.ResultAsync(initialResponse, HttpStatusCode.OK, "committedValid");
        var mapping = RevisionUploadHttpTestSupport.Mapping(initial);
        var head = RevisionUploadHttpTestSupport.Revision(initial);
        var path = RevisionUploadHttpTestSupport.AppendPath(mapping) + $"?head=equal&expectedHeadRevisionId={head:D}";
        var aGate = new RevisionIngestionGate();
        var bGate = new RevisionIngestionGate();
        test.BackendPort.AfterValidationGate = call => call == 2 ? aGate : call == 3 ? bGate : null;
        var keyA = Guid.NewGuid();
        var a = api.PostAsync(path, token, keyA, bytes);
        Task<HttpResponseMessage>? b = null;
        JsonElement aResult;
        JsonElement bResult;
        try
        {
            await aGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            b = api.PostAsync(path, token, Guid.NewGuid(), bytes, chunked: true);
            await bGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            aGate.Release();
            using var aResponse = await a.WaitAsync(TimeSpan.FromSeconds(15));
            aResult = await RevisionUploadHttpTestSupport.ResultAsync(aResponse, HttpStatusCode.OK, "committedValid");
            bGate.Release();
            using var bResponse = await b.WaitAsync(TimeSpan.FromSeconds(15));
            bResult = await RevisionUploadHttpTestSupport.ResultAsync(bResponse, HttpStatusCode.Conflict, "headConflict");
            Assert.Equal("2", aResult.GetProperty("receipt").GetProperty("committedSequence").GetString());
            Assert.Equal("headConflict", bResult.GetProperty("receipt").GetProperty("phase").GetString());
            Assert.Equal(JsonValueKind.Null, bResult.GetProperty("receipt").GetProperty("committedSequence").ValueKind);
            Assert.Equal(JsonValueKind.Null, bResult.GetProperty("committedRevisionUrl").ValueKind);
            Assert.NotEqual(RevisionUploadHttpTestSupport.Revision(aResult), RevisionUploadHttpTestSupport.Revision(bResult));
        }
        finally
        {
            aGate.Release(); bGate.Release(); test.BackendPort.AfterValidationGate = null;
            try { using var completed = await a.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { if (b is not null) { using var completed = await b.WaitAsync(TimeSpan.FromSeconds(20)); } }
        }
        api.ResetObservations();
        using var contradiction = await api.PostAsync(RevisionUploadHttpTestSupport.AppendPath(mapping) + "?head=any", token, keyA, bytes);
        await RevisionUploadHttpTestSupport.ResultAsync(contradiction, HttpStatusCode.Conflict, "idempotencyConflict");
        Assert.Equal(bytes.LongLength, api.BodyBytes);
        Assert.Equal(3, test.BackendPort.Calls);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT head_sequence FROM weft.mappings WHERE id=$1", mapping.MappingId));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        Assert.Equal("HTTP native integration", await test.Case.ScalarAsync<string>("SELECT name FROM weft.mappings WHERE id=$1", mapping.MappingId));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT version FROM weft.mappings WHERE id=$1", mapping.MappingId));
    }

    [RevisionIngestionFact]
    public async Task MissingOriginalPhysicalObjectNeedsSourceAndMatchingHttpResubmissionRetainsTheReservedIdentity()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var key = Guid.NewGuid();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        test.StorePort.FailAfterNextRealPublication();
        using var failedResponse = await api.PostAsync(path, token, key, bytes);
        var failed = await RevisionUploadHttpTestSupport.ResultAsync(failedResponse, HttpStatusCode.ServiceUnavailable, "retryableFailure");
        Assert.Equal("storageIo", failed.GetProperty("failure").GetString());
        var operation = RevisionUploadHttpTestSupport.Operation(failed);
        var facts = (await test.PrivateReceiptAsync(operation))!.Facts;
        Assert.Equal(bytes, File.ReadAllBytes(test.PhysicalRoot.FinalPath(facts.NativeObject)));
        File.Delete(test.PhysicalRoot.FinalPath(facts.NativeObject));
        var resumePath = RevisionUploadHttpTestSupport.ReceiptPath(test.Project, operation) + "/resume";
        api.ResetObservations();
        var knownNonempty = await api.HeadersOnlyPostAsync(resumePath, token, null, contentLength: 4096, contentType: null);
        RevisionUploadHttpTestSupport.AssertRawError(knownNonempty, HttpStatusCode.BadRequest, "invalidRequest");
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, api.ServiceCalls);
        using var chunkedNonempty = await api.PostAsync(resumePath, token, null, new byte[8192], chunked: true, contentType: null);
        await RevisionUploadHttpTestSupport.AssertErrorAsync(chunkedNonempty, HttpStatusCode.BadRequest, "invalidRequest");
        Assert.Equal(1L, api.BodyBytes);
        Assert.Equal(0, api.ServiceCalls);
        using var missingResponse = await api.PostAsync(resumePath, token, null, [], chunked: true, contentType: null);
        var missing = await RevisionUploadHttpTestSupport.ResultAsync(missingResponse, HttpStatusCode.Conflict, "needsSource");
        Assert.Equal("needsSource", missing.GetProperty("failure").GetString());
        Assert.Equal(2, missing.GetProperty("receipt").GetProperty("claimCount").GetInt32());
        Assert.Equal(0, test.BackendPort.Calls);
        using var recoveredResponse = await api.PostAsync(path, token, key, bytes, chunked: true);
        var recovered = await RevisionUploadHttpTestSupport.ResultAsync(recoveredResponse, HttpStatusCode.OK, "committedValid");
        Assert.Equal(operation, RevisionUploadHttpTestSupport.Operation(recovered));
        Assert.Equal(facts.FutureRevision.RevisionId, RevisionUploadHttpTestSupport.Revision(recovered));
        Assert.Equal(facts, (await test.PrivateReceiptAsync(operation))!.Facts);
        Assert.Equal(3, recovered.GetProperty("receipt").GetProperty("claimCount").GetInt32());
        Assert.Equal(bytes, await test.PhysicalStore.ReadVerifiedAsync(facts.FutureRevision, facts.NativeObject));
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Empty(test.PhysicalRoot.AttemptFiles());
    }

    [RevisionIngestionFact]
    public async Task ActualCorruptPrivateObjectReportsIntegrityFailureAndIsNeverOverwrittenByResumeOrMatchingSource()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var key = Guid.NewGuid();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        test.StorePort.FailAfterNextRealPublication();
        using var firstResponse = await api.PostAsync(path, token, key, bytes);
        var first = await RevisionUploadHttpTestSupport.ResultAsync(firstResponse, HttpStatusCode.ServiceUnavailable, "retryableFailure");
        var operation = RevisionUploadHttpTestSupport.Operation(first);
        var facts = (await test.PrivateReceiptAsync(operation))!.Facts;
        var ownedPath = test.PhysicalRoot.FinalPath(facts.NativeObject);
        var damaged = bytes[..^1];
        File.SetUnixFileMode(ownedPath, PrivateStorageTestRoot.AttemptMode);
        File.WriteAllBytes(ownedPath, damaged);
        File.SetUnixFileMode(ownedPath, PrivateStorageTestRoot.FinalMode);
        using var resumeResponse = await api.PostAsync(RevisionUploadHttpTestSupport.ReceiptPath(test.Project, operation) + "/resume", token, null, []);
        var resume = await RevisionUploadHttpTestSupport.ResultAsync(resumeResponse, HttpStatusCode.ServiceUnavailable, "retryableFailure");
        Assert.Equal("storageIntegrity", resume.GetProperty("failure").GetString());
        using var resubmitResponse = await api.PostAsync(path, token, key, bytes);
        var resubmit = await RevisionUploadHttpTestSupport.ResultAsync(resubmitResponse, HttpStatusCode.ServiceUnavailable, "retryableFailure");
        Assert.Equal("storageIntegrity", resubmit.GetProperty("failure").GetString());
        Assert.Equal(damaged, File.ReadAllBytes(ownedPath));
        Assert.Equal(facts, (await test.PrivateReceiptAsync(operation))!.Facts);
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [RevisionIngestionFact]
    public async Task RestartedNonterminalOperationRequiresItsExactReservedWorkerProfileWithoutCurrentFallback()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var key = Guid.NewGuid();
        Guid operation;
        test.StorePort.FailAfterNextRealPublication();
        await using (var firstApi = await RevisionUploadHttpTestSupport.StartAsync(test))
        {
            using var response = await firstApi.PostAsync(RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery(),
                await firstApi.TokenAsync(), key, bytes);
            var failed = await RevisionUploadHttpTestSupport.ResultAsync(response, HttpStatusCode.ServiceUnavailable, "retryableFailure");
            operation = RevisionUploadHttpTestSupport.Operation(failed);
        }
        var reserved = (await test.PrivateReceiptAsync(operation))!.Facts;
        await using var secondApi = await RevisionUploadHttpTestSupport.StartAsync(test, test.NewService(new IngestionBackendRegistry([])));
        using var resume = await secondApi.PostAsync(RevisionUploadHttpTestSupport.ReceiptPath(test.Project, operation) + "/resume",
            await secondApi.TokenAsync(), null, []);
        var result = await RevisionUploadHttpTestSupport.ResultAsync(resume, HttpStatusCode.ServiceUnavailable, "retryableFailure");
        Assert.Equal("reservedProfileUnavailable", result.GetProperty("failure").GetString());
        Assert.Equal(2, result.GetProperty("receipt").GetProperty("claimCount").GetInt32());
        Assert.Equal(reserved, (await test.PrivateReceiptAsync(operation))!.Facts);
        Assert.Equal(bytes, File.ReadAllBytes(test.PhysicalRoot.FinalPath(reserved.NativeObject)));
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [RevisionIngestionFact]
    public async Task FreshAuthorityAfterActualHttpBodyGateStopsReservationAndReleasesAdmissionForTheNextUpload()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1);
        var token = await api.TokenAsync();
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        var gate = api.GateBody();
        var pending = api.PostAsync(path, token, Guid.NewGuid(), bytes);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("project-read-completed", api.Events.First());
            Assert.Equal(1, api.ServiceCalls);
            Assert.Equal(0, api.BodyBytes);
            await test.AssertNoHeldTransactionsAsync();
            await api.AssertNoHeldCatalogTransactionAsync();
            await RevisionUploadHttpIntegrationTests.RoleAsync(test, "viewer");
            gate.Release.TrySetResult();
            using var denied = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await RevisionUploadHttpTestSupport.ResultAsync(denied, HttpStatusCode.NotFound, "notAuthorizedOrNotFound");
            Assert.Equal(JsonValueKind.Null, result.GetProperty("receipt").ValueKind);
            Assert.Null(denied.Headers.Location);
            await gate.RequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Release.TrySetResult();
            try { using var completed = await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { api.ClearBodyGate(); await RevisionUploadHttpIntegrationTests.RoleAsync(test, "publisher"); }
        }
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Equal(0, test.StorePort.PublishCalls);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
        using var next = await api.PostAsync(path, token, Guid.NewGuid(), bytes);
        await RevisionUploadHttpTestSupport.ResultAsync(next, HttpStatusCode.OK, "committedValid");
    }

    [RevisionIngestionFact]
    public async Task RevocationWhileActualWorkerWaitsForProtocolEofPreventsPublicationAndAnotherPublisherCannotAdoptReceipt()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        await using var otherApi = await RevisionUploadHttpTestSupport.StartAsync(test, principal: test.Seed.Admitted);
        var otherToken = await otherApi.TokenAsync();
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        foreach (var (revoke, restore, values) in new[]
        {
            ("UPDATE weft.principals SET active=false WHERE id=$1", "UPDATE weft.principals SET active=true WHERE id=$1", new object[] { test.Seed.Alice }),
            ("UPDATE weft.organization_memberships SET active=false WHERE principal_id=$1 AND organization_id=$2", "UPDATE weft.organization_memberships SET active=true WHERE principal_id=$1 AND organization_id=$2", new object[] { test.Seed.Alice, test.Project.OrganizationId }),
            ("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1 AND organization_id=$2 AND project_id=$3", "UPDATE weft.project_memberships SET active=true,version=version+1 WHERE principal_id=$1 AND organization_id=$2 AND project_id=$3", new object[] { test.Seed.Alice, test.Project.OrganizationId, test.Project.ProjectId }),
            ("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1 AND organization_id=$2 AND project_id=$3", "UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1 AND organization_id=$2 AND project_id=$3", new object[] { test.Seed.Alice, test.Project.OrganizationId, test.Project.ProjectId })
        })
        {
            var gate = new RevisionIngestionGate();
            test.Launcher.NextProtocolGate = gate;
            var key = Guid.NewGuid();
            var pending = api.PostAsync(path, token, key, bytes);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await test.AssertNoHeldTransactionsAsync();
                await api.AssertNoHeldCatalogTransactionAsync();
                var operation = await test.Case.ScalarAsync<Guid>("SELECT operation_id FROM weft.revision_ingestions WHERE idempotency_key=$1", key);
                var receiptPath = RevisionUploadHttpTestSupport.ReceiptPath(test.Project, operation);
                using var hiddenOther = await otherApi.Client.GetAsync(receiptPath);
                await RevisionUploadHttpTestSupport.AssertErrorAsync(hiddenOther, HttpStatusCode.NotFound, "notFound");
                var otherResume = await otherApi.HeadersOnlyPostAsync(receiptPath + "/resume", otherToken, null);
                RevisionUploadHttpTestSupport.AssertRawError(otherResume, HttpStatusCode.NotFound, "notFound");
                Assert.Equal(0, otherApi.ServiceCalls);
                Assert.Equal(0, otherApi.BodyReads);
                await test.Case.ExecuteAsync(revoke, values);
                gate.Release();
                using var denied = await pending.WaitAsync(TimeSpan.FromSeconds(15));
                var result = await RevisionUploadHttpTestSupport.ResultAsync(denied, HttpStatusCode.NotFound, "notAuthorizedOrNotFound");
                Assert.Equal(JsonValueKind.Null, result.GetProperty("receipt").ValueKind);
                using var revokedReceipt = await api.Client.GetAsync(receiptPath);
                await RevisionUploadHttpTestSupport.AssertErrorAsync(revokedReceipt, HttpStatusCode.NotFound, "notFound");
            }
            finally
            {
                gate.Release();
                try { using var completed = await pending.WaitAsync(TimeSpan.FromSeconds(20)); }
                finally { await test.Case.ExecuteAsync(restore, values); }
            }
            var actualEvidence = Assert.IsType<IngestionEvidenceDocument>(test.BackendPort.Completions[^1].Evidence);
            Assert.True(actualEvidence.Facts.IsValid);
            Assert.True(actualEvidence.Facts.Provenance.Confirmed);
        }
        Assert.Equal(4, test.BackendPort.Calls);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mappings"));
        Assert.Empty(test.PhysicalRoot.AttemptFiles());
    }

    [RevisionIngestionFact]
    public async Task ClientCancellationAtEveryRealPortSettlesBookkeepingBeforeReleasingTheSameSingleAdmissionSlot()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1);
        var token = await api.TokenAsync();
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        foreach (var point in new[] { "source", "publish", "read", "worker", "completion", "finalize" })
        {
            var portGate = new RevisionIngestionGate();
            var completion = api.GateBody();
            if (point != "source") completion.Release.TrySetResult();
            if (point == "publish") test.StorePort.PublishReadGate = portGate;
            if (point == "read") test.StorePort.AfterReadGate = portGate;
            if (point == "worker") test.Launcher.NextProtocolGate = portGate;
            if (point == "completion") test.MetadataPort.BeforeValidationCompletion = portGate;
            if (point == "finalize") test.MetadataPort.BeforeFinalize = portGate;
            using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var pending = api.PostAsync(path, token, Guid.NewGuid(), bytes, cancellationToken: caller.Token);
            try
            {
                await (point == "source" ? completion.Entered.Task : portGate.Entered.Task).WaitAsync(TimeSpan.FromSeconds(15));
                await test.AssertNoHeldTransactionsAsync();
                await api.AssertNoHeldCatalogTransactionAsync();
                var calls = api.ServiceCalls;
                var busy = await api.HeadersOnlyPostAsync(path, token, Guid.NewGuid().ToString("D"));
                RevisionUploadHttpTestSupport.AssertRawError(busy, HttpStatusCode.ServiceUnavailable, "ingestionAdmissionBusy");
                Assert.Equal(calls, api.ServiceCalls);
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var unexpected = await pending; });
                await completion.RequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(api.ServiceCalls, api.CompletedServiceCalls);
                await test.AssertNoHeldTransactionsAsync();
                await api.AssertNoHeldCatalogTransactionAsync();
                Assert.Empty(test.PhysicalRoot.AttemptFiles());
            }
            finally
            {
                caller.Cancel(); portGate.Release(); completion.Release.TrySetResult();
                test.StorePort.PublishReadGate = null; test.StorePort.AfterReadGate = null;
                test.MetadataPort.BeforeValidationCompletion = null; test.MetadataPort.BeforeFinalize = null;
                api.ClearBodyGate();
                try { using var completed = await pending.WaitAsync(TimeSpan.FromSeconds(20)); }
                catch (OperationCanceledException) { }
            }
        }
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        using var next = await api.PostAsync(path, token, Guid.NewGuid(), bytes);
        await RevisionUploadHttpTestSupport.ResultAsync(next, HttpStatusCode.OK, "committedValid");
        Assert.Equal(api.ServiceCalls, api.CompletedServiceCalls);
        Assert.Empty(test.PhysicalRoot.AttemptFiles());
    }

    [RevisionIngestionFact]
    public async Task ActualTruncatedFinCancelsTheObservedRequestBeforeReservationAndTheSameAdmissionSlotRecovers()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1);
        var token = await api.TokenAsync();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        using var observation = api.ObserveNextPost(3);
        int? firstWireReadCount = null;
        try
        {
            firstWireReadCount = await api.TruncatedFinPostAsync(path, token, Guid.NewGuid(), observation);
            await observation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await observation.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var finish = await observation.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, firstWireReadCount.Value);
            Assert.True(observation.CanObserveCancellation);
            Assert.True(observation.IsRequestAborted);
            Assert.False(finish.ResponseStarted);
            Assert.Equal(3L, observation.SuccessfulBytes);
            Assert.Equal(1, api.ServiceCalls);
            Assert.Equal(1, api.CompletedServiceCalls);
            Assert.Equal(3L, api.BodyBytes);
            Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
            Assert.Equal(0, test.StorePort.PublishCalls);
            Assert.Equal(0, test.StorePort.ReadCalls);
            Assert.Equal(0, test.BackendPort.Calls);
            Assert.Empty(test.Runner.NativeBodies);
            Assert.Empty(test.Launcher.Commands);
            await test.AssertNoHeldTransactionsAsync();
            await api.AssertNoHeldCatalogTransactionAsync();
            using var next = await api.PostAsync(path, token, Guid.NewGuid(), RevisionIngestionTestSupport.Fixture("valid-basic"));
            await RevisionUploadHttpTestSupport.ResultAsync(next, HttpStatusCode.OK, "committedValid");
            Assert.Equal(api.ServiceCalls, api.CompletedServiceCalls);
            Assert.Empty(test.PhysicalRoot.AttemptFiles());
        }
        finally { WriteTransportObservation("truncatedFin", observation, firstWireReadCount); }
    }

    [RevisionIngestionFact]
    public async Task ActualMalformedChunkWithSendSideOpenReturnsAnUncanceledBodyErrorAndTheSameAdmissionSlotRecovers()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1);
        var token = await api.TokenAsync();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        using var observation = api.ObserveNextPost(3);
        try
        {
            var reply = await api.MalformedChunkPostAsync(path, token, Guid.NewGuid(), observation);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            Assert.StartsWith("application/json", reply.Headers["Content-Type"], StringComparison.Ordinal);
            var finish = await observation.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observation.Entered.Task.IsCompletedSuccessfully);
            Assert.True(observation.CanObserveCancellation);
            Assert.False(finish.RequestAbortedAtFinish);
            Assert.True(finish.ResponseStarted);
            Assert.Equal(400, finish.ResponseStatus);
            Assert.Null(finish.PipelineExceptionType);
            var fault = Assert.Single(observation.BodyFailures);
            Assert.Equal(400, fault.StatusCode);
            Assert.False(fault.RequestAbortedAtCatch);
            Assert.Equal(3L, observation.SuccessfulBytes);
            Assert.Equal(3L, api.BodyBytes);
            Assert.Equal(1, api.ServiceCalls);
            Assert.Equal(1, api.CompletedServiceCalls);
            Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
            Assert.Equal(0, test.StorePort.PublishCalls);
            Assert.Equal(0, test.StorePort.ReadCalls);
            Assert.Equal(0, test.BackendPort.Calls);
            Assert.Empty(test.Runner.NativeBodies);
            Assert.Empty(test.Launcher.Commands);
            await test.AssertNoHeldTransactionsAsync();
            await api.AssertNoHeldCatalogTransactionAsync();
            using var next = await api.PostAsync(path, token, Guid.NewGuid(), RevisionIngestionTestSupport.Fixture("valid-basic"));
            await RevisionUploadHttpTestSupport.ResultAsync(next, HttpStatusCode.OK, "committedValid");
            Assert.Equal(api.ServiceCalls, api.CompletedServiceCalls);
            Assert.Empty(test.PhysicalRoot.AttemptFiles());
        }
        finally { WriteTransportObservation("malformedChunk", observation); }
    }

    [RevisionIngestionFact]
    public async Task ActualCatalogAndIngestionRoleGuardFailuresRemainPersistenceErrorsBeforeWithheldBody()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test, maximumConcurrentOperations: 1);
        var token = await api.TokenAsync();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        foreach (var (grant, revoke, serviceCalls) in new[]
        {
            ("GRANT UPDATE(name) ON weft.mappings TO weft_catalog_reader", "REVOKE UPDATE(name) ON weft.mappings FROM weft_catalog_reader", 0),
            ("GRANT DELETE ON weft.revision_ingestions TO weft_ingest", "REVOKE DELETE ON weft.revision_ingestions FROM weft_ingest", 1)
        })
        {
            api.ResetObservations();
            await test.Case.ExecuteAsync(grant);
            try
            {
                var failed = await api.HeadersOnlyPostAsync(path, token, Guid.NewGuid().ToString("D"));
                RevisionUploadHttpTestSupport.AssertRawError(failed, HttpStatusCode.InternalServerError, "persistenceFailure");
                Assert.Equal(serviceCalls, api.ServiceCalls);
                Assert.Equal(0, api.BodyReads);
            }
            finally { await test.Case.ExecuteAsync(revoke); }
        }
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
        Assert.Equal(0, test.StorePort.PublishCalls);
        Assert.Equal(0, test.BackendPort.Calls);
        using var next = await api.PostAsync(path, token, Guid.NewGuid(), RevisionIngestionTestSupport.Fixture("valid-basic"));
        await RevisionUploadHttpTestSupport.ResultAsync(next, HttpStatusCode.OK, "committedValid");
    }

    [RevisionIngestionFact]
    public async Task EightExplicitMissingSourceAttemptsExhaustThePersistedBudgetWithoutImplicitRetryOrWorker()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        test.StorePort.FailAfterNextRealPublication();
        using var firstResponse = await api.PostAsync(RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery(),
            token, Guid.NewGuid(), RevisionIngestionTestSupport.Fixture("valid-basic"));
        var first = await RevisionUploadHttpTestSupport.ResultAsync(firstResponse, HttpStatusCode.ServiceUnavailable, "retryableFailure");
        var operation = RevisionUploadHttpTestSupport.Operation(first);
        var facts = (await test.PrivateReceiptAsync(operation))!.Facts;
        File.Delete(test.PhysicalRoot.FinalPath(facts.NativeObject));
        var path = RevisionUploadHttpTestSupport.ReceiptPath(test.Project, operation);
        for (var claim = 2; claim <= 8; claim++)
        {
            using var response = await api.PostAsync(path + "/resume", token, null, []);
            var missing = await RevisionUploadHttpTestSupport.ResultAsync(response, HttpStatusCode.Conflict, "needsSource");
            Assert.Equal(claim, missing.GetProperty("receipt").GetProperty("claimCount").GetInt32());
        }
        using var exhaustedResponse = await api.PostAsync(path + "/resume", token, null, []);
        var exhausted = await RevisionUploadHttpTestSupport.ResultAsync(exhaustedResponse, HttpStatusCode.Conflict, "attemptExhausted");
        Assert.Equal("attemptExhausted", exhausted.GetProperty("receipt").GetProperty("phase").GetString());
        Assert.Equal(8, exhausted.GetProperty("receipt").GetProperty("claimCount").GetInt32());
        using var receiptResponse = await api.Client.GetAsync(path);
        var receipt = await CatalogHttpTestSupport.JsonAsync(receiptResponse);
        RevisionUploadHttpTestSupport.AssertSafeReceipt(receipt);
        Assert.Equal("attemptExhausted", receipt.GetProperty("phase").GetString());
        Assert.Equal(9, api.ServiceCalls);
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    private void WriteTransportObservation(string transport, RevisionUploadHttpTestSupport.RequestObservation observation,
        int? firstWireReadCount = null) => output.WriteLine(JsonSerializer.Serialize(new
        {
            transport,
            firstWireReadCount,
            captured = observation.IsCaptured,
            pipelineEntered = observation.Entered.Task.IsCompletedSuccessfully,
            successfulPrefixBytes = observation.SuccessfulBytes,
            cancellationObservable = observation.CanObserveCancellation,
            requestAborted = observation.IsRequestAborted,
            abortCallback = observation.Aborted.Task.IsCompletedSuccessfully,
            bodyFailures = observation.BodyFailures.ToArray(),
            pipelineFinished = observation.Finished.Task.IsCompletedSuccessfully,
            finish = observation.Finished.Task.IsCompletedSuccessfully ? observation.Finished.Task.Result : null
        }));
}
