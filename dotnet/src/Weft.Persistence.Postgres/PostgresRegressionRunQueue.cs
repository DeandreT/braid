using System.Data;
using Npgsql;
using Weft.Application.Regression;

namespace Weft.Persistence.Postgres;

/// <summary>Unscoped machine discovery through one narrow function, never direct run or sample reads.</summary>
public sealed class PostgresRegressionRunQueue : IRegressionRunQueue, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    public PostgresRegressionRunQueue(string writerConnectionString) =>
        source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(writerConnectionString));

    public async Task<IReadOnlyList<RegressionRunReady>> ListReadyAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var setup = new NpgsqlCommand("""
            SET TRANSACTION READ ONLY;
            SET LOCAL search_path=pg_catalog;
            SET LOCAL TimeZone='UTC';
            SET LOCAL DateStyle='ISO, MDY';
            SET LOCAL statement_timeout='15s';
            SET LOCAL lock_timeout='5s';
            """, connection, transaction))
            await setup.ExecuteNonQueryAsync(cancellationToken);
        await RegressionRunRoleGuard.ValidateAsync(connection, transaction, writer: true, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT organization_id,run_id FROM weft.list_ready_regression_runs(@maximum)", connection, transaction);
        command.Parameters.AddWithValue("maximum", limit);
        var result = new List<RegressionRunReady>();
        var distinct = new HashSet<RegressionRunReady>();
        await using (var rows = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await rows.ReadAsync(cancellationToken))
            {
                var item = new RegressionRunReady(rows.GetGuid(0), rows.GetGuid(1));
                if (item.OrganizationId == Guid.Empty || item.RunId == Guid.Empty || result.Count >= limit || !distinct.Add(item))
                    throw new InvalidOperationException("Regression dispatch page is invalid.");
                result.Add(item);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return result.AsReadOnly();
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
