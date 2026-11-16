using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual PostgreSQL evidence/privilege tests; synthetic artifacts are not runtime qualification.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BuildQualificationPersistenceTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData("\t")]
    [InlineData(" ")]
    public async Task Csv_approval_and_qualification_persist_actual_sql_for_whitespace_dialects(string delimiter)
    {
        var dialect = new CsvDocumentDialect(CsvDocumentPolicy.DialectSchema, delimiter, "\"", false, false, false, true);
        await using var test = await Context.CreateAsync(fixture, csvDialect: dialect);
        Assert.Equal(BuildControlOutcome.Replayed, await test.Control.Approve());
        Assert.Equal(BuildQualificationWriteOutcome.Created, await test.Store.SaveAsync(test.Actor, test.Qualification));
        Assert.Equal(BuildQualificationWriteOutcome.Replayed, await test.Store.SaveAsync(test.Actor, test.Qualification));
        var restored = (await test.Store.ReadAsync(test.Actor, test.Mapping, test.Qualification.Id))!;
        Assert.Equal(test.Qualification.CopyCanonicalBytes(), restored.CopyCanonicalBytes());
        Assert.Equal(test.Qualification.CopyResultBytes(), restored.CopyResultBytes());
        Assert.Equal(dialect, new RegressionRunResultCodec().Decode(restored.Id, test.Control.Runs.Value,
            restored.CopyResultBytes()).Outputs!.Outputs.Single().CsvDialect);
    }
    [PostgresFact]
    public Task Document_migration_preserves_legacy_qualification_and_result_bytes() => DocumentUpgradeAsync(24);

    [PostgresFact]
    public Task Document_migration_preserves_audited_legacy_qualification_and_result_bytes() => DocumentUpgradeAsync(25);

    [PostgresFact]
    public async Task Interchange_environment_selection_replays_exact_identity_and_refuses_unknown_or_mismatched_profiles()
    {
        await using var test = await Context.CreateAsync(fixture, documents: true);
        await using var metadata = new PostgresBuildMetadataReader(test.Case.RegressionConnectionString("braid_build_metadata_reader"));
        await using var database = new PostgresDatabase(test.Case.RuntimeConnectionString);
        var service = new BuildAdministrationService(metadata, test.Control.Store, test.Control.Store, new PostgresProjectReader(database),
            FixtureBuildCompiler.Profile, Braid.MappingBackends.FerruleBuildCompiler.DocumentProfile, Braid.MappingBackends.FerruleBuildCompiler.X12InterchangesProfile);
        var reference = new BuildEnvironmentReference(test.Control.Intent.Environment.Project, Guid.NewGuid(), 1);
        var profile = Braid.MappingBackends.FerruleBuildCompiler.X12InterchangesProfile;
        var selected = new BuildEnvironmentSelection(profile.Id, profile.Version);
        Assert.Equal(BuildEnvironmentRequestOutcome.Created, (await service.PutEnvironmentAsync(test.Actor, reference, selected)).Outcome);
        Assert.Equal(profile, (await metadata.ReadEnvironmentAsync(test.Actor, reference))!.Environment.Profile);
        Assert.Equal(BuildEnvironmentRequestOutcome.Replayed, (await service.PutEnvironmentAsync(test.Actor, reference, selected)).Outcome);
        Assert.Equal(BuildEnvironmentRequestOutcome.Conflict, (await service.PutEnvironmentAsync(test.Actor, reference, new(profile.Id, 2))).Outcome);
        Assert.Equal(BuildEnvironmentRequestOutcome.PreconditionFailed, (await service.PutEnvironmentAsync(test.Actor, new(reference.Project, reference.EnvironmentId, 2), new("unsupported", 1))).Outcome);
    }

    [PostgresFact]
    public async Task Interchange_profile_upgrade_preserves_populated_state_and_enables_only_the_explicit_profile()
    {
        await using var test = await Context.CreateAsync(fixture, documents: true, migrations: 34, interchanges: true);
        Assert.Equal(BuildQualificationWriteOutcome.Refused, await test.Store.SaveAsync(test.Actor, test.Qualification));
        var before = await test.Case.ScalarAsync<string>("""
            SELECT jsonb_build_object('environments',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM braid.build_environments t),
                'approvals',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM braid.build_approvals t),
                'runs',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM braid.build_runs t),
                'publications',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM braid.build_publications t),
                'history',(SELECT jsonb_agg(to_jsonb(t) ORDER BY version) FROM braid.schema_migrations t WHERE version<=34))::text
            """);
        await test.Store.DisposeAsync();
        await test.Case.MigrateAsync();
        Assert.Equal(before, await test.Case.ScalarAsync<string>("""
            SELECT jsonb_build_object('environments',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM braid.build_environments t),
                'approvals',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM braid.build_approvals t),
                'runs',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM braid.build_runs t),
                'publications',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM braid.build_publications t),
                'history',(SELECT jsonb_agg(to_jsonb(t) ORDER BY version) FROM braid.schema_migrations t WHERE version<=34))::text
            """));
        Assert.Equal(RegressionSchemaTestContract.X12InterchangeBuildsChecksum, await test.Case.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=35"));
        await using var reopened = new PostgresBuildQualificationStore(test.Connection, true);
        Assert.Equal(BuildQualificationWriteOutcome.Created, await reopened.SaveAsync(test.Actor, test.Qualification));
        Assert.Equal(test.Qualification.CopyCanonicalBytes(), (await reopened.ReadAsync(test.Actor, test.Mapping, test.Qualification.Id))!.CopyCanonicalBytes());
    }

    private async Task DocumentUpgradeAsync(int prefix)
    {
        await using var test = await Context.CreateAsync(fixture, migrations: prefix);
        Assert.Equal(BuildQualificationWriteOutcome.Created, await test.Store.SaveAsync(test.Actor, test.Qualification));
        var proof = test.Qualification.CopyCanonicalBytes();
        var result = test.Qualification.CopyResultBytes();
        var journal = await test.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM braid.schema_migrations r");
        var native = await test.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text)::text FROM braid.mapping_revisions r");
        await test.Store.DisposeAsync();
        await test.Case.MigrateAsync();
        Assert.Equal(journal, await test.Case.ScalarAsync<string>(
            "SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM braid.schema_migrations r WHERE version<=$1", prefix));
        Assert.Equal(native, await test.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text)::text FROM braid.mapping_revisions r"));
        Assert.Equal(RegressionSchemaTestContract.DocumentPreviewsChecksum,
            await test.Case.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=34"));
        await using var reopened = new PostgresBuildQualificationStore(test.Connection, true);
        var saved = (await reopened.ReadAsync(test.Actor, test.Mapping, test.Qualification.Id))!;
        Assert.Equal(proof, saved.CopyCanonicalBytes());
        Assert.Equal(result, saved.CopyResultBytes());
        Assert.Equal(BuildQualificationWriteOutcome.Replayed, await reopened.SaveAsync(test.Actor, saved));
    }

    [PostgresFact]
    public async Task Document_qualification_reopens_exact_bytes_and_media_and_refuses_media_substitution()
    {
        await using var test = await Context.CreateAsync(fixture, documents: true);
        Assert.Equal(BuildQualificationWriteOutcome.Created, await test.Store.SaveAsync(test.Actor, test.Qualification));
        var saved = (await test.Store.ReadAsync(test.Actor, test.Mapping, test.Qualification.Id))!;
        Assert.Equal(test.Qualification.CopyResultBytes(), saved.CopyResultBytes());
        Assert.Equal("application/edi-x12", Assert.Single(new RegressionRunResultCodec().Decode(saved.Id,
            test.Control.Runs.Value, saved.CopyResultBytes()).Outputs!.Outputs).MediaType);
        static (byte[] Proof, byte[] Result) SubstituteMedia(BuildQualification qualification)
        {
            var result = JsonNode.Parse(qualification.CopyResultBytes())!;
            result["outputs"]![0]!["mediaType"] = "application/edifact";
            var bytes = Encoding.UTF8.GetBytes(result.ToJsonString());
            var proof = JsonNode.Parse(qualification.CopyCanonicalBytes())!;
            proof["resultDigest"] = Sha256Digest.FromBytes(bytes).Value;
            proof["resultBytes"] = bytes.Length;
            return (Encoding.UTF8.GetBytes(proof.ToJsonString()), bytes);
        }
        // An existing key must preserve its original proof before any new-proof
        // validation. A fresh key exercises media validation rather than replay conflict.
        var changed = SubstituteMedia(test.Qualification);
        Assert.Equal(2, await test.Raw(changed.Proof, changed.Result));
        var candidate = test.Make(Guid.NewGuid());
        var forged = SubstituteMedia(candidate);
        Assert.Equal(3, await test.Raw(forged.Proof, forged.Result, candidate.Id));
        Assert.Null(await test.Store.ReadAsync(test.Actor, test.Mapping, candidate.Id));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_qualifications"));
        Assert.Equal(BuildQualificationWriteOutcome.Created, await test.Store.SaveAsync(test.Actor, candidate));
        Assert.Equal(BuildQualificationWriteOutcome.Replayed, await test.Store.SaveAsync(test.Actor, test.Qualification));
        Assert.Equal(saved.CopyResultBytes(), (await test.Store.ReadAsync(test.Actor, test.Mapping, saved.Id))!.CopyResultBytes());
    }

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
        await using var count = new NpgsqlCommand("SELECT count(*) FROM braid.build_qualifications", owner);
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
        await using (var command = new NpgsqlCommand("SELECT 1 FROM braid.principals WHERE id=$1 FOR UPDATE", blocker, transaction))
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
                await using var revoke = new NpgsqlCommand("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", blocker, transaction);
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
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_qualifications"));
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
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_qualifications"));
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
            "table" => "GRANT SELECT ON braid.build_qualifications TO braid_qualification_reader",
            "function" => "GRANT EXECUTE ON FUNCTION braid.build_json_text(text,boolean) TO braid_qualification_reader",
            _ => "ALTER TABLE braid.build_qualifications DISABLE TRIGGER qualification_immutable"
        });
        await using var reader = new PostgresBuildQualificationStore(test.Case.RegressionConnectionString("braid_qualification_reader"), false);
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
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_qualifications"));
    }

    internal sealed class Context : IAsyncDisposable
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
        internal static async Task<Context> CreateAsync(PostgresFixture fixture, bool documents = false, int? migrations = null, bool interchanges = false,
            CsvDocumentDialect? csvDialect = null)
        {
            var control = await BuildPersistenceTests.Context.CreateAsync(fixture, migrations: migrations, documents: documents, interchanges: interchanges, csvDialect: csvDialect);
            PostgresBuildQualificationStore? store = null;
            try
            {
                Assert.Equal(BuildControlOutcome.Created, await control.Approve());
                var test = control.Runs.Cases.Case;
                await using var writer = new PostgresBuildRunStore(test.RegressionConnectionString("braid_build_run_writer"), BuildRunDatabaseProfile.Writer);
                await using var reader = new PostgresBuildRunStore(test.RegressionConnectionString("braid_build_run_reader"), BuildRunDatabaseProfile.Reader);
                await using var dispatcher = new PostgresBuildRunStore(test.RegressionConnectionString("braid_build_dispatcher"), BuildRunDatabaseProfile.Dispatcher);
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
                var connection = test.RegressionConnectionString("braid_qualification_writer");
                store = new(connection, true);
                var context = new Context
                {
                    Control = control,
                    Store = store,
                    Connection = connection,
                    Build = (await reader.ReadAsync(control.Actor, control.Intent.Revision.OrganizationId, control.Intent.BuildId))!
                };
                context.evidence = (await control.Runs.ReadAsync(control.Actor, control.Runs.Organization, control.Runs.RunId))!;
                context.Qualification = context.Make(Guid.NewGuid());
                return context;
            }
            catch { if (store is not null) await store.DisposeAsync(); await control.DisposeAsync(); throw; }
        }
        internal async Task<int> Raw(byte[] proof, byte[] result, Guid? id = null)
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Connection) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using (var identity = new NpgsqlCommand("SELECT set_config('braid.organization_id',@org,true),set_config('braid.build_principal_id',@actor,true)", connection, tx))
            {
                identity.Parameters.AddWithValue("org", Mapping.OrganizationId.ToString("D"));
                identity.Parameters.AddWithValue("actor", Actor.PrincipalId.Value.ToString("D"));
                await identity.ExecuteNonQueryAsync();
            }
            await using var command = new NpgsqlCommand("SELECT braid.write_qualification(@org,@project,@mapping,@id,@proof,@result)", connection, tx);
            command.Parameters.AddWithValue("org", Mapping.OrganizationId); command.Parameters.AddWithValue("project", Mapping.ProjectId);
            command.Parameters.AddWithValue("mapping", Mapping.MappingId); command.Parameters.AddWithValue("id", id ?? Qualification.Id);
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
