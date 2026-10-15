using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual PostgreSQL metadata; all time shifts and deletion dispositions are synthetic.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresPrivateObjectMaintenanceWorkflowTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ACommittedNativeSourceAndAnEmptyForeignProjectNeverBecomeCleanupClaims()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var before = await test.OriginSnapshotAsync();
        var batch = await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid());
        Assert.Empty(batch.Claims);
        Assert.InRange(batch.ExaminedCount, 0, 100);
        Assert.Empty((await test.Maintenance.ClaimAsync(new(test.Seed.OrganizationB, test.Seed.ProjectB), Guid.NewGuid())).Claims);
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
        Assert.Equal(before, await test.OriginSnapshotAsync());
    }

    [PostgresFact]
    public async Task ReservedStagedAndReadyRemainResumableDespiteAuthoredOldLeaseAndRevokedActor()
    {
        foreach (var phase in new[] { IngestionPhase.Reserved, IngestionPhase.Staged, IngestionPhase.Ready })
        {
            await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
            var facts = await test.ReserveNativeAsync();
            var owner = await test.ClaimNativeAsync(facts);
            if (phase is IngestionPhase.Staged or IngestionPhase.Ready)
                Assert.Equal(IngestionStoreOutcome.Applied, (await test.Ingestions.MarkStagedAsync(test.Actor, owner, facts.NativeObject)).Outcome);
            if (phase == IngestionPhase.Ready)
                Assert.Equal(IngestionStoreOutcome.Applied, (await test.Ingestions.CompleteAttemptAsync(test.Actor, owner,
                    PostgresIngestionTestSupport.SyntheticCompletion(facts))).Outcome);
            await test.SyntheticAgeNativeAsync(facts, TimeSpan.FromHours(25));
            await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
            var before = await test.OriginSnapshotAsync();
            Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
            Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
            Assert.Equal(before, await test.OriginSnapshotAsync());
        }
    }

    [PostgresFact]
    public async Task AYoungTerminalNativeIsNotDueButItsAuthoredOldCompletionYieldsOneExactFinalFence()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync(aged: false);
        Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
        await test.SyntheticAgeNativeAsync(facts, TimeSpan.FromHours(25));
        var before = await test.OriginSnapshotAsync();
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        Assert.Equal(CleanupTargetKind.FinalObject, claim.Retirement.Target.Kind);
        Assert.Null(claim.Retirement.Target.PublicationAttemptId);
        Assert.Null(claim.Retirement.Publication);
        Assert.Equal(ObjectCleanupReason.TerminalNativeStage, claim.Retirement.Reason);
        Assert.Equal(facts.OperationId, claim.Retirement.Origin.IngestionOperationId);
        Assert.True(claim.Retirement.RetiredAtUtc - claim.Retirement.EligibilityOriginAtUtc >= TimeSpan.FromHours(24));
        Assert.Equal(TimeSpan.FromSeconds(60), claim.LeaseExpiresAtUtc - claim.StartedAtUtc);
        Assert.Equal(1, claim.Generation);
        Assert.Equal(new long[] { 1, 1, 0 }, await test.CountsAsync());
        Assert.Equal(before, await test.OriginSnapshotAsync());
    }

    [PostgresFact]
    public async Task FailedAndExpiredUnusableCandidatesRetireWithDistinctTruthfulReasonsAndNoInventedReference()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var failed = await test.CreateFailedReportAsync("failed");
        var expired = await test.ReserveReportAsync(test.ReportKey("expired"));
        expired = await test.SyntheticAgeReportAsync(expired, TimeSpan.FromHours(25));
        var before = await test.OriginSnapshotAsync();
        var claims = (await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims;
        Assert.Equal(2, claims.Count);
        var failure = Assert.Single(claims, c => c.Retirement.Target.Object == failed.CandidateObjectId);
        var interrupted = Assert.Single(claims, c => c.Retirement.Target.Object == expired.CandidateObjectId);
        Assert.Equal(ObjectCleanupReason.FailedDerivedCandidate, failure.Retirement.Reason);
        Assert.Equal(ObjectCleanupReason.ExpiredDerivedCandidate, interrupted.Retirement.Reason);
        Assert.Equal(failed.DerivationId, failure.Retirement.Origin.DerivationId);
        Assert.Equal(expired.DerivationId, interrupted.Retirement.Origin.DerivationId);
        Assert.All(claims, c => Assert.Null(c.Retirement.Publication));
        Assert.Equal(before, await test.OriginSnapshotAsync());
    }

    [PostgresFact]
    public async Task SelectedAndGenuinelySupersededUsableResultsRemainProtectedAfterActorRevocation()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var first = await test.PublishSyntheticReportAsync();
        var repair = await test.Reports.RepairAsync(test.Actor,
            new(first.Key, first.CacheEntryId, first.DerivationId, first.DerivedObject, RevisionReportRepairReason.Missing), Guid.NewGuid());
        Assert.Equal(RevisionReportStoreOutcome.Claimed, repair.Outcome);
        var successor = Assert.IsType<RevisionReportClaim>(repair.Claim);
        var finish = await test.Reports.FinishAsync(test.Actor, successor,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(successor));
        Assert.Equal(RevisionReportStoreOutcome.Applied, finish.Outcome);
        Assert.NotEqual(first.DerivationId, finish.Snapshot!.SelectedResult!.DerivationId);
        await test.SyntheticAgeReportAttemptAsync(first.DerivationId, TimeSpan.FromHours(25));
        await test.SyntheticAgeReportAsync(successor, TimeSpan.FromHours(25));
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
        var before = await test.OriginSnapshotAsync();
        Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable"));
        Assert.Equal(before, await test.OriginSnapshotAsync());
    }

    [PostgresFact]
    public async Task AnActualUsableFalseValidationIsProtectedEvenAfterItsAuthoredOldLeaseExpires()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var claim = await test.ReserveReportAsync(test.ValidationKey("false.validation"));
        var result = await test.Reports.FinishAsync(test.Actor, claim,
            PostgresRevisionDerivationTestSupport.SyntheticValidationCompletion(claim, valid: false, total: 1, emitted: 1));
        Assert.Equal(RevisionReportStoreOutcome.Applied, result.Outcome);
        Assert.False(result.Snapshot!.SelectedResult!.Observation.Validation!.IsValid);
        await test.SyntheticAgeReportAsync(claim, TimeSpan.FromHours(25));
        var before = await test.OriginSnapshotAsync();
        Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
        Assert.Equal(before, await test.OriginSnapshotAsync());
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task NonHeadHistoricalNativeReferenceProtectsAnOtherwiseEligibleTerminalOrigin()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateHeadConflictNativeAsync();
        await using (var tx = await test.IngestionDatabase.BeginTransactionAsync(test.Project.OrganizationId))
        {
            Assert.Equal(1, await PostgresPrivateObjectMaintenanceTestSupport.InsertHistoricalNativeReferenceAsync(tx.Connection, tx.Transaction, facts));
            await tx.CommitAsync();
        }
        Assert.NotEqual(facts.FutureRevision.RevisionId, await test.Case.ScalarAsync<Guid>(
            "SELECT head_revision_id FROM weft.mappings WHERE organization_id=$1 AND project_id=$2 AND id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, facts.FutureRevision.MappingId));
        var before = await test.OriginSnapshotAsync();
        Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
        Assert.Equal(before, await test.OriginSnapshotAsync());
    }

    [PostgresFact]
    public async Task MoreThanOneHundredEligibleOriginsAdvanceWithAtMostSixteenClaimsAndNoSkippedOrRepeatedTarget()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var expected = new HashSet<PrivateObjectId>();
        for (var i = 0; i < 101; i++) expected.Add((await test.CreateRejectedNativeAsync()).NativeObject.Id);
        var observed = new HashSet<PrivateObjectId>();
        ObjectCleanupScanCursor? cursor = null;
        var calls = 0;
        do
        {
            var batch = await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid(), cursor);
            Assert.InRange(batch.ExaminedCount, 0, 100);
            Assert.InRange(batch.Claims.Count, 0, 16);
            Assert.InRange(batch.ProtectedCount + batch.NotDueCount, 0, batch.ExaminedCount);
            foreach (var claim in batch.Claims) Assert.True(observed.Add(claim.Retirement.Target.Object));
            if (batch.NextCursor is { } next)
            {
                Assert.Equal(test.Project, next.Project);
                Assert.True(Enum.IsDefined(next.Stream));
                Assert.NotEqual(cursor, next);
            }
            cursor = batch.NextCursor;
            Assert.InRange(++calls, 1, 110);
        } while (cursor is not null);
        Assert.Equal(expected.OrderBy(x => x.ObjectId), observed.OrderBy(x => x.ObjectId));
        Assert.Equal(new long[] { 101, 101, 0 }, await test.CountsAsync());
        // Reported candidates are the metadata materialization bound, not a claim
        // about every physical PostgreSQL tuple/index page examined.
    }

    [PostgresFact]
    public async Task SyntheticDeletedAndMissingObservationsCommitOnceAndResolveExactRetainedAcknowledgements()
    {
        foreach (var disposition in new[] { ObjectCleanupDisposition.Deleted, ObjectCleanupDisposition.Missing })
        {
            await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
            var facts = await test.CreateRejectedNativeAsync();
            var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
            var result = await test.Maintenance.FinishAsync(claim, disposition);
            Assert.Equal(ObjectCleanupFinishOutcome.Applied, result.Outcome);
            Assert.True(result.Attempt!.IsSuccess);
            var snapshot = await test.CleanupSnapshotAsync(claim.Retirement.RetirementId);
            Assert.Equal(ObjectCleanupFinishOutcome.AlreadyApplied, (await test.Maintenance.FinishAsync(claim, disposition)).Outcome);
            Assert.Equal(ObjectCleanupFinishOutcome.ClaimLost, (await test.Maintenance.FinishAsync(claim,
                disposition == ObjectCleanupDisposition.Deleted ? ObjectCleanupDisposition.Missing : ObjectCleanupDisposition.Deleted)).Outcome);
            await using var fresh = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
            Assert.Equal(result.Attempt, await new PostgresPrivateObjectMaintenanceStore(fresh).GetAttemptAsync(test.Project, claim.Retirement.RetirementId, 1));
            Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
            Assert.Equal(snapshot, await test.CleanupSnapshotAsync(claim.Retirement.RetirementId));
        }
    }

    [PostgresFact]
    public async Task EveryNonSuccessDispositionWaitsThirtySecondsThenReclaimsWithoutEditingPriorEvidence()
    {
        foreach (var disposition in new[] { ObjectCleanupDisposition.Canceled, ObjectCleanupDisposition.Deadline,
                     ObjectCleanupDisposition.StorageIo, ObjectCleanupDisposition.UnsafeStorage })
        {
            await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
            var facts = await test.CreateRejectedNativeAsync();
            var first = await test.ClaimSingleAsync(facts.NativeObject.Id);
            var applied = await test.Maintenance.FinishAsync(first, disposition);
            Assert.Equal(ObjectCleanupFinishOutcome.Applied, applied.Outcome);
            Assert.False(applied.Attempt!.IsSuccess);
            Assert.Equal(applied.Attempt.FinishedAtUtc!.Value.AddSeconds(30), applied.Attempt.NextAttemptAtUtc);
            Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
            var aged = await test.SyntheticAgeCleanupAsync(first, TimeSpan.FromSeconds(31));
            var retained = await test.Maintenance.GetAttemptAsync(test.Project, first.Retirement.RetirementId, 1);
            var next = await test.ClaimSingleAsync(facts.NativeObject.Id);
            Assert.Equal(2, next.Generation);
            Assert.NotEqual(first.AttemptId, next.AttemptId);
            Assert.NotEqual(first.LeaseToken, next.LeaseToken);
            Assert.Equal(aged.Retirement, next.Retirement);
            Assert.Equal(retained, await test.Maintenance.GetAttemptAsync(test.Project, first.Retirement.RetirementId, 1));
            Assert.Equal(ObjectCleanupFinishOutcome.ClaimLost, (await test.Maintenance.FinishAsync(aged, ObjectCleanupDisposition.Deleted)).Outcome);
        }
    }

    [PostgresFact]
    public async Task AnInterruptedLeaseNeedsItsSixtySecondsPlusThirtyBeforeAnExactNewGeneration()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var first = await test.ClaimSingleAsync(facts.NativeObject.Id);
        var expired = await test.SyntheticAgeCleanupAsync(first, TimeSpan.FromSeconds(61));
        Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
        expired = await test.SyntheticAgeCleanupAsync(expired, TimeSpan.FromSeconds(31));
        var old = await test.Maintenance.GetAttemptAsync(test.Project, expired.Retirement.RetirementId, 1);
        Assert.Null(old!.FinishedAtUtc);
        Assert.Null(old.Disposition);
        var second = await test.ClaimSingleAsync(facts.NativeObject.Id);
        Assert.Equal(2, second.Generation);
        Assert.Equal(old, await test.Maintenance.GetAttemptAsync(test.Project, expired.Retirement.RetirementId, 1));
        Assert.Equal(ObjectCleanupFinishOutcome.ClaimLost, (await test.Maintenance.FinishAsync(expired, ObjectCleanupDisposition.Missing)).Outcome);
    }

    [PostgresFact]
    public async Task EighthFailedGenerationExhaustsPermanentlyWhileAllEightAttemptsRemainReadable()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        for (var generation = 1; generation <= 8; generation++)
        {
            Assert.Equal(generation, claim.Generation);
            Assert.Equal(ObjectCleanupFinishOutcome.Applied, (await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.UnsafeStorage)).Outcome);
            claim = await test.SyntheticAgeCleanupAsync(claim, TimeSpan.FromSeconds(31));
            if (generation < 8) claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        }
        Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
        for (var generation = 1; generation <= 8; generation++)
        {
            var attempt = await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, generation);
            Assert.NotNull(attempt);
            Assert.Equal(ObjectCleanupDisposition.UnsafeStorage, attempt.Disposition);
        }
        Assert.Equal(new long[] { 1, 8, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task FullClaimHeaderSubstitutionCannotAcknowledgeTheRealCurrentAttempt()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var original = await test.ClaimSingleAsync(facts.NativeObject.Id);
        var before = await test.CleanupSnapshotAsync(original.Retirement.RetirementId);
        var altered = new ObjectCleanupClaim[]
        {
            new(original.Retirement, Guid.NewGuid(), original.Generation, original.ServerInstanceId, original.LeaseToken, original.StartedAtUtc, original.LeaseExpiresAtUtc),
            new(original.Retirement, original.AttemptId, original.Generation, Guid.NewGuid(), original.LeaseToken, original.StartedAtUtc, original.LeaseExpiresAtUtc),
            new(original.Retirement, original.AttemptId, original.Generation, original.ServerInstanceId, Guid.NewGuid(), original.StartedAtUtc, original.LeaseExpiresAtUtc),
            new(original.Retirement, original.AttemptId, original.Generation, original.ServerInstanceId, original.LeaseToken, original.StartedAtUtc.AddTicks(10), original.LeaseExpiresAtUtc.AddTicks(10))
        };
        foreach (var claim in altered)
        {
            Assert.Equal(ObjectCleanupFinishOutcome.ClaimLost, (await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Deleted)).Outcome);
            Assert.Equal(before, await test.CleanupSnapshotAsync(original.Retirement.RetirementId));
        }
        Assert.Equal(ObjectCleanupFinishOutcome.Applied, (await test.Maintenance.FinishAsync(original, ObjectCleanupDisposition.Missing)).Outcome);
    }

    [PostgresFact]
    public async Task EightInterruptedGenerationsRemainUnfinishedAndNeverCreateANinthCleanupLease()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        for (var generation = 1; generation <= 8; generation++)
        {
            Assert.Equal(generation, claim.Generation);
            claim = await test.SyntheticAgeCleanupAsync(claim, TimeSpan.FromSeconds(92));
            if (generation < 8) claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        }
        Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
        for (var generation = 1; generation <= 8; generation++)
        {
            var attempt = await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, generation);
            Assert.NotNull(attempt);
            Assert.Null(attempt.FinishedAtUtc);
            Assert.Null(attempt.Disposition);
        }
        Assert.Equal(new long[] { 1, 8, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task ExactAttemptReadsCannotCrossOrganizationProjectOrGeneration()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateRejectedNativeAsync();
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        Assert.NotNull(await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, 1));
        Assert.Null(await test.Maintenance.GetAttemptAsync(new(test.Seed.OrganizationB, test.Seed.ProjectB), claim.Retirement.RetirementId, 1));
        Assert.Null(await test.Maintenance.GetAttemptAsync(new(test.Project.OrganizationId, test.Seed.ProjectB), claim.Retirement.RetirementId, 1));
        Assert.Null(await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, 2));
        var cursor = new ObjectCleanupScanCursor(new(test.Seed.OrganizationB, test.Seed.ProjectB),
            ObjectCleanupScanStream.NativeStage, claim.Retirement.EligibilityOriginAtUtc, new(
                PrivateObjectId.Restore(new(test.Seed.OrganizationB, test.Seed.ProjectB, Guid.NewGuid(), Guid.NewGuid()),
                    Guid.NewGuid(), PrivateObjectClass.NativeArtifact), CleanupTargetKind.FinalObject));
        await Assert.ThrowsAsync<ArgumentException>(() => test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid(), cursor));
        Assert.Equal(new long[] { 1, 1, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task AnAuthoredOwnerBypassReferenceMakesFinishRefuseSafetyWithoutAcknowledgingDeleted()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.CreateHeadConflictNativeAsync();
        var claim = await test.ClaimSingleAsync(facts.NativeObject.Id);
        // Deliberately damaged owner-authored metadata, outside runtime privileges.
        // This is not a legal attachment race or evidence of physical deletion.
        // Keep the old transition, every CHECK/FK and all other guards enabled;
        // restore this exact supplement atomically before exercising production Finish.
        await using (var owner = await test.OpenOwnerAsync())
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            await PostgresPrivateObjectMaintenanceTestSupport.ExecuteAsync(owner, transaction,
                "ALTER TABLE weft.mapping_revisions DISABLE TRIGGER mapping_revisions_z_retirement_guard");
            Assert.Equal(1, await PostgresPrivateObjectMaintenanceTestSupport.InsertHistoricalNativeReferenceAsync(owner, transaction, facts));
            await PostgresPrivateObjectMaintenanceTestSupport.ExecuteAsync(owner, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await PostgresPrivateObjectMaintenanceTestSupport.ExecuteAsync(owner, transaction,
                "ALTER TABLE weft.mapping_revisions ENABLE TRIGGER mapping_revisions_z_retirement_guard");
            await transaction.CommitAsync();
        }
        var before = await test.CleanupSnapshotAsync(claim.Retirement.RetirementId);
        var origins = await test.OriginSnapshotAsync();
        var refused = await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Deleted);
        Assert.Equal(ObjectCleanupFinishOutcome.SafetyRefusal, refused.Outcome);
        Assert.Null(refused.Attempt);
        Assert.Equal(before, await test.CleanupSnapshotAsync(claim.Retirement.RetirementId));
        Assert.Equal(origins, await test.OriginSnapshotAsync());
        Assert.Null((await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, 1))!.Disposition);
        Assert.Empty((await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
        Assert.Equal(new long[] { 1, 1, 0 }, await test.CountsAsync());
    }
}
