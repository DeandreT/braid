using System.Data;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Deployment;

namespace Weft.Persistence.Postgres;

/// <summary>Appends API request audits through the restricted audit-writer login, which can only call the 0025 writer.</summary>
public sealed class PostgresApiRequestAuditWriter(string connectionString) : IApiRequestAuditWriter, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public async Task<ApiRequestAuditOutcome> RecordAsync(ApiRequestAudit audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await BeginAsync(audit.OrganizationId, cancellationToken);
        await using var command = tx.CreateCommand(
            "SELECT weft.record_api_request(@org,@audit,@principal,@binding,@client,@operation,@method,@names,@ids)");
        command.Parameters.AddWithValue("org", audit.OrganizationId);
        command.Parameters.AddWithValue("audit", audit.AuditId);
        command.Parameters.AddWithValue("principal", audit.PrincipalId.Value);
        command.Parameters.AddWithValue("binding", audit.BindingId);
        command.Parameters.AddWithValue("client", audit.ClientApplicationId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Text, audit.Operation);
        command.Parameters.AddWithValue("method", NpgsqlDbType.Text, audit.Method);
        command.Parameters.AddWithValue("names", NpgsqlDbType.Array | NpgsqlDbType.Text, audit.Resources.Select(item => item.Key).ToArray());
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Text, audit.Resources.Select(item => item.Value).ToArray());
        var outcome = await command.ExecuteScalarAsync(cancellationToken) switch
        {
            0 => ApiRequestAuditOutcome.Recorded,
            1 => ApiRequestAuditOutcome.IdentityChanged,
            _ => throw Invalid()
        };
        // An unacknowledged commit is a failure, never a recorded audit.
        await tx.CommitAsync(cancellationToken);
        return outcome;
    }

    private async Task<PostgresTransaction> BeginAsync(Guid organization, CancellationToken token)
    {
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL statement_timeout='5s'; SET LOCAL lock_timeout='1s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            await ApiRequestAuditRoleGuard.ValidateAsync(connection, transaction, token);
            var result = new PostgresTransaction(connection, transaction, organization);
            await result.BindOrganizationAsync(organization, token);
            return result;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }

    private static InvalidOperationException Invalid() => new("API request audit persistence refused.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
    public override string ToString() => nameof(PostgresApiRequestAuditWriter);
}
