using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresCatalogReader
{
    private const string MappingColumns = """
        SELECT authorized.organization_id AS a_organization_id, authorized.project_id AS a_project_id,
            authorized.principal_id AS a_principal_id, authorized.role AS a_role,
            mapping.organization_id AS m_organization_id, mapping.project_id AS m_project_id,
            mapping.id AS m_id, mapping.name AS m_name, mapping.description AS m_description,
            mapping.backend_id AS m_backend_id, mapping.creator_principal_id AS m_creator,
            mapping.owner_principal_id AS m_owner, mapping.created_at AS m_created_at,
            mapping.updated_at AS m_updated_at, mapping.version AS m_version,
            mapping.head_revision_id AS m_head_id, mapping.head_sequence AS m_head_sequence,
            head.organization_id AS h_organization_id, head.project_id AS h_project_id,
            head.mapping_id AS h_mapping_id, head.id AS h_id, head.sequence AS h_sequence,
            head.initial_admission AS h_admission, head.backend_id AS h_backend_id
        """;

    private const string HeadJoin = """
        LEFT JOIN weft.mapping_revisions head
          ON head.organization_id = mapping.organization_id AND head.project_id = mapping.project_id
         AND head.mapping_id = mapping.id AND head.id = mapping.head_revision_id AND head.sequence = mapping.head_sequence
        """;

    public Task<CatalogMappingPage?> ListMappingsAsync(ActorContext actor, ProjectScope scope,
        int limit, CatalogMappingCursor? cursor = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => ListMappingsCoreAsync(actor, scope, limit, cursor, cancellationToken), cancellationToken);

    public Task<CatalogMapping?> GetMappingAsync(ActorContext actor, MappingScope scope,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => GetMappingCoreAsync(actor, scope, cancellationToken), cancellationToken);

    public Task<CatalogMappingMetadata?> GetMappingMetadataAsync(ActorContext actor, MappingScope scope,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => GetMappingMetadataCoreAsync(actor, scope, cancellationToken), cancellationToken);

    private async Task<CatalogMappingPage?> ListMappingsCoreAsync(ActorContext actor, ProjectScope scope,
        int limit, CatalogMappingCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        RequireLimit(limit);
        if (cursor is not null && cursor.Project != scope)
            throw new ArgumentException("The mapping cursor belongs to a different complete project scope.", nameof(cursor));
        await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(string.Join("\n", AuthorizedProjectSql, MappingColumns, """
            FROM authorized LEFT JOIN LATERAL (
                SELECT source.organization_id, source.project_id, source.id, source.name, source.description,
                    source.backend_id, source.creator_principal_id, source.owner_principal_id,
                    source.created_at, source.updated_at, source.version, source.head_revision_id, source.head_sequence
                FROM weft.mappings source
                WHERE source.organization_id = authorized.organization_id AND source.project_id = authorized.project_id
                  AND (@afterId::uuid IS NULL OR source.id > @afterId)
                ORDER BY source.id LIMIT @fetchLimit
            ) mapping ON true
            """, HeadJoin, "ORDER BY mapping.id"));
        AddProject(command, actor, scope);
        Add(command, "afterId", NpgsqlDbType.Uuid, cursor?.AfterMappingId);
        Add(command, "fetchLimit", NpgsqlDbType.Integer, limit + 1);
        ProjectAccess? access = null;
        var mappings = new List<CatalogMappingSummary>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                access ??= ReadAccess(reader, actor, scope);
                if (!IsNull(reader, "m_id"))
                {
                    if (mappings.Count == limit + 1)
                        throw InvalidMetadata();
                    var summary = ReadMapping(reader);
                    if (ProjectOf(summary.Scope) != scope)
                        throw InvalidMetadata();
                    mappings.Add(summary);
                }
            }
        }
        CatalogMappingPage? result = null;
        if (access is { } observedAccess)
        {
            CatalogMappingCursor? next = null;
            if (mappings.Count > limit)
            {
                mappings.RemoveAt(limit);
                next = new CatalogMappingCursor(scope, mappings[^1].Scope.MappingId);
            }
            result = Read(() => new CatalogMappingPage(observedAccess, mappings, next));
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<CatalogMapping?> GetMappingCoreAsync(ActorContext actor, MappingScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        var project = ProjectOf(scope);
        await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(string.Join("\n", AuthorizedProjectSql, MappingColumns, """
            FROM authorized JOIN weft.mappings mapping
              ON mapping.organization_id = authorized.organization_id AND mapping.project_id = authorized.project_id
            """, HeadJoin, "WHERE mapping.id = @mappingId"));
        AddProject(command, actor, project);
        Add(command, "mappingId", NpgsqlDbType.Uuid, scope.MappingId);
        CatalogMapping? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var summary = ReadMapping(reader);
                if (summary.Scope != scope)
                    throw InvalidMetadata();
                result = Read(() => new CatalogMapping(summary, ReadAccess(reader, actor, project)));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<CatalogMappingMetadata?> GetMappingMetadataCoreAsync(ActorContext actor, MappingScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(AuthorizedProjectSql + """
            SELECT mapping.organization_id, mapping.project_id, mapping.id, mapping.name, mapping.description, mapping.version
            FROM authorized JOIN weft.mappings mapping
              ON mapping.organization_id = authorized.organization_id AND mapping.project_id = authorized.project_id
            WHERE mapping.id = @mappingId
            """);
        AddProject(command, actor, ProjectOf(scope));
        Add(command, "mappingId", NpgsqlDbType.Uuid, scope.MappingId);
        CatalogMappingMetadata? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                result = Read(() => new CatalogMappingMetadata(new MappingScope(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)),
                    reader.GetString(3), reader.GetString(4), reader.GetInt64(5)));
                if (result.Scope != scope)
                    throw InvalidMetadata();
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static CatalogMappingSummary ReadMapping(NpgsqlDataReader reader) => Read(() =>
    {
        if (IsNull(reader, "h_id"))
            throw InvalidMetadata();
        var scope = new MappingScope(Value<Guid>(reader, "m_organization_id"), Value<Guid>(reader, "m_project_id"),
            Value<Guid>(reader, "m_id"));
        var head = new CatalogRevisionHead(new RevisionScope(Value<Guid>(reader, "h_organization_id"),
            Value<Guid>(reader, "h_project_id"), Value<Guid>(reader, "h_mapping_id"), Value<Guid>(reader, "h_id")),
            Value<long>(reader, "h_sequence"), Admission(Value<string>(reader, "h_admission")));
        var backendId = Value<string>(reader, "m_backend_id");
        if (head.Scope.RevisionId != Value<Guid>(reader, "m_head_id") ||
            head.Sequence != Value<long>(reader, "m_head_sequence") || backendId != Value<string>(reader, "h_backend_id"))
            throw InvalidMetadata();
        return new CatalogMappingSummary(scope, Value<string>(reader, "m_name"), Value<string>(reader, "m_description"),
            backendId, new PrincipalId(Value<Guid>(reader, "m_creator")), new PrincipalId(Value<Guid>(reader, "m_owner")),
            Value<DateTimeOffset>(reader, "m_created_at"), Value<DateTimeOffset>(reader, "m_updated_at"),
            Value<long>(reader, "m_version"), head);
    });
}
