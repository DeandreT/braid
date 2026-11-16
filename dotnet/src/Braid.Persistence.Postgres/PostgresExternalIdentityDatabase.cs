using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>Dedicated lazy, resetting pool for provider-neutral identity reads only.</summary>
public sealed class PostgresExternalIdentityDatabase : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource;
    private readonly object _lifetime = new();
    private volatile bool _disposed;

    public PostgresExternalIdentityDatabase(string identityReaderConnectionString)
    {
        var normalized = PostgresConfiguration.Normalize(identityReaderConnectionString);
        _dataSource = new(() => NpgsqlDataSource.Create(normalized));
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

    public async ValueTask DisposeAsync()
    {
        NpgsqlDataSource? source;
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            source = _dataSource.IsValueCreated ? _dataSource.Value : null;
        }
        if (source is not null) await source.DisposeAsync();
    }
}
