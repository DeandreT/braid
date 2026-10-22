using System.Data;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Administrator grant changes through the restricted grant-writer login; every change re-checks current authority.</summary>
public sealed class PostgresInvocationGrantStore(string connectionString) : IInvocationGrantStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public Task<InvocationGrantOutcome> GrantAsync(ActorContext administrator, InvocationGrant grant, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return WriteAsync(administrator, "grant", grant.Target, grant.GrantId, grant.Principal, token);
    }

    public Task<InvocationGrantOutcome> RevokeAsync(ActorContext administrator, ReleaseTargetReference target, Guid grantId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (grantId == Guid.Empty) throw new ArgumentException("A grant identifier is required.", nameof(grantId));
        return WriteAsync(administrator, "revoke", target, grantId, null, token);
    }

    public async Task<IReadOnlyList<InvocationGrantSnapshot>> ReadAsync(ActorContext administrator, ReleaseTargetReference target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var tx = await BeginAsync(administrator, target.Mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT * FROM weft.read_invocation_grants(@org,@project,@mapping,@environment,@version,@target)");
        Scope(command, target);
        var grants = new List<InvocationGrantSnapshot>();
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            while (await rows.ReadAsync(token))
            {
                if (grants.Count == 10_000) throw Invalid();
                grants.Add(new(rows.GetGuid(0), new(rows.GetString(1), rows.GetGuid(2), rows.GetGuid(3)), new(rows.GetGuid(4)),
                    rows.GetFieldValue<DateTimeOffset>(5), rows.IsDBNull(6) ? null : new PrincipalId(rows.GetGuid(6)),
                    rows.IsDBNull(7) ? null : rows.GetFieldValue<DateTimeOffset>(7)));
            }
        }
        await tx.CommitAsync(token);
        return grants.AsReadOnly();
    }

    private async Task<InvocationGrantOutcome> WriteAsync(ActorContext administrator, string operation, ReleaseTargetReference target, Guid key,
        MachinePrincipal? principal, CancellationToken token)
    {
        await using var tx = await BeginAsync(administrator, target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT weft.write_invocation_grant(@op,@org,@project,@mapping,@environment,@version,@target,@key,@issuer,@tenant,@principal)");
        command.Parameters.AddWithValue("op", operation);
        Scope(command, target);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Text, (object?)principal?.Provider ?? DBNull.Value);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, (object?)principal?.TenantId ?? DBNull.Value);
        command.Parameters.AddWithValue("principal", NpgsqlDbType.Uuid, (object?)principal?.ObjectId ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(token) is not int result || result is < 0 or > 3) throw Invalid();
        await tx.CommitAsync(token);
        return (InvocationGrantOutcome)result;
    }

    private async Task<PostgresTransaction> BeginAsync(ActorContext administrator, Guid org, bool write, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            if (!write) await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token);
            await MachineInvocationRoleGuard.ValidateAsync(connection, transaction, grants: true, token);
            await using (var identity = new NpgsqlCommand("SELECT set_config('weft.build_principal_id',@actor,true)", connection, transaction))
            {
                identity.Parameters.AddWithValue("actor", administrator.PrincipalId.Value.ToString("D"));
                await identity.ExecuteNonQueryAsync(token);
            }
            var result = new PostgresTransaction(connection, transaction, org);
            await result.BindOrganizationAsync(org, token);
            return result;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }

    internal static void Scope(NpgsqlCommand command, ReleaseTargetReference target)
    {
        command.Parameters.AddWithValue("org", target.Mapping.OrganizationId); command.Parameters.AddWithValue("project", target.Mapping.ProjectId);
        command.Parameters.AddWithValue("mapping", target.Mapping.MappingId); command.Parameters.AddWithValue("environment", target.Environment.EnvironmentId);
        command.Parameters.AddWithValue("version", target.Environment.Version); command.Parameters.AddWithValue("target", target.TargetId);
    }
    private static InvalidOperationException Invalid() => new("Invocation grant persistence refused.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
