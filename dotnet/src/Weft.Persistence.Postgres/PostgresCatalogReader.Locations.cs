using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresCatalogReader : ICatalogResourceLocator
{
    // The same fresh authority as AuthorizedProjectSql, for whichever project holds the identity.
    private const string ReadableProjectsSql = """
        WITH readable AS MATERIALIZED (
            SELECT project.organization_id, project.id AS project_id
            FROM weft.projects project
            JOIN weft.principals principal ON principal.id = @actorId AND principal.active
            JOIN weft.organization_memberships admission
              ON admission.organization_id = project.organization_id AND admission.principal_id = principal.id AND admission.active
            JOIN weft.project_memberships membership
              ON membership.organization_id = project.organization_id AND membership.project_id = project.id
             AND membership.principal_id = principal.id AND membership.active
             AND membership.role IN ('viewer', 'runner', 'publisher', 'administrator')
            WHERE project.organization_id = @organizationId
        )
        """;

    public Task<MappingScope?> LocateMappingAsync(ActorContext actor, Guid organizationId, Guid mappingId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => LocateAsync(actor, organizationId, mappingId, """
            SELECT mapping.project_id, mapping.id, NULL::uuid FROM readable
            JOIN weft.mappings mapping ON mapping.organization_id = readable.organization_id AND mapping.project_id = readable.project_id
            WHERE mapping.id = @id
            """, row => new MappingScope(organizationId, row.Project, row.Mapping), cancellationToken), cancellationToken);

    public Task<RevisionScope?> LocateRevisionAsync(ActorContext actor, Guid organizationId, Guid revisionId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => LocateAsync(actor, organizationId, revisionId, """
            SELECT revision.project_id, revision.mapping_id, revision.id FROM readable
            JOIN weft.mapping_revisions revision ON revision.organization_id = readable.organization_id AND revision.project_id = readable.project_id
            WHERE revision.id = @id
            """, row => new RevisionScope(organizationId, row.Project, row.Mapping, row.Revision!.Value), cancellationToken), cancellationToken);

    private async Task<T?> LocateAsync<T>(ActorContext actor, Guid organizationId, Guid id, string sql,
        Func<(Guid Project, Guid Mapping, Guid? Revision), T> scope, CancellationToken cancellationToken) where T : class
    {
        RequireOrganization(actor, organizationId);
        if (id == Guid.Empty) throw new ArgumentException("A catalog identity cannot be empty.", nameof(id));
        await using var transaction = await _database.BeginTransactionAsync(organizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(organizationId, cancellationToken);
        await using var command = transaction.CreateCommand(ReadableProjectsSql + sql);
        AddOrganization(command, actor, organizationId);
        Add(command, "id", NpgsqlDbType.Uuid, id);
        T? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var row = (reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2));
                result = Read(() => scope(row));
                // UUIDs are unique within their table; a second row is inconsistent metadata.
                if (await reader.ReadAsync(cancellationToken)) throw InvalidMetadata();
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
