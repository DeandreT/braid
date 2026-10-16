using System.Data;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>
/// Explicit migration-owner operations only. Never registered for web/sign-in use;
/// no organization, project role, capability or identity reactivation is inferred.
/// </summary>
public sealed partial class ExternalIdentityBindingProvisioner :
    IExternalIdentityBindingOwner, IExternalIdentityOwnerPreparation, IAsyncDisposable
{
    private readonly NpgsqlDataSource _owner;

    public ExternalIdentityBindingProvisioner(string migrationConnectionString)
        => _owner = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(migrationConnectionString));

    private async Task<T> ExecuteAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await _owner.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var mode = new NpgsqlCommand("SET TRANSACTION READ WRITE", connection, transaction))
                await mode.ExecuteNonQueryAsync(cancellationToken);
            await using (var limits = new NpgsqlCommand(
                "SET LOCAL lock_timeout='1s'; SET LOCAL statement_timeout='5s'", connection, transaction))
                await limits.ExecuteNonQueryAsync(cancellationToken);
            await ExternalIdentityRoleGuard.ValidateOwnerAsync(connection, transaction, cancellationToken);
            var result = await operation(connection, transaction, cancellationToken);
            // No result, including a replay/no-op, escapes an unacknowledged commit.
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("External identity owner operation failed."); }
    }

    private static async Task<ExternalIdentityBinding?> FindBindingAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, ExternalIdentityKey key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {PostgresExternalIdentityFacts.BindingProjection}
            FROM ONLY weft.external_identity_bindings b
            WHERE b.provider=@provider AND b.authority=@authority AND b.subject=@subject
            FOR UPDATE OF b
            """, connection, transaction);
        PostgresExternalIdentityFacts.AddKey(command, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var binding = PostgresExternalIdentityFacts.ReadBinding(reader, key);
        if (await reader.ReadAsync(cancellationToken)) throw PostgresExternalIdentityFacts.InvalidFacts();
        return binding;
    }

    private static async Task<bool?> PrincipalActiveAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PrincipalId principalId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT p.id,p.active FROM ONLY weft.principals p WHERE p.id=@principalId FOR SHARE OF p", connection, transaction);
        PostgresExternalIdentityFacts.Add(command, "principalId", NpgsqlDbType.Uuid, principalId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var active = PostgresExternalIdentityFacts.Read(() =>
        {
            if (reader.GetGuid(0) != principalId.Value) throw PostgresExternalIdentityFacts.InvalidFacts();
            return reader.GetBoolean(1);
        });
        if (await reader.ReadAsync(cancellationToken)) throw PostgresExternalIdentityFacts.InvalidFacts();
        return active;
    }

    private static void RequireChangedCount(int count)
    {
        if (count is < 0 or > 1) throw PostgresExternalIdentityFacts.InvalidFacts();
    }

    public ValueTask DisposeAsync() => _owner.DisposeAsync();
}
