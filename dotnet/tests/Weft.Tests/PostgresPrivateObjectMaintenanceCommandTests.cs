using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Real PostgreSQL and physical v2 cleanup; explicitly authored aging is not elapsed-time evidence.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed partial class PostgresPrivateObjectMaintenanceCommandTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task OneRunDeletesActualTerminalNativeFailedAndExpiredCandidatesWithoutChangingAnyOrigin()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var native = await test.PublishedNativeAsync();
        var failed = await test.PublishedReportAsync(failed: true);
        var expired = await test.PublishedReportAsync(failed: false);
        var references = new[] { native.NativeObject, failed.Reference, expired.Reference };
        foreach (var reference in references) Assert.True(File.Exists(test.Root.FinalPath(reference)));
        var before = await test.OriginsAsync();
        var pid = await CommandTestSupport.AssertIdleAsync(test.Facts.MaintenanceDatabase, test.Facts.Case.Name);
        var run = await test.Service().RunOnceAsync(test.Facts.Project);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Finished, run.Outcome);
        Assert.Null(run.Failure);
        Assert.Equal(3, run.ClaimedCount);
        Assert.Equal(3, run.PhysicalDeleted);
        Assert.Equal(3, run.AcknowledgedDeleted);
        Assert.Equal(0, run.PhysicalMissing);
        Assert.Equal(1, test.Metadata.ClaimCalls);
        Assert.Equal(3, test.Metadata.Finishes.Count);
        Assert.Equal(before, await test.OriginsAsync());
        Assert.Equal(new[] { ObjectCleanupReason.TerminalNativeStage, ObjectCleanupReason.FailedDerivedCandidate,
            ObjectCleanupReason.ExpiredDerivedCandidate }.Order(), test.Metadata.Claims.Select(x => x.Retirement.Reason).Order());
        foreach (var reference in references)
        {
            Assert.False(File.Exists(test.Root.FinalPath(reference)));
            Assert.True(File.Exists(Path.Combine(PrivateStorageV2Data.ObjectLeaf(test.Root, reference.Id), "object.retired")));
        }
        foreach (var claim in test.Metadata.Claims)
        {
            var attempt = await test.Facts.Maintenance.GetAttemptAsync(test.Facts.Project, claim.Retirement.RetirementId, claim.Generation);
            Assert.NotNull(attempt);
            Assert.Equal(claim, attempt.Claim);
            Assert.Equal(ObjectCleanupDisposition.Deleted, attempt.Disposition);
            Assert.NotNull(attempt.FinishedAtUtc);
        }
        await CommandTestSupport.AssertIdleAsync(test.Facts.MaintenanceDatabase, test.Facts.Case.Name, pid);
        await using var reopened = FileSystemV2PrivateObjectStore.Open(test.Root.Options);
        foreach (var reference in references)
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing,
                () => reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
        var again = await test.Service().RunOnceAsync(test.Facts.Project);
        Assert.Empty(again.Targets);
        Assert.Equal(0, again.PhysicalDeleted);
        Assert.Equal(before, await test.OriginsAsync());
    }

    [PostgresFact]
    public async Task ResumableFinalAndForeignProjectBytesRemainExactAfterRevocationAndReopen()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var resumable = await test.PublishedNativeAsync(terminal: false);
        var foreign = new ProjectScope(test.Facts.Seed.OrganizationB, test.Facts.Seed.ProjectB);
        var (_, foreignDirectory) = PrivateStorageObservationTestSupport.Revision(test.Root, foreign);
        var foreignFile = PrivateStorageObservationTestSupport.File(foreignDirectory, Guid.NewGuid().ToString("N") + ".bin");
        var path = test.Root.FinalPath(resumable.NativeObject);
        var identity = PrivateStorageV2Identity.Read(path);
        var foreignIdentity = PrivateStorageV2Identity.Read(foreignFile);
        var foreignBytes = File.ReadAllBytes(foreignFile);
        await test.Facts.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1",
            test.Facts.Actor.PrincipalId.Value);
        var before = await test.OriginsAsync();
        var result = await test.Service().RunOnceAsync(test.Facts.Project);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Finished, result.Outcome);
        Assert.Empty(result.Targets);
        Assert.Equal(before, await test.OriginsAsync());
        await using var reopened = FileSystemV2PrivateObjectStore.Open(test.Root.Options);
        Assert.Equal(PostgresIngestionTestSupport.SyntheticBody,
            await reopened.ReadVerifiedAsync(resumable.FutureRevision, resumable.NativeObject));
        Assert.Equal(identity, PrivateStorageV2Identity.Read(path));
        Assert.Equal(foreignIdentity, PrivateStorageV2Identity.Read(foreignFile));
        Assert.Equal(foreignBytes, File.ReadAllBytes(foreignFile));
        Assert.False(File.Exists(Path.Combine(PrivateStorageV2Data.ObjectLeaf(test.Root, resumable.NativeObject.Id), "object.retired")));
    }

    [PostgresFact]
    public async Task HiddenNonUtf8AndUnknownSubtreesAreCountedButNeverDeletedOrTreatedAsOwned()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var (_, directory) = PrivateStorageObservationTestSupport.Revision(test.Root, test.Facts.Project);
        var hidden = PrivateStorageObservationTestSupport.File(directory, ".attempt-unregistered.tmp");
        var unknown = PrivateStorageObservationTestSupport.Directory(Path.Combine(directory, "unknown"));
        var descendant = PrivateStorageObservationTestSupport.File(unknown, "retained-private-content");
        var hiddenBytes = File.ReadAllBytes(hidden);
        var hiddenIdentity = PrivateStorageV2Identity.Read(hidden);
        var descendantBytes = File.ReadAllBytes(descendant);
        var descendantIdentity = PrivateStorageV2Identity.Read(descendant);
        using var raw = new PrivateStorageObservationRawFile(directory, [0xff, 0x61]);
        var rawBytes = CommandTestSupport.ReadOwnedRawFile(directory, [0xff, 0x61]);
        var page = await test.Physical.ObserveAsync(test.Facts.Project);
        var result = await test.Service().RunOnceAsync(test.Facts.Project);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Finished, result.Outcome);
        Assert.Empty(result.Targets);
        Assert.Equal(page.ExaminedCount, result.Observations.FilesystemEntriesExamined);
        Assert.False(result.Observations.FilesystemTraversalComplete);
        Assert.True(result.Observations.OwnershipUnprovedEntriesObserved >= 3);
        Assert.True(File.Exists(hidden));
        Assert.True(File.Exists(descendant));
        Assert.Equal(hiddenBytes, File.ReadAllBytes(hidden));
        Assert.Equal(hiddenIdentity, PrivateStorageV2Identity.Read(hidden));
        Assert.Equal(descendantBytes, File.ReadAllBytes(descendant));
        Assert.Equal(descendantIdentity, PrivateStorageV2Identity.Read(descendant));
        Assert.Equal(rawBytes, CommandTestSupport.ReadOwnedRawFile(directory, [0xff, 0x61]));
        var after = await test.Physical.ObserveAsync(test.Facts.Project);
        var retainedRaw = Assert.Single(after.Entries, entry => entry.CopyRawName().AsSpan().SequenceEqual(new byte[] { 0xff, 0x61 }));
        Assert.Equal(raw.Identity, retainedRaw.Facts!.EntryIdentity);
    }

    [PostgresFact]
    public async Task ARunStopsAtOneHundredObservedEntriesAndSixteenRealClaimsWithExplicitContinuation()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var expected = new HashSet<PrivateObjectId>();
        for (var i = 0; i < 17; i++) expected.Add((await test.Facts.CreateRejectedNativeAsync()).NativeObject.Id);
        var (_, directory) = PrivateStorageObservationTestSupport.Revision(test.Root, test.Facts.Project);
        for (var i = 0; i < 105; i++) PrivateStorageObservationTestSupport.File(directory, ".unregistered-" + i);
        var before = PrivateStorageObservationTestSupport.Snapshot(test.Root.RootPath);
        var origins = await test.OriginsAsync();
        var first = await test.Service().RunOnceAsync(test.Facts.Project);
        Assert.Equal(16, first.ClaimedCount);
        Assert.Equal(16, first.PhysicalMissing);
        Assert.Equal(16, first.AcknowledgedMissing);
        Assert.Equal(100, first.Observations.FilesystemEntriesExamined);
        Assert.False(first.Observations.FilesystemTraversalComplete);
        Assert.NotNull(first.NextCleanupCursor);
        Assert.Equal(1, test.Metadata.ClaimCalls);
        var second = await test.Service().RunOnceAsync(test.Facts.Project, first.NextCleanupCursor, first.Observations.NextStatusCursor);
        Assert.Equal(1, second.ClaimedCount);
        Assert.Equal(1, second.AcknowledgedMissing);
        Assert.Equal(expected.OrderBy(x => x.ObjectId), test.Metadata.Claims.Select(x => x.Retirement.Target.Object).OrderBy(x => x.ObjectId));
        Assert.Equal(origins, await test.OriginsAsync());
        // Only new permanent control fences may appear. All unknown payloads,
        // including entries beyond the observed prefix, retain bytes and inode.
        var after = PrivateStorageObservationTestSupport.Snapshot(test.Root.RootPath);
        foreach (var (name, old) in before)
        {
            Assert.Equal(old.Identity, after[name].Identity);
            Assert.Equal(old.Bytes, after[name].Bytes);
            Assert.Equal(old.Mode, after[name].Mode);
        }
    }

    [PostgresFact]
    public async Task CallerCancellationAfterActualUnlinkRetainsDeletedButNeverFabricatesItsAcknowledgement()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var facts = await test.PublishedNativeAsync();
        using var cancellation = new CancellationTokenSource();
        test.Retirement.After = (_, outcome, _) =>
        {
            Assert.Equal(PrivateObjectRetirementOutcome.Deleted, outcome);
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        var first = await test.Service().RunOnceAsync(test.Facts.Project, cancellationToken: cancellation.Token);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Canceled, first.Outcome);
        Assert.Equal(1, first.PhysicalDeleted);
        Assert.Equal(0, first.AcknowledgedDeleted);
        Assert.Empty(test.Metadata.Finishes);
        var claim = Assert.Single(test.Metadata.Claims);
        Assert.Null((await test.Facts.Maintenance.GetAttemptAsync(test.Facts.Project, claim.Retirement.RetirementId, 1))!.Disposition);
        Assert.False(File.Exists(test.Root.FinalPath(facts.NativeObject)));
        await test.Facts.SyntheticAgeCleanupAsync(claim, TimeSpan.FromMinutes(2));
        test.Retirement.After = null;
        var next = await test.Service().RunOnceAsync(test.Facts.Project);
        Assert.Equal(1, next.PhysicalMissing);
        Assert.Equal(1, next.AcknowledgedMissing);
        Assert.Equal(2, Assert.Single(next.Targets).Generation);
        Assert.Equal(ObjectCleanupDisposition.Missing, Assert.Single(test.Metadata.Finishes).Disposition);
    }

    [PostgresFact]
    public async Task AnActuallyUnsafeRootStopsBeforeClaimAndRestorationAllowsTheSamePoolToRecover()
    {
        await using var test = await CommandTestSupport.CreateAsync(fixture);
        var facts = await test.PublishedNativeAsync();
        var before = await test.OriginsAsync();
        var path = test.Root.FinalPath(facts.NativeObject);
        var identity = PrivateStorageV2Identity.Read(path);
        var bytes = File.ReadAllBytes(path);
        var markerMode = File.GetUnixFileMode(test.Root.MarkerPath);
        File.SetUnixFileMode(test.Root.MarkerPath, PrivateStorageTestRoot.AttemptMode);
        try
        {
            var run = await test.Service().RunOnceAsync(test.Facts.Project);
            Assert.Equal(PrivateObjectMaintenanceRunOutcome.Failed, run.Outcome);
            Assert.Equal(PrivateObjectMaintenanceRunFailure.UnsafeStorage, run.Failure);
            Assert.False(run.ClaimAttempted);
            Assert.Null(run.ClaimedCount);
            Assert.Null(run.Observations.FilesystemEntriesExamined);
            Assert.Empty(run.Targets);
            Assert.Empty(test.Retirement.Returns);
            Assert.Equal(before, await test.OriginsAsync());
            Assert.Equal(identity, PrivateStorageV2Identity.Read(path));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { File.SetUnixFileMode(test.Root.MarkerPath, markerMode); }
        await CommandTestSupport.AssertIdleAsync(test.Facts.MaintenanceDatabase, test.Facts.Case.Name);
        Assert.Equal(1, (await test.Service().RunOnceAsync(test.Facts.Project)).AcknowledgedDeleted);
    }
}
