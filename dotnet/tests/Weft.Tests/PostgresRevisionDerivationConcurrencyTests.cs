using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual PostgreSQL races over explicitly synthetic metadata; no report object, codec or worker runs.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRevisionDerivationConcurrencyTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task SameKeyWaitObservesOneCommittedClaimOrReplacesTheRolledBackClaimWithoutOrphans()
    {
        foreach (var commit in new[] { true, false })
        {
            await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
            await using var contenderDatabase = new PostgresRevisionDerivationDatabase(test.Case.DerivationConnectionString);
            var contender = new PostgresRevisionDerivationStore(contenderDatabase);
            var winnerPid = await PostgresRevisionDerivationTestSupport.BackendAsync(test.DerivationDatabase.DataSource);
            var contenderPid = await PostgresRevisionDerivationTestSupport.BackendAsync(contenderDatabase.DataSource);
            Assert.NotEqual(winnerPid, contenderPid);
            var source = await test.ImmutableSourceSnapshotAsync();
            await using var winner = await test.DerivationDatabase.BeginTransactionAsync(test.Revision.OrganizationId);
            var held = await test.Store.TryClaimInTransactionAsync(winner, test.Actor, test.Key, Guid.NewGuid());
            var original = Assert.IsType<RevisionReportClaim>(held.Claim);
            Assert.Equal(RevisionReportStoreOutcome.Claimed, held.Outcome);
            Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
            var pending = contender.TryClaimAsync(test.AdmittedActor, test.Key, Guid.NewGuid());
            try
            {
                await PostgresRevisionDerivationTestSupport.WaitForBlockedAsync(test.Case, contenderPid, winnerPid, pending);
                if (commit) await winner.CommitAsync();
                else await winner.RollbackAsync();
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(commit ? RevisionReportStoreOutcome.Pending : RevisionReportStoreOutcome.Claimed, result.Outcome);
                if (commit)
                {
                    Assert.Null(result.Claim);
                    Assert.Equal(original.DerivationId, result.Snapshot!.CurrentAttempt!.DerivationId);
                    Assert.Equal(original.Generation, result.Snapshot.Generation);
                }
                else
                {
                    var replacement = Assert.IsType<RevisionReportClaim>(result.Claim);
                    Assert.NotEqual(original.CacheEntryId, replacement.CacheEntryId);
                    Assert.NotEqual(original.DerivationId, replacement.DerivationId);
                    Assert.NotEqual(original.CandidateObjectId, replacement.CandidateObjectId);
                    Assert.Equal(test.AdmittedActor.PrincipalId, replacement.OriginalActor);
                    Assert.Equal(1, replacement.Generation);
                    Assert.Null(await test.Reader.GetRetainedAsync(test.Actor, test.Revision,
                        test.Key.Operation, original.DerivationId));
                }
            }
            finally
            {
                await winner.DisposeAsync();
                await DrainAsync(pending);
            }
            Assert.Equal(new long[] { 1, 1, 0, 0 }, await test.CountsAsync());
            Assert.Equal(source, await test.ImmutableSourceSnapshotAsync());
        }
    }

    [PostgresFact]
    public async Task RevocationDuringObservedUniqueWaitCannotAdoptTheWinnerOrLeaveAnUnauthorizedEmptyKey()
    {
        foreach (var commit in new[] { true, false })
        {
            foreach (var authority in new[] { "principal", "organization", "project" })
            {
                await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
                await using var otherDatabase = new PostgresRevisionDerivationDatabase(test.Case.DerivationConnectionString);
                var other = new PostgresRevisionDerivationStore(otherDatabase);
                var firstPid = await PostgresRevisionDerivationTestSupport.BackendAsync(test.DerivationDatabase.DataSource);
                var otherPid = await PostgresRevisionDerivationTestSupport.BackendAsync(otherDatabase.DataSource);
                await using var first = await test.DerivationDatabase.BeginTransactionAsync(test.Revision.OrganizationId);
                var held = await test.Store.TryClaimInTransactionAsync(first, test.Actor, test.Key, Guid.NewGuid());
                Assert.Equal(RevisionReportStoreOutcome.Claimed, held.Outcome);
                var pending = other.TryClaimAsync(test.AdmittedActor, test.Key, Guid.NewGuid());
                var (revoke, restore, values) = AuthorityMutation(test, test.AdmittedActor, authority);
                try
                {
                    await PostgresRevisionDerivationTestSupport.WaitForBlockedAsync(test.Case, otherPid, firstPid, pending);
                    await test.Case.ExecuteAsync(revoke, values).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.False(pending.IsCompleted);
                    if (commit) await first.CommitAsync();
                    else await first.RollbackAsync();
                    Opaque(await pending.WaitAsync(TimeSpan.FromSeconds(5)), RevisionReportStoreOutcome.NotAuthorizedOrNotFound);
                    Assert.Equal(commit ? new long[] { 1, 1, 0, 0 } : new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
                }
                finally
                {
                    await first.DisposeAsync();
                    await DrainAsync(pending);
                    await test.Case.ExecuteAsync(restore, values);
                }
                Assert.Equal(commit ? RevisionReportStoreOutcome.Pending : RevisionReportStoreOutcome.Claimed,
                    (await other.TryClaimAsync(test.AdmittedActor, test.Key, Guid.NewGuid())).Outcome);
                Assert.Equal(new long[] { 1, 1, 0, 0 }, await test.CountsAsync());
            }
        }
    }

    [PostgresFact]
    public async Task DifferentOperationAndProfileKeysCommitWhileTheFirstKeyTransactionIsStillHeld()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        await using var otherDatabase = new PostgresRevisionDerivationDatabase(test.Case.DerivationConnectionString);
        var other = new PostgresRevisionDerivationStore(otherDatabase);
        await using var heldTransaction = await test.DerivationDatabase.BeginTransactionAsync(test.Revision.OrganizationId);
        var held = await test.Store.TryClaimInTransactionAsync(heldTransaction, test.Actor, test.Key, Guid.NewGuid());
        var first = Assert.IsType<RevisionReportClaim>(held.Claim);
        var profile = PostgresRevisionDerivationTestSupport.Profile;
        var keys = new RevisionReportKey[]
        {
            test.ValidationKey(),
            test.ProjectionKey(profile: new(profile.BackendId, profile.BackendRevision,
                profile.BackendBuild + ".independent", profile.AdapterBuild))
        };
        foreach (var key in keys)
        {
            var result = await other.TryClaimAsync(test.Actor, key, Guid.NewGuid()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RevisionReportStoreOutcome.Claimed, result.Outcome);
            Assert.NotEqual(first.CacheEntryId, result.Claim!.CacheEntryId);
            Assert.NotEqual(first.DerivationId, result.Claim.DerivationId);
        }
        Assert.Equal(new long[] { 2, 2, 0, 0 }, await test.CountsAsync());
        await heldTransaction.CommitAsync();
        Assert.Equal(new long[] { 3, 3, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task BlockedClaimReadsCommittedPublicationAndDoesNotReserveAnotherAttempt()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        await using var otherDatabase = new PostgresRevisionDerivationDatabase(test.Case.DerivationConnectionString);
        var other = new PostgresRevisionDerivationStore(otherDatabase);
        var winnerPid = await PostgresRevisionDerivationTestSupport.BackendAsync(test.DerivationDatabase.DataSource);
        var otherPid = await PostgresRevisionDerivationTestSupport.BackendAsync(otherDatabase.DataSource);
        await using var publication = await test.DerivationDatabase.BeginTransactionAsync(test.Revision.OrganizationId);
        var held = await test.Store.FinishInTransactionAsync(publication, test.Actor, claim,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim));
        Assert.Equal(RevisionReportStoreOutcome.Applied, held.Outcome);
        Assert.Equal(RevisionReportState.Pending, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.State);
        var pending = other.TryClaimAsync(test.AdmittedActor, test.Key, Guid.NewGuid());
        try
        {
            await PostgresRevisionDerivationTestSupport.WaitForBlockedAsync(test.Case, otherPid, winnerPid, pending);
            await publication.CommitAsync();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RevisionReportStoreOutcome.Available, result.Outcome);
            Assert.Null(result.Claim);
            Assert.Equal(held.Snapshot!.SelectedResult, result.Snapshot!.SelectedResult);
        }
        finally
        {
            await publication.DisposeAsync();
            await DrainAsync(pending);
        }
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task RevocationCommittedAfterObservedKeyWaitIsRecheckedByClaimFinishAndRepair()
    {
        foreach (var operation in new[] { "claim", "finish", "repair" })
        {
            foreach (var authority in new[] { "principal", "organization", "project" })
            {
                await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
                var claim = await test.ClaimAsync();
                RevisionReportResult? selected = null;
                if (operation == "repair")
                {
                    var published = await test.Store.FinishAsync(test.Actor, claim,
                        PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim));
                    selected = Assert.IsType<RevisionReportResult>(published.Snapshot!.SelectedResult);
                }
                var actor = operation == "repair" ? test.AdmittedActor : test.Actor;
                var before = await MetadataSnapshotAsync(test);
                var backend = await PostgresRevisionDerivationTestSupport.BackendAsync(test.DerivationDatabase.DataSource);
                await using var owner = await PostgresRevisionDerivationTestSupport.OpenOwnerAsync(test.Case);
                var ownerPid = await BackendAsync(owner);
                await using var keyLock = await owner.BeginTransactionAsync();
                await LockKeyAsync(owner, keyLock, claim);
                var pending = operation switch
                {
                    "claim" => test.Store.TryClaimAsync(actor, test.Key, Guid.NewGuid()),
                    "finish" => test.Store.FinishAsync(actor, claim, PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim)),
                    "repair" => test.Store.RepairAsync(actor, Target(selected!), Guid.NewGuid()),
                    _ => throw new InvalidOperationException()
                };
                var (revoke, restore, values) = AuthorityMutation(test, actor, authority);
                try
                {
                    await PostgresRevisionDerivationTestSupport.WaitForBlockedAsync(test.Case, backend, ownerPid, pending);
                    // A separate real owner transaction commits revocation before
                    // releasing the key lock; the pre-wait source snapshot is stale.
                    await test.Case.ExecuteAsync(revoke, values).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.False(pending.IsCompleted);
                    await keyLock.RollbackAsync();
                    Opaque(await pending.WaitAsync(TimeSpan.FromSeconds(5)), RevisionReportStoreOutcome.NotAuthorizedOrNotFound);
                    Assert.Equal(before, await MetadataSnapshotAsync(test));
                }
                finally
                {
                    await keyLock.DisposeAsync();
                    await DrainAsync(pending);
                    await test.Case.ExecuteAsync(restore, values);
                }
                var recovered = await test.Store.TryClaimAsync(test.Actor, test.Key, Guid.NewGuid());
                Assert.Equal(operation == "repair" ? RevisionReportStoreOutcome.Available : RevisionReportStoreOutcome.Pending,
                    recovered.Outcome);
                Assert.Equal(before, await MetadataSnapshotAsync(test));
            }
        }
    }

    [PostgresFact]
    public async Task ValidShapedWrongActorKeySourceAndEveryClaimFenceLeaveTheRealAttemptUnchanged()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        var before = await MetadataSnapshotAsync(test);
        var alteredSource = new RevisionReportSource(test.Revision,
            new(PrivateObjectId.Create(test.Revision, PrivateObjectClass.NativeArtifact),
                Sha256Digest.FromBytes("Synthetic different native source"u8), test.Source.NativeObject.ByteCount),
            test.Source.LogicalDocument, test.Source.DependencyIdentity, test.Source.SourceBackendId);
        var variations = new[]
        {
            CopyClaim(claim, cache: Guid.NewGuid()), CopyClaim(claim, derivation: Guid.NewGuid()),
            CopyClaim(claim, token: Guid.NewGuid()), CopyClaim(claim, instance: Guid.NewGuid()),
            CopyClaim(claim, generation: claim.Generation + 1),
            CopyClaim(claim, candidate: PrivateObjectId.Create(test.Revision, PrivateObjectClass.DerivedArtifact)),
            CopyClaim(claim, actor: test.AdmittedActor.PrincipalId),
            CopyClaim(claim, key: test.ProjectionKey(schema: "synthetic.other.schema.v1")),
            CopyClaim(claim, key: test.ProjectionKey(source: alteredSource)),
            CopyClaim(claim, shiftedLease: true), CopyClaim(claim, repair: Guid.NewGuid())
        };
        foreach (var altered in variations)
        {
            var actor = altered.OriginalActor == test.AdmittedActor.PrincipalId ? test.AdmittedActor : test.Actor;
            Opaque(await test.Store.FinishAsync(actor, altered,
                PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(altered)), RevisionReportStoreOutcome.ClaimLost);
            Assert.Equal(before, await MetadataSnapshotAsync(test));
        }
        Opaque(await test.Store.FinishAsync(test.AdmittedActor, claim,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim)), RevisionReportStoreOutcome.ClaimLost);
        Assert.Equal(before, await MetadataSnapshotAsync(test));
        Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.Actor, claim,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim))).Outcome);
    }

    [PostgresFact]
    public async Task SyntheticExpiredLeaseIsNotRenewedByReadsAndNewActorFencesTheOldGeneration()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var old = await test.ClaimAsync();
        // Owner-controlled metadata setup, not real elapsed time or worker death.
        await test.SyntheticExpireClaimAsync(old);
        var expiredRow = await test.AttemptSnapshotAsync(old.DerivationId);
        var expired = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.Actor, test.Key));
        Assert.Equal(RevisionReportState.Pending, expired.State);
        Assert.False(expired.IsClaimLive);
        Assert.Equal(expired.CurrentAttempt!.LeaseExpiresAtUtc, expired.NextAttemptAtUtc);
        Opaque(await test.Store.FinishAsync(test.Actor, old,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(old)), RevisionReportStoreOutcome.ClaimLost);
        Assert.Equal(expiredRow, await test.AttemptSnapshotAsync(old.DerivationId));
        var next = await test.ClaimAsync(actor: test.AdmittedActor);
        Assert.Equal(old.CacheEntryId, next.CacheEntryId);
        Assert.Equal(old.Generation + 1, next.Generation);
        Assert.NotEqual(old.DerivationId, next.DerivationId);
        Assert.NotEqual(old.LeaseToken, next.LeaseToken);
        Assert.NotEqual(old.CandidateObjectId, next.CandidateObjectId);
        Assert.Equal(test.AdmittedActor.PrincipalId, next.OriginalActor);
        Opaque(await test.Store.FinishAsync(test.Actor, old,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(old)), RevisionReportStoreOutcome.ClaimLost);
        Assert.Equal(expiredRow, await test.AttemptSnapshotAsync(old.DerivationId));
        Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.AdmittedActor, next,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(next))).Outcome);
        var retained = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(
            test.AdmittedActor, test.Revision, test.Key.Operation, old.DerivationId));
        Assert.Null(retained.Result);
        Assert.Null(retained.Attempt.Completion);
        Assert.Equal(new long[] { 1, 2, 1, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task ConcurrentRepairCompareAndSwapCommitsOnceOrReplacesOnlyTheRolledBackAttempt()
    {
        foreach (var commit in new[] { true, false })
        {
            await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
            var selected = await test.PublishSyntheticAsync();
            var originalRow = await test.AttemptSnapshotAsync(selected.DerivationId);
            await using var otherDatabase = new PostgresRevisionDerivationDatabase(test.Case.DerivationConnectionString);
            var other = new PostgresRevisionDerivationStore(otherDatabase);
            var firstPid = await PostgresRevisionDerivationTestSupport.BackendAsync(test.DerivationDatabase.DataSource);
            var secondPid = await PostgresRevisionDerivationTestSupport.BackendAsync(otherDatabase.DataSource);
            await using var first = await test.DerivationDatabase.BeginTransactionAsync(test.Revision.OrganizationId);
            var held = await test.Store.RepairInTransactionAsync(first, test.AdmittedActor, Target(selected), Guid.NewGuid());
            var heldClaim = Assert.IsType<RevisionReportClaim>(held.Claim);
            Assert.Equal(RevisionReportStoreOutcome.Claimed, held.Outcome);
            Assert.Equal(selected, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.SelectedResult);
            var pending = other.RepairAsync(test.Actor, Target(selected), Guid.NewGuid());
            try
            {
                await PostgresRevisionDerivationTestSupport.WaitForBlockedAsync(test.Case, secondPid, firstPid, pending);
                if (commit) await first.CommitAsync();
                else await first.RollbackAsync();
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                if (commit) Opaque(result, RevisionReportStoreOutcome.SelectionChanged);
                else
                {
                    Assert.Equal(RevisionReportStoreOutcome.Claimed, result.Outcome);
                    Assert.NotEqual(heldClaim.DerivationId, result.Claim!.DerivationId);
                    Assert.Equal(selected.DerivationId, result.Claim.RepairTargetDerivationId);
                    Assert.Equal(test.Actor.PrincipalId, result.Claim.OriginalActor);
                }
            }
            finally
            {
                await first.DisposeAsync();
                await DrainAsync(pending);
            }
            Assert.Equal(originalRow, await test.AttemptSnapshotAsync(selected.DerivationId));
            Assert.Equal(new long[] { 1, 2, 1, 0 }, await test.CountsAsync());
            var current = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.Actor, test.Key));
            Assert.Equal(2, current.Generation);
            Assert.Null(current.SelectedResult);
            Assert.Equal(selected.DerivationId, current.CurrentAttempt!.RepairTargetDerivationId);
            Assert.Equal(selected, (await test.Reader.GetRetainedAsync(test.Actor, test.Revision,
                test.Key.Operation, selected.DerivationId))!.Result);
        }
    }

    [PostgresFact]
    public async Task StaleRepairIdentityAndWrongReferenceCannotDeselectTheNewSelectedResult()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var old = await test.PublishSyntheticAsync();
        var oldRow = await test.AttemptSnapshotAsync(old.DerivationId);
        var repaired = await test.Store.RepairAsync(test.AdmittedActor, Target(old), Guid.NewGuid());
        var next = Assert.IsType<RevisionReportClaim>(repaired.Claim);
        var published = await test.Store.FinishAsync(test.AdmittedActor, next,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(next));
        var selected = Assert.IsType<RevisionReportResult>(published.Snapshot!.SelectedResult);
        var before = await MetadataSnapshotAsync(test);
        Opaque(await test.Store.RepairAsync(test.Actor, Target(old), Guid.NewGuid()), RevisionReportStoreOutcome.SelectionChanged);
        var wrongReference = new PrivateObjectReference(selected.DerivedObject.Id,
            Sha256Digest.FromBytes("Synthetic different derived bytes"u8), selected.DerivedObject.ByteCount);
        var wrong = new RevisionReportRepairTarget(selected.Key, selected.CacheEntryId, selected.DerivationId,
            wrongReference, RevisionReportRepairReason.Integrity);
        Opaque(await test.Store.RepairAsync(test.Actor, wrong, Guid.NewGuid()), RevisionReportStoreOutcome.SelectionChanged);
        Assert.Equal(before, await MetadataSnapshotAsync(test));
        Assert.Equal(selected, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.SelectedResult);
        Assert.Equal(oldRow, await test.AttemptSnapshotAsync(old.DerivationId));
    }

    [PostgresFact]
    public async Task ConcurrentTerminalSuccessWinsOnceAndLaterFailureCannotRewriteItsHistory()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        await using var otherDatabase = new PostgresRevisionDerivationDatabase(test.Case.DerivationConnectionString);
        var other = new PostgresRevisionDerivationStore(otherDatabase);
        var firstPid = await PostgresRevisionDerivationTestSupport.BackendAsync(test.DerivationDatabase.DataSource);
        var otherPid = await PostgresRevisionDerivationTestSupport.BackendAsync(otherDatabase.DataSource);
        await using var first = await test.DerivationDatabase.BeginTransactionAsync(test.Revision.OrganizationId);
        var held = await test.Store.FinishInTransactionAsync(first, test.Actor, claim,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim));
        var pending = other.FinishAsync(test.Actor, claim,
            RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.StorageIo));
        try
        {
            await PostgresRevisionDerivationTestSupport.WaitForBlockedAsync(test.Case, otherPid, firstPid, pending);
            await first.CommitAsync();
            Opaque(await pending.WaitAsync(TimeSpan.FromSeconds(5)), RevisionReportStoreOutcome.ClaimLost);
        }
        finally
        {
            await first.DisposeAsync();
            await DrainAsync(pending);
        }
        var terminal = await test.AttemptSnapshotAsync(claim.DerivationId);
        Opaque(await test.Store.FinishAsync(test.Actor, claim,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim)), RevisionReportStoreOutcome.ClaimLost);
        Assert.Equal(terminal, await test.AttemptSnapshotAsync(claim.DerivationId));
        Assert.Equal(held.Snapshot!.SelectedResult, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.SelectedResult);
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task SyntheticMaximumGenerationKeepsLiveAndUsableStatesAndNeverDeselectsOnExhaustion()
    {
        foreach (var state in new[] { "pending", "usable", "failed" })
        {
            await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
            var original = await test.ClaimAsync();
            if (state == "usable")
                Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.Actor, original,
                    PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(original))).Outcome);
            if (state == "failed")
                Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.Actor, original,
                    RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.StorageIo))).Outcome);
            var history = await test.AttemptSnapshotAsync(original.DerivationId);
            // Synthetic owner high-counter setup only; no lifetime loop, host
            // clock injection, report bytes or private object adoption is claimed.
            var maximum = await SyntheticMaximumAsync(test, original, state == "usable");
            var before = await MetadataSnapshotAsync(test);
            var observed = await test.Store.TryClaimAsync(test.Actor, test.Key, Guid.NewGuid());
            Assert.Equal(state switch
            {
                "usable" => RevisionReportStoreOutcome.Available,
                "failed" => RevisionReportStoreOutcome.RetryDelayed,
                _ => RevisionReportStoreOutcome.Pending
            }, observed.Outcome);
            Assert.Equal(long.MaxValue, observed.Snapshot!.Generation);
            Assert.Equal(before, await MetadataSnapshotAsync(test));
            if (state == "usable")
            {
                Opaque(await test.Store.RepairAsync(test.Actor, Target(observed.Snapshot.SelectedResult!), Guid.NewGuid()),
                    RevisionReportStoreOutcome.GenerationExhausted);
                Assert.Equal(before, await MetadataSnapshotAsync(test));
                Assert.Equal(observed.Snapshot.SelectedResult, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.SelectedResult);
            }
            else
            {
                if (state == "failed") await test.SyntheticMakeRetryEligibleAsync(maximum);
                else await test.SyntheticExpireClaimAsync(maximum);
                var expired = await MetadataSnapshotAsync(test);
                Opaque(await test.Store.TryClaimAsync(test.AdmittedActor, test.Key, Guid.NewGuid()),
                    RevisionReportStoreOutcome.GenerationExhausted);
                Assert.Equal(expired, await MetadataSnapshotAsync(test));
            }
            Assert.Equal(history, await test.AttemptSnapshotAsync(original.DerivationId));
        }
    }

    private static RevisionReportRepairTarget Target(RevisionReportResult result) =>
        new(result.Key, result.CacheEntryId, result.DerivationId, result.DerivedObject, RevisionReportRepairReason.Missing);

    private static void Opaque(RevisionReportStoreResult result, RevisionReportStoreOutcome expected)
    {
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.Null(result.Claim);
    }

    private static RevisionReportClaim CopyClaim(RevisionReportClaim claim, Guid? cache = null,
        Guid? derivation = null, Guid? token = null, Guid? instance = null, long? generation = null,
        PrivateObjectId? candidate = null, PrincipalId? actor = null, RevisionReportKey? key = null,
        bool shiftedLease = false, Guid? repair = null) => new(cache ?? claim.CacheEntryId,
            derivation ?? claim.DerivationId, key ?? claim.Key, actor ?? claim.OriginalActor,
            instance ?? claim.ServerInstanceId, token ?? claim.LeaseToken, generation ?? claim.Generation,
            shiftedLease ? claim.StartedAtUtc.AddSeconds(1) : claim.StartedAtUtc,
            shiftedLease ? claim.LeaseExpiresAtUtc.AddSeconds(1) : claim.LeaseExpiresAtUtc,
            candidate ?? claim.CandidateObjectId, repair ?? claim.RepairTargetDerivationId);

    private static Task<string> MetadataSnapshotAsync(PostgresRevisionDerivationTestSupport test) => test.Case.ScalarAsync<string>("""
        SELECT jsonb_build_object('keys',COALESCE((SELECT jsonb_agg(to_jsonb(k) ORDER BY k.cache_entry_id)
            FROM weft.revision_derivations k),'[]'::jsonb),
            'attempts',COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.cache_entry_id,a.generation)
            FROM weft.revision_derivation_attempts a),'[]'::jsonb))::text
        """);

    private static async Task LockKeyAsync(NpgsqlConnection owner, NpgsqlTransaction transaction, RevisionReportClaim claim)
    {
        await using var command = new NpgsqlCommand("""
            SELECT cache_entry_id FROM weft.revision_derivations
            WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4 AND cache_entry_id=$5 FOR UPDATE
            """, owner, transaction);
        PostgresRevisionDerivationTestSupport.AddScope(command, claim.Key.Scope);
        command.Parameters.AddWithValue(claim.CacheEntryId);
        Assert.Equal(claim.CacheEntryId, await command.ExecuteScalarAsync());
    }

    private static async Task<int> BackendAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static (string Revoke, string Restore, object[] Values) AuthorityMutation(
        PostgresRevisionDerivationTestSupport test, ActorContext actor, string authority) => authority switch
        {
            "principal" => ("UPDATE weft.principals SET active=false WHERE id=$1",
                "UPDATE weft.principals SET active=true WHERE id=$1", [actor.PrincipalId.Value]),
            "organization" => ("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
                "UPDATE weft.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2",
                [test.Revision.OrganizationId, actor.PrincipalId.Value]),
            "project" => ("UPDATE weft.project_memberships SET active=false WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                "UPDATE weft.project_memberships SET active=true WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                [test.Revision.OrganizationId, test.Revision.ProjectId, actor.PrincipalId.Value]),
            _ => throw new InvalidOperationException()
        };

    private static async Task<RevisionReportClaim> SyntheticMaximumAsync(PostgresRevisionDerivationTestSupport test,
        RevisionReportClaim previous, bool usable)
    {
        var next = CopyClaim(previous, derivation: Guid.NewGuid(), token: Guid.NewGuid(), generation: long.MaxValue,
            candidate: PrivateObjectId.Create(previous.Key.Scope, PrivateObjectClass.DerivedArtifact));
        await using var owner = await PostgresRevisionDerivationTestSupport.OpenOwnerAsync(test.Case);
        await using var transaction = await owner.BeginTransactionAsync();
        await using (var disable = new NpgsqlCommand("""
            ALTER TABLE weft.revision_derivations DISABLE TRIGGER revision_derivations_transition_guard;
            ALTER TABLE weft.revision_derivation_attempts DISABLE TRIGGER revision_derivation_attempts_transition_guard
            """, owner, transaction)) await disable.ExecuteNonQueryAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO weft.revision_derivation_attempts
            SELECT (jsonb_populate_record(NULL::weft.revision_derivation_attempts,
                to_jsonb(original)||jsonb_build_object('generation',$6::bigint,'derivation_id',$7::uuid,
                    'claim_token',$8::uuid,'candidate_object_id',$9::uuid))).*
            FROM weft.revision_derivation_attempts original
            WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4 AND derivation_id=$5
            """, owner, transaction))
        {
            PostgresRevisionDerivationTestSupport.AddScope(insert, previous.Key.Scope);
            insert.Parameters.AddWithValue(previous.DerivationId);
            insert.Parameters.AddWithValue(long.MaxValue);
            insert.Parameters.AddWithValue(next.DerivationId);
            insert.Parameters.AddWithValue(next.LeaseToken);
            insert.Parameters.AddWithValue(next.CandidateObjectId.ObjectId);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE weft.revision_derivations SET current_generation=$6,current_attempt_id=$7,
                selected_derivation_id=CASE WHEN $8 THEN $7 ELSE NULL::uuid END
            WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4 AND cache_entry_id=$5
            """, owner, transaction))
        {
            PostgresRevisionDerivationTestSupport.AddScope(update, previous.Key.Scope);
            update.Parameters.AddWithValue(previous.CacheEntryId);
            update.Parameters.AddWithValue(long.MaxValue);
            update.Parameters.AddWithValue(next.DerivationId);
            update.Parameters.AddWithValue(usable);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        await using (var restore = new NpgsqlCommand("""
            SET CONSTRAINTS ALL IMMEDIATE;
            ALTER TABLE weft.revision_derivation_attempts ENABLE TRIGGER revision_derivation_attempts_transition_guard;
            ALTER TABLE weft.revision_derivations ENABLE TRIGGER revision_derivations_transition_guard
            """, owner, transaction)) await restore.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return next;
    }

    private static async Task DrainAsync<T>(Task<T> pending)
    {
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) when (pending.IsCanceled || pending.IsFaulted) { }
    }
}
