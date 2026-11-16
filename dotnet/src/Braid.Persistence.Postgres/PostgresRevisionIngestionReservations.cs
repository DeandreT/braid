using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresRevisionIngestionStore
{
    private async Task<IngestionStoreResult> ReserveCoreAsync(ActorContext actor, ProjectScope project,
        Guid idempotencyKey, IngestionIntent intent, IngestionBackendProfile proposedProfile,
        IngestionAdmissionPolicy proposedPolicy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(proposedProfile);
        ArgumentNullException.ThrowIfNull(proposedPolicy);
        RequireId(idempotencyKey, nameof(idempotencyKey));
        var fingerprint = CanonicalFingerprint.Create(actor.PrincipalId, project, intent);
        await using (var transaction = await _database.BeginTransactionAsync(project.OrganizationId, cancellationToken))
        {
            var existing = await ReadKeyAsync(transaction, actor, project, idempotencyKey, intent, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return CompareExisting(existing, fingerprint, intent);
            }
            // A replay uses the original profile and policy. Only a genuinely new proposal is admitted here.
            if (proposedProfile.BackendId != intent.BackendId ||
                proposedPolicy.EffectiveNativeByteLimit < intent.NativeByteCount)
                throw new ArgumentException("The proposed backend profile and policy must cover the native intent.");
            var operationId = Guid.NewGuid();
            var mappingId = intent.Target?.MappingId ?? Guid.NewGuid();
            await using var command = transaction.CreateCommand(AuthorizedSql + """
                , inserted AS (
                    INSERT INTO braid.revision_ingestions
                    (organization_id, project_id, operation_id, actor_principal_id, operation_kind,
                     idempotency_key, fingerprint_version, intent_fingerprint, target_mapping_id,
                     future_mapping_id, future_revision_id, native_object_id, native_class,
                     native_digest, native_byte_count, logical_entry_document, dependency_manifest_id,
                     backend_id, engine_revision, backend_build, adapter_build, admission_policy_id,
                     effective_native_byte_limit, display_filename, create_name, create_description,
                      head_condition, expected_head_revision_id, created_at, phase, lease_generation, claim_count)
                    SELECT organization_id, project_id, @operationId, @actorId, @kind, @key, 1, @fingerprint,
                           @target, @mappingId, @revisionId, @objectId, 'native', @digest, @byteCount,
                           @document, @dependencies, @backend, @engine, @build, @adapter, @policy,
                           @limit, @filename, @name, @description, @headCondition, @expectedHead,
                            clock_timestamp(), 'reserved', 0, 0
                    FROM authorized
                    WHERE @kind = 'create' OR EXISTS (
                        SELECT 1 FROM braid.mappings mapping WHERE mapping.organization_id = authorized.organization_id
                          AND mapping.project_id = authorized.project_id AND mapping.id = @target
                          AND mapping.backend_id = @backend)
                    ON CONFLICT DO NOTHING RETURNING operation_id
                )
                SELECT (SELECT operation_id FROM inserted)
                """);
            AddScope(command, actor, project);
            Add(command, "operationId", NpgsqlDbType.Uuid, operationId);
            Add(command, "kind", NpgsqlDbType.Text, intent.Kind == IngestionOperationKind.Create ? "create" : "append");
            Add(command, "key", NpgsqlDbType.Uuid, idempotencyKey);
            Add(command, "fingerprint", NpgsqlDbType.Text, fingerprint.Digest.Value);
            Add(command, "target", NpgsqlDbType.Uuid, intent.Target?.MappingId);
            Add(command, "mappingId", NpgsqlDbType.Uuid, mappingId);
            Add(command, "revisionId", NpgsqlDbType.Uuid, Guid.NewGuid());
            Add(command, "objectId", NpgsqlDbType.Uuid, Guid.NewGuid());
            Add(command, "digest", NpgsqlDbType.Text, intent.NativeDigest.Value);
            Add(command, "byteCount", NpgsqlDbType.Bigint, intent.NativeByteCount);
            Add(command, "document", NpgsqlDbType.Text, intent.LogicalDocument);
            Add(command, "dependencies", NpgsqlDbType.Text, intent.DependencyIdentity);
            Add(command, "backend", NpgsqlDbType.Text, intent.BackendId);
            Add(command, "engine", NpgsqlDbType.Text, proposedProfile.EngineRevision);
            Add(command, "build", NpgsqlDbType.Text, proposedProfile.BackendBuild);
            Add(command, "adapter", NpgsqlDbType.Text, proposedProfile.AdapterBuild);
            Add(command, "policy", NpgsqlDbType.Text, proposedPolicy.Identity);
            Add(command, "limit", NpgsqlDbType.Bigint, proposedPolicy.EffectiveNativeByteLimit);
            Add(command, "filename", NpgsqlDbType.Text, intent.OriginalFilename);
            Add(command, "name", NpgsqlDbType.Text, intent.CreateMetadata?.Name);
            Add(command, "description", NpgsqlDbType.Text, intent.CreateMetadata?.Description);
            Add(command, "headCondition", NpgsqlDbType.Text, intent.HeadCondition is EqualHead ? "equal" : "any");
            Add(command, "expectedHead", NpgsqlDbType.Uuid, (intent.HeadCondition as EqualHead)?.RevisionId);
            var inserted = await command.ExecuteScalarAsync(cancellationToken) is Guid;
            if (inserted)
            {
                if (intent.ImportProvenance is { } provenance)
                {
                    await using var original = transaction.CreateCommand("""
                        INSERT INTO braid.revision_import_provenance
                            (organization_id, project_id, operation_id, provenance)
                        VALUES (@organizationId, @projectId, @operationId, @provenance)
                        """);
                    Add(original, "organizationId", NpgsqlDbType.Uuid, project.OrganizationId);
                    Add(original, "projectId", NpgsqlDbType.Uuid, project.ProjectId);
                    Add(original, "operationId", NpgsqlDbType.Uuid, operationId);
                    Add(original, "provenance", NpgsqlDbType.Bytea, provenance.Encode());
                    await original.ExecuteNonQueryAsync(cancellationToken);
                }
                var receipt = await ReadReceiptAsync(transaction, actor, project, operationId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return receipt is null ? IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.NotAuthorizedOrNotFound)
                    : IngestionStoreResult.ForReceipt(IngestionStoreOutcome.Reserved, receipt);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        // The losing INSERT snapshot may predate its winner. Releasing the transaction also releases its locks.
        await using var reread = await _database.BeginTransactionAsync(project.OrganizationId, cancellationToken);
        var winner = await ReadKeyAsync(reread, actor, project, idempotencyKey, intent, cancellationToken);
        await reread.CommitAsync(cancellationToken);
        return winner is null ? IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.NotAuthorizedOrNotFound)
            : CompareExisting(winner, fingerprint, intent);
    }

    private static IngestionStoreResult CompareExisting(IngestionReceipt receipt, CanonicalFingerprint fingerprint,
        IngestionIntent intent) => receipt.Facts.Fingerprint == fingerprint && receipt.Facts.Intent == intent
        ? IngestionStoreResult.ForReceipt(IngestionStoreOutcome.Existing, receipt)
        : IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.IdempotencyConflict);

    private async Task<IngestionStoreResult> ClaimCoreAsync(ActorContext actor, ProjectScope project,
        Guid operationId, Guid serverInstanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(project);
        RequireId(operationId, nameof(operationId));
        RequireId(serverInstanceId, nameof(serverInstanceId));
        var token = Guid.NewGuid();
        await using var transaction = await _database.BeginTransactionAsync(project.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(LockedSql + """
            , decision AS MATERIALIZED (
                SELECT locked.*, clock_timestamp() AS observed_at FROM locked
            ), changed AS (
                UPDATE braid.revision_ingestions ingestion
                SET phase = CASE WHEN decision.claim_count = 8 THEN 'attempt_exhausted' ELSE decision.phase END,
                    lease_owner_id = CASE WHEN decision.claim_count = 8 THEN NULL ELSE @instanceId END,
                    lease_token = CASE WHEN decision.claim_count = 8 THEN NULL ELSE @leaseToken END,
                    lease_generation = decision.lease_generation + CASE WHEN decision.claim_count = 8 THEN 0 ELSE 1 END,
                    claim_count = decision.claim_count + CASE WHEN decision.claim_count = 8 THEN 0 ELSE 1 END,
                    lease_expires_at = CASE WHEN decision.claim_count = 8 THEN NULL ELSE decision.observed_at + interval '120 seconds' END,
                    completed_at = CASE WHEN decision.claim_count = 8 THEN decision.observed_at ELSE NULL END
                FROM decision
                WHERE ingestion.organization_id = decision.organization_id AND ingestion.project_id = decision.project_id
                  AND ingestion.operation_id = decision.operation_id
                  AND decision.phase IN ('reserved', 'staged', 'ready')
                  AND (decision.lease_expires_at IS NULL OR decision.lease_expires_at <= decision.observed_at)
                RETURNING ingestion.*
            ), attempt AS (
                INSERT INTO braid.revision_ingestion_attempts
                (organization_id, project_id, operation_id, generation, claimant_instance_id, claim_token, started_at)
                SELECT organization_id, project_id, operation_id, lease_generation, lease_owner_id, lease_token,
                       lease_expires_at - interval '120 seconds'
                FROM changed WHERE phase <> 'attempt_exhausted'
                RETURNING generation
            )
            SELECT EXISTS (SELECT 1 FROM locked) AS found,
              (SELECT count(*) FROM changed) AS changed_count, (SELECT count(*) FROM attempt) AS attempt_count,
              (SELECT lease_generation FROM changed WHERE phase <> 'attempt_exhausted') AS claimed_generation,
              (SELECT lease_expires_at FROM changed WHERE phase <> 'attempt_exhausted') AS claimed_expiry
            """);
        AddScope(command, actor, project);
        Add(command, "operationId", NpgsqlDbType.Uuid, operationId);
        Add(command, "instanceId", NpgsqlDbType.Uuid, serverInstanceId);
        Add(command, "leaseToken", NpgsqlDbType.Uuid, token);
        bool found;
        long? generation;
        DateTimeOffset? expiry;
        long changedCount;
        long attemptCount;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            found = reader.GetBoolean(0);
            changedCount = reader.GetInt64(1);
            attemptCount = reader.GetInt64(2);
            generation = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            expiry = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4);
        }
        if (changedCount is < 0 or > 1 || attemptCount != (generation is null ? 0 : 1) ||
            (generation is not null && (changedCount != 1 || expiry is null)))
            throw InvalidMetadata();
        var receipt = found ? await ReadReceiptAsync(transaction, actor, project, operationId, cancellationToken) : null;
        await transaction.CommitAsync(cancellationToken);
        if (receipt is null)
            return IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.NotAuthorizedOrNotFound);
        if (generation is { } claimedGeneration)
            return IngestionStoreResult.ForClaim(receipt, new IngestionClaim(project, operationId, actor.PrincipalId,
                serverInstanceId, token, claimedGeneration, expiry!.Value));
        return IngestionStoreResult.ForReceipt(IsTerminal(receipt.Phase) ? IngestionStoreOutcome.Existing :
            IngestionStoreOutcome.InProgress, receipt);
    }

    private async Task<IngestionStoreResult> MarkStagedCoreAsync(ActorContext actor, IngestionClaim claim,
        PrivateObjectReference verifiedNativeObject, CancellationToken cancellationToken = default)
    {
        RequireClaim(actor, claim);
        ArgumentNullException.ThrowIfNull(verifiedNativeObject);
        var reference = verifiedNativeObject;
        await using var transaction = await _database.BeginTransactionAsync(claim.Project.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(FencedSql + """
            , stage_input AS MATERIALIZED (
                SELECT fenced.*, attempt.verified_stage_at AS previous_stage_at
                FROM fenced JOIN braid.revision_ingestion_attempts attempt USING (organization_id, project_id, operation_id)
                WHERE attempt.generation = fenced.lease_generation AND attempt.claimant_instance_id = @instanceId
                  AND attempt.claim_token = @leaseToken AND attempt.finished_at IS NULL
                  AND fenced.organization_id = @objectOrganization AND fenced.project_id = @objectProject
                  AND fenced.future_mapping_id = @objectMapping AND fenced.future_revision_id = @objectRevision
                  AND fenced.native_object_id = @objectId AND @objectClass = 'native'
                  AND fenced.native_digest = @digest AND fenced.native_byte_count = @byteCount
            ), staged_attempt AS (
                UPDATE braid.revision_ingestion_attempts attempt SET verified_stage_at = stage_input.observed_at
                FROM stage_input WHERE attempt.organization_id = stage_input.organization_id
                  AND attempt.project_id = stage_input.project_id AND attempt.operation_id = stage_input.operation_id
                  AND attempt.generation = stage_input.lease_generation AND stage_input.previous_stage_at IS NULL
                RETURNING attempt.verified_stage_at
            ), changed AS (
                UPDATE braid.revision_ingestions ingestion
                SET phase = CASE WHEN stage_input.phase = 'reserved' THEN 'staged' ELSE stage_input.phase END,
                    verified_stage_generation = stage_input.lease_generation,
                    verified_stage_at = COALESCE(stage_input.previous_stage_at, (SELECT verified_stage_at FROM staged_attempt))
                FROM stage_input WHERE ingestion.organization_id = stage_input.organization_id
                  AND ingestion.project_id = stage_input.project_id AND ingestion.operation_id = stage_input.operation_id
                RETURNING ingestion.operation_id
            )
            SELECT EXISTS (SELECT 1 FROM locked), EXISTS (SELECT 1 FROM changed),
              EXISTS (SELECT 1 FROM fenced WHERE organization_id = @objectOrganization AND project_id = @objectProject
                AND future_mapping_id = @objectMapping AND future_revision_id = @objectRevision
                AND native_object_id = @objectId AND @objectClass = 'native'
                AND native_digest = @digest AND native_byte_count = @byteCount),
              (SELECT count(*) FROM staged_attempt), (SELECT count(*) FROM changed)
            """);
        AddClaim(command, actor, claim);
        Add(command, "objectOrganization", NpgsqlDbType.Uuid, reference.Id.Scope.OrganizationId);
        Add(command, "objectProject", NpgsqlDbType.Uuid, reference.Id.Scope.ProjectId);
        Add(command, "objectMapping", NpgsqlDbType.Uuid, reference.Id.Scope.MappingId);
        Add(command, "objectRevision", NpgsqlDbType.Uuid, reference.Id.Scope.RevisionId);
        Add(command, "objectId", NpgsqlDbType.Uuid, reference.Id.ObjectId);
        Add(command, "objectClass", NpgsqlDbType.Text, reference.Id.Class == PrivateObjectClass.NativeArtifact ? "native" : "derived");
        Add(command, "digest", NpgsqlDbType.Text, reference.Digest.Value);
        Add(command, "byteCount", NpgsqlDbType.Bigint, reference.ByteCount);
        bool found;
        bool applied;
        bool matches;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            found = reader.GetBoolean(0);
            applied = reader.GetBoolean(1);
            matches = reader.GetBoolean(2);
            if (reader.GetInt64(3) > (applied ? 1 : 0) || reader.GetInt64(4) != (applied ? 1 : 0))
                throw InvalidMetadata();
        }
        if (found && !matches && applied)
            throw InvalidMetadata();
        return await FinishMutationAsync(transaction, actor, claim, found, applied, cancellationToken);
    }

    private static async Task<IngestionStoreResult> FinishMutationAsync(PostgresIngestionTransaction transaction,
        ActorContext actor, IngestionClaim claim, bool found, bool applied, CancellationToken cancellationToken)
    {
        var receipt = applied ? await ReadReceiptAsync(transaction, actor, claim.Project, claim.OperationId, cancellationToken) : null;
        await transaction.CommitAsync(cancellationToken);
        if (!found || (applied && receipt is null))
            return IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.NotAuthorizedOrNotFound);
        return applied ? IngestionStoreResult.ForReceipt(IngestionStoreOutcome.Applied, receipt!) :
            IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.ClaimLost);
    }
}
