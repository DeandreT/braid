using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Specific project reads with fresh principal, organization, and project admission checks.</summary>
public sealed class PostgresProjectReader : IProjectReader
{
    private readonly PostgresDatabase _database;

    public PostgresProjectReader(PostgresDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId,
        int limit, Guid? afterProjectId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (organizationId == Guid.Empty)
            throw new ArgumentException("An organization identifier cannot be empty.", nameof(organizationId));
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Project page limits must be between one and one hundred.");
        if (afterProjectId == Guid.Empty)
            throw new ArgumentException("A project cursor cannot be empty.", nameof(afterProjectId));

        await using var transaction = await _database.BeginTransactionAsync(organizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(organizationId, cancellationToken);
        await using var command = transaction.CreateCommand("""
            SELECT project.organization_id, project.id, project.name, project.description,
                   project.creator_principal_id, project.created_at, project.updated_at, project.version
            FROM braid.projects project
            JOIN braid.principals principal ON principal.id = $2 AND principal.active
            JOIN braid.organization_memberships admission
                ON admission.organization_id = project.organization_id
               AND admission.principal_id = principal.id AND admission.active
            JOIN braid.project_memberships grant_record
                ON grant_record.organization_id = project.organization_id
               AND grant_record.project_id = project.id
               AND grant_record.principal_id = principal.id AND grant_record.active
            WHERE project.organization_id = $1 AND ($3::uuid IS NULL OR project.id > $3)
            ORDER BY project.id
            LIMIT $4
            """);
        command.Parameters.AddWithValue(organizationId);
        command.Parameters.AddWithValue(actor.PrincipalId.Value);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)afterProjectId ?? DBNull.Value });
        command.Parameters.AddWithValue(limit + 1);

        var projects = new List<ProjectSummary>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                projects.Add(ReadSummary(reader));
        }
        Guid? nextProjectId = null;
        if (projects.Count > limit)
        {
            projects.RemoveAt(limit);
            nextProjectId = projects[^1].Scope.ProjectId;
        }
        await transaction.CommitAsync(cancellationToken);
        return new ProjectPage(projects, nextProjectId);
    }

    public async Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand("""
            SELECT project.organization_id, project.id, project.name, project.description,
                   project.creator_principal_id, project.created_at, project.updated_at, project.version
            FROM braid.projects project
            JOIN braid.principals principal ON principal.id = $3 AND principal.active
            JOIN braid.organization_memberships admission
                ON admission.organization_id = project.organization_id
               AND admission.principal_id = principal.id AND admission.active
            JOIN braid.project_memberships grant_record
                ON grant_record.organization_id = project.organization_id
               AND grant_record.project_id = project.id
               AND grant_record.principal_id = principal.id AND grant_record.active
            WHERE project.organization_id = $1 AND project.id = $2
            """);
        AddScopeAndActor(command, scope, actor);
        ProjectSummary? project;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            project = await reader.ReadAsync(cancellationToken) ? ReadSummary(reader) : null;
        await transaction.CommitAsync(cancellationToken);
        return project;
    }

    public async Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand("""
            SELECT grant_record.role
            FROM braid.projects project
            JOIN braid.principals principal ON principal.id = $3 AND principal.active
            JOIN braid.organization_memberships admission
                ON admission.organization_id = project.organization_id
               AND admission.principal_id = principal.id AND admission.active
            JOIN braid.project_memberships grant_record
                ON grant_record.organization_id = project.organization_id
               AND grant_record.project_id = project.id
               AND grant_record.principal_id = principal.id AND grant_record.active
            WHERE project.organization_id = $1 AND project.id = $2
            """);
        AddScopeAndActor(command, scope, actor);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var access = result is string role
            ? new ProjectAccess(scope, actor.PrincipalId, ReadRole(role))
            : null;
        await transaction.CommitAsync(cancellationToken);
        return access;
    }

    private static void AddScopeAndActor(NpgsqlCommand command, ProjectScope scope, ActorContext actor)
    {
        command.Parameters.AddWithValue(scope.OrganizationId);
        command.Parameters.AddWithValue(scope.ProjectId);
        command.Parameters.AddWithValue(actor.PrincipalId.Value);
    }

    private static ProjectSummary ReadSummary(NpgsqlDataReader reader) => new(
        new ProjectScope(reader.GetGuid(0), reader.GetGuid(1)), reader.GetString(2), reader.GetString(3),
        new PrincipalId(reader.GetGuid(4)), reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7));

    private static ProjectRole ReadRole(string value) => value switch
    {
        "viewer" => ProjectRole.Viewer,
        "runner" => ProjectRole.Runner,
        "publisher" => ProjectRole.Publisher,
        "administrator" => ProjectRole.Administrator,
        _ => throw new InvalidOperationException("The database contains an unsupported project role.")
    };
}
