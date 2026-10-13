using System.Data;
using Npgsql;

namespace Weft.Persistence.Postgres;

/// <summary>Dedicated restricted catalog pool with organization-bound read-only transactions.</summary>
public sealed class PostgresCatalogDatabase : IAsyncDisposable
{
    private bool _disposed;

    public PostgresCatalogDatabase(string catalogConnectionString) =>
        DataSource = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(catalogConnectionString));

    internal NpgsqlDataSource DataSource { get; }

    internal async Task<PostgresTransaction> BeginTransactionAsync(Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (organizationId == Guid.Empty)
            throw new ArgumentException("An organization identifier cannot be empty.", nameof(organizationId));
        var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                await using (var mode = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                    await mode.ExecuteNonQueryAsync(cancellationToken);
                await using (var timeouts = new NpgsqlCommand(
                    "SELECT set_config('lock_timeout', $1, true), set_config('statement_timeout', $2, true)", connection, transaction))
                {
                    timeouts.Parameters.AddWithValue("1s");
                    timeouts.Parameters.AddWithValue("5s");
                    await timeouts.ExecuteNonQueryAsync(cancellationToken);
                }
                await CatalogRoleGuard.ValidateAsync(connection, transaction, cancellationToken);
                var scope = new PostgresTransaction(connection, transaction, organizationId);
                await scope.BindOrganizationAsync(organizationId, cancellationToken);
                return scope;
            }
            catch { await transaction.DisposeAsync(); throw; }
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await DataSource.DisposeAsync();
    }
}
