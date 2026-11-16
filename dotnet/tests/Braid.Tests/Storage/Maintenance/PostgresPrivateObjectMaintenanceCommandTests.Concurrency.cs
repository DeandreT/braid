using Braid.Application;
using Braid.Domain;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

public sealed partial class PostgresPrivateObjectMaintenanceCommandTests
{
    [PostgresFact]
    public async Task ACommittedClaimWaitsOutsideEveryTransactionAndConcurrentCommandCannotShareItsLease()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var facts = await test.PublishedNativeAsync();
        var pid = await CommandTestSupport.AssertIdleAsync(test.Facts.MaintenanceDatabase, test.Facts.Case.Name);
        var gate = new RevisionIngestionGate();
        test.Retirement.Before = (_, token) => gate.HoldAsync(token);
        var observations = new CheckedStorageObservation(test.Physical,
            () => CommandTestSupport.AssertIdleAsync(test.Facts.MaintenanceDatabase, test.Facts.Case.Name, pid));
        var service = new PrivateObjectMaintenanceService(test.Metadata, test.Facts.Maintenance, test.Facts.Maintenance,
            observations, test.Retirement, Guid.NewGuid());
        var first = service.RunOnceAsync(test.Facts.Project);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(test.Root.FinalPath(facts.NativeObject)));
            var claim = Assert.Single(test.Metadata.Claims);
            Assert.Equal(claim, (await test.Facts.Maintenance.GetAttemptAsync(test.Facts.Project,
                claim.Retirement.RetirementId, claim.Generation))!.Claim);
            await CommandTestSupport.AssertIdleAsync(test.Facts.MaintenanceDatabase, test.Facts.Case.Name, pid);
            var second = await test.Service().RunOnceAsync(test.Facts.Project);
            Assert.Empty(second.Targets);
            Assert.Empty(test.Retirement.Returns);
            Assert.Empty(test.Metadata.Finishes);
            gate.Release();
            var finished = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, finished.PhysicalDeleted);
            Assert.Equal(1, finished.AcknowledgedDeleted);
            Assert.Equal(1, observations.Calls);
            Assert.Single(test.Metadata.Finishes);
            await CommandTestSupport.AssertIdleAsync(test.Facts.MaintenanceDatabase, test.Facts.Case.Name, pid);
        }
        finally { gate.Release(); await CommandTestSupport.DrainAsync(first); }
    }

    [PostgresFact]
    public async Task AnActuallyHeldSharedReadGateReturnsBusyWithoutFinishThenAReclaimedLeaseDeletesOnce()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var facts = await test.PublishedNativeAsync();
        var gate = new RevisionIngestionGate();
        test.Observation = async (observation, token) =>
        {
            if (observation.Phase == V2StoragePhase.ReadGateAcquired) await gate.HoldAsync(token);
        };
        var reader = test.Physical.ReadVerifiedAsync(facts.FutureRevision, facts.NativeObject);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var run = await test.Service().RunOnceAsync(test.Facts.Project);
            var row = Assert.Single(run.Targets);
            Assert.Equal(PrivateObjectMaintenancePhysicalObservation.Busy, row.Physical);
            Assert.Equal(PrivateObjectMaintenanceAcknowledgement.NotAttempted, row.Acknowledgement);
            Assert.Empty(test.Metadata.Finishes);
            var claim = Assert.Single(test.Metadata.Claims);
            Assert.Null((await test.Facts.Maintenance.GetAttemptAsync(test.Facts.Project,
                claim.Retirement.RetirementId, 1))!.Disposition);
            Assert.False(File.Exists(Path.Combine(PrivateStorageV2Data.ObjectLeaf(test.Root, facts.NativeObject.Id), "object.retired")));
            gate.Release();
            Assert.Equal(PostgresIngestionTestSupport.SyntheticBody, await reader.WaitAsync(TimeSpan.FromSeconds(5)));
            test.Observation = null;
            // Authored expiry+retry setup does not change the fixed60s/30s policy.
            await test.Facts.SyntheticAgeCleanupAsync(claim, TimeSpan.FromMinutes(2));
            var recovered = await test.Service().RunOnceAsync(test.Facts.Project);
            Assert.Equal(2, Assert.Single(recovered.Targets).Generation);
            Assert.Equal(1, recovered.PhysicalDeleted);
            Assert.Equal(1, recovered.AcknowledgedDeleted);
        }
        finally { gate.Release(); await CommandTestSupport.DrainAsync(reader); }
    }

    [PostgresFact]
    public async Task ActualUnlinkBeforeAnAuthoredLeaseExpiryCannotFinishOrResurrectItsSuccessor()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var facts = await test.PublishedNativeAsync();
        var gate = new RevisionIngestionGate();
        test.Retirement.After = async (_, outcome, token) =>
        {
            Assert.Equal(PrivateObjectRetirementOutcome.Deleted, outcome);
            await gate.HoldAsync(token);
        };
        var first = test.Service().RunOnceAsync(test.Facts.Project);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var original = Assert.Single(test.Metadata.Claims);
            Assert.False(File.Exists(test.Root.FinalPath(facts.NativeObject)));
            await CommandTestSupport.AssertIdleAsync(test.Facts.MaintenanceDatabase, test.Facts.Case.Name);
            await test.Facts.SyntheticAgeCleanupAsync(original, TimeSpan.FromMinutes(2));
            var secondPort = new CommandMetadata(test.Facts.Maintenance);
            var successor = await test.Service(new CommandRetirement(test.Physical), secondPort).RunOnceAsync(test.Facts.Project);
            var next = Assert.Single(secondPort.Claims);
            Assert.Equal(original.Retirement.RetirementId, next.Retirement.RetirementId);
            Assert.Equal(2, next.Generation);
            Assert.NotEqual(original.AttemptId, next.AttemptId);
            Assert.NotEqual(original.LeaseToken, next.LeaseToken);
            Assert.Equal(1, successor.PhysicalMissing);
            Assert.Equal(1, successor.AcknowledgedMissing);
            var retained = await test.Facts.CleanupSnapshotAsync(original.Retirement.RetirementId);
            gate.Release();
            var stale = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, stale.PhysicalDeleted);
            Assert.Equal(0, stale.AcknowledgedDeleted);
            Assert.Equal(PrivateObjectMaintenanceAcknowledgement.ClaimLost, Assert.Single(stale.Targets).Acknowledgement);
            Assert.Equal(retained, await test.Facts.CleanupSnapshotAsync(original.Retirement.RetirementId));
            Assert.False(File.Exists(test.Root.FinalPath(facts.NativeObject)));
            Assert.Equal(PrivateObjectRetirementOutcome.Missing, await test.Physical.RetireAsync(original));
            Assert.Equal(retained, await test.Facts.CleanupSnapshotAsync(original.Retirement.RetirementId));
        }
        finally { gate.Release(); await CommandTestSupport.DrainAsync(first); }
    }

    [PostgresFact]
    public async Task PublicationAndRetirementBothOrdersUseTheRealExclusiveGateAndPermanentFence()
    {
        foreach (var publisherFirst in new[] { true, false })
        {
            await using var test = await CommandTestSupport.CreateAsync(fixture);
            var facts = await test.Facts.ReserveNativeAsync();
            var owner = await test.Facts.ClaimNativeAsync(facts);
            var gate = new RevisionIngestionGate();
            var phase = publisherFirst ? V2StoragePhase.FinalGateAcquired : V2StoragePhase.PayloadPrepared;
            test.Observation = async (observation, token) =>
            {
                if (observation.Phase == phase && observation.Publication is not null) await gate.HoldAsync(token);
            };
            var publication = test.Physical.PublishCreateOnlyAsync(PrivatePublicationOwner.ForIngestion(test.Facts.Actor, owner),
                facts.FutureRevision, facts.NativeObject, new MemoryStream(PostgresIngestionTestSupport.SyntheticBody));
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(IngestionStoreOutcome.Applied, (await test.Facts.Ingestions.CompleteAttemptAsync(test.Facts.Actor,
                    owner, IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit))).Outcome);
                await test.Facts.SyntheticAgeNativeAsync(facts, TimeSpan.FromHours(25));
                var run = await test.Service().RunOnceAsync(test.Facts.Project);
                var row = Assert.Single(run.Targets);
                if (publisherFirst)
                {
                    Assert.Equal(PrivateObjectMaintenancePhysicalObservation.Busy, row.Physical);
                    Assert.Empty(test.Metadata.Finishes);
                    gate.Release();
                    Assert.Equal(PrivateObjectPublishResult.Created, await publication.WaitAsync(TimeSpan.FromSeconds(5)));
                    await test.Facts.SyntheticAgeCleanupAsync(Assert.Single(test.Metadata.Claims), TimeSpan.FromMinutes(2));
                    test.Observation = null;
                    var retry = await test.Service().RunOnceAsync(test.Facts.Project);
                    Assert.Equal(1, retry.PhysicalDeleted);
                    Assert.Equal(1, retry.AcknowledgedDeleted);
                }
                else
                {
                    Assert.Equal(PrivateObjectMaintenancePhysicalObservation.Missing, row.Physical);
                    Assert.Equal(1, run.AcknowledgedMissing);
                    Assert.True(File.Exists(Path.Combine(PrivateStorageV2Data.ObjectLeaf(test.Root, facts.NativeObject.Id), "object.retired")));
                    gate.Release();
                    await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => publication);
                }
                Assert.False(File.Exists(test.Root.FinalPath(facts.NativeObject)));
            }
            finally
            {
                gate.Release();
                try { await publication.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (PrivateObjectStorageException) when (!publisherFirst) { }
            }
        }
    }

    [PostgresFact]
    public async Task EightActualUnsafeRetirementsExhaustWithoutUnlinkingTheWrongModeInode()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var facts = await test.PublishedNativeAsync();
        var path = test.Root.FinalPath(facts.NativeObject);
        var identity = PrivateStorageV2Identity.Read(path);
        var bytes = File.ReadAllBytes(path);
        File.SetUnixFileMode(path, PrivateStorageTestRoot.AttemptMode); // Deliberate owned unsafe physical file.
        ObjectCleanupClaim? previous = null;
        for (var generation = 1; generation <= 8; generation++)
        {
            if (previous is not null) await test.Facts.SyntheticAgeCleanupAsync(previous, TimeSpan.FromMinutes(2));
            var run = await test.Service().RunOnceAsync(test.Facts.Project);
            var row = Assert.Single(run.Targets);
            Assert.Equal(generation, row.Generation);
            Assert.Equal(PrivateObjectMaintenancePhysicalObservation.UnsafeStorage, row.Physical);
            Assert.Equal(ObjectCleanupDisposition.UnsafeStorage, row.AcknowledgedDisposition);
            Assert.Equal(generation == 8 ? 1 : 0, run.AcknowledgedExhausted);
            Assert.Equal(generation < 8 ? 1 : 0, run.AcknowledgedRetryable);
            previous = test.Metadata.Claims[^1];
            Assert.Equal(identity, PrivateStorageV2Identity.Read(path));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        var before = await test.Facts.CleanupSnapshotAsync(previous!.Retirement.RetirementId);
        var final = await test.Service().RunOnceAsync(test.Facts.Project);
        Assert.Empty(final.Targets);
        Assert.Equal(1, final.ExhaustedRetirementsObserved);
        Assert.Equal(before, await test.Facts.CleanupSnapshotAsync(previous.Retirement.RetirementId));
        Assert.Equal(8, test.Metadata.Finishes.Count);
    }

    [PostgresFact]
    public async Task ActualOriginLockCancellationLeavesNoClaimAndTheSamePoolCanPerformTheNextRun()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var facts = await test.PublishedNativeAsync();
        var before = await test.OriginsAsync();
        var pid = await CommandTestSupport.AssertIdleAsync(test.Facts.MaintenanceDatabase, test.Facts.Case.Name);
        await using var owner = await test.Facts.OpenOwnerAsync();
        await using var held = await owner.BeginTransactionAsync();
        await using (var command = new Npgsql.NpgsqlCommand("SELECT operation_id FROM braid.revision_ingestions WHERE operation_id=$1 FOR UPDATE", owner, held))
        {
            command.Parameters.AddWithValue(facts.OperationId);
            Assert.Equal(facts.OperationId, await command.ExecuteScalarAsync());
        }
        using var cancellation = new CancellationTokenSource();
        await using var observer = await test.Facts.OpenBlockingObserverAsync(pid, owner.ProcessID);
        var pending = test.Service().RunOnceAsync(test.Facts.Project, cancellationToken: cancellation.Token);
        try
        {
            await observer.WaitForBlockedAsync(pending);
            cancellation.Cancel();
            var run = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PrivateObjectMaintenanceRunOutcome.Canceled, run.Outcome);
            Assert.True(run.ClaimAttempted);
            Assert.Null(run.ClaimedCount);
            Assert.Empty(run.Targets);
            Assert.Empty(test.Retirement.Returns);
            Assert.Empty(test.Metadata.Finishes);
            Assert.Equal(before, await test.OriginsAsync());
            Assert.Equal(new long[] { 0, 0, 1 }, await test.Facts.CountsAsync());
        }
        finally { cancellation.Cancel(); await held.RollbackAsync(); await CommandTestSupport.DrainAsync(pending); }
        await CommandTestSupport.AssertIdleAsync(test.Facts.MaintenanceDatabase, test.Facts.Case.Name, pid);
        var recovered = await test.Service().RunOnceAsync(test.Facts.Project);
        Assert.Equal(1, recovered.PhysicalDeleted);
        Assert.Equal(1, recovered.AcknowledgedDeleted);
        Assert.Equal(before, await test.OriginsAsync());
    }

    private sealed class CheckedStorageObservation(IPrivateObjectStorageObserver actual, Func<Task<int>> check)
        : IPrivateObjectStorageObserver
    {
        internal int Calls { get; private set; }
        public async Task<PrivateStorageObservationPage> ObserveAsync(ProjectScope project, CancellationToken cancellationToken = default)
        {
            await check();
            Calls++;
            return await actual.ObserveAsync(project, cancellationToken);
        }
    }
}
