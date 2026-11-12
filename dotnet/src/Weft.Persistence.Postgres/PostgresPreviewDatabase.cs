using System.Data;
using Npgsql;

namespace Weft.Persistence.Postgres;

public enum PreviewDatabaseProfile { Reader, Writer }

/// <summary>
/// Owns a separate, resetting, lazily opened preview credential pool. Each operation is one
/// short read-committed transaction that first proves the exact restricted session identity.
/// </summary>
public sealed class PostgresPreviewDatabase : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _source;
    private readonly object _lifetime = new();
    private bool _disposed;

    public PostgresPreviewDatabase(string connectionString, PreviewDatabaseProfile profile)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        Profile = profile;
        var normalized = PostgresConfiguration.Normalize(connectionString);
        _source = new(() => NpgsqlDataSource.Create(normalized));
    }

    internal PreviewDatabaseProfile Profile { get; }

    private NpgsqlDataSource Source
    {
        get { lock (_lifetime) { ObjectDisposedException.ThrowIf(_disposed, this); return _source.Value; } }
    }

    internal async Task<T> RunAsync<T>(bool write, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation, CancellationToken ct)
    {
        if (write && Profile != PreviewDatabaseProfile.Writer)
            throw new InvalidOperationException("Preview mutation requires its separate writer profile.");
        await using var connection = await Source.OpenConnectionAsync(ct);
        // A read sees one snapshot, so a run and its outputs cannot straddle a concurrent completion.
        var isolation = write ? IsolationLevel.ReadCommitted : IsolationLevel.RepeatableRead;
        await using var transaction = await connection.BeginTransactionAsync(isolation, ct);
        // Fix resolution before any function-based inspection; a startup search path is not trusted.
        await using (var path = new NpgsqlCommand("SET LOCAL search_path=pg_catalog", connection, transaction))
            await path.ExecuteNonQueryAsync(ct);
        if (!write)
        {
            await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            await readOnly.ExecuteNonQueryAsync(ct);
        }
        await using (var inspect = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user=@profile AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND current_setting('transaction_isolation')=@isolation
                AND current_setting('session_replication_role')='origin'
                AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                AND current_setting('standard_conforming_strings')='on'
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            inspect.Parameters.AddWithValue("profile", Profile == PreviewDatabaseProfile.Reader ? "weft_preview_reader" : "weft_preview_writer");
            inspect.Parameters.AddWithValue("isolation", write ? "read committed" : "repeatable read");
            if (await inspect.ExecuteScalarAsync(ct) is not true)
                throw new InvalidOperationException("Preview database profile is invalid.");
        }
        await XmlPreviewSchemaAdmission.ValidateAsync(connection, transaction, ct);
        await using (var settings = new NpgsqlCommand("""
            SELECT set_config('statement_timeout','15000',true),set_config('lock_timeout','5000',true),
                set_config('search_path','pg_catalog,weft,pg_temp',true),set_config('TimeZone','UTC',true)
            """, connection, transaction))
            await settings.ExecuteNonQueryAsync(ct);
        var result = await operation(connection, transaction);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlDataSource? source;
        lock (_lifetime) { if (_disposed) return; _disposed = true; source = _source.IsValueCreated ? _source.Value : null; }
        if (source is not null) await source.DisposeAsync();
    }
}
