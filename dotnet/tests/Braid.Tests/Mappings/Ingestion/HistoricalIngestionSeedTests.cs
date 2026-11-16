using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class HistoricalIngestionSeedTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task OriginalEnabledGuardsAdmitCreateAndSyntheticInvalidAppendWithCompleteRepeatableSnapshots()
    {
        await using var test = await CreateOriginalCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE braid.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var project = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        var schemaBefore = (await HistoricalIngestionSeed.CaptureSnapshotAsync(test)).SchemaStateJson;
        var first = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, project,
            name: "  Historical synthetic mapping  ", description: "Synthetic C1 evidence only.\nRetained exactly.",
            filename: "historical fixture.native.json");
        var second = await HistoricalIngestionSeed.CommitAppendAsync(test, first, valid: false);

        var snapshot = await HistoricalIngestionSeed.CaptureSnapshotAsync(test);
        Assert.Equal(snapshot, await HistoricalIngestionSeed.CaptureSnapshotAsync(test));
        Assert.Equal(schemaBefore, snapshot.SchemaStateJson);
        using var facts = JsonDocument.Parse(snapshot.RetainedFactsJson);
        Assert.Equal(new[]
        {
            "organizations", "principals", "organization_memberships", "projects", "project_memberships", "mappings",
            "revision_ingestions", "revision_ingestion_attempts", "mapping_revisions"
        }, facts.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, facts.RootElement.GetProperty("organizations").GetArrayLength());
        Assert.Equal(4, facts.RootElement.GetProperty("principals").GetArrayLength());
        Assert.Equal(5, facts.RootElement.GetProperty("organization_memberships").GetArrayLength());
        Assert.Equal(3, facts.RootElement.GetProperty("projects").GetArrayLength());
        Assert.Equal(3, facts.RootElement.GetProperty("project_memberships").GetArrayLength());
        Assert.Equal(2, facts.RootElement.GetProperty("revision_ingestions").GetArrayLength());
        Assert.Equal(2, facts.RootElement.GetProperty("revision_ingestion_attempts").GetArrayLength());
        Assert.Equal(2, facts.RootElement.GetProperty("mapping_revisions").GetArrayLength());
        AssertReceipt(snapshot, first, true, 1);
        AssertReceipt(snapshot, second, false, 2);
        Assert.Equal(first.Facts.FutureRevision.MappingId, second.Facts.FutureRevision.MappingId);
        Assert.NotEqual(first.Facts.OperationId, second.Facts.OperationId);
        Assert.NotEqual(first.Facts.FutureRevision.RevisionId, second.Facts.FutureRevision.RevisionId);
        Assert.NotEqual(first.Facts.NativeObject.Id.ObjectId, second.Facts.NativeObject.Id.ObjectId);
        Assert.Equal("historical fixture.native.json", first.Facts.Intent.OriginalFilename);
        Assert.Equal(IngestionIntent.EntryDocument, second.Facts.Intent.OriginalFilename);
        var mapping = Assert.Single(facts.RootElement.GetProperty("mappings").EnumerateArray());
        Assert.Equal("Historical synthetic mapping", mapping.GetProperty("name").GetString());
        Assert.Equal(first.Facts.Intent.CreateMetadata!.Description, mapping.GetProperty("description").GetString());
        Assert.Equal(1, mapping.GetProperty("version").GetInt64());
        Assert.Equal(2, mapping.GetProperty("head_sequence").GetInt64());
        Assert.Equal(second.Facts.FutureRevision.RevisionId, mapping.GetProperty("head_revision_id").GetGuid());
        Assert.Equal(first.CompletedAtUtc, mapping.GetProperty("created_at").GetDateTimeOffset());
        Assert.Equal(second.CompletedAtUtc, mapping.GetProperty("updated_at").GetDateTimeOffset());
        using var schema = JsonDocument.Parse(snapshot.SchemaStateJson);
        Assert.Equal(new[] { 1, 2 }, schema.RootElement.GetProperty("journal").EnumerateArray()
            .Select(row => row.GetProperty("version").GetInt32()));
        Assert.Equal(4, schema.RootElement.GetProperty("functions").GetArrayLength());
        Assert.All(schema.RootElement.GetProperty("triggers").EnumerateArray(), trigger =>
        {
            Assert.Equal("O", trigger.GetProperty("enabled").GetString());
            Assert.Equal(31, trigger.GetProperty("type").GetInt32());
        });
        Assert.Equal(4, schema.RootElement.GetProperty("triggers").GetArrayLength());
        Assert.NotEmpty(schema.RootElement.GetProperty("columns").EnumerateArray());
        Assert.NotEmpty(schema.RootElement.GetProperty("relations").EnumerateArray());
        Assert.NotEmpty(schema.RootElement.GetProperty("schemas").EnumerateArray());
    }

    [PostgresFact]
    public async Task CurrentSchemaRefusesHistoricalCreateAndAppendBeforeMutationWhileSnapshotsRemainReadable()
    {
        await using var test = await CreateOriginalCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var project = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        var committed = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, project);
        var old = await HistoricalIngestionSeed.CaptureSnapshotAsync(test);
        await test.MigrateAsync();
        var current = await HistoricalIngestionSeed.CaptureSnapshotAsync(test);
        Assert.Equal(old.RetainedFactsJson, current.RetainedFactsJson);
        Assert.NotEqual(old.SchemaStateJson, current.SchemaStateJson);
        var create = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HistoricalIngestionSeed.CommitCreateAsync(test, actor, project));
        var append = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HistoricalIngestionSeed.CommitAppendAsync(test, committed));
        Assert.Contains("0001/0002", create.Message, StringComparison.Ordinal);
        Assert.Contains("0001/0002", append.Message, StringComparison.Ordinal);
        Assert.Equal(current, await HistoricalIngestionSeed.CaptureSnapshotAsync(test));
    }

    [PostgresFact]
    public async Task DisabledOrChangedOriginalGuardRefusesHistoricalWritesBeforeMutation()
    {
        await using var test = await CreateOriginalCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        // Deliberate negative in this owned case: never seed through a disabled guard.
        await test.ExecuteAsync("ALTER TABLE braid.mappings DISABLE TRIGGER mappings_transition_guard");
        var before = await HistoricalIngestionSeed.CaptureSnapshotAsync(test);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => HistoricalIngestionSeed.CommitCreateAsync(test,
            new(new PrincipalId(seed.Alice)), new(seed.OrganizationA, seed.ProjectA)));
        Assert.Contains("original enabled transition guards", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, await HistoricalIngestionSeed.CaptureSnapshotAsync(test));
        await test.ExecuteAsync("ALTER TABLE braid.mappings ENABLE TRIGGER mappings_transition_guard");
        await test.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION braid.guard_mapping_revisions_transition() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog,braid
            AS $changed$ BEGIN RETURN NEW; END; $changed$;
            """);
        var changedBody = await HistoricalIngestionSeed.CaptureSnapshotAsync(test);
        var bodyRefusal = await Assert.ThrowsAsync<InvalidOperationException>(() => HistoricalIngestionSeed.CommitCreateAsync(test,
            new(new PrincipalId(seed.Alice)), new(seed.OrganizationA, seed.ProjectA)));
        Assert.Contains("original enabled transition guards", bodyRefusal.Message, StringComparison.Ordinal);
        Assert.Equal(changedBody, await HistoricalIngestionSeed.CaptureSnapshotAsync(test));
    }

    [PostgresFact]
    public async Task SnapshotRefusesTableRowOverflowRatherThanClaimingTruncatedPreservation()
    {
        await using var test = await CreateOriginalCaseAsync();
        await test.ExecuteAsync("""
            INSERT INTO braid.organizations(id,name)
            SELECT md5('historical-row-bound:' || value::text)::uuid,'Synthetic snapshot bound'
            FROM generate_series(1,65) value
            """);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => HistoricalIngestionSeed.CaptureSnapshotAsync(test));
        Assert.Contains("no truncated snapshot", refused.Message, StringComparison.Ordinal);
        Assert.Equal(65, await test.ScalarAsync<long>("SELECT count(*) FROM braid.organizations"));
    }

    [PostgresFact]
    public async Task SnapshotRefusesAccumulatedUtf8OverflowWithinIndividualTableRowLimits()
    {
        await using var test = await CreateOriginalCaseAsync();
        var organization = Guid.NewGuid();
        var creator = Guid.NewGuid();
        await test.ExecuteAsync("INSERT INTO braid.organizations(id,name) VALUES ($1,'Synthetic byte bound')", organization);
        await test.ExecuteAsync("INSERT INTO braid.principals(id,active) VALUES ($1,true)", creator);
        await test.ExecuteAsync("INSERT INTO braid.organization_memberships(organization_id,principal_id,active) VALUES ($1,$2,true)", organization, creator);
        await test.ExecuteAsync("""
            INSERT INTO braid.projects(organization_id,id,name,description,creator_principal_id)
            SELECT $1,md5('historical-byte-bound:' || value::text)::uuid,'Synthetic byte bound',repeat('😀',4000),$2
            FROM generate_series(1,64) value
            """, organization, creator);
        var before = await HistoricalIngestionSeed.CaptureSnapshotAsync(test);
        var beforeBytes = Encoding.UTF8.GetByteCount(before.RetainedFactsJson) + Encoding.UTF8.GetByteCount(before.SchemaStateJson);
        Assert.True(beforeBytes < 2 * 1024 * 1024);
        // Table rows alone fit the current 2 MiB budget. Add an individually admissible
        // schema row so only the shared facts-and-schema byte budget is exceeded.
        await test.ExecuteAsync("""
            DO $seed$ BEGIN
              EXECUTE format('CREATE FUNCTION braid.snapshot_budget_padding() RETURNS void LANGUAGE plpgsql AS %L',
                '/*' || repeat('😀',150000) || '*/ BEGIN RETURN; END');
            END $seed$;
            """);
        var paddingBytes = await test.ScalarAsync<int>("SELECT octet_length(prosrc)+octet_length(pg_get_functiondef(oid)) FROM pg_proc WHERE oid='braid.snapshot_budget_padding()'::regprocedure");
        Assert.InRange(paddingBytes, 1024 * 1024, 1536 * 1024);
        Assert.True(beforeBytes + paddingBytes > 2 * 1024 * 1024);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => HistoricalIngestionSeed.CaptureSnapshotAsync(test));
        Assert.Contains("UTF8 byte limit", refused.Message, StringComparison.Ordinal);
        Assert.Equal(64, await test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"));
    }

    [PostgresFact]
    public async Task CurrentJournalFitsTheSnapshotButAnUnknownExtraRowStillRefusesWithoutTruncation()
    {
        await using var test = await fixture.CreateCaseAsync();
        var count = MigrationRunner.EmbeddedMigrations.Count;
        var snapshot = await HistoricalIngestionSeed.CaptureSnapshotAsync(test);
        using (var schema = JsonDocument.Parse(snapshot.SchemaStateJson))
        {
            Assert.Equal(count, schema.RootElement.GetProperty("journal").GetArrayLength());
            // Grouping is lossless: keep the original row cap per relation and the
            // shared byte cap, rather than truncating columns or raising either cap.
            var columns = schema.RootElement.GetProperty("columns");
            var expectedColumns = await test.ScalarAsync<long>("""
                SELECT count(*) FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
                WHERE c.relnamespace='braid'::regnamespace AND c.relkind IN ('r','p','v','m','f')
                  AND a.attnum>0 AND NOT a.attisdropped
                """);
            Assert.Equal(expectedColumns, columns.EnumerateArray().Sum(row => (long)row.GetProperty("columns").GetArrayLength()));
            Assert.All(columns.EnumerateArray(), row => Assert.InRange(row.GetProperty("columns").GetArrayLength(), 1, 1024));
        }
        await test.ExecuteAsync("INSERT INTO braid.schema_migrations(version,checksum) VALUES($1,repeat('0',64))", count + 1);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => HistoricalIngestionSeed.CaptureSnapshotAsync(test));
        Assert.Contains("no truncated snapshot", refused.Message, StringComparison.Ordinal);
        Assert.Equal((long)count + 1, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
    }

    [PostgresFact]
    public async Task SnapshotRefusesPerRelationColumnOverflowWithoutReturningAPartialGroup()
    {
        await using var test = await CreateOriginalCaseAsync();
        await test.ExecuteAsync("""
            DO $seed$ BEGIN
                EXECUTE 'CREATE TABLE braid.snapshot_column_budget (' ||
                    (SELECT string_agg('c' || n::text || ' integer', ',' ORDER BY n) FROM generate_series(1,1025) n) || ')';
            END $seed$;
            """);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => HistoricalIngestionSeed.CaptureSnapshotAsync(test));
        Assert.Contains("no truncated snapshot", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1025L, await test.ScalarAsync<long>("SELECT count(*) FROM pg_attribute WHERE attrelid='braid.snapshot_column_budget'::regclass AND attnum>0"));
    }

    private async Task<PostgresCase> CreateOriginalCaseAsync()
    {
        var test = await fixture.CreateCaseAsync(migrate: false);
        try
        {
            await using var runner = new MigrationRunner(test.MigrationConnectionString,
                MigrationRunner.EmbeddedMigrations.Take(2).ToArray());
            await runner.MigrateAsync();
            return test;
        }
        catch { await test.DisposeAsync(); throw; }
    }

    private static void AssertReceipt(HistoricalIngestionOwnerSnapshot snapshot, IngestionReceipt receipt,
        bool valid, long sequence)
    {
        using var document = JsonDocument.Parse(snapshot.RetainedFactsJson);
        var root = document.RootElement;
        var facts = receipt.Facts;
        var scope = facts.FutureRevision;
        var reservation = root.GetProperty("revision_ingestions").EnumerateArray()
            .Single(row => row.GetProperty("operation_id").GetGuid() == facts.OperationId);
        Assert.Equal(IngestionPhase.Committed, receipt.Phase);
        Assert.Equal(1, receipt.CurrentGeneration);
        Assert.Equal(1, receipt.ClaimCount);
        Assert.Equal(1, receipt.VerifiedStageGeneration);
        Assert.Equal(1, receipt.EligibleAttemptGeneration);
        Assert.Equal(facts.Project.OrganizationId, reservation.GetProperty("organization_id").GetGuid());
        Assert.Equal(facts.Project.ProjectId, reservation.GetProperty("project_id").GetGuid());
        Assert.Equal(facts.OriginalActor.Value, reservation.GetProperty("actor_principal_id").GetGuid());
        Assert.Equal(facts.IdempotencyKey, reservation.GetProperty("idempotency_key").GetGuid());
        Assert.Equal(facts.Fingerprint.Version, reservation.GetProperty("fingerprint_version").GetInt32());
        Assert.Equal(facts.Fingerprint.Digest.Value, reservation.GetProperty("intent_fingerprint").GetString());
        Assert.Equal(facts.Intent.Kind == IngestionOperationKind.Create ? "create" : "append", reservation.GetProperty("operation_kind").GetString());
        Assert.Equal(facts.Intent.Target?.MappingId, NullableGuid(reservation.GetProperty("target_mapping_id")));
        Assert.Equal(scope.MappingId, reservation.GetProperty("future_mapping_id").GetGuid());
        Assert.Equal(scope.RevisionId, reservation.GetProperty("future_revision_id").GetGuid());
        Assert.Equal(facts.NativeObject.Id.ObjectId, reservation.GetProperty("native_object_id").GetGuid());
        Assert.Equal("native", reservation.GetProperty("native_class").GetString());
        Assert.Equal(facts.NativeObject.Digest.Value, reservation.GetProperty("native_digest").GetString());
        Assert.Equal(facts.NativeObject.ByteCount, reservation.GetProperty("native_byte_count").GetInt64());
        Assert.Equal(facts.Intent.LogicalDocument, reservation.GetProperty("logical_entry_document").GetString());
        Assert.Equal(facts.Intent.DependencyIdentity, reservation.GetProperty("dependency_manifest_id").GetString());
        Assert.Equal(facts.BackendProfile.BackendId, reservation.GetProperty("backend_id").GetString());
        Assert.Equal(facts.BackendProfile.EngineRevision, reservation.GetProperty("engine_revision").GetString());
        Assert.Equal(facts.BackendProfile.BackendBuild, reservation.GetProperty("backend_build").GetString());
        Assert.Equal(facts.BackendProfile.AdapterBuild, reservation.GetProperty("adapter_build").GetString());
        Assert.Equal(facts.AdmissionPolicy.Identity, reservation.GetProperty("admission_policy_id").GetString());
        Assert.Equal(facts.AdmissionPolicy.EffectiveNativeByteLimit, reservation.GetProperty("effective_native_byte_limit").GetInt64());
        Assert.Equal(facts.Intent.OriginalFilename, reservation.GetProperty("display_filename").GetString());
        Assert.Equal(facts.Intent.CreateMetadata?.Name, reservation.GetProperty("create_name").GetString());
        Assert.Equal(facts.Intent.CreateMetadata?.Description, reservation.GetProperty("create_description").GetString());
        Assert.Equal("any", reservation.GetProperty("head_condition").GetString());
        Assert.Equal(JsonValueKind.Null, reservation.GetProperty("expected_head_revision_id").ValueKind);
        Assert.Equal(facts.ReservedAtUtc, reservation.GetProperty("created_at").GetDateTimeOffset());
        Assert.Equal("committed", reservation.GetProperty("phase").GetString());
        Assert.Equal(receipt.VerifiedStageAtUtc, reservation.GetProperty("verified_stage_at").GetDateTimeOffset());
        Assert.Equal(receipt.CompletedAtUtc, reservation.GetProperty("completed_at").GetDateTimeOffset());
        Assert.Equal(scope.RevisionId, reservation.GetProperty("committed_revision_id").GetGuid());
        Assert.Equal(1, reservation.GetProperty("lease_generation").GetInt64());
        Assert.Equal(1, reservation.GetProperty("claim_count").GetInt32());
        Assert.Equal(1, reservation.GetProperty("verified_stage_generation").GetInt64());
        Assert.Equal(1, reservation.GetProperty("eligible_attempt_generation").GetInt64());
        foreach (var field in new[] { "lease_owner_id", "lease_token", "lease_expires_at", "last_error" })
            Assert.Equal(JsonValueKind.Null, reservation.GetProperty(field).ValueKind);

        var attempt = Assert.Single(receipt.Attempts);
        var persisted = root.GetProperty("revision_ingestion_attempts").EnumerateArray()
            .Single(row => row.GetProperty("operation_id").GetGuid() == facts.OperationId);
        Assert.Equal(attempt.StartedAtUtc, persisted.GetProperty("started_at").GetDateTimeOffset());
        Assert.Equal(facts.Project.OrganizationId, persisted.GetProperty("organization_id").GetGuid());
        Assert.Equal(facts.Project.ProjectId, persisted.GetProperty("project_id").GetGuid());
        Assert.Equal(attempt.Generation, persisted.GetProperty("generation").GetInt64());
        Assert.Equal(attempt.FinishedAtUtc, persisted.GetProperty("finished_at").GetDateTimeOffset());
        Assert.Equal(receipt.VerifiedStageAtUtc, persisted.GetProperty("verified_stage_at").GetDateTimeOffset());
        Assert.NotEqual(Guid.Empty, persisted.GetProperty("claimant_instance_id").GetGuid());
        Assert.NotEqual(Guid.Empty, persisted.GetProperty("claim_token").GetGuid());
        var classification = valid ? IngestionAdmissionClassification.EligibleValid : IngestionAdmissionClassification.EligibleDecodedInvalid;
        var admission = valid ? "eligible_valid" : "eligible_decoded_invalid";
        Assert.Equal(classification, attempt.Classification);
        Assert.Equal(admission, reservation.GetProperty("eligible_admission").GetString());
        Assert.Equal(admission, persisted.GetProperty("admission_classification").GetString());
        Assert.Equal("validation", persisted.GetProperty("completion_kind").GetString());
        Assert.Equal(valid ? "succeeded" : "invalid_mapping", persisted.GetProperty("backend_status").GetString());
        Assert.True(persisted.GetProperty("provenance_confirmed").GetBoolean());
        Assert.Equal(scope.OrganizationId, persisted.GetProperty("provenance_organization_id").GetGuid());
        Assert.Equal(scope.ProjectId, persisted.GetProperty("provenance_project_id").GetGuid());
        Assert.Equal(scope.MappingId, persisted.GetProperty("provenance_mapping_id").GetGuid());
        Assert.Equal(scope.RevisionId, persisted.GetProperty("provenance_revision_id").GetGuid());
        Assert.Equal(facts.NativeObject.Digest.Value, persisted.GetProperty("provenance_native_digest").GetString());
        Assert.Equal(facts.BackendProfile.BackendId, persisted.GetProperty("provenance_backend_id").GetString());
        Assert.Equal(facts.BackendProfile.EngineRevision, persisted.GetProperty("provenance_engine_revision").GetString());
        Assert.Equal(facts.BackendProfile.BackendBuild, persisted.GetProperty("provenance_backend_build").GetString());
        Assert.Equal(facts.BackendProfile.AdapterBuild, persisted.GetProperty("provenance_adapter_build").GetString());
        Assert.Equal(valid, persisted.GetProperty("is_valid").GetBoolean());
        Assert.Equal(0, persisted.GetProperty("total_issue_count").GetInt32());
        Assert.Equal(0, persisted.GetProperty("emitted_issue_count").GetInt32());
        Assert.False(persisted.GetProperty("issues_truncated").GetBoolean());
        Assert.Equal(IngestionEvidenceDocument.SchemaVersion, persisted.GetProperty("evidence_version").GetString());
        foreach (var field in new[] { "reused_attempt_generation", "reused_admission", "last_error" })
            Assert.Equal(JsonValueKind.Null, persisted.GetProperty(field).ValueKind);
        var completion = Assert.IsType<IngestionAttemptCompletion>(attempt.Completion);
        Assert.Equal(IngestionAttemptCompletionKind.Validation, completion.Kind);
        Assert.Null(completion.Failure);
        Assert.Null(completion.ObservedProvenance);
        Assert.Null(completion.ReusedAttemptGeneration);
        var evidence = Assert.IsType<IngestionEvidenceDocument>(completion.Evidence);
        Assert.Equal(valid, evidence.Facts.IsValid);
        Assert.Equal(new IngestionProvenance(scope, facts.NativeObject.Digest, facts.BackendProfile, true), evidence.Facts.Provenance);
        Assert.Equal(valid ? IngestionBackendStatus.Succeeded : IngestionBackendStatus.InvalidMapping, evidence.Facts.Status);
        Assert.Equal(0, evidence.Facts.TotalIssueCount);
        Assert.Equal(0, evidence.Facts.EmittedIssueCount);
        Assert.False(evidence.Facts.Truncated);
        Assert.Equal(persisted.GetProperty("evidence").GetRawText(), Encoding.UTF8.GetString(evidence.CopyUtf8()));

        var revision = Assert.IsType<CommittedRevisionFacts>(receipt.CommittedRevision);
        var committed = root.GetProperty("mapping_revisions").EnumerateArray()
            .Single(row => row.GetProperty("id").GetGuid() == scope.RevisionId);
        Assert.Equal(scope, revision.Scope);
        Assert.Equal(sequence, revision.Sequence);
        Assert.Equal(facts.NativeObject, revision.NativeObject);
        Assert.Equal(facts.BackendProfile, revision.BackendProfile);
        Assert.Equal(facts.Intent.LogicalDocument, revision.LogicalDocument);
        Assert.Equal(facts.Intent.DependencyIdentity, revision.DependencyIdentity);
        Assert.Equal(facts.OriginalActor, revision.Creator);
        Assert.Equal(facts.OperationId, revision.OperationId);
        Assert.Equal(1, revision.InitialAdmissionAttemptGeneration);
        Assert.Equal(classification, revision.InitialAdmission);
        Assert.Equal(receipt.CompletedAtUtc, revision.CommittedAtUtc);
        Assert.Equal(revision.Sequence, committed.GetProperty("sequence").GetInt64());
        Assert.Equal(scope.OrganizationId, committed.GetProperty("organization_id").GetGuid());
        Assert.Equal(scope.ProjectId, committed.GetProperty("project_id").GetGuid());
        Assert.Equal(scope.MappingId, committed.GetProperty("mapping_id").GetGuid());
        Assert.Equal(revision.NativeObject.Id.ObjectId, committed.GetProperty("native_object_id").GetGuid());
        Assert.Equal("native", committed.GetProperty("native_class").GetString());
        Assert.Equal(revision.NativeObject.Digest.Value, committed.GetProperty("native_digest").GetString());
        Assert.Equal(revision.NativeObject.ByteCount, committed.GetProperty("native_byte_count").GetInt64());
        Assert.Equal(revision.LogicalDocument, committed.GetProperty("logical_entry_document").GetString());
        Assert.Equal(revision.DependencyIdentity, committed.GetProperty("dependency_manifest_id").GetString());
        Assert.Equal(revision.BackendProfile.BackendId, committed.GetProperty("backend_id").GetString());
        Assert.Equal(revision.Creator.Value, committed.GetProperty("creator_principal_id").GetGuid());
        Assert.Equal(revision.CommittedAtUtc, committed.GetProperty("committed_at").GetDateTimeOffset());
        Assert.Equal(revision.OperationId, committed.GetProperty("ingestion_operation_id").GetGuid());
        Assert.Equal(revision.InitialAdmissionAttemptGeneration, committed.GetProperty("initial_attempt_generation").GetInt64());
        Assert.Equal(admission, committed.GetProperty("initial_admission").GetString());
    }

    private static Guid? NullableGuid(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetGuid();
}
