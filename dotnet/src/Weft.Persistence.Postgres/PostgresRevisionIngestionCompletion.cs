using System.Text;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresRevisionIngestionStore
{
    private async Task<IngestionStoreResult> CompleteAttemptCoreAsync(ActorContext actor, IngestionClaim claim,
        IngestionAttemptCompletion completion, CancellationToken cancellationToken = default)
    {
        RequireClaim(actor, claim);
        ArgumentNullException.ThrowIfNull(completion);
        await using var transaction = await _database.BeginTransactionAsync(claim.Project.OrganizationId, cancellationToken);
        var observedReceipt = await ReadReceiptAsync(transaction, actor, claim.Project, claim.OperationId, cancellationToken);
        if (observedReceipt is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.NotAuthorizedOrNotFound);
        }
        var classification = completion.Classify(observedReceipt.Facts);
        var validation = completion.Evidence?.Facts;
        var provenance = validation?.Provenance ?? completion.ObservedProvenance;
        var kind = completion.Kind switch
        {
            IngestionAttemptCompletionKind.Validation => "validation",
            IngestionAttemptCompletionKind.OperationalFailure => "operational_failure",
            IngestionAttemptCompletionKind.ReusedEvidence => "reused_evidence",
            _ => throw new ArgumentException("The ingestion completion kind must be known.", nameof(completion))
        };
        await using var command = transaction.CreateCommand(FencedSql + """
            , normalized AS MATERIALIZED (
                SELECT @evidence::jsonb AS evidence,
                    COALESCE(octet_length(@evidence::jsonb::text) > 8388608, false) AS evidence_limit
            ), completion_input AS MATERIALIZED (
                SELECT fenced.*, normalized.evidence, normalized.evidence_limit,
                    CASE WHEN normalized.evidence_limit THEN 'operational_failure' ELSE @completionKind END AS effective_kind,
                    CASE WHEN normalized.evidence_limit THEN 'rejected_resource_limit' ELSE @classification END AS classification,
                    CASE WHEN normalized.evidence_limit THEN 'evidence_limit' ELSE @failure END AS failure
                FROM fenced CROSS JOIN normalized
                JOIN weft.revision_ingestion_attempts current_attempt
                  ON current_attempt.organization_id = fenced.organization_id AND current_attempt.project_id = fenced.project_id
                 AND current_attempt.operation_id = fenced.operation_id AND current_attempt.generation = fenced.lease_generation
                 AND current_attempt.claimant_instance_id = @instanceId AND current_attempt.claim_token = @leaseToken
                WHERE current_attempt.finished_at IS NULL
                  AND (@completionKind = 'operational_failure' OR
                      (fenced.verified_stage_generation = fenced.lease_generation AND current_attempt.verified_stage_at IS NOT NULL))
                  AND (@completionKind <> 'validation' OR fenced.eligible_attempt_generation IS NULL)
                  AND (@completionKind <> 'reused_evidence' OR (
                      fenced.eligible_attempt_generation = @reuseGeneration AND EXISTS (
                        SELECT 1 FROM weft.revision_ingestion_attempts original_attempt
                        WHERE original_attempt.organization_id = fenced.organization_id
                          AND original_attempt.project_id = fenced.project_id
                          AND original_attempt.operation_id = fenced.operation_id
                          AND original_attempt.generation = @reuseGeneration AND original_attempt.finished_at IS NOT NULL
                          AND original_attempt.admission_classification = fenced.eligible_admission
                          AND original_attempt.admission_classification IN ('eligible_valid', 'eligible_decoded_invalid'))))
                  AND (@classification NOT IN ('eligible_valid', 'eligible_decoded_invalid') OR (
                      @confirmed AND @provenanceOrganization = fenced.organization_id AND @provenanceProject = fenced.project_id
                      AND @provenanceMapping = fenced.future_mapping_id AND @provenanceRevision = fenced.future_revision_id
                      AND @provenanceDigest = fenced.native_digest AND @provenanceBackend = fenced.backend_id
                      AND @provenanceEngine = fenced.engine_revision AND @provenanceBuild = fenced.backend_build
                      AND @provenanceAdapter = fenced.adapter_build))
            ), completed_attempt AS (
                UPDATE weft.revision_ingestion_attempts attempt
                SET finished_at = completion_input.observed_at,
                    completion_kind = completion_input.effective_kind,
                    backend_status = CASE WHEN completion_input.effective_kind = 'validation' THEN @status ELSE NULL END,
                    admission_classification = completion_input.classification,
                    provenance_confirmed = @confirmed,
                    provenance_organization_id = @provenanceOrganization, provenance_project_id = @provenanceProject,
                    provenance_mapping_id = @provenanceMapping, provenance_revision_id = @provenanceRevision,
                    provenance_native_digest = @provenanceDigest, provenance_backend_id = @provenanceBackend,
                    provenance_engine_revision = @provenanceEngine, provenance_backend_build = @provenanceBuild,
                    provenance_adapter_build = @provenanceAdapter,
                    is_valid = CASE WHEN completion_input.effective_kind = 'validation' THEN @isValid ELSE NULL END,
                    total_issue_count = CASE WHEN completion_input.effective_kind = 'validation' THEN @totalCount ELSE NULL END,
                    emitted_issue_count = CASE WHEN completion_input.effective_kind = 'validation' THEN @emittedCount ELSE NULL END,
                    issues_truncated = CASE WHEN completion_input.effective_kind = 'validation' THEN @truncated ELSE NULL END,
                    evidence_version = CASE WHEN completion_input.effective_kind = 'validation' THEN @evidenceVersion ELSE NULL END,
                    evidence = CASE WHEN completion_input.effective_kind = 'validation' THEN completion_input.evidence ELSE NULL END,
                    reused_attempt_generation = CASE WHEN completion_input.effective_kind = 'reused_evidence' THEN @reuseGeneration ELSE NULL END,
                    reused_admission = CASE WHEN completion_input.effective_kind = 'reused_evidence' THEN completion_input.eligible_admission ELSE NULL END,
                    last_error = completion_input.failure
                FROM completion_input WHERE attempt.organization_id = completion_input.organization_id
                  AND attempt.project_id = completion_input.project_id AND attempt.operation_id = completion_input.operation_id
                  AND attempt.generation = completion_input.lease_generation
                RETURNING attempt.organization_id, attempt.project_id, attempt.operation_id, attempt.generation,
                    attempt.admission_classification, attempt.finished_at, attempt.last_error
            ), changed AS (
                UPDATE weft.revision_ingestions ingestion
                SET phase = CASE
                        WHEN completed_attempt.admission_classification IN ('eligible_valid', 'eligible_decoded_invalid', 'reused_evidence') THEN 'ready'
                        WHEN completed_attempt.admission_classification IN ('rejected_native_decode', 'rejected_unsupported', 'rejected_resource_limit') THEN 'rejected'
                        ELSE ingestion.phase END,
                    eligible_attempt_generation = CASE WHEN completed_attempt.admission_classification IN ('eligible_valid', 'eligible_decoded_invalid')
                        THEN completed_attempt.generation ELSE ingestion.eligible_attempt_generation END,
                    eligible_admission = CASE WHEN completed_attempt.admission_classification IN ('eligible_valid', 'eligible_decoded_invalid')
                        THEN completed_attempt.admission_classification ELSE ingestion.eligible_admission END,
                    lease_owner_id = CASE WHEN completed_attempt.admission_classification IN ('eligible_valid', 'eligible_decoded_invalid', 'reused_evidence')
                        THEN ingestion.lease_owner_id ELSE NULL END,
                    lease_token = CASE WHEN completed_attempt.admission_classification IN ('eligible_valid', 'eligible_decoded_invalid', 'reused_evidence')
                        THEN ingestion.lease_token ELSE NULL END,
                    lease_expires_at = CASE WHEN completed_attempt.admission_classification IN ('eligible_valid', 'eligible_decoded_invalid', 'reused_evidence')
                        THEN ingestion.lease_expires_at ELSE NULL END,
                    completed_at = CASE WHEN completed_attempt.admission_classification IN ('rejected_native_decode', 'rejected_unsupported', 'rejected_resource_limit')
                        THEN completed_attempt.finished_at ELSE NULL END,
                    last_error = completed_attempt.last_error
                FROM completed_attempt WHERE ingestion.organization_id = completed_attempt.organization_id
                  AND ingestion.project_id = completed_attempt.project_id AND ingestion.operation_id = completed_attempt.operation_id
                RETURNING ingestion.operation_id
            )
            SELECT EXISTS (SELECT 1 FROM locked), (SELECT count(*) FROM completed_attempt), (SELECT count(*) FROM changed)
            """);
        AddClaim(command, actor, claim);
        Add(command, "completionKind", NpgsqlDbType.Text, kind);
        Add(command, "classification", NpgsqlDbType.Text, AdmissionName(classification));
        Add(command, "failure", NpgsqlDbType.Text, completion.Failure is { } failure ? FailureName(failure) : null);
        Add(command, "evidence", NpgsqlDbType.Jsonb, completion.Evidence is { } evidence
            ? Encoding.UTF8.GetString(evidence.CopyUtf8()) : null);
        Add(command, "status", NpgsqlDbType.Text, validation is null ? null : IngestionPersistenceNames.BackendStatus(validation.Status));
        Add(command, "isValid", NpgsqlDbType.Boolean, validation?.IsValid);
        Add(command, "totalCount", NpgsqlDbType.Integer, validation?.TotalIssueCount);
        Add(command, "emittedCount", NpgsqlDbType.Integer, validation?.EmittedIssueCount);
        Add(command, "truncated", NpgsqlDbType.Boolean, validation?.Truncated);
        Add(command, "evidenceVersion", NpgsqlDbType.Text, completion.Evidence?.Version);
        Add(command, "reuseGeneration", NpgsqlDbType.Bigint, completion.ReusedAttemptGeneration);
        Add(command, "confirmed", NpgsqlDbType.Boolean, provenance?.Confirmed);
        Add(command, "provenanceOrganization", NpgsqlDbType.Uuid, provenance?.Scope.OrganizationId);
        Add(command, "provenanceProject", NpgsqlDbType.Uuid, provenance?.Scope.ProjectId);
        Add(command, "provenanceMapping", NpgsqlDbType.Uuid, provenance?.Scope.MappingId);
        Add(command, "provenanceRevision", NpgsqlDbType.Uuid, provenance?.Scope.RevisionId);
        Add(command, "provenanceDigest", NpgsqlDbType.Text, provenance?.NativeDigest.Value);
        Add(command, "provenanceBackend", NpgsqlDbType.Text, provenance?.BackendProfile.BackendId);
        Add(command, "provenanceEngine", NpgsqlDbType.Text, provenance?.BackendProfile.EngineRevision);
        Add(command, "provenanceBuild", NpgsqlDbType.Text, provenance?.BackendProfile.BackendBuild);
        Add(command, "provenanceAdapter", NpgsqlDbType.Text, provenance?.BackendProfile.AdapterBuild);
        bool found;
        bool applied;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            found = reader.GetBoolean(0);
            var attempts = reader.GetInt64(1);
            var ingestions = reader.GetInt64(2);
            if (attempts is < 0 or > 1 || ingestions != attempts)
                throw InvalidMetadata();
            applied = ingestions == 1;
        }
        return await FinishMutationAsync(transaction, actor, claim, found, applied, cancellationToken);
    }

    private static string AdmissionName(IngestionAdmissionClassification value) => value switch
    {
        IngestionAdmissionClassification.EligibleValid => "eligible_valid",
        IngestionAdmissionClassification.EligibleDecodedInvalid => "eligible_decoded_invalid",
        IngestionAdmissionClassification.RejectedNativeDecode => "rejected_native_decode",
        IngestionAdmissionClassification.RejectedUnsupported => "rejected_unsupported",
        IngestionAdmissionClassification.RejectedResourceLimit => "rejected_resource_limit",
        IngestionAdmissionClassification.RetryableFailure => "retryable_failure",
        IngestionAdmissionClassification.ReusedEvidence => "reused_evidence",
        _ => throw InvalidMetadata()
    };

    private static string FailureName(IngestionFailureCode value) => value switch
    {
        IngestionFailureCode.NeedsSource => "needs_source",
        IngestionFailureCode.StorageIntegrity => "storage_integrity",
        IngestionFailureCode.StorageIo => "storage_io",
        IngestionFailureCode.BackendUnavailable => "backend_unavailable",
        IngestionFailureCode.BackendFailure => "backend_failure",
        IngestionFailureCode.TimedOut => "timed_out",
        IngestionFailureCode.Canceled => "canceled",
        IngestionFailureCode.IsolationFailure => "isolation_failure",
        IngestionFailureCode.ProtocolFailure => "protocol_failure",
        IngestionFailureCode.ReservedProfileUnavailable => "reserved_profile_unavailable",
        IngestionFailureCode.EvidenceLimit => "evidence_limit",
        _ => throw InvalidMetadata()
    };
}
