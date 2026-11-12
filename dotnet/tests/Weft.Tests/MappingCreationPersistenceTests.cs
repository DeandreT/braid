using Npgsql;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MappingCreationPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Real_44_upgrade_preserves_exact_history_and_installs_45_once()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(44).ToArray())) await prefix.MigrateAsync();
        var before = await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations");
        await test.MigrateAsync(); await test.MigrateAsync();
        Assert.Equal(before, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations WHERE version<=44"));
        Assert.Equal(45L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
    }

    [PostgresFact]
    public async Task Actual_application_create_lost_ack_reconciles_durable_receipt_and_replay()
    {
        await using var f = await Fixture.Create(fixture);
        await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default);
        var ports = new PortablePorts(f.Test);
        var ingestion = new RevisionIngestionService(f.Test.Store, f.Test.Store, ports, new(ports, 4 * 1024 * 1024), ports);
        var lost = new LostAck(ingestion);
        var service = new MappingCreationApprovalService(f.Store, f.Store, lost, f.Test.Store);
        var first = await service.ApproveAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, f.Proposal.Fingerprint, new(f.Proposal.Fingerprint, ["a1"]));
        Assert.Equal(IngestionPhase.Committed, first!.Receipt!.Phase);
        Assert.Equal(1, lost.Creates);
        var replay = await service.ApproveAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, f.Proposal.Fingerprint, new(f.Proposal.Fingerprint, ["a1"]));
        Assert.Equal(first.Receipt.OperationId, replay!.Receipt!.OperationId); Assert.Equal(1, lost.Creates);
        await f.Tombstone();
        Assert.Equal(first.Receipt.OperationId, (await service.RecoverAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, f.Proposal.Fingerprint))!.Receipt!.OperationId);
        Assert.Equal(0, lost.Resumes);
    }

    [PostgresFact]
    public async Task Actual_application_ingestion_cannot_finish_after_lease_death_and_source_invalidation()
    {
        await using var f = await Fixture.Create(fixture);
        await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default);
        var ports = new PortablePorts(f.Test)
        {
            BeforeValidation = async () =>
            {
                await f.Test.Case.BootstrapAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=current_database() AND usename='weft_creation_approver' AND state='idle in transaction'");
                await f.Tombstone();
            }
        };
        var ingestion = new RevisionIngestionService(f.Test.Store, f.Test.Store, ports, new(ports, 4 * 1024 * 1024), ports);
        var service = new MappingCreationApprovalService(f.Store, f.Store, ingestion, f.Test.Store);
        var result = await service.ApproveAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, f.Proposal.Fingerprint, new(f.Proposal.Fingerprint, ["a1"]));
        Assert.NotEqual(IngestionPhase.Committed, result!.Receipt!.Phase);
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mappings"));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task Installed_shape_matches_exact_admission()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setup = new NpgsqlCommand("SET LOCAL search_path=pg_catalog", connection, transaction)) await setup.ExecuteNonQueryAsync();
        var shape = await CreationSchemaAdmission.ShapeAsync(connection, transaction, default);
        Assert.True(shape == CreationSchemaAdmission.ShapeDigest, "Actual schema shape: " + shape);
    }

    [PostgresFact]
    public async Task Decision_is_committed_and_independent_read_and_ingestion_complete_while_fenced()
    {
        await using var f = await Fixture.Create(fixture);
        var first = await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default);
        Assert.False(first!.Approved);
        await using var lease = await f.Approve();
        Assert.NotNull(lease);
        var restored = await f.Store.ReadAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(restored!.Approved);
        Assert.Equal(f.Proposal.Fingerprint, restored.Proposal.Fingerprint);
        Assert.Equal(f.Proposal.Compilation.CopyNative(), restored.Proposal.Compilation.CopyNative());
        var facts = await f.Test.ReserveAsync(f.Proposal.RequestKey, f.Intent);
        var claim = await f.Test.ClaimAsync(facts); await f.Test.ReadyAsync(facts, claim);
        Assert.Equal(IngestionPhase.Committed, (await f.Test.Store.FinalizeAsync(f.Test.Actor, claim)).Receipt!.Phase);
        Assert.True((await f.Store.ReadAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, default))!.Approved);
    }

    [PostgresFact]
    public async Task Approval_crash_before_reservation_recovery_does_not_create_and_deliberate_retry_can_continue()
    {
        await using var f = await Fixture.Create(fixture);
        await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default);
        await using (var lease = await f.Approve()) Assert.NotNull(lease);
        var recording = new NeverIngest();
        var service = new MappingCreationApprovalService(f.Store, f.Store, recording, f.Test.Store);
        var observed = await service.RecoverAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, f.Proposal.Fingerprint);
        Assert.True(observed!.State.Approved); Assert.Null(observed.Receipt);
        Assert.Equal(0, recording.Calls);
        await using var retry = await f.Approve();
        Assert.NotNull(await f.Test.ReserveAsync(f.Proposal.RequestKey, f.Intent));
    }

    [PostgresFact]
    public async Task Reserved_key_requires_approval_and_exact_native_metadata()
    {
        await using var f = await Fixture.Create(fixture);
        await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Test.ReserveAsync(f.Proposal.RequestKey, f.Intent));
        await using var lease = await f.Approve();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Test.ReserveAsync(f.Proposal.RequestKey, PostgresIngestionTestSupport.CreateIntent(name: "substitute")));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
    }

    [PostgresFact]
    public async Task Terminated_lease_then_tombstone_cannot_publish_mapping_or_revision()
    {
        await using var f = await Fixture.Create(fixture);
        await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default);
        var lease = await f.Approve(); Assert.NotNull(lease);
        try
        {
            var facts = await f.Test.ReserveAsync(f.Proposal.RequestKey, f.Intent);
            var claim = await f.Test.ClaimAsync(facts); await f.Test.ReadyAsync(facts, claim);
            await f.Test.Case.BootstrapAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=current_database() AND usename='weft_creation_approver' AND state='idle in transaction'");
            await f.Tombstone();
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Test.Store.FinalizeAsync(f.Test.Actor, claim));
            Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mappings"));
            Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
            await using var retryStore = f.NewStore();
            Assert.Null(await retryStore.AcquireAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, f.Proposal.Fingerprint, null, default));
        }
        finally { await lease!.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task Independent_approvers_replay_one_decision_and_reject_changed_assumptions()
    {
        await using var f = await Fixture.Create(fixture);
        await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var store = f.NewStore();
            await using var lease = await store.AcquireAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, f.Proposal.Fingerprint, new(f.Proposal.Fingerprint, ["a1"]), default);
            Assert.NotNull(lease);
        }));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.creation_approvals"));
        Assert.Null(await f.Store.AcquireAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, f.Proposal.Fingerprint, new(f.Proposal.Fingerprint, []), default));
    }

    [PostgresFact]
    public async Task Bundle_tombstone_and_grant_revocation_are_fenced_then_deny_new_work()
    {
        await using var f = await Fixture.Create(fixture);
        await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default);
        var lease = await f.Approve();
        try
        {
            // NOWAIT probes prove a real database row fence, not elapsed-time assertions.
            await Assert.ThrowsAsync<PostgresException>(() => f.Test.Case.ExecuteAsync("SELECT 1 FROM weft.spec_bundles WHERE id=$1 FOR UPDATE NOWAIT", f.Bundle.BundleId));
            await Assert.ThrowsAsync<PostgresException>(() => f.Test.Case.ExecuteAsync("SELECT 1 FROM weft.project_payload_read_grants WHERE principal_id=$1 FOR UPDATE NOWAIT", f.Test.Actor.PrincipalId.Value));
        }
        finally { await lease!.DisposeAsync(); }
        await f.Test.Case.ExecuteAsync("UPDATE weft.project_payload_read_grants SET active=false,version=version+1 WHERE principal_id=$1", f.Test.Actor.PrincipalId.Value);
        Assert.Null(await f.Store.ReadAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, default));
        Assert.Null(await f.Approve());
    }

    [PostgresFact]
    public async Task Reader_writer_roles_cannot_approve_and_schema_tamper_fails_closed()
    {
        await using var f = await Fixture.Create(fixture);
        foreach (var role in new[] { "weft_creation_reader", "weft_creation_writer" })
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(f.Test.Case.RegressionConnectionString(role)) { Pooling = false }.ConnectionString); await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT weft.creation_approve($1,$2,$3,$4,$5,$6)", connection);
            foreach (var value in new object[] { f.Test.Actor.PrincipalId.Value, f.Test.Project.OrganizationId, f.Test.Project.ProjectId, f.Proposal.Id, f.Proposal.Fingerprint.Value, new[] { "a1" } }) command.Parameters.AddWithValue(value);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync()); Assert.Equal("42501", error.SqlState);
        }
        await f.Test.Case.ExecuteAsync("ALTER TABLE weft.revision_ingestions DISABLE TRIGGER revision_ingestions_creation_guard");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ReadAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, default));
    }

    [PostgresFact]
    public async Task Published_exact_extraction_is_required_and_codec_refuses_substitution()
    {
        await using var f = await Fixture.Create(fixture);
        var bytes = MappingCreationStorageCodec.Encode(f.Proposal);
        Assert.Equal(f.Proposal.Fingerprint, MappingCreationStorageCodec.Decode(bytes).Fingerprint);
        Assert.ThrowsAny<Exception>(() => MappingCreationStorageCodec.Decode(bytes[..^1]));
        bytes[^1] ^= 1;
        Assert.ThrowsAny<Exception>(() => MappingCreationStorageCodec.Decode(bytes));
        await f.Test.Case.ExecuteAsync("UPDATE weft.spec_objects SET state='pending' WHERE kind=2");
        Assert.Null(await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.creation_proposals"));
    }

    [PostgresFact]
    public async Task Disabled_internal_foreign_key_trigger_refuses_creation_and_ingestion()
    {
        await using var f = await Fixture.Create(fixture);
        await f.Test.Case.BootstrapAsync("ALTER TABLE weft.creation_sources DISABLE TRIGGER ALL");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ReadAsync(f.Test.Actor, f.Test.Project, f.Proposal.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Test.ReserveAsync(Guid.NewGuid(), f.Intent));
    }

    [PostgresTheory]
    [InlineData("UPDATE weft.spec_sources SET digest=repeat('0',64)")]
    [InlineData("UPDATE weft.spec_sources SET format=2")]
    [InlineData("UPDATE weft.spec_objects SET profile_digest=repeat('0',64) WHERE kind=2")]
    [InlineData("UPDATE weft.spec_objects SET extraction_outcome=2 WHERE kind=2")]
    [InlineData("UPDATE weft.spec_objects SET source_digest=repeat('0',64) WHERE kind=2")]
    [InlineData("UPDATE weft.spec_stores SET active=false")]
    public async Task Changed_selected_registry_facts_refuse_retention(string mutation)
    {
        await using var f = await Fixture.Create(fixture);
        await f.Test.Case.ExecuteAsync(mutation);
        Assert.Null(await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.creation_proposals"));
    }

    [PostgresFact]
    public async Task Replays_preserve_content_and_existing_ingestion_keys_cannot_be_adopted()
    {
        await using var f = await Fixture.Create(fixture);
        await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default);
        Assert.Equal(f.Proposal.Fingerprint, (await f.Store.RetainAsync(f.Test.Actor, f.Proposal, default))!.Proposal.Fingerprint);
        var p = f.Proposal;
        var changed = new MappingCreationProposal(p.Author, p.Id, p.RequestKey, p.Specification, p.Plan, p.Notes, p.Compilation, new(new("Changed", "Fixture"), "ferrule"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.RetainAsync(f.Test.Actor, changed, default));
        var key = Guid.NewGuid();
        await f.Test.ReserveAsync(key, f.Intent);
        var id = Guid.NewGuid();
        var compilation = new CreationCompilation(p.Project, id, p.Plan.CompilerDigest, p.Specification.Snapshot.Digest,
            p.Compilation.EngineRevision, p.Compilation.WorkerBuild, p.Compilation.CopyNative(), p.Compilation.CopyValidation(), p.Compilation.CopyProjection());
        var adopted = new MappingCreationProposal(p.Author, id, key, p.Specification, p.Plan, p.Notes, compilation, p.Request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.RetainAsync(f.Test.Actor, adopted, default));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal required PostgresIngestionTestSupport Test;
        internal required PostgresMappingCreationStore Store;
        internal required MappingCreationProposal Proposal;
        internal SpecBundleScope Bundle => Proposal.Specification.Snapshot.Bundle;
        internal IngestionIntent Intent => IngestionIntent.ForCreate(Proposal.Request.Metadata, Proposal.Request.BackendId, Proposal.Request.OriginalFilename,
            Proposal.Compilation.NativeDigest, Proposal.Compilation.CopyNative().Length);
        internal PostgresMappingCreationStore NewStore() => new(Test.Case.RegressionConnectionString("weft_creation_reader"), Test.Case.RegressionConnectionString("weft_creation_writer"), Test.Case.RegressionConnectionString("weft_creation_approver"));
        internal Task<IMappingCreationApprovalLease?> Approve() => Store.AcquireAsync(Test.Actor, Test.Project, Proposal.Id, Proposal.Fingerprint, new(Proposal.Fingerprint, ["a1"]), default);
        internal Task Tombstone() => Test.Case.ExecuteAsync("SELECT * FROM weft.spec_tombstone($1,$2,$3,$4,NULL,NULL,1,$5)", Test.Actor.PrincipalId.Value, Test.Project.OrganizationId, Test.Project.ProjectId, Bundle.BundleId, Guid.NewGuid());
        internal static async Task<Fixture> Create(PostgresFixture fixture)
        {
            var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
            try
            {
                await test.Case.ExecuteAsync("INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,true,1)", test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
                var bundle = new SpecBundleScope(test.Project, Guid.NewGuid());
                var source = new SpecCapturedSource(new(bundle, Guid.NewGuid(), 1), SpecSourceFormat.Text, "Map a to b"u8);
                var extraction = SpecSourceExtractor.Extract(source); var specification = new CreationSpecification(bundle, [source], [extraction]);
                var plan = new MappingCreationPlan([new("a", CreationScalarType.Text, true)], [new("b", CreationScalarType.Text, true)], [new("a", "b")]);
                var id = Guid.NewGuid(); var storeId = Guid.NewGuid();
                var compilation = new CreationCompilation(test.Project, id, plan.CompilerDigest, specification.Snapshot.Digest, "fixture", "fixture",
                    PostgresIngestionTestSupport.SyntheticBody, "{}"u8, "{}"u8);
                var proposal = new MappingCreationProposal(test.Actor.PrincipalId, id, Guid.NewGuid(), specification, plan,
                    new([new(source.Identity, 0)], [new("a1", "Source is text", true)], []), compilation, new(new("Mapping", "Fixture"), "ferrule"));
                await test.Case.ExecuteAsync("INSERT INTO weft.spec_stores VALUES($1,'filesystem',$2,true)", storeId, Sha256Digest.FromBytes(Guid.NewGuid().ToByteArray()).Value);
                await test.Case.ExecuteAsync("INSERT INTO weft.spec_bundles(organization_id,project_id,id,creator_id,creation_key) VALUES($1,$2,$3,$4,$5)", test.Project.OrganizationId, test.Project.ProjectId, bundle.BundleId, test.Actor.PrincipalId.Value, Guid.NewGuid());
                await test.Case.ExecuteAsync("INSERT INTO weft.spec_sources(organization_id,project_id,bundle_id,id,version,format,digest,byte_count) VALUES($1,$2,$3,$4,1,1,$5,$6)", test.Project.OrganizationId, test.Project.ProjectId, bundle.BundleId, source.Identity.SourceId, source.Digest.Value, source.ByteCount);
                await test.Case.ExecuteAsync("""
                    INSERT INTO weft.spec_objects(organization_id,project_id,bundle_id,id,source_id,source_version,kind,digest,byte_count,store_id,owner_id,attempt_id,state,reservation_version)
                    VALUES($1,$2,$3,$4,$5,1,1,$6,$7,$8,$9,$10,'published',1)
                    """, test.Project.OrganizationId, test.Project.ProjectId, bundle.BundleId, Guid.NewGuid(), source.Identity.SourceId, source.Digest.Value, source.ByteCount, storeId, test.Actor.PrincipalId.Value, Guid.NewGuid());
                await test.Case.ExecuteAsync("""
                    INSERT INTO weft.spec_objects(organization_id,project_id,bundle_id,id,source_id,source_version,kind,digest,byte_count,store_id,owner_id,attempt_id,state,reservation_version,extractor_version,profile_digest,extraction_outcome,source_digest)
                    VALUES($1,$2,$3,$4,$5,1,2,$6,$7,$8,$9,$10,'published',1,$11,$12,1,$13)
                    """, test.Project.OrganizationId, test.Project.ProjectId, bundle.BundleId, Guid.NewGuid(), source.Identity.SourceId, extraction.Identity.EvidenceDigest.Value, extraction.CopyEvidenceBytes().Length,
                    storeId, test.Actor.PrincipalId.Value, Guid.NewGuid(), extraction.Identity.ExtractorVersion, extraction.Identity.ProfileDigest.Value, source.Digest.Value);
                return new() { Test = test, Proposal = proposal, Store = new(test.Case.RegressionConnectionString("weft_creation_reader"), test.Case.RegressionConnectionString("weft_creation_writer"), test.Case.RegressionConnectionString("weft_creation_approver")) };
            }
            catch { await test.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); await Test.DisposeAsync(); }
    }

    private sealed class NeverIngest : IRevisionIngestionService
    {
        internal int Calls;
        public Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid key, CreateRevisionRequest request, Stream source, CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException(); }
        public Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target, Guid key, AppendRevisionRequest request, Stream source, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException(); }
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    // Actual application and PostgreSQL metadata; in-memory bytes and explicitly synthetic validation.
    private sealed class PortablePorts(PostgresIngestionTestSupport test) : IProjectReader, IPrivateObjectStore, IIngestionBackendRegistry, IIngestionAdmissionBackend
    {
        private byte[]? bytes;
        internal Func<Task>? BeforeValidation;
        public Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => Task.FromResult<ProjectAccess?>(new(scope, actor.PrincipalId, ProjectRole.Publisher));
        public Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit, Guid? afterProjectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream(); await source.CopyToAsync(buffer, cancellationToken); bytes = buffer.ToArray();
            Assert.Equal(reference.Digest, Sha256Digest.FromBytes(bytes)); return PrivateObjectPublishResult.Created;
        }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference, CancellationToken cancellationToken = default) => Task.FromResult(bytes?.ToArray() ?? throw new PrivateObjectStorageException(PrivateObjectStorageError.Missing));
        public IngestionBackendRegistration? GetCurrent(string backendId) => backendId == "ferrule" ? new(PostgresIngestionTestSupport.Profile, 4 * 1024 * 1024, this) : null;
        public IngestionBackendRegistration? GetReserved(IngestionBackendProfile exactProfile) => exactProfile == PostgresIngestionTestSupport.Profile ? GetCurrent("ferrule") : null;
        public async Task<IngestionAttemptCompletion> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            Assert.Equal(test.Project.OrganizationId, artifact.Scope.OrganizationId);
            if (BeforeValidation is not null) await BeforeValidation();
            using var diagnostics = JsonDocument.Parse("[]");
            var facts = new IngestionValidationFacts(IngestionBackendStatus.Succeeded, new(artifact.Scope, artifact.Digest, PostgresIngestionTestSupport.Profile, true), true, 0, 0, false);
            return IngestionAttemptCompletion.FromValidation(IngestionEvidenceDocument.Create(facts, diagnostics.RootElement));
        }
    }

    private sealed class LostAck(IRevisionIngestionService inner) : IRevisionIngestionService
    {
        internal int Creates, Resumes;
        public async Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid key, CreateRevisionRequest request, Stream source, CancellationToken cancellationToken = default)
        { Creates++; await inner.CreateAsync(actor, project, key, request, source, cancellationToken); throw new IOException("Lost acknowledgement fixture."); }
        public Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target, Guid key, AppendRevisionRequest request, Stream source, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default)
        { Resumes++; return inner.ResumeAsync(actor, project, operationId, cancellationToken); }
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => inner.GetOwnReceiptAsync(actor, project, operationId, cancellationToken);
    }
}
