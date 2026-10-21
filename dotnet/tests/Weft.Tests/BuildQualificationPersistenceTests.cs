using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual PostgreSQL evidence/privilege tests; synthetic artifacts are not runtime qualification.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BuildQualificationPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Proof_and_bytes_are_immutable_and_reopen_after_database_restart_and_approval_revocation()
    {
        await using var test = await Context.CreateAsync(fixture);
        Assert.Equal(BuildQualificationWriteOutcome.Created, await test.Store.SaveAsync(test.Actor, test.Qualification));
        Assert.Equal(BuildQualificationWriteOutcome.Replayed, await test.Store.SaveAsync(test.Actor, test.Qualification));
        var saved = (await test.Store.ReadAsync(test.Actor, test.Mapping, test.Qualification.Id))!;
        Assert.Equal(test.Qualification.Fingerprint, saved.Fingerprint);
        Assert.Equal(test.Qualification.CopyResultBytes(), saved.CopyResultBytes());
        Assert.True(await test.Control.Store.RevokeAsync(test.Actor, test.Mapping.OrganizationId, test.Mapping.ProjectId, test.Control.Intent.ApprovalId));
        await test.Store.DisposeAsync();
        await test.Control.Store.DisposeAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var reopened = new PostgresBuildQualificationStore(test.Connection, true);
        Assert.Equal(saved.Fingerprint, (await reopened.ReadAsync(test.Actor, test.Mapping, saved.Id))!.Fingerprint);
        Assert.Equal(BuildQualificationWriteOutcome.Replayed, await reopened.SaveAsync(test.Actor, saved));
        var replacement = test.Make(Guid.NewGuid());
        Assert.Equal(BuildQualificationWriteOutcome.Refused, await reopened.SaveAsync(test.Actor, replacement));
        await using var owner = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await owner.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM weft.build_qualifications", owner);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Lost_commit_acknowledgment_recovers_the_original_durable_qualification()
    {
        await using var test = await Context.CreateAsync(fixture);
        await using var relay = new ServerSessionCommitAckLossRelay(test.Connection);
        await using var routed = new PostgresBuildQualificationStore(relay.RoutedConnectionString, true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pending = routed.SaveAsync(test.Actor, test.Qualification, deadline.Token);
        try
        {
            await Task.WhenAny(pending, relay.CommitAcknowledgementWithheld).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            Assert.True(relay.CommitAcknowledgementWithheld.IsCompletedSuccessfully && !pending.IsCompleted);
            var durable = (await test.Store.ReadAsync(test.Actor, test.Mapping, test.Qualification.Id, deadline.Token))!;
            Assert.Equal(test.Qualification.Fingerprint, durable.Fingerprint);
            relay.DropAcknowledgement();
            await Assert.ThrowsAnyAsync<NpgsqlException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token));
            Assert.Equal(BuildQualificationWriteOutcome.Replayed, await test.Store.SaveAsync(test.Actor, test.Qualification, deadline.Token));
            Assert.Equal(durable.CopyResultBytes(), (await test.Store.ReadAsync(test.Actor, test.Mapping, durable.Id, deadline.Token))!.CopyResultBytes());
        }
        finally
        {
            relay.DropAcknowledgement(); deadline.Cancel();
            try { await pending; } catch (Exception failure) when (failure is NpgsqlException or OperationCanceledException) { }
        }
    }

    [PostgresFact]
    public async Task Own_scope_is_required_for_read_and_revocation_drops_private_proof()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Store.SaveAsync(test.Actor, test.Qualification);
        Assert.Null(await test.Store.ReadAsync(new(new(Guid.NewGuid())), test.Mapping, test.Qualification.Id));
        var other = new ActorContext(new(test.Control.Runs.Cases.Seed.Admitted));
        await test.Control.Runs.Cases.Seed.AddMembershipAsync(test.Case, test.Mapping.OrganizationId, test.Mapping.ProjectId, other.PrincipalId.Value, "publisher");
        Assert.Null(await test.Store.ReadAsync(other, test.Mapping, test.Qualification.Id));
        Assert.Null(await test.Store.ReadAsync(test.Actor, new(test.Mapping.OrganizationId, test.Mapping.ProjectId, Guid.NewGuid()), test.Qualification.Id));
        await test.Control.Runs.Cases.RoleAsync("viewer");
        Assert.Null(await test.Store.ReadAsync(test.Actor, test.Mapping, test.Qualification.Id));
        Assert.Equal(BuildQualificationWriteOutcome.Refused, await test.Store.SaveAsync(test.Actor, test.Qualification));
    }

    [RegressionPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authority_and_cancellation_are_rechecked_after_waiting_for_the_original_build_lock(bool cancel)
    {
        await using var test = await Context.CreateAsync(fixture);
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT 1 FROM weft.principals WHERE id=$1 FOR UPDATE", blocker, transaction))
        {
            command.Parameters.AddWithValue(test.Actor.PrincipalId.Value);
            await command.ExecuteScalarAsync();
        }
        var application = "qualification-wait-" + Guid.NewGuid().ToString("N");
        await using var observer = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var edge = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        await using var writer = new PostgresBuildQualificationStore(PostgresServerSessionTestSupport.Application(test.Connection, application), true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = writer.SaveAsync(test.Actor, test.Qualification, stop.Token);
        try
        {
            await PostgresServerSessionTestSupport.WaitBlockedAsync(edge, pending);
            if (cancel)
            {
                await stop.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                await transaction.RollbackAsync();
            }
            else
            {
                await using var revoke = new NpgsqlCommand("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", blocker, transaction);
                revoke.Parameters.AddWithValue(test.Mapping.OrganizationId); revoke.Parameters.AddWithValue(test.Mapping.ProjectId);
                revoke.Parameters.AddWithValue(test.Actor.PrincipalId.Value);
                await revoke.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
                Assert.Equal(BuildQualificationWriteOutcome.Refused, await pending);
            }
        }
        finally
        {
            if (!pending.IsCompleted) { await transaction.RollbackAsync(); await PostgresServerSessionTestSupport.SettleAsync(pending); }
        }
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_qualifications"));
    }

    [RegressionPostgresTheory]
    [InlineData("output")]
    [InlineData("runtime")]
    [InlineData("case")]
    [InlineData("replay")]
    [InlineData("trailing")]
    public async Task SQL_recomputes_output_and_identity_instead_of_accepting_forged_proof(string field)
    {
        await using var test = await Context.CreateAsync(fixture);
        var proof = JsonNode.Parse(test.Qualification.CopyCanonicalBytes())!;
        var result = test.Qualification.CopyResultBytes();
        if (field == "output")
        {
            var changed = JsonNode.Parse(result)!;
            changed["outputs"]![0]!["content"] = "eA";
            result = Encoding.UTF8.GetBytes(changed.ToJsonString());
            proof["resultDigest"] = Sha256Digest.FromBytes(result).Value; proof["resultBytes"] = result.Length;
        }
        else if (field == "runtime") proof["runtimeImage"] = "latest";
        else if (field == "case") proof["caseId"] = Guid.NewGuid().ToString("D");
        else if (field == "replay") proof["replayDigest"] = new string('0', 64);
        var text = proof.ToJsonString();
        if (field == "trailing") text += "\n";
        Assert.Equal(3, await test.Raw(Encoding.UTF8.GetBytes(text), result));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_qualifications"));
    }

    [RegressionPostgresTheory]
    [InlineData("table")]
    [InlineData("function")]
    [InlineData("trigger")]
    public async Task Widened_privileges_or_changed_schema_fail_closed(string change)
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Case.ExecuteAsync(change switch
        {
            "table" => "GRANT SELECT ON weft.build_qualifications TO weft_qualification_reader",
            "function" => "GRANT EXECUTE ON FUNCTION weft.build_json_text(text,boolean) TO weft_qualification_reader",
            _ => "ALTER TABLE weft.build_qualifications DISABLE TRIGGER qualification_immutable"
        });
        await using var reader = new PostgresBuildQualificationStore(test.Case.RegressionConnectionString("weft_qualification_reader"), false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(test.Actor, test.Mapping, test.Qualification.Id));
    }

    [PostgresFact]
    public async Task Concurrent_same_request_preserves_one_proof_and_a_changed_request_conflicts()
    {
        await using var test = await Context.CreateAsync(fixture);
        await using var second = new PostgresBuildQualificationStore(test.Connection, true);
        var outcomes = await Task.WhenAll(test.Store.SaveAsync(test.Actor, test.Qualification), second.SaveAsync(test.Actor, test.Qualification));
        Assert.Contains(BuildQualificationWriteOutcome.Created, outcomes); Assert.Contains(BuildQualificationWriteOutcome.Replayed, outcomes);
        var text = JsonNode.Parse(test.Qualification.CopyCanonicalBytes())!;
        text["runtimeImage"] = "sha256:" + new string('a', 64);
        Assert.Equal(2, await test.Raw(Encoding.UTF8.GetBytes(text.ToJsonString()), test.Qualification.CopyResultBytes()));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_qualifications"));
    }

    private sealed class Context : IAsyncDisposable
    {
        internal required BuildPersistenceTests.Context Control { get; init; }
        internal required BuildRunSnapshot Build { get; init; }
        internal required PostgresBuildQualificationStore Store { get; init; }
        internal required string Connection { get; init; }
        internal ActorContext Actor => Control.Actor;
        internal PostgresCase Case => Control.Runs.Cases.Case;
        internal MappingScope Mapping => Control.Runs.Cases.Mapping;
        internal BuildQualification Qualification { get; private set; } = null!;
        internal BuildQualification Make(Guid id) => BuildQualification.Create(id, Actor.PrincipalId, Build, evidence!, Control.Runs.Value,
            "sha256:" + new string('f', 64), new(CompiledBuildOutcome.Completed, Control.Runs.Value.Expected));
        private RegressionRunSnapshot? evidence;
        internal static async Task<Context> CreateAsync(PostgresFixture fixture)
        {
            var control = await BuildPersistenceTests.Context.CreateAsync(fixture);
            PostgresBuildQualificationStore? store = null;
            try
            {
                Assert.Equal(BuildControlOutcome.Created, await control.Approve());
                var test = control.Runs.Cases.Case;
                await using var writer = new PostgresBuildRunStore(test.RegressionConnectionString("weft_build_run_writer"), BuildRunDatabaseProfile.Writer);
                await using var reader = new PostgresBuildRunStore(test.RegressionConnectionString("weft_build_run_reader"), BuildRunDatabaseProfile.Reader);
                await using var dispatcher = new PostgresBuildRunStore(test.RegressionConnectionString("weft_build_dispatcher"), BuildRunDatabaseProfile.Dispatcher);
                Assert.Equal(BuildRunSubmitOutcome.Created, await writer.SubmitAsync(control.Actor, control.Intent));
                var claim = (await dispatcher.ClaimAsync(control.Intent.Revision.OrganizationId, control.Intent.BuildId, Guid.NewGuid()))!;
                await using var publishing = new PostgresBuildStore(control.Connection, true, claim);
                var bytes = new BuildBytes([2, 3], control.Intent.Profile.MaximumArtifactBytes);
                var artifact = BuildArtifactReference.Restore(control.Intent, bytes.Digest, bytes.Length);
                var publication = new BuildPublication(control.Actor.PrincipalId, artifact, new(PrivateObjectId.Restore(control.Intent.Revision,
                    control.Intent.BuildId, PrivateObjectClass.DerivedArtifact), bytes.Digest, bytes.Length));
                Assert.NotNull(await publishing.StageAsync(control.Actor, new(publication, bytes)));
                var objects = new PostgresBuildObjectStore(publishing, new NoNative(), control.Actor);
                using (var input = new MemoryStream(bytes.Copy(), false)) await objects.PublishCreateOnlyAsync(control.Intent.Revision, publication.Object, input);
                Assert.Equal(BuildPublicationOutcome.Created, await publishing.PublishAsync(control.Actor, publication));
                Assert.True(await dispatcher.CompleteAsync(claim, new(BuildExecutionOutcome.Created, publication)));
                var connection = test.RegressionConnectionString("weft_qualification_writer");
                store = new(connection, true);
                var context = new Context
                {
                    Control = control,
                    Store = store,
                    Connection = connection,
                    Build = (await reader.ReadAsync(control.Actor, control.Intent.Revision.OrganizationId, control.Intent.BuildId))!
                };
                context.evidence = (await control.Runs.Reader.ReadAsync(control.Actor, control.Runs.Organization, control.Runs.RunId))!;
                context.Qualification = context.Make(Guid.NewGuid());
                return context;
            }
            catch { if (store is not null) await store.DisposeAsync(); await control.DisposeAsync(); throw; }
        }
        internal async Task<int> Raw(byte[] proof, byte[] result)
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Connection) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using (var identity = new NpgsqlCommand("SELECT set_config('weft.organization_id',@org,true),set_config('weft.build_principal_id',@actor,true)", connection, tx))
            {
                identity.Parameters.AddWithValue("org", Mapping.OrganizationId.ToString("D"));
                identity.Parameters.AddWithValue("actor", Actor.PrincipalId.Value.ToString("D"));
                await identity.ExecuteNonQueryAsync();
            }
            await using var command = new NpgsqlCommand("SELECT weft.write_qualification(@org,@project,@mapping,@id,@proof,@result)", connection, tx);
            command.Parameters.AddWithValue("org", Mapping.OrganizationId); command.Parameters.AddWithValue("project", Mapping.ProjectId);
            command.Parameters.AddWithValue("mapping", Mapping.MappingId); command.Parameters.AddWithValue("id", Qualification.Id);
            command.Parameters.AddWithValue("proof", proof); command.Parameters.AddWithValue("result", result);
            var outcome = (int)(await command.ExecuteScalarAsync())!;
            await tx.CommitAsync();
            return outcome;
        }
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); await Control.DisposeAsync(); }
    }
    private sealed class NoNative : IPrivateObjectStore
    {
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream bytes, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken token = default) => throw new NotSupportedException();
    }
}
