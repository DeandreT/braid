using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Actual metadata persistence; every derived publication observation/reference is explicitly synthetic.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRevisionDerivationWorkflowTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task AbsentAndStructurallyPersistedGenerationZeroKeysStayNotDerivedUntilAnExplicitClaim()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var absent = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.Actor, test.Key));
        Assert.Equal(RevisionReportState.NotDerived, absent.State);
        Assert.Equal(0L, absent.Generation);
        Assert.Null(absent.CacheEntryId);
        Assert.Null(absent.CurrentAttempt);
        Assert.Null(absent.SelectedResult);
        Assert.False(absent.IsClaimLive);
        Assert.Null(absent.NextAttemptAtUtc);
        var cacheId = await test.InsertSyntheticEmptyKeyAsync(test.Key);
        var empty = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.Actor, test.Key));
        Assert.Equal(cacheId, empty.CacheEntryId);
        Assert.Equal(RevisionReportState.NotDerived, empty.State);
        Assert.Equal(0L, empty.Generation);
        Assert.Null(empty.CurrentAttempt);
        Assert.Null(empty.SelectedResult);
        Assert.Equal(new long[] { 1, 0, 0, 0 }, await test.CountsAsync());
        var claim = await test.ClaimAsync();
        Assert.Equal(cacheId, claim.CacheEntryId);
        Assert.Equal(1L, claim.Generation);
        Assert.Equal(TimeSpan.FromSeconds(60), claim.LeaseExpiresAtUtc - claim.StartedAtUtc);
    }

    [PostgresFact]
    public async Task FullOperationProfileSchemaAlgorithmCodecAndEveryPolicyLimitHaveSeparateDurableIdentities()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var before = await test.ImmutableSourceSnapshotAsync();
        RevisionReportPolicy[] policies =
        [
            new(nativeByteLimit: 4194303), new(workerResponseByteLimit: 8388607), new(itemLimit: 8191),
            new(responseItemLimit: 32767), new(jsonDepthLimit: 63), new(detailDepthLimit: 15), new(diagnosticLimit: 127),
            new(validationIssueLimit: 32767), new(textByteLimit: 2047), new(headerByteLimit: 16383),
            new(storedObjectByteLimit: 16777215), new(responseByteLimit: 16777215)
        ];
        RevisionReportKey[] keys =
        [
            test.Key, test.ValidationKey(), test.ProjectionKey(schema: "weft.mapping-projection.v2"),
            test.ProjectionKey(algorithm: "synthetic.report.projection.v2"), test.ProjectionKey(codec: "synthetic.report.codec.v2"),
            test.ProjectionKey(profile: new("ferrule", "different.revision", test.Key.BackendBuild, test.Key.AdapterBuild)),
            test.ProjectionKey(profile: new("ferrule", test.Key.BackendRevision, "different.build", test.Key.AdapterBuild)),
            test.ProjectionKey(profile: new("ferrule", test.Key.BackendRevision, test.Key.BackendBuild, "different.adapter")),
            .. policies.Select(policy => test.ProjectionKey(policy))
        ];
        Assert.Equal(keys.Length, keys.Select(key => key.Fingerprint.Value).Distinct(StringComparer.Ordinal).Count());
        var identities = new HashSet<Guid>();
        foreach (var key in keys)
        {
            var claim = await test.ClaimAsync(key);
            Assert.True(identities.Add(claim.CacheEntryId));
            var restored = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.Actor, key));
            Assert.Equal(key, restored.Key);
            Assert.Equal(key.Source, restored.Source.Source);
            Assert.Equal(key.Fingerprint, restored.Key.Fingerprint);
            Assert.Equal(key.CopyCanonicalUtf8(), restored.Key.CopyCanonicalUtf8());
            Assert.Equal(key.Policy.CopyCanonicalUtf8(), restored.Key.Policy.CopyCanonicalUtf8());
            Assert.Equal(claim.DerivationId, restored.CurrentAttempt!.DerivationId);
            Assert.Equal(RevisionReportState.Pending, restored.State);
        }
        Assert.Equal(new long[] { keys.Length, keys.Length, 0, 0 }, await test.CountsAsync());
        Assert.Equal(before, await test.ImmutableSourceSnapshotAsync());
    }

    [PostgresFact]
    public async Task SyntheticUsableValidationTrueAndFalseRemainAvailableAndNeverBecomeAnOperationalFailure()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        foreach (var valid in new[] { true, false })
        {
            var key = test.ValidationKey(algorithm: valid ? "synthetic.true" : "synthetic.false");
            var result = await test.PublishSyntheticAsync(key, valid: valid);
            Assert.Equal(valid, result.Observation.Validation!.IsValid);
            Assert.Equal(valid ? RevisionReportBackendStatus.Succeeded : RevisionReportBackendStatus.InvalidMapping, result.Observation.Status);
            var repeat = await test.Store.TryClaimAsync(test.AdmittedActor, key, Guid.NewGuid());
            Assert.Equal(RevisionReportStoreOutcome.Available, repeat.Outcome);
            Assert.Null(repeat.Claim);
            Assert.Equal(result, repeat.Snapshot!.SelectedResult);
            var retained = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(
                test.AdmittedActor, test.Revision, RevisionReportOperation.Validation, result.DerivationId));
            Assert.Equal(result, retained.Result);
            Assert.True(retained.Attempt.Completion!.IsUsable);
            Assert.Null(retained.Attempt.Completion.Failure);
        }
        Assert.Equal(new long[] { 2, 2, 2, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task SyntheticProjectionCoverageRetainsCompletePartialAndUnavailableWithoutInventingAnEmptyGraph()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        foreach (var coverage in Enum.GetValues<RevisionReportCoverage>())
        {
            var key = test.ProjectionKey(algorithm: "synthetic.coverage." + coverage);
            var result = await test.PublishSyntheticAsync(key, coverage: coverage);
            var observed = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.Actor, key));
            Assert.Equal(RevisionReportState.Available, observed.State);
            Assert.Equal(result, observed.SelectedResult);
            Assert.Equal(coverage, observed.SelectedResult!.Observation.Projection!.Coverage);
            Assert.Equal(coverage == RevisionReportCoverage.Complete ? 0 : 1, result.Observation.Projection!.CoverageGapCount);
            Assert.Equal(coverage == RevisionReportCoverage.Complete ? 3 : 4, result.Observation.Projection.DocumentItemCount);
        }
    }

    [PostgresFact]
    public async Task NullValidityAbsentAndExplicitlyEmptyObservationsStayDistinctFailedMetadata()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        RevisionReportObservation?[] observations =
        [
            null,
            new(RevisionReportOperation.Validation, null, null, null, null),
            new(RevisionReportOperation.Validation, RevisionReportBackendStatus.InvalidMapping,
                PostgresRevisionDerivationTestSupport.SyntheticProvenance(test.ValidationKey()), null, new(null, 0, 0, false, 1)),
            new(RevisionReportOperation.Validation, RevisionReportBackendStatus.Succeeded,
                PostgresRevisionDerivationTestSupport.SyntheticProvenance(test.ValidationKey()), null, new(true, 0, 0, false, 0))
        ];
        for (var index = 0; index < observations.Length; index++)
        {
            var key = test.ValidationKey(algorithm: "synthetic.failure." + index);
            var claim = await test.ClaimAsync(key);
            var completion = RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.StorageIo, observations[index]);
            var finished = await test.Store.FinishAsync(test.Actor, claim, completion);
            Assert.Equal(RevisionReportStoreOutcome.Applied, finished.Outcome);
            Assert.Equal(RevisionReportState.Failed, finished.Snapshot!.State);
            Assert.Null(finished.Snapshot.SelectedResult);
            Assert.Null(finished.Snapshot.CurrentAttempt!.Completion!.DerivedObject);
            var retained = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(
                test.Actor, test.Revision, RevisionReportOperation.Validation, claim.DerivationId));
            Assert.Null(retained.Result);
            Assert.Equal(completion, retained.Attempt.Completion);
            Assert.Equal(observations[index], retained.Attempt.Completion!.Observation);
            Assert.Equal(observations[index] is not null, await test.Case.ScalarAsync<bool>(
                "SELECT observation_present FROM weft.revision_derivation_attempts WHERE derivation_id=$1", claim.DerivationId));
        }
        Assert.Equal(new long[] { 4, 4, 0, 4 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task FailedObservationsPreserveAboveReducedPolicyCountsAndIndependentTextTruncation()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var key = test.ValidationKey(new(diagnosticLimit: 1, validationIssueLimit: 1));
        var claim = await test.ClaimAsync(key);
        var observation = new RevisionReportObservation(RevisionReportOperation.Validation, RevisionReportBackendStatus.InvalidMapping,
            PostgresRevisionDerivationTestSupport.SyntheticProvenance(key), null, new(false, 3, 3, true, 3));
        var completion = RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.ResourceLimit, observation);
        Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.Actor, claim, completion)).Outcome);
        var retained = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(
            test.Actor, test.Revision, key.Operation, claim.DerivationId));
        Assert.Equal(observation, retained.Attempt.Completion!.Observation);
        Assert.True(retained.Attempt.Completion.Observation!.Validation!.Truncated);
        Assert.Equal(3, retained.Attempt.Completion.Observation.Validation.EmittedIssueCount);
        Assert.Equal(3, retained.Attempt.Completion.Observation.Validation.TotalIssueCount);
        Assert.Null(retained.Result);
    }

    [PostgresFact]
    public async Task LivePendingAndFailedRetryDelayCreateNoSecondAttemptUntilExplicitSyntheticClockRecovery()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        var pending = await test.Store.TryClaimAsync(test.AdmittedActor, test.Key, Guid.NewGuid());
        Assert.Equal(RevisionReportStoreOutcome.Pending, pending.Outcome);
        Assert.Equal(claim.LeaseExpiresAtUtc, pending.Snapshot!.NextAttemptAtUtc);
        Assert.True(pending.Snapshot.IsClaimLive);
        Assert.Null(pending.Claim);
        var failed = await test.Store.FinishAsync(test.Actor, claim,
            RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.BackendUnavailable));
        Assert.Equal(RevisionReportStoreOutcome.Applied, failed.Outcome);
        var finished = failed.Snapshot!.CurrentAttempt!.FinishedAtUtc!.Value;
        var delayed = await test.Store.TryClaimAsync(test.AdmittedActor, test.Key, Guid.NewGuid());
        Assert.Equal(RevisionReportStoreOutcome.RetryDelayed, delayed.Outcome);
        Assert.Equal(finished.AddSeconds(30), delayed.Snapshot!.NextAttemptAtUtc);
        Assert.Equal(new long[] { 1, 1, 0, 1 }, await test.CountsAsync());
        await test.SyntheticMakeRetryEligibleAsync(claim);
        var old = await test.AttemptSnapshotAsync(claim.DerivationId);
        var next = await test.ClaimAsync(actor: test.AdmittedActor);
        Assert.Equal(2L, next.Generation);
        Assert.NotEqual(claim.DerivationId, next.DerivationId);
        Assert.NotEqual(claim.CandidateObjectId, next.CandidateObjectId);
        Assert.Equal(test.AdmittedActor.PrincipalId, next.OriginalActor);
        Assert.Equal(old, await test.AttemptSnapshotAsync(claim.DerivationId));
    }

    [PostgresFact]
    public async Task ExpiredPendingRemainsObservableUntilAnExplicitNewActorClaimAndStaleCompletionCannotPublish()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var original = await test.ClaimAsync();
        await test.SyntheticExpireClaimAsync(original);
        var old = await test.AttemptSnapshotAsync(original.DerivationId);
        var expired = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.AdmittedActor, test.Key));
        Assert.Equal(RevisionReportState.Pending, expired.State);
        Assert.False(expired.IsClaimLive);
        Assert.True(expired.NextAttemptAtUtc <= expired.Source.ObservedAtUtc);
        Assert.Equal(original.DerivationId, expired.CurrentAttempt!.DerivationId);
        var next = await test.ClaimAsync(actor: test.AdmittedActor);
        Assert.Equal(2L, next.Generation);
        var lost = await test.Store.FinishAsync(test.Actor, original,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(original));
        Assert.Equal(RevisionReportStoreOutcome.ClaimLost, lost.Outcome);
        Assert.Null(lost.Snapshot);
        Assert.Equal(old, await test.AttemptSnapshotAsync(original.DerivationId));
        Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.AdmittedActor, next,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(next))).Outcome);
    }

    [PostgresFact]
    public async Task RepairChangesOnlySelectionAndRetainsOldSyntheticUsableResultAndImmutableAttempt()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var before = await test.ImmutableSourceSnapshotAsync();
        var original = await test.PublishSyntheticAsync();
        var old = await test.AttemptSnapshotAsync(original.DerivationId);
        var target = new RevisionReportRepairTarget(test.Key, original.CacheEntryId, original.DerivationId,
            original.DerivedObject, RevisionReportRepairReason.Integrity);
        var repair = await test.Store.RepairAsync(test.AdmittedActor, target, Guid.NewGuid());
        Assert.Equal(RevisionReportStoreOutcome.Claimed, repair.Outcome);
        var claim = Assert.IsType<RevisionReportClaim>(repair.Claim);
        Assert.Equal(original.DerivationId, claim.RepairTargetDerivationId);
        Assert.Equal(2L, claim.Generation);
        Assert.Null(repair.Snapshot!.SelectedResult);
        Assert.Equal(RevisionReportState.Pending, repair.Snapshot.State);
        var oldRetained = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(
            test.AdmittedActor, test.Revision, test.Key.Operation, original.DerivationId));
        Assert.Equal(original, oldRetained.Result);
        Assert.Equal(old, await test.AttemptSnapshotAsync(original.DerivationId));
        Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.AdmittedActor, claim,
            PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim))).Outcome);
        var current = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.Actor, test.Key));
        Assert.Equal(claim.DerivationId, current.SelectedResult!.DerivationId);
        Assert.NotEqual(original.DerivedObject.Id, current.SelectedResult.DerivedObject.Id);
        Assert.Equal(old, await test.AttemptSnapshotAsync(original.DerivationId));
        Assert.Equal(before, await test.ImmutableSourceSnapshotAsync());
        Assert.Equal(new long[] { 1, 2, 2, 0 }, await test.CountsAsync());
    }
}
