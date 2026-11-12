using System.Data;
using Npgsql;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public enum SpecDatabaseProfile { Reader, Writer, Maintenance }

/// <summary>Dedicated resetting pool. Every transaction validates role, schema and version before executing owner functions.</summary>
public sealed class PostgresSpecBundleDatabase : IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    internal SpecDatabaseProfile Profile { get; }
    public PostgresSpecBundleDatabase(string connectionString, SpecDatabaseProfile profile)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        Profile = profile;
        source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));
    }

    public PostgresSpecBundleSession ForActor(ActorContext actor, SpecStoreBinding store)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Profile == SpecDatabaseProfile.Maintenance) throw new InvalidOperationException("Maintenance has no actor session.");
        return new(this, actor, store);
    }

    public PostgresSpecBundleSession ForMaintenance(SpecStoreBinding store)
    {
        if (Profile != SpecDatabaseProfile.Maintenance) throw new InvalidOperationException("A maintenance profile is required.");
        return new(this, null, store);
    }

    internal async Task<T> RunAsync<T>(bool mutation, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation, CancellationToken token)
    {
        if (mutation && Profile == SpecDatabaseProfile.Reader) throw new SpecStorageException(SpecStorageError.NotAdmitted);
        try
        {
            await using var connection = await source.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'; SET LOCAL idle_in_transaction_session_timeout='60s'; SET LOCAL TimeZone='UTC'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await SpecSchemaAdmission.ValidateAsync(connection, transaction, Profile, token).ConfigureAwait(false);
            var result = await operation(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (SpecStorageException) { throw; }
        catch (PostgresException error)
        {
            throw new SpecStorageException(error.SqlState switch
            {
                "42501" => SpecStorageError.NotAdmitted,
                "23505" or "40001" or "40P01" => SpecStorageError.Conflict,
                "54000" => SpecStorageError.Limit,
                "23514" or "23502" or "23503" => SpecStorageError.Integrity,
                _ => SpecStorageError.Unavailable
            });
        }
        catch { throw new SpecStorageException(SpecStorageError.Unavailable); }
    }
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
