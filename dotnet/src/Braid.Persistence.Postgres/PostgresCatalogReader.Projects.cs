using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresCatalogReader
{
    public Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionAsync(ActorContext actor,
        Guid organizationId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => GetOrganizationAdmissionCoreAsync(actor, organizationId, cancellationToken), cancellationToken);

    public Task<CatalogProjectPage?> ListProjectsAsync(ActorContext actor, Guid organizationId,
        int limit, CatalogProjectCursor? cursor = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => ListProjectsCoreAsync(actor, organizationId, limit, cursor, cancellationToken), cancellationToken);

    public Task<CatalogProject?> GetProjectAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => GetProjectCoreAsync(actor, scope, cancellationToken), cancellationToken);

    public async Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId,
        int limit, Guid? afterProjectId = null, CancellationToken cancellationToken = default)
    {
        var cursor = afterProjectId is { } id ? new CatalogProjectCursor(organizationId, id) : null;
        var page = await ListProjectsAsync(actor, organizationId, limit, cursor, cancellationToken);
        return page is null ? new ProjectPage([]) :
            new ProjectPage(page.Projects.Select(project => project.Summary), page.NextCursor?.AfterProjectId);
    }

    public async Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken = default) =>
        (await GetProjectAsync(actor, scope, cancellationToken))?.Summary;

    public async Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken = default) =>
        (await GetProjectAsync(actor, scope, cancellationToken))?.Access;

    private async Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionCoreAsync(ActorContext actor,
        Guid organizationId, CancellationToken cancellationToken)
    {
        RequireOrganization(actor, organizationId);
        await using var transaction = await _database.BeginTransactionAsync(organizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(organizationId, cancellationToken);
        await using var command = transaction.CreateCommand(AuthorizedOrganizationSql + """
            SELECT organization_id, principal_id, organization_name, admission_version FROM admitted
            """);
        AddOrganization(command, actor, organizationId);
        CatalogOrganizationAdmission? result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            result = await reader.ReadAsync(cancellationToken) ? ReadOrganization(reader, actor, organizationId) : null;
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<CatalogProjectPage?> ListProjectsCoreAsync(ActorContext actor, Guid organizationId,
        int limit, CatalogProjectCursor? cursor, CancellationToken cancellationToken)
    {
        RequireOrganization(actor, organizationId);
        RequireLimit(limit);
        if (cursor is not null && cursor.OrganizationId != organizationId)
            throw new ArgumentException("The project cursor belongs to a different organization.", nameof(cursor));
        await using var transaction = await _database.BeginTransactionAsync(organizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(organizationId, cancellationToken);
        await using var command = transaction.CreateCommand(AuthorizedOrganizationSql + """
            SELECT admitted.organization_id, admitted.principal_id, admitted.organization_name, admitted.admission_version,
                admitted.organization_id AS a_organization_id, child.project_id AS a_project_id,
                admitted.principal_id AS a_principal_id, child.role AS a_role,
                child.organization_id AS p_organization_id, child.project_id AS p_project_id,
                child.name AS p_name, child.description AS p_description,
                child.creator_principal_id AS p_creator, child.created_at AS p_created_at,
                child.updated_at AS p_updated_at, child.version AS p_version
            FROM admitted LEFT JOIN LATERAL (
                SELECT project.organization_id, project.id AS project_id, project.name, project.description,
                    project.creator_principal_id, project.created_at, project.updated_at, project.version, membership.role
                FROM braid.projects project JOIN braid.project_memberships membership
                  ON membership.organization_id = project.organization_id AND membership.project_id = project.id
                 AND membership.principal_id = admitted.principal_id AND membership.active
                 AND membership.role IN ('viewer', 'runner', 'publisher', 'administrator')
                WHERE project.organization_id = admitted.organization_id AND (@afterId::uuid IS NULL OR project.id > @afterId)
                ORDER BY project.id LIMIT @fetchLimit
            ) child ON true ORDER BY child.project_id
            """);
        AddOrganization(command, actor, organizationId);
        Add(command, "afterId", NpgsqlDbType.Uuid, cursor?.AfterProjectId);
        Add(command, "fetchLimit", NpgsqlDbType.Integer, limit + 1);
        CatalogOrganizationAdmission? admission = null;
        var projects = new List<CatalogProject>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                admission ??= ReadOrganization(reader, actor, organizationId);
                if (!IsNull(reader, "p_project_id"))
                {
                    if (projects.Count == limit + 1)
                        throw InvalidMetadata();
                    projects.Add(Read(() => ReadProject(reader, actor, new ProjectScope(organizationId,
                        Value<Guid>(reader, "p_project_id")))));
                }
            }
        }
        CatalogProjectPage? result = null;
        if (admission is { } observedAdmission)
        {
            CatalogProjectCursor? next = null;
            if (projects.Count > limit)
            {
                projects.RemoveAt(limit);
                next = new CatalogProjectCursor(organizationId, projects[^1].Summary.Scope.ProjectId);
            }
            result = Read(() => new CatalogProjectPage(observedAdmission, projects, next));
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<CatalogProject?> GetProjectCoreAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(AuthorizedProjectSql + """
            SELECT authorized.organization_id AS a_organization_id, authorized.project_id AS a_project_id,
                authorized.principal_id AS a_principal_id, authorized.role AS a_role,
                project.organization_id AS p_organization_id, project.id AS p_project_id,
                project.name AS p_name, project.description AS p_description, project.creator_principal_id AS p_creator,
                project.created_at AS p_created_at, project.updated_at AS p_updated_at, project.version AS p_version
            FROM authorized JOIN braid.projects project
              ON project.organization_id = authorized.organization_id AND project.id = authorized.project_id
            """);
        AddProject(command, actor, scope);
        CatalogProject? result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            result = await reader.ReadAsync(cancellationToken) ? ReadProject(reader, actor, scope) : null;
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static CatalogOrganizationAdmission ReadOrganization(NpgsqlDataReader reader, ActorContext actor,
        Guid organizationId) => Read(() =>
    {
        var result = new CatalogOrganizationAdmission(Value<Guid>(reader, "organization_id"),
            new PrincipalId(Value<Guid>(reader, "principal_id")), Value<string>(reader, "organization_name"),
            Value<long>(reader, "admission_version"));
        if (result.OrganizationId != organizationId || result.PrincipalId != actor.PrincipalId)
            throw InvalidMetadata();
        return result;
    });

    private static CatalogProject ReadProject(NpgsqlDataReader reader, ActorContext actor, ProjectScope scope) => Read(() =>
    {
        var summary = new ProjectSummary(new ProjectScope(Value<Guid>(reader, "p_organization_id"),
            Value<Guid>(reader, "p_project_id")), Value<string>(reader, "p_name"), Value<string>(reader, "p_description"),
            new PrincipalId(Value<Guid>(reader, "p_creator")), Value<DateTimeOffset>(reader, "p_created_at"),
            Value<DateTimeOffset>(reader, "p_updated_at"), Value<long>(reader, "p_version"));
        if (summary.Scope != scope)
            throw InvalidMetadata();
        return new CatalogProject(summary, ReadAccess(reader, actor, scope));
    });
}
