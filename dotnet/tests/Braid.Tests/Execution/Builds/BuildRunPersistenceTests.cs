using Npgsql;
using Braid.Application;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BuildRunPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Approved_intent_is_replayed_without_resetting_the_run_or_adopting_a_different_request()
    {
        await using var test = await Context.CreateAsync(fixture);
        Assert.Equal(BuildRunSubmitOutcome.Created, await test.Writer.SubmitAsync(test.Actor, test.Intent));
        Assert.Equal(BuildRunSubmitOutcome.Replayed, await test.Writer.SubmitAsync(test.Actor, test.Intent));
        var changed = new BuildIntent(test.Intent.BuildId, test.Intent.Revision, test.Intent.NativeDigest, test.Intent.Backend,
            test.Intent.Profile, test.Intent.Boundaries, test.Intent.Environment, test.Intent.ApprovalId, Guid.NewGuid());
        Assert.Equal(BuildRunSubmitOutcome.Conflict, await test.Writer.SubmitAsync(test.Actor, changed));
        var row = await test.Reader.ReadAsync(test.Actor, test.Org, test.Intent.BuildId);
        Assert.NotNull(row); Assert.Equal(BuildRunState.Queued, row.State); Assert.Equal(0, row.Attempt);
        Assert.Equal(test.Intent.Fingerprint, row.Intent.Fingerprint);
        Assert.Null(await test.Reader.ReadAsync(new(new(Guid.NewGuid())), test.Org, test.Intent.BuildId));
        Assert.Null(await test.Reader.ReadAsync(test.Actor, Guid.NewGuid(), test.Intent.BuildId));
        Assert.Equal(new BuildRunKey(test.Org, test.Intent.BuildId), Assert.Single(await test.Dispatcher.DiscoverAsync(4)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.SubmitAsync(test.Actor, test.Intent));
    }

    [PostgresFact]
    public async Task Success_requires_the_exact_final_publication_not_only_a_stage_or_object()
    {
        await using var test = await Context.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        Assert.Equal(test.Actor.PrincipalId, claim.Actor);
        Assert.Null(await test.Dispatcher.ClaimAsync(test.Org, test.Intent.BuildId, Guid.NewGuid()));
        Assert.Empty(await test.Dispatcher.DiscoverAsync(4));
        await using var controls = new PostgresBuildStore(test.Control.Connection, true, claim);
        var staged = await Stage(controls, test.Actor, test.Intent);
        var completion = new BuildExecutionResult(BuildExecutionOutcome.Created, staged.Publication);
        Assert.False(await test.Dispatcher.CompleteAsync(claim, completion));
        var objects = new PostgresBuildObjectStore(controls, new NoNative(), test.Actor);
        using (var bytes = new MemoryStream(staged.Bytes.Copy(), false))
            await objects.PublishCreateOnlyAsync(test.Intent.Revision, staged.Publication.Object, bytes);
        Assert.False(await test.Dispatcher.CompleteAsync(claim, completion));
        Assert.Equal(BuildPublicationOutcome.Created, await controls.PublishAsync(test.Actor, staged.Publication));
        Assert.True(await test.Dispatcher.CompleteAsync(claim, completion));
        var saved = (await test.Reader.ReadAsync(test.Actor, test.Org, test.Intent.BuildId))!;
        Assert.Equal(BuildRunState.Succeeded, saved.State); Assert.Equal(1, saved.Attempt);
        Assert.Equal(staged.Bytes.Digest, saved.Artifact!.ContentDigest);
        Assert.False(await test.Dispatcher.CompleteAsync(claim, completion));
        Assert.Null(await test.Dispatcher.ClaimAsync(test.Org, test.Intent.BuildId, Guid.NewGuid()));
        Assert.Equal(BuildRunSubmitOutcome.Replayed, await test.Writer.SubmitAsync(test.Actor, test.Intent));
        var retained = (await test.Reader.ReadAsync(test.Actor, test.Org, test.Intent.BuildId))!;
        Assert.Equal(saved.State, retained.State); Assert.Equal(saved.Attempt, retained.Attempt);
        Assert.Equal(saved.Intent.Fingerprint, retained.Intent.Fingerprint); Assert.Equal(saved.Artifact!.ContentDigest, retained.Artifact!.ContentDigest);
    }

    [PostgresFact]
    public async Task Cancellation_atomically_blocks_a_stale_worker_from_advancing_publication()
    {
        await using var test = await Context.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        await using var controls = new PostgresBuildStore(test.Control.Connection, true, claim);
        var staged = await Stage(controls, test.Actor, test.Intent);
        Assert.True(await test.Writer.CancelAsync(test.Actor, test.Org, test.Intent.BuildId));
        var objects = new PostgresBuildObjectStore(controls, new NoNative(), test.Actor);
        using var bytes = new MemoryStream(staged.Bytes.Copy(), false);
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            objects.PublishCreateOnlyAsync(test.Intent.Revision, staged.Publication.Object, bytes));
        Assert.Equal("42501", failure.SqlState);
        Assert.False(await test.Dispatcher.CompleteAsync(claim, new(BuildExecutionOutcome.Created, staged.Publication)));
        Assert.False(await test.Dispatcher.RenewAsync(claim)); Assert.False(await test.Dispatcher.ReleaseAsync(claim));
        var snapshot = (await test.Reader.ReadAsync(test.Actor, test.Org, test.Intent.BuildId))!;
        Assert.Equal(BuildRunState.Canceled, snapshot.State); Assert.Null(snapshot.Artifact);
        Assert.Equal(0, await test.Case.ScalarAsync<int>("SELECT state FROM braid.build_publications"));
        Assert.Empty(await test.Dispatcher.DiscoverAsync(4));
    }

    [PostgresFact]
    public async Task Queued_cancellation_is_terminal_before_any_attempt_or_artifact()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Writer.SubmitAsync(test.Actor, test.Intent);
        Assert.True(await test.Writer.CancelAsync(test.Actor, test.Org, test.Intent.BuildId));
        Assert.True(await test.Writer.CancelAsync(test.Actor, test.Org, test.Intent.BuildId));
        Assert.Null(await test.Dispatcher.ClaimAsync(test.Org, test.Intent.BuildId, Guid.NewGuid()));
        var row = (await test.Reader.ReadAsync(test.Actor, test.Org, test.Intent.BuildId))!;
        Assert.Equal(BuildRunState.Canceled, row.State); Assert.Equal(0, row.Attempt);
        Assert.Null(row.Outcome); Assert.Null(row.Artifact);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_publications"));
    }

    [RegressionPostgresTheory]
    [InlineData("actor")]
    [InlineData("intent")]
    [InlineData("token")]
    [InlineData("worker")]
    [InlineData("attempt")]
    public async Task Substituted_claim_facts_cannot_renew_release_or_finish(string field)
    {
        await using var test = await Context.CreateAsync(fixture);
        var original = await test.ClaimAsync();
        var intent = field == "intent" ? new BuildIntent(test.Intent.BuildId, test.Intent.Revision, Sha256Digest.FromBytes([9]),
            test.Intent.Backend, test.Intent.Profile, test.Intent.Boundaries, test.Intent.Environment, test.Intent.ApprovalId, test.Intent.RegressionEvidenceId) : test.Intent;
        var changed = new BuildRunClaim(field == "actor" ? new(Guid.NewGuid()) : original.Actor, intent,
            field == "worker" ? Guid.NewGuid() : original.WorkerId, field == "token" ? Guid.NewGuid() : original.Token,
            field == "attempt" ? 2 : 1, original.LeaseExpires);
        Assert.False(await test.Dispatcher.RenewAsync(changed));
        Assert.False(await test.Dispatcher.ReleaseAsync(changed));
        Assert.False(await test.Dispatcher.CompleteAsync(changed, new(BuildExecutionOutcome.Unsupported)));
        Assert.Equal(BuildRunState.Running, (await test.Reader.ReadAsync(test.Actor, test.Org, test.Intent.BuildId))!.State);
        Assert.True(await test.Dispatcher.ReleaseAsync(original));
    }

    [RegressionPostgresTheory]
    [InlineData("approval")]
    [InlineData("membership")]
    [InlineData("environment")]
    public async Task Authority_changes_between_workflow_and_completion_prevent_run_success(string change)
    {
        await using var test = await Context.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        await using var controls = new PostgresBuildStore(test.Control.Connection, true, claim);
        var staged = await Publish(controls, test.Actor, test.Intent);
        if (change == "approval") Assert.True(await test.Control.Store.RevokeAsync(test.Actor, test.Org, test.Intent.Revision.ProjectId, test.Intent.ApprovalId));
        else if (change == "membership") await test.Control.Runs.Cases.RoleAsync("viewer");
        else Assert.Equal(BuildControlOutcome.Created, await test.Control.Store.SaveEnvironmentAsync(test.Actor,
            new(new(test.Intent.Environment.Project, test.Intent.Environment.EnvironmentId, 2), test.Intent.Profile, true)));
        Assert.False(await test.Dispatcher.CompleteAsync(claim, new(BuildExecutionOutcome.Created, staged.Publication)));
        Assert.Equal("revoked", await test.Case.ScalarAsync<string>("SELECT state FROM braid.build_runs"));
        Assert.Equal(2, await test.Case.ScalarAsync<int>("SELECT state FROM braid.build_publications"));
    }

    [PostgresFact]
    public async Task Last_attempt_acknowledgment_loss_retains_publication_as_uncertain_not_success()
    {
        await using var test = await Context.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        for (var attempt = 1; attempt < 3; attempt++)
        {
            Assert.True(await test.Dispatcher.ReleaseAsync(claim));
            await Task.Delay(TimeSpan.FromMilliseconds(2100));
            claim = (await test.Dispatcher.ClaimAsync(test.Org, test.Intent.BuildId, Guid.NewGuid()))!;
            Assert.Equal(attempt + 1, claim.Attempt);
        }
        await using var controls = new PostgresBuildStore(test.Control.Connection, true, claim);
        var publication = await Publish(controls, test.Actor, test.Intent);
        Assert.True(await test.Dispatcher.ReleaseAsync(claim));
        var row = (await test.Reader.ReadAsync(test.Actor, test.Org, test.Intent.BuildId))!;
        Assert.Equal(BuildRunState.Uncertain, row.State); Assert.Equal(3, row.Attempt); Assert.Null(row.Outcome);
        Assert.Equal(publication.Bytes.Digest, row.Artifact!.ContentDigest);
        await using var metadata = new PostgresBuildMetadataReader(test.Case.RegressionConnectionString("braid_build_metadata_reader"));
        var page = await metadata.ListRunsAsync(test.Actor, new(test.Intent.Revision.OrganizationId, test.Intent.Revision.ProjectId, test.Intent.Revision.MappingId), 1);
        var summary = Assert.Single(page!.Runs);
        var response = Braid.Api.Catalog.CatalogBuildMapping.Run(summary);
        Assert.Equal("uncertain", response.State); Assert.Null(response.Outcome);
        Assert.Equal(publication.Bytes.Digest.Value, response.Artifact!.Sha256);
        Assert.Equal(publication.Bytes.Length, response.Artifact.ByteCount);
        Assert.Empty(await test.Dispatcher.DiscoverAsync(4));
        Assert.Equal(BuildRunSubmitOutcome.Replayed, await test.Writer.SubmitAsync(test.Actor, test.Intent));
        Assert.NotNull(await test.Control.Store.ReadAsync(test.Actor, test.Org, test.Intent.BuildId));
    }

    [PostgresFact]
    public async Task Natural_lease_expiry_fences_old_writes_and_allows_a_new_attempt()
    {
        await using var test = await Context.CreateAsync(fixture);
        var old = await test.ClaimAsync();
        await Task.Delay(BuildRunPolicy.Lease + TimeSpan.FromSeconds(1));
        var current = await test.Dispatcher.ClaimAsync(test.Org, test.Intent.BuildId, Guid.NewGuid());
        Assert.NotNull(current); Assert.Equal(2, current.Attempt); Assert.NotEqual(old.Token, current.Token);
        Assert.False(await test.Dispatcher.RenewAsync(old)); Assert.False(await test.Dispatcher.ReleaseAsync(old));
        await using var stale = new PostgresBuildStore(test.Control.Connection, true, old);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => Stage(stale, test.Actor, test.Intent))).SqlState);
        await using var active = new PostgresBuildStore(test.Control.Connection, true, current);
        var published = await Publish(active, test.Actor, test.Intent);
        Assert.True(await test.Dispatcher.CompleteAsync(current, new(BuildExecutionOutcome.Created, published.Publication)));
    }

    [PostgresFact]
    public async Task Removing_the_publication_fence_refuses_both_new_dispatch_and_legacy_controls()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Case.ExecuteAsync("ALTER TABLE braid.build_publications DISABLE TRIGGER build_run_publication_fence");
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Dispatcher.DiscoverAsync(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Control.Store.ReadEnvironmentAsync(test.Actor, test.Intent.Environment));
    }

    private static async Task<StagedBuild> Stage(PostgresBuildStore store, ActorContext actor, BuildIntent intent)
    {
        var bytes = new BuildBytes([7, 8, 9], 1024);
        var reference = BuildArtifactReference.Restore(intent, bytes.Digest, bytes.Length);
        return (await store.StageAsync(actor, new(new(actor.PrincipalId, reference,
            new(PrivateObjectId.Restore(intent.Revision, intent.BuildId, PrivateObjectClass.DerivedArtifact), bytes.Digest, bytes.Length)), bytes)))!;
    }
    private static async Task<StagedBuild> Publish(PostgresBuildStore store, ActorContext actor, BuildIntent intent)
    {
        var staged = await Stage(store, actor, intent);
        var objects = new PostgresBuildObjectStore(store, new NoNative(), actor);
        using (var bytes = new MemoryStream(staged.Bytes.Copy(), false))
            await objects.PublishCreateOnlyAsync(intent.Revision, staged.Publication.Object, bytes);
        Assert.Equal(BuildPublicationOutcome.Created, await store.PublishAsync(actor, staged.Publication));
        return staged;
    }
    private sealed class Context : IAsyncDisposable
    {
        internal required BuildPersistenceTests.Context Control { get; init; }
        internal required PostgresBuildRunStore Writer { get; init; }
        internal required PostgresBuildRunStore Reader { get; init; }
        internal required PostgresBuildRunStore Dispatcher { get; init; }
        internal ActorContext Actor => Control.Actor;
        internal BuildIntent Intent => Control.Intent;
        internal Guid Org => Intent.Revision.OrganizationId;
        internal PostgresCase Case => Control.Runs.Cases.Case;
        internal static async Task<Context> CreateAsync(PostgresFixture fixture)
        {
            var control = await BuildPersistenceTests.Context.CreateAsync(fixture);
            try
            {
                Assert.Equal(BuildControlOutcome.Created, await control.Approve());
                var test = control.Runs.Cases.Case;
                return new()
                {
                    Control = control,
                    Writer = new(test.RegressionConnectionString("braid_build_run_writer"), BuildRunDatabaseProfile.Writer),
                    Reader = new(test.RegressionConnectionString("braid_build_run_reader"), BuildRunDatabaseProfile.Reader),
                    Dispatcher = new(test.RegressionConnectionString("braid_build_dispatcher"), BuildRunDatabaseProfile.Dispatcher)
                };
            }
            catch { await control.DisposeAsync(); throw; }
        }
        internal async Task<BuildRunClaim> ClaimAsync()
        {
            Assert.Equal(BuildRunSubmitOutcome.Created, await Writer.SubmitAsync(Actor, Intent));
            return (await Dispatcher.ClaimAsync(Org, Intent.BuildId, Guid.NewGuid()))!;
        }
        public async ValueTask DisposeAsync()
        { await Writer.DisposeAsync(); await Reader.DisposeAsync(); await Dispatcher.DisposeAsync(); await Control.DisposeAsync(); }
    }
    private sealed class NoNative : IPrivateObjectStore
    {
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream bytes, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken token = default) => throw new NotSupportedException();
    }
}
