using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresIngestionWorkflowTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task IndependentSameKeyReservationsKeepOneOperationAndOriginalProfileThenReplayTheCommittedReceipt()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var key = Guid.NewGuid();
        var intent = PostgresIngestionTestSupport.CreateIntent();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var database = new PostgresIngestionDatabase(test.Case.IngestionConnectionString);
            return await new PostgresRevisionIngestionStore(database).ReserveAsync(test.Actor, test.Project, key, intent,
                PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        }));
        Assert.Single(results, result => result.Outcome == IngestionStoreOutcome.Reserved);
        Assert.Equal(3, results.Count(result => result.Outcome == IngestionStoreOutcome.Existing));
        var facts = results[0].Receipt!.Facts;
        Assert.Single(results.Select(result => result.Receipt!.Facts.OperationId).Distinct());
        Assert.Single(results.Select(result => result.Receipt!.Facts.FutureRevision).Distinct());
        Assert.Single(results.Select(result => result.Receipt!.Facts.NativeObject).Distinct());
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mappings"));
        var replay = await test.Store.ReserveAsync(test.Actor, test.Project, key, intent,
            new("ferrule", "synthetic.changed.engine", "synthetic.changed.worker", "synthetic.changed.adapter"), new("weft.native-admission.v1", 1));
        Assert.Equal(IngestionStoreOutcome.Existing, replay.Outcome);
        Assert.Equal(facts, replay.Receipt!.Facts);
        var claim = await test.ClaimAsync(facts);
        await test.ReadyAsync(facts, claim);
        var committed = await test.Store.FinalizeAsync(test.Actor, claim);
        Assert.Equal(IngestionPhase.Committed, committed.Receipt!.Phase);
        var lostReplyReplay = await test.Store.ReserveAsync(test.Actor, test.Project, key, intent,
            PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Existing, lostReplyReplay.Outcome);
        Assert.Equal(facts, lostReplyReplay.Receipt!.Facts);
        Assert.Equal(committed.Receipt.CommittedRevision, lostReplyReplay.Receipt.CommittedRevision);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mappings"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task SameKeyChangedBodyCountMetadataFilenameOrBackendConflictsWithoutReplacingReservedFacts()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var key = Guid.NewGuid();
        var facts = await test.ReserveAsync(key);
        var bytes = PostgresIngestionTestSupport.SyntheticBody;
        foreach (var changed in new[]
        {
            PostgresIngestionTestSupport.CreateIntent(name: "Changed name"),
            PostgresIngestionTestSupport.CreateIntent(description: "Changed description"),
            PostgresIngestionTestSupport.CreateIntent(filename: "changed.native.json"),
            PostgresIngestionTestSupport.CreateIntent(body: bytes.Select(value => (byte)(value ^ 1)).ToArray()),
            PostgresIngestionTestSupport.CreateIntent(body: bytes.Concat(new byte[] { 7 }).ToArray()),
            PostgresIngestionTestSupport.CreateIntent(backend: "another-backend")
        })
        {
            var profile = new IngestionBackendProfile(changed.BackendId, "synthetic.c1.engine.v1", "synthetic.c1.worker.v1", "synthetic.c1.adapter.v1");
            var result = await test.Store.ReserveAsync(test.Actor, test.Project, key, changed, profile, PostgresIngestionTestSupport.Policy);
            Assert.Equal(IngestionStoreOutcome.IdempotencyConflict, result.Outcome);
            Assert.Null(result.Receipt);
        }
        Assert.Equal(facts, (await test.Store.GetReceiptAsync(test.Actor, test.Project, facts.OperationId))!.Facts);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task CreatePublishesMappingRevisionAndTerminalReceiptTogetherWithoutPlaceholderRows()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveAsync();
        var claim = await test.ClaimAsync(facts);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mappings"));
        Assert.Equal(IngestionStoreOutcome.ClaimLost, (await test.Store.FinalizeAsync(test.Actor, claim)).Outcome);
        await test.ReadyAsync(facts, claim);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        var result = await test.Store.FinalizeAsync(test.Actor, claim);
        Assert.Equal(IngestionStoreOutcome.Applied, result.Outcome);
        var receipt = result.Receipt!;
        Assert.Equal(IngestionPhase.Committed, receipt.Phase);
        var revision = Assert.IsType<CommittedRevisionFacts>(receipt.CommittedRevision);
        Assert.Equal(facts.FutureRevision, revision.Scope);
        Assert.Equal(facts.NativeObject, revision.NativeObject);
        Assert.Equal(facts.BackendProfile, revision.BackendProfile);
        Assert.Equal(facts.OriginalActor, revision.Creator);
        Assert.Equal(1, revision.Sequence);
        Assert.Equal(1, revision.InitialAdmissionAttemptGeneration);
        Assert.Equal(IngestionAdmissionClassification.EligibleValid, revision.InitialAdmission);
        Assert.Equal(facts.FutureRevision.RevisionId, await test.Case.ScalarAsync<Guid>("SELECT head_revision_id FROM weft.mappings"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT head_sequence FROM weft.mappings"));
        Assert.Equal(IngestionStoreOutcome.Existing, (await test.Store.FinalizeAsync(test.Actor, claim)).Outcome);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task SyntheticValidWarningsAndDecodedInvalidWithoutFindingsPreserveNullableCountsAndTruncation()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        using var diagnostics = JsonDocument.Parse("[{\"code\":\"c1.synthetic.warning\",\"nativeOwner\":{\"nodeId\":\"9007199254740993\"}}]");
        foreach (var valid in new[] { true, false })
        {
            var facts = await test.ReserveAsync();
            var claim = await test.ClaimAsync(facts);
            await test.StageAsync(facts, claim);
            var completed = await test.Store.CompleteAttemptAsync(test.Actor, claim,
                PostgresIngestionTestSupport.SyntheticCompletion(facts, valid, diagnostics: valid ? diagnostics.RootElement : null,
                    total: valid ? 1 : 0, emitted: valid ? 1 : 0, truncated: valid));
            Assert.Equal(IngestionPhase.Ready, completed.Receipt!.Phase);
            var evidence = completed.Receipt.Attempts.Single().Completion!.Evidence!;
            Assert.Equal(valid, evidence.Facts.IsValid);
            Assert.Equal(valid ? 1 : 0, evidence.Facts.EmittedIssueCount);
            Assert.Equal(valid, evidence.Facts.Truncated);
            if (valid)
                Assert.Equal("9007199254740993", evidence.CopyDocument().GetProperty("diagnostics")[0].GetProperty("nativeOwner").GetProperty("nodeId").GetString());
            var finalized = await test.Store.FinalizeAsync(test.Actor, claim);
            Assert.Equal(valid ? IngestionAdmissionClassification.EligibleValid : IngestionAdmissionClassification.EligibleDecodedInvalid,
                finalized.Receipt!.CommittedRevision!.InitialAdmission);
        }
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task WrongScopeDigestProfileOrUnconfirmedSyntheticEvidenceNeverBecomesEligible()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        for (var variant = 0; variant < 9; variant++)
        {
            var facts = await test.ReserveAsync();
            var claim = await test.ClaimAsync(facts);
            await test.StageAsync(facts, claim);
            var scope = facts.FutureRevision;
            var changedScope = variant switch
            {
                0 => new RevisionScope(Guid.NewGuid(), scope.ProjectId, scope.MappingId, scope.RevisionId),
                1 => new RevisionScope(scope.OrganizationId, Guid.NewGuid(), scope.MappingId, scope.RevisionId),
                2 => new RevisionScope(scope.OrganizationId, scope.ProjectId, Guid.NewGuid(), scope.RevisionId),
                3 => new RevisionScope(scope.OrganizationId, scope.ProjectId, scope.MappingId, Guid.NewGuid()),
                _ => scope
            };
            var profile = facts.BackendProfile;
            var observed = new IngestionProvenance(changedScope, variant == 4 ? Sha256Digest.FromBytes([99]) : facts.NativeObject.Digest,
                new(profile.BackendId, variant == 5 ? "wrong.synthetic.engine" : profile.EngineRevision,
                    variant == 6 ? "wrong.synthetic.worker" : profile.BackendBuild, variant == 7 ? "wrong.synthetic.adapter" : profile.AdapterBuild), variant != 8);
            var result = await test.Store.CompleteAttemptAsync(test.Actor, claim,
                PostgresIngestionTestSupport.SyntheticCompletion(facts, provenance: observed));
            Assert.Equal(IngestionStoreOutcome.Applied, result.Outcome);
            Assert.NotEqual(IngestionPhase.Ready, result.Receipt!.Phase);
            Assert.Null(result.Receipt.EligibleAttemptGeneration);
            Assert.Equal(IngestionAdmissionClassification.RetryableFailure, result.Receipt.Attempts.Single().Classification);
            Assert.Equal(IngestionStoreOutcome.ClaimLost, (await test.Store.FinalizeAsync(test.Actor, claim)).Outcome);
        }
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mappings"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task NullValidatorOperationalDiagnosticsAndEvidenceLimitKeepHonestDistinctRejections()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        foreach (var code in new[] { "ferrule.nativeDecodeFailed", "c1.synthetic.noValidatorResult" })
        {
            var facts = await test.ReserveAsync();
            var claim = await test.ClaimAsync(facts);
            await test.StageAsync(facts, claim);
            using var diagnostics = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new { code } }));
            var evidence = IngestionEvidenceDocument.Create(new(IngestionBackendStatus.InvalidMapping,
                new(facts.FutureRevision, facts.NativeObject.Digest, facts.BackendProfile, true), null, 0, 0, false), diagnostics.RootElement);
            var result = await test.Store.CompleteAttemptAsync(test.Actor, claim, IngestionAttemptCompletion.FromValidation(evidence));
            Assert.Equal(code == "ferrule.nativeDecodeFailed" ? IngestionAdmissionClassification.RejectedNativeDecode : IngestionAdmissionClassification.RetryableFailure,
                result.Receipt!.Attempts.Single().Classification);
            Assert.Null(result.Receipt.Attempts.Single().Completion!.Evidence!.Facts.IsValid);
            Assert.Equal(0, result.Receipt.Attempts.Single().Completion!.Evidence!.Facts.EmittedIssueCount);
            Assert.Null(result.Receipt.EligibleAttemptGeneration);
        }
        var limited = await test.ReserveAsync();
        var limitedClaim = await test.ClaimAsync(limited);
        var actualObserved = new IngestionProvenance(limited.FutureRevision, limited.NativeObject.Digest, limited.BackendProfile, false);
        var rejected = await test.Store.CompleteAttemptAsync(test.Actor, limitedClaim,
            IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit, actualObserved));
        Assert.Equal(IngestionPhase.Rejected, rejected.Receipt!.Phase);
        var completion = rejected.Receipt.Attempts.Single().Completion!;
        Assert.Equal(IngestionAdmissionClassification.RejectedResourceLimit, rejected.Receipt.Attempts.Single().Classification);
        Assert.Null(completion.Evidence);
        Assert.Equal(actualObserved, completion.ObservedProvenance);
        Assert.Equal(IngestionFailureCode.EvidenceLimit, completion.Failure);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestion_attempts WHERE operation_id=$1 AND backend_status IS NULL AND is_valid IS NULL AND total_issue_count IS NULL AND emitted_issue_count IS NULL AND evidence IS NULL", limited.OperationId));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task AnotherPublisherAndAnotherProjectsAdministratorCannotAdoptOrReadOriginalActorsOperation()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveAsync();
        var claim = await test.ClaimAsync(facts);
        await test.ReadyAsync(facts, claim);
        foreach (var principal in new[] { test.Seed.Admitted, test.Seed.OtherProjectAdmin })
        {
            var other = new ActorContext(new PrincipalId(principal));
            Assert.Null(await test.Store.GetReceiptAsync(other, test.Project, facts.OperationId));
            Assert.Equal(IngestionStoreOutcome.NotAuthorizedOrNotFound, (await test.Store.ClaimAsync(other, test.Project, facts.OperationId, Guid.NewGuid())).Outcome);
            Assert.Equal(IngestionStoreOutcome.NotAuthorizedOrNotFound, (await test.Store.MarkStagedAsync(other, claim, facts.NativeObject)).Outcome);
            Assert.Equal(IngestionStoreOutcome.NotAuthorizedOrNotFound, (await test.Store.CompleteAttemptAsync(other, claim, PostgresIngestionTestSupport.SyntheticCompletion(facts))).Outcome);
            Assert.Equal(IngestionStoreOutcome.NotAuthorizedOrNotFound, (await test.Store.FinalizeAsync(other, claim)).Outcome);
        }
        Assert.Equal(IngestionPhase.Ready, (await test.Store.GetReceiptAsync(test.Actor, test.Project, facts.OperationId))!.Phase);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task PrincipalOrganizationProjectAndRoleRevocationCommittedBeforeFinalizeDenyTheNextStatement()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        foreach (var (revoke, restore) in new[]
        {
            ("UPDATE weft.principals SET active=false WHERE id=$1", "UPDATE weft.principals SET active=true WHERE id=$1"),
            ("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$2 AND principal_id=$1", "UPDATE weft.organization_memberships SET active=true WHERE organization_id=$2 AND principal_id=$1"),
            ("UPDATE weft.project_memberships SET active=false WHERE organization_id=$2 AND project_id=$3 AND principal_id=$1", "UPDATE weft.project_memberships SET active=true WHERE organization_id=$2 AND project_id=$3 AND principal_id=$1"),
            ("UPDATE weft.project_memberships SET role='runner' WHERE organization_id=$2 AND project_id=$3 AND principal_id=$1", "UPDATE weft.project_memberships SET role='publisher' WHERE organization_id=$2 AND project_id=$3 AND principal_id=$1")
        })
        {
            var facts = await test.ReserveAsync();
            var claim = await test.ClaimAsync(facts);
            await test.ReadyAsync(facts, claim);
            object[] values = revoke.Contains("$3", StringComparison.Ordinal) ? new object[] { test.Seed.Alice, test.Seed.OrganizationA, test.Seed.ProjectA }
                : revoke.Contains("$2", StringComparison.Ordinal) ? [test.Seed.Alice, test.Seed.OrganizationA] : [test.Seed.Alice];
            await test.Case.ExecuteAsync(revoke, values);
            try
            {
                Assert.Null(await test.Store.GetReceiptAsync(test.Actor, test.Project, facts.OperationId));
                Assert.Equal(IngestionStoreOutcome.NotAuthorizedOrNotFound, (await test.Store.FinalizeAsync(test.Actor, claim)).Outcome);
            }
            finally { await test.Case.ExecuteAsync(restore, values); }
            Assert.Equal(IngestionPhase.Ready, (await test.Store.GetReceiptAsync(test.Actor, test.Project, facts.OperationId))!.Phase);
        }
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mappings"));
    }

    [PostgresFact]
    public async Task AnyHeadOutOfOrderAndConcurrentFinalizersAllocateDistinctMonotonicCommittedSequences()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var first = await test.CommitCreateAsync();
        var a = await test.ReserveAsync(intent: PostgresIngestionTestSupport.AppendIntent(first));
        var b = await test.ReserveAsync(intent: PostgresIngestionTestSupport.AppendIntent(first));
        var claimA = await test.ClaimAsync(a);
        var claimB = await test.ClaimAsync(b);
        await test.ReadyAsync(b, claimB);
        await test.ReadyAsync(a, claimA);
        Assert.Equal(2, (await test.Store.FinalizeAsync(test.Actor, claimB)).Receipt!.CommittedRevision!.Sequence);
        Assert.Equal(3, (await test.Store.FinalizeAsync(test.Actor, claimA)).Receipt!.CommittedRevision!.Sequence);
        var claims = new List<IngestionClaim>();
        for (var index = 0; index < 4; index++)
        {
            var facts = await test.ReserveAsync(intent: PostgresIngestionTestSupport.AppendIntent(first));
            var claim = await test.ClaimAsync(facts);
            await test.ReadyAsync(facts, claim);
            claims.Add(claim);
        }
        var finalized = await Task.WhenAll(claims.Select(async claim =>
        {
            await using var database = new PostgresIngestionDatabase(test.Case.IngestionConnectionString);
            return await new PostgresRevisionIngestionStore(database).FinalizeAsync(test.Actor, claim);
        }));
        Assert.All(finalized, result => Assert.Equal(IngestionPhase.Committed, result.Receipt!.Phase));
        Assert.Equal(new long[] { 4, 5, 6, 7 }, finalized.Select(result => result.Receipt!.CommittedRevision!.Sequence).Order());
        Assert.Equal(7L, await test.Case.ScalarAsync<long>("SELECT head_sequence FROM weft.mappings"));
        Assert.Equal(7L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        Assert.Equal(first.CommittedRevision, (await test.Store.GetReceiptAsync(test.Actor, test.Project, first.Facts.OperationId))!.CommittedRevision);
    }

    [PostgresFact]
    public async Task EqualHeadConcurrentFinalizersHaveOneWinnerAndOneImmutableTerminalConflict()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var initial = await test.CommitCreateAsync();
        var claims = new List<IngestionClaim>();
        for (var index = 0; index < 2; index++)
        {
            var facts = await test.ReserveAsync(intent: PostgresIngestionTestSupport.AppendIntent(initial, new EqualHead(initial.Facts.FutureRevision.RevisionId)));
            var claim = await test.ClaimAsync(facts);
            await test.ReadyAsync(facts, claim);
            claims.Add(claim);
        }
        var results = await Task.WhenAll(claims.Select(async claim =>
        {
            await using var database = new PostgresIngestionDatabase(test.Case.IngestionConnectionString);
            return await new PostgresRevisionIngestionStore(database).FinalizeAsync(test.Actor, claim);
        }));
        Assert.Single(results, result => result.Receipt!.Phase == IngestionPhase.Committed);
        var conflict = Assert.Single(results, result => result.Receipt!.Phase == IngestionPhase.HeadConflict).Receipt!;
        Assert.Null(conflict.CommittedRevision);
        Assert.NotNull(conflict.CompletedAtUtc);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT head_sequence FROM weft.mappings"));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        var replay = await test.Store.ClaimAsync(test.Actor, test.Project, conflict.Facts.OperationId, Guid.NewGuid());
        Assert.Equal(IngestionStoreOutcome.Existing, replay.Outcome);
        Assert.Equal(IngestionPhase.HeadConflict, replay.Receipt!.Phase);
    }

    [PostgresFact]
    public async Task DatabaseTimeLeaseAndStaleGenerationOwnerTokenAndReferenceFenceEveryMutation()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveAsync();
        var original = await test.ClaimAsync(facts);
        Assert.InRange(await test.Case.ScalarAsync<double>("SELECT extract(epoch FROM lease_expires_at-clock_timestamp())::double precision FROM weft.revision_ingestions WHERE operation_id=$1", facts.OperationId), 100, 120);
        Assert.Equal(IngestionStoreOutcome.InProgress, (await test.Store.ClaimAsync(test.Actor, test.Project, facts.OperationId, Guid.NewGuid())).Outcome);
        await test.ExpireLeaseAsync(facts);
        Assert.Equal(IngestionStoreOutcome.ClaimLost, (await test.Store.MarkStagedAsync(test.Actor, original, facts.NativeObject)).Outcome);
        var current = await test.ClaimAsync(facts);
        Assert.Equal(2, current.Generation);
        Assert.NotEqual(original.LeaseToken, current.LeaseToken);
        foreach (var invalid in new[]
        {
            original,
            new IngestionClaim(current.Project, current.OperationId, current.OriginalActor, Guid.NewGuid(), current.LeaseToken, current.Generation, current.LeaseExpiresAtUtc),
            new IngestionClaim(current.Project, current.OperationId, current.OriginalActor, current.ServerInstanceId, Guid.NewGuid(), current.Generation, current.LeaseExpiresAtUtc),
            new IngestionClaim(current.Project, current.OperationId, current.OriginalActor, current.ServerInstanceId, current.LeaseToken, 1, current.LeaseExpiresAtUtc)
        })
        {
            Assert.Equal(IngestionStoreOutcome.ClaimLost, (await test.Store.MarkStagedAsync(test.Actor, invalid, facts.NativeObject)).Outcome);
            Assert.Equal(IngestionStoreOutcome.ClaimLost, (await test.Store.CompleteAttemptAsync(test.Actor, invalid, PostgresIngestionTestSupport.SyntheticCompletion(facts))).Outcome);
            Assert.Equal(IngestionStoreOutcome.ClaimLost, (await test.Store.FinalizeAsync(test.Actor, invalid)).Outcome);
        }
        var wrongObject = new PrivateObjectReference(PrivateObjectId.Create(facts.FutureRevision, PrivateObjectClass.NativeArtifact), facts.NativeObject.Digest, facts.NativeObject.ByteCount);
        foreach (var reference in new[]
        {
            wrongObject,
            new PrivateObjectReference(facts.NativeObject.Id, Sha256Digest.FromBytes([99]), facts.NativeObject.ByteCount),
            new PrivateObjectReference(facts.NativeObject.Id, facts.NativeObject.Digest, facts.NativeObject.ByteCount + 1),
            new PrivateObjectReference(PrivateObjectId.Restore(facts.FutureRevision, facts.NativeObject.Id.ObjectId, PrivateObjectClass.DerivedArtifact), facts.NativeObject.Digest, facts.NativeObject.ByteCount)
        })
            Assert.Equal(IngestionStoreOutcome.ClaimLost, (await test.Store.MarkStagedAsync(test.Actor, current, reference)).Outcome);
        var scope = facts.FutureRevision;
        foreach (var foreign in new[]
        {
            new RevisionScope(Guid.NewGuid(), scope.ProjectId, scope.MappingId, scope.RevisionId),
            new RevisionScope(scope.OrganizationId, Guid.NewGuid(), scope.MappingId, scope.RevisionId),
            new RevisionScope(scope.OrganizationId, scope.ProjectId, Guid.NewGuid(), scope.RevisionId),
            new RevisionScope(scope.OrganizationId, scope.ProjectId, scope.MappingId, Guid.NewGuid())
        })
        {
            var reference = new PrivateObjectReference(PrivateObjectId.Restore(foreign, facts.NativeObject.Id.ObjectId, PrivateObjectClass.NativeArtifact), facts.NativeObject.Digest, facts.NativeObject.ByteCount);
            Assert.Equal(IngestionStoreOutcome.ClaimLost, (await test.Store.MarkStagedAsync(test.Actor, current, reference)).Outcome);
        }
        Assert.Null((await test.Store.GetReceiptAsync(test.Actor, test.Project, facts.OperationId))!.VerifiedStageGeneration);
    }

    [PostgresFact]
    public async Task ReclaimedReadyOperationNeedsFreshStageAndKeepsOriginalEligibleEvidenceGeneration()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveAsync();
        var original = await test.ClaimAsync(facts);
        await test.ReadyAsync(facts, original);
        await test.ExpireLeaseAsync(facts);
        var recovery = await test.ClaimAsync(facts);
        Assert.Equal(2, recovery.Generation);
        Assert.Equal(IngestionStoreOutcome.ClaimLost, (await test.Store.FinalizeAsync(test.Actor, recovery)).Outcome);
        Assert.Equal(IngestionStoreOutcome.ClaimLost, (await test.Store.CompleteAttemptAsync(test.Actor, recovery, IngestionAttemptCompletion.ReuseEligibleEvidence(1))).Outcome);
        await test.StageAsync(facts, recovery);
        var reused = await test.Store.CompleteAttemptAsync(test.Actor, recovery, IngestionAttemptCompletion.ReuseEligibleEvidence(1));
        Assert.Equal(IngestionStoreOutcome.Applied, reused.Outcome);
        Assert.Equal(1, reused.Receipt!.EligibleAttemptGeneration);
        Assert.Equal(2, reused.Receipt.VerifiedStageGeneration);
        Assert.Null(reused.Receipt.Attempts[1].Completion!.Evidence);
        Assert.Equal(1, reused.Receipt.Attempts[1].Completion!.ReusedAttemptGeneration);
        var committed = await test.Store.FinalizeAsync(test.Actor, recovery);
        Assert.Equal(IngestionPhase.Committed, committed.Receipt!.Phase);
        Assert.Equal(1, committed.Receipt.CommittedRevision!.InitialAdmissionAttemptGeneration);
        Assert.Equal(facts.FutureRevision, committed.Receipt.CommittedRevision.Scope);
        Assert.Equal(IngestionStoreOutcome.Existing, (await test.Store.FinalizeAsync(test.Actor, original)).Outcome);
    }

    [PostgresFact]
    public async Task EightExplicitClaimsExhaustWithoutInventingARevisionOrAllowingAnotherLease()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveAsync();
        for (var generation = 1; generation <= 8; generation++)
        {
            var claim = await test.ClaimAsync(facts);
            Assert.Equal(generation, claim.Generation);
            var released = await test.Store.CompleteAttemptAsync(test.Actor, claim, IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.NeedsSource));
            Assert.Equal(IngestionStoreOutcome.Applied, released.Outcome);
            Assert.Equal(generation, released.Receipt!.ClaimCount);
            Assert.Equal(IngestionAdmissionClassification.RetryableFailure, released.Receipt.Attempts[^1].Classification);
        }
        var exhausted = await test.Store.ClaimAsync(test.Actor, test.Project, facts.OperationId, Guid.NewGuid());
        Assert.Equal(IngestionStoreOutcome.Existing, exhausted.Outcome);
        Assert.Equal(IngestionPhase.AttemptExhausted, exhausted.Receipt!.Phase);
        Assert.Equal(8, exhausted.Receipt.Attempts.Count);
        Assert.NotNull(exhausted.Receipt.CompletedAtUtc);
        Assert.Null(exhausted.Claim);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mappings"));
    }

    [PostgresFact]
    public async Task IndependentPoolAndSameOwnedClusterRestartRetainReservationReceiptAndRevisionFacts()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var original = await test.CommitCreateAsync();
        var connectionString = test.Case.IngestionConnectionString;
        await test.Database.DisposeAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var fresh = new PostgresIngestionDatabase(connectionString);
        var store = new PostgresRevisionIngestionStore(fresh);
        var reopened = await store.GetReceiptAsync(test.Actor, test.Project, original.Facts.OperationId);
        Assert.Equal(original.Facts, reopened!.Facts);
        Assert.Equal(original.CommittedRevision, reopened.CommittedRevision);
        Assert.Equal(original.CompletedAtUtc, reopened.CompletedAtUtc);
        Assert.Equal(original.Attempts.Single().Completion!.Evidence!.Facts, reopened.Attempts.Single().Completion!.Evidence!.Facts);
        Assert.Equal(original.Attempts.Single().Completion!.Evidence!.Version, reopened.Attempts.Single().Completion!.Evidence!.Version);
        Assert.Equal(0, reopened.Attempts.Single().Completion!.Evidence!.CopyDocument().GetProperty("diagnostics").GetArrayLength());
    }

    [PostgresFact]
    public async Task AppendHeadIntentConflictsAndForeignMappingAncestryCannotReserveAnOperation()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var initial = await test.CommitCreateAsync();
        var key = Guid.NewGuid();
        var facts = await test.ReserveAsync(key, PostgresIngestionTestSupport.AppendIntent(initial));
        var changedHead = await test.Store.ReserveAsync(test.Actor, test.Project, key,
            PostgresIngestionTestSupport.AppendIntent(initial, new EqualHead(initial.Facts.FutureRevision.RevisionId)),
            PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.IdempotencyConflict, changedHead.Outcome);
        Assert.Equal(facts, (await test.Store.GetReceiptAsync(test.Actor, test.Project, facts.OperationId))!.Facts);
        var projectB = new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB);
        var foreignTarget = new MappingScope(projectB.OrganizationId, projectB.ProjectId, initial.Facts.FutureRevision.MappingId);
        var bytes = PostgresIngestionTestSupport.SyntheticBody;
        var foreign = IngestionIntent.ForAppend(foreignTarget, "ferrule", null, new AnyHead(), Sha256Digest.FromBytes(bytes), bytes.LongLength);
        var rejected = await test.Store.ReserveAsync(test.Actor, projectB, Guid.NewGuid(), foreign,
            PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.NotAuthorizedOrNotFound, rejected.Outcome);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }
}
