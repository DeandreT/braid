using System.Data;
using Npgsql;
using NpgsqlTypes;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Explicit owner-tool capability grant/revoke; never creates or reactivates identity.</summary>
public sealed class OrganizationProjectCreationCapabilityProvisioner : IAsyncDisposable
{
    private readonly NpgsqlDataSource _owner;

    public OrganizationProjectCreationCapabilityProvisioner(string migrationConnectionString)
        => _owner = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(migrationConnectionString));

    public async Task SetAsync(Guid organizationId, PrincipalId principalId, bool canCreateProjects,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("An organization identifier cannot be empty.", nameof(organizationId));
        ArgumentNullException.ThrowIfNull(principalId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await _owner.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var limits = new NpgsqlCommand("SET LOCAL lock_timeout='1s'; SET LOCAL statement_timeout='5s'", connection, transaction))
                await limits.ExecuteNonQueryAsync(cancellationToken);
            await using (var owner = new NpgsqlCommand("""
                SELECT current_user=session_user AND current_user='weft_migrator'
                  AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreaterole AND NOT rolreplication
                  AND current_setting('transaction_read_only')='off'
                  AND current_setting('session_replication_role')='origin' AND current_setting('row_security')='on'
                FROM pg_catalog.pg_roles WHERE rolname=current_user
                """, connection, transaction))
                if (await owner.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new InvalidOperationException("Creation capability provisioning requires the separate migration owner.");

            await using (var protect = new NpgsqlCommand("LOCK TABLE ONLY weft.schema_migrations IN SHARE MODE", connection, transaction))
                await protect.ExecuteNonQueryAsync(cancellationToken);
            await RequireHistoryAsync(connection, transaction, cancellationToken);
            // The helper is owner-only, and checks the same trusted relation and
            // restricted-role contract used by the two creation entrypoints.
            await using (var sources = new NpgsqlCommand("SELECT weft.assert_project_creation_contract_v1()", connection, transaction))
                await sources.ExecuteNonQueryAsync(cancellationToken);

            await using (var organization = Command("SELECT o.id FROM weft.organizations o WHERE o.id=@organizationId FOR KEY SHARE OF o"))
                if (await organization.ExecuteScalarAsync(cancellationToken) is not Guid)
                    throw new InvalidOperationException("The intended organization does not exist.");
            bool principalActive;
            await using (var principal = Command("SELECT p.active FROM weft.principals p WHERE p.id=@principalId FOR SHARE OF p"))
                principalActive = await principal.ExecuteScalarAsync(cancellationToken) is bool principalIsActive
                    ? principalIsActive : throw new InvalidOperationException("The intended principal does not exist.");
            bool membershipActive;
            await using (var membership = Command("""
                SELECT m.active FROM weft.organization_memberships m
                WHERE m.organization_id=@organizationId AND m.principal_id=@principalId FOR SHARE OF m
                """))
                membershipActive = await membership.ExecuteScalarAsync(cancellationToken) is bool membershipIsActive
                    ? membershipIsActive : throw new InvalidOperationException("The intended organization admission does not exist.");
            if (canCreateProjects && (!principalActive || !membershipActive))
                throw new InvalidOperationException("Creation capability cannot be granted to an inactive identity or admission.");

            await using (var update = Command("""
                INSERT INTO weft.organization_project_creation_capabilities AS capability
                    (organization_id,principal_id,can_create_projects)
                VALUES(@organizationId,@principalId,@allowed)
                ON CONFLICT(organization_id,principal_id) DO UPDATE
                    SET can_create_projects=EXCLUDED.can_create_projects
                    WHERE capability.can_create_projects IS DISTINCT FROM EXCLUDED.can_create_projects
                """))
            {
                update.Parameters.Add(new NpgsqlParameter("allowed", NpgsqlDbType.Boolean) { Value = canCreateProjects });
                var changed = await update.ExecuteNonQueryAsync(cancellationToken);
                if (changed is < 0 or > 1)
                    throw new InvalidOperationException("Creation capability provisioning returned inconsistent counts.");
            }
            await transaction.CommitAsync(cancellationToken);

            NpgsqlCommand Command(string sql)
            {
                var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.Add(new NpgsqlParameter("organizationId", NpgsqlDbType.Uuid) { Value = organizationId });
                command.Parameters.Add(new NpgsqlParameter("principalId", NpgsqlDbType.Uuid) { Value = principalId.Value });
                return command;
            }
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("Creation capability provisioning failed."); }
    }

    private static async Task RequireHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var history = new NpgsqlCommand("SELECT version,checksum FROM weft.schema_migrations ORDER BY version", connection, transaction);
        await using var reader = await history.ExecuteReaderAsync(cancellationToken);
        var expected = MigrationRunner.EmbeddedMigrations;
        var index = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (index >= expected.Count || reader.GetInt32(0) != expected[index].Version || reader.GetString(1) != expected[index].Checksum)
                throw new InvalidOperationException("Creation capability provisioning requires the current exact migration history.");
            index++;
        }
        if (index != expected.Count)
            throw new InvalidOperationException("Apply the current migrations before provisioning creation capability.");
    }

    public ValueTask DisposeAsync() => _owner.DisposeAsync();
}
