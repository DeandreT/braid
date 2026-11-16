using Braid.Application;
using Braid.Domain;
using static Braid.Tests.MaintenanceObservationContractFacts;

namespace Braid.Tests;

/// <summary>Pure state and ownership checks with explicitly authored database-time observations.</summary>
public sealed class PrivateObjectMaintenanceObservationContractTests
{
    [Theory]
    [InlineData(1, 59, ObjectCleanupObservedState.Leased)]
    [InlineData(1, 60, ObjectCleanupObservedState.RetryWaiting)]
    [InlineData(1, 89, ObjectCleanupObservedState.RetryWaiting)]
    [InlineData(1, 90, ObjectCleanupObservedState.RetryDue)]
    [InlineData(8, 59, ObjectCleanupObservedState.Leased)]
    [InlineData(8, 60, ObjectCleanupObservedState.Exhausted)]
    public void AuthoredUnfinishedStateUsesExactDatabaseObservationBoundaries(long generation, int seconds,
        ObjectCleanupObservedState expected)
    {
        var status = Status(attempt: Attempt(generation), observed: At.AddSeconds(seconds));
        Assert.Equal(expected, status.State);
        Assert.Null(status.CurrentAttempt!.Disposition);
        Assert.Null(status.CompletedAttemptId);
        Assert.Equal(generation == 8 ? null : At.AddSeconds(90), status.NextAttemptAtUtc);
    }

    [Theory]
    [InlineData(ObjectCleanupDisposition.Canceled)]
    [InlineData(ObjectCleanupDisposition.Deadline)]
    [InlineData(ObjectCleanupDisposition.StorageIo)]
    [InlineData(ObjectCleanupDisposition.UnsafeStorage)]
    public void FailedObservationRetriesFromActualFinishAndEighthFailureExhausts(ObjectCleanupDisposition disposition)
    {
        var first = Attempt(finished: At.AddSeconds(5), disposition: disposition);
        Assert.Equal(ObjectCleanupObservedState.RetryWaiting, Status(attempt: first, observed: At.AddSeconds(34)).State);
        Assert.Equal(ObjectCleanupObservedState.RetryDue, Status(attempt: first, observed: At.AddSeconds(35)).State);
        var last = Status(attempt: Attempt(8, At.AddSeconds(5), disposition), observed: At.AddSeconds(5));
        Assert.Equal(ObjectCleanupObservedState.Exhausted, last.State);
        Assert.Null(last.NextAttemptAtUtc);
    }

    [Theory]
    [InlineData(ObjectCleanupDisposition.Deleted)]
    [InlineData(ObjectCleanupDisposition.Missing)]
    public void SuccessPointerIsRequiredInBothDirections(ObjectCleanupDisposition disposition)
    {
        var success = Attempt(8, At, disposition);
        var status = Status(attempt: success, completed: success.AttemptId);
        Assert.Equal(ObjectCleanupObservedState.Completed, status.State);
        Assert.Null(status.NextAttemptAtUtc);
        Assert.Throws<ArgumentException>(() => Status(attempt: success));
        Assert.Throws<ArgumentException>(() => Status(attempt: success, completed: Id(99)));
        Assert.Throws<ArgumentException>(() => Status(attempt: Attempt(), completed: Id(30)));
        Assert.Throws<ArgumentException>(() => Status(attempt: Attempt(finished: At, disposition: ObjectCleanupDisposition.StorageIo), completed: Id(30)));
        Assert.Throws<ArgumentException>(() => Status(completed: Id(30)));
    }

    [Fact]
    public void StatusRefusesContradictoryGenerationsAndFutureOrPreRetirementTimes()
    {
        var target = new ObjectCleanupTarget(Object(), CleanupTargetKind.FinalObject);
        Assert.Equal(ObjectCleanupObservedState.Unattempted, Status().State);
        Assert.Throws<ArgumentException>(() => new ObjectCleanupStatusHeader(Id(20), target, At, 1, null, null, At));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupStatusHeader(Id(20), target, At, 0, Attempt(), null, At));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupStatusHeader(Id(20), target, At, 2, Attempt(), null, At));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupStatusHeader(Id(20), target, At.AddTicks(1), 1, Attempt(), null, At.AddSeconds(1)));
        Assert.Throws<ArgumentException>(() => Status(attempt: Attempt(), observed: At.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => Status(attempt: Attempt(finished: At.AddSeconds(1), disposition: ObjectCleanupDisposition.StorageIo)));
        Assert.Throws<ArgumentException>(() => Status(observed: At.AddSeconds(-2)));
    }

    [Fact]
    public void FlatAttemptRejectsUnknownCompletionAndUnrepresentableRetryWithoutInventingAClaim()
    {
        Assert.Throws<ArgumentException>(() => new ObjectCleanupStatusCursor(Project, Guid.Empty));
        Assert.Throws<ArgumentException>(() => Attempt(id: Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => Attempt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Attempt(9));
        Assert.Throws<ArgumentException>(() => Attempt(finished: At));
        Assert.Throws<ArgumentException>(() => Attempt(disposition: ObjectCleanupDisposition.Deleted));
        Assert.Throws<ArgumentOutOfRangeException>(() => Attempt(finished: At, disposition: (ObjectCleanupDisposition)99));
        Assert.Throws<ArgumentException>(() => new CleanupAttemptStatusHeader(Id(30), 1, At, At.AddSeconds(59), null, null));
        Assert.Throws<ArgumentException>(() => Attempt(finished: At.AddSeconds(60), disposition: ObjectCleanupDisposition.Deleted));
        var lastStart = DateTimeOffset.MaxValue.AddSeconds(-90);
        Assert.Equal(DateTimeOffset.MaxValue, Status(attempt: Attempt(start: lastStart), observed: lastStart).NextAttemptAtUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() => Attempt(start: lastStart.AddTicks(1)));
        Assert.Throws<ArgumentException>(() => new CleanupAttemptStatusHeader(Id(30), 1, At.ToOffset(TimeSpan.FromHours(1)), At.AddSeconds(60), null, null));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public void StatusPageBoundaryAndCursorAreConservativeWithoutLookahead(int count, bool complete)
    {
        var input = Enumerable.Range(1, count).Select(seed => Status(Id(1000 + seed))).ToList();
        var page = new ObjectCleanupStatusPage(Project, At, input);
        input.Clear();
        Assert.Equal(count, page.ExaminedCount);
        Assert.Equal(complete, page.IsComplete);
        if (complete) Assert.Null(page.Next);
        else
        {
            Assert.Equal(Id(1100), page.Next!.AfterRetirementId);
            Assert.Equal(Project, page.Next.Project);
        }
        Assert.Throws<NotSupportedException>(() => ((IList<ObjectCleanupStatusHeader>)page.Headers).Clear());
    }

    [Fact]
    public void StatusOrderingUsesNetworkUuidBytesAndRefusesDuplicateForeignOrDifferentTimeRows()
    {
        Guid[] order =
        [
            Guid.Parse("00000001-0000-4000-8000-000000000001"),
            Guid.Parse("00000100-0000-4000-8000-000000000001"),
            Guid.Parse("01000000-0000-4000-8000-000000000001")
        ];
        Assert.Equal(order, new ObjectCleanupStatusPage(Project, At, order.Select(id => Status(id))).Headers.Select(row => row.RetirementId));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupStatusPage(Project, At, order.Reverse().Select(id => Status(id))));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupStatusPage(Project, At, [Status(), Status()]));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupStatusPage(Project, At, [Status(observed: At.AddTicks(1))]));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupStatusPage(Project, At, [Status(scope: new RevisionScope(Id(1), Id(99), Id(3), Id(4)))]));
    }

    [Fact]
    public void BoundedSnapshotDisposesOverflowingEnumerationAndObservesMidTraversalCancellation()
    {
        var visits = 0;
        var disposed = false;
        IEnumerable<ObjectCleanupStatusHeader> Infinite()
        {
            try
            {
                while (true)
                {
                    visits++;
                    if (visits > 101) throw new InvalidOperationException("The bounded constructor over-enumerated.");
                    yield return Status(Id(1000 + visits));
                }
            }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new ObjectCleanupStatusPage(Project, At, Infinite()));
        Assert.Equal(101, visits);
        Assert.True(disposed);
        using var cancellation = new CancellationTokenSource();
        IEnumerable<ObjectCleanupStatusHeader> Canceling()
        {
            cancellation.Cancel();
            yield return Status();
        }
        Assert.Throws<OperationCanceledException>(() => new ObjectCleanupStatusPage(Project, At, Canceling(), cancellation.Token));
    }
}
