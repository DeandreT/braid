using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>Owns one leased connection and one organization-bound transaction.</summary>
public sealed class PostgresTransaction : IAsyncDisposable
{
    internal const string OrganizationSetting = "braid.organization_id";
    private bool _bound;
    private bool _completed;
    private bool _disposed;

    internal PostgresTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid organizationId)
    {
        Connection = connection;
        Transaction = transaction;
        OrganizationId = organizationId;
    }

    public Guid OrganizationId { get; }
    internal NpgsqlConnection Connection { get; }
    internal NpgsqlTransaction Transaction { get; }

    internal NpgsqlCommand CreateCommand(string sql)
    {
        EnsureActive();
        if (!_bound)
            throw new InvalidOperationException("Organization context has not been bound.");
        return new NpgsqlCommand(sql, Connection, Transaction);
    }

    internal async Task BindOrganizationAsync(Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (_bound || organizationId == Guid.Empty || organizationId != OrganizationId)
            throw new InvalidOperationException("Organization context may be bound exactly once to the transaction's organization.");

        await using (var inspect = new NpgsqlCommand(
            "SELECT NULLIF(current_setting($1, true), '')", Connection, Transaction))
        {
            inspect.Parameters.AddWithValue(OrganizationSetting);
            if (await inspect.ExecuteScalarAsync(cancellationToken) is string)
                throw new InvalidOperationException("A leased connection contains a preset organization context.");
        }

        await using var bind = new NpgsqlCommand("SELECT set_config($1, $2, true)", Connection, Transaction);
        bind.Parameters.AddWithValue(OrganizationSetting);
        bind.Parameters.AddWithValue(organizationId.ToString("D"));
        await bind.ExecuteNonQueryAsync(cancellationToken);
        _bound = true;
    }

    internal async Task RequireOrganizationAsync(Guid organizationId,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        if (!_bound || organizationId != OrganizationId)
            throw new InvalidOperationException("The operation does not belong to this transaction's organization.");
        await using var command = CreateCommand("SELECT current_setting($1, true)");
        command.Parameters.AddWithValue(OrganizationSetting);
        if (await command.ExecuteScalarAsync(cancellationToken) is not string current ||
            !string.Equals(current, OrganizationId.ToString("D"), StringComparison.Ordinal))
            throw new InvalidOperationException("The transaction's organization context changed.");
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await Transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await Transaction.RollbackAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            // Npgsql rolls back incomplete transactions before resetting pooled state.
            await Transaction.DisposeAsync();
        }
        finally
        {
            await Connection.DisposeAsync();
        }
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("The transaction has already completed.");
    }
}
