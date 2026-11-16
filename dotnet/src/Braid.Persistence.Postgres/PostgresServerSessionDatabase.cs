using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>One separately credentialed session metadata profile.</summary>
public enum ServerSessionDatabaseProfile
{
    Reader,
    Writer
}

/// <summary>Owns one lazy normalized resetting pool; use independent reader and writer instances.</summary>
public sealed class PostgresServerSessionDatabase : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource;
    private readonly object _lifetime = new();
    private volatile bool _disposed;

    public PostgresServerSessionDatabase(string connectionString, ServerSessionDatabaseProfile profile)
    {
        if (profile is not ServerSessionDatabaseProfile.Reader and not ServerSessionDatabaseProfile.Writer)
            throw new ArgumentOutOfRangeException(nameof(profile));
        Profile = profile;
        var normalized = PostgresConfiguration.Normalize(connectionString);
        _dataSource = new(() => NpgsqlDataSource.Create(normalized));
    }

    internal ServerSessionDatabaseProfile Profile { get; }

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
