using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>Atomically installs and verifies the build's ordered embedded migrations using its own owner pool.</summary>
public sealed class MigrationRunner : IAsyncDisposable
{
    // A fixed database-local lock; acquisition precedes every schema and journal inspection.
    private const long MigrationLock = 6287392987644524871;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyList<MigrationScript> _migrations;
    private bool _disposed;

    internal static IReadOnlyList<MigrationScript> EmbeddedMigrations => LoadMigrations();

    public MigrationRunner(string migrationConnectionString)
        : this(migrationConnectionString, LoadMigrations())
    {
    }

    internal MigrationRunner(string migrationConnectionString, IReadOnlyList<MigrationScript> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Count == 0 || migrations.Where((migration, index) =>
                migration is null || migration.Version != index + 1).Any())
            throw new ArgumentException("Migration versions must be nonempty and consecutive from one.", nameof(migrations));
        _migrations = migrations.ToArray();
        _dataSource = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(migrationConnectionString));
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var acquire = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_xact_lock($1)", connection, transaction))
        {
            acquire.Parameters.AddWithValue(MigrationLock);
            await acquire.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var owner = new NpgsqlCommand("""
            SELECT current_user = 'braid_migrator' AND session_user = current_user
                AND NOT rolsuper AND NOT rolbypassrls
            FROM pg_catalog.pg_roles WHERE rolname = current_user
            """, connection, transaction))
        {
            if (await owner.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException("Migrations require the separately provisioned braid_migrator owner login.");
        }

        var installed = 0;
        await using (var inspect = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = 'braid'),
                   pg_catalog.to_regclass('braid.schema_migrations') IS NOT NULL
            """, connection, transaction))
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Could not inspect the owned migration schema.");
            if (reader.GetBoolean(0) && !reader.GetBoolean(1))
                throw new InvalidOperationException("An unversioned owned schema must not be adopted as a fresh baseline.");
            if (reader.GetBoolean(1))
                installed = -1;
        }
        if (installed == -1)
            installed = await ValidateHistoryAsync(connection, transaction, cancellationToken);

        foreach (var migration in _migrations.Skip(installed))
        {
            await using (var apply = new NpgsqlCommand(migration.Sql, connection, transaction))
                await apply.ExecuteNonQueryAsync(cancellationToken);
            await using var record = new NpgsqlCommand("""
                INSERT INTO braid.schema_migrations(version, checksum) VALUES ($1, $2)
                """, connection, transaction);
            record.Parameters.AddWithValue(migration.Version);
            record.Parameters.AddWithValue(migration.Checksum);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _dataSource.DisposeAsync();
    }

    private async Task<int> ValidateHistoryAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand(
            "SELECT version, checksum FROM braid.schema_migrations ORDER BY version", connection, transaction);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        var count = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (count >= _migrations.Count || reader.GetInt32(0) != _migrations[count].Version ||
                !string.Equals(reader.GetString(1), _migrations[count].Checksum, StringComparison.Ordinal))
                throw new InvalidOperationException("Applied migration versions and exact checksums do not match this build.");
            count++;
        }
        if (count == 0)
            throw new InvalidOperationException("An empty migration journal cannot establish the owned schema's history.");
        return count;
    }

    private static IReadOnlyList<MigrationScript> LoadMigrations()
    {
        const string prefix = "Braid.Persistence.Postgres.Migrations.";
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) &&
                           Regex.IsMatch(name[prefix.Length..], "^[0-9]{4}_[a-z0-9_]+\\.sql$", RegexOptions.CultureInvariant))
            .Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0)
            throw new InvalidOperationException("The required embedded PostgreSQL migrations are missing.");
        var migrations = new List<MigrationScript>(names.Length);
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name) ??
                throw new InvalidOperationException("An embedded migration could not be read.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var sql = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            var version = int.Parse(name.AsSpan(prefix.Length, 4), CultureInfo.InvariantCulture);
            if (version != migrations.Count + 1)
                throw new InvalidOperationException("Embedded migration versions must be consecutive from one.");
            migrations.Add(new MigrationScript(version, sql));
        }
        return migrations;
    }
}
