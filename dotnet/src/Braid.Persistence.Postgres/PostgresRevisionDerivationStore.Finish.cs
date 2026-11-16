using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;
using static Braid.Persistence.Postgres.RevisionReportPersistence;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresRevisionDerivationStore
{
    private static async Task<RevisionReportStoreResult> FinishCoreAsync(PostgresTransaction transaction,
        ActorContext actor, RevisionReportClaim claim, RevisionReportCompletion completion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.RequireOrganizationAsync(claim.Key.Scope.OrganizationId, cancellationToken);
        var locked = await LockEntryAsync(transaction, actor, claim.Key, claim.CacheEntryId, cancellationToken);
        // The second statement starts after the row wait, observing revocation
        // committed while this operation was blocked instead of the old snapshot.
        var row = await ReadCurrentAsync(transaction, actor, claim.Key.Scope, claim.Key.Operation,
            claim.Key.Fingerprint.Value, claim.CacheEntryId, cancellationToken);
        if (row is null) return await DeniedAsync(transaction, cancellationToken);
        var entry = row.Entry;
        var current = row.Current;
        if (locked is null || entry is null || entry.Key != claim.Key || current is null || current.Claim != claim ||
            current.Summary.Completion is not null || row.Source.ObservedAtUtc >= claim.LeaseExpiresAtUtc)
            return RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.ClaimLost);

        await using var command = transaction.CreateCommand(AuthorizedSourceSql + $$"""
            , finished AS (
                UPDATE braid.revision_derivation_attempts attempt SET
                    completion_kind=@completionKind,failure_code=@failureCode,observation_present=@observationPresent,
                    backend_status=@backendStatus,provenance_confirmed=@provenanceConfirmed,
                    provenance_organization_id=@provenanceOrganizationId,provenance_project_id=@provenanceProjectId,
                    provenance_mapping_id=@provenanceMappingId,provenance_revision_id=@provenanceRevisionId,
                    provenance_native_digest=@provenanceNativeDigest,provenance_backend_id=@provenanceBackendId,
                    provenance_engine_revision=@provenanceEngineRevision,provenance_backend_build=@provenanceBackendBuild,
                    provenance_adapter_build=@provenanceAdapterBuild,projection_coverage=@projectionCoverage,
                    document_item_count=@documentItemCount,document_diagnostic_count=@documentDiagnosticCount,
                    coverage_gap_count=@coverageGapCount,operation_diagnostic_count=@operationDiagnosticCount,
                    is_valid=@isValid,total_issue_count=@totalIssueCount,emitted_issue_count=@emittedIssueCount,
                    issues_truncated=@issuesTruncated,validation_diagnostic_count=@validationDiagnosticCount,
                    derived_digest=@derivedDigest,derived_byte_count=@derivedByteCount
                FROM braid.revision_derivations entry JOIN authorized_source source ON {{SourceMatchesEntrySql}},observed
                WHERE attempt.organization_id=entry.organization_id AND attempt.project_id=entry.project_id
                  AND attempt.mapping_id=entry.mapping_id AND attempt.revision_id=entry.revision_id
                  AND attempt.cache_entry_id=entry.cache_entry_id AND entry.cache_entry_id=@cacheEntryId
                  AND entry.operation_kind=@operation AND entry.key_fingerprint=@fingerprint
                  AND entry.current_generation=@generation AND entry.current_attempt_id=@derivationId
                  AND attempt.generation=@generation AND attempt.derivation_id=@derivationId
                  AND attempt.actor_principal_id=@actorId AND attempt.claimant_instance_id=@instanceId
                  AND attempt.claim_token=@claimToken AND attempt.candidate_object_id=@candidateObjectId
                  AND attempt.started_at=@startedAt AND attempt.lease_expires_at=@expiresAt
                  AND attempt.repair_target_derivation_id IS NOT DISTINCT FROM @repairTarget::uuid
                  AND attempt.completion_kind IS NULL AND attempt.finished_at IS NULL
                  AND observed.observed_at < attempt.lease_expires_at
                RETURNING attempt.*
            )
            SELECT (SELECT count(*) FROM authorized_source) AS authorized_count,
                (SELECT to_jsonb(finished)::text FROM finished) AS attempt_row
            """);
        AddKey(command, actor, claim.Key);
        Add(command, "cacheEntryId", NpgsqlDbType.Uuid, claim.CacheEntryId);
        Add(command, "generation", NpgsqlDbType.Bigint, claim.Generation);
        Add(command, "derivationId", NpgsqlDbType.Uuid, claim.DerivationId);
        Add(command, "instanceId", NpgsqlDbType.Uuid, claim.ServerInstanceId);
        Add(command, "claimToken", NpgsqlDbType.Uuid, claim.LeaseToken);
        Add(command, "candidateObjectId", NpgsqlDbType.Uuid, claim.CandidateObjectId.ObjectId);
        Add(command, "startedAt", NpgsqlDbType.TimestampTz, claim.StartedAtUtc);
        Add(command, "expiresAt", NpgsqlDbType.TimestampTz, claim.LeaseExpiresAtUtc);
        Add(command, "repairTarget", NpgsqlDbType.Uuid, claim.RepairTargetDerivationId);
        AddCompletion(command, completion);
        long authorized;
        PersistedAttempt? finished;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
            authorized = reader.GetInt64(0);
            finished = reader.IsDBNull(1) ? null : Read(() => Attempt(Parse(reader.GetString(1)), entry));
            if (await reader.ReadAsync(cancellationToken) || authorized is < 0 or > 1) throw InvalidFacts();
        }
        if (authorized == 0) return await DeniedAsync(transaction, cancellationToken);
        if (finished is null) return RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.ClaimLost);
        if (finished.Claim != claim || finished.Summary.Completion != completion) throw InvalidFacts();
        if (completion.IsUsable)
        {
            await using var select = transaction.CreateCommand($"""
                UPDATE braid.revision_derivations SET selected_derivation_id=@derivationId
                WHERE {ScopeSql} AND cache_entry_id=@cacheEntryId AND current_generation=@generation
                  AND current_attempt_id=@derivationId AND selected_derivation_id IS NULL
                """);
            AddScope(select, actor, claim.Key.Scope);
            Add(select, "cacheEntryId", NpgsqlDbType.Uuid, claim.CacheEntryId);
            Add(select, "generation", NpgsqlDbType.Bigint, claim.Generation);
            Add(select, "derivationId", NpgsqlDbType.Uuid, claim.DerivationId);
            if (await select.ExecuteNonQueryAsync(cancellationToken) != 1) throw InvalidFacts();
        }
        var applied = await ReadCurrentAsync(transaction, actor, claim.Key.Scope, claim.Key.Operation,
            claim.Key.Fingerprint.Value, claim.CacheEntryId, cancellationToken);
        if (applied is null) return await DeniedAsync(transaction, cancellationToken);
        var snapshot = Read(() => applied.Snapshot(claim.Key));
        var appliedCurrent = applied.Current;
        if (appliedCurrent is null || appliedCurrent.Claim != claim || appliedCurrent.Summary != finished.Summary ||
            applied.Selected?.Result != finished.Result) throw InvalidFacts();
        return Read(() => RevisionReportStoreResult.ForSnapshot(RevisionReportStoreOutcome.Applied, snapshot));
    }

    private static void AddCompletion(NpgsqlCommand command, RevisionReportCompletion completion)
    {
        var observation = completion.Observation;
        var provenance = observation?.Provenance;
        var projection = observation?.Projection;
        var validation = observation?.Validation;
        Add(command, "completionKind", NpgsqlDbType.Text, completion.IsUsable ? "usable" : "failed");
        Add(command, "failureCode", NpgsqlDbType.Text, completion.Failure is { } failure ? FailureText(failure) : null);
        Add(command, "observationPresent", NpgsqlDbType.Boolean, observation is not null);
        Add(command, "backendStatus", NpgsqlDbType.Text, observation?.Status is { } status ? StatusText(status) : null);
        Add(command, "provenanceConfirmed", NpgsqlDbType.Boolean, provenance?.Confirmed);
        Add(command, "provenanceOrganizationId", NpgsqlDbType.Uuid, provenance?.Scope.OrganizationId);
        Add(command, "provenanceProjectId", NpgsqlDbType.Uuid, provenance?.Scope.ProjectId);
        Add(command, "provenanceMappingId", NpgsqlDbType.Uuid, provenance?.Scope.MappingId);
        Add(command, "provenanceRevisionId", NpgsqlDbType.Uuid, provenance?.Scope.RevisionId);
        Add(command, "provenanceNativeDigest", NpgsqlDbType.Text, provenance?.NativeDigest.Value);
        Add(command, "provenanceBackendId", NpgsqlDbType.Text, provenance?.Profile.BackendId);
        Add(command, "provenanceEngineRevision", NpgsqlDbType.Text, provenance?.Profile.BackendRevision);
        Add(command, "provenanceBackendBuild", NpgsqlDbType.Text, provenance?.Profile.BackendBuild);
        Add(command, "provenanceAdapterBuild", NpgsqlDbType.Text, provenance?.Profile.AdapterBuild);
        Add(command, "projectionCoverage", NpgsqlDbType.Text, projection?.Coverage switch
        {
            null => null,
            RevisionReportCoverage.Complete => "complete",
            RevisionReportCoverage.Partial => "partial",
            RevisionReportCoverage.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(completion))
        });
        Add(command, "documentItemCount", NpgsqlDbType.Integer, projection?.DocumentItemCount);
        Add(command, "documentDiagnosticCount", NpgsqlDbType.Integer, projection?.DocumentDiagnosticCount);
        Add(command, "coverageGapCount", NpgsqlDbType.Integer, projection?.CoverageGapCount);
        Add(command, "operationDiagnosticCount", NpgsqlDbType.Integer, projection?.OperationDiagnosticCount);
        Add(command, "isValid", NpgsqlDbType.Boolean, validation?.IsValid);
        Add(command, "totalIssueCount", NpgsqlDbType.Integer, validation?.TotalIssueCount);
        Add(command, "emittedIssueCount", NpgsqlDbType.Integer, validation?.EmittedIssueCount);
        Add(command, "issuesTruncated", NpgsqlDbType.Boolean, validation?.Truncated);
        Add(command, "validationDiagnosticCount", NpgsqlDbType.Integer, validation?.DiagnosticCount);
        Add(command, "derivedDigest", NpgsqlDbType.Text, completion.DerivedObject?.Digest.Value);
        Add(command, "derivedByteCount", NpgsqlDbType.Bigint, completion.DerivedObject?.ByteCount);
    }

    private static string StatusText(RevisionReportBackendStatus value) => value switch
    {
        RevisionReportBackendStatus.Succeeded => "succeeded",
        RevisionReportBackendStatus.InvalidMapping => "invalid_mapping",
        RevisionReportBackendStatus.UnsupportedArtifact => "unsupported_artifact",
        RevisionReportBackendStatus.ResourceLimit => "resource_limit",
        RevisionReportBackendStatus.ProtocolFailure => "protocol_failure",
        RevisionReportBackendStatus.BackendUnavailable => "backend_unavailable",
        RevisionReportBackendStatus.BackendFailure => "backend_failure",
        RevisionReportBackendStatus.TimedOut => "timed_out",
        RevisionReportBackendStatus.Canceled => "canceled",
        RevisionReportBackendStatus.IsolationFailure => "isolation_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string FailureText(RevisionReportFailureCategory value) => value switch
    {
        RevisionReportFailureCategory.ObjectUnavailable => "object_unavailable",
        RevisionReportFailureCategory.StorageIntegrity => "storage_integrity",
        RevisionReportFailureCategory.StorageIo => "storage_io",
        RevisionReportFailureCategory.BackendUnavailable => "backend_unavailable",
        RevisionReportFailureCategory.BackendFailure => "backend_failure",
        RevisionReportFailureCategory.TimedOut => "timed_out",
        RevisionReportFailureCategory.Canceled => "canceled",
        RevisionReportFailureCategory.IsolationFailure => "isolation_failure",
        RevisionReportFailureCategory.ProtocolFailure => "protocol_failure",
        RevisionReportFailureCategory.ProfileUnavailable => "profile_unavailable",
        RevisionReportFailureCategory.ResourceLimit => "resource_limit",
        RevisionReportFailureCategory.UnsupportedArtifact => "unsupported_artifact",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
