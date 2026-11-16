using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using H = Braid.Tests.CatalogRevisionReportHttpTestSupport;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CatalogRevisionReportEnsureTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task RealSingletonWorkAdmissionExposesDurablePending202AndDifferentKeyBusy429WithoutAdditionalClaim()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        await using var api = await H.StartAsync(test);
        var token = await api.TokenAsync();
        var gate = new RevisionReportBoundaryGate();
        test.StoragePort.ObserveAsync = (value, cancellation) => value is
        { Boundary: RevisionReportIoBoundary.BeforeRead, Reference.Id.Class: PrivateObjectClass.NativeArtifact }
            ? gate.HoldAsync(cancellation) : Task.CompletedTask;
        var pids = await test.AssertPoolsUnboundAsync();
        var first = api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Projection), token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await test.AssertPoolsUnboundAsync(pids);
            await api.AssertCatalogPoolUnboundAsync();
            using (var pending = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Projection), token)
                .WaitAsync(TimeSpan.FromSeconds(5)))
            {
                var body = await H.ReportAsync(pending, "projection", "pending", HttpStatusCode.Accepted);
                Assert.Equal(H.CurrentPath(test.Revision, RevisionReportOperation.Projection), pending.Headers.Location!.ToString());
                var retry = Assert.Single(pending.Headers.GetValues("Retry-After"));
                Assert.InRange(int.Parse(retry, CultureInfo.InvariantCulture), 1, 60);
                Assert.Equal(retry, int.Parse(retry, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                Assert.Equal(JsonValueKind.Null, body.GetProperty("report").ValueKind);
                Assert.Equal(JsonValueKind.Null, body.GetProperty("result").ValueKind);
                Assert.Equal("1", body.GetProperty("currentAttempt").GetProperty("generation").GetString());
            }
            using (var read = await api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection)))
                await H.ReportAsync(read, "projection", "pending");
            using (var busy = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Validation), token)
                .WaitAsync(TimeSpan.FromSeconds(5)))
                await H.ErrorAsync(busy, HttpStatusCode.TooManyRequests, "revisionReportAdmissionBusy");
            Assert.Equal(new long[] { 1, 1, 0, 0 }, await test.CountsAsync());
            Assert.Equal(1, test.MetadataPort.ClaimCalls);
            Assert.Equal(0, test.BackendPort.Calls);
            Assert.Equal(1, test.StoragePort.NativeReads);
        }
        finally { gate.Release(); }
        using (var completed = await first.WaitAsync(TimeSpan.FromSeconds(30))) await H.ReportAsync(completed, "projection");
        test.StoragePort.ObserveAsync = null;
        using (var next = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Validation), token))
            await H.ReportAsync(next, "validation");
        Assert.Equal(2, test.BackendPort.Calls);
        Assert.Equal(new long[] { 2, 2, 2, 0 }, await test.CountsAsync());
        Assert.Equal(0, api.BodyReads);
    }

    [RevisionIngestionFact]
    public async Task TwoRealCachedReadsExhaustReadAdmissionWhileAnIndependentAdmittedWorkStillCompletes()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var selected = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        await using var api = await H.StartAsync(test);
        var token = await api.TokenAsync();
        var gate = new RevisionReportBoundaryGate();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        test.StoragePort.ObserveAsync = (value, cancellation) =>
        {
            if (value.Boundary != RevisionReportIoBoundary.BeforeRead || value.Reference != selected.Metadata.DerivedObject)
                return Task.CompletedTask;
            if (Interlocked.Increment(ref entered) == 2) secondEntered.TrySetResult();
            return gate.HoldAsync(cancellation);
        };
        var first = api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection));
        Task<HttpResponseMessage>? second = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = api.SendAsync(HttpMethod.Get, H.RetainedPath(test.Revision, RevisionReportOperation.Projection, selected.Metadata.DerivationId));
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await test.AssertPoolsUnboundAsync();
            using (var busy = await api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection))
                .WaitAsync(TimeSpan.FromSeconds(5)))
                await H.ErrorAsync(busy, HttpStatusCode.TooManyRequests, "revisionReportAdmissionBusy");
            Assert.Equal(2, Volatile.Read(ref entered));
            using var work = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Validation), token);
            await H.ReportAsync(work, "validation");
            Assert.Equal(2, test.BackendPort.Calls);
        }
        finally { gate.Release(); }
        using (var one = await first.WaitAsync(TimeSpan.FromSeconds(5))) await H.ReportAsync(one, "projection");
        using (var two = await second!.WaitAsync(TimeSpan.FromSeconds(5))) await H.ReportAsync(two, "projection");
        test.StoragePort.ObserveAsync = null;
        using var recovered = await api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection));
        await H.ReportAsync(recovered, "projection");
        Assert.Equal(new long[] { 2, 2, 2, 0 }, await test.CountsAsync());
    }

    [RevisionIngestionFact]
    public async Task ActualOwnedSocketAbortCancelsAcceptedHttpWorkAndTheSameSingletonAndPoolsRecover()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        await using var api = await H.StartAsync(test);
        var token = await api.TokenAsync();
        var source = await test.SourceSnapshotAsync();
        var pids = await test.AssertPoolsUnboundAsync();
        var gate = new RevisionReportBoundaryGate();
        test.StoragePort.ObserveAsync = (value, cancellation) => value is
        { Boundary: RevisionReportIoBoundary.BeforeRead, Reference.Id.Class: PrivateObjectClass.NativeArtifact }
            ? gate.HoldAsync(cancellation) : Task.CompletedTask;
        var observation = api.ObserveNextRequest();
        using var request = await api.StartRawAsync("POST", H.EnsurePath(test.Revision, RevisionReportOperation.Projection), token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, test.BackendPort.Calls);
            await test.AssertPoolsUnboundAsync(pids);
            request.Abort();
            await observation.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await observation.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observation.AbortedAtFinish);
            Assert.False(observation.ResponseStartedAtFinish);
        }
        finally { gate.Release(); }
        test.StoragePort.ObserveAsync = null;
        await test.AssertPoolsUnboundAsync(pids);
        using var next = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Validation), token);
        await H.ReportAsync(next, "validation");
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(source, await test.SourceSnapshotAsync());
        Assert.Equal(new long[] { 2, 2, 1, 1 }, await test.CountsAsync());
        var canceled = await test.Reader.GetCurrentAsync(test.Actor, test.Key(RevisionReportOperation.Projection));
        Assert.Equal(RevisionReportFailureCategory.Canceled, canceled!.CurrentAttempt!.Completion!.Failure);
        Assert.Equal(0, api.BodyReads);
    }

    [RevisionIngestionFact]
    public async Task ExactSelector409AndRealMissingCorruptRepairsPreserveEveryOriginalSuccessfulAttempt()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        await using var api = await H.StartAsync(test);
        var token = await api.TokenAsync();
        var source = await test.SourceSnapshotAsync();
        var ensure = H.EnsurePath(test.Revision, RevisionReportOperation.Projection);
        Guid original;
        using (var response = await api.SendAsync(HttpMethod.Post, ensure, token))
            original = (await H.ReportAsync(response, "projection")).GetProperty("result").GetProperty("derivationId").GetGuid();
        var firstRow = await test.AttemptSnapshotAsync(original);
        var beforeReads = test.StoragePort.DerivedReads;
        using (var response = await api.SendAsync(HttpMethod.Post, ensure + $"?repairDerivationId={Guid.NewGuid():D}", token))
            await H.ErrorAsync(response, HttpStatusCode.Conflict, "revisionReportSelectionChanged");
        Assert.Equal(beforeReads, test.StoragePort.DerivedReads);
        var selected = (await test.Reader.GetRetainedAsync(test.Actor, test.Revision, RevisionReportOperation.Projection, original))!.Result!;
        File.Delete(test.PhysicalRoot.FinalPath(selected.DerivedObject));
        using (var response = await api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection)))
        {
            var body = await H.ReportAsync(response, "projection");
            Assert.Equal("objectUnavailable", body.GetProperty("readOutcome").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("report").ValueKind);
        }
        using (var response = await api.SendAsync(HttpMethod.Post, ensure, token))
            Assert.Equal("objectUnavailable", (await H.ReportAsync(response, "projection")).GetProperty("readOutcome").GetString());
        Assert.Equal(1, test.BackendPort.Calls);
        Guid repaired;
        using (var response = await api.SendAsync(HttpMethod.Post, ensure + $"?repairDerivationId={original:D}", token))
        {
            var body = await H.ReportAsync(response, "projection");
            repaired = body.GetProperty("result").GetProperty("derivationId").GetGuid();
            Assert.NotEqual(original, repaired);
            Assert.Equal("2", body.GetProperty("result").GetProperty("generation").GetString());
        }
        var secondRow = await test.AttemptSnapshotAsync(repaired);
        var secondResult = (await test.Reader.GetRetainedAsync(test.Actor, test.Revision, RevisionReportOperation.Projection, repaired))!.Result!;
        var path = test.PhysicalRoot.FinalPath(secondResult.DerivedObject);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllBytesAsync(path, "Actual owned HTTP corrupted report"u8.ToArray());
        File.SetUnixFileMode(path, PrivateStorageTestRoot.FinalMode);
        using (var response = await api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection)))
            Assert.Equal("storageIntegrity", (await H.ReportAsync(response, "projection")).GetProperty("readOutcome").GetString());
        using (var response = await api.SendAsync(HttpMethod.Post, ensure + $"?repairDerivationId={repaired:D}", token))
        {
            var body = await H.ReportAsync(response, "projection");
            Assert.Equal("3", body.GetProperty("result").GetProperty("generation").GetString());
            Assert.NotEqual(repaired, body.GetProperty("result").GetProperty("derivationId").GetGuid());
        }
        Assert.Equal(firstRow, await test.AttemptSnapshotAsync(original));
        Assert.Equal(secondRow, await test.AttemptSnapshotAsync(repaired));
        Assert.Equal(source, await test.SourceSnapshotAsync());
        Assert.Equal(3, test.BackendPort.Calls);
        Assert.Equal(new long[] { 1, 3, 3, 0 }, await test.CountsAsync());
    }

    [RevisionIngestionFact]
    public async Task SyntheticCrossRevisionReportPortIsAnInternalFailureRatherThanAvailabilityOrResourceRefusal()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        var actualFirst = await test.Service.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
        var second = await test.Ingestion.CreateAsync(RevisionIngestionTestSupport.Fixture("valid-basic"));
        var receipt = (await test.Ingestion.PrivateReceiptAsync(second.Receipt!.OperationId))!;
        // Real valid result from A, intentionally supplied by a dishonest application port for admitted revision B.
        await using var api = await H.StartAsync(test, reader: new CrossRevisionReader(actualFirst));
        using var response = await api.SendAsync(HttpMethod.Get, H.CurrentPath(receipt.Facts.FutureRevision, RevisionReportOperation.Projection));
        await H.ErrorAsync(response, HttpStatusCode.InternalServerError, "internalFailure");
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
    }

    private sealed class CrossRevisionReader(RevisionReportReadResult unrelatedActualResult) : IRevisionReportReader
    {
        public Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
            Guid? projectionDerivationId = null, CancellationToken cancellationToken = default) => Task.FromResult(unrelatedActualResult);
        public Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
            Guid derivationId, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default) => Task.FromResult(unrelatedActualResult);
    }
}
