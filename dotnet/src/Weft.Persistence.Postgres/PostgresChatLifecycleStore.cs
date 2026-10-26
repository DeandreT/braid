using System.Data;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Owns the separately credentialed lifecycle pool; never substitutes an owner/writer connection.</summary>
public sealed class PostgresChatLifecycleDatabase : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _source;
    private readonly object _lifetime = new();
    private bool _disposed;
    public PostgresChatLifecycleDatabase(string connectionString)
    {
        var normalized = PostgresConfiguration.Normalize(connectionString);
        _source = new(() => NpgsqlDataSource.Create(normalized));
    }
    private NpgsqlDataSource Source
    { get { lock (_lifetime) { ObjectDisposedException.ThrowIf(_disposed, this); return _source.Value; } } }
    internal NpgsqlDataSource DataSource => Source;
    internal async Task<PostgresTransaction> BeginAsync(ChatConversationKey key, CancellationToken ct)
    {
        var connection = await Source.OpenConnectionAsync(ct); NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using (var settings = new NpgsqlCommand("SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'; SET LOCAL search_path=pg_catalog,weft,pg_temp; SET LOCAL TimeZone='UTC'", connection, transaction))
                await settings.ExecuteNonQueryAsync(ct);
            var result = new PostgresTransaction(connection, transaction, key.OrganizationId);
            await result.BindOrganizationAsync(key.OrganizationId, ct);
            await using (var scope = result.CreateCommand("SELECT pg_catalog.set_config('weft.chat_principal_id',$1,true)"))
            {
                scope.Parameters.AddWithValue(key.Creator.Value.ToString("D")); await scope.ExecuteNonQueryAsync(ct);
            }
            await ChatRoleGuard.ValidateAsync(connection, transaction, ChatDatabaseProfile.Lifecycle, false, ct);
            return result;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        NpgsqlDataSource? source;
        lock (_lifetime) { if (_disposed) return; _disposed = true; source = _source.IsValueCreated ? _source.Value : null; }
        if (source is not null) await source.DisposeAsync();
    }
}

/// <summary>Exact-target metadata lifecycle, no history/content/listing/dispatch capability.</summary>
public sealed class PostgresChatLifecycleStore(PostgresChatLifecycleDatabase database) : IChatLifecycleStore
{
    private readonly PostgresChatLifecycleDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    public Task<ChatLifecycleObservation?> ObserveAsync(ChatConversationKey key, Guid turnId, CancellationToken ct = default)
    {
        ChatBounds.Id(turnId);
        return ExecuteAsync<ChatLifecycleObservation?>(key, async tx =>
        {
            await using var command = Command(tx, "SELECT phase,version FROM weft.chat_lifecycle_observe(@org,@creator,@conversation,@project,@mapping,@revision,@turn)", key);
            command.Parameters.AddWithValue("turn", turnId); await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var phase = (ChatTurnPhase)reader.GetInt32(0); var version = reader.GetInt64(1);
            if (!Enum.IsDefined(phase) || version < 1 || await reader.ReadAsync(ct)) throw new InvalidOperationException("Chat lifecycle facts are invalid.");
            return new(phase, version);
        }, ct);
    }
    public Task<ChatStoreOutcome> CloseAsync(ChatConversationKey key, Guid turnId, long expectedVersion, ChatTurnPhase phase, CancellationToken ct = default)
        => CloseWithMetadataAsync(key, turnId, expectedVersion, phase, null, ct);
    public Task<ChatStoreOutcome> CloseWithMetadataAsync(ChatConversationKey key, Guid turnId, long expectedVersion, ChatTurnPhase phase, ChatTerminalMetadata? metadata, CancellationToken ct = default)
    {
        ChatBounds.Id(turnId); if (expectedVersion < 1 || phase is not (ChatTurnPhase.Cancelled or ChatTurnPhase.Interrupted)) throw ChatBounds.Invalid();
        return ExecuteAsync(key, async tx =>
        {
            await using var command = Command(tx, "SELECT weft.chat_lifecycle_close(@org,@creator,@conversation,@project,@mapping,@revision,@turn,@version,@phase,@event,@metadata)", key);
            command.Parameters.AddWithValue("turn", turnId); command.Parameters.AddWithValue("version", expectedVersion);
            command.Parameters.AddWithValue("phase", (int)phase); command.Parameters.AddWithValue("event", Guid.NewGuid());
            command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Text) { Value = metadata is null ? DBNull.Value : ChatMetadataCodec.Terminal(metadata) });
            return Outcome(await command.ExecuteScalarAsync(ct));
        }, ct);
    }
    public Task<ChatStoreOutcome> EraseExpiredAsync(ChatConversationKey key, ChatErasurePolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return ExecuteAsync(key, async tx =>
        {
            await using var command = Command(tx, "SELECT weft.chat_lifecycle_erase(@org,@creator,@conversation,@project,@mapping,@revision,@policy,@policyVersion,@retirement)", key);
            command.Parameters.AddWithValue("policy", policy.PolicyId); command.Parameters.AddWithValue("policyVersion", policy.Version);
            command.Parameters.AddWithValue("retirement", NpgsqlDbType.TimestampTz, policy.IdentityRetirementNotBeforeUtc);
            return Outcome(await command.ExecuteScalarAsync(ct));
        }, ct);
    }
    private async Task<T> ExecuteAsync<T>(ChatConversationKey key, Func<PostgresTransaction, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key); ct.ThrowIfCancellationRequested();
        try
        {
            await using var tx = await _database.BeginAsync(key, ct);
            var result = await operation(tx); await tx.CommitAsync(ct); return result;
        }
        catch (NpgsqlException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (NpgsqlException) { throw new InvalidOperationException("Private chat lifecycle persistence failed."); }
    }
    private static NpgsqlCommand Command(PostgresTransaction tx, string sql, ChatConversationKey key)
    {
        var command = tx.CreateCommand(sql); command.Parameters.AddWithValue("org", key.OrganizationId);
        command.Parameters.AddWithValue("creator", key.Creator.Value); command.Parameters.AddWithValue("conversation", key.ConversationId);
        // A workspace conversation's exact target has no revision scope; the routines match it by null.
        foreach (var (name, value) in new[] { ("project", key.Revision?.ProjectId), ("mapping", key.Revision?.MappingId), ("revision", key.Revision?.RevisionId) })
            command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value is { } id ? id : DBNull.Value });
        return command;
    }
    private static ChatStoreOutcome Outcome(object? value) => value is string text && Enum.TryParse<ChatStoreOutcome>(text, out var result) && Enum.IsDefined(result)
        ? result : throw new InvalidOperationException("Chat lifecycle outcome is invalid.");
}
