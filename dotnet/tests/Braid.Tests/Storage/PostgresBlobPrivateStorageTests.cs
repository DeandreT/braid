using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Server;
using Braid.Storage.Blob;

namespace Braid.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresAzuriteCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Owned actual PostgreSQL and Azurite";
}

public sealed class PostgresAzuriteFactAttribute : FactAttribute
{
    public PostgresAzuriteFactAttribute()
    {
        if (!PostgresFixture.IsConfigured || !AzuriteFixture.IsConfigured)
            Skip = "Set BRAID_POSTGRES_IMAGE and BRAID_AZURITE_IMAGE to execute actual database and blob emulator gates.";
    }
}

/// <summary>
/// Actual PostgreSQL claims, registrations and maintenance metadata with the Blob store. Aging is
/// owner-authored fixture time, not elapsed grace; source/C1 metadata is seeded, with no worker claim.
/// </summary>
[Collection(PostgresAzuriteCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresBlobPrivateStorageTests(PostgresFixture fixture, AzuriteFixture azurite)
{
    [PostgresAzuriteFact]
    public async Task ActualOwnersCommitBlobBindingsThatPostgreSqlAdmitsAndMatchesToObservedSlots()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var native = await test.ReserveNativeAsync();
        var nativeOwner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(native));
        var reportClaim = await test.ReserveReportAsync();
        var reportBytes = "opaque derived storage fixture; not a verified report"u8.ToArray();
        var reportReference = new PrivateObjectReference(reportClaim.CandidateObjectId, Sha256Digest.FromBytes(reportBytes), reportBytes.LongLength);
        var targets = new (PrivateObjectReference Reference, byte[] Bytes, PrivatePublicationOwner Owner, IPrivateObjectPublicationRegistry Registry)[]
        {
            (native.NativeObject, PostgresIngestionTestSupport.SyntheticBody, nativeOwner, test.NativePublications),
            (reportReference, reportBytes, PrivatePublicationOwner.ForReport(test.Actor, reportClaim), test.ReportPublications)
        };
        foreach (var (reference, bytes, owner, registry) in targets)
        {
            var container = await azurite.CreateContainerAsync();
            using var input = new PrivateStorageInput(bytes, chunkSize: 17);
            PrivatePublicationRecord? committed = null;
            BlobPrivateObjectStore? store = null;
            var observer = new BlobObserver(async (observation, token) =>
            {
                if (observation.Phase is not (BlobStoragePhase.ReservationAcknowledged or BlobStoragePhase.BindingAcknowledged)) return;
                Assert.Equal(0, input.ReadCalls);
                var key = Assert.IsType<PrivatePublicationKey>(observation.Publication);
                var persisted = await registry.GetAsync(owner, PrivatePublicationStoreKind.AzureBlob, key.RootId, key.Object, key.TempId, token);
                Assert.NotNull(persisted);
                Assert.True(persisted.CanWrite);
                committed = persisted.Record;
                if (observation.Phase == BlobStoragePhase.ReservationAcknowledged)
                {
                    Assert.Null(committed.Binding);
                    return;
                }
                // The binding PostgreSQL admitted is exactly the identity maintenance later observes.
                var page = await store!.ObserveAsync(test.Project, token);
                var slot = Assert.Single(page.Entries, entry => entry.Kind == PrivateStorageEntryKind.CandidateSlot).Slot!;
                Assert.Equal(key.TempId, slot.TempId);
                Assert.Equal(committed.Binding!.ActualIdentity, slot.ActualIdentity);
                var matched = await test.Maintenance.MatchAsync(new(test.Project, [slot]), token);
                Assert.Equal(PublicationObservationOutcome.ExactlyOne, Assert.Single(matched.Results).Outcome);
            });
            store = await BlobPrivateObjectStore.InitializeAsync(azurite.Options(container), azurite.Client(container), () => registry, observer);
            await using (store)
            {
                Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, input));
                Assert.NotNull(committed);
                // The Blob store registers its own kind, and PostgreSQL stamps the Blob label for it.
                Assert.Equal(PrivatePublicationStoreKind.AzureBlob, committed.Reservation.StoreKind);
                Assert.Equal(PostgresPrivatePublicationStoreKindTests.Expected(PrivatePublicationStoreKind.AzureBlob),
                    await PostgresPrivatePublicationStoreKindTests.LabelAsync(test, committed.Reservation.PublicationId));
                var key = committed.Reservation.Key;
                Assert.Equal(committed, (await registry.GetAsync(owner, PrivatePublicationStoreKind.AzureBlob, key.RootId, key.Object, key.TempId))!.Record);
                var page = await store.ObserveAsync(test.Project);
                var final = Assert.Single(page.Entries, entry => entry.Kind == PrivateStorageEntryKind.CandidateSlot).Slot!;
                Assert.Equal(reference.Id, final.ObjectId);
                Assert.Equal(committed.Binding!.ActualIdentity, final.ActualIdentity);
                var matched = await test.Maintenance.MatchAsync(new(test.Project, [final]));
                Assert.Equal(PublicationObservationOutcome.ExactlyOne, Assert.Single(matched.Results).Outcome);
            }
            await using var readOnly = await BlobPrivateObjectStore.OpenAsync(azurite.Options(container), azurite.Client(container), null, null);
            Assert.Equal(bytes, await readOnly.ReadVerifiedAsync(reference.Id.Scope, reference));
        }
        Assert.Equal(new long[] { 0, 0, 2 }, await test.CountsAsync());
    }

    [PostgresAzuriteFact]
    public async Task OneMaintenanceRunRetiresActualTerminalNativeFailedAndExpiredBlobCandidates()
    {
        await using var facts = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var container = await azurite.CreateContainerAsync();
        var registry = new LazyPrivateObjectPublicationRegistry(() => facts.NativePublications, () => facts.ReportPublications);
        await using var store = await BlobPrivateObjectStore.InitializeAsync(azurite.Options(container), azurite.Client(container), () => registry, null);
        var references = new List<PrivateObjectReference>();

        var native = await facts.ReserveNativeAsync();
        var nativeClaim = await facts.ClaimNativeAsync(native);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(PrivatePublicationOwner.ForIngestion(facts.Actor, nativeClaim),
            native.FutureRevision, native.NativeObject, new MemoryStream(PostgresIngestionTestSupport.SyntheticBody)));
        Assert.Equal(IngestionPhase.Rejected, (await facts.Ingestions.CompleteAttemptAsync(facts.Actor, nativeClaim,
            IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit))).Receipt!.Phase);
        await facts.SyntheticAgeNativeAsync(native, TimeSpan.FromHours(25));
        references.Add(native.NativeObject);
        foreach (var failed in new[] { true, false })
        {
            var claim = await facts.ReserveReportAsync(facts.ReportKey(Guid.NewGuid().ToString("N")));
            var bytes = "Opaque failed/pending candidate, not a usable encoded report."u8.ToArray();
            var reference = new PrivateObjectReference(claim.CandidateObjectId, Sha256Digest.FromBytes(bytes), bytes.LongLength);
            Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(
                PrivatePublicationOwner.ForReport(facts.Actor, claim), claim.Key.Scope, reference, new MemoryStream(bytes)));
            if (failed)
                Assert.Equal(RevisionReportStoreOutcome.Applied, (await facts.Reports.FinishAsync(facts.Actor, claim,
                    RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.BackendFailure))).Outcome);
            await facts.SyntheticAgeReportAsync(claim, TimeSpan.FromHours(25));
            references.Add(reference);
        }

        var service = new PrivateObjectMaintenanceService(facts.Maintenance, facts.Maintenance, facts.Maintenance, store, store, Guid.NewGuid());
        var run = await service.RunOnceAsync(facts.Project);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Finished, run.Outcome);
        Assert.Null(run.Failure);
        Assert.Equal(3, run.ClaimedCount);
        Assert.Equal(3, run.PhysicalDeleted);
        Assert.Equal(3, run.AcknowledgedDeleted);
        Assert.Equal(3, run.Observations.BoundPublicationMatchesObserved);
        Assert.Equal(0, run.Observations.OwnershipUnprovedEntriesObserved);
        Assert.True(run.Observations.FilesystemTraversalComplete);
        foreach (var reference in references)
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        var again = await service.RunOnceAsync(facts.Project);
        Assert.Empty(again.Targets);
        // Retirement fences are not payload entries; only the structural directories remain observed.
        Assert.Equal(0, again.Observations.BoundPublicationMatchesObserved);
        Assert.Equal(0, again.Observations.OwnershipUnprovedEntriesObserved);
        Assert.Equal(new long[] { 3, 3, 3 }, await facts.CountsAsync());
    }

    [PostgresAzuriteFact]
    public async Task AMaintenanceRunOverBothKindsRetiresItsOwnObjectAndRefusesTheOtherKindsTemporary()
    {
        await using var facts = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var container = await azurite.CreateContainerAsync();
        var registry = new LazyPrivateObjectPublicationRegistry(() => facts.NativePublications, () => facts.ReportPublications);
        await using var store = await BlobPrivateObjectStore.InitializeAsync(azurite.Options(container), azurite.Client(container), () => registry, null);

        var native = await facts.ReserveNativeAsync();
        var nativeClaim = await facts.ClaimNativeAsync(native);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(PrivatePublicationOwner.ForIngestion(facts.Actor, nativeClaim),
            native.FutureRevision, native.NativeObject, new MemoryStream(PostgresIngestionTestSupport.SyntheticBody)));
        Assert.Equal(IngestionPhase.Rejected, (await facts.Ingestions.CompleteAttemptAsync(facts.Actor, nativeClaim,
            IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit))).Receipt!.Phase);
        await facts.SyntheticAgeNativeAsync(native, TimeSpan.FromHours(25));

        // An abandoned temporary that the filesystem store registered in the same project.
        var other = await facts.ReserveNativeAsync();
        var otherOwner = PrivatePublicationOwner.ForIngestion(facts.Actor, await facts.ClaimNativeAsync(other));
        var root = Guid.NewGuid();
        var reserved = await facts.NativePublications.ReserveAsync(otherOwner, PrivatePublicationStoreKind.FileSystem, root,
            other.NativeObject.Id, Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
        var bound = await facts.NativePublications.BindAsync(reserved.Snapshot!.Record.Reservation,
            PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root), PostgresPrivateObjectMaintenanceTestSupport.BoundDigest);
        var foreign = await facts.SyntheticAgePublicationAsync(bound.Snapshot!.Record.Reservation, TimeSpan.FromHours(25));

        var service = new PrivateObjectMaintenanceService(facts.Maintenance, facts.Maintenance, facts.Maintenance, store, store, Guid.NewGuid());
        var run = await service.RunOnceAsync(facts.Project);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Finished, run.Outcome);
        Assert.Null(run.Failure);
        Assert.Equal(2, run.ClaimedCount);
        var own = run.Targets[0];
        Assert.Equal((CleanupTargetKind.FinalObject, PrivateObjectMaintenancePhysicalObservation.Deleted, ObjectCleanupDisposition.Deleted),
            (own.TargetKind, own.Physical, own.AcknowledgedDisposition));
        // The other kind's temporary is never fenced or acknowledged as removed by this store.
        var refused = run.Targets[1];
        Assert.Equal((CleanupTargetKind.TrackedTemporary, PrivateObjectMaintenancePhysicalObservation.UnsafeStorage, ObjectCleanupDisposition.UnsafeStorage),
            (refused.TargetKind, refused.Physical, refused.AcknowledgedDisposition));
        Assert.True(await facts.Case.ScalarAsync<bool>("""
            SELECT r.completed_attempt_id IS NULL AND a.disposition='unsafe_storage' AND NOT a.success
            FROM braid.private_object_retirements r JOIN braid.private_object_cleanup_attempts a
              ON a.organization_id=r.organization_id AND a.project_id=r.project_id AND a.retirement_id=r.retirement_id
            WHERE r.publication_id=$1
            """, foreign.PublicationId));
        Assert.Equal(PostgresPrivatePublicationStoreKindTests.Expected(PrivatePublicationStoreKind.FileSystem),
            await PostgresPrivatePublicationStoreKindTests.LabelAsync(facts, foreign.PublicationId));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadVerifiedAsync(native.FutureRevision, native.NativeObject));
        Assert.Equal(new long[] { 2, 2, 2 }, await facts.CountsAsync());
    }

    [PostgresAzuriteFact]
    public async Task TheMaintenanceToolRunsAgainstTheExplicitlySelectedBlobStore()
    {
        await using var facts = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var container = await azurite.CreateContainerAsync();
        var registry = new LazyPrivateObjectPublicationRegistry(() => facts.NativePublications, () => facts.ReportPublications);
        var native = await facts.ReserveNativeAsync();
        var claim = await facts.ClaimNativeAsync(native);
        await using (var store = await BlobPrivateObjectStore.InitializeAsync(azurite.Options(container), azurite.Client(container), () => registry, null))
            Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(PrivatePublicationOwner.ForIngestion(facts.Actor, claim),
                native.FutureRevision, native.NativeObject, new MemoryStream(PostgresIngestionTestSupport.SyntheticBody)));
        Assert.Equal(IngestionPhase.Rejected, (await facts.Ingestions.CompleteAttemptAsync(facts.Actor, claim,
            IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit))).Receipt!.Phase);
        await facts.SyntheticAgeNativeAsync(native, TimeSpan.FromHours(25));
        var environment = new Dictionary<string, string>
        {
            ["BRAID_MAINTENANCE_CONNECTION_STRING"] = facts.Case.MaintenanceConnectionString,
            ["BRAID_PRIVATE_STORAGE_KIND"] = "azure-blob",
            ["BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT"] = azurite.ServiceUri.AbsoluteUri,
            ["BRAID_PRIVATE_STORAGE_BLOB_CONTAINER"] = container.Name,
            ["BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL"] = "emulator"
        };
        var project = new[] { "run-once", facts.Project.OrganizationId.ToString("D"), facts.Project.ProjectId.ToString("D") };

        // Filesystem operands are refused under the Blob selection, and an unknown kind is invalid configuration.
        var operands = await InvokeAsync([.. project, "/var/lib/braid/private", "/srv/braid"], environment);
        Assert.Equal(2, operands.ExitCode);
        Assert.StartsWith("Usage" + Environment.NewLine, operands.Error, StringComparison.Ordinal);
        var unknown = await InvokeAsync(project, new Dictionary<string, string>(environment) { ["BRAID_PRIVATE_STORAGE_KIND"] = "blob" });
        Assert.Equal((2, "InvalidConfiguration" + Environment.NewLine), (unknown.ExitCode, unknown.Error));
        var incomplete = new Dictionary<string, string>(environment);
        incomplete.Remove("BRAID_PRIVATE_STORAGE_BLOB_CONTAINER");
        var missing = await InvokeAsync(project, incomplete);
        Assert.Equal((2, "InvalidConfiguration" + Environment.NewLine), (missing.ExitCode, missing.Error));

        var result = await InvokeAsync(project, environment);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("finished", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("physicalDeleted").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("acknowledgedDeleted").GetInt32());
        Assert.DoesNotContain(container.Name, result.Output, StringComparison.Ordinal);
        await using var reopened = await BlobPrivateObjectStore.OpenAsync(azurite.Options(container), azurite.Client(container), null, null);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing,
            () => reopened.ReadVerifiedAsync(native.FutureRevision, native.NativeObject));
    }

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(string[] arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "MaintenanceTool", "Braid.MaintenanceTool.dll");
        Assert.True(File.Exists(dll), "The ordinary maintenance tool must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("BRAID_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The owned maintenance tool did not start.");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
