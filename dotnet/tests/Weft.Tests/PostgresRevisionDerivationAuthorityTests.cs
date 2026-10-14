using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Real source/current authority; typed derived publication assertions remain explicitly synthetic.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRevisionDerivationAuthorityTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task EveryCurrentProjectRoleMayReadAndPublishReportMetadataWithoutNativeOrSampleAuthority()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        foreach (var role in new[] { "viewer", "runner", "publisher", "administrator" })
        {
            await test.Case.ExecuteAsync("""
                UPDATE weft.project_memberships SET role=$1,version=version+1
                WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4 AND role<>$1
                """, role, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Admitted);
            var observed = Assert.IsType<AuthorizedRevisionReportSource>(await test.Reader.GetSourceAsync(test.AdmittedActor, test.Revision));
            Assert.Equal(test.Source, observed.Source);
            Assert.Equal(test.AdmittedActor.PrincipalId, observed.Actor);
            var key = test.ProjectionKey(algorithm: "synthetic.role." + role);
            var result = await test.PublishSyntheticAsync(key, test.AdmittedActor);
            Assert.Equal(test.AdmittedActor.PrincipalId, result.OriginalActor);
            var retained = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(
                test.Actor, test.Revision, RevisionReportOperation.Projection, result.DerivationId));
            Assert.Equal(result, retained.Result);
        }
        Assert.Equal(new long[] { 4, 4, 4, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task ForeignAncestryAndUnadmittedOrUnrelatedProjectRolesRevealNoSourceOrRetainedResult()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var result = await test.PublishSyntheticAsync();
        foreach (var principal in new[] { test.Seed.Bob, test.Seed.OtherProjectAdmin, Guid.NewGuid() })
        {
            var actor = new ActorContext(new PrincipalId(principal));
            Assert.Null(await test.Reader.GetSourceAsync(actor, test.Revision));
            Assert.Null(await test.Reader.GetCurrentAsync(actor, test.Key));
            Assert.Null(await test.Reader.GetRetainedAsync(actor, test.Revision, test.Key.Operation, result.DerivationId));
            AssertOpaque(await test.Store.TryClaimAsync(actor, test.Key, Guid.NewGuid()));
            AssertOpaque(await test.Store.RepairAsync(actor, new(test.Key, result.CacheEntryId,
                result.DerivationId, result.DerivedObject, RevisionReportRepairReason.Missing), Guid.NewGuid()));
        }
        RevisionScope[] wrongScopes =
        [
            new(test.Seed.OrganizationB, test.Revision.ProjectId, test.Revision.MappingId, test.Revision.RevisionId),
            new(test.Revision.OrganizationId, test.Seed.HiddenProjectA, test.Revision.MappingId, test.Revision.RevisionId),
            new(test.Revision.OrganizationId, test.Revision.ProjectId, Guid.NewGuid(), test.Revision.RevisionId),
            new(test.Revision.OrganizationId, test.Revision.ProjectId, test.Revision.MappingId, Guid.NewGuid())
        ];
        foreach (var scope in wrongScopes)
        {
            Assert.Null(await test.Reader.GetSourceAsync(test.Actor, scope));
            Assert.Null(await test.Reader.GetRetainedAsync(test.Actor, scope, test.Key.Operation, result.DerivationId));
            var source = new RevisionReportSource(scope,
                new(PrivateObjectId.Restore(scope, test.Source.NativeObject.Id.ObjectId, PrivateObjectClass.NativeArtifact),
                    test.Source.NativeObject.Digest, test.Source.NativeObject.ByteCount),
                test.Source.LogicalDocument, test.Source.DependencyIdentity, test.Source.SourceBackendId);
            var key = test.ProjectionKey(source: source);
            Assert.Null(await test.Reader.GetCurrentAsync(test.Actor, key));
            AssertOpaque(await test.Store.TryClaimAsync(test.Actor, key, Guid.NewGuid()));
        }
        Assert.Null(await test.Reader.GetRetainedAsync(test.Actor, test.Revision, RevisionReportOperation.Validation, result.DerivationId));
        Assert.Null(await test.Reader.GetRetainedAsync(test.Actor, test.Revision, test.Key.Operation, Guid.NewGuid()));
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task EverySuppliedNativeTupleFieldMustMatchTheCommittedSourceBeforeAnyKeyReservation()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var reference = test.Source.NativeObject;
        RevisionReportSource[] sources =
        [
            new(test.Revision, new(PrivateObjectId.Restore(test.Revision, Guid.NewGuid(), PrivateObjectClass.NativeArtifact), reference.Digest, reference.ByteCount),
                test.Source.LogicalDocument, test.Source.DependencyIdentity, test.Source.SourceBackendId),
            new(test.Revision, new(reference.Id, Sha256Digest.FromBytes("different synthetic source"u8), reference.ByteCount),
                test.Source.LogicalDocument, test.Source.DependencyIdentity, test.Source.SourceBackendId),
            new(test.Revision, new(reference.Id, reference.Digest, reference.ByteCount + 1),
                test.Source.LogicalDocument, test.Source.DependencyIdentity, test.Source.SourceBackendId),
            new(test.Revision, reference, "foreign.entry.json", test.Source.DependencyIdentity, test.Source.SourceBackendId),
            new(test.Revision, reference, test.Source.LogicalDocument, "foreign.dependencies.v1", test.Source.SourceBackendId),
            new(test.Revision, reference, test.Source.LogicalDocument, test.Source.DependencyIdentity, "foreign-backend")
        ];
        foreach (var source in sources)
        {
            var profile = new RevisionReportBackendProfile(source.SourceBackendId,
                test.Key.BackendRevision, test.Key.BackendBuild, test.Key.AdapterBuild);
            var key = test.ProjectionKey(source: source, profile: profile);
            Assert.Null(await test.Reader.GetCurrentAsync(test.Actor, key));
            AssertOpaque(await test.Store.TryClaimAsync(test.Actor, key, Guid.NewGuid()));
        }
        Assert.Equal(new long[] { 0, 0, 0, 0 }, await test.CountsAsync());
        Assert.Equal(test.Source, (await test.Reader.GetSourceAsync(test.Actor, test.Revision))!.Source);
    }

    [PostgresFact]
    public async Task PrincipalOrganizationAndProjectMembershipRevocationIsFreshForReadsClaimsAndCompletion()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var retainedResult = await test.PublishSyntheticAsync();
        var old = await test.AttemptSnapshotAsync(retainedResult.DerivationId);
        (string Name, string Statement, object[] Parameters)[] revocations =
        [
            ("principal", "UPDATE weft.principals SET active=$1 WHERE id=$2", [false, test.Seed.Admitted]),
            ("organization", "UPDATE weft.organization_memberships SET active=$1 WHERE organization_id=$2 AND principal_id=$3",
                [false, test.Project.OrganizationId, test.Seed.Admitted]),
            ("project", "UPDATE weft.project_memberships SET active=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
                [false, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Admitted])
        ];
        foreach (var (name, statement, parameters) in revocations)
        {
            var key = test.ProjectionKey(algorithm: "synthetic.revocation." + name);
            var claim = await test.ClaimAsync(key, test.AdmittedActor);
            await test.Case.ExecuteAsync(statement, parameters);
            try
            {
                Assert.Null(await test.Reader.GetSourceAsync(test.AdmittedActor, test.Revision));
                Assert.Null(await test.Reader.GetCurrentAsync(test.AdmittedActor, test.Key));
                Assert.Null(await test.Reader.GetRetainedAsync(test.AdmittedActor, test.Revision, test.Key.Operation, retainedResult.DerivationId));
                AssertOpaque(await test.Store.TryClaimAsync(test.AdmittedActor, key, Guid.NewGuid()));
                AssertOpaque(await test.Store.FinishAsync(test.AdmittedActor, claim,
                    PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim)));
                Assert.Equal(retainedResult, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.SelectedResult);
                Assert.Equal(old, await test.AttemptSnapshotAsync(retainedResult.DerivationId));
            }
            finally
            {
                parameters[0] = true;
                await test.Case.ExecuteAsync(statement, parameters);
            }
            Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.AdmittedActor, claim,
                PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim))).Outcome);
            Assert.Equal(test.Source, (await test.Reader.GetSourceAsync(test.AdmittedActor, test.Revision))!.Source);
        }
        Assert.Equal(new long[] { 4, 4, 4, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task AnotherCurrentlyAuthorizedActorCanReadButCannotFinishTheOriginalActorsClaim()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var claim = await test.ClaimAsync(actor: test.AdmittedActor);
        var before = await test.AttemptSnapshotAsync(claim.DerivationId);
        var completion = PostgresRevisionDerivationTestSupport.SyntheticProjectionCompletion(claim);
        var refused = await test.Store.FinishAsync(test.Actor, claim, completion);
        Assert.Equal(RevisionReportStoreOutcome.ClaimLost, refused.Outcome);
        Assert.Null(refused.Snapshot);
        Assert.Null(refused.Claim);
        Assert.Equal(before, await test.AttemptSnapshotAsync(claim.DerivationId));
        var observed = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(
            test.Actor, test.Revision, claim.Key.Operation, claim.DerivationId));
        Assert.Null(observed.Attempt.Completion);
        Assert.Null(observed.Result);
        var finished = await test.Store.FinishAsync(test.AdmittedActor, claim, completion);
        Assert.Equal(RevisionReportStoreOutcome.Applied, finished.Outcome);
        Assert.Equal(test.AdmittedActor.PrincipalId, finished.Snapshot!.SelectedResult!.OriginalActor);
        Assert.Equal(finished.Snapshot.SelectedResult,
            (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.SelectedResult);
    }

    [PostgresFact]
    public async Task DeletingTheReportActorsCurrentMembershipPreservesHistoryForOtherCurrentlyAuthorizedReaders()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var source = await test.ImmutableSourceSnapshotAsync();
        var result = await test.PublishSyntheticAsync(actor: test.AdmittedActor);
        var old = await test.AttemptSnapshotAsync(result.DerivationId);
        await test.Case.ExecuteAsync("DELETE FROM weft.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Admitted);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.project_memberships WHERE principal_id=$1", test.Seed.Admitted));
        Assert.Null(await test.Reader.GetSourceAsync(test.AdmittedActor, test.Revision));
        Assert.Null(await test.Reader.GetCurrentAsync(test.AdmittedActor, test.Key));
        Assert.Null(await test.Reader.GetRetainedAsync(test.AdmittedActor, test.Revision, test.Key.Operation, result.DerivationId));
        AssertOpaque(await test.Store.TryClaimAsync(test.AdmittedActor, test.Key, Guid.NewGuid()));
        var retained = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(
            test.Actor, test.Revision, test.Key.Operation, result.DerivationId));
        Assert.Equal(result, retained.Result);
        Assert.Equal(test.AdmittedActor.PrincipalId, retained.Result!.OriginalActor);
        Assert.Equal(result, (await test.Reader.GetCurrentAsync(test.Actor, test.Key))!.SelectedResult);
        Assert.Equal(old, await test.AttemptSnapshotAsync(result.DerivationId));
        Assert.Equal(source, await test.ImmutableSourceSnapshotAsync());
    }

    private static void AssertOpaque(RevisionReportStoreResult result)
    {
        Assert.Equal(RevisionReportStoreOutcome.NotAuthorizedOrNotFound, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.Null(result.Claim);
    }
}
