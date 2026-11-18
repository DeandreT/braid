using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresCatalogReader
{
    private const string SearchAuthorizationSql = AuthorizedOrganizationSql + """
        , visible AS MATERIALIZED (
            SELECT m.project_id FROM admitted a JOIN braid.project_memberships m
              ON m.organization_id = a.organization_id AND m.principal_id = a.principal_id AND m.active
             AND m.role IN ('viewer','runner','publisher','administrator')
             AND NOT EXISTS (SELECT 1 FROM ONLY braid.resource_lifecycles l WHERE l.organization_id=m.organization_id
                 AND l.project_id=m.project_id AND l.mapping_id IS NULL AND l.state='archived')
        )
        """;

    internal Task<IReadOnlyList<(RevisionScope Scope, Guid DerivationId)>> PendingSearchReportsAsync(
        ActorContext actor, Guid organizationId, int limit, CancellationToken token) =>
        ExecuteAsync(() => PendingSearchReportsCoreAsync(actor, organizationId, limit, token), token);

    private async Task<IReadOnlyList<(RevisionScope Scope, Guid DerivationId)>> PendingSearchReportsCoreAsync(
        ActorContext actor, Guid organizationId, int limit, CancellationToken token)
    {
        RequireOrganization(actor, organizationId);
        await using var transaction = await _database.BeginTransactionAsync(organizationId, token);
        await transaction.RequireOrganizationAsync(organizationId, token);
        await using var command = transaction.CreateCommand(SearchAuthorizationSql + """
            SELECT r.project_id, r.mapping_id, r.revision_id, r.derivation_id
            FROM braid.catalog_search_reports r JOIN visible v ON v.project_id = r.project_id
            WHERE r.organization_id = @organizationId AND NOT EXISTS (
                    SELECT 1 FROM ONLY braid.resource_lifecycles l WHERE l.organization_id=r.organization_id
                        AND l.project_id=r.project_id AND l.mapping_id=r.mapping_id AND l.state='archived') AND NOT EXISTS (
                SELECT 1 FROM braid.catalog_search_report_index i WHERE
                    (i.organization_id,i.project_id,i.mapping_id,i.revision_id,i.derivation_id,i.derived_digest) =
                    (r.organization_id,r.project_id,r.mapping_id,r.revision_id,r.derivation_id,r.derived_digest))
            ORDER BY r.project_id,r.mapping_id,r.revision_id,r.derivation_id LIMIT @fetchLimit
            """);
        AddOrganization(command, actor, organizationId);
        Add(command, "fetchLimit", NpgsqlDbType.Integer, limit);
        var result = new List<(RevisionScope, Guid)>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                result.Add((new(organizationId, reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)), reader.GetGuid(3)));
        await transaction.CommitAsync(token);
        return result;
    }

    internal Task<CatalogSearchPage?> SearchIndexAsync(ActorContext actor, Guid organizationId,
        CatalogSearchQuery query, int limit, CatalogSearchCursor? cursor, CancellationToken token) =>
        ExecuteAsync(() => SearchIndexCoreAsync(actor, organizationId, query, limit, cursor, token), token);

    private async Task<CatalogSearchPage?> SearchIndexCoreAsync(ActorContext actor, Guid organizationId,
        CatalogSearchQuery query, int limit, CatalogSearchCursor? cursor, CancellationToken token)
    {
        RequireOrganization(actor, organizationId);
        RequireLimit(limit);
        cursor?.RequireContext(organizationId, query);
        await using var transaction = await _database.BeginTransactionAsync(organizationId, token);
        await transaction.RequireOrganizationAsync(organizationId, token);
        await using var command = transaction.CreateCommand(SearchAuthorizationSql + """
            , pending AS MATERIALIZED (
                SELECT 1 FROM braid.catalog_search_reports r JOIN visible v ON v.project_id = r.project_id
                WHERE r.organization_id = @organizationId AND NOT EXISTS (
                    SELECT 1 FROM ONLY braid.resource_lifecycles l WHERE l.organization_id=r.organization_id
                        AND l.project_id=r.project_id AND l.mapping_id=r.mapping_id AND l.state='archived') AND NOT EXISTS (
                    SELECT 1 FROM braid.catalog_search_report_index i WHERE
                        (i.organization_id,i.project_id,i.mapping_id,i.revision_id,i.derivation_id,i.derived_digest) =
                        (r.organization_id,r.project_id,r.mapping_id,r.revision_id,r.derivation_id,r.derived_digest)) LIMIT 1
            ), hits AS (
                SELECT 1 AS kind, p.id AS project_id, @empty::uuid AS mapping_id,
                    @empty::uuid AS revision_id, @empty::uuid AS derivation_id, ''::text COLLATE "C" AS resource_id,
                    p.name AS title, p.description AS description, NULL::uuid AS target_revision_id
                FROM braid.projects p JOIN visible v ON v.project_id = p.id
                WHERE p.organization_id = @organizationId
                    AND lower((p.name || E'\n' || p.description) COLLATE "C") LIKE @pattern
                UNION ALL
                SELECT 2, m.project_id, m.id, @empty, @empty, '', m.name, m.description || E'\n' || m.backend_id, m.head_revision_id
                FROM braid.mappings m JOIN visible v USING (project_id)
                WHERE m.organization_id = @organizationId AND NOT EXISTS (
                    SELECT 1 FROM ONLY braid.resource_lifecycles l WHERE l.organization_id=m.organization_id
                        AND l.project_id=m.project_id AND l.mapping_id=m.id AND l.state='archived')
                    AND lower((m.name || E'\n' || m.description || E'\n' || m.backend_id) COLLATE "C") LIKE @pattern
                UNION ALL
                SELECT 3, r.project_id, r.mapping_id, r.id, @empty, '', f.display_filename, r.native_digest, NULL::uuid
                FROM braid.mapping_revisions r JOIN visible v USING (project_id)
                JOIN braid.revision_ingestions f ON f.organization_id = r.organization_id AND f.project_id = r.project_id
                    AND f.operation_id = r.ingestion_operation_id
                WHERE r.organization_id = @organizationId AND NOT EXISTS (
                    SELECT 1 FROM ONLY braid.resource_lifecycles l WHERE l.organization_id=r.organization_id
                        AND l.project_id=r.project_id AND l.mapping_id=r.mapping_id AND l.state='archived')
                    AND (lower(f.display_filename COLLATE "C") LIKE @pattern OR lower(r.native_digest COLLATE "C") LIKE @pattern)
                UNION ALL
                SELECT (e->>'kind')::integer, i.project_id, i.mapping_id, i.revision_id, i.derivation_id,
                    (e->>'id') COLLATE "C", e->>'title', e->>'path', NULL::uuid
                FROM braid.catalog_search_report_index i JOIN visible v USING (project_id)
                CROSS JOIN LATERAL jsonb_array_elements(i.terms) e
                WHERE i.organization_id = @organizationId AND NOT EXISTS (
                    SELECT 1 FROM ONLY braid.resource_lifecycles l WHERE l.organization_id=i.organization_id
                        AND l.project_id=i.project_id AND l.mapping_id=i.mapping_id AND l.state='archived') AND lower(i.search_text COLLATE "C") LIKE @pattern
                    AND (lower((e->>'title') COLLATE "C") LIKE @pattern OR lower((e->>'path') COLLATE "C") LIKE @pattern)
            )
            SELECT a.*, EXISTS(SELECT 1 FROM pending) AS pending, h.* FROM admitted a LEFT JOIN LATERAL (
                SELECT * FROM hits WHERE (@afterKind::integer IS NULL OR
                    (kind,project_id,mapping_id,revision_id,derivation_id,resource_id) >
                    (@afterKind,@afterProject,@afterMapping,@afterRevision,@afterDerivation,@afterResource COLLATE "C"))
                ORDER BY kind,project_id,mapping_id,revision_id,derivation_id,resource_id LIMIT @fetchLimit
            ) h ON true ORDER BY kind,project_id,mapping_id,revision_id,derivation_id,resource_id
            """);
        AddOrganization(command, actor, organizationId);
        Add(command, "empty", NpgsqlDbType.Uuid, Guid.Empty);
        Add(command, "pattern", NpgsqlDbType.Text, "%" + CatalogSearchSnippets.Fold(query.Text)
            .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
        Add(command, "afterKind", NpgsqlDbType.Integer, cursor is null ? null : (int)cursor.After.Kind);
        Add(command, "afterProject", NpgsqlDbType.Uuid, cursor?.After.Project.ProjectId ?? Guid.Empty);
        Add(command, "afterMapping", NpgsqlDbType.Uuid, cursor?.After.MappingId ?? Guid.Empty);
        Add(command, "afterRevision", NpgsqlDbType.Uuid, cursor?.After.RevisionId ?? Guid.Empty);
        Add(command, "afterDerivation", NpgsqlDbType.Uuid, cursor?.After.ProjectionDerivationId ?? Guid.Empty);
        Add(command, "afterResource", NpgsqlDbType.Text, cursor?.After.ResourceId ?? "");
        Add(command, "fetchLimit", NpgsqlDbType.Integer, limit + 1);
        var hits = new List<CatalogSearchHit>();
        CatalogOrganizationAdmission? admission = null;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                admission ??= ReadOrganization(reader, actor, organizationId);
                if (Value<bool>(reader, "pending")) throw new CatalogSearchIndexPendingException();
                if (IsNull(reader, "kind")) continue;
                var kind = (CatalogSearchKind)Value<int>(reader, "kind");
                var key = new CatalogSearchKey(kind, new(organizationId, Value<Guid>(reader, "project_id")),
                    kind > CatalogSearchKind.Project ? Value<Guid>(reader, "mapping_id") : null,
                    kind > CatalogSearchKind.Mapping ? Value<Guid>(reader, "revision_id") : null,
                    kind is CatalogSearchKind.Field or CatalogSearchKind.Rule ? Value<string>(reader, "resource_id") : null,
                    kind is CatalogSearchKind.Field or CatalogSearchKind.Rule ? Value<Guid>(reader, "derivation_id") : null);
                hits.Add(new(key, CatalogSearchSnippets.Create(Value<string>(reader, "title"), query),
                    CatalogSearchSnippets.Create(Value<string>(reader, "description"), query),
                    IsNull(reader, "target_revision_id") ? null : Value<Guid>(reader, "target_revision_id")));
            }
        }
        await transaction.CommitAsync(token);
        return admission is null ? null : new(admission, query, hits.Take(limit), hits.Count > limit);
    }
}
