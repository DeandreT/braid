using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Real PostgreSQL release transitions over synthetic qualified artifacts; actual invocation is a separate live gate.</summary>
[Collection(PostgresCollection.Name)]
public sealed class LocalReleasePersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Replica_rosters_and_receipts_have_the_exact_narrow_schema()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using var runner = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(49).ToArray());
        await runner.MigrateAsync();
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var setting = new NpgsqlCommand("SET LOCAL search_path=pg_catalog", connection, tx);
        await setting.ExecuteNonQueryAsync();
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, tx, ["release_replica_rosters", "release_replica_receipts"], default);
        Assert.Equal(LocalReleaseRoleGuard.ReplicaFingerprint, fingerprint);
        Assert.Equal(LocalReleaseRoleGuard.ReplicaMigrationChecksum, MigrationRunner.EmbeddedMigrations[48].Checksum);
    }
    [PostgresFact]
    public async Task Activation_rollback_and_superseded_retry_preserve_the_original_artifacts_and_audit()
    {
        await using var test = await Context.CreateAsync(fixture);
        await using var runs = new PostgresBuildRunStore(test.Case.RegressionConnectionString("braid_build_run_reader"), BuildRunDatabaseProfile.Reader);
        await using var builds = new PostgresBuildStore(test.Case.RegressionConnectionString("braid_build_reader"), false);
        await using var access = new PostgresDatabase(test.Case.RuntimeConnectionString);
        await using var reports = new PostgresRevisionReportDatabase(test.Case.ReportConnectionString);
        await using var evidence = new PostgresRegressionRunStore(test.Case.RegressionConnectionString("braid_run_reader"), false);
        await using var cases = new PostgresRegressionCaseStore(test.Case.RegressionConnectionString("braid_case_reader"), RegressionCaseDatabaseProfile.Reader);
        var projects = new PostgresProjectReader(access);
        var runtime = new RecordingRuntime(test);
        var execution = new CompiledBuildExecution(runs, new BuildControlService(builds, projects, new PostgresRevisionReportMetadataReader(reports),
            new RegressionRunReadService(evidence, cases)), builds, new PostgresBuildObjectStore(builds, new NoNative(), test.Actor), runtime);
        var host = new LocalReleaseInvocation(test.Store, execution);
        async Task Invoke(byte[] expected)
        {
            Assert.Equal(CompiledBuildOutcome.Completed, (await host.ExecuteAsync(test.Actor, test.Target.Reference, test.Control.Runs.Value.Replay)).Outcome);
            Assert.Equal(expected, runtime.Last);
        }
        var first = await test.Request(test.First, null);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Apply(first));
        await Invoke([2, 3]);
        var secondQualification = await test.Build([4, 5]);
        var published = (await builds.ReadPublishedPackageAsync(test.Actor, test.Target.Reference.Mapping.OrganizationId, secondQualification.Artifact.Intent.BuildId))!;
        var package = new MappingPackage(published.Publication.Manifest, published.Bytes);
        var service = new BuildQualificationService(test.Qualifications, runs, evidence, cases, execution, projects);
        var id = Guid.NewGuid();
        Task<BuildQualificationRequestResult> Stage() => service.PutAsync(test.Actor, test.Target.Reference.Mapping, id, secondQualification.Artifact.Intent.BuildId, package: package);
        runtime.Refuse = true;
        Assert.Equal(BuildQualificationRequestOutcome.NotQualified, (await Stage()).Outcome);
        Assert.Null(await test.Qualifications.ReadAsync(test.Actor, test.Target.Reference.Mapping, id));
        runtime.Refuse = false;
        await Invoke([2, 3]);
        var staged = await Stage();
        Assert.Equal(BuildQualificationRequestOutcome.Created, staged.Outcome);
        var second = await test.Request(staged.Qualification!, first.ReleaseId);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Apply(second));
        await Invoke([4, 5]);
        var rollback = await test.Request(test.First, second.ReleaseId, ReleaseAction.Rollback, first.ReleaseId);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Apply(rollback));
        await Invoke([2, 3]);
        Assert.Equal(rollback.ReleaseId, (await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.ActiveReleaseId);
        var restored = (await test.Store.ReadAsync(test.Actor, test.Target.Reference, rollback.ReleaseId))!;
        Assert.Equal(test.First.Artifact.ContentDigest, restored.Intent.Qualification.Artifact.ContentDigest);
        Assert.Equal(ReleaseTransitionOutcome.Replayed, await test.Apply(first));
        Assert.Equal(rollback.ReleaseId, (await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.ActiveReleaseId);
        foreach (var intent in new[] { first, second, rollback })
            Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.local_release_audit WHERE release_id=$1", intent.ReleaseId));
        Assert.Equal(3L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_qualifications"));
    }

    internal sealed class RecordingRuntime(Context test) : ICompiledBuildRuntime
    {
        internal byte[]? Last;
        internal bool Refuse;
        public string RuntimeImage => test.Target.RuntimeImage;
        public Task<CompiledBuildResult> ExecuteAsync(BuildArtifactReference artifact, BuildBytes bytes, RegressionReplay replay, CancellationToken token)
        {
            Last = bytes.Copy();
            return Task.FromResult(Refuse ? new CompiledBuildResult(CompiledBuildOutcome.ExecutionFailed) :
                new(CompiledBuildOutcome.Completed, test.Control.Runs.Value.Expected));
        }
    }

    [RegressionPostgresTheory]
    [InlineData(49)]
    [InlineData(null)]
    public async Task Replicas_must_independently_confirm_the_candidate_and_remain_ready_at_apply(int? migrations)
    {
        await using var test = await Context.CreateAsync(fixture, migrations: migrations);
        var target = test.Target.Reference;
        var actor = test.Actor;
        var one = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var two = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        string Verifier(byte[] secret) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(secret));
        Assert.True(await test.Store.RegisterReplicasAsync(actor, test.Target, [Verifier(one), Verifier(two)]));
        Assert.False(await test.Store.RegisterReplicasAsync(actor, test.Target, [Verifier(one)]));
        var a = test.First;
        var b = await test.Build([4, 5]);
        async Task<Guid> SecondProof(BuildQualification proof)
        {
            await using var runs = new PostgresBuildRunStore(test.Case.RegressionConnectionString("braid_build_run_reader"), BuildRunDatabaseProfile.Reader);
            var run = (await runs.ReadAsync(actor, target.Mapping.OrganizationId, proof.Artifact.Intent.BuildId))!;
            var evidence = (await test.Control.Runs.ReadAsync(actor, target.Mapping.OrganizationId, run.Intent.RegressionEvidenceId))!;
            var copy = BuildQualification.Create(Guid.NewGuid(), actor.PrincipalId, run, evidence, test.Control.Runs.Value, test.Target.RuntimeImage,
                new(CompiledBuildOutcome.Completed, test.Control.Runs.Value.Expected));
            Assert.Equal(BuildQualificationWriteOutcome.Created, await test.Qualifications.SaveAsync(actor, copy));
            return copy.Id;
        }
        var intent = new ReleaseIntent(Guid.NewGuid(), actor.PrincipalId, target, a, ReleaseAction.Activate, null);
        Assert.False(await test.Store.RecordReplicaAsync(actor, target, a.Id, new byte[32]));
        Assert.True(await test.Store.RecordReplicaAsync(actor, target, a.Id, one));
        Assert.False(await test.Store.RecordReplicaAsync(actor, target, a.Id, two));
        Assert.True(await test.Store.RecordReplicaAsync(actor, target, b.Id, two));
        Assert.Equal(ReleaseWriteOutcome.Refused, await test.Store.RequestAsync(actor, intent));
        Assert.True(await test.Store.RecordReplicaAsync(actor, target, await SecondProof(a), two));
        Assert.Equal(ReleaseWriteOutcome.Created, await test.Store.RequestAsync(actor, intent));
        Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Apply(intent));
        Assert.True(await test.Store.RecordReplicaAsync(actor, target, await SecondProof(b), one));
        var pending = await test.Request(b, intent.ReleaseId);
        Assert.True(await test.Store.RecordReplicaAsync(actor, target, null, two));
        Assert.Equal(ReleaseTransitionOutcome.Refused, await test.Apply(pending));
        Assert.Equal(intent.ReleaseId, (await test.Store.ReadTargetAsync(actor, target))!.ActiveReleaseId);
        Assert.True(await test.Store.RecordReplicaAsync(actor, target, await SecondProof(b), two));
        var next = await test.Request(b, intent.ReleaseId);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Apply(next));
        var rollback = new ReleaseIntent(Guid.NewGuid(), actor.PrincipalId, target, a, ReleaseAction.Rollback, next.ReleaseId, intent.ReleaseId);
        Assert.Equal(ReleaseWriteOutcome.Refused, await test.Store.RequestAsync(actor, rollback));
        Assert.True(await test.Store.RecordReplicaAsync(actor, target, await SecondProof(a), two));
        Assert.Equal(ReleaseWriteOutcome.Created, await test.Store.RequestAsync(actor, rollback));
        Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Apply(rollback));
    }

    [PostgresFact]
    public async Task Concurrent_activations_have_one_winner_and_a_retained_rejection()
    {
        await using var test = await Context.CreateAsync(fixture);
        var first = await test.Request(test.First, null);
        var second = await test.Request(test.First, null);
        await using var other = test.NewStore();
        var results = await Task.WhenAll(test.Apply(first), other.ApplyAsync(test.Actor, test.Target.Reference, second.ReleaseId));
        Assert.Contains(ReleaseTransitionOutcome.Activated, results); Assert.Contains(ReleaseTransitionOutcome.Conflict, results);
        var active = (await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.ActiveReleaseId;
        var rejected = (await test.Store.ReadAsync(test.Actor, test.Target.Reference, active == first.ReleaseId ? second.ReleaseId : first.ReleaseId))!;
        Assert.Equal(ReleasePhase.Failed, rejected.Phase);
        Assert.Equal(ReleaseRejection.StaleActiveRelease, rejected.Audit[1].Rejection);
        Assert.Equal(ReleaseTransitionOutcome.Conflict, await test.Apply(rejected.Intent));
        Assert.Equal(active, (await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.ActiveReleaseId);
    }

    [PostgresFact]
    public async Task Cancellation_and_approval_revocation_never_update_the_pointer()
    {
        await using var test = await Context.CreateAsync(fixture);
        var canceled = await test.Request(test.First, null);
        Assert.Equal(ReleaseTransitionOutcome.Canceled, await test.Store.CancelAsync(test.Actor, test.Target.Reference, canceled.ReleaseId));
        Assert.Equal(ReleaseTransitionOutcome.Canceled, await test.Apply(canceled));
        var denied = await test.Request(test.First, null);
        await test.Control.Store.RevokeAsync(test.Actor, test.Target.Reference.Mapping.OrganizationId,
            test.Target.Reference.Mapping.ProjectId, test.First.Artifact.Intent.ApprovalId);
        Assert.Equal(ReleaseTransitionOutcome.Refused, await test.Apply(denied));
        var snapshot = (await test.Store.ReadAsync(test.Actor, test.Target.Reference, denied.ReleaseId))!;
        Assert.Equal(ReleasePhase.Failed, snapshot.Phase); Assert.Equal(ReleaseRejection.ApprovalRevoked, snapshot.Audit[1].Rejection);
        Assert.Null((await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.ActiveReleaseId);
        Assert.Equal(ReleaseWriteOutcome.Replayed, await test.Store.RequestAsync(test.Actor, denied));
    }

    [PostgresFact]
    public async Task Disabled_registration_is_retained_and_cannot_be_reactivated_by_replay()
    {
        await using var test = await Context.CreateAsync(fixture);
        var intent = await test.Request(test.First, null);
        Assert.True(await test.Store.DisableAsync(test.Actor, test.Target.Reference));
        Assert.Equal(ReleaseWriteOutcome.Replayed, await test.Store.RegisterAsync(test.Actor, test.Target));
        Assert.False((await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.Enabled);
        Assert.Equal(ReleaseTransitionOutcome.Refused, await test.Apply(intent));
        Assert.Equal(ReleaseRejection.TargetUnavailable, (await test.Store.ReadAsync(test.Actor, test.Target.Reference, intent.ReleaseId))!.Audit[1].Rejection);
    }

    [PostgresFact]
    public async Task Lost_activation_acknowledgment_recovers_without_reapplying_the_pointer()
    {
        await using var test = await Context.CreateAsync(fixture);
        var intent = await test.Request(test.First, null);
        await using var relay = new ServerSessionCommitAckLossRelay(test.Connection);
        await using var routed = new PostgresLocalReleaseStore(relay.RoutedConnectionString, true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pending = routed.ApplyAsync(test.Actor, test.Target.Reference, intent.ReleaseId, deadline.Token);
        try
        {
            await Task.WhenAny(pending, relay.CommitAcknowledgementWithheld).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            Assert.True(relay.CommitAcknowledgementWithheld.IsCompletedSuccessfully && !pending.IsCompleted);
            Assert.Equal(intent.ReleaseId, (await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference, deadline.Token))!.ActiveReleaseId);
            relay.DropAcknowledgement();
            await Assert.ThrowsAnyAsync<NpgsqlException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token));
            Assert.Equal(ReleaseTransitionOutcome.Replayed, await test.Apply(intent));
            Assert.Equal(2, (await test.Store.ReadAsync(test.Actor, test.Target.Reference, intent.ReleaseId))!.Audit.Count);
        }
        finally
        {
            relay.DropAcknowledgement(); deadline.Cancel();
            try { await pending; } catch (Exception failure) when (failure is NpgsqlException or OperationCanceledException) { }
        }
    }

    [PostgresFact]
    public async Task Restart_keeps_active_release_and_private_provenance_without_new_effects()
    {
        await using var test = await Context.CreateAsync(fixture);
        var intent = await test.Request(test.First, null);
        await test.Apply(intent);
        await test.Store.DisposeAsync(); await test.Qualifications.DisposeAsync(); await test.Control.Store.DisposeAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var reopened = new PostgresLocalReleaseStore(test.Connection, true);
        Assert.Equal(intent.ReleaseId, (await reopened.ReadTargetAsync(test.Actor, test.Target.Reference))!.ActiveReleaseId);
        var record = (await reopened.ReadAsync(test.Actor, test.Target.Reference, intent.ReleaseId))!;
        Assert.Equal(intent.Fingerprint, record.Intent.Fingerprint); Assert.Equal(2, record.Audit.Count);
        Assert.Equal(ReleaseTransitionOutcome.Replayed, await reopened.ApplyAsync(test.Actor, test.Target.Reference, intent.ReleaseId));
    }

    [PostgresFact]
    public async Task Another_publisher_cannot_adopt_private_release_and_reader_cannot_mutate()
    {
        await using var test = await Context.CreateAsync(fixture);
        var intent = await test.Request(test.First, null);
        var other = new ActorContext(new(test.Control.Runs.Cases.Seed.Admitted));
        var mapping = test.Target.Reference.Mapping;
        await test.Control.Runs.Cases.Seed.AddMembershipAsync(test.Case, mapping.OrganizationId, mapping.ProjectId, other.PrincipalId.Value, "publisher");
        Assert.Null(await test.Store.ReadAsync(other, test.Target.Reference, intent.ReleaseId));
        Assert.Equal(ReleaseTransitionOutcome.Refused, await test.Store.ApplyAsync(other, test.Target.Reference, intent.ReleaseId));
        await using var reader = new PostgresLocalReleaseStore(test.Case.RegressionConnectionString("braid_release_reader"), false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.RequestAsync(test.Actor, intent));
        var failure = await Assert.ThrowsAsync<PostgresException>(() => test.Case.ExecuteAsync("DELETE FROM braid.local_release_audit"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, failure.SqlState);
        Assert.Single((await test.Store.ReadAsync(test.Actor, test.Target.Reference, intent.ReleaseId))!.Audit);
    }

    [PostgresFact]
    public async Task Registration_remains_administrative_and_each_release_operation_uses_current_authority()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Control.Runs.Cases.RoleAsync("publisher");
        Assert.Equal(ReleaseWriteOutcome.Refused, await test.Store.RegisterAsync(test.Actor, test.Target));
        Assert.False(await test.Store.DisableAsync(test.Actor, test.Target.Reference));
        var intent = await test.Request(test.First, null);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Apply(intent));
        await test.Control.Runs.Cases.RoleAsync("viewer");
        Assert.Null(await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference));
        Assert.Null(await test.Store.ReadAsync(test.Actor, test.Target.Reference, intent.ReleaseId));
        Assert.Equal(ReleaseTransitionOutcome.Refused, await test.Apply(intent));
        await test.Control.Runs.Cases.RoleAsync("administrator");
        Assert.Equal(intent.ReleaseId, (await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.ActiveReleaseId);
    }

    [PostgresFact]
    public async Task Foreign_organization_project_mapping_and_environment_do_not_expose_or_apply_a_release()
    {
        await using var test = await Context.CreateAsync(fixture);
        var intent = await test.Request(test.First, null);
        var reference = test.Target.Reference; var mapping = reference.Mapping;
        foreach (var field in new[] { "organization", "project", "mapping", "environment" })
        {
            var org = field == "organization" ? Guid.NewGuid() : mapping.OrganizationId;
            var project = field == "project" ? Guid.NewGuid() : mapping.ProjectId;
            var changed = new ReleaseTargetReference(new(org, project, field == "mapping" ? Guid.NewGuid() : mapping.MappingId),
                new(new(org, project), reference.Environment.EnvironmentId, field == "environment" ? reference.Environment.Version + 1 : reference.Environment.Version), reference.TargetId);
            Assert.Null(await test.Store.ReadTargetAsync(test.Actor, changed));
            Assert.Null(await test.Store.ReadAsync(test.Actor, changed, intent.ReleaseId));
            Assert.Equal(ReleaseTransitionOutcome.Refused, await test.Store.ApplyAsync(test.Actor, changed, intent.ReleaseId));
        }
        Assert.Null((await test.Store.ReadTargetAsync(test.Actor, reference))!.ActiveReleaseId);
    }

    [RegressionPostgresTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task One_missing_target_does_not_block_independent_releases_and_only_that_request_needs_correction(int failed)
    {
        await using var test = await Context.CreateAsync(fixture);
        var targets = Enumerable.Range(0, 3).Select(_ => new LocalReleaseTarget(
            new(test.Target.Reference.Mapping, test.Target.Reference.Environment, Guid.NewGuid()), test.Target.Profile, test.Target.RuntimeImage)).ToArray();
        var intents = targets.Select(t => new ReleaseIntent(Guid.NewGuid(), test.Actor.PrincipalId, t.Reference, test.First, ReleaseAction.Activate, null)).ToArray();
        for (var index = 0; index < targets.Length; index++)
        {
            if (index != failed) Assert.Equal(ReleaseWriteOutcome.Created, await test.Store.RegisterAsync(test.Actor, targets[index]));
            Assert.Equal(index == failed ? ReleaseWriteOutcome.Refused : ReleaseWriteOutcome.Created, await test.Store.RequestAsync(test.Actor, intents[index]));
            if (index != failed) Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Store.ApplyAsync(test.Actor, targets[index].Reference, intents[index].ReleaseId));
        }
        Assert.Equal(ReleaseWriteOutcome.Created, await test.Store.RegisterAsync(test.Actor, targets[failed]));
        for (var index = 0; index < targets.Length; index++)
        {
            Assert.Equal(index == failed ? ReleaseWriteOutcome.Created : ReleaseWriteOutcome.Replayed, await test.Store.RequestAsync(test.Actor, intents[index]));
            Assert.Equal(index == failed ? ReleaseTransitionOutcome.Activated : ReleaseTransitionOutcome.Replayed,
                await test.Store.ApplyAsync(test.Actor, targets[index].Reference, intents[index].ReleaseId));
            Assert.Equal(2, (await test.Store.ReadAsync(test.Actor, targets[index].Reference, intents[index].ReleaseId))!.Audit.Count);
        }
    }

    [RegressionPostgresTheory]
    [InlineData("table")]
    [InlineData("trigger")]
    public async Task Broadened_database_contracts_are_refused(string change)
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Case.ExecuteAsync(change == "table" ? "GRANT SELECT ON braid.local_releases TO braid_release_reader" :
            "ALTER TABLE braid.local_releases DISABLE TRIGGER local_release_history");
        await using var reader = new PostgresLocalReleaseStore(test.Case.RegressionConnectionString("braid_release_reader"), false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadTargetAsync(test.Actor, test.Target.Reference));
    }

    [PostgresFact]
    public async Task Null_rollback_action_is_refused_even_when_the_other_selectors_are_valid()
    {
        await using var test = await Context.CreateAsync(fixture);
        var first = await test.Request(test.First, null); await test.Apply(first);
        var second = await test.Request(test.First, first.ReleaseId); await test.Apply(second);
        var invalid = new ReleaseIntent(Guid.NewGuid(), test.Actor.PrincipalId, test.Target.Reference, test.First,
            ReleaseAction.Rollback, second.ReleaseId, first.ReleaseId);
        var body = JsonNode.Parse(invalid.CopyCanonicalBytes())!; body["action"] = null;
        Assert.Equal(3, await test.Raw(invalid.ReleaseId, Encoding.UTF8.GetBytes(body.ToJsonString())));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.local_releases"));
        Assert.Equal(second.ReleaseId, (await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.ActiveReleaseId);
    }

    [PostgresFact]
    public async Task Noncanonical_UUID_selectors_cannot_poison_a_recoverable_release()
    {
        await using var test = await Context.CreateAsync(fixture);
        var first = await test.Request(test.First, null); await test.Apply(first);
        var second = await test.Request(test.First, first.ReleaseId); await test.Apply(second);
        foreach (var field in new[] { "qualificationId", "expectedActiveReleaseId", "rollbackOfReleaseId" })
        {
            var invalid = new ReleaseIntent(Guid.NewGuid(), test.Actor.PrincipalId, test.Target.Reference, test.First,
                ReleaseAction.Rollback, second.ReleaseId, first.ReleaseId);
            var body = JsonNode.Parse(invalid.CopyCanonicalBytes())!;
            body[field] = "{" + body[field]!.GetValue<string>() + "}";
            Assert.Equal(3, await test.Raw(invalid.ReleaseId, Encoding.UTF8.GetBytes(body.ToJsonString())));
        }
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.local_releases"));
        Assert.Equal(second.ReleaseId, (await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.ActiveReleaseId);
    }

    [PostgresFact]
    public async Task Null_scope_identity_and_operation_cannot_authorize_mutations()
    {
        await using var test = await Context.CreateAsync(fixture);
        var intent = new ReleaseIntent(Guid.NewGuid(), test.Actor.PrincipalId, test.Target.Reference, test.First, ReleaseAction.Activate, null);
        var invalid = JsonNode.Parse(intent.CopyCanonicalBytes())!; invalid["environmentId"] = null;
        Assert.Equal(3, await test.Raw(intent.ReleaseId, Encoding.UTF8.GetBytes(invalid.ToJsonString()), nullField: "environment"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.local_releases"));
        Assert.Equal(3, await test.Raw(Guid.Empty, [], bindActor: false, kind: "target", operation: "disable"));
        Assert.Equal(3, await test.Raw(Guid.Empty, [], kind: "target"));
        Assert.True((await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.Enabled);
        Assert.Equal(ReleaseWriteOutcome.Created, await test.Store.RequestAsync(test.Actor, intent));
        foreach (var field in new[] { "org", "project", "mapping", "environment", "version", "target" })
            Assert.Equal(4, await test.Raw(intent.ReleaseId, [], nullField: field, kind: "transition", operation: "apply"));
        Assert.Equal(4, await test.Raw(intent.ReleaseId, [], bindActor: false, kind: "transition", operation: "apply"));
        Assert.Equal(4, await test.Raw(intent.ReleaseId, [], kind: "transition"));
        Assert.Null((await test.Store.ReadTargetAsync(test.Actor, test.Target.Reference))!.ActiveReleaseId);
        Assert.Equal(ReleasePhase.Requested, (await test.Store.ReadAsync(test.Actor, test.Target.Reference, intent.ReleaseId))!.Phase);
    }

    internal sealed class Context : IAsyncDisposable
    {
        internal required BuildPersistenceTests.Context Control { get; init; }
        internal required PostgresBuildQualificationStore Qualifications { get; init; }
        internal required PostgresLocalReleaseStore Store { get; init; }
        internal required LocalReleaseTarget Target { get; init; }
        internal required string Connection { get; init; }
        internal BuildQualification First { get; private set; } = null!;
        internal PostgresCase Case => Control.Runs.Cases.Case;
        internal ActorContext Actor => Control.Actor;
        internal static async Task<Context> CreateAsync(PostgresFixture fixture, int? migrations = null, bool documents = false, bool shipment = false,
            byte[]? nativeBytes = null, BuildBackendIdentity? actualBackend = null, string? runtimeImage = null, byte[]? xmlExpected = null, bool contrl = false, BuildPersistenceTests.ByteCase? bytesCase = null)
        {
            var control = await BuildPersistenceTests.Context.CreateAsync(fixture, migrations: migrations, documents: documents, shipment: shipment, nativeBytes: nativeBytes, actualBackend: actualBackend, xmlExpected: xmlExpected, contrl: contrl, bytesCase: bytesCase);
            PostgresBuildQualificationStore? qualifications = null; PostgresLocalReleaseStore? releases = null;
            try
            {
                qualifications = new(control.Runs.Cases.Case.RegressionConnectionString("braid_qualification_writer"), true);
                var connection = control.Runs.Cases.Case.RegressionConnectionString("braid_release_writer");
                releases = new(connection, true);
                var context = new Context
                {
                    Control = control,
                    Qualifications = qualifications,
                    Store = releases,
                    Connection = connection,
                    Target = new(new(control.Runs.Cases.Mapping, control.Intent.Environment, Guid.NewGuid()), control.Intent.Profile, runtimeImage ?? "sha256:" + new string('f', 64))
                };
                if (nativeBytes is null) context.First = await context.Build([2, 3], control.Intent);
                Assert.Equal(ReleaseWriteOutcome.Created, await releases.RegisterAsync(context.Actor, context.Target));
                return context;
            }
            catch { if (releases is not null) await releases.DisposeAsync(); if (qualifications is not null) await qualifications.DisposeAsync(); await control.DisposeAsync(); throw; }
        }
        internal PostgresLocalReleaseStore NewStore() => new(Connection, true);
        internal async Task<BuildQualification> Build(byte[] bytes, BuildIntent? selected = null)
        {
            var original = Control.Intent;
            var intent = selected ?? new BuildIntent(Guid.NewGuid(), original.Revision, original.NativeDigest, original.Backend, original.Profile,
                original.Boundaries, original.Environment, Guid.NewGuid(), original.RegressionEvidenceId);
            var run = await PublishOnly(intent, bytes);
            var evidence = (await Control.Runs.ReadAsync(Actor, intent.Revision.OrganizationId, intent.RegressionEvidenceId))!;
            var proof = BuildQualification.Create(Guid.NewGuid(), Actor.PrincipalId, run, evidence, Control.Runs.Value, Target.RuntimeImage,
                new(CompiledBuildOutcome.Completed, Control.Runs.Value.Expected));
            Assert.Equal(BuildQualificationWriteOutcome.Created, await Qualifications.SaveAsync(Actor, proof));
            return proof;
        }

        internal async Task<BuildRunSnapshot> PublishOnly(BuildIntent intent, byte[] bytes)
        {
            Assert.Equal(BuildControlOutcome.Created, await Control.Store.ApproveAsync(Actor, new(Actor.PrincipalId, intent, Control.Native, Control.Evidence)));
            await using var runs = new PostgresBuildRunStore(Case.RegressionConnectionString("braid_build_run_writer"), BuildRunDatabaseProfile.Writer);
            await using var dispatcher = new PostgresBuildRunStore(Case.RegressionConnectionString("braid_build_dispatcher"), BuildRunDatabaseProfile.Dispatcher);
            Assert.Equal(BuildRunSubmitOutcome.Created, await runs.SubmitAsync(Actor, intent));
            var claim = (await dispatcher.ClaimAsync(intent.Revision.OrganizationId, intent.BuildId, Guid.NewGuid()))!;
            await using var publishing = new PostgresBuildStore(Control.Connection, true, claim);
            var content = new BuildBytes(bytes, intent.Profile.MaximumArtifactBytes);
            var artifact = BuildArtifactReference.Restore(intent, content.Digest, content.Length);
            var publication = new BuildPublication(Actor.PrincipalId, artifact, new(Braid.Domain.PrivateObjectId.Restore(intent.Revision,
                intent.BuildId, Braid.Domain.PrivateObjectClass.DerivedArtifact), content.Digest, content.Length));
            await publishing.StageAsync(Actor, new(publication, content));
            var objects = new PostgresBuildObjectStore(publishing, new NoNative(), Actor);
            using (var stream = new MemoryStream(content.Copy(), false)) await objects.PublishCreateOnlyAsync(intent.Revision, publication.Object, stream);
            Assert.Equal(BuildPublicationOutcome.Created, await publishing.PublishAsync(Actor, publication));
            Assert.True(await dispatcher.CompleteAsync(claim, new(BuildExecutionOutcome.Created, publication)));
            return (await runs.ReadAsync(Actor, intent.Revision.OrganizationId, intent.BuildId))!;
        }
        internal async Task<ReleaseIntent> Request(BuildQualification proof, Guid? expected, ReleaseAction action = ReleaseAction.Activate, Guid? rollback = null)
        {
            var intent = new ReleaseIntent(Guid.NewGuid(), Actor.PrincipalId, Target.Reference, proof, action, expected, rollback);
            Assert.Equal(ReleaseWriteOutcome.Created, await Store.RequestAsync(Actor, intent));
            return intent;
        }
        internal Task<ReleaseTransitionOutcome> Apply(ReleaseIntent intent) => Store.ApplyAsync(Actor, Target.Reference, intent.ReleaseId);
        internal async Task<int> Raw(Guid id, byte[] body, string? nullField = null, bool bindActor = true,
            string kind = "request", string? operation = null)
        {
            await using var connection = PostgresServerSessionTestSupport.Unpooled(Connection);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using (var identity = new NpgsqlCommand("SELECT set_config('braid.organization_id',@org,true),set_config('braid.build_principal_id',@actor,true)", connection, tx))
            {
                identity.Parameters.AddWithValue("org", Target.Reference.Mapping.OrganizationId.ToString("D"));
                identity.Parameters.AddWithValue("actor", bindActor ? Actor.PrincipalId.Value.ToString("D") : "");
                await identity.ExecuteNonQueryAsync();
            }
            var sql = kind switch
            {
                "target" => "SELECT braid.write_local_target(@op,@org,@project,@mapping,@environment,@version,@target,@body)",
                "transition" => "SELECT braid.transition_local_release(@op,@org,@project,@mapping,@environment,@version,@target,@id)",
                _ => "SELECT braid.write_local_release(@org,@project,@mapping,@environment,@version,@target,@id,@body)"
            };
            await using var command = new NpgsqlCommand(sql, connection, tx);
            foreach (var (name, value) in new[] { ("org", Target.Reference.Mapping.OrganizationId), ("project", Target.Reference.Mapping.ProjectId),
                ("mapping", Target.Reference.Mapping.MappingId), ("environment", Target.Reference.Environment.EnvironmentId), ("target", Target.Reference.TargetId) })
                command.Parameters.AddWithValue(name, NpgsqlTypes.NpgsqlDbType.Uuid, nullField == name ? DBNull.Value : (object)value);
            command.Parameters.AddWithValue("version", NpgsqlTypes.NpgsqlDbType.Bigint, nullField == "version" ? DBNull.Value : (object)Target.Reference.Environment.Version);
            if (kind != "target") command.Parameters.AddWithValue("id", id);
            if (kind != "transition") command.Parameters.AddWithValue("body", body);
            if (kind != "request") command.Parameters.AddWithValue("op", NpgsqlTypes.NpgsqlDbType.Text, (object?)operation ?? DBNull.Value);
            var outcome = (int)(await command.ExecuteScalarAsync())!;
            await tx.CommitAsync();
            return outcome;
        }
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); await Qualifications.DisposeAsync(); await Control.DisposeAsync(); }
    }
    internal sealed class NoNative : IPrivateObjectStore
    {
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(Braid.Domain.RevisionScope scope, Braid.Domain.PrivateObjectReference reference, Stream bytes, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]> ReadVerifiedAsync(Braid.Domain.RevisionScope scope, Braid.Domain.PrivateObjectReference reference, CancellationToken token = default) => throw new NotSupportedException();
    }
}
