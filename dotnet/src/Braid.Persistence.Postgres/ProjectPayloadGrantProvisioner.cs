using System.Data;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Explicit migration-owner grant/revoke, separate from identity and web application operations.</summary>
public sealed class ProjectPayloadGrantProvisioner : IProjectPayloadGrantOwner, IAsyncDisposable
{
    private readonly NpgsqlDataSource _owner;

    public ProjectPayloadGrantProvisioner(string migrationConnectionString)
        => _owner = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(migrationConnectionString));

    public async Task<ProjectPayloadGrantOwnerResult> SetAsync(ProjectScope scope, PrincipalId principalId,
        bool active, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedVersion, 0);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await _owner.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var mode = new NpgsqlCommand("SET TRANSACTION READ WRITE", connection, transaction))
                await mode.ExecuteNonQueryAsync(cancellationToken);
            await PostgresProjectPayloadPermissionFacts.SetLimitsAsync(connection, transaction, cancellationToken);
            await PostgresProjectPayloadPermissionFacts.BindOrganizationAsync(connection, transaction,
                scope.OrganizationId, cancellationToken);
            await PayloadPermissionRoleGuard.ValidateOwnerAsync(connection, transaction, cancellationToken);

            var parentsLocked = await LockParentsAsync(connection, transaction, scope, principalId, cancellationToken);
            var existing = await FindGrantAsync(connection, transaction, scope, principalId, cancellationToken);
            // The post-wait statement uses current facts, with authority locks held through commit.
            // Inactive parents remain lockable so an explicit revoke can still proceed.
            // A parent appearing after a missing-row lock attempt cannot supply unlocked authority.
            var access = parentsLocked
                ? await ReadCurrentAccessAsync(connection, transaction, scope, principalId, cancellationToken)
                : null;
            var outcome = ProjectPayloadPermissionPolicy.ClassifySet(scope, principalId,
                active, expectedVersion, access, existing);
            ProjectPayloadGrantOwnerResult result;
            if (outcome == ProjectPayloadGrantOwnerOutcome.Created)
            {
                int inserted;
                await using (var insert = Command("""
                    INSERT INTO braid.project_payload_read_grants
                        (organization_id,project_id,principal_id,active,version)
                    VALUES(@organizationId,@projectId,@principalId,true,1)
                    ON CONFLICT(organization_id,project_id,principal_id) DO NOTHING
                    """))
                {
                    inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
                    if (inserted is < 0 or > 1) throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
                }
                // Unique arbitration can wait for an initially absent tuple. Do not reuse the
                // INSERT snapshot: lock/read its actual winner and recheck current authority.
                var current = await FindGrantAsync(connection, transaction, scope, principalId, cancellationToken)
                    ?? throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
                access = await ReadCurrentAccessAsync(connection, transaction, scope, principalId, cancellationToken);
                if (inserted == 1)
                {
                    if (access is null || !current.Active || current.Version != 1)
                        throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
                    result = ProjectPayloadGrantOwnerResult.ForGrant(ProjectPayloadGrantOwnerOutcome.Created, current);
                }
                else
                {
                    outcome = ProjectPayloadPermissionPolicy.ClassifySet(scope, principalId,
                        active, expectedVersion, access, current);
                    if (outcome is not (ProjectPayloadGrantOwnerOutcome.VersionConflict or
                        ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable))
                        throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
                    result = ProjectPayloadGrantOwnerResult.WithoutGrant(outcome);
                }
            }
            else if (outcome == ProjectPayloadGrantOwnerOutcome.Updated)
            {
                await using var update = Command($"""
                    UPDATE ONLY braid.project_payload_read_grants payload
                    SET active=@active,version=payload.version+1
                    WHERE payload.organization_id=@organizationId AND payload.project_id=@projectId
                      AND payload.principal_id=@principalId AND payload.version=@expectedVersion
                      AND payload.active IS DISTINCT FROM @active
                    RETURNING {PostgresProjectPayloadPermissionFacts.GrantProjection}
                    """);
                PostgresProjectPayloadPermissionFacts.Add(update, "active", NpgsqlDbType.Boolean, active);
                PostgresProjectPayloadPermissionFacts.Add(update, "expectedVersion", NpgsqlDbType.Bigint, expectedVersion);
                ProjectPayloadGrant current;
                await using (var reader = await update.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                        throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
                    current = PostgresProjectPayloadPermissionFacts.ReadGrant(reader, scope, principalId);
                    if (await reader.ReadAsync(cancellationToken))
                        throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
                }
                if (existing is null || current.Active != active || current.Version != existing.Version + 1)
                    throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
                result = ProjectPayloadGrantOwnerResult.ForGrant(ProjectPayloadGrantOwnerOutcome.Updated, current);
            }
            else
                result = outcome == ProjectPayloadGrantOwnerOutcome.Unchanged && existing is not null
                    ? ProjectPayloadGrantOwnerResult.ForGrant(outcome, existing)
                    : ProjectPayloadGrantOwnerResult.WithoutGrant(outcome);

            // Refusals and no-ops, as well as changes, never escape an unacknowledged commit.
            await transaction.CommitAsync(cancellationToken);
            return result;

            NpgsqlCommand Command(string sql)
            {
                var command = new NpgsqlCommand(sql, connection, transaction);
                PostgresProjectPayloadPermissionFacts.AddTuple(command, scope, principalId);
                return command;
            }
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("Project payload grant owner operation failed."); }
    }

    private static async Task<bool> LockParentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ProjectScope scope, PrincipalId principalId, CancellationToken cancellationToken)
    {
        // Match existing authority order before the new grant lock. Each exact row is
        // selected regardless of active state; these statements never mutate parents.
        var complete = await LockAsync("SELECT organization.id FROM ONLY braid.organizations organization " +
            "WHERE organization.id=@organizationId FOR KEY SHARE OF organization", [scope.OrganizationId]);
        complete &= await LockAsync("SELECT principal.id FROM ONLY braid.principals principal " +
            "WHERE principal.id=@principalId FOR SHARE OF principal", [principalId.Value]);
        complete &= await LockAsync("""
            SELECT admission.organization_id,admission.principal_id FROM ONLY braid.organization_memberships admission
            WHERE admission.organization_id=@organizationId AND admission.principal_id=@principalId
            FOR SHARE OF admission
            """, [scope.OrganizationId, principalId.Value]);
        complete &= await LockAsync("""
            SELECT project.organization_id,project.id FROM ONLY braid.projects project
            WHERE project.organization_id=@organizationId AND project.id=@projectId FOR KEY SHARE OF project
            """, [scope.OrganizationId, scope.ProjectId]);
        complete &= await LockAsync("""
            SELECT membership.organization_id,membership.project_id,membership.principal_id
            FROM ONLY braid.project_memberships membership
            WHERE membership.organization_id=@organizationId AND membership.project_id=@projectId
              AND membership.principal_id=@principalId FOR SHARE OF membership
            """, [scope.OrganizationId, scope.ProjectId, principalId.Value]);

        return complete;

        async Task<bool> LockAsync(string sql, Guid[] expected)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            PostgresProjectPayloadPermissionFacts.AddTuple(command, scope, principalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            PostgresProjectPayloadPermissionFacts.Read(() =>
            {
                for (var index = 0; index < expected.Length; index++)
                    if (reader.GetGuid(index) != expected[index])
                        throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
                return true;
            });
            if (await reader.ReadAsync(cancellationToken)) throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
            return true;
        }
    }

    private static async Task<ProjectPayloadGrant?> FindGrantAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, ProjectScope scope, PrincipalId principalId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {PostgresProjectPayloadPermissionFacts.GrantProjection}
            FROM ONLY braid.project_payload_read_grants payload
            WHERE payload.organization_id=@organizationId AND payload.project_id=@projectId
              AND payload.principal_id=@principalId FOR UPDATE OF payload
            """, connection, transaction);
        PostgresProjectPayloadPermissionFacts.AddTuple(command, scope, principalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var grant = PostgresProjectPayloadPermissionFacts.ReadGrant(reader, scope, principalId);
        if (await reader.ReadAsync(cancellationToken)) throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
        return grant;
    }

    private static async Task<ProjectAccess?> ReadCurrentAccessAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, ProjectScope scope, PrincipalId principalId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT project.organization_id,project.id,principal.id,membership.role
            FROM ONLY braid.projects project
            JOIN ONLY braid.principals principal ON principal.id=@principalId AND principal.active
            JOIN ONLY braid.organization_memberships admission
                ON admission.organization_id=project.organization_id
               AND admission.principal_id=principal.id AND admission.active
            JOIN ONLY braid.project_memberships membership
                ON membership.organization_id=project.organization_id AND membership.project_id=project.id
               AND membership.principal_id=principal.id AND membership.active
            WHERE project.organization_id=@organizationId AND project.id=@projectId
            """, connection, transaction);
        PostgresProjectPayloadPermissionFacts.AddTuple(command, scope, principalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var access = PostgresProjectPayloadPermissionFacts.Read(() =>
        {
            if (reader.GetGuid(0) != scope.OrganizationId || reader.GetGuid(1) != scope.ProjectId ||
                reader.GetGuid(2) != principalId.Value) throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
            return new ProjectAccess(scope, principalId, PostgresProjectPayloadPermissionFacts.ReadRole(reader.GetString(3)));
        });
        if (await reader.ReadAsync(cancellationToken)) throw PostgresProjectPayloadPermissionFacts.InvalidFacts();
        return access;
    }

    public ValueTask DisposeAsync() => _owner.DisposeAsync();
}
