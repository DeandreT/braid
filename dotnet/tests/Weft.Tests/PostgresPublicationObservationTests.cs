using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Actual restricted PostgreSQL matching of authored Linux observations. No filesystem or native-worker ownership is asserted.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresPublicationObservationTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task EmptyAndHundredOrderedSlotsReturnOneResultEachWithLaterDatabaseObservationAndNoWrites()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var owner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
        var identity = PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(Guid.NewGuid());
        var reservation = await PostgresMaintenanceObservationTestSupport.BoundAsync(test, test.NativePublications, owner, facts.NativeObject.Id, identity);
        var before = await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test);
        var empty = await test.Maintenance.MatchAsync(new(test.Project, []));
        Assert.Empty(empty.Results);
        var slots = Enumerable.Range(0, 100).Reverse().Select(index => PrivateStorageSlotObservation.Final(index,
            facts.NativeObject.Id, index % 2 == 0 ? identity : CopyIdentity(identity, root: Guid.NewGuid()))).ToArray();
        var lower = await test.Case.ScalarAsync<DateTime>("SELECT clock_timestamp()");
        var result = await test.Maintenance.MatchAsync(new(test.Project, slots));
        var upper = await test.Case.ScalarAsync<DateTime>("SELECT clock_timestamp()");
        Assert.InRange(result.ObservedAtUtc.UtcDateTime, lower, upper);
        Assert.True(result.ObservedAtUtc >= empty.ObservedAtUtc);
        Assert.Equal(slots.Select(s => s.Ordinal), result.Results.Select(r => r.Ordinal));
        Assert.Equal(100, result.Results.Count);
        foreach (var observation in result.Results)
        {
            if (observation.Ordinal % 2 == 0)
            {
                Assert.Equal(PublicationObservationOutcome.ExactlyOne, observation.Outcome);
                var witness = Assert.Single(observation.Witnesses);
                Assert.Equal(reservation.PublicationId, witness.PublicationId);
                Assert.Equal(reservation.Key, witness.Key);
                Assert.Equal(identity, witness.ActualIdentity);
                Assert.Equal(ObjectCleanupOrigin.ForIngestion(facts.FutureRevision, facts.OperationId), witness.Origin);
            }
            else
            {
                Assert.Equal(PublicationObservationOutcome.NoMatch, observation.Outcome);
                Assert.Empty(observation.Witnesses);
            }
        }
        Assert.Equal(before, await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test));
    }

    [PostgresFact]
    public async Task MoreThanHundredMatchingRegistrationsYieldOnlyTwoOrderedWitnessesAndExactTemporaryLookup()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var owner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
        var identity = PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(Guid.NewGuid());
        var reservation = await PostgresMaintenanceObservationTestSupport.BoundAsync(test, test.NativePublications, owner, facts.NativeObject.Id,
            identity, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        var temps = Enumerable.Range(1, 101).Select(PostgresMaintenanceObservationTestSupport.OrderedId).Reverse().ToArray();
        await PostgresMaintenanceObservationTestSupport.AddAuthoredPublicationCopiesAsync(test, reservation.PublicationId, temps);
        var before = await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test);
        var slots = Enumerable.Range(0, 100).Select(index => PrivateStorageSlotObservation.Final(index, facts.NativeObject.Id, identity)).ToArray();
        var result = await test.Maintenance.MatchAsync(new(test.Project, slots));
        Assert.Equal(100, result.Results.Count);
        Assert.Equal(200, result.Results.Sum(r => r.Witnesses.Count));
        Assert.All(result.Results, item =>
        {
            Assert.Equal(PublicationObservationOutcome.Ambiguous, item.Outcome);
            Assert.Equal(new[] { PostgresMaintenanceObservationTestSupport.OrderedId(1), PostgresMaintenanceObservationTestSupport.OrderedId(2) },
                item.Witnesses.Select(w => w.Key.TempId));
            Assert.Equal(2, item.Witnesses.Select(w => w.PublicationId).Distinct().Count());
        });
        var temporary = PrivateStorageSlotObservation.Temporary(31, facts.FutureRevision, PrivateObjectClass.NativeArtifact, temps[0], identity);
        var exact = Assert.Single((await test.Maintenance.MatchAsync(new(test.Project, [temporary]))).Results);
        Assert.Equal(PublicationObservationOutcome.ExactlyOne, exact.Outcome);
        Assert.Equal(temps[0], Assert.Single(exact.Witnesses).Key.TempId);
        Assert.Equal(before, await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test));
    }

    [PostgresFact]
    public async Task ATemporaryNameWithoutObjectIdMatchesTwoRealReportOriginsInNetworkObjectOrder()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var first = await test.ReserveReportAsync(test.ReportKey("observation.first"));
        var second = await test.ReserveReportAsync(test.ReportKey("observation.second"));
        var identity = PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(Guid.NewGuid());
        var tempId = Guid.NewGuid();
        var reservations = new[]
        {
            await PostgresMaintenanceObservationTestSupport.BoundAsync(test, test.ReportPublications,
                PrivatePublicationOwner.ForReport(test.Actor, first), first.CandidateObjectId, identity, tempId),
            await PostgresMaintenanceObservationTestSupport.BoundAsync(test, test.ReportPublications,
                PrivatePublicationOwner.ForReport(test.Actor, second), second.CandidateObjectId, identity, tempId)
        };
        var slot = PrivateStorageSlotObservation.Temporary(7, test.Revision, PrivateObjectClass.DerivedArtifact, tempId, identity);
        Assert.Null(slot.ObjectId);
        var observed = Assert.Single((await test.Maintenance.MatchAsync(new(test.Project, [slot]))).Results);
        Assert.Equal(PublicationObservationOutcome.Ambiguous, observed.Outcome);
        var expected = reservations.OrderBy(r => r.Key.Object.ObjectId, Comparer<Guid>.Create(Weft.Application.CatalogReadGuard.CompareIds)).ToArray();
        Assert.Equal(expected.Select(r => r.PublicationId), observed.Witnesses.Select(w => w.PublicationId));
        Assert.All(observed.Witnesses, w => Assert.Equal(tempId, w.Key.TempId));
        Assert.Equal(new[] { first.DerivationId, second.DerivationId }.Order(), observed.Witnesses.Select(w => w.Origin.DerivationId!.Value).Order());
        var finals = await test.Maintenance.MatchAsync(new(test.Project,
            [PrivateStorageSlotObservation.Final(3, first.CandidateObjectId, identity), PrivateStorageSlotObservation.Final(1, second.CandidateObjectId, identity)]));
        Assert.All(finals.Results, r => Assert.Equal(PublicationObservationOutcome.ExactlyOne, r.Outcome));
    }

    [PostgresFact]
    public async Task EveryUnsignedBindingComponentAndFullAncestryMustMatchWhileUnboundRegistrationsStayUnproved()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var owner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
        var identity = PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(Guid.NewGuid());
        // This deliberately exercises values beyond signed64 and signed32; it is
        // authored Linux metadata, not observation of a real filesystem inode.
        identity = CopyIdentity(identity, uid: uint.MaxValue);
        var reservation = await PostgresMaintenanceObservationTestSupport.BoundAsync(test, test.NativePublications, owner, facts.NativeObject.Id, identity);
        var unboundTemp = Guid.NewGuid();
        Assert.Equal(PrivatePublicationReserveOutcome.Reserved, (await test.NativePublications.ReserveAsync(owner, PrivatePublicationStoreKind.FileSystem,
            identity.RootId, facts.NativeObject.Id, unboundTemp, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
        var changed = new[]
        {
            CopyIdentity(identity, root: Guid.NewGuid()), CopyIdentity(identity, control: Guid.NewGuid()),
            CopyIdentity(identity, boot: Guid.NewGuid()), CopyIdentity(identity, namespaceDevice: 1),
            CopyIdentity(identity, namespaceInode: 2), CopyIdentity(identity, uid: 0),
            CopyIdentity(identity, fileInode: 3), CopyIdentity(identity, directoryInode: 4),
            CopyIdentity(identity, major: 5), CopyIdentity(identity, minor: 6), CopyIdentity(identity, mount: 7)
        };
        var slots = changed.Select((actual, index) => PrivateStorageSlotObservation.Final(index, facts.NativeObject.Id, actual)).ToList();
        slots.Add(PrivateStorageSlotObservation.Temporary(20, facts.FutureRevision, PrivateObjectClass.NativeArtifact, unboundTemp, identity));
        slots.Add(PrivateStorageSlotObservation.Temporary(21, facts.FutureRevision, PrivateObjectClass.NativeArtifact, Guid.NewGuid(), identity));
        slots.Add(PrivateStorageSlotObservation.Final(22, PrivateObjectId.Restore(facts.FutureRevision, Guid.NewGuid(), PrivateObjectClass.NativeArtifact), identity));
        slots.Add(PrivateStorageSlotObservation.Final(23, PrivateObjectId.Restore(facts.FutureRevision, facts.NativeObject.Id.ObjectId, PrivateObjectClass.DerivedArtifact), identity));
        var wrongMapping = new RevisionScope(test.Project.OrganizationId, test.Project.ProjectId, Guid.NewGuid(), facts.FutureRevision.RevisionId);
        var wrongRevision = new RevisionScope(test.Project.OrganizationId, test.Project.ProjectId, facts.FutureRevision.MappingId, Guid.NewGuid());
        slots.Add(PrivateStorageSlotObservation.Temporary(24, wrongMapping, PrivateObjectClass.NativeArtifact, reservation.Key.TempId, identity));
        slots.Add(PrivateStorageSlotObservation.Temporary(25, wrongRevision, PrivateObjectClass.NativeArtifact, reservation.Key.TempId, identity));
        var before = await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test);
        Assert.All((await test.Maintenance.MatchAsync(new(test.Project, slots))).Results, r =>
        {
            Assert.Equal(PublicationObservationOutcome.NoMatch, r.Outcome);
            Assert.Empty(r.Witnesses);
        });
        var positive = Assert.Single((await test.Maintenance.MatchAsync(new(test.Project,
            [PrivateStorageSlotObservation.Final(99, facts.NativeObject.Id, identity)]))).Results);
        Assert.Equal(identity, Assert.Single(positive.Witnesses).ActualIdentity);
        Assert.Equal(ulong.MaxValue, positive.Witnesses[0].ActualIdentity.FileIdentity.Inode);
        Assert.Equal(uint.MaxValue, positive.Witnesses[0].ActualIdentity.EffectiveUserId);
        Assert.Equal(before, await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test));
    }

    [PostgresFact]
    public async Task MatchingAfterOwnerRevocationIsOnlyObservationAndBothReadersResetOneSharedPool()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var owner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
        var identity = PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(Guid.NewGuid());
        var reservation = await PostgresMaintenanceObservationTestSupport.BoundAsync(test, test.NativePublications, owner, facts.NativeObject.Id, identity);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
        var before = await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test);
        var pid = await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(test.MaintenanceDatabase.DataSource);
        var positive = Assert.Single((await test.Maintenance.MatchAsync(new(test.Project,
            [PrivateStorageSlotObservation.Temporary(8, facts.FutureRevision, PrivateObjectClass.NativeArtifact, reservation.Key.TempId, identity)]))).Results);
        Assert.Equal(PublicationObservationOutcome.ExactlyOne, positive.Outcome);
        var foreign = new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB);
        var foreignScope = new RevisionScope(foreign.OrganizationId, foreign.ProjectId, facts.FutureRevision.MappingId, facts.FutureRevision.RevisionId);
        Assert.Equal(PublicationObservationOutcome.NoMatch, Assert.Single((await test.Maintenance.MatchAsync(new(foreign,
            [PrivateStorageSlotObservation.Temporary(8, foreignScope, PrivateObjectClass.NativeArtifact, reservation.Key.TempId, identity)]))).Results).Outcome);
        Assert.Empty((await test.Maintenance.GetStatusesAsync(foreign)).Headers);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Maintenance.MatchAsync(new(test.Project, []), cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Maintenance.GetStatusesAsync(test.Project, cancellationToken: cancel.Token));
        await PostgresMaintenanceObservationTestSupport.AssertUnboundAsync(test, pid);
        Assert.Equal(before, await PostgresMaintenanceObservationTestSupport.SnapshotAsync(test));
    }

    private static PrivatePublicationFileIdentity CopyIdentity(PrivatePublicationFileIdentity value,
        Guid? root = null, Guid? control = null, Guid? boot = null, ulong? namespaceDevice = null,
        ulong? namespaceInode = null, uint? uid = null, ulong? fileInode = null, ulong? directoryInode = null,
        uint? major = null, uint? minor = null, ulong? mount = null) => new(root ?? value.RootId,
            control ?? value.ControlNamespaceId, boot ?? value.LinuxBootId, namespaceDevice ?? value.MountNamespaceDevice,
            namespaceInode ?? value.MountNamespaceInode, uid ?? value.EffectiveUserId,
            new(fileInode ?? value.FileIdentity.Inode, major ?? value.FileIdentity.DeviceMajor, minor ?? value.FileIdentity.DeviceMinor, mount ?? value.FileIdentity.MountId),
            new(directoryInode ?? value.DirectoryIdentity.Inode, major ?? value.DirectoryIdentity.DeviceMajor, minor ?? value.DirectoryIdentity.DeviceMinor, mount ?? value.DirectoryIdentity.MountId));
}
