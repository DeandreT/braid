using System.Runtime.Versioning;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RevisionReportAdmissionTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task OneWorkSlotRefusesAnotherKeyWithoutClaimWhileDurableSameKeyPendingPrecedesAdmissionAcrossInstances()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        var source = await test.SourceSnapshotAsync();
        var pids = await test.AssertPoolsUnboundAsync();
        var gate = new RevisionReportBoundaryGate();
        test.StoragePort.ObserveAsync = (observed, token) => observed.Boundary == RevisionReportIoBoundary.BeforeRead &&
            observed.Reference.Id.Class == PrivateObjectClass.NativeArtifact ? gate.HoldAsync(token) : Task.CompletedTask;
        using var cancellation = new CancellationTokenSource();
        var original = test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection,
            cancellationToken: cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var claim = Assert.Single(test.MetadataPort.Claims);
            Assert.Equal(TimeSpan.FromSeconds(60), claim.LeaseExpiresAtUtc - claim.StartedAtUtc);
            Assert.Equal(new long[] { 1, 1, 0, 0 }, await test.CountsAsync());
            await test.AssertPoolsUnboundAsync(pids);

            var busy = await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Validation)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RevisionReportEnsureOutcome.AdmissionBusy, busy.Outcome);
            Assert.Null(busy.Read);
            Assert.Null(busy.Snapshot);
            Assert.Equal(1, test.MetadataPort.ClaimCalls);
            Assert.Equal(0, test.MetadataPort.RepairCalls);
            Assert.Equal(0, test.MetadataPort.FinishCalls);
            Assert.Equal(0, test.BackendPort.Calls);
            Assert.Equal(0, test.StoragePort.PublishCalls);

            foreach (var service in new[] { test.Service, test.NewService(serverInstanceId: Guid.NewGuid()) })
            {
                var pending = await service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)
                    .WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(RevisionReportEnsureOutcome.Pending, pending.Outcome);
                Assert.Null(pending.Read);
                Assert.Equal(claim.DerivationId, pending.Snapshot!.CurrentAttempt!.DerivationId);
                Assert.Equal(claim.CacheEntryId, pending.Snapshot.CacheEntryId);
                Assert.Equal(claim.CandidateObjectId.Scope, pending.Snapshot.Key.Scope);
                Assert.Equal(claim.LeaseExpiresAtUtc, pending.NextAttemptAtUtc);
            }
            var read = await test.Service.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RevisionReportReadOutcome.Observed, read.Outcome);
            Assert.Equal(RevisionReportState.Pending, read.State);
            Assert.Null(read.Report);
            Assert.Equal(claim.DerivationId, read.Snapshot!.CurrentAttempt!.DerivationId);
            Assert.Equal(1, test.MetadataPort.ClaimCalls);
            Assert.Equal(0, test.StoragePort.DerivedReads);
            Assert.Equal(source, await test.SourceSnapshotAsync());

            gate.Release();
            var projection = Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(await original));
            Assert.Equal(claim.DerivationId, projection.Metadata.DerivationId);
            Assert.Equal(claim.CandidateObjectId, projection.Metadata.DerivedObject.Id);
            var validation = Assert.IsType<VerifiedValidationReport>(RevisionReportTestSupport.Available(
                await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Validation)));
            Assert.True(validation.Report.IsValid);
            Assert.Equal(2, test.BackendPort.Calls);
            Assert.Equal(new long[] { 2, 2, 2, 0 }, await test.CountsAsync());
            Assert.Equal(source, await test.SourceSnapshotAsync());
            await test.AssertPoolsUnboundAsync(pids);
        }
        finally
        {
            cancellation.Cancel();
            gate.Release();
            await DrainAsync(original);
        }
    }

    [RevisionIngestionFact]
    public async Task TwoCachedReadSlotsRefuseAThirdButLeaveWorkIndependentAndRecoverAfterReleaseAndCancellation()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        var validation = Assert.IsType<VerifiedValidationReport>(RevisionReportTestSupport.Available(
            await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Validation)));
        var source = await test.SourceSnapshotAsync();
        var pids = await test.AssertPoolsUnboundAsync();
        var firstGate = new RevisionReportBoundaryGate();
        var secondGate = new RevisionReportBoundaryGate();
        var heldReads = 0;
        test.StoragePort.ObserveAsync = (observed, token) =>
        {
            if (observed.Boundary != RevisionReportIoBoundary.BeforeRead || observed.Reference != validation.Metadata.DerivedObject)
                return Task.CompletedTask;
            return Interlocked.Increment(ref heldReads) switch
            {
                1 => firstGate.HoldAsync(token),
                2 => secondGate.HoldAsync(token),
                _ => Task.CompletedTask
            };
        };
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = test.Service.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Validation,
            cancellationToken: firstCancellation.Token);
        Task<RevisionReportReadResult>? second = null;
        try
        {
            await firstGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = test.Service.GetRetainedAsync(test.Actor, test.Revision, RevisionReportOperation.Validation,
                validation.Metadata.DerivationId, cancellationToken: secondCancellation.Token);
            await secondGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await test.AssertPoolsUnboundAsync(pids);
            var readsBeforeRefusal = test.StoragePort.DerivedReads;
            var claimsBeforeRefusal = test.MetadataPort.ClaimCalls;
            var finishesBeforeRefusal = test.MetadataPort.FinishCalls;
            var busy = await test.Service.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Validation)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RevisionReportReadOutcome.AdmissionBusy, busy.Outcome);
            Assert.Null(busy.Source);
            Assert.Null(busy.Report);
            Assert.Equal(readsBeforeRefusal, test.StoragePort.DerivedReads);
            Assert.Equal(claimsBeforeRefusal, test.MetadataPort.ClaimCalls);
            Assert.Equal(finishesBeforeRefusal, test.MetadataPort.FinishCalls);

            // Both cached reads remain inside their held I/O boundaries. A separate operation
            // really dispatches and finishes through the independent work capacity.
            var projection = Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(
                await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)));
            Assert.Equal(test.Revision, projection.Metadata.Key.Scope);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(2, test.BackendPort.Calls);

            firstGate.Release();
            Assert.Equal(validation.Metadata, RevisionReportTestSupport.Available(await first).Metadata);
            var recoveredOne = RevisionReportTestSupport.Available(await test.Service.GetCurrentAsync(test.Actor,
                test.Revision, RevisionReportOperation.Validation).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(validation.Metadata, recoveredOne.Metadata);
            Assert.False(second.IsCompleted);
            secondCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second!);
            var recoveredBoth = RevisionReportTestSupport.Available(await test.Service.GetRetainedAsync(test.Actor,
                test.Revision, RevisionReportOperation.Validation, validation.Metadata.DerivationId));
            Assert.Equal(validation.Metadata, recoveredBoth.Metadata);
            Assert.Equal(2, test.BackendPort.Calls);
            Assert.Equal(2, test.StoragePort.NativeReads);
            Assert.Equal(new long[] { 2, 2, 2, 0 }, await test.CountsAsync());
            Assert.Equal(source, await test.SourceSnapshotAsync());
            await test.AssertPoolsUnboundAsync(pids);
        }
        finally
        {
            firstCancellation.Cancel();
            secondCancellation.Cancel();
            firstGate.Release();
            secondGate.Release();
            await DrainAsync(first);
            if (second is not null) await DrainAsync(second);
        }
    }

    [RevisionIngestionFact]
    public async Task ActualInFlightCancellationSettlesTheWorkerAndFencedFailureBeforeSameRunnerAndWorkSlotRecover()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        var source = await test.SourceSnapshotAsync();
        var pids = await test.AssertPoolsUnboundAsync();
        var nativeCallsBefore = test.Ingestion.Runner.NativeBodies.Count;
        var input = new RevisionIngestionGate();
        test.Ingestion.Launcher.NextProtocolGate = input;
        using var cancellation = new CancellationTokenSource();
        var original = test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection,
            cancellationToken: cancellation.Token);
        try
        {
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(nativeCallsBefore + 1, test.Ingestion.Runner.NativeBodies.Count);
            Assert.False(original.IsCompleted);
            await test.AssertPoolsUnboundAsync(pids);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
            Assert.True(original.IsCanceled);
            var completion = Assert.Single(test.BackendPort.Completions);
            Assert.False(completion.IsUsable);
            Assert.Equal(RevisionReportFailureCategory.Canceled, completion.Failure);
            var claim = Assert.Single(test.MetadataPort.Claims);
            var retained = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(test.Actor,
                test.Revision, RevisionReportOperation.Projection, claim.DerivationId));
            Assert.Equal(claim.Key, retained.Key);
            Assert.NotNull(retained.Attempt.FinishedAtUtc);
            Assert.Equal(RevisionReportFailureCategory.Canceled, retained.Attempt.Completion!.Failure);
            Assert.Null(retained.Result);
            Assert.Equal(new long[] { 1, 1, 0, 1 }, await test.CountsAsync());
            var failedFacts = await test.AttemptSnapshotAsync(claim.DerivationId);
            await test.AssertPoolsUnboundAsync(pids);

            input.Release();
            var validation = Assert.IsType<VerifiedValidationReport>(RevisionReportTestSupport.Available(
                await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Validation)));
            Assert.True(validation.Report.IsValid);
            Assert.Equal(2, test.BackendPort.Calls);
            Assert.Equal(nativeCallsBefore + 2, test.Ingestion.Runner.NativeBodies.Count);
            Assert.Equal(test.NativeBytes, test.Ingestion.Runner.NativeBodies[^1]);
            Assert.Equal(failedFacts, await test.AttemptSnapshotAsync(claim.DerivationId));
            Assert.Equal(new long[] { 2, 2, 1, 1 }, await test.CountsAsync());
            Assert.Equal(source, await test.SourceSnapshotAsync());
            await test.AssertPoolsUnboundAsync(pids);
        }
        finally
        {
            cancellation.Cancel();
            input.Release();
            await DrainAsync(original);
        }
    }

    private static async Task DrainAsync(Task task)
    {
        // The service and actual runner own their fixed cancellation/cleanup budgets.
        // Settle them before disposing the shared case; do not replace a primary assertion.
        try { await task; }
        catch (Exception) { }
    }
}
