using Braid.Application;
using static Braid.Tests.PrivateObjectMaintenanceContractFacts;

namespace Braid.Tests;

/// <summary>Synthetic monotonic-clock and held-task tests; real SQL/storage settlement has separate qualification.</summary>
public sealed class PrivateObjectMaintenanceServiceScheduleTests
{
    [Theory]
    [InlineData("status", 1)]
    [InlineData("storage", 2)]
    [InlineData("match", 3)]
    public async Task WorkCutoffBeforeNextObservationOrClaimDoesNotCreateAnotherBudget(string stage, int expectedCalls)
    {
        var time = new MaintenanceServiceTestTimeProvider();
        var ports = new PrivateObjectMaintenanceServiceTestPorts();
        if (stage == "status") ports.OnStatus = (_, _, _) => { time.Advance(TimeSpan.FromSeconds(25)); return Task.FromResult(ports.Statuses); };
        if (stage == "storage") ports.OnObserve = (_, _) => { time.Advance(TimeSpan.FromSeconds(25)); return Task.FromResult(ports.Storage); };
        if (stage == "match") ports.OnMatch = (request, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(25));
            return Task.FromResult(new PrivatePublicationObservationBatch(request, RetiredAt, []));
        };
        var result = await ports.Service(time).RunOnceAsync(Project);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Deadline, result.Outcome);
        Assert.Equal(expectedCalls, ports.Calls.Count);
        Assert.False(result.ClaimAttempted);
        Assert.Null(result.ClaimedCount);
        Assert.Empty(ports.Retirements);
    }

    [Fact]
    public async Task ReturnedDeletionAtWorkCutoffIsRetainedWithoutNewFinishOrSecondTarget()
    {
        var time = new MaintenanceServiceTestTimeProvider();
        var ports = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnRetire = (_, token) =>
            {
                time.Advance(TimeSpan.FromSeconds(25));
                Assert.True(token.IsCancellationRequested);
                return Task.FromResult(PrivateObjectRetirementOutcome.Deleted);
            }
        };
        ports.SetClaims(CleanupClaim(), CleanupClaim(seed: 1));
        var result = await ports.Service(time).RunOnceAsync(Project);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Deadline, result.Outcome);
        Assert.Equal(1, result.PhysicalDeleted);
        Assert.Equal(0, result.AcknowledgedDeleted);
        Assert.Equal(PrivateObjectMaintenancePhysicalObservation.NotStarted, result.Targets[1].Physical);
        Assert.Single(ports.Retirements);
        Assert.Empty(ports.Finishes);
    }

    [Fact]
    public async Task PhysicalDeadlineCancellationIsNotAnIoObservationOrACompletion()
    {
        var time = new MaintenanceServiceTestTimeProvider();
        var ports = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnRetire = (_, token) =>
            {
                time.Advance(TimeSpan.FromSeconds(25));
                return Task.FromCanceled<PrivateObjectRetirementOutcome>(token);
            }
        };
        ports.SetClaims(CleanupClaim());
        var result = await ports.Service(time).RunOnceAsync(Project);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Deadline, result.Outcome);
        Assert.Equal(PrivateObjectMaintenancePhysicalObservation.Deadline, Assert.Single(result.Targets).Physical);
        Assert.Null(result.Failure);
        Assert.Empty(ports.Finishes);
        Assert.Equal(0, result.PhysicalDeleted);
    }

    [Theory]
    [InlineData(PrivateObjectRetirementOutcome.Deleted, PrivateObjectMaintenancePhysicalObservation.Deleted)]
    [InlineData(PrivateObjectRetirementOutcome.Missing, PrivateObjectMaintenancePhysicalObservation.Missing)]
    public async Task CallerStopDoesNotOverwriteAnActualReturnedPhysicalResult(PrivateObjectRetirementOutcome actual,
        PrivateObjectMaintenancePhysicalObservation expected)
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnRetire = (_, _) => { cancellation.Cancel(); return Task.FromResult(actual); }
        };
        ports.SetClaims(CleanupClaim(), CleanupClaim(seed: 1));
        var result = await ports.Service().RunOnceAsync(Project, cancellationToken: cancellation.Token);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Canceled, result.Outcome);
        Assert.Equal(expected, result.Targets[0].Physical);
        Assert.Equal(PrivateObjectMaintenanceAcknowledgement.NotAttempted, result.Targets[0].Acknowledgement);
        Assert.Equal(PrivateObjectMaintenancePhysicalObservation.NotStarted, result.Targets[1].Physical);
        Assert.Null(result.Failure);
        Assert.Empty(ports.Finishes);
    }

    [Fact]
    public async Task PhysicalCancellationAndLateFaultDoNotBecomeInventedIoOrPositiveCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnRetire = (_, _) => { cancellation.Cancel(); return Task.FromException<PrivateObjectRetirementOutcome>(new IOException("synthetic late fault")); }
        };
        ports.SetClaims(CleanupClaim());
        var result = await ports.Service().RunOnceAsync(Project, cancellationToken: cancellation.Token);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Canceled, result.Outcome);
        Assert.Equal(PrivateObjectMaintenanceRunFailure.StorageUnavailable, result.Failure);
        Assert.Equal(PrivateObjectMaintenancePhysicalObservation.Unresolved, Assert.Single(result.Targets).Physical);
        Assert.Equal(0, result.PhysicalDeleted);
        Assert.Empty(ports.Finishes);

        using var stopped = new CancellationTokenSource();
        var canceledPorts = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnRetire = (_, token) => { stopped.Cancel(); return Task.FromCanceled<PrivateObjectRetirementOutcome>(token); }
        };
        canceledPorts.SetClaims(CleanupClaim());
        var canceled = await canceledPorts.Service().RunOnceAsync(Project, cancellationToken: stopped.Token);
        Assert.Equal(PrivateObjectMaintenancePhysicalObservation.Canceled, Assert.Single(canceled.Targets).Physical);
        Assert.Null(canceled.Failure);
        Assert.Empty(canceledPorts.Finishes);
    }

    [Fact]
    public async Task RecoveryWaitsForOriginalFinishAndUsesExactlyItsSettlementTokenAndClaim()
    {
        var time = new MaintenanceServiceTestTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var original = new TaskCompletionSource<ObjectCleanupFinishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var claim = CleanupClaim();
        var ports = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnFinish = _ => { entered.SetResult(true); return original.Task; }
        };
        ports.OnGet = call =>
        {
            Assert.True(original.Task.IsCompleted);
            Assert.Equal(ports.Finishes[0].Settlement, call.Token);
            Assert.False(call.Token.IsCancellationRequested);
            Assert.Equal(claim.Retirement.RetirementId, call.Retirement);
            Assert.Equal(claim.Generation, call.Generation);
            return Task.FromResult<ObjectCleanupAttempt?>(new(claim, claim.StartedAtUtc, ObjectCleanupDisposition.Deleted));
        };
        ports.SetClaims(claim);
        var run = ports.Service(time).RunOnceAsync(Project, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(23));
        cancellation.Cancel();
        Assert.False(run.IsCompleted);
        Assert.Empty(ports.Gets);
        original.SetException(new IOException("synthetic settled ACK loss"));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Canceled, result.Outcome);
        Assert.Equal(PrivateObjectMaintenanceRunFailure.MetadataUnavailable, result.Failure);
        Assert.Equal(PrivateObjectMaintenanceAcknowledgement.Recovered, Assert.Single(result.Targets).Acknowledgement);
        Assert.Equal(1, result.AcknowledgedDeleted);
        Assert.Single(ports.Gets);
        Assert.Single(ports.Retirements);
    }

    [Fact]
    public async Task CallerGraceShortensSharedSettlementToFiveWithoutDetachingOriginal()
    {
        var time = new MaintenanceServiceTestTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var original = new TaskCompletionSource<ObjectCleanupFinishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ports = new PrivateObjectMaintenanceServiceTestPorts { OnFinish = _ => original.Task };
        ports.SetClaims(CleanupClaim());
        var run = ports.Service(time).RunOnceAsync(Project, cancellationToken: cancellation.Token);
        var finish = Assert.Single(ports.Finishes);
        time.Advance(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        Assert.True(finish.Work.IsCancellationRequested);
        time.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.False(finish.Settlement.IsCancellationRequested);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(finish.Settlement.IsCancellationRequested);
        Assert.False(run.IsCompleted);
        Assert.Empty(ports.Gets);
        original.SetException(new IOException("synthetic late original failure"));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Canceled, result.Outcome);
        Assert.Equal(PrivateObjectMaintenanceRunFailure.MetadataUnavailable, result.Failure);
        Assert.Equal(PrivateObjectMaintenanceAcknowledgement.Unresolved, Assert.Single(result.Targets).Acknowledgement);
        Assert.Empty(ports.Gets);
    }

    [Fact]
    public async Task CancellationAtTwentyNineCannotResetTheOriginalThirtySecondTimer()
    {
        var time = new MaintenanceServiceTestTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var original = new TaskCompletionSource<ObjectCleanupFinishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ports = new PrivateObjectMaintenanceServiceTestPorts { OnFinish = _ => original.Task };
        var claim = CleanupClaim();
        ports.SetClaims(claim);
        var run = ports.Service(time).RunOnceAsync(Project, cancellationToken: cancellation.Token);
        var finish = Assert.Single(ports.Finishes);
        time.Advance(TimeSpan.FromSeconds(29));
        cancellation.Cancel();
        Assert.False(finish.Settlement.IsCancellationRequested);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(finish.Settlement.IsCancellationRequested);
        Assert.False(run.IsCompleted);
        original.SetResult(ObjectCleanupFinishResult.ForAttempt(ObjectCleanupFinishOutcome.Applied,
            new(claim, claim.StartedAtUtc, ObjectCleanupDisposition.Deleted)));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Canceled, result.Outcome);
        Assert.Equal(PrivateObjectMaintenanceAcknowledgement.Applied, Assert.Single(result.Targets).Acknowledgement);
        Assert.Equal(1, result.AcknowledgedDeleted);
        Assert.Empty(ports.Gets);
    }

    [Fact]
    public async Task ExactReadRecoveryConsumesSharedAbsoluteRemainderWithoutAnotherTail()
    {
        var time = new MaintenanceServiceTestTimeProvider();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = false;
        var ports = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnFinish = _ => Task.FromException<ObjectCleanupFinishResult>(new IOException("synthetic ACK loss")),
            OnGet = async call =>
            {
                entered.SetResult(true);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, call.Token); return null; }
                finally { settled = true; }
            }
        };
        ports.SetClaims(CleanupClaim());
        var run = ports.Service(time).RunOnceAsync(Project);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ports.Finishes[0].Settlement, Assert.Single(ports.Gets).Token);
        time.Advance(TimeSpan.FromSeconds(30));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(settled);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Deadline, result.Outcome);
        Assert.Equal(PrivateObjectMaintenanceAcknowledgement.Unresolved, Assert.Single(result.Targets).Acknowledgement);
        Assert.Equal(1, result.PhysicalDeleted);
        Assert.Equal(0, result.AcknowledgedDeleted);
        Assert.Single(ports.Gets);
    }
}
