using System.Text;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresRevisionIngestionStore
{
    private const string ReceiptColumns = """
        SELECT ingestion.*,
          CASE WHEN row_number() OVER (
            PARTITION BY ingestion.organization_id, ingestion.project_id, ingestion.operation_id
            ORDER BY attempt.generation) = 1 THEN import.provenance END AS import_provenance,
          attempt.generation AS a_generation, attempt.started_at AS a_started_at,
          attempt.verified_stage_at AS a_verified_stage_at, attempt.finished_at AS a_finished_at,
          attempt.completion_kind AS a_completion_kind, attempt.backend_status AS a_backend_status,
          attempt.admission_classification AS a_admission_classification,
          attempt.provenance_confirmed AS a_provenance_confirmed,
          attempt.provenance_organization_id AS a_provenance_organization_id,
          attempt.provenance_project_id AS a_provenance_project_id,
          attempt.provenance_mapping_id AS a_provenance_mapping_id,
          attempt.provenance_revision_id AS a_provenance_revision_id,
          attempt.provenance_native_digest AS a_provenance_native_digest,
          attempt.provenance_backend_id AS a_provenance_backend_id,
          attempt.provenance_engine_revision AS a_provenance_engine_revision,
          attempt.provenance_backend_build AS a_provenance_backend_build,
          attempt.provenance_adapter_build AS a_provenance_adapter_build,
          attempt.is_valid AS a_is_valid, attempt.total_issue_count AS a_total_issue_count,
          attempt.emitted_issue_count AS a_emitted_issue_count, attempt.issues_truncated AS a_issues_truncated,
          attempt.evidence_version AS a_evidence_version, attempt.evidence::text AS a_evidence,
          attempt.reused_attempt_generation AS a_reused_attempt_generation,
          attempt.reused_admission AS a_reused_admission, attempt.last_error AS a_last_error,
          revision.id AS r_id, revision.organization_id AS r_organization_id,
          revision.project_id AS r_project_id, revision.mapping_id AS r_mapping_id,
          revision.sequence AS r_sequence, revision.native_object_id AS r_native_object_id,
          revision.native_class AS r_native_class, revision.native_digest AS r_native_digest,
          revision.native_byte_count AS r_native_byte_count, revision.backend_id AS r_backend_id,
          revision.logical_entry_document AS r_logical_entry_document,
          revision.dependency_manifest_id AS r_dependency_manifest_id,
          revision.creator_principal_id AS r_creator_principal_id, revision.committed_at AS r_committed_at,
          revision.ingestion_operation_id AS r_ingestion_operation_id,
          revision.initial_attempt_generation AS r_initial_attempt_generation,
          revision.initial_admission AS r_initial_admission
        FROM weft.revision_ingestions ingestion
        JOIN authorized USING (organization_id, project_id)
        LEFT JOIN weft.revision_import_provenance import
          ON import.organization_id = ingestion.organization_id AND import.project_id = ingestion.project_id
         AND import.operation_id = ingestion.operation_id
        LEFT JOIN weft.revision_ingestion_attempts attempt
          ON attempt.organization_id = ingestion.organization_id AND attempt.project_id = ingestion.project_id
         AND attempt.operation_id = ingestion.operation_id
        LEFT JOIN weft.mapping_revisions revision
          ON revision.organization_id = ingestion.organization_id AND revision.project_id = ingestion.project_id
         AND revision.mapping_id = ingestion.future_mapping_id AND revision.id = ingestion.committed_revision_id
        WHERE ingestion.actor_principal_id = @actorId
        """;

    private async Task<IngestionReceipt?> GetReceiptCoreAsync(ActorContext actor, ProjectScope project,
        Guid operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(project);
        RequireId(operationId, nameof(operationId));
        await using var transaction = await _database.BeginTransactionAsync(project.OrganizationId, cancellationToken);
        var receipt = await ReadReceiptAsync(transaction, actor, project, operationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task<IngestionReceipt?> ReadReceiptAsync(PostgresIngestionTransaction transaction,
        ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken) =>
        await ReadReceiptQueryAsync(transaction, actor, project,
            "AND ingestion.operation_id = @operationId", command =>
                Add(command, "operationId", NpgsqlDbType.Uuid, operationId), cancellationToken);

    private static async Task<IngestionReceipt?> ReadKeyAsync(PostgresIngestionTransaction transaction,
        ActorContext actor, ProjectScope project, Guid key, IngestionIntent intent, CancellationToken cancellationToken) =>
        await ReadReceiptQueryAsync(transaction, actor, project, """
            AND ingestion.idempotency_key = @key AND ingestion.operation_kind = @kind
            AND ingestion.target_mapping_id IS NOT DISTINCT FROM @target
            """, command =>
        {
            Add(command, "key", NpgsqlDbType.Uuid, key);
            Add(command, "kind", NpgsqlDbType.Text, intent.Kind == IngestionOperationKind.Create ? "create" : "append");
            Add(command, "target", NpgsqlDbType.Uuid, intent.Target?.MappingId);
        }, cancellationToken);

    private static async Task<IngestionReceipt?> ReadReceiptQueryAsync(PostgresIngestionTransaction transaction,
        ActorContext actor, ProjectScope project, string predicate, Action<NpgsqlCommand> parameters,
        CancellationToken cancellationToken)
    {
        await transaction.RequireOrganizationAsync(project.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(ReceiptQuery(predicate));
        AddScope(command, actor, project);
        parameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        try
        {
            var facts = ReadFacts(reader);
            if (facts.Project != project || facts.OriginalActor != actor.PrincipalId)
                throw InvalidMetadata();
            var phase = ParsePhase(Value<string>(reader, "phase"));
            var generation = Value<long>(reader, "lease_generation");
            var count = Value<int>(reader, "claim_count");
            var stageGeneration = Optional<long>(reader, "verified_stage_generation");
            var stageAt = Optional<DateTimeOffset>(reader, "verified_stage_at");
            var eligibleGeneration = Optional<long>(reader, "eligible_attempt_generation");
            var completedAt = Optional<DateTimeOffset>(reader, "completed_at");
            var committed = ReadCommitted(reader, facts);
            var attempts = new List<IngestionAttemptReceipt>(8);
            do
            {
                if (Optional<long>(reader, "a_generation") is { } attemptGeneration)
                {
                    if (attempts.Count == 8)
                        throw InvalidMetadata();
                    attempts.Add(ReadAttempt(reader, facts, attemptGeneration));
                }
            } while (await reader.ReadAsync(cancellationToken));
            return new IngestionReceipt(facts, phase, generation, count, stageGeneration, stageAt,
                eligibleGeneration, attempts, committed, completedAt);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or
            System.Text.Json.JsonException or InvalidCastException)
        {
            throw InvalidMetadata();
        }
    }

    // Keep facts and attempts in one statement snapshot. Only the first (possibly
    // attempt-less) row carries the bounded but large original package.
    internal static string ReceiptQuery(string predicate) =>
        AuthorizedSql + ReceiptColumns + "\n" + predicate + " ORDER BY attempt.generation";

    private static ReservedIngestionFacts ReadFacts(NpgsqlDataReader reader)
    {
        var project = new ProjectScope(Value<Guid>(reader, "organization_id"), Value<Guid>(reader, "project_id"));
        var mappingId = Value<Guid>(reader, "future_mapping_id");
        var scope = new RevisionScope(project.OrganizationId, project.ProjectId, mappingId,
            Value<Guid>(reader, "future_revision_id"));
        var digest = Sha256Digest.Parse(Value<string>(reader, "native_digest"));
        var byteCount = Value<long>(reader, "native_byte_count");
        HeadCondition head = Value<string>(reader, "head_condition") switch
        {
            "any" when Optional<Guid>(reader, "expected_head_revision_id") is null => new AnyHead(),
            "equal" => new EqualHead(Value<Guid>(reader, "expected_head_revision_id")),
            _ => throw InvalidMetadata()
        };
        var backendId = Value<string>(reader, "backend_id");
        var filename = Value<string>(reader, "display_filename");
        var provenance = reader.IsDBNull(reader.GetOrdinal("import_provenance")) ? null :
            InterchangeImportProvenance.Decode(Value<byte[]>(reader, "import_provenance"));
        IngestionIntent intent = Value<string>(reader, "operation_kind") switch
        {
            "create" when Optional<Guid>(reader, "target_mapping_id") is null && head is AnyHead =>
                IngestionIntent.ForCreate(new IngestionCreateMetadata(Value<string>(reader, "create_name"),
                    Value<string>(reader, "create_description")), backendId, filename, digest, byteCount, provenance),
            "append" when Text(reader, "create_name") is null && Text(reader, "create_description") is null =>
                IngestionIntent.ForAppend(new MappingScope(project.OrganizationId, project.ProjectId,
                    Value<Guid>(reader, "target_mapping_id")), backendId, filename, head, digest, byteCount, provenance),
            _ => throw InvalidMetadata()
        };
        if (Value<string>(reader, "native_class") != "native" ||
            Value<string>(reader, "logical_entry_document") != intent.LogicalDocument ||
            Value<string>(reader, "dependency_manifest_id") != intent.DependencyIdentity)
            throw InvalidMetadata();
        return new ReservedIngestionFacts(project, Value<Guid>(reader, "operation_id"),
            new PrincipalId(Value<Guid>(reader, "actor_principal_id")), Value<Guid>(reader, "idempotency_key"),
            CanonicalFingerprint.Restore(Value<int>(reader, "fingerprint_version"),
                Sha256Digest.Parse(Value<string>(reader, "intent_fingerprint"))), intent, scope,
            new PrivateObjectReference(PrivateObjectId.Restore(scope, Value<Guid>(reader, "native_object_id"),
                PrivateObjectClass.NativeArtifact), digest, byteCount),
            new IngestionBackendProfile(backendId, Value<string>(reader, "engine_revision"),
                Value<string>(reader, "backend_build"), Value<string>(reader, "adapter_build")),
            new IngestionAdmissionPolicy(Value<string>(reader, "admission_policy_id"),
                Value<long>(reader, "effective_native_byte_limit")), Value<DateTimeOffset>(reader, "created_at"));
    }

    private static CommittedRevisionFacts? ReadCommitted(NpgsqlDataReader reader, ReservedIngestionFacts facts)
    {
        if (Optional<Guid>(reader, "r_id") is not { } revisionId)
            return null;
        var scope = new RevisionScope(Value<Guid>(reader, "r_organization_id"), Value<Guid>(reader, "r_project_id"),
            Value<Guid>(reader, "r_mapping_id"), revisionId);
        var reference = new PrivateObjectReference(PrivateObjectId.Restore(scope,
            Value<Guid>(reader, "r_native_object_id"), PrivateObjectClass.NativeArtifact),
            Sha256Digest.Parse(Value<string>(reader, "r_native_digest")), Value<long>(reader, "r_native_byte_count"));
        if (scope != facts.FutureRevision || reference != facts.NativeObject ||
            Value<string>(reader, "r_native_class") != "native" ||
            Value<string>(reader, "r_backend_id") != facts.BackendProfile.BackendId)
            throw InvalidMetadata();
        return new CommittedRevisionFacts(scope, Value<long>(reader, "r_sequence"), reference, facts.BackendProfile,
            Value<string>(reader, "r_logical_entry_document"), Value<string>(reader, "r_dependency_manifest_id"),
            new PrincipalId(Value<Guid>(reader, "r_creator_principal_id")), Value<DateTimeOffset>(reader, "r_committed_at"),
            Value<Guid>(reader, "r_ingestion_operation_id"), Value<long>(reader, "r_initial_attempt_generation"),
            ParseAdmission(Value<string>(reader, "r_initial_admission")));
    }

    private static IngestionAttemptReceipt ReadAttempt(NpgsqlDataReader reader, ReservedIngestionFacts facts,
        long generation)
    {
        var kind = Text(reader, "a_completion_kind");
        var finishedAt = Optional<DateTimeOffset>(reader, "a_finished_at");
        IngestionAttemptCompletion? completion = kind switch
        {
            null when finishedAt is null => null,
            "validation" => ReadValidation(reader),
            "operational_failure" => IngestionAttemptCompletion.FromOperationalFailure(
                ParseFailure(Value<string>(reader, "a_last_error")), ReadProvenance(reader)),
            "reused_evidence" => IngestionAttemptCompletion.ReuseEligibleEvidence(
                Value<long>(reader, "a_reused_attempt_generation")),
            _ => throw InvalidMetadata()
        };
        var admission = Text(reader, "a_admission_classification") is { } value ? ParseAdmission(value) :
            (IngestionAdmissionClassification?)null;
        if (completion is not null && completion.Classify(facts) != admission)
            throw InvalidMetadata();
        return new IngestionAttemptReceipt(generation, Value<DateTimeOffset>(reader, "a_started_at"),
            finishedAt, completion, admission);
    }

    private static IngestionAttemptCompletion ReadValidation(NpgsqlDataReader reader)
    {
        var provenance = ReadProvenance(reader) ?? throw InvalidMetadata();
        var facts = new IngestionValidationFacts(ParseStatus(Value<string>(reader, "a_backend_status")), provenance,
            Optional<bool>(reader, "a_is_valid"), Value<int>(reader, "a_total_issue_count"),
            Value<int>(reader, "a_emitted_issue_count"), Value<bool>(reader, "a_issues_truncated"));
        var evidence = IngestionEvidenceDocument.Restore(facts,
            Encoding.UTF8.GetBytes(Value<string>(reader, "a_evidence")));
        if (evidence.Version != Value<string>(reader, "a_evidence_version"))
            throw InvalidMetadata();
        return IngestionAttemptCompletion.FromValidation(evidence);
    }

    private static IngestionProvenance? ReadProvenance(NpgsqlDataReader reader) =>
        Optional<bool>(reader, "a_provenance_confirmed") is { } confirmed
            ? new IngestionProvenance(new RevisionScope(Value<Guid>(reader, "a_provenance_organization_id"),
                Value<Guid>(reader, "a_provenance_project_id"), Value<Guid>(reader, "a_provenance_mapping_id"),
                Value<Guid>(reader, "a_provenance_revision_id")),
                Sha256Digest.Parse(Value<string>(reader, "a_provenance_native_digest")),
                new IngestionBackendProfile(Value<string>(reader, "a_provenance_backend_id"),
                    Value<string>(reader, "a_provenance_engine_revision"),
                    Value<string>(reader, "a_provenance_backend_build"),
                    Value<string>(reader, "a_provenance_adapter_build")), confirmed)
            : null;

    private static T Value<T>(NpgsqlDataReader reader, string name) => reader.GetFieldValue<T>(reader.GetOrdinal(name));
    private static T? Optional<T>(NpgsqlDataReader reader, string name) where T : struct =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : Value<T>(reader, name);
    private static string? Text(NpgsqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : Value<string>(reader, name);

    private static IngestionPhase ParsePhase(string value) => value switch
    {
        "reserved" => IngestionPhase.Reserved,
        "staged" => IngestionPhase.Staged,
        "ready" => IngestionPhase.Ready,
        "committed" => IngestionPhase.Committed,
        "rejected" => IngestionPhase.Rejected,
        "head_conflict" => IngestionPhase.HeadConflict,
        "attempt_exhausted" => IngestionPhase.AttemptExhausted,
        _ => throw InvalidMetadata()
    };

    private static IngestionAdmissionClassification ParseAdmission(string value) => value switch
    {
        "eligible_valid" => IngestionAdmissionClassification.EligibleValid,
        "eligible_decoded_invalid" => IngestionAdmissionClassification.EligibleDecodedInvalid,
        "rejected_native_decode" => IngestionAdmissionClassification.RejectedNativeDecode,
        "rejected_unsupported" => IngestionAdmissionClassification.RejectedUnsupported,
        "rejected_resource_limit" => IngestionAdmissionClassification.RejectedResourceLimit,
        "retryable_failure" => IngestionAdmissionClassification.RetryableFailure,
        "reused_evidence" => IngestionAdmissionClassification.ReusedEvidence,
        _ => throw InvalidMetadata()
    };

    private static IngestionBackendStatus ParseStatus(string value) => value switch
    {
        "succeeded" => IngestionBackendStatus.Succeeded,
        "invalid_mapping" => IngestionBackendStatus.InvalidMapping,
        "unsupported_artifact" => IngestionBackendStatus.UnsupportedArtifact,
        "resource_limit" => IngestionBackendStatus.ResourceLimit,
        "protocol_failure" => IngestionBackendStatus.ProtocolFailure,
        "backend_unavailable" => IngestionBackendStatus.BackendUnavailable,
        "backend_failure" => IngestionBackendStatus.BackendFailure,
        "timed_out" => IngestionBackendStatus.TimedOut,
        "canceled" => IngestionBackendStatus.Canceled,
        "isolation_failure" => IngestionBackendStatus.IsolationFailure,
        _ => throw InvalidMetadata()
    };

    private static IngestionFailureCode ParseFailure(string value) => value switch
    {
        "needs_source" => IngestionFailureCode.NeedsSource,
        "storage_integrity" => IngestionFailureCode.StorageIntegrity,
        "storage_io" => IngestionFailureCode.StorageIo,
        "backend_unavailable" => IngestionFailureCode.BackendUnavailable,
        "backend_failure" => IngestionFailureCode.BackendFailure,
        "timed_out" => IngestionFailureCode.TimedOut,
        "canceled" => IngestionFailureCode.Canceled,
        "isolation_failure" => IngestionFailureCode.IsolationFailure,
        "protocol_failure" => IngestionFailureCode.ProtocolFailure,
        "reserved_profile_unavailable" => IngestionFailureCode.ReservedProfileUnavailable,
        "evidence_limit" => IngestionFailureCode.EvidenceLimit,
        _ => throw InvalidMetadata()
    };
}
