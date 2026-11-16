using System.Data;
using Npgsql;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>A lazy dedicated pool for short, exact-project orphan-maintenance transactions.</summary>
public sealed class PostgresPrivateObjectMaintenanceDatabase : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource;
    private readonly object _lifetime = new();
    private volatile bool _disposed;

    public PostgresPrivateObjectMaintenanceDatabase(string maintenanceConnectionString)
    {
        var normalized = PostgresConfiguration.Normalize(maintenanceConnectionString);
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

    internal async Task<PostgresPrivateObjectMaintenanceTransaction> BeginTransactionAsync(ProjectScope project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ObjectDisposedException.ThrowIf(_disposed, this);
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
                // Admission checks both context settings are empty, the exact dedicated login,
                // the restricted capabilities and all trusted current schema/routine contracts.
                await MaintenanceRoleGuard.ValidateAsync(connection, transaction, cancellationToken);
                var scoped = new PostgresPrivateObjectMaintenanceTransaction(connection, transaction, project);
                await scoped.BindProjectAsync(cancellationToken);
                return scoped;
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
            if (_disposed) return;
            _disposed = true;
            source = _dataSource.IsValueCreated ? _dataSource.Value : null;
        }
        if (source is not null) await source.DisposeAsync();
    }
}

/// <summary>Owns one admitted connection and a transaction bound to one organization and project.</summary>
internal sealed class PostgresPrivateObjectMaintenanceTransaction : IAsyncDisposable
{
    private const string ProjectSetting = "braid.project_id";
    private readonly PostgresTransaction _scope;
    private bool _projectBound;

    internal PostgresPrivateObjectMaintenanceTransaction(NpgsqlConnection connection,
        NpgsqlTransaction transaction, ProjectScope project)
    {
        Project = project;
        _scope = new(connection, transaction, project.OrganizationId);
    }

    internal ProjectScope Project { get; }
    internal NpgsqlConnection Connection => _scope.Connection;
    internal NpgsqlTransaction Transaction => _scope.Transaction;

    internal NpgsqlCommand CreateCommand(string sql)
    {
        if (!_projectBound) throw new InvalidOperationException("Maintenance project context has not been bound.");
        return _scope.CreateCommand(sql);
    }

    internal async Task BindProjectAsync(CancellationToken cancellationToken)
    {
        if (_projectBound) throw new InvalidOperationException("Maintenance project context is already bound.");
        await _scope.BindOrganizationAsync(Project.OrganizationId, cancellationToken);
        await using (var inspect = _scope.CreateCommand("SELECT NULLIF(current_setting($1, true), '')"))
        {
            inspect.Parameters.AddWithValue(ProjectSetting);
            if (await inspect.ExecuteScalarAsync(cancellationToken) is string)
                throw new InvalidOperationException("A leased connection contains a preset maintenance project context.");
        }
        await using var bind = _scope.CreateCommand("SELECT set_config($1, $2, true)");
        bind.Parameters.AddWithValue(ProjectSetting);
        bind.Parameters.AddWithValue(Project.ProjectId.ToString("D"));
        await bind.ExecuteNonQueryAsync(cancellationToken);
        _projectBound = true;
    }

    internal async Task RequireProjectAsync(ProjectScope project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!_projectBound || project != Project)
            throw new InvalidOperationException("The maintenance operation does not belong to this transaction's project.");
        await _scope.RequireOrganizationAsync(Project.OrganizationId, cancellationToken);
        await using var inspect = CreateCommand("SELECT current_setting($1, true)");
        inspect.Parameters.AddWithValue(ProjectSetting);
        if (await inspect.ExecuteScalarAsync(cancellationToken) is not string value ||
            !string.Equals(value, Project.ProjectId.ToString("D"), StringComparison.Ordinal))
            throw new InvalidOperationException("The maintenance transaction's project context changed.");
    }

    internal Task CommitAsync(CancellationToken cancellationToken = default) => _scope.CommitAsync(cancellationToken);
    internal Task RollbackAsync(CancellationToken cancellationToken = default) => _scope.RollbackAsync(cancellationToken);
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
