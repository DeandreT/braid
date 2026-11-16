using System.Runtime.Versioning;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>
/// Real C2/Ferrule/v2 publication and restricted PostgreSQL serialization.
/// The pre-Finish outage and old attempt's clock shift are explicit test setup.
/// A live usable completion and a 24h-eligible retirement cannot share one candidate.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresMaintenanceDerivedAttachmentConcurrencyTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task GenuineUsableFinishAndSelectionPreserveTheirNewCandidateWhileOldCandidateRetirementWaitsInEitherOrder()
    {
        foreach (var finishFirst in new[] { true, false })
        {
            await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
            var nativeBytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
            var created = await test.CreateNativeAsync(nativeBytes);
            Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
            var source = await test.ReceiptAsync(created);
            var scope = source.Facts.FutureRevision;
            var nativeIdentity = PrivateStorageV2Identity.Read(test.Root.FinalPath(source.Facts.NativeObject));
            var markerBytes = File.ReadAllBytes(test.Root.MarkerPath);
            var markerIdentity = PrivateStorageV2Identity.Read(test.Root.MarkerPath);

            // A really ran the worker and published/read back its encoded object.
            // Only its subsequent SQL bookkeeping is deliberately interrupted.
            var old = await PrepareWithoutFinishingAsync(test, scope);
            await AgeOnlyUnfinishedAttemptAsync(test.Case, old.Claim);
            var oldAttempt = await AttemptJsonAsync(test.Case, old.Claim.DerivationId);
            var nativeHistory = await NativeHistoryAsync(test.Case);

            // B is a new genuine worker/codec/publication result, never an invented
            // usable completion. Its original public service call has fully settled.
            var current = await PrepareWithoutFinishingAsync(test, scope);
            Assert.Equal(old.Claim.Key, current.Claim.Key);
            Assert.Equal(old.Claim.CacheEntryId, current.Claim.CacheEntryId);
            Assert.Equal(old.Claim.Generation + 1, current.Claim.Generation);
            Assert.NotEqual(old.Claim.CandidateObjectId, current.Claim.CandidateObjectId);
            Assert.Equal(TimeSpan.FromSeconds(60), current.Claim.LeaseExpiresAtUtc - current.Claim.StartedAtUtc);
            Assert.Equal(2, test.ReportBackend.Completions.Count);
            Assert.All(test.ReportBackend.Completions, result => Assert.True(result.IsUsable));
            var poolPids = await test.AssertPoolsUnboundAsync();

            await using var maintenanceDatabase = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
            var maintenance = new PostgresPrivateObjectMaintenanceStore(maintenanceDatabase);
            var maintenancePid = await CommandTestSupport.AssertIdleAsync(maintenanceDatabase, test.Case.Name);
            var (cleanup, finished) = await SerializeAsync(test, maintenanceDatabase, maintenance, current, finishFirst);
            Assert.Equal(RevisionReportStoreOutcome.Applied, finished.Outcome);
            var finishedSnapshot = Assert.IsType<RevisionReportReadSnapshot>(finished.Snapshot);
            var selected = Assert.IsType<RevisionReportResult>(finishedSnapshot.SelectedResult);
            Assert.Equal(current.Claim.DerivationId, selected.DerivationId);
            Assert.Equal(current.Claim.CacheEntryId, selected.CacheEntryId);
            Assert.Equal(current.Claim.Key, selected.Key);
            Assert.Equal(current.Reference, selected.DerivedObject);
            Assert.Equal(current.Completion, finishedSnapshot.CurrentAttempt!.Completion);
            Assert.Equal(old.Claim.CandidateObjectId, cleanup.Retirement.Target.Object);
            Assert.Equal(CleanupTargetKind.FinalObject, cleanup.Retirement.Target.Kind);
            Assert.Equal(ObjectCleanupReason.ExpiredDerivedCandidate, cleanup.Retirement.Reason);
            Assert.Equal(old.Claim.DerivationId, cleanup.Retirement.Origin.DerivationId);
            Assert.Equal(oldAttempt, await AttemptJsonAsync(test.Case, old.Claim.DerivationId));
            Assert.Equal(nativeHistory, await NativeHistoryAsync(test.Case));
            Assert.Equal(old.Identity, PrivateStorageV2Identity.Read(test.Root.FinalPath(old.Reference)));
            Assert.Equal(old.Bytes, await test.Physical.ReadVerifiedAsync(scope, old.Reference));
            Assert.Equal(current.Identity, PrivateStorageV2Identity.Read(test.Root.FinalPath(current.Reference)));
            Assert.Equal(current.Bytes, await test.Physical.ReadVerifiedAsync(scope, current.Reference));

            // The committed original claim alone authorizes actual retirement of A.
            // No metadata transaction spans this physical operation.
            await test.AssertPoolsUnboundAsync(poolPids);
            await CommandTestSupport.AssertIdleAsync(maintenanceDatabase, test.Case.Name, maintenancePid);
            var originsBeforeDelete = await CommandTestSupport.OriginsAsync(test.Case);
            var physical = await test.Physical.RetireAsync(cleanup);
            Assert.Equal(PrivateObjectRetirementOutcome.Deleted, physical);
            var acknowledged = await maintenance.FinishAsync(cleanup, ObjectCleanupDisposition.Deleted);
            Assert.Equal(ObjectCleanupFinishOutcome.Applied, acknowledged.Outcome);
            var acknowledgedAttempt = Assert.IsType<ObjectCleanupAttempt>(acknowledged.Attempt);
            Assert.Equal(cleanup, acknowledgedAttempt.Claim);
            Assert.Equal(ObjectCleanupDisposition.Deleted, acknowledgedAttempt.Disposition);
            Assert.NotNull(acknowledgedAttempt.FinishedAtUtc);
            Assert.Equal(acknowledgedAttempt, await maintenance.GetAttemptAsync(test.Project,
                cleanup.Retirement.RetirementId, cleanup.Generation));
            Assert.False(File.Exists(test.Root.FinalPath(old.Reference)));
            Assert.True(File.Exists(Path.Combine(PrivateStorageV2Data.ObjectLeaf(test.Root, old.Reference.Id), "object.retired")));
            Assert.Equal(originsBeforeDelete, await CommandTestSupport.OriginsAsync(test.Case));
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.private_object_retirements"));
            Assert.Equal(1L, await test.Case.ScalarAsync<long>(
                "SELECT count(*) FROM braid.private_object_retirements WHERE completed_attempt_id=$1", cleanup.AttemptId));
            Assert.Equal(0L, await test.Case.ScalarAsync<long>(
                "SELECT count(*) FROM braid.private_object_retirements WHERE object_id=$1", current.Reference.Id.ObjectId));
            Assert.Equal(0L, await test.Case.ScalarAsync<long>(
                "SELECT count(*) FROM braid.revision_derivation_attempts WHERE derivation_id=$1 AND result_usable", old.Claim.DerivationId));

            var available = RevisionReportTestSupport.Available(await test.Reports.GetCurrentAsync(test.Actor,
                scope, RevisionReportOperation.Projection));
            Assert.IsType<VerifiedProjectionReport>(available);
            Assert.Equal(selected, available.Metadata);
            Assert.Equal(selected, RevisionReportTestSupport.Available(await test.Reports.GetRetainedAsync(test.Actor,
                scope, RevisionReportOperation.Projection, current.Claim.DerivationId)).Metadata);
            Assert.Equal(current.Identity, PrivateStorageV2Identity.Read(test.Root.FinalPath(current.Reference)));
            Assert.Equal(current.Bytes, await test.Physical.ReadVerifiedAsync(scope, current.Reference));
            Assert.Equal(nativeIdentity, PrivateStorageV2Identity.Read(test.Root.FinalPath(source.Facts.NativeObject)));
            Assert.Equal(nativeBytes, await test.Physical.ReadVerifiedAsync(scope, source.Facts.NativeObject));
            Assert.Equal(markerIdentity, PrivateStorageV2Identity.Read(test.Root.MarkerPath));
            Assert.Equal(markerBytes, File.ReadAllBytes(test.Root.MarkerPath));
            Assert.Equal(nativeHistory, await NativeHistoryAsync(test.Case));
            Assert.Empty((await maintenance.ClaimAsync(test.Project, Guid.NewGuid())).Claims);
            await test.AssertPoolsUnboundAsync(poolPids);
            await CommandTestSupport.AssertIdleAsync(maintenanceDatabase, test.Case.Name, maintenancePid);
        }
    }

    private static async Task<PreparedReport> PrepareWithoutFinishingAsync(
        PostgresClaimAwareStorageIntegrationSupport test, RevisionScope scope)
    {
        RevisionReportClaim? capturedClaim = null;
        RevisionReportCompletion? capturedCompletion = null;
        test.ReportMetadata.BeforeFinishAsync = (claim, completion, _) =>
        {
            Assert.Null(capturedClaim);
            capturedClaim = claim;
            capturedCompletion = completion;
            throw new IOException("Owned pre-Finish bookkeeping stop; no database completion was sent.");
        };
        try
        {
            await Assert.ThrowsAsync<RevisionReportServiceException>(() => test.Reports.EnsureAsync(test.Actor,
                scope, RevisionReportOperation.Projection));
        }
        finally { test.ReportMetadata.BeforeFinishAsync = null; }
        var actualClaim = Assert.IsType<RevisionReportClaim>(capturedClaim);
        var actualCompletion = Assert.IsType<RevisionReportCompletion>(capturedCompletion);
        Assert.True(actualCompletion.IsUsable);
        Assert.Equal(actualClaim.Key, actualCompletion.Key);
        var observation = Assert.IsType<RevisionReportObservation>(actualCompletion.Observation);
        Assert.Equal(RevisionReportBackendStatus.Succeeded, observation.Status);
        Assert.IsType<RevisionReportProjectionSummary>(observation.Projection);
        var provenance = Assert.IsType<RevisionReportProvenance>(observation.Provenance);
        Assert.True(provenance.Confirmed);
        Assert.Equal(actualClaim.Key.Scope, provenance.Scope);
        Assert.Equal(actualClaim.Key.Source.NativeObject.Digest, provenance.NativeDigest);
        Assert.Equal(actualClaim.Key.Profile, provenance.Profile);
        Assert.Equal(test.ReportBackend.Completions[^1].Observation, observation);
        var reference = Assert.IsType<PrivateObjectReference>(actualCompletion.DerivedObject);
        Assert.Equal(actualClaim.CandidateObjectId, reference.Id);
        var publication = Assert.Single(test.Storage.Publications, item => item.Reference.Id == reference.Id);
        Assert.Equal(PrivateObjectPublishResult.Created, publication.Result);
        Assert.Equal(PrivatePublicationOwner.ForReport(test.Actor, actualClaim), publication.Owner);
        Assert.True(await test.Case.ScalarAsync<bool>(
            "SELECT finished_at IS NULL AND completion_kind IS NULL AND NOT result_usable FROM braid.revision_derivation_attempts WHERE derivation_id=$1",
            actualClaim.DerivationId));
        var bytes = await test.Physical.ReadVerifiedAsync(scope, reference);
        Assert.Equal(reference.Digest, Sha256Digest.FromBytes(bytes));
        Assert.Equal(reference.ByteCount, bytes.LongLength);
        return new(actualClaim, actualCompletion, reference, bytes,
            PrivateStorageV2Identity.Read(test.Root.FinalPath(reference)));
    }

    private static async Task<(ObjectCleanupClaim Cleanup, RevisionReportStoreResult Finish)> SerializeAsync(
        PostgresClaimAwareStorageIntegrationSupport test, PostgresPrivateObjectMaintenanceDatabase maintenanceDatabase,
        PostgresPrivateObjectMaintenanceStore maintenance, PreparedReport current, bool finishFirst)
    {
        await using var writer = await test.DerivationDatabase.BeginTransactionAsync(test.Project.OrganizationId);
        await using var retiring = await maintenanceDatabase.BeginTransactionAsync(test.Project);
        Task? pending = null;
        try
        {
            if (finishFirst)
            {
                var finished = await test.ReportWriter.FinishInTransactionAsync(writer, test.Actor,
                    current.Claim, current.Completion);
                Assert.Equal(RevisionReportStoreOutcome.Applied, finished.Outcome);
                await using var observer = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case,
                    retiring.Connection.ProcessID, writer.Connection.ProcessID);
                var waiting = maintenance.ClaimInTransactionAsync(retiring, test.Project, Guid.NewGuid());
                pending = waiting;
                await observer.WaitForBlockedAsync(waiting);
                await writer.CommitAsync();
                var cleanup = Assert.Single((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Claims);
                await retiring.CommitAsync();
                return (cleanup, finished);
            }
            else
            {
                var cleanup = Assert.Single((await maintenance.ClaimInTransactionAsync(retiring,
                    test.Project, Guid.NewGuid())).Claims);
                await using var observer = await ProjectCreationTestSupport.OpenBlockingObserverAsync(test.Case,
                    writer.Connection.ProcessID, retiring.Connection.ProcessID);
                var waiting = test.ReportWriter.FinishInTransactionAsync(writer, test.Actor,
                    current.Claim, current.Completion);
                pending = waiting;
                await observer.WaitForBlockedAsync(waiting);
                await retiring.CommitAsync();
                var finished = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
                await writer.CommitAsync();
                return (cleanup, finished);
            }
        }
        finally
        {
            if (finishFirst) await writer.DisposeAsync(); else await retiring.DisposeAsync();
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch when (pending.IsCompleted) { }
            }
            if (finishFirst) await retiring.DisposeAsync(); else await writer.DisposeAsync();
        }
    }

    private static async Task AgeOnlyUnfinishedAttemptAsync(PostgresCase test, RevisionReportClaim claim)
    {
        // Only fixture timestamps move. No successful completion, source, seal,
        // publication registration, candidate ID or key fact is authored here.
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test);
        await using var transaction = await owner.BeginTransactionAsync();
        await using (var disable = new NpgsqlCommand(
            "ALTER TABLE braid.revision_derivation_attempts DISABLE TRIGGER revision_derivation_attempts_transition_guard", owner, transaction))
            await disable.ExecuteNonQueryAsync();
        await using (var age = new NpgsqlCommand("""
            UPDATE braid.revision_derivation_attempts SET started_at=started_at-$1,lease_expires_at=lease_expires_at-$1
            WHERE organization_id=$2 AND project_id=$3 AND mapping_id=$4 AND revision_id=$5
              AND cache_entry_id=$6 AND derivation_id=$7 AND generation=$8
              AND finished_at IS NULL AND completion_kind IS NULL AND NOT result_usable
            """, owner, transaction))
        {
            age.Parameters.AddWithValue(TimeSpan.FromHours(25));
            age.Parameters.AddWithValue(claim.Key.Scope.OrganizationId);
            age.Parameters.AddWithValue(claim.Key.Scope.ProjectId);
            age.Parameters.AddWithValue(claim.Key.Scope.MappingId);
            age.Parameters.AddWithValue(claim.Key.Scope.RevisionId);
            age.Parameters.AddWithValue(claim.CacheEntryId);
            age.Parameters.AddWithValue(claim.DerivationId);
            age.Parameters.AddWithValue(claim.Generation);
            Assert.Equal(1, await age.ExecuteNonQueryAsync());
        }
        await using (var flush = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", owner, transaction))
            await flush.ExecuteNonQueryAsync();
        await using (var enable = new NpgsqlCommand(
            "ALTER TABLE braid.revision_derivation_attempts ENABLE TRIGGER revision_derivation_attempts_transition_guard", owner, transaction))
            await enable.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static Task<string> AttemptJsonAsync(PostgresCase test, Guid derivationId) =>
        test.ScalarAsync<string>("SELECT to_jsonb(a)::text FROM braid.revision_derivation_attempts a WHERE derivation_id=$1", derivationId);

    private static Task<string> NativeHistoryAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
          'mappings',(SELECT jsonb_agg(to_jsonb(x) ORDER BY organization_id,project_id,id) FROM braid.mappings x),
          'revisions',(SELECT jsonb_agg(to_jsonb(x) ORDER BY organization_id,project_id,mapping_id,id) FROM braid.mapping_revisions x),
          'ingestions',(SELECT jsonb_agg(to_jsonb(x) ORDER BY operation_id) FROM braid.revision_ingestions x),
          'native_attempts',(SELECT jsonb_agg(to_jsonb(x) ORDER BY operation_id,generation) FROM braid.revision_ingestion_attempts x))::text
        """);

    private sealed record PreparedReport(RevisionReportClaim Claim, RevisionReportCompletion Completion,
        PrivateObjectReference Reference, byte[] Bytes, PrivatePublicationInodeIdentity Identity);
}
