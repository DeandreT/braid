using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresCatalogReader
{
    private const string AuthorizedMappingSql = AuthorizedProjectSql + """
        , readable_mapping AS MATERIALIZED (
            SELECT authorized.organization_id, authorized.project_id, authorized.principal_id,
                authorized.role, mapping.id AS mapping_id, mapping.backend_id
            FROM authorized JOIN braid.mappings mapping
              ON mapping.organization_id = authorized.organization_id AND mapping.project_id = authorized.project_id
            WHERE mapping.id = @mappingId
        )
        """;

    private const string RevisionColumns = """
        SELECT readable_mapping.organization_id AS a_organization_id, readable_mapping.project_id AS a_project_id,
            readable_mapping.principal_id AS a_principal_id, readable_mapping.role AS a_role,
            readable_mapping.backend_id AS parent_backend_id,
            revision.organization_id AS r_organization_id, revision.project_id AS r_project_id,
            revision.mapping_id AS r_mapping_id, revision.id AS r_id, revision.sequence AS r_sequence,
            revision.initial_admission AS r_admission, revision.creator_principal_id AS r_creator,
            revision.committed_at AS r_committed_at, revision.native_class AS r_native_class,
            revision.native_digest AS r_native_digest, revision.native_byte_count AS r_native_byte_count,
            revision.logical_entry_document AS r_logical_document, revision.backend_id AS r_backend_id,
            reservation.display_filename AS r_original_filename
        """;

    private const string FilenameJoin = """
        LEFT JOIN braid.revision_ingestions reservation
          ON reservation.organization_id = revision.organization_id AND reservation.project_id = revision.project_id
         AND reservation.operation_id = revision.ingestion_operation_id
        """;

    public Task<CatalogRevisionPage?> ListRevisionsAsync(ActorContext actor, MappingScope scope,
        int limit, CatalogRevisionCursor? cursor = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => ListRevisionsCoreAsync(actor, scope, limit, cursor, cancellationToken), cancellationToken);

    public Task<CatalogRevision?> GetRevisionAsync(ActorContext actor, RevisionScope scope,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => GetRevisionCoreAsync(actor, scope, cancellationToken), cancellationToken);

    private async Task<CatalogRevisionPage?> ListRevisionsCoreAsync(ActorContext actor, MappingScope scope,
        int limit, CatalogRevisionCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        RequireLimit(limit);
        if (cursor is not null && cursor.Mapping != scope)
            throw new ArgumentException("The history cursor belongs to a different complete mapping scope.", nameof(cursor));
        var project = ProjectOf(scope);
        await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(string.Join("\n", AuthorizedMappingSql, RevisionColumns, """
            FROM readable_mapping LEFT JOIN LATERAL (
                SELECT source.organization_id, source.project_id, source.mapping_id, source.id, source.sequence,
                    source.initial_admission, source.creator_principal_id, source.committed_at, source.native_class,
                    source.native_digest, source.native_byte_count, source.logical_entry_document, source.backend_id,
                    source.ingestion_operation_id
                FROM braid.mapping_revisions source
                WHERE source.organization_id = readable_mapping.organization_id AND source.project_id = readable_mapping.project_id
                  AND source.mapping_id = readable_mapping.mapping_id
                  AND (@beforeSequence::bigint IS NULL OR source.sequence < @beforeSequence)
                ORDER BY source.sequence DESC LIMIT @fetchLimit
            ) revision ON true
            """, FilenameJoin, "ORDER BY revision.sequence DESC"));
        AddProject(command, actor, project);
        Add(command, "mappingId", NpgsqlDbType.Uuid, scope.MappingId);
        Add(command, "beforeSequence", NpgsqlDbType.Bigint, cursor?.BeforeSequence);
        Add(command, "fetchLimit", NpgsqlDbType.Integer, limit + 1);
        ProjectAccess? access = null;
        var revisions = new List<CatalogRevisionSummary>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                access ??= ReadAccess(reader, actor, project);
                if (!IsNull(reader, "r_id"))
                {
                    if (revisions.Count == limit + 1)
                        throw InvalidMetadata();
                    var revision = ReadRevision(reader);
                    if (MappingOf(revision.Scope) != scope)
                        throw InvalidMetadata();
                    revisions.Add(revision);
                }
            }
        }
        CatalogRevisionPage? result = null;
        if (access is { } observedAccess)
        {
            CatalogRevisionCursor? next = null;
            if (revisions.Count > limit)
            {
                revisions.RemoveAt(limit);
                next = new CatalogRevisionCursor(scope, revisions[^1].Sequence);
            }
            result = Read(() => new CatalogRevisionPage(scope, observedAccess, revisions, next));
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<CatalogRevision?> GetRevisionCoreAsync(ActorContext actor, RevisionScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        var mapping = MappingOf(scope);
        var project = ProjectOf(mapping);
        await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(string.Join("\n", AuthorizedMappingSql, RevisionColumns, """
            FROM readable_mapping JOIN braid.mapping_revisions revision
              ON revision.organization_id = readable_mapping.organization_id AND revision.project_id = readable_mapping.project_id
             AND revision.mapping_id = readable_mapping.mapping_id
            """, FilenameJoin, "WHERE revision.id = @revisionId"));
        AddProject(command, actor, project);
        Add(command, "mappingId", NpgsqlDbType.Uuid, scope.MappingId);
        Add(command, "revisionId", NpgsqlDbType.Uuid, scope.RevisionId);
        CatalogRevision? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var summary = ReadRevision(reader);
                if (summary.Scope != scope)
                    throw InvalidMetadata();
                result = Read(() => new CatalogRevision(summary, ReadAccess(reader, actor, project)));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static CatalogRevisionSummary ReadRevision(NpgsqlDataReader reader) => Read(() =>
    {
        if (IsNull(reader, "r_original_filename") || Value<string>(reader, "r_native_class") != "native" ||
            Value<string>(reader, "r_backend_id") != Value<string>(reader, "parent_backend_id"))
            throw InvalidMetadata();
        var digestText = Value<string>(reader, "r_native_digest");
        var digest = Sha256Digest.Parse(digestText);
        if (digest.Value != digestText)
            throw InvalidMetadata();
        return new CatalogRevisionSummary(new RevisionScope(Value<Guid>(reader, "r_organization_id"),
            Value<Guid>(reader, "r_project_id"), Value<Guid>(reader, "r_mapping_id"), Value<Guid>(reader, "r_id")),
            Value<long>(reader, "r_sequence"), Admission(Value<string>(reader, "r_admission")),
            new PrincipalId(Value<Guid>(reader, "r_creator")), Value<DateTimeOffset>(reader, "r_committed_at"),
            Value<string>(reader, "r_original_filename"), Value<string>(reader, "r_logical_document"), digest,
            Value<long>(reader, "r_native_byte_count"), Value<string>(reader, "r_backend_id"));
    });
}
