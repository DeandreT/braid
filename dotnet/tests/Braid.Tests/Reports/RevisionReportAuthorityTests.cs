using System.Runtime.Versioning;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RevisionReportAuthorityTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task EveryReadMetadataRoleMayDeriveAndReadRealReportsIncludingPublisherDowngradedToViewer()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var source = await test.SourceSnapshotAsync();
        var index = 0;
        foreach (var role in new[] { "viewer", "runner", "publisher", "administrator" })
        {
            await test.Case.ExecuteAsync("""
                UPDATE braid.project_memberships SET role=$1,version=version+1
                WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4 AND role<>$1
                """, role, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
            var policy = new RevisionReportPolicy(itemLimit: 1024 - index++);
            var service = test.NewService(test.NewRegistry(policy));
            var report = RevisionReportTestSupport.Available(await service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
            Assert.Equal(test.Actor.PrincipalId, report.Metadata.OriginalActor);
            Assert.Equal(policy, report.Metadata.Key.Policy);
            Assert.Equal(report.Metadata, RevisionReportTestSupport.Available(await service.GetCurrentAsync(
                test.Actor, test.Revision, RevisionReportOperation.Projection)).Metadata);
            var otherAuthorized = new ActorContext(new PrincipalId(test.Seed.Admitted));
            Assert.Equal(report.Metadata, RevisionReportTestSupport.Available(await service.GetRetainedAsync(
                otherAuthorized, test.Revision, RevisionReportOperation.Projection, report.Metadata.DerivationId)).Metadata);
        }
        Assert.Equal(4, test.BackendPort.Calls);
        Assert.Equal(new long[] { 4, 4, 4, 0 }, await test.CountsAsync());
        Assert.Equal(source, await test.SourceSnapshotAsync());
    }

    [RevisionIngestionFact]
    public async Task DeniedActorsAndEveryForeignAncestryComponentResolveNoObjectOrWorker()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var selected = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        // Organization admission alone confers no project access; another project's administrator is also denied.
        await test.Case.ExecuteAsync("DELETE FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Admitted);
        var reads = test.StoragePort.DerivedReads;
        var nativeReads = test.StoragePort.NativeReads;
        var claims = test.MetadataPort.ClaimCalls;
        var row = await test.AttemptSnapshotAsync(selected.Metadata.DerivationId);
        foreach (var id in new[] { test.Seed.Admitted, test.Seed.OtherProjectAdmin, test.Seed.Bob, Guid.NewGuid() })
            await AssertHiddenAsync(new(new PrincipalId(id)), test.Revision);
        RevisionScope[] foreign =
        [
            new(test.Seed.OrganizationB, test.Revision.ProjectId, test.Revision.MappingId, test.Revision.RevisionId),
            new(test.Revision.OrganizationId, test.Seed.HiddenProjectA, test.Revision.MappingId, test.Revision.RevisionId),
            new(test.Revision.OrganizationId, test.Revision.ProjectId, Guid.NewGuid(), test.Revision.RevisionId),
            new(test.Revision.OrganizationId, test.Revision.ProjectId, test.Revision.MappingId, Guid.NewGuid())
        ];
        foreach (var scope in foreign) await AssertHiddenAsync(test.Actor, scope);
        Assert.Equal(reads, test.StoragePort.DerivedReads);
        Assert.Equal(nativeReads, test.StoragePort.NativeReads);
        Assert.Equal(claims, test.MetadataPort.ClaimCalls);
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(row, await test.AttemptSnapshotAsync(selected.Metadata.DerivationId));
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());

        async Task AssertHiddenAsync(ActorContext actor, RevisionScope scope)
        {
            var current = await test.Service.GetCurrentAsync(actor, scope, RevisionReportOperation.Projection);
            var retained = await test.Service.GetRetainedAsync(actor, scope, RevisionReportOperation.Projection, selected.Metadata.DerivationId);
            Assert.Equal(RevisionReportReadOutcome.NotAuthorizedOrNotFound, current.Outcome);
            Assert.Equal(RevisionReportReadOutcome.NotAuthorizedOrNotFound, retained.Outcome);
            Assert.Null(current.Report);
            Assert.Null(current.SelectedResult);
            Assert.Null(retained.Report);
            Assert.Equal(RevisionReportEnsureOutcome.NotAuthorizedOrNotFound,
                (await test.Service.EnsureAsync(actor, scope, RevisionReportOperation.Projection)).Outcome);
        }
    }

    [RevisionIngestionFact]
    public async Task RevocationAfterNativeReadPreventsDispatchAndAfterActualEligibleWorkerPreventsPublication()
    {
        foreach (var afterWorker in new[] { false, true })
        {
            await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
            var gate = new RevisionReportBoundaryGate();
            if (afterWorker) test.BackendPort.AfterDeriveAsync = (_, _, token) => gate.HoldAsync(token);
            else test.StoragePort.ObserveAsync = (observation, token) => observation.Boundary == RevisionReportIoBoundary.AfterRead &&
                observation.Reference.Id.Class == PrivateObjectClass.NativeArtifact ? gate.HoldAsync(token) : Task.CompletedTask;
            var pending = test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await test.AssertPoolsUnboundAsync();
                if (afterWorker) AssertActualEligibleCompletion(test);
                await test.Case.ExecuteAsync(afterWorker
                    ? "UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3"
                    : "UPDATE braid.principals SET active=false WHERE id=$1",
                    afterWorker ? [test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice] : [test.Seed.Alice]);
            }
            finally { gate.Release(); }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RevisionReportEnsureOutcome.NotAuthorizedOrNotFound, result.Outcome);
            Assert.Null(result.Read);
            Assert.Equal(afterWorker ? 1 : 0, test.BackendPort.Calls);
            Assert.Equal(0, test.StoragePort.PublishCalls);
            Assert.Equal(0L, (await test.CountsAsync())[2]);
            var stillAuthorized = new ActorContext(new PrincipalId(test.Seed.Admitted));
            Assert.NotNull(await test.Reader.GetSourceAsync(stillAuthorized, test.Revision));
        }
    }

    [RevisionIngestionFact]
    public async Task RevocationAfterRealPublicationOrReadbackPreventsCompletionWithoutAdoptingPublishedBytes()
    {
        foreach (var afterReadback in new[] { false, true })
        {
            await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
            var gate = new RevisionReportBoundaryGate();
            test.StoragePort.ObserveAsync = (observation, token) => observation.Reference.Id.Class == PrivateObjectClass.DerivedArtifact &&
                observation.Boundary == (afterReadback ? RevisionReportIoBoundary.AfterRead : RevisionReportIoBoundary.AfterPublish)
                ? gate.HoldAsync(token) : Task.CompletedTask;
            var pending = test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                AssertActualEligibleCompletion(test);
                await test.AssertPoolsUnboundAsync();
                await test.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
                    test.Project.OrganizationId, test.Seed.Alice);
            }
            finally { gate.Release(); }
            Assert.Equal(RevisionReportEnsureOutcome.NotAuthorizedOrNotFound,
                (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
            Assert.Equal(1, test.StoragePort.PublishCalls);
            Assert.Equal(afterReadback ? 1 : 0, test.StoragePort.DerivedReads);
            Assert.Equal(0L, (await test.CountsAsync())[2]);
            var publication = Assert.Single(test.StoragePort.Observations, value => value.Boundary == RevisionReportIoBoundary.AfterPublish);
            Assert.Equal(PrivateObjectPublishResult.Created, publication.Publication);
            var bytes = await test.PhysicalStore.ReadVerifiedAsync(test.Revision, publication.Reference);
            Assert.Equal(publication.Reference.Digest, Sha256Digest.FromBytes(bytes));
            Assert.Equal(publication.Reference.ByteCount, bytes.LongLength);
            var claim = Assert.Single(test.MetadataPort.Claims);
            var visible = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(
                new(new PrincipalId(test.Seed.Admitted)), test.Revision, RevisionReportOperation.Projection, claim.DerivationId));
            Assert.Null(visible.Result);
        }
    }

    [RevisionIngestionFact]
    public async Task RevocationAfterAcknowledgedFinishDiscardsDeliveryButPreservesExactCommittedSuccess()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var gate = new RevisionReportBoundaryGate();
        test.MetadataPort.AfterFinishAsync = (observation, token) => observation.Completion.IsUsable
            ? gate.HoldAsync(token) : Task.CompletedTask;
        var pending = test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
        string row;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var finish = Assert.Single(test.MetadataPort.Finishes);
            Assert.Equal(RevisionReportStoreOutcome.Applied, finish.Result.Outcome);
            AssertActualEligibleCompletion(test);
            row = await test.AttemptSnapshotAsync(finish.Claim.DerivationId);
            await test.Case.ExecuteAsync("UPDATE braid.principals SET active=false WHERE id=$1", test.Seed.Alice);
        }
        finally { gate.Release(); }
        Assert.Equal(RevisionReportEnsureOutcome.NotAuthorizedOrNotFound,
            (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        var id = Assert.Single(test.MetadataPort.Claims).DerivationId;
        var other = new ActorContext(new PrincipalId(test.Seed.Admitted));
        var recovered = RevisionReportTestSupport.Available(await test.Service.GetRetainedAsync(other, test.Revision,
            RevisionReportOperation.Projection, id));
        Assert.Equal(id, recovered.Metadata.DerivationId);
        Assert.Equal(row, await test.AttemptSnapshotAsync(id));
        Assert.Equal(1, test.MetadataPort.FinishCalls);
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
    }

    [RevisionIngestionFact]
    public async Task ReadRevocationAfterVerifiedBytesAndLaterMembershipDeletionNeverExposeTheCachedPayload()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var selected = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        var row = await test.AttemptSnapshotAsync(selected.Metadata.DerivationId);
        var gate = new RevisionReportBoundaryGate();
        test.StoragePort.ObserveAsync = (observation, token) => observation.Boundary == RevisionReportIoBoundary.AfterRead &&
            observation.Reference.Id.Class == PrivateObjectClass.DerivedArtifact ? gate.HoldAsync(token) : Task.CompletedTask;
        var pending = test.Service.GetCurrentAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await test.AssertPoolsUnboundAsync();
            await test.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
                test.Project.OrganizationId, test.Seed.Alice);
        }
        finally { gate.Release(); }
        var denied = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RevisionReportReadOutcome.NotAuthorizedOrNotFound, denied.Outcome);
        Assert.Null(denied.Report);
        Assert.Null(denied.SelectedResult);
        test.StoragePort.ObserveAsync = null;
        await test.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2",
            test.Project.OrganizationId, test.Seed.Alice);
        Assert.Equal(selected.Metadata, RevisionReportTestSupport.Available(await test.Service.GetCurrentAsync(
            test.Actor, test.Revision, RevisionReportOperation.Projection)).Metadata);
        var unreferenced = new ActorContext(new PrincipalId(test.Seed.Admitted));
        Assert.Equal(selected.Metadata, RevisionReportTestSupport.Available(await test.Service.GetRetainedAsync(
            unreferenced, test.Revision, RevisionReportOperation.Projection, selected.Metadata.DerivationId)).Metadata);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("""
            SELECT count(*) FROM braid.mappings WHERE organization_id=$1 AND project_id=$2
                AND (creator_principal_id=$3 OR owner_principal_id=$3)
            """, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Admitted));
        await test.Case.ExecuteAsync("DELETE FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Admitted);
        Assert.Equal(RevisionReportReadOutcome.NotAuthorizedOrNotFound, (await test.Service.GetRetainedAsync(
            unreferenced, test.Revision, RevisionReportOperation.Projection, selected.Metadata.DerivationId)).Outcome);
        Assert.Equal(selected.Metadata, RevisionReportTestSupport.Available(await test.Service.GetCurrentAsync(
            test.Actor, test.Revision, RevisionReportOperation.Projection)).Metadata);
        Assert.Equal(row, await test.AttemptSnapshotAsync(selected.Metadata.DerivationId));
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
    }

    [RevisionIngestionFact]
    public async Task MaximumOneMetadataPoolsStayUnboundAndReusableAcrossEveryActualIoBoundary()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var native = new RevisionReportBoundaryGate();
        var worker = new RevisionReportBoundaryGate();
        var publication = new RevisionReportBoundaryGate();
        var readback = new RevisionReportBoundaryGate();
        var finish = new RevisionReportBoundaryGate();
        var pids = await test.AssertPoolsUnboundAsync();
        test.StoragePort.ObserveAsync = (observation, token) => observation switch
        {
            { Boundary: RevisionReportIoBoundary.BeforeRead, Reference.Id.Class: PrivateObjectClass.NativeArtifact } => native.HoldAsync(token),
            { Boundary: RevisionReportIoBoundary.BeforePublish } => publication.HoldAsync(token),
            { Boundary: RevisionReportIoBoundary.AfterRead, Reference.Id.Class: PrivateObjectClass.DerivedArtifact } => readback.HoldAsync(token),
            _ => Task.CompletedTask
        };
        test.BackendPort.AfterDeriveAsync = (_, _, token) => worker.HoldAsync(token);
        test.MetadataPort.BeforeFinishAsync = (_, _, token) => finish.HoldAsync(token);
        var pending = test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
        try
        {
            foreach (var gate in new[] { native, worker, publication, readback, finish })
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await test.AssertPoolsUnboundAsync(pids);
                gate.Release();
            }
        }
        finally
        {
            foreach (var gate in new[] { native, worker, publication, readback, finish }) gate.Release();
        }
        RevisionReportTestSupport.Available(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        AssertActualEligibleCompletion(test);
        await test.AssertPoolsUnboundAsync(pids);
    }

    private static void AssertActualEligibleCompletion(RevisionReportTestSupport test)
    {
        var actual = Assert.Single(test.BackendPort.Completions);
        Assert.True(actual.IsUsable);
        Assert.Equal(RevisionReportBackendStatus.Succeeded, actual.Observation!.Status);
        Assert.True(actual.Observation.Provenance!.Confirmed);
        Assert.Equal(test.Revision, actual.Observation.Provenance.Scope);
        Assert.Equal(test.Source.NativeObject.Digest, actual.Observation.Provenance.NativeDigest);
        Assert.Equal(test.Profile, actual.Observation.Provenance.Profile);
    }
}
