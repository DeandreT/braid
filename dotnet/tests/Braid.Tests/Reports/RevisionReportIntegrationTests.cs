using System.Runtime.Versioning;
using System.Text.Json;
using Braid.Application;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RevisionReportIntegrationTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task ActualExactProjectionPublishesVerifiedBytesBeforeFinishAndSubsequentReadsDoNoWork()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        var source = await test.SourceSnapshotAsync();
        var workerCalls = test.Ingestion.Runner.NativeBodies.Count;
        test.StoragePort.ObserveAsync = async (observation, token) =>
        {
            if (observation.Boundary == RevisionReportIoBoundary.AfterPublish)
            {
                var beforeFinish = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(
                    test.Actor, test.Key(RevisionReportOperation.Projection), token));
                Assert.Equal(RevisionReportState.Pending, beforeFinish.State);
                Assert.Null(beforeFinish.SelectedResult);
                Assert.Empty(test.MetadataPort.Finishes);
            }
        };
        test.MetadataPort.BeforeFinishAsync = (_, completion, _) =>
        {
            Assert.True(completion.IsUsable);
            var verified = Assert.Single(test.StoragePort.Observations, value => value.Boundary == RevisionReportIoBoundary.AfterRead &&
                value.Reference.Id.Class == PrivateObjectClass.DerivedArtifact);
            Assert.Equal(completion.DerivedObject, verified.Reference);
            Assert.Equal(verified.Reference.Digest, verified.ActualDigest);
            Assert.Equal(verified.Reference.ByteCount, verified.ActualByteCount);
            return Task.CompletedTask;
        };
        var projected = Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(
            await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)));
        var document = Assert.IsType<ProjectionDocumentV1>(projected.Report.Document);
        var literals = document.Rules.SelectMany(rule => rule.Dependencies).OfType<ConstantDependency>()
            .Select(value => value.Value).OfType<ExactNumberLiteralValue>().ToArray();
        Assert.Contains(literals, value => value.Flavor == ExactNumberFlavor.Integer && value.Value == "9007199254740993");
        Assert.Contains(literals, value => value.Flavor == ExactNumberFlavor.BinaryFloat && value.Value == "3ff0000000000000");
        Assert.Contains(literals, value => value.Flavor == ExactNumberFlavor.BinaryFloat && value.Value == "8000000000000000");
        Assert.Equal(test.Key(RevisionReportOperation.Projection), projected.Metadata.Key);
        Assert.Equal(test.Actor.PrincipalId, projected.Metadata.OriginalActor);
        Assert.Equal(test.Profile, projected.Metadata.Observation.Provenance!.Profile);
        Assert.True(projected.Metadata.Observation.Provenance.Confirmed);
        Assert.Equal(test.NativeBytes, test.Ingestion.Runner.NativeBodies[^1]);
        Assert.Equal(workerCalls + 1, test.Ingestion.Runner.NativeBodies.Count);
        Assert.Equal(1, test.BackendPort.Calls);
        var claim = Assert.Single(test.MetadataPort.Claims);
        Assert.Equal(TimeSpan.FromSeconds(60), claim.LeaseExpiresAtUtc - claim.StartedAtUtc);
        Assert.Equal(claim.CandidateObjectId, projected.Metadata.DerivedObject.Id);
        var finish = Assert.Single(test.MetadataPort.Finishes);
        Assert.Equal(RevisionReportStoreOutcome.Applied, finish.Result.Outcome);
        Assert.True(finish.Completion.IsUsable);
        var observations = test.StoragePort.Observations;
        var published = Assert.Single(observations, value => value.Boundary == RevisionReportIoBoundary.AfterPublish);
        Assert.Equal(PrivateObjectPublishResult.Created, published.Publication);
        var readbacks = observations.Where(value => value.Boundary == RevisionReportIoBoundary.AfterRead &&
            value.Reference.Id.Class == PrivateObjectClass.DerivedArtifact).ToArray();
        Assert.NotEmpty(readbacks);
        foreach (var readback in readbacks)
        {
            Assert.Equal(published.Reference, readback.Reference);
            Assert.Equal(projected.Metadata.DerivedObject.ByteCount, readback.ActualByteCount);
            Assert.Equal(projected.Metadata.DerivedObject.Digest, readback.ActualDigest);
        }

        var bytes = await test.PhysicalStore.ReadVerifiedAsync(test.Revision, projected.Metadata.DerivedObject);
        Assert.Equal(projected.Metadata.DerivedObject.ByteCount, bytes.LongLength);
        Assert.Equal(projected.Metadata.DerivedObject.Digest, Sha256Digest.FromBytes(bytes));
        using (var envelope = JsonDocument.Parse(bytes))
        {
            var root = envelope.RootElement;
            Assert.Equal("braid.revision-report-envelope.v1", root.GetProperty("schemaVersion").GetString());
            Assert.Equal(projected.Metadata.DerivationId.ToString("D"), root.GetProperty("derivationId").GetString());
            Assert.Equal(projected.Metadata.CacheEntryId.ToString("D"), root.GetProperty("cacheEntryId").GetString());
            Assert.Equal(claim.CandidateObjectId.ObjectId.ToString("D"), root.GetProperty("candidateObjectId").GetString());
            Assert.Equal(1L, root.GetProperty("generation").GetInt64());
        }
        var row = await test.AttemptSnapshotAsync(projected.Metadata.DerivationId);
        var beforeCounts = await test.CountsAsync();
        var beforeClaims = test.MetadataPort.ClaimCalls;
        var beforeFinishes = test.MetadataPort.FinishCalls;
        var beforePublications = test.StoragePort.PublishCalls;
        foreach (var hit in new[]
        {
            RevisionReportTestSupport.Available(await test.Service.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)),
            RevisionReportTestSupport.Available(await test.Service.GetRetainedAsync(test.Actor, test.Revision, RevisionReportOperation.Projection, projected.Metadata.DerivationId)),
            RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection))
        }) Assert.Equal(projected.Metadata, hit.Metadata);
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(beforeClaims, test.MetadataPort.ClaimCalls);
        Assert.Equal(beforeFinishes, test.MetadataPort.FinishCalls);
        Assert.Equal(0, test.MetadataPort.RepairCalls);
        Assert.Equal(beforePublications, test.StoragePort.PublishCalls);
        Assert.Equal(beforeCounts, await test.CountsAsync());
        Assert.Equal(row, await test.AttemptSnapshotAsync(projected.Metadata.DerivationId));
        Assert.Equal(source, await test.SourceSnapshotAsync());
        Assert.Equal(test.NativeBytes, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, test.Source.NativeObject));
        await test.AssertPoolsUnboundAsync();
    }

    [RevisionIngestionFact]
    public async Task ActualTrueAndFalseValidationPersistUsableResultsWithNativeOnlyFunctionEvidence()
    {
        foreach (var (name, expectedValid) in new[] { ("valid-basic", true), ("invalid-function", false) })
        {
            await using var test = await RevisionReportTestSupport.CreateAsync(fixture, name);
            var before = await test.SourceSnapshotAsync();
            var report = Assert.IsType<VerifiedValidationReport>(RevisionReportTestSupport.Available(
                await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Validation)));
            Assert.Equal(expectedValid, report.Report.IsValid);
            Assert.Equal(expectedValid ? OperationStatusV1.Succeeded : OperationStatusV1.InvalidMapping, report.Report.Status);
            Assert.Equal(expectedValid ? RevisionReportBackendStatus.Succeeded : RevisionReportBackendStatus.InvalidMapping,
                report.Metadata.Observation.Status);
            Assert.Equal(expectedValid, report.Metadata.Observation.Validation!.IsValid);
            Assert.Equal(report.Report.Diagnostics.Count, report.Report.EmittedIssueCount);
            Assert.All(report.Report.Diagnostics, diagnostic => Assert.Null(diagnostic.ProjectionLocator));
            Assert.Null(report.ProjectionDerivationId);
            if (expectedValid) Assert.Empty(report.Report.Diagnostics);
            else
            {
                Assert.True(report.Report.TotalIssueCount > 0);
                Assert.Contains(report.Report.Diagnostics, diagnostic => diagnostic.NativeLocator is GraphNodeNativeLocator
                { FunctionId: "18446744073709551615", NodeId: "7" });
                Assert.Contains(report.Report.Diagnostics, diagnostic => diagnostic.Owner is Braid.Contracts.Validation.GraphNodeNativeOwnerV1
                { FunctionId: "18446744073709551615", NodeId: "7" });
            }
            var bytes = await test.PhysicalStore.ReadVerifiedAsync(test.Revision, report.Metadata.DerivedObject);
            var reread = Assert.IsType<VerifiedValidationReport>(new RevisionReportEnvelopeCodec(test.Registry)
                .Decode(report.Metadata, bytes, new()));
            Assert.Equal(expectedValid, reread.Report.IsValid);
            Assert.Equal(report.Metadata, reread.Metadata);
            Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
            Assert.Equal(before, await test.SourceSnapshotAsync());
            Assert.Equal(test.NativeBytes, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, test.Source.NativeObject));
        }
    }

    [RevisionIngestionFact]
    public async Task ActualPartialProjectionAndExplicitVerifiedProjectionLinksRemainSeparateFromStoredValidation()
    {
        await using (var partial = await RevisionReportTestSupport.CreateAsync(fixture, "projection-partial"))
        {
            var report = AssertProjection(partial, "projection-partial",
                await partial.Service.EnsureAsync(partial.Actor, partial.Revision, RevisionReportOperation.Projection));
            Assert.Equal(CoverageStatus.Partial, report.Report.Document!.Coverage.Status);
            Assert.NotEmpty(report.Report.Document.Coverage.Gaps);
            Assert.Equal(RevisionReportCoverage.Partial, report.Metadata.Observation.Projection!.Coverage);
        }
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "validation-cycle");
        var validation = Assert.IsType<VerifiedValidationReport>(RevisionReportTestSupport.Available(
            await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Validation)));
        var projection = AssertProjection(test, "validation-cycle",
            await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        Assert.False(validation.Report.IsValid);
        Assert.All(validation.Report.Diagnostics, diagnostic => Assert.Null(diagnostic.ProjectionLocator));
        var storedBytes = await test.PhysicalStore.ReadVerifiedAsync(test.Revision, validation.Metadata.DerivedObject);
        var storedRow = await test.AttemptSnapshotAsync(validation.Metadata.DerivationId);
        var linked = Assert.IsType<VerifiedValidationReport>(RevisionReportTestSupport.Available(
            await test.Service.GetRetainedAsync(test.Actor, test.Revision, RevisionReportOperation.Validation,
                validation.Metadata.DerivationId, projection.Metadata.DerivationId)));
        Assert.Equal(projection.Metadata.DerivationId, linked.ProjectionDerivationId);
        Assert.Contains(linked.Report.Diagnostics, diagnostic => diagnostic.ProjectionLocator is RuleNodeReference);
        Assert.Equal(validation.Report.Diagnostics.Select(value => value.NativeLocator), linked.Report.Diagnostics.Select(value => value.NativeLocator));
        var missingSelection = await test.Service.GetCurrentAsync(test.Actor, test.Revision,
            RevisionReportOperation.Validation, Guid.NewGuid());
        Assert.Equal(RevisionReportReadOutcome.SelectedProjectionUnavailable, missingSelection.Outcome);
        Assert.Null(missingSelection.Report);
        Assert.Equal(2, test.BackendPort.Calls);
        Assert.Equal(storedBytes, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, validation.Metadata.DerivedObject));
        Assert.Equal(storedRow, await test.AttemptSnapshotAsync(validation.Metadata.DerivationId));
        Assert.All(Assert.IsType<VerifiedValidationReport>(new RevisionReportEnvelopeCodec(test.Registry)
            .Decode(validation.Metadata, storedBytes, new())).Report.Diagnostics, value => Assert.Null(value.ProjectionLocator));

        static VerifiedProjectionReport AssertProjection(RevisionReportTestSupport actual, string fixtureName,
            RevisionReportEnsureResult ensured)
        {
            var backend = actual.BackendPort.Completions.LastOrDefault();
            var completion = ensured.Read?.Snapshot?.CurrentAttempt?.Completion;
            var diagnostic = $"Actual fixture {fixtureName}: ensure={ensured.Outcome}; read={ensured.Read?.Outcome}; " +
                $"state={ensured.Read?.State}; completionPresent={completion is not null}; " +
                $"completionUsable={completion?.IsUsable}; failure={completion?.Failure}; " +
                $"completionStatus={completion?.Observation?.Status}; completionCoverage={completion?.Observation?.Projection?.Coverage}; " +
                $"completionConfirmed={completion?.Observation?.Provenance?.Confirmed}; " +
                $"backendPresent={backend is not null}; backendUsable={backend?.IsUsable}; backendFailure={backend?.Failure}; " +
                $"backendOperation={backend?.Observation?.Operation}; backendStatus={backend?.Observation?.Status}; " +
                $"backendCoverage={backend?.Observation?.Projection?.Coverage}; " +
                $"backendConfirmed={backend?.Observation?.Provenance?.Confirmed}.";
            Assert.True(backend is { IsUsable: true } && backend.Key?.Operation == RevisionReportOperation.Projection, diagnostic);
            Assert.True(ensured.Outcome == RevisionReportEnsureOutcome.Observed &&
                ensured.Read is { Outcome: RevisionReportReadOutcome.Observed, State: RevisionReportState.Available }, diagnostic);
            return Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(ensured));
        }
    }

    [RevisionIngestionFact]
    public async Task ChangedEffectivePolicyUsesAnotherCurrentKeyWhileHistoricalReadsNeedNoNativeOrBackend()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var original = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        var policy = new RevisionReportPolicy(nativeByteLimit: 65536, itemLimit: 256, textByteLimit: 512);
        var reduced = test.NewService(test.NewRegistry(policy));
        Assert.Equal(RevisionReportState.NotDerived,
            (await reduced.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)).State);
        var newer = RevisionReportTestSupport.Available(await reduced.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        Assert.NotEqual(original.Metadata.Key.Fingerprint, newer.Metadata.Key.Fingerprint);
        Assert.NotEqual(original.Metadata.DerivationId, newer.Metadata.DerivationId);
        Assert.Equal(policy, newer.Metadata.Key.Policy);
        var calls = test.BackendPort.Calls;
        var nativeReads = test.StoragePort.NativeReads;
        var claims = test.MetadataPort.ClaimCalls;
        var nativePath = test.PhysicalRoot.FinalPath(test.Source.NativeObject);
        var heldNativePath = test.PhysicalRoot.NewPath("owned-native-held.blob");
        File.Move(nativePath, heldNativePath);
        try
        {
            var readonlyService = test.NewService(test.NewRegistry(includeCurrent: false,
                backendFactory: () => throw new InvalidOperationException("A retained read resolved a worker.")),
                activeReadCaps: new(nativeByteLimit: 1));
            var retained = RevisionReportTestSupport.Available(await readonlyService.GetRetainedAsync(test.Actor,
                test.Revision, RevisionReportOperation.Projection, original.Metadata.DerivationId));
            Assert.Equal(original.Metadata, retained.Metadata);
            Assert.Equal(RevisionReportReadOutcome.ProfileUnavailable,
                (await readonlyService.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Projection)).Outcome);
            Assert.Equal(calls, test.BackendPort.Calls);
            Assert.Equal(nativeReads, test.StoragePort.NativeReads);
            Assert.Equal(claims, test.MetadataPort.ClaimCalls);
        }
        finally { File.Move(heldNativePath, nativePath); }
        Assert.Equal(new long[] { 2, 2, 2, 0 }, await test.CountsAsync());
    }

    [RevisionIngestionFact]
    public async Task FreshPoolsAndSameOwnedClusterRestartRecoverRealDerivedObjectsWithoutResolvingAWorker()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var original = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Validation));
        var row = await test.AttemptSnapshotAsync(original.Metadata.DerivationId);
        var source = await test.SourceSnapshotAsync();
        var bytes = await test.PhysicalStore.ReadVerifiedAsync(test.Revision, original.Metadata.DerivedObject);
        var calls = test.BackendPort.Calls;
        await test.ReportDatabase.DisposeAsync();
        await test.DerivationDatabase.DisposeAsync();
        await test.Ingestion.Database.DisposeAsync();
        await test.Ingestion.ReaderDatabase.DisposeAsync();
        await test.PhysicalStore.DisposeAsync();
        await AssertReopenedAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var owner = await PostgresRevisionDerivationTestSupport.OpenOwnerAsync(test.Case);
        await AssertReopenedAsync();
        Assert.Equal(row, await test.AttemptSnapshotAsync(original.Metadata.DerivationId, owner));
        Assert.Equal(source, await test.SourceSnapshotAsync(owner));
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync(owner));
        Assert.Equal(calls, test.BackendPort.Calls);

        async Task AssertReopenedAsync()
        {
            await using var reads = new PostgresRevisionReportDatabase(test.Case.ReportConnectionString);
            await using var store = test.PhysicalRoot.Open();
            var service = test.NewService(test.NewRegistry(backendFactory: () =>
                    throw new InvalidOperationException("A reopened read resolved a worker.")),
                store: store, reader: new PostgresRevisionReportMetadataReader(reads));
            var current = RevisionReportTestSupport.Available(await service.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Validation));
            var retained = RevisionReportTestSupport.Available(await service.GetRetainedAsync(test.Actor, test.Revision,
                RevisionReportOperation.Validation, original.Metadata.DerivationId));
            Assert.Equal(original.Metadata, current.Metadata);
            Assert.Equal(original.Metadata, retained.Metadata);
            Assert.Equal(bytes, await store.ReadVerifiedAsync(test.Revision, original.Metadata.DerivedObject));
            Assert.Equal(test.NativeBytes, await store.ReadVerifiedAsync(test.Revision, test.Source.NativeObject));
        }
    }
}
