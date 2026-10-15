using System.Runtime.Versioning;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>
/// Actual external process death, PostgreSQL journal, existing v2 root and C2
/// native rejection. Timestamp authoring is setup, not elapsed-grace evidence.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresMaintenanceCommandProcessTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task ActualDeathAfterCommittedClaimRetainsItsUnfinishedAttemptAndReopensTheSameRootForRetry()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var receipt = await MaintenanceCommandProcessTestSupport.RejectedNativeAsync(test);
        var markerBytes = File.ReadAllBytes(test.Root.MarkerPath);
        var markerIdentity = PrivateStorageV2Identity.Read(test.Root.MarkerPath);
        await using var database = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
        var metadata = new PostgresPrivateObjectMaintenanceStore(database);
        var directory = test.Root.NewDirectory("maintenance-after-claim");
        await using (var child = await MaintenanceCommandProcessProbe.StartAsync(test,
            gateDirectory: directory, gateStage: "afterClaim"))
        {
            var signal = await child.WaitReadyAsync(directory, "afterClaim");
            Assert.Equal(1, signal.ClaimCount);
            Assert.Null(signal.Physical);
            var claim = await MaintenanceCommandProcessTestSupport.SingleCleanupClaimAsync(test.Case, metadata, test.Project);
            Assert.Equal(receipt.Facts.NativeObject.Id, claim.Retirement.Target.Object);
            Assert.Equal(TimeSpan.FromSeconds(60), claim.LeaseExpiresAtUtc - claim.StartedAtUtc);
            Assert.Equal(ObjectCleanupReason.TerminalNativeStage, claim.Retirement.Reason);
            Assert.Equal("{not native JSON"u8.ToArray(), File.ReadAllBytes(test.Root.FinalPath(receipt.Facts.NativeObject)));
            Assert.Null((await metadata.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, 1))!.FinishedAtUtc);
            await child.KillAsync();

            // The original committed journal row persists after real process loss.
            var original = Assert.IsType<ObjectCleanupAttempt>(await metadata.GetAttemptAsync(test.Project,
                claim.Retirement.RetirementId, 1));
            Assert.Equal(claim, original.Claim);
            Assert.Null(original.Disposition);
            Assert.Null(original.FinishedAtUtc);
            Assert.Equal(markerBytes, File.ReadAllBytes(test.Root.MarkerPath));
            Assert.Equal(markerIdentity, PrivateStorageV2Identity.Read(test.Root.MarkerPath));

            // Disposable owner-authored 120-second shift admits retry without
            // changing the production 60-second lease or 30-second retry delay.
            await MaintenanceCommandProcessTestSupport.AgeCleanupAsync(test.Case, claim);
            await using var retry = await MaintenanceCommandProcessProbe.StartAsync(test);
            AssertRun(await retry.CompleteAsync(), "Deleted", deleted: 1, missing: 0, generation: 2);
            Assert.False(File.Exists(test.Root.FinalPath(receipt.Facts.NativeObject)));
            var retained = Assert.IsType<ObjectCleanupAttempt>(await metadata.GetAttemptAsync(test.Project,
                claim.Retirement.RetirementId, 1));
            Assert.Equal(claim.AttemptId, retained.Claim.AttemptId);
            Assert.Null(retained.Disposition);
            Assert.Null(retained.FinishedAtUtc);
            var successor = Assert.IsType<ObjectCleanupAttempt>(await metadata.GetAttemptAsync(test.Project,
                claim.Retirement.RetirementId, 2));
            Assert.Equal(ObjectCleanupDisposition.Deleted, successor.Disposition);
            Assert.NotNull(successor.FinishedAtUtc);
            Assert.NotEqual(claim.AttemptId, successor.Claim.AttemptId);
            Assert.Equal(markerBytes, File.ReadAllBytes(test.Root.MarkerPath));
            Assert.Equal(markerIdentity, PrivateStorageV2Identity.Read(test.Root.MarkerPath));
        }
    }

    [RevisionIngestionFact]
    public async Task ActualDeathAfterUnlinkBeforeFinishRecoversAsRealMissingWithoutInventingAnOriginalDeletedAcknowledgement()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var receipt = await MaintenanceCommandProcessTestSupport.RejectedNativeAsync(test);
        var markerBytes = File.ReadAllBytes(test.Root.MarkerPath);
        var markerIdentity = PrivateStorageV2Identity.Read(test.Root.MarkerPath);
        await using var database = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
        var metadata = new PostgresPrivateObjectMaintenanceStore(database);
        var directory = test.Root.NewDirectory("maintenance-after-unlink");
        await using var original = await MaintenanceCommandProcessProbe.StartAsync(test,
            gateDirectory: directory, gateStage: "afterRetire");
        var signal = await original.WaitReadyAsync(directory, "afterRetire");
        Assert.Equal("Deleted", signal.Physical);
        Assert.False(File.Exists(test.Root.FinalPath(receipt.Facts.NativeObject)));
        var claim = await MaintenanceCommandProcessTestSupport.SingleCleanupClaimAsync(test.Case, metadata, test.Project);
        Assert.Equal(receipt.Facts.NativeObject.Id, claim.Retirement.Target.Object);
        var beforeDeath = Assert.IsType<ObjectCleanupAttempt>(await metadata.GetAttemptAsync(test.Project,
            claim.Retirement.RetirementId, claim.Generation));
        Assert.Null(beforeDeath.Disposition);
        Assert.Null(beforeDeath.FinishedAtUtc);
        Assert.True(File.Exists(Path.Combine(PrivateStorageV2Data.ObjectLeaf(test.Root, receipt.Facts.NativeObject.Id), "object.retired")));
        await original.KillAsync();
        Assert.Equal(beforeDeath, await metadata.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, claim.Generation));

        await MaintenanceCommandProcessTestSupport.AgeCleanupAsync(test.Case, claim);
        await using var recovery = await MaintenanceCommandProcessProbe.StartAsync(test);
        AssertRun(await recovery.CompleteAsync(), "Missing", deleted: 0, missing: 1, generation: 2);
        var old = Assert.IsType<ObjectCleanupAttempt>(await metadata.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, 1));
        Assert.Null(old.FinishedAtUtc);
        Assert.Null(old.Disposition);
        Assert.Equal(claim.AttemptId, old.Claim.AttemptId);
        var completed = Assert.IsType<ObjectCleanupAttempt>(await metadata.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, 2));
        Assert.Equal(ObjectCleanupDisposition.Missing, completed.Disposition);
        Assert.NotNull(completed.FinishedAtUtc);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_cleanup_attempts WHERE disposition='deleted'"));
        Assert.Equal(markerBytes, File.ReadAllBytes(test.Root.MarkerPath));
        Assert.Equal(markerIdentity, PrivateStorageV2Identity.Read(test.Root.MarkerPath));
        Assert.Equal(IngestionPhase.Rejected, (await test.Native.Metadata.GetReceiptAsync(test.Actor, test.Project,
            receipt.Facts.OperationId))!.Phase);
    }

    [RevisionIngestionFact]
    public async Task ActualDeathDuringBoundTrackedPublicationLeavesPositiveRegistrationAndOnlyItsTemporaryCanBeCleaned()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
        var intent = IngestionIntent.ForCreate(new("Interrupted tracked publication", "Actual external writer"),
            "ferrule", null, Sha256Digest.FromBytes(bytes), bytes.LongLength);
        var reserved = await test.Native.Metadata.ReserveAsync(test.Actor, test.Project, Guid.NewGuid(), intent,
            test.Native.Registration.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, reserved.Outcome);
        var facts = reserved.Receipt!.Facts;
        var markerBytes = File.ReadAllBytes(test.Root.MarkerPath);
        var markerIdentity = PrivateStorageV2Identity.Read(test.Root.MarkerPath);
        var directory = test.Root.NewDirectory("maintenance-during-publication");
        await using var writer = await MaintenanceCommandProcessProbe.StartAsync(test, operation: "publish",
            gateDirectory: directory, gateStage: "duringPublication", operationId: facts.OperationId, content: bytes);
        var signal = await writer.WaitReadyAsync(directory, "duringPublication");
        Assert.Equal(0, signal.ClaimCount);
        Assert.Null(signal.Physical);
        var owner = PrivatePublicationOwner.ForIngestion(test.Actor,
            await MaintenanceCommandProcessTestSupport.NativeClaimAsync(test, facts.OperationId));
        var tempId = await test.Case.ScalarAsync<Guid>("SELECT temp_id FROM weft.private_object_publications");
        var rootId = PrivateStorageV2Data.Marker(test.Root).Root;
        var publication = Assert.IsType<PrivatePublicationSnapshot>(await test.NativeRegistry.Actual.GetAsync(owner,
            rootId, facts.NativeObject.Id, tempId));
        Assert.True(publication.CanWrite);
        Assert.NotNull(publication.Record.Binding);
        Assert.Equal(owner, publication.Record.Reservation.Owner);
        var key = publication.Record.Reservation.Key;
        var tempPath = PrivateStorageV2Data.TempPath(test.Root, key);
        var identity = PrivateStorageV2Identity.Read(tempPath);
        Assert.Equal(identity, publication.Record.Binding.ActualIdentity.FileIdentity);
        Assert.Equal(0, new FileInfo(tempPath).Length);
        Assert.False(File.Exists(test.Root.FinalPath(facts.NativeObject)));
        Assert.Empty(test.Native.Runner.NativeBodies); // No worker ran in the test-only writer.
        var controlRoot = PrivateStorageV2Data.ControlRoot(test.Root);
        var tempGatePath = Path.GetRelativePath(controlRoot,
            Path.Combine(PrivateStorageV2Data.TempLeaf(test.Root, key), "temp.gate"));
        var originalControlPaths = Directory.EnumerateFiles(controlRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(controlRoot, path)).ToArray();
        Assert.Contains(tempGatePath, originalControlPaths);
        var originalControlIdentities = originalControlPaths.ToDictionary(path => path,
            path => PrivateStorageV2Identity.Read(Path.Combine(controlRoot, path)), StringComparer.Ordinal);
        Assert.Equal(0L, new FileInfo(Path.Combine(controlRoot, tempGatePath)).Length);
        // The live writer owns this exact gate EX; defer only its byte read until reap.
        var originalControl = originalControlPaths.Where(path => !StringComparer.Ordinal.Equals(path, tempGatePath))
            .ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(controlRoot, path)), StringComparer.Ordinal);
        await writer.KillAsync();
        Assert.Equal(identity, PrivateStorageV2Identity.Read(tempPath));
        Assert.Equal(publication.Record, (await test.NativeRegistry.Actual.GetAsync(owner, rootId, facts.NativeObject.Id, tempId))!.Record);
        var retainedControl = PrivateStorageV2Data.Files(controlRoot);
        Assert.Equal(originalControlPaths.Order(StringComparer.Ordinal), retainedControl.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, originalIdentity) in originalControlIdentities)
            Assert.Equal(originalIdentity, PrivateStorageV2Identity.Read(Path.Combine(controlRoot, path)));
        foreach (var (path, content) in originalControl) Assert.Equal(content, retainedControl[path]);
        Assert.Empty(retainedControl[tempGatePath]);

        // The actual process-loss proof above retains the original row/seals.
        // This separate quiesced, owner-authored fixture phase changes coherent
        // timestamps and linked record hashes; it is not elapsed-grace evidence.
        await test.Physical.DisposeAsync();
        var aged = await MaintenanceCommandTrackedTemporaryAging.AgeAsync(test.Case, test.Root, key, TimeSpan.FromHours(25));
        Assert.Equal(publication.Record.Reservation.PublicationId, aged.Reservation.PublicationId);
        Assert.Equal(publication.Record.Binding.ActualIdentity, aged.Binding!.ActualIdentity);
        Assert.Equal(identity, PrivateStorageV2Identity.Read(tempPath));
        await using var cleanup = await MaintenanceCommandProcessProbe.StartAsync(test);
        AssertRun(await cleanup.CompleteAsync(), "Deleted", deleted: 1, missing: 0, generation: 1);
        Assert.False(File.Exists(tempPath));
        Assert.True(File.Exists(Path.Combine(PrivateStorageV2Data.TempLeaf(test.Root, key), "temp.retired")));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_retirements WHERE target_kind='final_object'"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_retirements WHERE target_kind='tracked_temporary'"));
        Assert.Equal(IngestionPhase.Reserved, (await test.Native.Metadata.GetReceiptAsync(test.Actor, test.Project, facts.OperationId))!.Phase);
        Assert.Equal(markerBytes, File.ReadAllBytes(test.Root.MarkerPath));
        Assert.Equal(markerIdentity, PrivateStorageV2Identity.Read(test.Root.MarkerPath));
    }

    [RevisionIngestionFact]
    public async Task ActualFinishCommitReplyLossRecoversOnlyTheOriginalAttemptAfterItsRealRoutedCallSettles()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var receipt = await MaintenanceCommandProcessTestSupport.RejectedNativeAsync(test);
        await using var database = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
        var direct = new PostgresPrivateObjectMaintenanceStore(database);
        var actual = new NpgsqlConnectionStringBuilder(test.Case.MaintenanceConnectionString);
        await using var relay = new RevisionReportCommitAckLossRelay(actual.Host!, actual.Port);
        await using var routedDatabase = new PostgresPrivateObjectMaintenanceDatabase(relay.Route(actual.ConnectionString));
        var metadata = new RoutedFinishMetadata(direct, new PostgresPrivateObjectMaintenanceStore(routedDatabase));
        var retirement = new RecordingRetirement(test.Physical);
        var service = new PrivateObjectMaintenanceService(metadata, direct, direct, test.Physical, retirement, Guid.NewGuid());
        var run = service.RunOnceAsync(test.Project);
        try
        {
            await relay.CommitAcknowledgementWithheld.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(relay.FrontendCommitSeen);
            Assert.True(relay.BackendCommitCompleteSeen);
            Assert.True(relay.BackendIdleReadySeen);
            Assert.False(run.IsCompleted);
            Assert.False(metadata.OriginalFinishSettled);
            Assert.Empty(metadata.RecoveredClaims);
            var original = Assert.IsType<ObjectCleanupClaim>(metadata.OriginalClaim);
            Assert.Equal(receipt.Facts.NativeObject.Id, original.Retirement.Target.Object);
            Assert.Equal(ObjectCleanupDisposition.Deleted, metadata.ExpectedDisposition);
            Assert.Equal(original, Assert.Single(retirement.Claims));
            Assert.False(File.Exists(test.Root.FinalPath(receipt.Facts.NativeObject)));
            var durable = Assert.IsType<ObjectCleanupAttempt>(await direct.GetAttemptAsync(test.Project,
                original.Retirement.RetirementId, original.Generation));
            Assert.Equal(original, durable.Claim);
            Assert.Equal(ObjectCleanupDisposition.Deleted, durable.Disposition);
            Assert.NotNull(durable.FinishedAtUtc);
            var snapshot = await test.Case.ScalarAsync<string>("""
                SELECT jsonb_agg(to_jsonb(a) ORDER BY generation)::text
                FROM weft.private_object_cleanup_attempts a WHERE retirement_id=$1
                """, original.Retirement.RetirementId);

            relay.DisconnectCaller();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PrivateObjectMaintenanceRunOutcome.Finished, result.Outcome);
            Assert.Null(result.Failure);
            Assert.True(metadata.OriginalFinishSettled);
            Assert.True(metadata.OriginalFinishThrew);
            Assert.Equal(1, metadata.FinishCalls);
            Assert.Equal(1, result.PhysicalDeleted);
            Assert.Equal(1, result.AcknowledgedDeleted);
            Assert.Equal(PrivateObjectMaintenanceAcknowledgement.Recovered, Assert.Single(result.Targets).Acknowledgement);
            Assert.Equal(original, Assert.Single(metadata.RecoveredClaims));
            Assert.Equal(original, Assert.Single(retirement.Claims));
            Assert.Equal(snapshot, await test.Case.ScalarAsync<string>("""
                SELECT jsonb_agg(to_jsonb(a) ORDER BY generation)::text
                FROM weft.private_object_cleanup_attempts a WHERE retirement_id=$1
                """, original.Retirement.RetirementId));
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_cleanup_attempts"));
        }
        finally
        {
            relay.DisconnectCaller();
            await relay.DisposeAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static void AssertRun(MaintenanceCommandProcessResponse result, string physical, int deleted, int missing, long generation)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Run", result.Kind);
        Assert.Null(result.Error);
        Assert.Equal("Finished", result.Outcome);
        Assert.Null(result.Failure);
        Assert.Equal(1, result.ClaimedCount);
        Assert.Equal(deleted, result.PhysicalDeleted);
        Assert.Equal(missing, result.PhysicalMissing);
        Assert.Equal(deleted, result.AcknowledgedDeleted);
        Assert.Equal(missing, result.AcknowledgedMissing);
        Assert.Equal(generation, Assert.Single(result.Generations!));
        Assert.Equal(physical, Assert.Single(result.Physical!));
        Assert.Contains(Assert.Single(result.Acknowledgements!), new[] { "Applied", "AlreadyApplied", "Recovered" });
    }

    private sealed class RecordingRetirement(IPrivateObjectRetirementStore actual) : IPrivateObjectRetirementStore
    {
        internal List<ObjectCleanupClaim> Claims { get; } = [];
        public Task<PrivateObjectRetirementOutcome> RetireAsync(ObjectCleanupClaim claim, CancellationToken cancellationToken = default)
        {
            Claims.Add(claim);
            return actual.RetireAsync(claim, cancellationToken);
        }
    }

    private sealed class RoutedFinishMetadata(IPrivateObjectMaintenanceMetadata direct,
        IPrivateObjectMaintenanceMetadata routed) : IPrivateObjectMaintenanceMetadata
    {
        internal ObjectCleanupClaim? OriginalClaim { get; private set; }
        internal ObjectCleanupDisposition? ExpectedDisposition { get; private set; }
        internal bool OriginalFinishSettled { get; private set; }
        internal bool OriginalFinishThrew { get; private set; }
        internal int FinishCalls { get; private set; }
        internal List<ObjectCleanupClaim> RecoveredClaims { get; } = [];

        public Task<ObjectCleanupClaimBatch> ClaimAsync(ProjectScope project, Guid instance,
            ObjectCleanupScanCursor? after = null, CancellationToken cancellationToken = default) =>
            direct.ClaimAsync(project, instance, after, cancellationToken);

        public Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("The command must use shared settlement.");

        public async Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
            CancellationToken cancellationToken, CancellationToken settlementCancellationToken)
        {
            Assert.Equal(1, ++FinishCalls);
            OriginalClaim = claim;
            ExpectedDisposition = disposition;
            Assert.True(settlementCancellationToken.CanBeCanceled);
            try { return await routed.FinishAsync(claim, disposition, cancellationToken, settlementCancellationToken); }
            catch { OriginalFinishThrew = true; throw; }
            finally { OriginalFinishSettled = true; }
        }

        public async Task<ObjectCleanupAttempt?> GetAttemptAsync(ProjectScope project, Guid retirementId, long generation,
            CancellationToken cancellationToken = default)
        {
            Assert.True(OriginalFinishSettled);
            Assert.True(OriginalFinishThrew);
            var original = Assert.IsType<ObjectCleanupClaim>(OriginalClaim);
            Assert.Equal(new ProjectScope(original.Retirement.Target.Scope.OrganizationId, original.Retirement.Target.Scope.ProjectId), project);
            Assert.Equal(original.Retirement.RetirementId, retirementId);
            Assert.Equal(original.Generation, generation);
            var attempt = await direct.GetAttemptAsync(project, retirementId, generation, cancellationToken);
            Assert.Equal(original, attempt!.Claim);
            Assert.Equal(ExpectedDisposition, attempt.Disposition);
            Assert.NotNull(attempt.FinishedAtUtc);
            RecoveredClaims.Add(attempt.Claim);
            return attempt;
        }
    }
}
