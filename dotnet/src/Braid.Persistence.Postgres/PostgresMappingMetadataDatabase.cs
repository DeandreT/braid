using System.Data;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>Dedicated restricted pool for short organization-bound mapping metadata edits.</summary>
public sealed class PostgresMappingMetadataDatabase : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource;
    private readonly object _lifetime = new();
    private volatile bool _disposed;

    public PostgresMappingMetadataDatabase(string metadataConnectionString)
    {
        var normalized = PostgresConfiguration.Normalize(metadataConnectionString);
        _dataSource = new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(normalized));
    }

    internal NpgsqlDataSource DataSource
    {
        get
        {
            lock (_lifetime)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _dataSource.Value;
            }
        }
    }

    internal async Task<PostgresTransaction> BeginTransactionAsync(Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (organizationId == Guid.Empty)
            throw new ArgumentException("An organization identifier cannot be empty.", nameof(organizationId));
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                await using (var mode = new NpgsqlCommand("SET TRANSACTION READ WRITE", connection, transaction))
                    await mode.ExecuteNonQueryAsync(cancellationToken);
                await using (var timeouts = new NpgsqlCommand(
                    "SELECT set_config('lock_timeout', $1, true), set_config('statement_timeout', $2, true)", connection, transaction))
                {
                    timeouts.Parameters.AddWithValue("1s");
                    timeouts.Parameters.AddWithValue("5s");
                    await timeouts.ExecuteNonQueryAsync(cancellationToken);
                }
                await MappingMetadataRoleGuard.ValidateAsync(connection, transaction, cancellationToken);
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
        NpgsqlDataSource? source;
        lock (_lifetime)
        {
            if (_disposed)
                return;
            _disposed = true;
            source = _dataSource.IsValueCreated ? _dataSource.Value : null;
        }
        if (source is not null)
            await source.DisposeAsync();
    }
}
