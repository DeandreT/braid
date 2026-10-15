using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;
using static Weft.Persistence.Postgres.PrivateObjectMaintenancePersistence;

namespace Weft.Persistence.Postgres;

internal sealed partial class PostgresPrivateObjectPublicationRegistry
{
    private async Task PrepareAsync(PrivatePublicationTransaction transaction, PrivatePublicationOwner owner,
        PrivatePublicationKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.RequireOrganizationAsync(owner.Project.OrganizationId, cancellationToken);
        var originSql = _kind == PrivateObjectOwnerKind.NativeIngestion
            ? "SELECT operation_id FROM weft.revision_ingestions WHERE organization_id=@organizationId AND project_id=@projectId AND operation_id=@operationId FOR UPDATE"
            : "SELECT cache_entry_id FROM weft.revision_derivations WHERE organization_id=@organizationId AND project_id=@projectId AND mapping_id=@mappingId AND revision_id=@revisionId AND cache_entry_id=@cacheEntryId FOR UPDATE";
        await using (var origin = transaction.CreateCommand(originSql))
        {
            Parameters(origin, owner, key);
            _ = await origin.ExecuteScalarAsync(cancellationToken);
        }
        // The same origin-first/publication-second order is used by retirement. Both waits
        // finish before Observe's separate SQL statement obtains fresh authority/protection.
        await using var publication = transaction.CreateCommand("""
            SELECT publication_id FROM weft.private_object_publications
            WHERE organization_id=@organizationId AND project_id=@projectId AND mapping_id=@mappingId AND revision_id=@revisionId
              AND object_class=@objectClass AND object_id=@objectId AND root_id=@rootId AND temp_id=@tempId
            FOR UPDATE
            """);
        Parameters(publication, owner, key);
        _ = await publication.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<PublicationObservation> ObserveAsync(PrivatePublicationTransaction transaction,
        PrivatePublicationOwner owner, PrivatePublicationKey key, CancellationToken cancellationToken)
    {
        await transaction.RequireOrganizationAsync(owner.Project.OrganizationId, cancellationToken);
        var ownerSql = _kind == PrivateObjectOwnerKind.NativeIngestion ? NativeOwnerSql : ReportOwnerSql;
        var recordSql = _kind == PrivateObjectOwnerKind.NativeIngestion ? NativeRecordSql : ReportRecordSql;
        var recordJoins = _kind == PrivateObjectOwnerKind.NativeIngestion ? NativeRecordJoins : ReportRecordJoins;
        await using var command = transaction.CreateCommand($$"""
            WITH observed AS MATERIALIZED (SELECT clock_timestamp() AS observed_at)
            SELECT jsonb_build_object(
                'record',CASE WHEN publication.publication_id IS NULL THEN NULL ELSE to_jsonb(publication) || {{recordSql}} END,
                'observed_at',observed.observed_at,
                'owner_is_live',EXISTS ({{ownerSql}}),
                'final_object_retired',EXISTS (
                    SELECT 1 FROM weft.private_object_retirements retired
                    WHERE retired.organization_id=@organizationId AND retired.project_id=@projectId
                      AND retired.mapping_id=@mappingId AND retired.revision_id=@revisionId
                      AND retired.object_class=@objectClass AND retired.object_id=@objectId AND retired.target_kind='final_object'),
                'temporary_retired',EXISTS (
                    SELECT 1 FROM weft.private_object_retirements retired
                    WHERE retired.organization_id=@organizationId AND retired.project_id=@projectId
                      AND retired.mapping_id=@mappingId AND retired.revision_id=@revisionId
                      AND retired.object_class=@objectClass AND retired.object_id=@objectId
                      AND retired.target_kind='tracked_temporary' AND retired.publication_id=publication.publication_id))::text
            FROM observed
            LEFT JOIN weft.private_object_publications publication ON publication.organization_id=@organizationId
              AND publication.project_id=@projectId AND publication.mapping_id=@mappingId AND publication.revision_id=@revisionId
              AND publication.object_class=@objectClass AND publication.object_id=@objectId
              AND publication.root_id=@rootId AND publication.temp_id=@tempId
            {{recordJoins}}
            """);
        Parameters(command, owner, key);
        var json = await ReadJsonAsync(command, cancellationToken) ?? throw InvalidFacts();
        var result = Restore(json, row => new PublicationObservation(Null(row, "record") ? null : Publication(row.GetProperty("record")),
            Time(row, "observed_at"), row.GetProperty("owner_is_live").GetBoolean(),
            row.GetProperty("final_object_retired").GetBoolean(), row.GetProperty("temporary_retired").GetBoolean()));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    // Each ordinary login reads only its own source family; neither adapter requires
    // access to the other owner's ingestion or report tables.
    private const string NativeRecordSql = """
        jsonb_build_object(
            'ingestion',CASE WHEN ingestion.operation_id IS NULL THEN NULL ELSE jsonb_build_object(
                'organization_id',ingestion.organization_id,'project_id',ingestion.project_id,'operation_id',ingestion.operation_id,
                'future_mapping_id',ingestion.future_mapping_id,'future_revision_id',ingestion.future_revision_id,
                'native_object_id',ingestion.native_object_id,'native_class',ingestion.native_class,
                'actor_principal_id',ingestion.actor_principal_id) END,
            'owner_ingestion_attempt',CASE WHEN native_attempt.operation_id IS NULL THEN NULL ELSE jsonb_build_object(
                'organization_id',native_attempt.organization_id,'project_id',native_attempt.project_id,
                'operation_id',native_attempt.operation_id,'generation',native_attempt.generation,
                'claimant_instance_id',native_attempt.claimant_instance_id,'claim_token',native_attempt.claim_token) END)
        """;

    private const string NativeRecordJoins = """
        LEFT JOIN weft.revision_ingestions ingestion ON ingestion.organization_id=publication.organization_id
          AND ingestion.project_id=publication.project_id AND ingestion.operation_id=publication.ingestion_operation_id
        LEFT JOIN weft.revision_ingestion_attempts native_attempt ON native_attempt.organization_id=publication.organization_id
          AND native_attempt.project_id=publication.project_id AND native_attempt.operation_id=publication.ingestion_operation_id
          AND native_attempt.generation=publication.owner_generation
        """;

    private const string ReportRecordSql = """
        jsonb_build_object('report_entry',to_jsonb(report_entry),
            'owner_report_attempt',CASE WHEN report_attempt.derivation_id IS NULL THEN NULL ELSE jsonb_build_object(
                'organization_id',report_attempt.organization_id,'project_id',report_attempt.project_id,
                'mapping_id',report_attempt.mapping_id,'revision_id',report_attempt.revision_id,
                'cache_entry_id',report_attempt.cache_entry_id,'derivation_id',report_attempt.derivation_id,
                'generation',report_attempt.generation,'actor_principal_id',report_attempt.actor_principal_id,
                'claimant_instance_id',report_attempt.claimant_instance_id,'claim_token',report_attempt.claim_token,
                'started_at',report_attempt.started_at,'lease_expires_at',report_attempt.lease_expires_at,
                'candidate_object_id',report_attempt.candidate_object_id,'derived_class',report_attempt.derived_class,
                'repair_target_derivation_id',report_attempt.repair_target_derivation_id) END)
        """;

    private const string ReportRecordJoins = """
        LEFT JOIN weft.revision_derivations report_entry ON report_entry.organization_id=publication.organization_id
          AND report_entry.project_id=publication.project_id AND report_entry.mapping_id=publication.mapping_id
          AND report_entry.revision_id=publication.revision_id AND report_entry.cache_entry_id=publication.cache_entry_id
        LEFT JOIN weft.revision_derivation_attempts report_attempt ON report_attempt.organization_id=publication.organization_id
          AND report_attempt.project_id=publication.project_id AND report_attempt.mapping_id=publication.mapping_id
          AND report_attempt.revision_id=publication.revision_id AND report_attempt.cache_entry_id=publication.cache_entry_id
          AND report_attempt.derivation_id=publication.derivation_id AND report_attempt.generation=publication.owner_generation
        """;

    private const string NativeOwnerSql = """
        SELECT 1 FROM weft.revision_ingestions owner
        JOIN weft.projects project ON project.organization_id=owner.organization_id AND project.id=owner.project_id
        JOIN weft.principals principal ON principal.id=@actorId AND principal.active
        JOIN weft.organization_memberships admission ON admission.organization_id=project.organization_id
          AND admission.principal_id=principal.id AND admission.active
        JOIN weft.project_memberships membership ON membership.organization_id=project.organization_id
          AND membership.project_id=project.id AND membership.principal_id=principal.id AND membership.active
          AND membership.role IN ('publisher','administrator')
        JOIN weft.revision_ingestion_attempts actual ON actual.organization_id=owner.organization_id
          AND actual.project_id=owner.project_id AND actual.operation_id=owner.operation_id AND actual.generation=owner.lease_generation
        WHERE owner.organization_id=@organizationId AND owner.project_id=@projectId AND owner.operation_id=@operationId
          AND owner.actor_principal_id=@actorId AND owner.future_mapping_id=@mappingId AND owner.future_revision_id=@revisionId
          AND owner.native_object_id=@objectId AND owner.native_class=@objectClass AND owner.phase IN ('reserved','staged','ready')
          AND owner.lease_generation=@ownerGeneration AND owner.lease_owner_id=@instanceId AND owner.lease_token=@leaseToken
          AND owner.lease_expires_at=@expiresAt AND observed.observed_at<owner.lease_expires_at
          AND actual.claimant_instance_id=@instanceId AND actual.claim_token=@leaseToken
        """;

    private const string ReportOwnerSql = """
        SELECT 1 FROM weft.revision_derivations owner
        JOIN weft.revision_derivation_attempts actual ON actual.organization_id=owner.organization_id
          AND actual.project_id=owner.project_id AND actual.mapping_id=owner.mapping_id AND actual.revision_id=owner.revision_id
          AND actual.cache_entry_id=owner.cache_entry_id AND actual.derivation_id=owner.current_attempt_id AND actual.generation=owner.current_generation
        JOIN weft.projects project ON project.organization_id=owner.organization_id AND project.id=owner.project_id
        JOIN weft.principals principal ON principal.id=@actorId AND principal.active
        JOIN weft.organization_memberships admission ON admission.organization_id=project.organization_id
          AND admission.principal_id=principal.id AND admission.active
        JOIN weft.project_memberships membership ON membership.organization_id=project.organization_id
          AND membership.project_id=project.id AND membership.principal_id=principal.id AND membership.active
          AND membership.role IN ('viewer','runner','publisher','administrator')
        JOIN weft.mappings mapping ON mapping.organization_id=project.organization_id AND mapping.project_id=project.id AND mapping.id=owner.mapping_id
        JOIN weft.mapping_revisions source ON source.organization_id=owner.organization_id AND source.project_id=owner.project_id
          AND source.mapping_id=owner.mapping_id AND source.id=owner.revision_id
          AND source.native_object_id=owner.native_object_id AND source.native_class=owner.native_class
          AND source.native_digest=owner.native_digest AND source.native_byte_count=owner.native_byte_count
          AND source.logical_entry_document=owner.logical_entry_document AND source.dependency_manifest_id=owner.dependency_manifest_id
          AND source.backend_id=owner.backend_id
        WHERE owner.organization_id=@organizationId AND owner.project_id=@projectId AND owner.mapping_id=@mappingId AND owner.revision_id=@revisionId
          AND owner.cache_entry_id=@cacheEntryId AND owner.key_fingerprint=@keyFingerprint AND owner.operation_kind=@operation
          AND owner.current_generation=@ownerGeneration AND owner.current_attempt_id=@derivationId
          AND actual.actor_principal_id=@actorId AND actual.claimant_instance_id=@instanceId AND actual.claim_token=@leaseToken
          AND actual.started_at=@startedAt AND actual.lease_expires_at=@expiresAt AND observed.observed_at<actual.lease_expires_at
          AND actual.repair_target_derivation_id IS NOT DISTINCT FROM @repairTarget::uuid
          AND actual.candidate_object_id=@objectId AND actual.derived_class=@objectClass
          AND actual.finished_at IS NULL AND actual.completion_kind IS NULL
        """;

    private static void Parameters(NpgsqlCommand command, PrivatePublicationOwner owner, PrivatePublicationKey key)
    {
        AddProject(command, owner.Project);
        Add(command, "mappingId", NpgsqlDbType.Uuid, key.Scope.MappingId);
        Add(command, "revisionId", NpgsqlDbType.Uuid, key.Scope.RevisionId);
        Add(command, "objectClass", NpgsqlDbType.Text, ObjectClass(key.Object.Class));
        Add(command, "objectId", NpgsqlDbType.Uuid, key.Object.ObjectId);
        Add(command, "rootId", NpgsqlDbType.Uuid, key.RootId);
        Add(command, "tempId", NpgsqlDbType.Uuid, key.TempId);
        Add(command, "ownerKind", NpgsqlDbType.Text, OwnerKind(owner.Kind));
        Add(command, "actorId", NpgsqlDbType.Uuid, owner.OriginalActor.Value);
        Add(command, "operationId", NpgsqlDbType.Uuid, owner.IngestionClaim?.OperationId);
        Add(command, "cacheEntryId", NpgsqlDbType.Uuid, owner.ReportClaim?.CacheEntryId);
        Add(command, "derivationId", NpgsqlDbType.Uuid, owner.ReportClaim?.DerivationId);
        Add(command, "ownerGeneration", NpgsqlDbType.Bigint, owner.Generation);
        Add(command, "instanceId", NpgsqlDbType.Uuid, owner.ServerInstanceId);
        Add(command, "leaseToken", NpgsqlDbType.Uuid, owner.LeaseToken);
        Add(command, "expiresAt", NpgsqlDbType.TimestampTz, owner.LeaseExpiresAtUtc);
        Add(command, "keyFingerprint", NpgsqlDbType.Text, owner.ReportClaim?.Key.Fingerprint.Value);
        Add(command, "operation", NpgsqlDbType.Text, owner.ReportClaim is { } report ? RevisionReportPersistence.Operation(report.Key.Operation) : null);
        Add(command, "startedAt", NpgsqlDbType.TimestampTz, owner.ReportClaim?.StartedAtUtc);
        Add(command, "repairTarget", NpgsqlDbType.Uuid, owner.ReportClaim?.RepairTargetDerivationId);
    }

    private sealed record PublicationObservation(PrivatePublicationRecord? Record, DateTimeOffset ObservedAtUtc,
        bool OwnerLive, bool FinalRetired, bool TempRetired)
    {
        internal PrivatePublicationSnapshot Snapshot() => new(Record ?? throw InvalidFacts(), ObservedAtUtc,
            OwnerLive, FinalRetired, TempRetired);
    }
}
