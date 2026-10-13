using Weft.Application;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresRevisionIngestionStore
{
    private async Task<IngestionStoreResult> FinalizeCoreAsync(ActorContext actor, IngestionClaim claim,
        CancellationToken cancellationToken = default)
    {
        RequireClaim(actor, claim);
        await using var transaction = await _database.BeginTransactionAsync(claim.Project.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(FencedSql + """
            , ready AS MATERIALIZED (
                SELECT fenced.* FROM fenced
                JOIN weft.revision_ingestion_attempts original_attempt
                  ON original_attempt.organization_id = fenced.organization_id AND original_attempt.project_id = fenced.project_id
                 AND original_attempt.operation_id = fenced.operation_id AND original_attempt.generation = fenced.eligible_attempt_generation
                JOIN weft.revision_ingestion_attempts current_attempt
                  ON current_attempt.organization_id = fenced.organization_id AND current_attempt.project_id = fenced.project_id
                 AND current_attempt.operation_id = fenced.operation_id AND current_attempt.generation = fenced.lease_generation
                WHERE fenced.phase = 'ready' AND fenced.verified_stage_generation = fenced.lease_generation
                  AND current_attempt.claimant_instance_id = @instanceId AND current_attempt.claim_token = @leaseToken
                  AND current_attempt.verified_stage_at IS NOT NULL AND current_attempt.finished_at IS NOT NULL
                  AND original_attempt.finished_at IS NOT NULL AND original_attempt.verified_stage_at IS NOT NULL
                  AND original_attempt.completion_kind = 'validation'
                  AND original_attempt.admission_classification = fenced.eligible_admission
                  AND original_attempt.provenance_confirmed
                  AND original_attempt.provenance_organization_id = fenced.organization_id
                  AND original_attempt.provenance_project_id = fenced.project_id
                  AND original_attempt.provenance_mapping_id = fenced.future_mapping_id
                  AND original_attempt.provenance_revision_id = fenced.future_revision_id
                  AND original_attempt.provenance_native_digest = fenced.native_digest
                  AND original_attempt.provenance_backend_id = fenced.backend_id
                  AND original_attempt.provenance_engine_revision = fenced.engine_revision
                  AND original_attempt.provenance_backend_build = fenced.backend_build
                  AND original_attempt.provenance_adapter_build = fenced.adapter_build
                  AND ((original_attempt.admission_classification = 'eligible_valid'
                        AND original_attempt.backend_status = 'succeeded' AND original_attempt.is_valid = true)
                    OR (original_attempt.admission_classification = 'eligible_decoded_invalid'
                        AND original_attempt.backend_status = 'invalid_mapping' AND original_attempt.is_valid = false))
                  AND ((current_attempt.generation = original_attempt.generation
                        AND current_attempt.admission_classification = original_attempt.admission_classification)
                    OR (current_attempt.completion_kind = 'reused_evidence'
                        AND current_attempt.admission_classification = 'reused_evidence'
                        AND current_attempt.reused_attempt_generation = original_attempt.generation
                        AND current_attempt.reused_admission = original_attempt.admission_classification))
            ), locked_mapping AS MATERIALIZED (
                SELECT mapping.* FROM weft.mappings mapping JOIN ready
                  ON mapping.organization_id = ready.organization_id AND mapping.project_id = ready.project_id
                 AND mapping.id = ready.future_mapping_id AND mapping.backend_id = ready.backend_id
                WHERE ready.operation_kind = 'append' FOR UPDATE OF mapping
            ), publication_input AS MATERIALIZED (
                SELECT ready.*, mapping.head_revision_id AS previous_head, mapping.head_sequence AS previous_sequence,
                    mapping.updated_at AS previous_updated_at, clock_timestamp() AS publication_time
                FROM ready LEFT JOIN locked_mapping mapping
                  ON mapping.organization_id = ready.organization_id AND mapping.project_id = ready.project_id
                 AND mapping.id = ready.future_mapping_id
                WHERE (ready.operation_kind = 'create' OR mapping.id IS NOT NULL)
                  AND ready.lease_expires_at > clock_timestamp()
            ), created_mapping AS (
                INSERT INTO weft.mappings
                  (organization_id, project_id, id, name, description, backend_id, creator_principal_id,
                   owner_principal_id, created_at, updated_at, version, head_revision_id, head_sequence)
                SELECT organization_id, project_id, future_mapping_id, create_name, create_description, backend_id,
                    actor_principal_id, actor_principal_id, publication_time, publication_time, 1, future_revision_id, 1
                FROM publication_input WHERE operation_kind = 'create'
                RETURNING organization_id, project_id, id
            ), revision_input AS MATERIALIZED (
                SELECT publication_input.*, 1::bigint AS sequence
                FROM publication_input JOIN created_mapping
                  ON created_mapping.organization_id = publication_input.organization_id
                 AND created_mapping.project_id = publication_input.project_id
                 AND created_mapping.id = publication_input.future_mapping_id
                UNION ALL
                SELECT publication_input.*, publication_input.previous_sequence + 1 AS sequence
                FROM publication_input WHERE operation_kind = 'append'
                  AND (head_condition = 'any' OR expected_head_revision_id = previous_head)
            ), inserted_revision AS (
                INSERT INTO weft.mapping_revisions
                  (organization_id, project_id, mapping_id, id, sequence, native_object_id, native_class,
                   native_digest, native_byte_count, logical_entry_document, dependency_manifest_id, backend_id,
                   creator_principal_id, committed_at, ingestion_operation_id, initial_attempt_generation, initial_admission)
                SELECT organization_id, project_id, future_mapping_id, future_revision_id, sequence,
                    native_object_id, native_class, native_digest, native_byte_count, logical_entry_document,
                    dependency_manifest_id, backend_id, actor_principal_id, publication_time, operation_id,
                    eligible_attempt_generation, eligible_admission
                FROM revision_input
                RETURNING organization_id, project_id, mapping_id, id, sequence, ingestion_operation_id, committed_at
            ), advanced_mapping AS (
                UPDATE weft.mappings mapping
                SET head_revision_id = revision.id, head_sequence = revision.sequence,
                    updated_at = greatest(publication_input.previous_updated_at, revision.committed_at)
                FROM inserted_revision revision JOIN publication_input
                  ON publication_input.organization_id = revision.organization_id
                 AND publication_input.project_id = revision.project_id
                 AND publication_input.operation_id = revision.ingestion_operation_id
                WHERE publication_input.operation_kind = 'append'
                  AND mapping.organization_id = revision.organization_id AND mapping.project_id = revision.project_id
                  AND mapping.id = revision.mapping_id
                RETURNING mapping.id
            ), receipt_input AS MATERIALIZED (
                SELECT revision.organization_id, revision.project_id, revision.ingestion_operation_id AS operation_id,
                    revision.id AS committed_revision_id, revision.committed_at AS completed_at, 'committed'::text AS phase
                FROM inserted_revision revision
                UNION ALL
                SELECT organization_id, project_id, operation_id, NULL::uuid, publication_time, 'head_conflict'::text
                FROM publication_input WHERE operation_kind = 'append' AND head_condition = 'equal'
                  AND expected_head_revision_id <> previous_head
            ), finalized_receipt AS (
                UPDATE weft.revision_ingestions ingestion
                SET phase = receipt_input.phase, completed_at = receipt_input.completed_at,
                    committed_revision_id = receipt_input.committed_revision_id,
                    lease_owner_id = NULL, lease_token = NULL, lease_expires_at = NULL, last_error = NULL
                FROM receipt_input WHERE ingestion.organization_id = receipt_input.organization_id
                  AND ingestion.project_id = receipt_input.project_id AND ingestion.operation_id = receipt_input.operation_id
                RETURNING ingestion.phase
            )
            SELECT EXISTS (SELECT 1 FROM locked), (SELECT phase FROM locked), (SELECT operation_kind FROM locked),
                (SELECT count(*) FROM created_mapping), (SELECT count(*) FROM inserted_revision),
                (SELECT count(*) FROM advanced_mapping), (SELECT count(*) FROM finalized_receipt),
                (SELECT phase FROM finalized_receipt)
            """);
        AddClaim(command, actor, claim);
        bool found;
        string? previousPhase;
        bool applied;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            found = reader.GetBoolean(0);
            previousPhase = reader.IsDBNull(1) ? null : reader.GetString(1);
            var kind = reader.IsDBNull(2) ? null : reader.GetString(2);
            var createdCount = reader.GetInt64(3);
            var revisionCount = reader.GetInt64(4);
            var advancedCount = reader.GetInt64(5);
            var receiptCount = reader.GetInt64(6);
            var phase = reader.IsDBNull(7) ? null : reader.GetString(7);
            applied = receiptCount == 1;
            var consistent = receiptCount == 0
                ? createdCount == 0 && revisionCount == 0 && advancedCount == 0 && phase is null
                : receiptCount == 1 && (phase == "head_conflict"
                    ? kind == "append" && createdCount == 0 && revisionCount == 0 && advancedCount == 0
                    : phase == "committed" && revisionCount == 1 && (kind == "create"
                        ? createdCount == 1 && advancedCount == 0
                        : kind == "append" && createdCount == 0 && advancedCount == 1));
            if (!consistent)
                throw InvalidMetadata();
        }
        if (!applied && claim.OriginalActor == actor.PrincipalId &&
            previousPhase is "committed" or "rejected" or "head_conflict" or "attempt_exhausted")
        {
            var receipt = await ReadReceiptAsync(transaction, actor, claim.Project, claim.OperationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return receipt is null ? IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.NotAuthorizedOrNotFound)
                : IngestionStoreResult.ForReceipt(IngestionStoreOutcome.Existing, receipt);
        }
        return await FinishMutationAsync(transaction, actor, claim, found, applied, cancellationToken);
    }
}
