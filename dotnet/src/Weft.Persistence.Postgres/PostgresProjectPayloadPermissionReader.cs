using System.Data;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Fresh exact project authority intersected with an explicit active payload-read grant.</summary>
public sealed class PostgresProjectPayloadPermissionReader : IProjectPayloadPermissionReader, IProjectPayloadGrantPresence
{
    private readonly PostgresPayloadPermissionDatabase _database;

    public PostgresProjectPayloadPermissionReader(PostgresPayloadPermissionDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<ProjectPayloadGrant?> ObserveAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await _database.DataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var mode = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await mode.ExecuteNonQueryAsync(cancellationToken);
            await PostgresProjectPayloadPermissionFacts.SetLimitsAsync(connection, transaction, cancellationToken);
            await PostgresProjectPayloadPermissionFacts.BindOrganizationAsync(connection, transaction,
                scope.OrganizationId, cancellationToken);
            await PayloadPermissionRoleGuard.ValidateReaderAsync(connection, transaction, cancellationToken);

            // All authority predicates and the explicit grant share this actual statement snapshot.
            // The schema's exact project/organization foreign keys establish organization existence;
            // no organization-active flag or additional role/action matrix is introduced.
            await using var command = new NpgsqlCommand("""
                SELECT payload.organization_id,payload.project_id,payload.principal_id,payload.active,payload.version,
                       membership.role
                FROM ONLY weft.projects project
                JOIN ONLY weft.principals principal ON principal.id=@principalId AND principal.active
                JOIN ONLY weft.organization_memberships admission
                    ON admission.organization_id=project.organization_id
                   AND admission.principal_id=principal.id AND admission.active
                JOIN ONLY weft.project_memberships membership
                    ON membership.organization_id=project.organization_id
                   AND membership.project_id=project.id AND membership.principal_id=principal.id AND membership.active
                JOIN ONLY weft.project_payload_read_grants payload
                    ON payload.organization_id=project.organization_id AND payload.project_id=project.id
                   AND payload.principal_id=principal.id AND payload.active
                WHERE project.organization_id=@organizationId AND project.id=@projectId
                """, connection, transaction);
            PostgresProjectPayloadPermissionFacts.AddTuple(command, scope, actor.PrincipalId);
            ProjectPayloadGrant? result;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) result = null;
                else
                {
                    var grant = PostgresProjectPayloadPermissionFacts.ReadGrant(reader, scope, actor.PrincipalId);
                    var access = PostgresProjectPayloadPermissionFacts.Read(() => new ProjectAccess(scope,
                        actor.PrincipalId, PostgresProjectPayloadPermissionFacts.ReadRole(reader.GetString(5))));
                    result = ProjectPayloadPermissionPolicy.ObserveCurrent(actor, scope, access, grant);
                    if (result is null || await reader.ReadAsync(cancellationToken))
                        throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
                }
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("Project payload permission persistence failed."); }
    }

    public async Task<bool> AnyAsync(ActorContext actor, Guid organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (organizationId == Guid.Empty) throw new ArgumentException("An organization identity cannot be empty.", nameof(organizationId));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await _database.DataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var mode = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await mode.ExecuteNonQueryAsync(cancellationToken);
            await PostgresProjectPayloadPermissionFacts.SetLimitsAsync(connection, transaction, cancellationToken);
            await PostgresProjectPayloadPermissionFacts.BindOrganizationAsync(connection, transaction, organizationId, cancellationToken);
            await PayloadPermissionRoleGuard.ValidateReaderAsync(connection, transaction, cancellationToken);
            // The same authority joins as ObserveAsync, for any project of the organization.
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS(
                    SELECT 1
                    FROM ONLY weft.projects project
                    JOIN ONLY weft.principals principal ON principal.id=@principalId AND principal.active
                    JOIN ONLY weft.organization_memberships admission
                        ON admission.organization_id=project.organization_id
                       AND admission.principal_id=principal.id AND admission.active
                    JOIN ONLY weft.project_memberships membership
                        ON membership.organization_id=project.organization_id
                       AND membership.project_id=project.id AND membership.principal_id=principal.id AND membership.active
                    JOIN ONLY weft.project_payload_read_grants payload
                        ON payload.organization_id=project.organization_id AND payload.project_id=project.id
                       AND payload.principal_id=principal.id AND payload.active
                    WHERE project.organization_id=@organizationId)
                """, connection, transaction);
            PostgresProjectPayloadPermissionFacts.Add(command, "organizationId", NpgsqlDbType.Uuid, organizationId);
            PostgresProjectPayloadPermissionFacts.Add(command, "principalId", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
            var any = await command.ExecuteScalarAsync(cancellationToken) is bool value ? value : throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
            await transaction.CommitAsync(cancellationToken);
            return any;
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("Project payload permission persistence failed."); }
    }
}

internal static class PostgresProjectPayloadPermissionFacts
{
    internal const string GrantProjection =
        "payload.organization_id,payload.project_id,payload.principal_id,payload.active,payload.version";

    internal static Task SetLimitsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
        => ExecuteAsync("SET LOCAL lock_timeout='1s'; SET LOCAL statement_timeout='5s'",
            connection, transaction, cancellationToken);

    internal static async Task BindOrganizationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid organizationId, CancellationToken cancellationToken)
    {
        await using (var inspect = new NpgsqlCommand(
            "SELECT NULLIF(pg_catalog.current_setting($1,true),'')", connection, transaction))
        {
            inspect.Parameters.AddWithValue(PostgresTransaction.OrganizationSetting);
            if (await inspect.ExecuteScalarAsync(cancellationToken) is string)
                throw new InvalidOperationException("A leased connection contains a preset organization context.");
        }
        await using var bind = new NpgsqlCommand("SELECT pg_catalog.set_config($1,$2,true)", connection, transaction);
        bind.Parameters.AddWithValue(PostgresTransaction.OrganizationSetting);
        bind.Parameters.AddWithValue(organizationId.ToString("D"));
        if (await bind.ExecuteScalarAsync(cancellationToken) is not string current ||
            !string.Equals(current, organizationId.ToString("D"), StringComparison.Ordinal))
            throw new InvalidOperationException("The transaction's organization context could not be bound.");
    }

    internal static ProjectPayloadGrant ReadGrant(NpgsqlDataReader reader, ProjectScope scope, PrincipalId principalId)
        => Read(() =>
        {
            var grant = new ProjectPayloadGrant(new ProjectScope(reader.GetGuid(0), reader.GetGuid(1)),
                new PrincipalId(reader.GetGuid(2)), reader.GetBoolean(3), reader.GetInt64(4));
            if (grant.Scope != scope || grant.PrincipalId != principalId) throw InvalidFacts();
            return grant;
        });

    internal static ProjectRole ReadRole(string value) => value switch
    {
        "viewer" => ProjectRole.Viewer,
        "runner" => ProjectRole.Runner,
        "publisher" => ProjectRole.Publisher,
        "administrator" => ProjectRole.Administrator,
        _ => throw InvalidFacts()
    };

    internal static void AddTuple(NpgsqlCommand command, ProjectScope scope, PrincipalId principalId)
    {
        Add(command, "organizationId", NpgsqlDbType.Uuid, scope.OrganizationId);
        Add(command, "projectId", NpgsqlDbType.Uuid, scope.ProjectId);
        Add(command, "principalId", NpgsqlDbType.Uuid, principalId.Value);
    }

    internal static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value)
        => command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    internal static T Read<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is ArgumentException or InvalidCastException or FormatException or OverflowException)
        { throw InvalidFacts(); }
    }

    internal static InvalidOperationException InvalidFacts()
        => new("The database contains inconsistent project payload permission facts.");

    private static async Task ExecuteAsync(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
