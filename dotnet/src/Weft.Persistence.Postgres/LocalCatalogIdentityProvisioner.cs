using Npgsql;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>
/// An explicit owner-tool operation, never registered in the web application.
/// Existing inactive identities and conflicting grants are preserved and refused.
/// </summary>
public sealed class LocalCatalogIdentityProvisioner : IAsyncDisposable
{
    private readonly NpgsqlDataSource _owner;

    public LocalCatalogIdentityProvisioner(string migrationConnectionString)
        => _owner = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(migrationConnectionString));

    public async Task ProvisionAsync(Guid organizationId, string organizationName, Guid principalId,
        ProjectScope? project = null, ProjectRole? projectRole = null,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty || principalId == Guid.Empty)
            throw new ArgumentException("Local identity identifiers must be nonempty.");
        var name = new IngestionCreateMetadata(organizationName, string.Empty).Name;
        if ((project is null) != (projectRole is null) ||
            project is not null && project.OrganizationId != organizationId ||
            projectRole is { } suppliedRole && !Enum.IsDefined(suppliedRole))
            throw new ArgumentException("An optional project grant requires matching scope and an explicit known role.");

        await using var connection = await _owner.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var owner = new NpgsqlCommand("""
            SELECT current_user = 'weft_migrator' AND session_user = current_user
                AND NOT rolsuper AND NOT rolbypassrls
            FROM pg_catalog.pg_roles WHERE rolname = current_user
            """, connection, transaction))
            if (await owner.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException("Local identity provisioning requires the separately configured migration owner.");

        await using (var timeouts = new NpgsqlCommand("SET LOCAL lock_timeout = '1s'; SET LOCAL statement_timeout = '5s'", connection, transaction))
            await timeouts.ExecuteNonQueryAsync(cancellationToken);

        await using (var history = new NpgsqlCommand("SELECT version, checksum FROM weft.schema_migrations ORDER BY version", connection, transaction))
        await using (var reader = await history.ExecuteReaderAsync(cancellationToken))
        {
            var expected = MigrationRunner.EmbeddedMigrations;
            var index = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (index >= expected.Count || reader.GetInt32(0) != expected[index].Version || reader.GetString(1) != expected[index].Checksum)
                    throw new InvalidOperationException("Local identity provisioning requires the current exact migration history.");
                index++;
            }
            if (index != expected.Count)
                throw new InvalidOperationException("Apply the current migrations before provisioning a local identity.");
        }

        await ExecuteAsync("INSERT INTO weft.organizations(id,name) VALUES ($1,$2) ON CONFLICT(id) DO NOTHING", organizationId, name);
        if (await ScalarAsync("SELECT name FROM weft.organizations WHERE id=$1 FOR UPDATE", organizationId) is not string existingName || existingName != name)
            throw new InvalidOperationException("The organization already exists with different metadata.");

        await ExecuteAsync("INSERT INTO weft.principals(id,active) VALUES ($1,true) ON CONFLICT(id) DO NOTHING", principalId);
        if (await ScalarAsync("SELECT active FROM weft.principals WHERE id=$1 FOR UPDATE", principalId) is not true)
            throw new InvalidOperationException("The existing local principal is inactive.");

        await ExecuteAsync("""
            INSERT INTO weft.organization_memberships(organization_id,principal_id,active)
            VALUES ($1,$2,true) ON CONFLICT(organization_id,principal_id) DO NOTHING
            """, organizationId, principalId);
        if (await ScalarAsync("SELECT active FROM weft.organization_memberships WHERE organization_id=$1 AND principal_id=$2 FOR UPDATE", organizationId, principalId) is not true)
            throw new InvalidOperationException("The existing organization admission is inactive.");

        if (project is not null && projectRole is { } role)
        {
            if (await ScalarAsync("SELECT id FROM weft.projects WHERE organization_id=$1 AND id=$2 FOR KEY SHARE", organizationId, project.ProjectId) is not Guid)
                throw new InvalidOperationException("The explicitly selected project does not exist in this organization.");
            var roleName = role switch
            {
                ProjectRole.Viewer => "viewer",
                ProjectRole.Runner => "runner",
                ProjectRole.Publisher => "publisher",
                ProjectRole.Administrator => "administrator",
                _ => throw new ArgumentOutOfRangeException(nameof(projectRole))
            };
            await ExecuteAsync("""
                INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role,active)
                VALUES ($1,$2,$3,$4,true) ON CONFLICT(organization_id,project_id,principal_id) DO NOTHING
                """, organizationId, project.ProjectId, principalId, roleName);
            if (await ScalarAsync("""
                SELECT active AND role=$4 FROM weft.project_memberships
                WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3 FOR UPDATE
                """, organizationId, project.ProjectId, principalId, roleName) is not true)
                throw new InvalidOperationException("The existing project membership is inactive or has a different role.");
        }
        await transaction.CommitAsync(cancellationToken);

        async Task ExecuteAsync(string sql, params object[] values)
        {
            await using var command = Command(sql, values);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        async Task<object?> ScalarAsync(string sql, params object[] values)
        {
            await using var command = Command(sql, values);
            return await command.ExecuteScalarAsync(cancellationToken);
        }

        NpgsqlCommand Command(string sql, object[] values)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var value in values)
                command.Parameters.AddWithValue(value);
            return command;
        }
    }

    public ValueTask DisposeAsync() => _owner.DisposeAsync();
}
