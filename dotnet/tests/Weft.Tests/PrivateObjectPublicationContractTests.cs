using Weft.Application;
using Weft.Domain;
using static Weft.Tests.PrivateObjectMaintenanceContractFacts;

namespace Weft.Tests;

/// <summary>Authored contract facts only; no registry, inode ownership or real SQL result is simulated.</summary>
public sealed class PrivateObjectPublicationContractTests
{
    [Fact]
    public void NaturalKeyRetainsRootClassObjectEveryScopeComponentAndTemporaryId()
    {
        var original = new PrivatePublicationKey(Id(10), Native(), Id(26));
        var variants = new List<PrivatePublicationKey>
        {
            new(Id(99), Native(), Id(26)), new(Id(10), Native(), Id(99)),
            new(Id(10), Native(Id(99)), Id(26)),
            new(Id(10), PrivateObjectId.Restore(Scope, Native().ObjectId, PrivateObjectClass.DerivedArtifact), Id(26))
        };
        variants.AddRange(ForeignScopes().Select(scope => new PrivatePublicationKey(Id(10), Native(scope: scope), Id(26))));
        Assert.Equal(original, new PrivatePublicationKey(Id(10), Native(), Id(26)));
        Assert.All(variants, variant => Assert.NotEqual(original, variant));
        Assert.Equal(9, new HashSet<PrivatePublicationKey>(variants) { original }.Count);
        Assert.Throws<ArgumentException>(() => new PrivatePublicationKey(Guid.Empty, Native(), Id(26)));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationKey(Id(10), Native(), Guid.Empty));
    }

    [Fact]
    public void ActualLinuxIdentityPreservesUnsignedExtremesAndAllowsRealZeroDeviceAndUid()
    {
        var file = new PrivatePublicationInodeIdentity(ulong.MaxValue, uint.MaxValue, uint.MaxValue, ulong.MaxValue);
        var directory = new PrivatePublicationInodeIdentity(ulong.MaxValue - 1, uint.MaxValue, uint.MaxValue, ulong.MaxValue);
        var identity = new PrivatePublicationFileIdentity(Id(10), Id(11), Id(12), ulong.MaxValue, ulong.MaxValue,
            uint.MaxValue, file, directory);
        Assert.Equal((ulong.MaxValue, uint.MaxValue, uint.MaxValue, ulong.MaxValue),
            (identity.FileIdentity.Inode, identity.FileIdentity.DeviceMajor, identity.FileIdentity.DeviceMinor, identity.FileIdentity.MountId));
        Assert.Equal((ulong.MaxValue, ulong.MaxValue, uint.MaxValue),
            (identity.MountNamespaceDevice, identity.MountNamespaceInode, identity.EffectiveUserId));
        Assert.Equal(identity, new PrivatePublicationFileIdentity(Id(10), Id(11), Id(12), ulong.MaxValue, ulong.MaxValue,
            uint.MaxValue, file, directory));
        Assert.Equal((0UL, 0U, 0U, 0U), (FileIdentity().MountNamespaceDevice, FileIdentity().EffectiveUserId,
            FileIdentity().FileIdentity.DeviceMajor, FileIdentity().FileIdentity.DeviceMinor));
    }

    [Fact]
    public void IdentityEqualityNeverRebindsAnotherRootBootOrMountIncarnation()
    {
        var original = FileIdentity();
        PrivatePublicationFileIdentity[] changed =
        [
            FileIdentity(Id(99)),
            new(Id(10), Id(99), Id(12), 0, 14, 0, original.FileIdentity, original.DirectoryIdentity),
            new(Id(10), Id(11), Id(99), 0, 14, 0, original.FileIdentity, original.DirectoryIdentity),
            new(Id(10), Id(11), Id(12), 1, 14, 0, original.FileIdentity, original.DirectoryIdentity),
            new(Id(10), Id(11), Id(12), 0, 99, 0, original.FileIdentity, original.DirectoryIdentity),
            new(Id(10), Id(11), Id(12), 0, 14, 1, original.FileIdentity, original.DirectoryIdentity),
            new(Id(10), Id(11), Id(12), 0, 14, 0, new(99, 0, 0, 17), original.DirectoryIdentity),
            new(Id(10), Id(11), Id(12), 0, 14, 0, original.FileIdentity, new(99, 0, 0, 17))
        ];
        Assert.Equal(original, FileIdentity());
        Assert.All(changed, identity => Assert.NotEqual(original, identity));
        Assert.Equal(changed.Length + 1, new HashSet<PrivatePublicationFileIdentity>(changed) { original }.Count);
    }

    [Theory]
    [InlineData(16UL, 0U, 0U, 17UL)]
    [InlineData(15UL, 1U, 0U, 17UL)]
    [InlineData(15UL, 0U, 1U, 17UL)]
    [InlineData(15UL, 0U, 0U, 18UL)]
    public void TemporaryInodeMustBeDistinctAndOnTheContainingDirectoryMount(ulong inode, uint major, uint minor, ulong mount)
    {
        var directory = new PrivatePublicationInodeIdentity(inode, major, minor, mount);
        Assert.Throws<ArgumentException>(() => new PrivatePublicationFileIdentity(Id(10), Id(11), Id(12), 0, 14, 0,
            new PrivatePublicationInodeIdentity(16, 0, 0, 17), directory));
    }

    [Fact]
    public void ActualIdentityRequiresNonzeroObservedIdentifiersWithoutInventingThem()
    {
        var original = FileIdentity();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrivatePublicationInodeIdentity(0, 0, 0, 17));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrivatePublicationInodeIdentity(16, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrivatePublicationFileIdentity(Id(10), Id(11), Id(12),
            0, 0, 0, original.FileIdentity, original.DirectoryIdentity));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationFileIdentity(Guid.Empty, Id(11), Id(12),
            0, 14, 0, original.FileIdentity, original.DirectoryIdentity));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationFileIdentity(Id(10), Guid.Empty, Id(12),
            0, 14, 0, original.FileIdentity, original.DirectoryIdentity));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationFileIdentity(Id(10), Id(11), Guid.Empty,
            0, 14, 0, original.FileIdentity, original.DirectoryIdentity));
    }

    [Fact]
    public void PublicationOwnerRetainsTheActualClaimAndRejectsAnotherActor()
    {
        var ingestion = IngestionClaim(generation: 2);
        var report = ReportClaim();
        var nativeOwner = PrivatePublicationOwner.ForIngestion(Actor, ingestion);
        var reportOwner = PrivatePublicationOwner.ForReport(Actor, report);
        Assert.Same(ingestion, nativeOwner.IngestionClaim);
        Assert.Null(nativeOwner.ReportClaim);
        Assert.Same(report, reportOwner.ReportClaim);
        Assert.Null(reportOwner.IngestionClaim);
        Assert.Equal((PrivateObjectOwnerKind.NativeIngestion, ingestion.ServerInstanceId, ingestion.LeaseToken, ingestion.Generation),
            (nativeOwner.Kind, nativeOwner.ServerInstanceId, nativeOwner.LeaseToken, nativeOwner.Generation));
        Assert.Equal((PrivateObjectOwnerKind.ReportAttempt, report.ServerInstanceId, report.LeaseToken, report.Generation),
            (reportOwner.Kind, reportOwner.ServerInstanceId, reportOwner.LeaseToken, reportOwner.Generation));
        var otherActor = new ActorContext(new PrincipalId(Id(99)));
        Assert.Throws<ArgumentException>(() => PrivatePublicationOwner.ForIngestion(otherActor, ingestion));
        Assert.Throws<ArgumentException>(() => PrivatePublicationOwner.ForReport(otherActor, report));
        Assert.Throws<ArgumentException>(() => PrivatePublicationOwner.ForIngestion(Actor,
            IngestionClaim(expires: DateTimeOffset.MinValue.AddSeconds(IngestionWorkflowLimits.ClaimLeaseSeconds - 1))));
    }

    [Fact]
    public void IngestionCapabilityChecksProjectAndClassWithoutPretendingToKnowFutureReservedIds()
    {
        var claim = IngestionClaim();
        var owner = PrivatePublicationOwner.ForIngestion(Actor, claim);
        foreach (var foreign in ForeignScopes().Take(2))
            Assert.Throws<ArgumentException>(() => owner.OriginFor(Native(scope: foreign)));
        Assert.Throws<ArgumentException>(() => owner.OriginFor(Derived()));
        var future = Native(Id(99), new RevisionScope(Scope.OrganizationId, Scope.ProjectId, Id(98), Id(97)));
        // The existing ingestion claim has no future revision/object ID; the real registry must prove that reservation.
        Assert.Equal(ObjectCleanupOrigin.ForIngestion(future.Scope, claim.OperationId), owner.OriginFor(future));
    }

    [Fact]
    public void ReportCapabilityMatchesOnlyItsExactFullCandidateIdentity()
    {
        var claim = ReportClaim();
        var owner = PrivatePublicationOwner.ForReport(Actor, claim);
        Assert.Equal(ObjectCleanupOrigin.ForReport(Scope, claim.CacheEntryId, claim.DerivationId, claim.Generation),
            owner.OriginFor(claim.CandidateObjectId));
        var refused = ForeignScopes().Select(scope => Derived(claim.CandidateObjectId.ObjectId, scope)).ToList();
        refused.Add(Derived(Id(99)));
        refused.Add(PrivateObjectId.Restore(Scope, claim.CandidateObjectId.ObjectId, PrivateObjectClass.NativeArtifact));
        Assert.All(refused, candidate => Assert.Throws<ArgumentException>(() => owner.OriginFor(candidate)));
    }

    [Fact]
    public void ReservationFitsOnlyTheOriginalOwnerLeaseAndPreservesItsNaturalKey()
    {
        var original = Publication().Reservation;
        var owner = original.Owner;
        Assert.Equal(original, new PrivatePublicationReservation(original.PublicationId, original.Key, owner, AbsenceDigest, At));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationReservation(original.PublicationId, original.Key,
            owner, AbsenceDigest, At.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationReservation(original.PublicationId, original.Key,
            owner, AbsenceDigest, owner.LeaseExpiresAtUtc));
        var last = new PrivatePublicationReservation(original.PublicationId, original.Key, owner,
            AbsenceDigest, owner.LeaseExpiresAtUtc.AddTicks(-1));
        Assert.Equal(original.Key, last.Key);
        Assert.Throws<ArgumentException>(() => new PrivatePublicationReservation(Guid.Empty, original.Key, owner, AbsenceDigest, At));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationReservation(original.PublicationId,
            new PrivatePublicationKey(Id(10), Derived(), Id(26)), owner, AbsenceDigest, At));
    }

    [Fact]
    public void SameNaturalKeyNeverErasesTheOriginalOwnerFenceOrAbsenceCommitment()
    {
        var original = Publication().Reservation;
        var changedOwner = PrivatePublicationOwner.ForIngestion(Actor, IngestionClaim(token: Id(99)));
        var ownerVariant = new PrivatePublicationReservation(original.PublicationId, original.Key, changedOwner, AbsenceDigest, At);
        var absenceVariant = new PrivatePublicationReservation(original.PublicationId, original.Key, original.Owner, BoundDigest, At);
        Assert.Equal(original.Key, ownerVariant.Key);
        Assert.Equal(original.Key, absenceVariant.Key);
        Assert.NotEqual(original, ownerVariant);
        Assert.NotEqual(original, absenceVariant);
        Assert.NotEqual(original.Owner, ownerVariant.Owner);
        // Database replay must compare these retained facts; key equality alone is not authority.
    }

    [Fact]
    public void BoundIdentityAgreesWithTheReservedRootAndExclusiveLeaseBoundary()
    {
        var reservation = Publication().Reservation;
        Assert.NotNull(new PrivatePublicationRecord(reservation,
            new PrivatePublicationBinding(FileIdentity(), BoundDigest, reservation.RegisteredAtUtc)).Binding);
        Assert.NotNull(new PrivatePublicationRecord(reservation,
            new PrivatePublicationBinding(FileIdentity(), BoundDigest, reservation.Owner.LeaseExpiresAtUtc.AddTicks(-1))).Binding);
        Assert.Throws<ArgumentException>(() => new PrivatePublicationRecord(reservation,
            new PrivatePublicationBinding(FileIdentity(Id(99)), BoundDigest, At)));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationRecord(reservation,
            new PrivatePublicationBinding(FileIdentity(), BoundDigest, reservation.RegisteredAtUtc.AddTicks(-1))));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationRecord(reservation,
            new PrivatePublicationBinding(FileIdentity(), BoundDigest, reservation.Owner.LeaseExpiresAtUtc)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void PublicationTimestampsRequireExplicitUtc(int offsetHours)
    {
        var reservation = Publication().Reservation;
        var shifted = At.ToOffset(TimeSpan.FromHours(offsetHours));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationReservation(reservation.PublicationId, reservation.Key,
            reservation.Owner, AbsenceDigest, shifted));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationBinding(FileIdentity(), BoundDigest, shifted));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationSnapshot(Publication(), shifted, true, false, false));
    }

    [Fact]
    public void SnapshotCannotObserveFutureRegistrationBindingOrAnExpiredLiveOwner()
    {
        var unbound = Publication();
        var bound = Publication(bound: true);
        Assert.Throws<ArgumentException>(() => new PrivatePublicationSnapshot(unbound, At.AddTicks(-1), true, false, false));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationSnapshot(bound, At, true, false, false));
        var expiry = unbound.Reservation.Owner.LeaseExpiresAtUtc;
        Assert.Throws<ArgumentException>(() => new PrivatePublicationSnapshot(unbound, expiry, true, false, false));
        var retained = new PrivatePublicationSnapshot(bound, expiry, false, false, false);
        Assert.Same(bound, retained.Record);
        Assert.False(retained.CanWrite);
        Assert.Throws<ArgumentException>(() => PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Existing, retained));
        Assert.Throws<ArgumentException>(() => PrivatePublicationBindResult.ForSnapshot(PrivatePublicationBindOutcome.Existing, retained));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    public void RetainedFactsGrantNoSuccessfulResultWhenOwnershipOrEitherTargetIsUnavailable(
        bool live, bool finalRetired, bool temporaryRetired, bool writable)
    {
        var unbound = new PrivatePublicationSnapshot(Publication(), At.AddSeconds(2), live, finalRetired, temporaryRetired);
        var bound = new PrivatePublicationSnapshot(Publication(bound: true), At.AddSeconds(2), live, finalRetired, temporaryRetired);
        Assert.Equal(writable, unbound.CanWrite);
        Assert.Equal(writable, bound.CanWrite);
        if (writable)
        {
            Assert.Same(unbound, PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Reserved, unbound).Snapshot);
            Assert.Same(bound, PrivatePublicationBindResult.ForSnapshot(PrivatePublicationBindOutcome.Bound, bound).Snapshot);
        }
        else
        {
            foreach (var outcome in new[] { PrivatePublicationReserveOutcome.Reserved, PrivatePublicationReserveOutcome.Existing })
                Assert.Throws<ArgumentException>(() => PrivatePublicationReserveResult.ForSnapshot(outcome, unbound));
            foreach (var outcome in new[] { PrivatePublicationBindOutcome.Bound, PrivatePublicationBindOutcome.Existing })
                Assert.Throws<ArgumentException>(() => PrivatePublicationBindResult.ForSnapshot(outcome, bound));
        }
    }

    [Fact]
    public void SuccessfulFactoriesRequireTheAppropriateActualRegistrationStage()
    {
        var unbound = new PrivatePublicationSnapshot(Publication(), At, true, false, false);
        var bound = new PrivatePublicationSnapshot(Publication(bound: true), At.AddSeconds(1), true, false, false);
        Assert.Null(unbound.Record.Binding);
        Assert.Same(unbound, PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Existing, unbound).Snapshot);
        Assert.Same(bound, PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Existing, bound).Snapshot);
        Assert.Throws<ArgumentException>(() => PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Reserved, bound));
        foreach (var outcome in new[] { PrivatePublicationBindOutcome.Bound, PrivatePublicationBindOutcome.Existing })
        {
            Assert.Throws<ArgumentException>(() => PrivatePublicationBindResult.ForSnapshot(outcome, unbound));
            Assert.Same(bound, PrivatePublicationBindResult.ForSnapshot(outcome, bound).Snapshot);
        }
    }

    [Fact]
    public void RefusalFactoriesStayDataFreeAndUnknownOutcomesFailClosed()
    {
        var snapshot = new PrivatePublicationSnapshot(Publication(bound: true), At.AddSeconds(1), true, false, false);
        foreach (var outcome in new[] { PrivatePublicationReserveOutcome.OwnerUnavailable, PrivatePublicationReserveOutcome.Conflict,
                     PrivatePublicationReserveOutcome.Retired })
        {
            Assert.Null(PrivatePublicationReserveResult.WithoutSnapshot(outcome).Snapshot);
            Assert.Throws<ArgumentOutOfRangeException>(() => PrivatePublicationReserveResult.ForSnapshot(outcome, snapshot));
        }
        foreach (var outcome in new[] { PrivatePublicationBindOutcome.OwnerUnavailable, PrivatePublicationBindOutcome.Conflict,
                     PrivatePublicationBindOutcome.Retired })
        {
            Assert.Null(PrivatePublicationBindResult.WithoutSnapshot(outcome).Snapshot);
            Assert.Throws<ArgumentOutOfRangeException>(() => PrivatePublicationBindResult.ForSnapshot(outcome, snapshot));
        }
        foreach (var outcome in new[] { PrivatePublicationReserveOutcome.Reserved, PrivatePublicationReserveOutcome.Existing })
            Assert.Throws<ArgumentException>(() => PrivatePublicationReserveResult.WithoutSnapshot(outcome));
        foreach (var outcome in new[] { PrivatePublicationBindOutcome.Bound, PrivatePublicationBindOutcome.Existing })
            Assert.Throws<ArgumentException>(() => PrivatePublicationBindResult.WithoutSnapshot(outcome));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrivatePublicationReserveResult.WithoutSnapshot((PrivatePublicationReserveOutcome)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrivatePublicationBindResult.WithoutSnapshot((PrivatePublicationBindOutcome)99));
    }
}
