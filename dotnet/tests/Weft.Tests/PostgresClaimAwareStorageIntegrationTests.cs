using System.Net;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Server;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresClaimAwareStorageIntegrationTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task ActualC2AndProjectionServicesPreserveOriginalClaimsAndReleaseMaxOnePoolsThroughPayloadAndWorkerWaits()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture, activateLegacy: true);
        var bytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
        var pids = await test.AssertPoolsUnboundAsync();
        var nativeBody = new RevisionIngestionGate();
        var nativeWorker = new RevisionIngestionGate();
        var reportBody = new RevisionIngestionGate();
        test.Native.Launcher.NextProtocolGate = nativeWorker;
        test.Storage.BeforeFirstPayloadReadAsync = (publication, token) =>
            publication.Reference.Id.Class == PrivateObjectClass.NativeArtifact ? nativeBody.HoldAsync(token) : reportBody.HoldAsync(token);
        var reservationChecks = 0;
        var bindingChecks = 0;
        test.ObserveAsync = async (observation, token) =>
        {
            if (observation.Phase is not (V2StoragePhase.ReservationAcknowledged or V2StoragePhase.BindingAcknowledged)) return;
            var key = Assert.IsType<PrivatePublicationKey>(observation.Publication);
            var publication = Assert.Single(test.Storage.Publications, item => item.Reference.Id == key.Object);
            Assert.Equal(0, publication.ReadCalls);
            Assert.Equal(0, publication.BytesRead);
            var port = key.Object.Class == PrivateObjectClass.NativeArtifact ? test.NativeRegistry : test.ReportPublicationRegistry;
            var stored = await port.Actual.GetAsync(publication.Owner, key.RootId, key.Object, key.TempId, token);
            Assert.NotNull(stored);
            Assert.True(stored.CanWrite);
            Assert.Equal(publication.Owner, stored.Record.Reservation.Owner);
            Assert.Equal(key, stored.Record.Reservation.Key);
            var temp = PrivateStorageV2Data.TempPath(test.Root, key);
            if (observation.Phase == V2StoragePhase.ReservationAcknowledged)
            {
                reservationChecks++;
                Assert.Null(stored.Record.Binding);
                Assert.False(File.Exists(temp));
            }
            else
            {
                bindingChecks++;
                Assert.NotNull(stored.Record.Binding);
                Assert.Equal(0, new FileInfo(temp).Length);
                Assert.Equal(PrivateStorageV2Identity.Read(temp), stored.Record.Binding.ActualIdentity.FileIdentity);
                Assert.Equal(PrivateStorageV2Identity.Read(Path.GetDirectoryName(temp)!), stored.Record.Binding.ActualIdentity.DirectoryIdentity);
            }
        };
        using var cancellation = new CancellationTokenSource();
        var key = Guid.NewGuid();
        var native = test.CreateNativeAsync(bytes, key, cancellation.Token);
        Task<RevisionReportEnsureResult>? projection = null;
        try
        {
            await nativeBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var ownedNative = Assert.Single(test.Metadata.Claims);
            var nativePublication = Assert.Single(test.Storage.Publications);
            Assert.Equal(PrivatePublicationOwner.ForIngestion(test.Actor, ownedNative), nativePublication.Owner);
            Assert.Equal(test.Actor.PrincipalId, ownedNative.OriginalActor);
            Assert.Equal(test.Project, ownedNative.Project);
            Assert.Equal(0, nativePublication.ReadCalls);
            Assert.Single(test.NativeRegistry.Bindings);
            Assert.Empty(test.Native.Runner.NativeBodies);
            Assert.Equal(0, test.ReportRegistryResolutions);
            await test.AssertPoolsUnboundAsync(pids);
            nativeBody.Release();
            await nativeWorker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(native.IsCompleted);
            Assert.Equal(PrivateObjectPublishResult.Created, nativePublication.Result);
            Assert.Equal(bytes, File.ReadAllBytes(test.Root.FinalPath(nativePublication.Reference)));
            Assert.Equal(bytes, Assert.Single(test.Native.Runner.NativeBodies));
            await test.AssertPoolsUnboundAsync(pids);
            nativeWorker.Release();
            var created = await native;
            Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
            var receipt = await test.ReceiptAsync(created);
            Assert.Equal(key, receipt.Facts.IdempotencyKey);
            Assert.Equal(ownedNative.OperationId, receipt.Facts.OperationId);
            Assert.Equal(ownedNative.Generation, receipt.CurrentGeneration);
            Assert.Equal(TimeSpan.FromSeconds(120), ownedNative.LeaseExpiresAtUtc - Assert.Single(receipt.Attempts).StartedAtUtc);
            Assert.Equal(bytes, await test.Physical.ReadVerifiedAsync(receipt.Facts.FutureRevision, receipt.Facts.NativeObject));
            var evidence = Assert.Single(receipt.Attempts).Completion!.Evidence!.Facts;
            Assert.True(evidence.IsValid);
            Assert.True(evidence.Provenance.Confirmed);
            Assert.Equal(test.Native.Registration.Profile, evidence.Provenance.BackendProfile);

            projection = test.Reports.EnsureAsync(test.Actor, receipt.Facts.FutureRevision, RevisionReportOperation.Projection,
                cancellationToken: cancellation.Token);
            await reportBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var ownedReport = Assert.Single(test.ReportMetadata.Claims);
            var derived = Assert.Single(test.Storage.Publications, item => item.Reference.Id.Class == PrivateObjectClass.DerivedArtifact);
            Assert.Equal(PrivatePublicationOwner.ForReport(test.Actor, ownedReport), derived.Owner);
            Assert.Equal(TimeSpan.FromSeconds(60), ownedReport.LeaseExpiresAtUtc - ownedReport.StartedAtUtc);
            Assert.Equal(ownedReport.CandidateObjectId, derived.Reference.Id);
            Assert.Equal(0, derived.ReadCalls);
            Assert.Single(test.ReportPublicationRegistry.Bindings);
            Assert.Single(test.ReportBackend.Completions);
            Assert.True(test.ReportBackend.Completions[0].IsUsable);
            await test.AssertPoolsUnboundAsync(pids);
            reportBody.Release();
            var available = Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(await projection));
            Assert.Equal(ownedReport.DerivationId, available.Metadata.DerivationId);
            Assert.Equal(ownedReport.CandidateObjectId, available.Metadata.DerivedObject.Id);
            Assert.Equal(PrivateObjectPublishResult.Created, derived.Result);
            Assert.Equal(available.Metadata.DerivedObject, derived.Reference);
            Assert.Equal(PrivateStorageV2Identity.Read(test.Root.FinalPath(derived.Reference)),
                Assert.Single(test.ReportPublicationRegistry.Bindings).Record.Binding!.ActualIdentity.FileIdentity);
            var storedBytes = await test.Physical.ReadVerifiedAsync(receipt.Facts.FutureRevision, derived.Reference);
            Assert.Equal(derived.Reference.ByteCount, storedBytes.LongLength);
            Assert.Equal(derived.Reference.Digest, Sha256Digest.FromBytes(storedBytes));
            Assert.Equal(2, reservationChecks);
            Assert.Equal(2, bindingChecks);
            Assert.Equal(0, test.Storage.PlainCalls);
            Assert.Equal(1, test.NativeRegistryResolutions);
            Assert.Equal(1, test.ReportRegistryResolutions);
            Assert.Equal(1, test.RegistryFactoryResolutions);

            var nativeCalls = test.Native.Runner.NativeBodies.Count;
            var publications = test.Storage.Publications.Count;
            var replay = await test.CreateNativeAsync(bytes, key);
            Assert.Equal(created.Receipt, replay.Receipt);
            Assert.Equal(RevisionIngestionOutcome.CommittedValid, replay.Outcome);
            Assert.Equal(available.Metadata, RevisionReportTestSupport.Available(await test.Reports.GetRetainedAsync(test.Actor,
                receipt.Facts.FutureRevision, RevisionReportOperation.Projection, available.Metadata.DerivationId)).Metadata);
            Assert.Equal(available.Metadata, RevisionReportTestSupport.Available(await test.Reports.EnsureAsync(test.Actor,
                receipt.Facts.FutureRevision, RevisionReportOperation.Projection)).Metadata);
            Assert.Equal(nativeCalls, test.Native.Runner.NativeBodies.Count);
            Assert.Equal(publications, test.Storage.Publications.Count);
            await test.AssertPoolsUnboundAsync(pids);
        }
        finally
        {
            cancellation.Cancel();
            nativeBody.Release(); nativeWorker.Release(); reportBody.Release();
            await DrainAsync(native);
            if (projection is not null) await DrainAsync(projection);
        }
    }

    [RevisionIngestionFact]
    public async Task ActualConfiguredCachedAndRetainedReadsDoNotResolveUnusableWritersOrRunner()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var created = await test.CreateNativeAsync(RevisionIngestionTestSupport.Fixture("versioned-exact"));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
        var receipt = await test.ReceiptAsync(created);
        var report = RevisionReportTestSupport.Available(await test.Reports.EnsureAsync(test.Actor,
            receipt.Facts.FutureRevision, RevisionReportOperation.Projection));
        var workerCalls = test.Native.Runner.NativeBodies.Count;
        foreach (var imageConfigured in new[] { false, true })
        {
            var values = Configuration(test);
            if (imageConfigured) values["WEFT_FERRULE_WORKER_IMAGE"] = test.Native.Runner.BuildIdentity;
            var reads = new List<string>();
            var builder = Builder(test.Root.ApplicationRoot);
            CatalogHostConfiguration.Configure(builder, new(IPAddress.Loopback, 4100), key =>
            { reads.Add(key); return values.GetValueOrDefault(key); });
            var writerResolutions = 0;
            var runnerResolutions = 0;
            builder.Services.AddSingleton<PostgresRevisionDerivationDatabase>(_ =>
            { writerResolutions++; throw new InvalidOperationException("Cached delivery cannot resolve an unusable writer."); });
            builder.Services.AddSingleton<IInspectionWorkerRunner>(_ =>
            { runnerResolutions++; throw new InvalidOperationException("Cached delivery cannot resolve a runner."); });
            await using var app = builder.Build();
            Assert.Same(app.Services.GetRequiredService<IPrivateObjectStore>(), app.Services.GetRequiredService<IPrivateObjectPublicationStore>());
            var reader = app.Services.GetRequiredService<IRevisionReportReader>();
            var deriver = app.Services.GetRequiredService<IRevisionReportDeriver>();
            var retained = await reader.GetRetainedAsync(test.Actor, receipt.Facts.FutureRevision,
                RevisionReportOperation.Projection, report.Metadata.DerivationId);
            Assert.Equal(report.Metadata, RevisionReportTestSupport.Available(retained).Metadata);
            var current = await reader.GetCurrentAsync(test.Actor, receipt.Facts.FutureRevision, RevisionReportOperation.Projection);
            var ensure = await deriver.EnsureAsync(test.Actor, receipt.Facts.FutureRevision, RevisionReportOperation.Projection);
            if (imageConfigured)
            {
                Assert.Equal(report.Metadata, RevisionReportTestSupport.Available(current).Metadata);
                Assert.Equal(report.Metadata, RevisionReportTestSupport.Available(ensure).Metadata);
            }
            else
            {
                Assert.Equal(RevisionReportReadOutcome.ProfileUnavailable, current.Outcome);
                Assert.Equal(RevisionReportEnsureOutcome.ProfileUnavailable, ensure.Outcome);
            }
            Assert.Equal(0, writerResolutions);
            Assert.Equal(0, runnerResolutions);
            Assert.DoesNotContain("WEFT_POSTGRES_DERIVATION_CONNECTION_STRING", reads);
            Assert.DoesNotContain("WEFT_POSTGRES_INGESTION_CONNECTION_STRING", reads);
        }
        Assert.Equal(workerCalls, test.Native.Runner.NativeBodies.Count);
        Assert.Equal(2, test.Storage.Publications.Count);
    }

    [RevisionIngestionFact]
    public async Task RevocationBeforeActualBindPreventsPrivatePayloadReadsAndAnyNativeWorkerDispatch()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        test.NativeRegistry.BeforeBindAsync = async (_, _) =>
            await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
        var result = await test.CreateNativeAsync(RevisionIngestionTestSupport.Fixture("versioned-exact"));
        Assert.Equal(RevisionIngestionOutcome.NotAuthorizedOrNotFound, result.Outcome);
        var publication = Assert.Single(test.Storage.Publications);
        Assert.Equal(0, publication.ReadCalls);
        Assert.Equal(0, publication.BytesRead);
        Assert.Null(publication.Result);
        Assert.Single(test.NativeRegistry.Reservations);
        Assert.Empty(test.NativeRegistry.Bindings);
        Assert.Empty(test.Native.Runner.NativeBodies);
        Assert.Empty(Directory.GetFiles(test.Root.RootPath, "*.tmp", SearchOption.AllDirectories));
        Assert.False(File.Exists(test.Root.FinalPath(publication.Reference)));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_publications WHERE bound_at IS NULL"));
        await test.AssertPoolsUnboundAsync();
    }

    [RevisionIngestionFact]
    public async Task CancellationAfterActualBindingSettlesTheOriginalAttemptAndAdmitsANewActualC2Upload()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var pids = await test.AssertPoolsUnboundAsync();
        var body = new RevisionIngestionGate();
        test.Storage.BeforeFirstPayloadReadAsync = (_, token) => body.HoldAsync(token);
        using var cancellation = new CancellationTokenSource();
        var bytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
        var original = test.CreateNativeAsync(bytes, cancellationToken: cancellation.Token);
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(test.NativeRegistry.Bindings);
            Assert.Equal(0, Assert.Single(test.Storage.Publications).ReadCalls);
            await test.AssertPoolsUnboundAsync(pids);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
            Assert.Empty(test.Native.Runner.NativeBodies);
            Assert.Empty(Directory.GetFiles(test.Root.RootPath, "*.tmp", SearchOption.AllDirectories));
            Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
            var claim = Assert.Single(test.Metadata.Claims);
            var receipt = Assert.IsType<IngestionReceipt>(await test.Native.Metadata.GetReceiptAsync(test.Actor, test.Project, claim.OperationId));
            Assert.Equal(IngestionFailureCode.Canceled, Assert.Single(receipt.Attempts).Completion!.Failure);
            await test.AssertPoolsUnboundAsync(pids);
            test.Storage.BeforeFirstPayloadReadAsync = null;
            Assert.Equal(RevisionIngestionOutcome.CommittedValid, (await test.CreateNativeAsync(bytes)).Outcome);
            Assert.Single(test.Native.Runner.NativeBodies);
        }
        finally { cancellation.Cancel(); body.Release(); await DrainAsync(original); }
    }

    [RevisionIngestionFact]
    public async Task ReportCancellationDuringTheActualWorkerSettlesBeforeAnotherOperationPublishes()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var created = await test.CreateNativeAsync(RevisionIngestionTestSupport.Fixture("versioned-exact"));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
        var receipt = await test.ReceiptAsync(created);
        var pids = await test.AssertPoolsUnboundAsync();
        var worker = new RevisionIngestionGate();
        test.Native.Launcher.NextProtocolGate = worker;
        using var cancellation = new CancellationTokenSource();
        var original = test.Reports.EnsureAsync(test.Actor, receipt.Facts.FutureRevision, RevisionReportOperation.Projection,
            cancellationToken: cancellation.Token);
        try
        {
            await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(original.IsCompleted);
            await test.AssertPoolsUnboundAsync(pids);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
            Assert.Empty(test.ReportPublicationRegistry.Reservations);
            Assert.Single(test.Storage.Publications);
            var completion = Assert.Single(test.ReportMetadata.Finishes);
            Assert.Equal(RevisionReportFailureCategory.Canceled, completion.Completion.Failure);
            Assert.Equal(RevisionReportStoreOutcome.Applied, completion.Result.Outcome);
            await test.AssertPoolsUnboundAsync(pids);
            var next = RevisionReportTestSupport.Available(await test.Reports.EnsureAsync(test.Actor,
                receipt.Facts.FutureRevision, RevisionReportOperation.Validation));
            Assert.IsType<VerifiedValidationReport>(next);
            Assert.Single(test.ReportPublicationRegistry.Bindings);
            Assert.Equal(2, test.Storage.Publications.Count);
        }
        finally { cancellation.Cancel(); worker.Release(); await DrainAsync(original); }
    }

    [RevisionIngestionFact]
    public async Task ActualTerminalReceiptAndReplayDoNotOpenAnUnusableRootOrDispatchAnotherWorker()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
        var created = await test.CreateNativeAsync(bytes);
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
        var original = await test.ReceiptAsync(created);
        var values = Configuration(test);
        values.Remove("WEFT_REVISION_REPORTS_ENABLED");
        values.Remove("WEFT_REVISION_REPORT_DERIVATION_ENABLED");
        values["WEFT_REVISION_INGESTION_ENABLED"] = "true";
        values["WEFT_POSTGRES_INGESTION_CONNECTION_STRING"] = test.Case.IngestionConnectionString;
        values["WEFT_PRIVATE_STORAGE_ROOT"] = "/proc/weft-claim-aware-replay-" + Guid.NewGuid().ToString("N");
        var builder = Builder(test.Root.ApplicationRoot);
        CatalogHostConfiguration.Configure(builder, new(IPAddress.Loopback, 4100), variable => values.GetValueOrDefault(variable));
        await using var app = builder.Build();
        Assert.Null(app.Services.GetService<IInspectionWorkerRunner>());
        var service = app.Services.GetRequiredService<IRevisionIngestionService>();
        var observed = await service.GetOwnReceiptAsync(test.Actor, test.Project, original.Facts.OperationId);
        Assert.Equal(created.Receipt, observed);
        var replay = await service.CreateAsync(test.Actor, test.Project, original.Facts.IdempotencyKey,
            RevisionIngestionTestSupport.Request(), new MemoryStream(bytes));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, replay.Outcome);
        Assert.Equal(created.Receipt, replay.Receipt);
        Assert.Single(test.Storage.Publications);
        Assert.Single(test.Native.Runner.NativeBodies);
    }

    private static WebApplicationBuilder Builder(string root) => WebApplication.CreateBuilder(new WebApplicationOptions
    { EnvironmentName = "Development", Args = [], ContentRootPath = root });

    private static Dictionary<string, string> Configuration(PostgresClaimAwareStorageIntegrationSupport test) => new()
    {
        ["WEFT_CATALOG_ENABLED"] = "true",
        ["WEFT_DEV_IDENTITY_ENABLED"] = "true",
        ["WEFT_DEV_PRINCIPAL_ID"] = test.Actor.PrincipalId.Value.ToString("D"),
        ["WEFT_DEV_ORGANIZATION_ID"] = test.Project.OrganizationId.ToString("D"),
        ["WEFT_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4100",
        ["WEFT_CATALOG_CONNECTION_STRING"] = test.Case.CatalogConnectionString,
        ["WEFT_REVISION_REPORTS_ENABLED"] = "true",
        ["WEFT_REVISION_REPORT_DERIVATION_ENABLED"] = "true",
        ["WEFT_POSTGRES_REPORT_CONNECTION_STRING"] = test.Case.ReportConnectionString,
        ["WEFT_POSTGRES_DERIVATION_CONNECTION_STRING"] = "unusable writer must remain unparsed",
        ["WEFT_PRIVATE_STORAGE_ROOT"] = test.Root.RootPath
    };

    private static async Task DrainAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(12)); }
        catch (Exception) { /* Settle the owned operation before the fixture disposes its real pools/root. */ }
    }
}
