using Braid.Application;
using Braid.Domain;
using static Braid.Tests.PrivateObjectMaintenanceContractFacts;

namespace Braid.Tests;

/// <summary>Pure contracts with authored facts; these tests do not prove SQL eligibility or physical ownership.</summary>
public sealed class PrivateObjectMaintenanceContractTests
{
    [Fact]
    public void FinalAndTemporaryTargetsNeverAliasEvenForTheSameObject()
    {
        var final = new ObjectCleanupTarget(Native(), CleanupTargetKind.FinalObject);
        var temporary = new ObjectCleanupTarget(Native(), CleanupTargetKind.TrackedTemporary, Id(25));
        var otherPublication = new ObjectCleanupTarget(Native(), CleanupTargetKind.TrackedTemporary, Id(26));
        Assert.Equal(final, new ObjectCleanupTarget(Native(), CleanupTargetKind.FinalObject));
        Assert.Equal(3, new HashSet<ObjectCleanupTarget> { final, temporary, otherPublication }.Count);
        Assert.NotEqual(final, new ObjectCleanupTarget(Derived(), CleanupTargetKind.FinalObject));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupTarget(Native(), CleanupTargetKind.FinalObject, Id(25)));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupTarget(Native(), CleanupTargetKind.TrackedTemporary));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupTarget(Native(), CleanupTargetKind.TrackedTemporary, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectCleanupTarget(Native(), (CleanupTargetKind)99));
    }

    [Fact]
    public void OrphanIdentityDoesNotInventDigestCountOrSuccessfulDeletion()
    {
        var retirement = FinalRetirement(Derived(), ObjectCleanupReason.ExpiredDerivedCandidate);
        var attempt = new ObjectCleanupAttempt(CleanupClaim(retirement));
        Assert.Null(retirement.Publication);
        Assert.Null(attempt.Disposition);
        Assert.Null(attempt.FinishedAtUtc);
        Assert.False(attempt.IsSuccess);
        Assert.Equal(ObjectCleanupAttemptState.RetryWaiting, attempt.StateAt(attempt.Claim.LeaseExpiresAtUtc));
        Type[] identityOnly = [typeof(ObjectCleanupTarget), typeof(ObjectCleanupOrigin),
            typeof(ObjectCleanupRetirement), typeof(ObjectCleanupClaim), typeof(ObjectCleanupAttempt)];
        Assert.All(identityOnly, type => Assert.DoesNotContain(type.GetProperties(), property =>
            property.PropertyType == typeof(PrivateObjectReference) || property.PropertyType == typeof(Sha256Digest) ||
            property.Name is "ByteCount" or "Path"));
    }

    [Fact]
    public void OriginEqualityRetainsActualOperationAndReportGeneration()
    {
        var native = ObjectCleanupOrigin.ForIngestion(Scope, Id(22));
        var report = ObjectCleanupOrigin.ForReport(Scope, Id(23), Id(24), 1);
        Assert.Equal(native, ObjectCleanupOrigin.ForIngestion(Scope, Id(22)));
        Assert.NotEqual(native, ObjectCleanupOrigin.ForIngestion(Scope, Id(99)));
        Assert.NotEqual(report, ObjectCleanupOrigin.ForReport(Scope, Id(23), Id(24), 2));
        Assert.NotEqual(report, ObjectCleanupOrigin.ForReport(Scope, Id(23), Id(99), 1));
        Assert.NotEqual(report, ObjectCleanupOrigin.ForReport(OtherScope(), Id(23), Id(24), 1));
        Assert.Throws<ArgumentException>(() => ObjectCleanupOrigin.ForIngestion(Scope, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectCleanupOrigin.ForReport(Scope, Id(23), Id(24), 0));
    }

    [Fact]
    public void FinalRetirementRequiresFullScopeClassAndMatchingReason()
    {
        var native = new ObjectCleanupTarget(Native(), CleanupTargetKind.FinalObject);
        var nativeOrigin = ObjectCleanupOrigin.ForIngestion(Scope, Id(22));
        var reportOrigin = ObjectCleanupOrigin.ForReport(Scope, Id(23), Id(24), 1);
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), native, reportOrigin,
            ObjectCleanupReason.TerminalNativeStage, At, RetiredAt));
        foreach (var foreign in ForeignScopes())
            Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), native,
                ObjectCleanupOrigin.ForIngestion(foreign, Id(22)), ObjectCleanupReason.TerminalNativeStage, At, RetiredAt));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), native, nativeOrigin,
            ObjectCleanupReason.FailedDerivedCandidate, At, RetiredAt));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), native, nativeOrigin,
            ObjectCleanupReason.AbandonedTrackedTemporary, At, RetiredAt));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), native, nativeOrigin,
            ObjectCleanupReason.TerminalNativeStage, At, RetiredAt, Publication()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectCleanupRetirement(Id(30), native, nativeOrigin,
            (ObjectCleanupReason)99, At, RetiredAt));
        Assert.Equal(ObjectCleanupReason.FailedDerivedCandidate,
            FinalRetirement(Derived(), ObjectCleanupReason.FailedDerivedCandidate).Reason);
    }

    [Fact]
    public void GraceUsesTheExactUtcBoundaryWithoutRoundingUp()
    {
        var target = new ObjectCleanupTarget(Native(), CleanupTargetKind.FinalObject);
        var origin = ObjectCleanupOrigin.ForIngestion(Scope, Id(22));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), target, origin,
            ObjectCleanupReason.TerminalNativeStage, At, RetiredAt.AddTicks(-1)));
        Assert.Equal(RetiredAt, new ObjectCleanupRetirement(Id(30), target, origin,
            ObjectCleanupReason.TerminalNativeStage, At, RetiredAt).RetiredAtUtc);
    }

    [Fact]
    public void TrackedTemporaryRetirementRequiresItsExactPositiveRegistration()
    {
        var publication = Publication(bound: true);
        var reservation = publication.Reservation;
        var target = new ObjectCleanupTarget(reservation.Key.Object, CleanupTargetKind.TrackedTemporary, reservation.PublicationId);
        var eligibility = reservation.Owner.LeaseExpiresAtUtc;
        var retired = eligibility.AddHours(24);
        var accepted = new ObjectCleanupRetirement(Id(30), target, reservation.Origin,
            ObjectCleanupReason.AbandonedTrackedTemporary, eligibility, retired, publication);
        Assert.Same(publication, accepted.Publication);
        Assert.NotEqual(target, new ObjectCleanupTarget(target.Object, CleanupTargetKind.FinalObject));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), target, reservation.Origin,
            ObjectCleanupReason.AbandonedTrackedTemporary, eligibility, retired));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30),
            new ObjectCleanupTarget(target.Object, CleanupTargetKind.TrackedTemporary, reservation.Key.TempId), reservation.Origin,
            ObjectCleanupReason.AbandonedTrackedTemporary, eligibility, retired, publication));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30),
            new ObjectCleanupTarget(Native(Id(99)), CleanupTargetKind.TrackedTemporary, reservation.PublicationId), reservation.Origin,
            ObjectCleanupReason.AbandonedTrackedTemporary, eligibility, retired, publication));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), target,
            ObjectCleanupOrigin.ForIngestion(Scope, Id(99)), ObjectCleanupReason.AbandonedTrackedTemporary,
            eligibility, retired, publication));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), target, reservation.Origin,
            ObjectCleanupReason.AbandonedTrackedTemporary, eligibility.AddTicks(-1), retired, publication));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), target, reservation.Origin,
            ObjectCleanupReason.TerminalNativeStage, eligibility, retired, publication));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void MaintenanceTimestampsRequireExplicitUtc(int offsetHours)
    {
        var shifted = At.ToOffset(TimeSpan.FromHours(offsetHours));
        var target = new ObjectCleanupTarget(Native(), CleanupTargetKind.FinalObject);
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), target,
            ObjectCleanupOrigin.ForIngestion(Scope, Id(22)), ObjectCleanupReason.TerminalNativeStage, shifted, RetiredAt));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupRetirement(Id(30), target,
            ObjectCleanupOrigin.ForIngestion(Scope, Id(22)), ObjectCleanupReason.TerminalNativeStage,
            At, RetiredAt.ToOffset(shifted.Offset)));
        var claim = CleanupClaim();
        Assert.Throws<ArgumentException>(() => CleanupClaim(started: claim.StartedAtUtc.ToOffset(shifted.Offset)));
        Assert.Throws<ArgumentException>(() => CleanupClaim(expires: claim.LeaseExpiresAtUtc.ToOffset(shifted.Offset)));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupAttempt(claim,
            claim.StartedAtUtc.ToOffset(shifted.Offset), ObjectCleanupDisposition.Deleted));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupAttempt(claim).StateAt(claim.StartedAtUtc.ToOffset(shifted.Offset)));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupScanCursor(Project, ObjectCleanupScanStream.NativeStage, shifted, target));
    }

    [Fact]
    public void ClaimHasExactlySixtySecondsAndEightFencedGenerations()
    {
        var first = CleanupClaim();
        Assert.Equal(TimeSpan.FromSeconds(60), first.LeaseExpiresAtUtc - first.StartedAtUtc);
        Assert.Equal(8, CleanupClaim(generation: 8).Generation);
        Assert.Throws<ArgumentOutOfRangeException>(() => CleanupClaim(generation: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CleanupClaim(generation: 9));
        Assert.Throws<ArgumentException>(() => CleanupClaim(expires: first.StartedAtUtc.AddSeconds(59)));
        Assert.Throws<ArgumentException>(() => CleanupClaim(expires: first.StartedAtUtc.AddSeconds(61)));
        Assert.Throws<ArgumentException>(() => CleanupClaim(started: RetiredAt.AddTicks(-1), expires: RetiredAt.AddSeconds(60).AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => CleanupClaim(attemptId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CleanupClaim(instance: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CleanupClaim(token: Guid.Empty));
        var later = CleanupClaim(generation: 2, attemptId: Id(99), token: Id(98));
        Assert.NotEqual(first, later);
        Assert.Equal(first.Retirement, later.Retirement);
    }

    [Fact]
    public void LastRepresentableLeaseStillHasRoomForItsThirtySecondRetry()
    {
        var start = DateTimeOffset.MaxValue.AddSeconds(-90);
        var claim = CleanupClaim(started: start, expires: start.AddSeconds(60));
        var unfinished = new ObjectCleanupAttempt(claim);
        Assert.Equal(DateTimeOffset.MaxValue, unfinished.NextAttemptAtUtc);
        Assert.Equal(ObjectCleanupAttemptState.RetryDue, unfinished.StateAt(DateTimeOffset.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => CleanupClaim(started: start.AddTicks(1),
            expires: start.AddSeconds(60).AddTicks(1)));
    }

    [Fact]
    public void UnfinishedClaimExpiresIntoRetryWithoutInventingACompletion()
    {
        var attempt = new ObjectCleanupAttempt(CleanupClaim());
        var expiry = attempt.Claim.LeaseExpiresAtUtc;
        Assert.Equal(ObjectCleanupAttemptState.Leased, attempt.StateAt(expiry.AddTicks(-1)));
        Assert.Equal(ObjectCleanupAttemptState.RetryWaiting, attempt.StateAt(expiry));
        Assert.Equal(expiry.AddSeconds(30), attempt.NextAttemptAtUtc);
        Assert.Equal(ObjectCleanupAttemptState.RetryWaiting, attempt.StateAt(expiry.AddSeconds(30).AddTicks(-1)));
        Assert.Equal(ObjectCleanupAttemptState.RetryDue, attempt.StateAt(expiry.AddSeconds(30)));
        Assert.Null(attempt.Disposition);
        Assert.Throws<ArgumentException>(() => attempt.StateAt(attempt.Claim.StartedAtUtc.AddTicks(-1)));
    }

    [Theory]
    [InlineData(ObjectCleanupDisposition.Canceled)]
    [InlineData(ObjectCleanupDisposition.Deadline)]
    [InlineData(ObjectCleanupDisposition.StorageIo)]
    [InlineData(ObjectCleanupDisposition.UnsafeStorage)]
    public void FailedCompletionRetriesFromItsAcknowledgedFinish(ObjectCleanupDisposition disposition)
    {
        var claim = CleanupClaim();
        var finish = claim.StartedAtUtc.AddSeconds(5);
        var attempt = new ObjectCleanupAttempt(claim, finish, disposition);
        Assert.False(attempt.IsSuccess);
        Assert.Equal(finish.AddSeconds(30), attempt.NextAttemptAtUtc);
        Assert.Equal(ObjectCleanupAttemptState.RetryWaiting, attempt.StateAt(finish.AddSeconds(30).AddTicks(-1)));
        Assert.Equal(ObjectCleanupAttemptState.RetryDue, attempt.StateAt(finish.AddSeconds(30)));
        Assert.Throws<ArgumentException>(() => attempt.StateAt(finish.AddTicks(-1)));
    }

    [Theory]
    [InlineData(ObjectCleanupDisposition.Deleted)]
    [InlineData(ObjectCleanupDisposition.Missing)]
    public void SuccessfulCompletionRemainsTerminalEvenOnTheEighthAttempt(ObjectCleanupDisposition disposition)
    {
        var claim = CleanupClaim(generation: 8);
        var attempt = new ObjectCleanupAttempt(claim, claim.StartedAtUtc, disposition);
        Assert.True(attempt.IsSuccess);
        Assert.Null(attempt.NextAttemptAtUtc);
        Assert.Equal(ObjectCleanupAttemptState.Completed, attempt.StateAt(claim.LeaseExpiresAtUtc.AddDays(30)));
    }

    [Fact]
    public void EighthFailureOrUnfinishedExpiryIsExhaustedWithoutAnotherRetry()
    {
        var claim = CleanupClaim(generation: 8);
        var open = new ObjectCleanupAttempt(claim);
        Assert.Equal(ObjectCleanupAttemptState.Leased, open.StateAt(claim.LeaseExpiresAtUtc.AddTicks(-1)));
        Assert.Equal(ObjectCleanupAttemptState.Exhausted, open.StateAt(claim.LeaseExpiresAtUtc));
        var failed = new ObjectCleanupAttempt(claim, claim.StartedAtUtc, ObjectCleanupDisposition.StorageIo);
        Assert.Equal(ObjectCleanupAttemptState.Exhausted, failed.StateAt(claim.StartedAtUtc));
        Assert.Null(open.NextAttemptAtUtc);
        Assert.Null(failed.NextAttemptAtUtc);
        Assert.False(open.IsSuccess);
    }

    [Fact]
    public void CompletionRequiresPairedFactsInsideTheOriginalLease()
    {
        var claim = CleanupClaim();
        Assert.Throws<ArgumentException>(() => new ObjectCleanupAttempt(claim, claim.StartedAtUtc));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupAttempt(claim, disposition: ObjectCleanupDisposition.Deleted));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupAttempt(claim, claim.StartedAtUtc.AddTicks(-1), ObjectCleanupDisposition.Deleted));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupAttempt(claim, claim.LeaseExpiresAtUtc, ObjectCleanupDisposition.Deleted));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectCleanupAttempt(claim, claim.StartedAtUtc, (ObjectCleanupDisposition)99));
        Assert.True(new ObjectCleanupAttempt(claim, claim.LeaseExpiresAtUtc.AddTicks(-1), ObjectCleanupDisposition.Missing).IsSuccess);
    }

    [Fact]
    public void BatchOwnsItsSnapshotAndKeepsTheSixteenTargetHundredCandidateBounds()
    {
        var input = Enumerable.Range(0, 16).Select(seed => CleanupClaim(seed: seed)).ToList();
        var first = input[0];
        var cursor = new ObjectCleanupScanCursor(Project, ObjectCleanupScanStream.DueRetirement, At,
            input[^1].Retirement.Target);
        var batch = new ObjectCleanupClaimBatch(Project, input, 100, 40, 44, cursor);
        input[0] = CleanupClaim(seed: 90);
        input.Clear();
        Assert.Equal(16, batch.Claims.Count);
        Assert.Same(first, batch.Claims[0]);
        Assert.Same(cursor, batch.NextCursor);
        Assert.Throws<NotSupportedException>(() => ((IList<ObjectCleanupClaim>)batch.Claims).Clear());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectCleanupClaimBatch(Project, [], 101, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectCleanupClaimBatch(Project, [], -1, 0, 0));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupClaimBatch(Project, [first], 1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectCleanupClaimBatch(Project, [], 1, 2, 0));
    }

    [Fact]
    public void OversizedBatchStopsAfterTheSeventeenthItemWithoutDrainingAnUnboundedSource()
    {
        var enumerated = 0;
        var disposed = false;
        IEnumerable<ObjectCleanupClaim> Unbounded()
        {
            try
            {
                while (true)
                {
                    enumerated++;
                    if (enumerated > 17)
                        throw new InvalidOperationException("The batch consumed beyond its one-item overflow witness.");
                    yield return CleanupClaim(seed: enumerated);
                }
            }
            finally
            {
                disposed = true;
            }
        }
        Assert.Throws<ArgumentException>(() => new ObjectCleanupClaimBatch(Project, Unbounded(), 100, 0, 0));
        Assert.Equal(17, enumerated);
        Assert.True(disposed);
    }

    [Fact]
    public void BatchRejectsDuplicateTargetsRetirementsAttemptsAndForeignScopeOrInstance()
    {
        var first = CleanupClaim();
        var second = CleanupClaim(seed: 1);
        ObjectCleanupClaim[] conflicting =
        [
            CleanupClaim(FinalRetirement(first.Retirement.Target.Object, retirementId: Id(96)), attemptId: Id(99)),
            CleanupClaim(FinalRetirement(Native(Id(99)), retirementId: first.Retirement.RetirementId), attemptId: Id(98)),
            CleanupClaim(second.Retirement, attemptId: first.AttemptId),
            CleanupClaim(second.Retirement, instance: Id(99)),
            CleanupClaim(FinalRetirement(Native(scope: OtherScope())), attemptId: Id(97))
        ];
        Assert.All(conflicting, other => Assert.Throws<ArgumentException>(() =>
            new ObjectCleanupClaimBatch(Project, [first, other], 2, 0, 0)));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupClaimBatch(Project, [null!], 1, 0, 0));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupClaimBatch(Project, [], 0, 0, 0,
            new ObjectCleanupScanCursor(new ProjectScope(Scope.OrganizationId, Id(99)), ObjectCleanupScanStream.DueRetirement,
                At, new ObjectCleanupTarget(Native(scope: OtherScope()), CleanupTargetKind.FinalObject))));
    }

    [Fact]
    public void CursorRetainsTheCompleteTargetAndAdmitsOnlyItsMatchingStream()
    {
        var native = new ObjectCleanupTarget(Native(), CleanupTargetKind.FinalObject);
        var derived = new ObjectCleanupTarget(Derived(), CleanupTargetKind.FinalObject);
        var temp = new ObjectCleanupTarget(Native(), CleanupTargetKind.TrackedTemporary, Id(25));
        Assert.NotEqual(new ObjectCleanupScanCursor(Project, ObjectCleanupScanStream.DueRetirement, At, native),
            new ObjectCleanupScanCursor(Project, ObjectCleanupScanStream.DueRetirement, At, temp));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupScanCursor(Project, ObjectCleanupScanStream.NativeStage, At, derived));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupScanCursor(Project, ObjectCleanupScanStream.DerivedCandidate, At, temp));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupScanCursor(Project, ObjectCleanupScanStream.TrackedTemporary, At, native));
        Assert.Throws<ArgumentException>(() => new ObjectCleanupScanCursor(Project, ObjectCleanupScanStream.DueRetirement, At,
            new ObjectCleanupTarget(Native(scope: OtherScope()), CleanupTargetKind.FinalObject)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectCleanupScanCursor(Project, (ObjectCleanupScanStream)99, At, native));
    }

    [Fact]
    public void FinishFactoriesRequireAnExactCompletedAttemptAndKeepRefusalsDataFree()
    {
        var claim = CleanupClaim();
        var completed = new ObjectCleanupAttempt(claim, claim.StartedAtUtc, ObjectCleanupDisposition.StorageIo);
        foreach (var outcome in new[] { ObjectCleanupFinishOutcome.Applied, ObjectCleanupFinishOutcome.AlreadyApplied })
        {
            Assert.Same(completed, ObjectCleanupFinishResult.ForAttempt(outcome, completed).Attempt);
            Assert.Throws<ArgumentException>(() => ObjectCleanupFinishResult.ForAttempt(outcome, new ObjectCleanupAttempt(claim)));
            Assert.Throws<ArgumentException>(() => ObjectCleanupFinishResult.WithoutAttempt(outcome));
        }
        foreach (var outcome in new[] { ObjectCleanupFinishOutcome.ClaimLost, ObjectCleanupFinishOutcome.SafetyRefusal })
        {
            Assert.Null(ObjectCleanupFinishResult.WithoutAttempt(outcome).Attempt);
            Assert.Throws<ArgumentOutOfRangeException>(() => ObjectCleanupFinishResult.ForAttempt(outcome, completed));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectCleanupFinishResult.WithoutAttempt((ObjectCleanupFinishOutcome)99));
    }
}

/// <summary>Authored values for pure contracts only; no real worker, inode or SQL eligibility is implied.</summary>
internal static class PrivateObjectMaintenanceContractFacts
{
    internal static Guid Id(int value) => Guid.Parse($"00000000-0000-4000-8000-{value:x12}");
    internal static DateTimeOffset At => new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    internal static DateTimeOffset RetiredAt => At.AddHours(24);
    internal static RevisionScope Scope => new(Id(1), Id(2), Id(3), Id(4));
    internal static ProjectScope Project => new(Scope.OrganizationId, Scope.ProjectId);
    internal static ActorContext Actor => new(new PrincipalId(Id(7)));
    internal static Sha256Digest AbsenceDigest => Sha256Digest.FromBytes("SYNTHETIC-absence-record"u8);
    internal static Sha256Digest BoundDigest => Sha256Digest.FromBytes("SYNTHETIC-bound-record"u8);
    internal static RevisionScope OtherScope() => new(Scope.OrganizationId, Id(99), Scope.MappingId, Scope.RevisionId);
    internal static RevisionScope[] ForeignScopes() =>
    [
        new(Id(99), Scope.ProjectId, Scope.MappingId, Scope.RevisionId), OtherScope(),
        new(Scope.OrganizationId, Scope.ProjectId, Id(99), Scope.RevisionId),
        new(Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, Id(99))
    ];

    internal static PrivateObjectId Native(Guid? id = null, RevisionScope? scope = null) =>
        PrivateObjectId.Restore(scope ?? Scope, id ?? Id(5), PrivateObjectClass.NativeArtifact);
    internal static PrivateObjectId Derived(Guid? id = null, RevisionScope? scope = null) =>
        PrivateObjectId.Restore(scope ?? Scope, id ?? Id(6), PrivateObjectClass.DerivedArtifact);
    internal static IngestionClaim IngestionClaim(DateTimeOffset? expires = null, Guid? token = null, long generation = 1) =>
        new(Project, Id(22), Actor.PrincipalId, Id(20), token ?? Id(21), generation,
            expires ?? At.AddSeconds(IngestionWorkflowLimits.ClaimLeaseSeconds));
    internal static RevisionReportClaim ReportClaim(PrivateObjectId? candidate = null)
    {
        var profile = new RevisionReportBackendProfile("braid.synthetic", "SYNTHETIC-engine", "SYNTHETIC-image", "SYNTHETIC-adapter");
        var source = new RevisionReportSource(Scope, new PrivateObjectReference(Native(), Sha256Digest.FromBytes("abc"u8), 3),
            "entry.native.json", "braid.dependencies.empty.v1", profile.BackendId);
        var key = new ProjectionCacheKey(source, profile, "braid.mapping-projection.v1", "braid.synthetic-projection.v1",
            "braid.synthetic-codec.v1", new RevisionReportPolicy());
        return new(Id(23), Id(24), key, Actor.PrincipalId, Id(20), Id(21), 1, At, At.AddSeconds(60), candidate ?? Derived());
    }

    internal static PrivatePublicationFileIdentity FileIdentity(Guid? root = null) =>
        new(root ?? Id(10), Id(11), Id(12), 0, 14, 0,
            new PrivatePublicationInodeIdentity(16, 0, 0, 17), new PrivatePublicationInodeIdentity(15, 0, 0, 17));
    internal static PrivatePublicationRecord Publication(bool bound = false)
    {
        var owner = PrivatePublicationOwner.ForIngestion(Actor, IngestionClaim());
        var reservation = new PrivatePublicationReservation(Id(25), new PrivatePublicationKey(Id(10), Native(), Id(26)), PrivatePublicationStoreKind.FileSystem,
            owner, AbsenceDigest, At);
        return new(reservation, bound ? new PrivatePublicationBinding(FileIdentity(), BoundDigest, At.AddSeconds(1)) : null);
    }

    internal static ObjectCleanupRetirement FinalRetirement(PrivateObjectId? objectId = null,
        ObjectCleanupReason? reason = null, Guid? retirementId = null)
    {
        var target = objectId ?? Native();
        var origin = target.Class == PrivateObjectClass.NativeArtifact
            ? ObjectCleanupOrigin.ForIngestion(target.Scope, Id(22))
            : ObjectCleanupOrigin.ForReport(target.Scope, Id(23), Id(24), 1);
        return new(retirementId ?? Id(30), new ObjectCleanupTarget(target, CleanupTargetKind.FinalObject), origin,
            reason ?? (target.Class == PrivateObjectClass.NativeArtifact
                ? ObjectCleanupReason.TerminalNativeStage : ObjectCleanupReason.FailedDerivedCandidate), At, RetiredAt);
    }

    internal static ObjectCleanupClaim CleanupClaim(ObjectCleanupRetirement? retirement = null, int seed = 0,
        long generation = 1, DateTimeOffset? started = null, DateTimeOffset? expires = null,
        Guid? attemptId = null, Guid? instance = null, Guid? token = null)
    {
        var exact = retirement ?? FinalRetirement(Native(Id(1000 + seed)), retirementId: Id(100 + seed));
        var start = started ?? exact.RetiredAtUtc;
        return new(exact, attemptId ?? Id(200 + seed), generation, instance ?? Id(20), token ?? Id(21), start,
            expires ?? start.AddSeconds(60));
    }
}
