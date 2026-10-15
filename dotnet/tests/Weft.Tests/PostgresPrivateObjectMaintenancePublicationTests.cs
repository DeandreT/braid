using Npgsql;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Actual registration metadata. Every absence/binding digest and inode is synthetic, never a filesystem observation.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresPrivateObjectMaintenancePublicationTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task BothActualOwnerKindsReserveBindAndReadExactNaturalKeysWithLosslessUnsignedIdentity()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var native = await test.ReserveNativeAsync();
        var nativeClaim = await test.ClaimNativeAsync(native);
        var report = await test.ReserveReportAsync();
        var owners = new (IPrivateObjectPublicationRegistry Registry, PrivatePublicationOwner Owner, PrivateObjectId Object)[]
        {
            (test.NativePublications, PrivatePublicationOwner.ForIngestion(test.Actor, nativeClaim), native.NativeObject.Id),
            (test.ReportPublications, PrivatePublicationOwner.ForReport(test.Actor, report), report.CandidateObjectId)
        };
        foreach (var (registry, owner, target) in owners)
        {
            var root = Guid.NewGuid();
            var temp = Guid.NewGuid();
            var reserved = await registry.ReserveAsync(owner, root, target, temp, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
            Assert.Equal(PrivatePublicationReserveOutcome.Reserved, reserved.Outcome);
            var record = reserved.Snapshot!.Record;
            Assert.Null(record.Binding);
            Assert.Equal(owner, record.Reservation.Owner);
            Assert.Equal(new PrivatePublicationKey(root, target, temp), record.Reservation.Key);
            Assert.True(reserved.Snapshot.CanWrite);
            // Recover by the caller-known exact natural key, without using the
            // returned publication ID as an input or adopting another registration.
            var recovered = await registry.GetAsync(owner, root, target, temp);
            Assert.Equal(record, recovered!.Record);
            Assert.True(recovered.CanWrite);
            Assert.Equal(PrivatePublicationReserveOutcome.Existing,
                (await registry.ReserveAsync(owner, root, target, temp, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
            var identity = PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root);
            var bound = await registry.BindAsync(record.Reservation, identity, PostgresPrivateObjectMaintenanceTestSupport.BoundDigest);
            Assert.Equal(PrivatePublicationBindOutcome.Bound, bound.Outcome);
            Assert.Equal(identity, bound.Snapshot!.Record.Binding!.ActualIdentity);
            Assert.Equal(ulong.MaxValue, bound.Snapshot.Record.Binding.ActualIdentity.FileIdentity.Inode);
            Assert.Equal(uint.MaxValue, bound.Snapshot.Record.Binding.ActualIdentity.FileIdentity.DeviceMajor);
            Assert.Equal(PrivatePublicationBindOutcome.Existing,
                (await registry.BindAsync(record.Reservation, identity, PostgresPrivateObjectMaintenanceTestSupport.BoundDigest)).Outcome);
            Assert.Equal(bound.Snapshot.Record, (await registry.GetAsync(owner, root, target, temp))!.Record);
        }
        Assert.Equal(new long[] { 0, 0, 2 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task NaturalKeyAndSingleBindingReplayCompareAllImmutableFactsWithoutOverwriting()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var owner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
        var root = Guid.NewGuid(); var temp = Guid.NewGuid();
        var reserved = await test.NativePublications.ReserveAsync(owner, root, facts.NativeObject.Id, temp, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
        var reservation = reserved.Snapshot!.Record.Reservation;
        var differentDigest = Sha256Digest.FromBytes("Different synthetic sealed control record"u8);
        var before = await test.PublicationSnapshotAsync(reservation.PublicationId);
        Assert.Equal(PrivatePublicationReserveOutcome.Conflict,
            (await test.NativePublications.ReserveAsync(owner, root, facts.NativeObject.Id, temp, differentDigest)).Outcome);
        Assert.Equal(before, await test.PublicationSnapshotAsync(reservation.PublicationId));
        var identity = PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root);
        Assert.Equal(PrivatePublicationBindOutcome.Bound,
            (await test.NativePublications.BindAsync(reservation, identity, PostgresPrivateObjectMaintenanceTestSupport.BoundDigest)).Outcome);
        before = await test.PublicationSnapshotAsync(reservation.PublicationId);
        Assert.Equal(PrivatePublicationBindOutcome.Conflict,
            (await test.NativePublications.BindAsync(reservation, identity, differentDigest)).Outcome);
        var differentIncarnation = new PrivatePublicationFileIdentity(root, identity.ControlNamespaceId, Guid.NewGuid(),
            identity.MountNamespaceDevice, identity.MountNamespaceInode, identity.EffectiveUserId,
            identity.FileIdentity, identity.DirectoryIdentity);
        Assert.Equal(PrivatePublicationBindOutcome.Conflict,
            (await test.NativePublications.BindAsync(reservation, differentIncarnation, PostgresPrivateObjectMaintenanceTestSupport.BoundDigest)).Outcome);
        Assert.Equal(before, await test.PublicationSnapshotAsync(reservation.PublicationId));
        Assert.Null(await test.NativePublications.GetAsync(owner, root, facts.NativeObject.Id, Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task AClaimShapedWrongTokenInstanceGenerationOrFullNativeTargetNeverRegisters()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var actual = await test.ClaimNativeAsync(facts);
        IngestionClaim Copy(Guid? instance = null, Guid? token = null, long? generation = null) => new(actual.Project,
            actual.OperationId, actual.OriginalActor, instance ?? actual.ServerInstanceId, token ?? actual.LeaseToken,
            generation ?? actual.Generation, actual.LeaseExpiresAtUtc);
        foreach (var claim in new[] { Copy(instance: Guid.NewGuid()), Copy(token: Guid.NewGuid()), Copy(generation: 2) })
            Assert.Equal(PrivatePublicationReserveOutcome.OwnerUnavailable,
                (await test.NativePublications.ReserveAsync(PrivatePublicationOwner.ForIngestion(test.Actor, claim),
                    Guid.NewGuid(), facts.NativeObject.Id, Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
        var wrongScope = new RevisionScope(facts.FutureRevision.OrganizationId, facts.FutureRevision.ProjectId,
            Guid.NewGuid(), facts.FutureRevision.RevisionId);
        var wrongTarget = PrivateObjectId.Restore(wrongScope, facts.NativeObject.Id.ObjectId, PrivateObjectClass.NativeArtifact);
        Assert.Equal(PrivatePublicationReserveOutcome.OwnerUnavailable,
            (await test.NativePublications.ReserveAsync(PrivatePublicationOwner.ForIngestion(test.Actor, actual),
                Guid.NewGuid(), wrongTarget, Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task RevokedOwnerRetainsExactAcknowledgementButCannotReserveOrBindAgain()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var owner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
        var root = Guid.NewGuid(); var temp = Guid.NewGuid();
        var registered = await test.NativePublications.ReserveAsync(owner, root, facts.NativeObject.Id, temp, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
        var reservation = registered.Snapshot!.Record.Reservation;
        var before = await test.PublicationSnapshotAsync(reservation.PublicationId);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
        var retained = await test.NativePublications.GetAsync(owner, root, facts.NativeObject.Id, temp);
        Assert.NotNull(retained);
        Assert.Equal(registered.Snapshot.Record, retained.Record);
        Assert.False(retained.OwnerIsLive);
        Assert.False(retained.CanWrite);
        Assert.Equal(PrivatePublicationReserveOutcome.OwnerUnavailable,
            (await test.NativePublications.ReserveAsync(owner, root, facts.NativeObject.Id, temp, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
        Assert.Equal(PrivatePublicationBindOutcome.OwnerUnavailable,
            (await test.NativePublications.BindAsync(reservation, PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root),
                PostgresPrivateObjectMaintenanceTestSupport.BoundDigest)).Outcome);
        Assert.Equal(before, await test.PublicationSnapshotAsync(reservation.PublicationId));
    }

    [PostgresFact]
    public async Task IngestionNeedsPublisherWhileTheSameActualReportOwnerMayStillPublishAsAViewer()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var nativeOwner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
        var report = await test.ReserveReportAsync();
        var reportOwner = PrivatePublicationOwner.ForReport(test.Actor, report);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
        Assert.Equal(PrivatePublicationReserveOutcome.OwnerUnavailable,
            (await test.NativePublications.ReserveAsync(nativeOwner, Guid.NewGuid(), facts.NativeObject.Id,
                Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
        Assert.Equal(PrivatePublicationReserveOutcome.Reserved,
            (await test.ReportPublications.ReserveAsync(reportOwner, Guid.NewGuid(), report.CandidateObjectId,
                Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
        Assert.Equal(new long[] { 0, 0, 1 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task AnInactivePrincipalOrOrganizationAdmissionDeniesBothActualOwnerKindsBeforeRegistration()
    {
        foreach (var authority in new[] { "principal", "organization" })
        {
            await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
            var facts = await test.ReserveNativeAsync();
            var native = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
            var report = await test.ReserveReportAsync();
            if (authority == "principal")
                await test.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", test.Actor.PrincipalId.Value);
            else
                await test.Case.ExecuteAsync("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
                    test.Project.OrganizationId, test.Actor.PrincipalId.Value);
            Assert.Equal(PrivatePublicationReserveOutcome.OwnerUnavailable,
                (await test.NativePublications.ReserveAsync(native, Guid.NewGuid(), facts.NativeObject.Id,
                    Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
            Assert.Equal(PrivatePublicationReserveOutcome.OwnerUnavailable,
                (await test.ReportPublications.ReserveAsync(PrivatePublicationOwner.ForReport(test.Actor, report), Guid.NewGuid(),
                    report.CandidateObjectId, Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
            Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
        }
    }

    [PostgresFact]
    public async Task ARevocationCommittedDuringTheActualOriginWaitIsRecheckedBeforeRegistration()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var publicationOwner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
        var pid = await PostgresPrivateObjectMaintenanceTestSupport.BackendAsync(test.IngestionDatabase.DataSource);
        await using var owner = await test.OpenOwnerAsync();
        await using var held = await owner.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT operation_id FROM weft.revision_ingestions WHERE operation_id=$1 FOR UPDATE", owner, held))
        {
            command.Parameters.AddWithValue(facts.OperationId);
            Assert.Equal(facts.OperationId, await command.ExecuteScalarAsync());
        }
        var pending = test.NativePublications.ReserveAsync(publicationOwner, Guid.NewGuid(), facts.NativeObject.Id,
            Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
        try
        {
            await test.WaitForBlockedAsync(pid, owner.ProcessID, pending);
            await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
            await held.CommitAsync();
            Assert.Equal(PrivatePublicationReserveOutcome.OwnerUnavailable, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        }
        finally { await held.DisposeAsync(); try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch when (pending.IsCompleted) { } }
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task AnOldCapturedNativeTempRetiresIndependentlyOfItsResumableFinalAndUnrelatedSuccessor()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var original = await test.ClaimNativeAsync(facts);
        var owner = PrivatePublicationOwner.ForIngestion(test.Actor, original);
        var root = Guid.NewGuid(); var temp = Guid.NewGuid();
        var registered = await test.NativePublications.ReserveAsync(owner, root, facts.NativeObject.Id, temp, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
        var aged = await test.SyntheticAgePublicationAsync(registered.Snapshot!.Record.Reservation, TimeSpan.FromHours(25));
        var successor = await test.ClaimNativeAsync(facts);
        Assert.Equal(2, successor.Generation);
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        Assert.Equal(CleanupTargetKind.TrackedTemporary, claim.Retirement.Target.Kind);
        Assert.Equal(aged.PublicationId, claim.Retirement.Target.PublicationAttemptId);
        Assert.Equal(aged.Owner, claim.Retirement.Publication!.Reservation.Owner);
        Assert.Equal(1, claim.Retirement.Publication.Reservation.Owner.Generation);
        Assert.Equal(ObjectCleanupReason.AbandonedTrackedTemporary, claim.Retirement.Reason);
        Assert.Equal(ObjectCleanupFinishOutcome.Applied, (await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Missing)).Outcome);
        var old = await test.NativePublications.GetAsync(aged.Owner, root, facts.NativeObject.Id, temp);
        Assert.True(old!.TemporaryRetired);
        Assert.False(old.FinalObjectRetired);
        Assert.False(old.CanWrite);
        Assert.Equal(PrivatePublicationBindOutcome.OwnerUnavailable,
            (await test.NativePublications.BindAsync(aged, PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root), PostgresPrivateObjectMaintenanceTestSupport.BoundDigest)).Outcome);
        var newOwner = PrivatePublicationOwner.ForIngestion(test.Actor, successor);
        var next = await test.NativePublications.ReserveAsync(newOwner, root, facts.NativeObject.Id, Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
        Assert.Equal(PrivatePublicationReserveOutcome.Reserved, next.Outcome);
        Assert.Equal(IngestionStoreOutcome.Applied, (await test.Ingestions.MarkStagedAsync(test.Actor, successor, facts.NativeObject)).Outcome);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_retirements WHERE target_kind='final_object'"));
    }

    [PostgresFact]
    public async Task ASeparateOldBoundReportTempCanRetireWhileItsUsableFinalReferenceRemainsProtected()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var report = await test.ReserveReportAsync();
        var owner = PrivatePublicationOwner.ForReport(test.Actor, report);
        var root = Guid.NewGuid(); var temp = Guid.NewGuid();
        var reserved = await test.ReportPublications.ReserveAsync(owner, root, report.CandidateObjectId, temp, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
        var reservation = reserved.Snapshot!.Record.Reservation;
        var identity = PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root);
        Assert.Equal(PrivatePublicationBindOutcome.Bound,
            (await test.ReportPublications.BindAsync(reservation, identity, PostgresPrivateObjectMaintenanceTestSupport.BoundDigest)).Outcome);
        var published = await test.Reports.FinishAsync(test.Actor, report, PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(report));
        Assert.Equal(RevisionReportStoreOutcome.Applied, published.Outcome);
        var aged = await test.SyntheticAgePublicationAsync(reservation, TimeSpan.FromHours(25));
        var claim = await test.ClaimSingleAsync(report.CandidateObjectId);
        Assert.Equal(CleanupTargetKind.TrackedTemporary, claim.Retirement.Target.Kind);
        Assert.Equal(identity, claim.Retirement.Publication!.Binding!.ActualIdentity);
        Assert.Equal(aged.Owner.LeaseExpiresAtUtc, claim.Retirement.EligibilityOriginAtUtc);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_retirements WHERE target_kind='final_object'"));
        var retained = await test.ReportReader.GetRetainedAsync(test.Actor, test.Revision, RevisionReportOperation.Projection, report.DerivationId);
        Assert.Equal(report.CandidateObjectId, retained!.Result!.DerivedObject.Id);
        Assert.Equal(ObjectCleanupFinishOutcome.Applied, (await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Deleted)).Outcome);
        Assert.True((await test.ReportPublications.GetAsync(aged.Owner, root, report.CandidateObjectId, temp))!.TemporaryRetired);
    }

    [PostgresFact]
    public async Task TheFourthTrackedTempStreamPaginatesItsOwnExactRegistrationsWithoutRetiringFinalObjects()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var expected = new HashSet<Guid>();
        for (var index = 0; index < 17; index++)
        {
            var facts = await test.ReserveNativeAsync();
            var owner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
            var reserved = await test.NativePublications.ReserveAsync(owner, Guid.NewGuid(), facts.NativeObject.Id,
                Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
            var aged = await test.SyntheticAgePublicationAsync(reserved.Snapshot!.Record.Reservation, TimeSpan.FromHours(25));
            expected.Add(aged.PublicationId);
        }
        var first = await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid());
        Assert.Equal(16, first.Claims.Count);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(ObjectCleanupScanStream.TrackedTemporary, first.NextCursor.Stream);
        var second = await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid(), first.NextCursor);
        Assert.Single(second.Claims);
        Assert.Null(second.NextCursor);
        var claims = first.Claims.Concat(second.Claims).ToArray();
        Assert.All(claims, claim => Assert.Equal(CleanupTargetKind.TrackedTemporary, claim.Retirement.Target.Kind));
        Assert.Equal(expected.Order(), claims.Select(c => c.Retirement.Target.PublicationAttemptId!.Value).Order());
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_retirements WHERE target_kind='final_object'"));
        Assert.Equal(new long[] { 17, 17, 17 }, await test.CountsAsync());
    }
}
