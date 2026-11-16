using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;
using static Braid.Persistence.Postgres.PrivateObjectMaintenancePersistence;

namespace Braid.Persistence.Postgres;

/// <summary>Read-only flat observations, distinct from claims, source restoration and cleanup authority.</summary>
public sealed partial class PostgresPrivateObjectMaintenanceStore : IPrivateObjectMaintenanceStatusReader,
    IPrivateObjectPublicationObservationReader
{
    public Task<ObjectCleanupStatusPage> GetStatusesAsync(ProjectScope project,
        ObjectCleanupStatusCursor? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (after is not null && after.Project != project)
            throw new ArgumentException("A maintenance observation cursor must belong to its requested project.", nameof(after));
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(project, cancellationToken);
            await using var command = transaction.CreateCommand("""
                WITH observed AS MATERIALIZED (SELECT clock_timestamp() AS observed_at),
                page AS MATERIALIZED (
                  SELECT r.organization_id,r.project_id,r.mapping_id,r.revision_id,r.object_class,r.object_id,
                    r.retirement_id,r.target_kind,r.publication_id,r.retired_at,r.current_generation,
                    r.current_attempt_id,r.completed_attempt_id,a.attempt_id,a.generation,a.started_at,
                    a.lease_expires_at,a.finished_at,a.disposition,a.success
                  FROM braid.private_object_retirements r
                  LEFT JOIN braid.private_object_cleanup_attempts a ON
                    (a.organization_id,a.project_id,a.mapping_id,a.revision_id,a.object_class,a.object_id,
                     a.retirement_id,a.generation,a.attempt_id) =
                    (r.organization_id,r.project_id,r.mapping_id,r.revision_id,r.object_class,r.object_id,
                     r.retirement_id,r.current_generation,r.current_attempt_id)
                  WHERE r.organization_id=@organizationId AND r.project_id=@projectId
                    AND (@after::uuid IS NULL OR r.retirement_id>@after)
                  ORDER BY r.retirement_id LIMIT 100)
                SELECT o.observed_at,p.* FROM observed o LEFT JOIN page p ON true ORDER BY p.retirement_id
                """);
            AddProject(command, project);
            Add(command, "after", NpgsqlDbType.Uuid, after?.AfterRetirementId);
            var headers = new List<ObjectCleanupStatusHeader>();
            DateTimeOffset? observed = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var at = reader.GetFieldValue<DateTimeOffset>(0);
                    if (observed is { } first && first != at) throw InvalidFacts();
                    observed = at;
                    if (reader.IsDBNull(7))
                    {
                        if (headers.Count != 0) throw InvalidFacts();
                        for (var column = 1; column < reader.FieldCount; column++)
                            if (!reader.IsDBNull(column)) throw InvalidFacts();
                        continue;
                    }
                    if (headers.Count == 100 || reader.GetGuid(1) != project.OrganizationId || reader.GetGuid(2) != project.ProjectId)
                        throw InvalidFacts();
                    var id = reader.GetGuid(7);
                    if ((after is not null && CompareObservationIds(id, after.AfterRetirementId) <= 0) ||
                        (headers.Count != 0 && CompareObservationIds(headers[^1].RetirementId, id) >= 0))
                        throw InvalidFacts();
                    headers.Add(ObservationFacts(() => ReadStatus(reader, at)));
                }
            }
            var page = ObservationFacts(() => new ObjectCleanupStatusPage(project, observed ?? throw InvalidFacts(), headers, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return page;
        }, cancellationToken);
    }

    public Task<PrivatePublicationObservationBatch> MatchAsync(PublicationObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(async () =>
        {
            var json = ObservationInputs(request, cancellationToken);
            await using var transaction = await _database.BeginTransactionAsync(request.Project, cancellationToken);
            await using var command = transaction.CreateCommand("""
                WITH observed AS MATERIALIZED (SELECT clock_timestamp() AS observed_at),
                inputs AS MATERIALIZED (
                  SELECT * FROM jsonb_to_recordset(@slots::jsonb) AS i(position integer,ordinal integer,
                    mapping_id uuid,revision_id uuid,object_class text,object_id uuid,temp_id uuid,root_id uuid,
                    control_namespace_id uuid,linux_boot_id uuid,mount_namespace_device numeric,mount_namespace_inode numeric,
                    effective_user_id bigint,file_inode numeric,file_device_major bigint,file_device_minor bigint,file_mount_id numeric,
                    directory_inode numeric,directory_device_major bigint,directory_device_minor bigint,directory_mount_id numeric))
                SELECT o.observed_at,i.position,i.ordinal,w.witnesses::text
                FROM observed o LEFT JOIN inputs i ON true
                LEFT JOIN LATERAL (
                  SELECT coalesce(jsonb_agg(jsonb_build_object(
                    'publication_id',p.publication_id,'organization_id',p.organization_id,'project_id',p.project_id,
                    'mapping_id',p.mapping_id,'revision_id',p.revision_id,'object_class',p.object_class,'object_id',p.object_id,
                    'root_id',p.root_id,'temp_id',p.temp_id,'control_namespace_id',p.control_namespace_id,'linux_boot_id',p.linux_boot_id,
                    'mount_namespace_device',p.mount_namespace_device,'mount_namespace_inode',p.mount_namespace_inode,
                    'effective_user_id',p.effective_user_id,'file_inode',p.file_inode,'file_device_major',p.file_device_major,
                    'file_device_minor',p.file_device_minor,'file_mount_id',p.file_mount_id,'directory_inode',p.directory_inode,
                    'directory_device_major',p.directory_device_major,'directory_device_minor',p.directory_device_minor,
                    'directory_mount_id',p.directory_mount_id,'owner_kind',p.owner_kind,'ingestion_operation_id',p.ingestion_operation_id,
                    'cache_entry_id',p.cache_entry_id,'derivation_id',p.derivation_id,'owner_generation',p.owner_generation)
                    ORDER BY p.object_id,p.temp_id),'[]'::jsonb) AS witnesses
                  FROM (
                    SELECT p.* FROM braid.private_object_publications p
                    WHERE p.organization_id=@organizationId AND p.project_id=@projectId
                      AND p.mapping_id=i.mapping_id AND p.revision_id=i.revision_id AND p.object_class=i.object_class
                      AND p.root_id=i.root_id AND p.bound_at IS NOT NULL
                      AND ((i.object_id IS NOT NULL AND p.object_id=i.object_id) OR
                           (i.temp_id IS NOT NULL AND p.temp_id=i.temp_id))
                      AND p.control_namespace_id=i.control_namespace_id AND p.linux_boot_id=i.linux_boot_id
                      AND p.mount_namespace_device=i.mount_namespace_device AND p.mount_namespace_inode=i.mount_namespace_inode
                      AND p.effective_user_id=i.effective_user_id AND p.file_inode=i.file_inode
                      AND p.file_device_major=i.file_device_major AND p.file_device_minor=i.file_device_minor
                      AND p.file_mount_id=i.file_mount_id AND p.directory_inode=i.directory_inode
                      AND p.directory_device_major=i.directory_device_major AND p.directory_device_minor=i.directory_device_minor
                      AND p.directory_mount_id=i.directory_mount_id
                    -- Final object_id is fixed, so this is temp_id order on the natural-key index.
                    -- A temporary slot has no object_id and retains object_id,temp_id order.
                    ORDER BY p.object_id,p.temp_id LIMIT 2
                  ) p
                ) w ON true ORDER BY i.position
                """);
            AddProject(command, request.Project);
            Add(command, "slots", NpgsqlDbType.Jsonb, json);
            var results = new List<PublicationSlotObservation>();
            DateTimeOffset? observed = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var at = reader.GetFieldValue<DateTimeOffset>(0);
                    if (observed is { } first && first != at) throw InvalidFacts();
                    observed = at;
                    if (reader.IsDBNull(1))
                    {
                        if (request.Slots.Count != 0 || results.Count != 0 || !reader.IsDBNull(2) || reader.GetString(3) != "[]")
                            throw InvalidFacts();
                        continue;
                    }
                    var index = reader.GetInt32(1);
                    if (index != results.Count || index >= request.Slots.Count || reader.GetInt32(2) != request.Slots[index].Ordinal)
                        throw InvalidFacts();
                    var witnesses = ReadWitnesses(reader.GetString(3), cancellationToken);
                    var outcome = witnesses.Count switch
                    {
                        0 => PublicationObservationOutcome.NoMatch,
                        1 => PublicationObservationOutcome.ExactlyOne,
                        2 => PublicationObservationOutcome.Ambiguous,
                        _ => throw InvalidFacts()
                    };
                    results.Add(ObservationFacts(() => new PublicationSlotObservation(request.Slots[index].Ordinal, outcome, witnesses, cancellationToken)));
                }
            }
            var batch = ObservationFacts(() => new PrivatePublicationObservationBatch(request, observed ?? throw InvalidFacts(), results, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return batch;
        }, cancellationToken);
    }

    private static ObjectCleanupStatusHeader ReadStatus(NpgsqlDataReader row, DateTimeOffset observed)
    {
        var scope = new RevisionScope(row.GetGuid(1), row.GetGuid(2), row.GetGuid(3), row.GetGuid(4));
        var target = new ObjectCleanupTarget(PrivateObjectId.Restore(scope, row.GetGuid(6), ObservationClass(row.GetString(5))),
            row.GetString(8) switch { "final_object" => CleanupTargetKind.FinalObject, "tracked_temporary" => CleanupTargetKind.TrackedTemporary, _ => throw InvalidFacts() },
            row.IsDBNull(9) ? null : row.GetGuid(9));
        CleanupAttemptStatusHeader? attempt = null;
        if (!row.IsDBNull(14))
        {
            var disposition = row.IsDBNull(19) ? (ObjectCleanupDisposition?)null : Disposition(row.GetString(19));
            if (row.IsDBNull(12) || row.GetGuid(12) != row.GetGuid(14) ||
                row.GetBoolean(20) != (disposition is ObjectCleanupDisposition.Deleted or ObjectCleanupDisposition.Missing)) throw InvalidFacts();
            attempt = new(row.GetGuid(14), row.GetInt64(15), row.GetFieldValue<DateTimeOffset>(16), row.GetFieldValue<DateTimeOffset>(17),
                row.IsDBNull(18) ? null : row.GetFieldValue<DateTimeOffset>(18), disposition);
        }
        else
        {
            if (!row.IsDBNull(12)) throw InvalidFacts();
            for (var column = 15; column <= 20; column++) if (!row.IsDBNull(column)) throw InvalidFacts();
        }
        return new(row.GetGuid(7), target, row.GetFieldValue<DateTimeOffset>(10), row.GetInt64(11), attempt,
            row.IsDBNull(13) ? null : row.GetGuid(13), observed);
    }

    private static string ObservationInputs(PublicationObservationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            for (var index = 0; index < request.Slots.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var slot = request.Slots[index];
                var actual = slot.ActualIdentity;
                writer.WriteStartObject();
                writer.WriteNumber("position", index); writer.WriteNumber("ordinal", slot.Ordinal);
                writer.WriteString("mapping_id", slot.Scope.MappingId); writer.WriteString("revision_id", slot.Scope.RevisionId);
                writer.WriteString("object_class", ObjectClass(slot.ObjectClass));
                if (slot.ObjectId is { } objectId) writer.WriteString("object_id", objectId.ObjectId); else writer.WriteNull("object_id");
                if (slot.TempId is { } temp) writer.WriteString("temp_id", temp); else writer.WriteNull("temp_id");
                writer.WriteString("root_id", actual.RootId); writer.WriteString("control_namespace_id", actual.ControlNamespaceId);
                writer.WriteString("linux_boot_id", actual.LinuxBootId); writer.WriteNumber("mount_namespace_device", actual.MountNamespaceDevice);
                writer.WriteNumber("mount_namespace_inode", actual.MountNamespaceInode); writer.WriteNumber("effective_user_id", actual.EffectiveUserId);
                writer.WriteNumber("file_inode", actual.FileIdentity.Inode); writer.WriteNumber("file_device_major", actual.FileIdentity.DeviceMajor);
                writer.WriteNumber("file_device_minor", actual.FileIdentity.DeviceMinor); writer.WriteNumber("file_mount_id", actual.FileIdentity.MountId);
                writer.WriteNumber("directory_inode", actual.DirectoryIdentity.Inode); writer.WriteNumber("directory_device_major", actual.DirectoryIdentity.DeviceMajor);
                writer.WriteNumber("directory_device_minor", actual.DirectoryIdentity.DeviceMinor); writer.WriteNumber("directory_mount_id", actual.DirectoryIdentity.MountId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.Flush();
        }
        token.ThrowIfCancellationRequested();
        if (output.Length > 262_144) throw InvalidFacts();
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static List<ObservedPublicationWitness> ReadWitnesses(string json, CancellationToken token) => ObservationFacts(() =>
    {
        if (json.Length > 16_384 || Encoding.UTF8.GetByteCount(json) > 16_384) throw InvalidFacts();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        var rows = document.RootElement;
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 2) throw InvalidFacts();
        var witnesses = new List<ObservedPublicationWitness>();
        foreach (var row in rows.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var scope = new RevisionScope(Id(row, "organization_id"), Id(row, "project_id"), Id(row, "mapping_id"), Id(row, "revision_id"));
            var target = PrivateObjectId.Restore(scope, Id(row, "object_id"), ObservationClass(Text(row, "object_class")));
            var identity = new PrivatePublicationFileIdentity(Id(row, "root_id"), Id(row, "control_namespace_id"), Id(row, "linux_boot_id"),
                row.GetProperty("mount_namespace_device").GetUInt64(), row.GetProperty("mount_namespace_inode").GetUInt64(), row.GetProperty("effective_user_id").GetUInt32(),
                new(row.GetProperty("file_inode").GetUInt64(), row.GetProperty("file_device_major").GetUInt32(), row.GetProperty("file_device_minor").GetUInt32(), row.GetProperty("file_mount_id").GetUInt64()),
                new(row.GetProperty("directory_inode").GetUInt64(), row.GetProperty("directory_device_major").GetUInt32(), row.GetProperty("directory_device_minor").GetUInt32(), row.GetProperty("directory_mount_id").GetUInt64()));
            var origin = Text(row, "owner_kind") switch
            {
                "native_ingestion" when Null(row, "cache_entry_id") && Null(row, "derivation_id") => ObjectCleanupOrigin.ForIngestion(scope, Id(row, "ingestion_operation_id")),
                "report_attempt" when Null(row, "ingestion_operation_id") => ObjectCleanupOrigin.ForReport(scope, Id(row, "cache_entry_id"), Id(row, "derivation_id"), Long(row, "owner_generation")),
                _ => throw InvalidFacts()
            };
            witnesses.Add(new(Id(row, "publication_id"), new(identity.RootId, target, Id(row, "temp_id")), identity, origin));
        }
        return witnesses;
    });

    private static PrivateObjectClass ObservationClass(string value) => value switch
    {
        "native" => PrivateObjectClass.NativeArtifact,
        "derived" => PrivateObjectClass.DerivedArtifact,
        _ => throw InvalidFacts()
    };

    // Same network-byte ordering as CatalogReadGuard; no Application-internal dependency.
    private static int CompareObservationIds(Guid left, Guid right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        left.TryWriteBytes(leftBytes, bigEndian: true, out _);
        right.TryWriteBytes(rightBytes, bigEndian: true, out _);
        return ((ReadOnlySpan<byte>)leftBytes).SequenceCompareTo(rightBytes);
    }

    private static T ObservationFacts<T>(Func<T> restore)
    {
        try { return restore(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException or JsonException or KeyNotFoundException)
        { throw InvalidFacts(); }
    }
}
